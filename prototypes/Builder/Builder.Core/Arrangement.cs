using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Accepted leaf targets; each target expands to its complete flat group.</summary>
public abstract record ArrangementRequest(ImmutableArray<Guid> ObjectIds) : GeometryRequest;
/// <summary>Flattens selected units into one named group without changing geometry or leaf IDs.</summary>
public sealed record GroupRequest(ImmutableArray<Guid> ObjectIds, string Name = "Group") : ArrangementRequest(ObjectIds);
/// <summary>Removes groups touched by the targets. Ungrouped leaves are unchanged.</summary>
public sealed record UngroupRequest(ImmutableArray<Guid> ObjectIds) : ArrangementRequest(ObjectIds);
public enum AxisAlignment { Keep, Minimum, Center, Maximum }
/// <summary>
/// Translates at least two selection units along chosen world axes in millimeters, preserving each group's
/// internal offsets. With no reference, anchors are the combined accepted selection bounds. An optional
/// reference leaf must belong to the selection and resolves to its whole group's bounds.
/// </summary>
public sealed record AlignmentRequest(ImmutableArray<Guid> ObjectIds, AxisAlignment X, AxisAlignment Y,
    AxisAlignment Z, Guid? ReferenceObjectId = null) : ArrangementRequest(ObjectIds);

