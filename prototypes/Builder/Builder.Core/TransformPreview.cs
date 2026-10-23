using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>A transform draft always derives from the accepted object, never the preceding candidate.</summary>
public abstract record TransformRequest(Guid ObjectId) : GeometryRequest
{
    /// <summary>Explicit leaf targets, expanded to whole groups. Empty uses the compatible positional ObjectId.</summary>
    public ImmutableArray<Guid> ObjectIds { get; init; } = [];
}
/// <summary>World-axis displacement in millimeters. Zero snap disables quantization; midpoint ties round away from zero.</summary>
public sealed record MoveTransform(Guid ObjectId, Vec3 DeltaMillimeters, double SnapMillimeters = 0) : TransformRequest(ObjectId);
/// <summary>Relative right-handed world X→Y→Z degrees about the combined accepted selection bounds center.
/// Snap quantizes unwrapped degrees to multiples of SnapDegrees; only trigonometry reduces full turns.</summary>
public sealed record RotateTransform(Guid ObjectId, Vec3 Degrees, double SnapDegrees = 0) : TransformRequest(ObjectId);
/// <summary>Positive world-axis factors about the combined accepted selection bounds center. SnapPercent quantizes the change from 100%.</summary>
public sealed record ScaleTransform(Guid ObjectId, Vec3 Factors, double SnapPercent = 0) : TransformRequest(ObjectId);

