using System.Collections.Immutable;

namespace Builder.Core;

public enum BooleanKind { Union, Subtract, Intersect }
public enum SplitKeep { Both, Positive, Negative }
public abstract record GeometryRequest
{
    /// <summary>Exact member-editing group scope; null means scene scope. Replay after scope change returns Conflict.</summary>
    public Guid? MemberGroupId { get; init; }
}
/// <summary>Consumes both operands on acceptance. Subtract means A minus B.</summary>
public sealed record BooleanRequest(Guid A, Guid B, BooleanKind Kind) : GeometryRequest;
/// <summary>World-space plane: dot(normalized Normal, point) = OffsetMillimeters.</summary>
public sealed record SplitRequest(Guid ObjectId, Vec3 Normal, double OffsetMillimeters,
    SplitKeep Keep = SplitKeep.Both) : GeometryRequest;
public sealed record GeometryPreview(Guid Id, long BaseRevision, GeometryRequest Request, Scene Scene,
    Guid? SelectedObjectId, ImmutableArray<Guid> ResultObjectIds, string Summary, RepairComparison? Repair = null)
{
    /// <summary>Complete selection to restore on Apply; primary SelectedObjectId remains a selected leaf.</summary>
    public ImmutableArray<Guid> SelectedObjectIds { get; init; } = SelectedObjectId is {} id ? [id] : [];
    /// <summary>Member-editing scope to retain on Apply.</summary>
    public Guid? EditingGroupId { get; init; }
    public HollowComparison? Hollow { get; init; }
    public SimplifyComparison? Simplify { get; init; }
    public SmoothComparison? Smooth { get; init; }
    public SeparateComparison? Separate { get; init; }
    public ExtrudeDownComparison? ExtrudeDown { get; init; }
    public SurfaceMaterialComparison? SurfaceMaterial { get; init; }
    public SurfaceBrushComparison? SurfaceBrush { get; init; }
}

public sealed partial class EditorSession
{
    private GeometryPreview? preview;

