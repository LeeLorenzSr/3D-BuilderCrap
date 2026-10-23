namespace Builder.Core;

/// <summary>Read-only millimeter distance between explicit world points. Delta is second minus first.
/// CapturedRevision and EditingGroupId identify the accepted session snapshot, not a viewport or preview.</summary>
public sealed record PointMeasurement(Vec3 FirstMillimeters, Vec3 SecondMillimeters, Vec3 DeltaMillimeters,
    double DistanceMillimeters, long CapturedRevision, Guid? EditingGroupId);

public sealed partial class EditorSession
{
    /// <summary>Measures explicit physical world points without changing document, selection, preview,
    /// history or busy state. Available during jobs/previews. Points need not lie on a mesh: the caller
    /// validates its own surface picks against their captured revision and scope. Coordinates must be
    /// finite and within ±10,000,000 mm; zero distance is valid. No snapping or annotations are stored.
    /// Invalid coordinates, stale revision or mismatched exact member scope throw ModelException;
    /// cancellation throws OperationCanceledException. A later edit does not alter this captured result.</summary>
    public PointMeasurement MeasureDistance(Vec3 firstMillimeters, Vec3 secondMillimeters,
        long? expectedRevision = null, Guid? memberGroupId = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        long capturedRevision;Guid? capturedScope;
        lock(gate)
        {
            if(expectedRevision.HasValue && expectedRevision.Value!=revision)
                throw new ModelException("The scene changed. Pick the measurement points again.");
            if(memberGroupId!=editingGroupId)
                throw new ModelException("The editing scope changed. Pick the measurement points again.");
            capturedRevision=revision;capturedScope=editingGroupId;
        }
        static void Validate(Vec3 p)
        {
            if(!p.IsFinite || Math.Max(Math.Abs(p.X),Math.Max(Math.Abs(p.Y),Math.Abs(p.Z)))>10_000_000)
                throw new ModelException("Measurement points must be finite and within 10,000,000 mm of the origin.");
        }
        Validate(firstMillimeters);Validate(secondMillimeters);
        var delta=secondMillimeters-firstMillimeters;
        var largest=Math.Max(Math.Abs(delta.X),Math.Max(Math.Abs(delta.Y),Math.Abs(delta.Z)));
        var distance=0d;
        if(largest>0)
        {
            var x=delta.X/largest;var y=delta.Y/largest;var z=delta.Z/largest;
            distance=largest*Math.Sqrt(x*x+y*y+z*z);
        }
        cancellation.ThrowIfCancellationRequested();
        return new(firstMillimeters,secondMillimeters,delta,distance,capturedRevision,capturedScope);
    }
}