public sealed partial class EditorSession
{
    /// <summary>
    /// Publishes an unaccepted transform candidate. Replacement requires the exact current transform-preview ID
    /// and target; every successful update receives a fresh preview ID. Cancellation or failure preserves both
    /// accepted state and the prior candidate. Apply/Discard use the existing shared preview boundary.
    /// </summary>
    public Task<OperationResult> PreviewTransformAsync(TransformRequest request, Guid? replacePreviewId = null,
        CancellationToken cancellation = default, IProgress<OperationProgress>? progress = null, long? expectedRevision = null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked) return Task.FromResult(blocked);
            if(request is null) return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose a transform."));
            if(GuardMemberScope(request.MemberGroupId) is {} scopeConflict) return Task.FromResult(scopeConflict);
            if(preview is {} current)
            {
                if(current.Id != replacePreviewId || current.Request is not TransformRequest)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This transform preview is no longer current. Read it again before updating."));
            }
            else if(replacePreviewId.HasValue)
                return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no transform preview to replace."));
            try
            {
                if(request.ObjectIds.IsDefault) throw new ModelException("A transform target collection is required.");
                var canonical=scene.ExpandObjectIds(request.ObjectIds.IsEmpty ? [request.ObjectId] : request.ObjectIds,editingGroupId);
                var targets=request.ObjectIds.SequenceEqual(canonical) ? request.ObjectIds : canonical;
                if(targets.Length==0) throw new ModelException("Choose objects to transform.");
                if(preview?.Request is TransformRequest previous && !previous.ObjectIds.SequenceEqual(targets))
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"A transform update must keep the same target objects."));
                request=request with {ObjectId=targets.Contains(request.ObjectId) ? request.ObjectId : targets[0],ObjectIds=targets};
            }
            catch(ModelException e) { return Task.FromResult(new OperationResult(Outcome.Invalid,e.Message)); }
            var captured = Snapshot;
            // The reentrant gate makes preflight and lease acquisition atomic. Mesh walks execute in
            // RunIo's worker task and never require this gate.
            return RunIo(() => PrepareTransform(request,captured,cancellation,progress),expectedRevision,allowPreview:true);
        }
    }

    private string PrepareTransform(TransformRequest request, EditorSnapshot captured, CancellationToken cancellation,
        IProgress<OperationProgress>? progress)
    {
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new("Preparing transform")); cancellation.ThrowIfCancellationRequested();
        var targets=request.ObjectIds.ToHashSet();
        var originals=captured.Scene.Objects.Where(o=>targets.Contains(o.Id)).ToArray();
        var total=originals.Sum(o=>o.Mesh.Vertices.Length); var completed=0;
        var originalBounds=originals.ToDictionary(o=>o.Id,o=>GeometryCache.Bounds(o,cancellation));
        var bounds = Bounds3.From(originalBounds.Values.SelectMany(b=>new[] {b.Min,b.Max}));
        Affine change; TransformRequest effective; string summary;
        switch(request)
        {
            case MirrorTransform mirror:
                (change,var reflected,summary)=MirrorChange(mirror,bounds);
                effective=reflected;
                break;
            case MoveTransform move:
                var delta=SnapVector(move.DeltaMillimeters,move.SnapMillimeters);
                effective=move with {DeltaMillimeters=delta}; change=Affine.Translation(delta);
                summary=FormattableString.Invariant($"Move preview: Δ X {delta.X:G6}, Y {delta.Y:G6}, Z {delta.Z:G6} mm.");
                break;
            case RotateTransform rotate:
                // Preserve unwrapped effective degrees: the recorded request must replay identically,
                // including when the step does not divide a full turn (170° at 100° snap becomes 200°).
                var degrees=SnapVector(rotate.Degrees,rotate.SnapDegrees);
                effective=rotate with {Degrees=degrees}; change=Affine.RotateAbout(degrees,bounds.Center);
                summary=FormattableString.Invariant($"Rotate preview: world X {degrees.X:G6}°, Y {degrees.Y:G6}°, Z {degrees.Z:G6}° about bounds center.");
                break;
            case ScaleTransform scale:
                ValidateSnap(scale.SnapPercent);
                if(!Positive(scale.Factors)) throw new ModelException("Enter a positive, finite scale factor for each axis.");
                var factors=scale.Factors;
                if(scale.SnapPercent > 0)
                {
                    var step=scale.SnapPercent/100;
                    // A percent step below the smallest representable factor cannot change a factor.
                    if(step > 0) factors=SnapVector(factors-new Vec3(1,1,1),step)+new Vec3(1,1,1);
                }
                if(!Positive(factors)) throw new ModelException("Snapping must leave a positive, finite scale factor for each axis.");
                effective=scale with {Factors=factors}; change=Affine.ScaleAbout(factors,bounds.Center);
                summary=FormattableString.Invariant($"Scale preview: X ×{factors.X:G6}, Y ×{factors.Y:G6}, Z ×{factors.Z:G6} about bounds center (mm).");
                break;
            default: throw new ModelException("Choose a supported transform.");
        }
        var replacements=new Dictionary<Guid,SceneObject>();
        foreach(var original in originals)
        {
            cancellation.ThrowIfCancellationRequested();
            var transformed=effective is MirrorTransform
                ? BakeMirror(original,change,cancellation,progress,completed,total)
                : original with {Transform=original.Transform.Then(change)};
            var resultBounds=GeometryCache.Bounds(transformed,cancellation,progress,"Transforming vertices",completed,total);
            var beforeBounds=originalBounds[original.Id];
            if(effective is ScaleTransform or MirrorTransform &&
                ((beforeBounds.Size.X > 0 && resultBounds.Size.X <= 0) || (beforeBounds.Size.Y > 0 && resultBounds.Size.Y <= 0) ||
                 (beforeBounds.Size.Z > 0 && resultBounds.Size.Z <= 0)))
                throw new ModelException("This transform cannot preserve an object's dimensions at its current position.");
            replacements.Add(original.Id,transformed); completed+=original.Mesh.Vertices.Length;
        }
        var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>replacements.GetValueOrDefault(o.Id,o)).ToImmutableArray());
        // Counts/identity/group invariants are unchanged. Mirror also bakes vertices and compensates
        // winding; the cancellable world-point walk validates every new coordinate against physical limits.
        progress?.Report(new("Ready to preview")); cancellation.ThrowIfCancellationRequested();
        lock(gate)
        {
            cancellation.ThrowIfCancellationRequested();
            preview=new(Guid.NewGuid(),captured.Revision,effective,candidate,captured.SelectedObjectId,request.ObjectIds,summary)
                {SelectedObjectIds=captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId};
        }
        return summary;
    }

    private static bool Positive(Vec3 v)=>v.IsFinite && v.X > 0 && v.Y > 0 && v.Z > 0;
    private static void ValidateSnap(double step)
    {
        if(!double.IsFinite(step) || step < 0) throw new ModelException("Enter a finite snap step of zero (off) or greater.");
    }
    private static Vec3 SnapVector(Vec3 values,double step)
    {
        ValidateSnap(step);
        if(!values.IsFinite) throw new ModelException("Enter a finite transform value for each axis.");
        if(step == 0) return values;
        static double Snap(double value,double step)
        {
            // Remainders avoid overflowing value/step for tiny positive steps. Compare remainder
            // against its complement to handle midpoint ties without underflowing step/2.
            var remainder=value%step;
            if(remainder == 0) return value;
            var snapped=value-remainder;
            if(Math.Abs(remainder) >= step-Math.Abs(remainder)) snapped+=Math.CopySign(step,value);
            if(!double.IsFinite(snapped)) throw new ModelException("Snapping overflowed the supported transform range.");
            return snapped;
        }
        return new(Snap(values.X,step),Snap(values.Y,step),Snap(values.Z,step));
    }
}