    /// <summary>Computes a private candidate. The accepted document and its revision never change here.</summary>
    public Task<OperationResult> PreviewAsync(GeometryRequest request, CancellationToken cancellation = default,
        IProgress<OperationProgress>? progress = null, long? expectedRevision = null)
        => request is SurfaceBrushRequest brush
            ? PreviewSurfaceBrushAsync(brush,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is SurfaceMaterialRequest surface
            ? PreviewSurfaceMaterialAsync(surface,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is MaterialRequest material
            ? PreviewMaterialAsync(material,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is TransformRequest transform
            ? PreviewTransformAsync(transform,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is ExtrudeDownRequest extrudeDown
            ? PreviewExtrudeDownAsync(extrudeDown,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is SeparateRequest separate
            ? PreviewSeparateAsync(separate,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is SmoothRequest smooth
            ? PreviewSmoothAsync(smooth,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is SimplifyRequest simplify
            ? PreviewSimplifyAsync(simplify,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is HollowRequest hollow
            ? PreviewHollowAsync(hollow,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is ImageEdgeRequest edge
            ? PreviewImageEdgeAsync(edge,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is ImageStampRequest stamp
            ? PreviewImageStampAsync(stamp,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is ImageContourRequest contour
            ? PreviewImageContourAsync(contour,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is ImageReliefRequest relief
            ? PreviewImageReliefAsync(relief,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is TextRequest text
            ? PreviewTextAsync(text,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is EmbossRequest emboss
            ? PreviewEmbossAsync(emboss,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is PrimitiveRequest primitive
            ? PreviewPrimitiveAsync(primitive,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : request is ArrangementRequest arrangement
            ? PreviewArrangementAsync(arrangement,cancellation:cancellation,progress:progress,expectedRevision:expectedRevision)
            : RunIo(() => {
            cancellation.ThrowIfCancellationRequested();
            var captured = Snapshot;
            if(request is RepairRequest repair) return PrepareRepair(repair,captured,cancellation,progress);
            var ids = request switch {
                BooleanRequest b when Enum.IsDefined(b.Kind) && b.A != b.B => new[] { b.A,b.B },
                SplitRequest s when Enum.IsDefined(s.Keep) => new[] { s.ObjectId },
                _ => throw new ModelException("Choose two different objects for a boolean operation.") };
            var inputs = ids.Select(id => captured.Scene.Objects.FirstOrDefault(o => o.Id == id)
                ?? throw new ModelException("One of the selected objects no longer exists.")).ToArray();
            foreach(var input in inputs)SurfacePainting.RequireUnpainted(input,"Split and Boolean operations");
            _=captured.Scene.ExpandObjectIds(ids.ToImmutableArray(),captured.EditingGroupId);
            if(captured.EditingGroupId is null && ids.Any(captured.Scene.IsGrouped))
                throw new ModelException("Ungroup these objects before split or boolean operations. Group transforms remain available.");
            if(request is SplitRequest split && (!split.Normal.IsFinite ||
                Math.Max(Math.Abs(split.Normal.X),Math.Max(Math.Abs(split.Normal.Y),Math.Abs(split.Normal.Z))) == 0 ||
                !double.IsFinite(split.OffsetMillimeters) || Math.Abs(split.OffsetMillimeters) > 10_000_000))
                throw new ModelException("Enter a finite, nonzero plane normal and an offset within 10,000,000 mm.");
            var meshes = geometry.Calculate(request,inputs,cancellation,progress);
            var results = ImmutableArray.CreateBuilder<SceneObject>();
            for(var i=0;i<meshes.Length;i++)
            {
                if(meshes[i] is not {} mesh) continue; // Empty results are valid and explicitly previewed.
                if(request is SplitRequest s && ((s.Keep == SplitKeep.Positive && i != 0) ||
                    (s.Keep == SplitKeep.Negative && i != 1))) continue;
                var name = request is BooleanRequest b ? $"{b.Kind} result" : inputs[0].Name + (i == 0 ? " · positive side" : " · negative side");
                // Split inherits its source; a Boolean result inherits operand A's display material.
                results.Add(new(Guid.NewGuid(),name,mesh,Affine.Identity) {Material=inputs[0].Material});
            }
            // Insert results where A stood, keeping every unrelated object and its identity intact.
            var objects = ImmutableArray.CreateBuilder<SceneObject>();
            foreach(var obj in captured.Scene.Objects)
            {
                if(obj.Id == ids[0]) objects.AddRange(results);
                if(!ids.Contains(obj.Id)) objects.Add(obj);
            }
            var candidate = captured.Scene.WithObjects(objects.ToImmutable());
            if(captured.EditingGroupId is {} scope) candidate=candidate.ReplaceMemberIds(scope,ids,results.Select(o=>o.Id));
            candidate.Validate(cancellation);
            var summary = results.Count == 0
                ? "No solid remains. Apply removes the operands; Undo restores them."
                : $"Preview: {results.Count} result object{(results.Count == 1 ? "" : "s")}. Apply replaces the operands; Undo restores them.";
            progress?.Report(new("Ready to preview")); cancellation.ThrowIfCancellationRequested();
            lock(gate)
            {
                var resultIds=results.Select(o=>o.Id).ToImmutableArray();
                var nextIds=candidate.ExpandObjectIds(captured.SelectedObjectIds.Where(id=>!ids.Contains(id)).Concat(resultIds).ToImmutableArray(),captured.EditingGroupId);
                Guid? primary=results.FirstOrDefault()?.Id ??
                    (captured.SelectedObjectId is {} old && nextIds.Contains(old) ? old : nextIds.Length > 0 ? nextIds[0] : captured.EditingGroupId is null ? objects.FirstOrDefault()?.Id : null);
                if(nextIds.Length==0 && primary is {} first) nextIds=candidate.ExpandObjectIds([first],captured.EditingGroupId);
                preview = new(Guid.NewGuid(),captured.Revision,request,candidate,primary,
                    resultIds,summary) {SelectedObjectIds=nextIds,EditingGroupId=captured.EditingGroupId};
            }
            return summary;
        },expectedRevision,preflight:()=>GuardMemberScope(request?.MemberGroupId));

    public OperationResult ApplyPreview(Guid previewId, long? expectedRevision = null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked) return blocked;
            if(preview is not {} p || p.Id != previewId || p.BaseRevision != revision || p.EditingGroupId!=editingGroupId)
                return new(Outcome.Conflict,"This preview is no longer current. Create a new preview.");
            if(p.Request is TransformRequest or ArrangementRequest or MaterialRequest or SurfaceMaterialRequest or SurfaceBrushRequest && p.Scene == scene)
            {
                preview = null;
                return new(Outcome.Success,"No change. Preview cleared.");
            }
            undo.Add((scene,selected,selectedIds,editingGroupId)); if(undo.Count > 100) undo.RemoveAt(0);
            redo.Clear(); scene = p.Scene; selected = p.SelectedObjectId; selectedIds=p.SelectedObjectIds;
            editingGroupId=p.EditingGroupId; preview = null; revision++;
            return new(Outcome.Success,"Applied. You can undo this operation.");
        }
    }
    public OperationResult DiscardPreview(Guid previewId)
    {
        lock(gate)
        {
            if(Guard(null,true) is {} blocked) return blocked;
            if(preview?.Id != previewId) return new(Outcome.Conflict,"This preview is no longer current.");
            preview = null;
            return new(Outcome.Cancelled,"Preview cancelled. Your scene is unchanged.");
        }
    }
}