public sealed partial class EditorSession
{
    /// <summary>
    /// Previews grouping, ungrouping, alignment, distribution, centering or arrays from accepted content. Replacement requires the exact
    /// current arrangement-preview token and identical canonical expanded leaf targets. Every published
    /// update gets a fresh token. Apply commits one undo step and selects the targets (new copies for arrays); cancellation/failure
    /// preserves accepted content, full selection, history and any prior candidate.
    /// </summary>
    public Task<OperationResult> PreviewArrangementAsync(ArrangementRequest request, Guid? replacePreviewId = null,
        CancellationToken cancellation = default, IProgress<OperationProgress>? progress = null, long? expectedRevision = null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked) return Task.FromResult(blocked);
            if(request is null) return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose an arrangement operation."));
            if(GuardMemberScope(request.MemberGroupId) is {} scopeConflict) return Task.FromResult(scopeConflict);
            if(editingGroupId.HasValue && request is GroupRequest or UngroupRequest) return Task.FromResult(DoneWithGroup());
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId || current.Request is not ArrangementRequest)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This arrangement preview is no longer current. Read it again before updating."));
            }
            else if(replacePreviewId.HasValue)
                return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no arrangement preview to replace."));
            try
            {
                var canonical=scene.ExpandObjectIds(request.ObjectIds,editingGroupId);
                var targets=request.ObjectIds.SequenceEqual(canonical) ? request.ObjectIds : canonical;
                if(targets.Length==0) throw new ModelException("Choose objects to arrange.");
                if(preview?.Request is ArrangementRequest previous && !previous.ObjectIds.SequenceEqual(targets))
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"An arrangement update must keep the same target objects."));
                request=request with {ObjectIds=targets};
            }
            catch(ModelException e) { return Task.FromResult(new OperationResult(Outcome.Invalid,e.Message)); }
            var captured=Snapshot;
            return RunIo(()=>PrepareArrangement(request,captured,cancellation,progress),expectedRevision,allowPreview:true);
        }
    }

    private string PrepareArrangement(ArrangementRequest request,EditorSnapshot captured,CancellationToken cancellation,
        IProgress<OperationProgress>? progress)
    {
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new("Preparing arrangement")); cancellation.ThrowIfCancellationRequested();
        var targets=request.ObjectIds; var targetSet=targets.ToHashSet();
        Scene candidate; string summary;
        var resultIds=targets;Guid? resultPrimary=captured.SelectedObjectId is {} old && targetSet.Contains(old)?old:targets[0];
        switch(request)
        {
            case GroupRequest group:
                var name=Scene.ValidateGroupName(group.Name);
                if(targets.Length < 2) throw new ModelException("Choose at least two objects to group.");
                progress?.Report(new("Grouping objects",0,targets.Length)); cancellation.ThrowIfCancellationRequested();
                var existing=captured.Scene.Groups.FirstOrDefault(g=>g.ObjectIds.Length==targets.Length && g.ObjectIds.All(targetSet.Contains));
                if(existing is not null)
                {
                    candidate=existing.Name==name ? captured.Scene : captured.Scene with {
                        ContentId=Guid.NewGuid(),Groups=captured.Scene.Groups.Replace(existing,existing with {Name=name})};
                }
                else
                {
                    var groups=captured.Scene.Groups.Where(g=>!g.ObjectIds.Any(targetSet.Contains)).ToImmutableArray();
                    candidate=captured.Scene with {ContentId=Guid.NewGuid(),Groups=groups.Add(new(Guid.NewGuid(),name,targets))};
                }
                request=group with {Name=name};
                summary=$"Group preview: {targets.Length} objects as “{name}”. Geometry stays in world millimeters.";
                break;
            case UngroupRequest:
                progress?.Report(new("Ungrouping objects",0,targets.Length)); cancellation.ThrowIfCancellationRequested();
                var remaining=captured.Scene.Groups.Where(g=>!g.ObjectIds.Any(targetSet.Contains)).ToImmutableArray();
                var removed=captured.Scene.Groups.Length-remaining.Length;
                candidate=removed==0 ? captured.Scene : captured.Scene with {ContentId=Guid.NewGuid(),Groups=remaining};
                summary=$"Ungroup preview: {removed} groups become independent objects. Geometry stays in world millimeters.";
                break;
            case CenterOnPlateRequest center:
                (candidate,summary)=PrepareCenterOnPlate(center,captured,cancellation,progress);
                if(ReferenceEquals(candidate,captured.Scene))
                {resultIds=captured.SelectedObjectIds;resultPrimary=captured.SelectedObjectId;}
                break;
            case RadialArrayRequest radial:
                (candidate,summary,resultIds,resultPrimary)=PrepareRadialArray(radial,captured,cancellation,progress);
                break;
            case RectangularArrayRequest array:
                (candidate,summary,resultIds,resultPrimary)=PrepareRectangularArray(array,captured,cancellation,progress);
                break;
            case DistributionRequest distribution:
                (candidate,summary)=PrepareDistribution(distribution,captured,cancellation,progress);
                break;
            case AlignmentRequest alignment:
                if(!Enum.IsDefined(alignment.X) || !Enum.IsDefined(alignment.Y) || !Enum.IsDefined(alignment.Z))
                    throw new ModelException("Choose a supported alignment for each axis.");
                if(alignment.ReferenceObjectId is {} referenceId && !targetSet.Contains(referenceId))
                    throw new ModelException("The reference object must belong to the selected objects.");
                var units=captured.Scene.GetSelectionUnits(targets,cancellation,progress,captured.EditingGroupId);
                if(units.Length < 2) throw new ModelException("Choose at least two independent objects or groups to align.");
                var reference=alignment.ReferenceObjectId is {} referenceLeaf
                    ? units.Single(u=>u.ObjectIds.Contains(referenceLeaf)).Bounds
                    : Bounds3.From(units.SelectMany(u=>new[] {u.Bounds.Min,u.Bounds.Max}));
                var originals=captured.Scene.Objects.ToDictionary(o=>o.Id); var replacements=new Dictionary<Guid,SceneObject>();
                var total=targets.Sum(id=>originals[id].Mesh.Vertices.Length); var completed=0;
                foreach(var unit in units)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var delta=new Vec3(Anchor(reference,alignment.X,0)-Anchor(unit.Bounds,alignment.X,0),
                        Anchor(reference,alignment.Y,1)-Anchor(unit.Bounds,alignment.Y,1),
                        Anchor(reference,alignment.Z,2)-Anchor(unit.Bounds,alignment.Z,2));
                    foreach(var id in unit.ObjectIds)
                    {
                        var original=originals[id]; var transformed=original with {Transform=original.Transform.Then(Affine.Translation(delta))};
                        _=GeometryCache.Bounds(transformed,cancellation,progress,"Aligning objects",completed,total);
                        replacements.Add(id,transformed); completed+=transformed.Mesh.Vertices.Length;
                    }
                }
                candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>replacements.GetValueOrDefault(o.Id,o)).ToImmutableArray());
                summary=$"Align preview: {units.Length} units, world X {alignment.X}, Y {alignment.Y}, Z {alignment.Z} in millimeters; "+
                    (alignment.ReferenceObjectId.HasValue ? "selected reference unit." : "combined accepted selection bounds.");
                break;
            default: throw new ModelException("Choose a supported arrangement operation.");
        }
        progress?.Report(new("Ready to preview")); cancellation.ThrowIfCancellationRequested();
        lock(gate)
        {
            cancellation.ThrowIfCancellationRequested();
            preview=new(Guid.NewGuid(),captured.Revision,request,candidate,resultPrimary,resultIds,summary)
                {SelectedObjectIds=resultIds,EditingGroupId=captured.EditingGroupId};
        }
        return summary;
    }

    private static double Anchor(Bounds3 bounds,AxisAlignment alignment,int axis)
    {
        var point=alignment switch {AxisAlignment.Minimum=>bounds.Min,AxisAlignment.Center=>bounds.Center,
            AxisAlignment.Maximum=>bounds.Max,_=>new Vec3()};
        return axis switch {0=>point.X,1=>point.Y,_=>point.Z};
    }
}
