using System.Collections.Immutable;

namespace Builder.Core;

public enum RadialArrayAxis { X=0,Y=1,Z=2 }
/// <summary>Copies the entire accepted selection around an explicit world-axis line through PivotMillimeters.
/// Count includes the originals. Signed angular steps are at most 360 degrees. RotateCopies rotates each set
/// rigidly; false translates its aggregate bounds center around that line while keeping orientation/offsets.
/// Copies may overlap, including repeated full turns. Count one is an exact no-op.</summary>
public sealed record RadialArrayRequest(ImmutableArray<Guid> ObjectIds,int Count,double AngleStepDegrees,
    RadialArrayAxis Axis,Vec3 PivotMillimeters,bool RotateCopies=true):ArrangementRequest(ObjectIds);

public sealed partial class EditorSession
{
    private static (Scene Scene,string Summary,ImmutableArray<Guid> Selection,Guid? Primary) PrepareRadialArray(
        RadialArrayRequest request,EditorSnapshot captured,CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        if(request.Count<1)throw new ModelException("Radial array count must include at least the original placement.");
        if(!double.IsFinite(request.AngleStepDegrees)||Math.Abs(request.AngleStepDegrees)>360||request.Count>1&&request.AngleStepDegrees==0)
            throw new ModelException("Enter a finite angle step from -360 to 360 degrees, nonzero when making copies.");
        if(!Enum.IsDefined(request.Axis))throw new ModelException("Choose world X, Y or Z for the radial array axis.");
        var pivot=request.PivotMillimeters;
        if(!pivot.IsFinite||Math.Max(Math.Abs(pivot.X),Math.Max(Math.Abs(pivot.Y),Math.Abs(pivot.Z)))>10_000_000)
            throw new ModelException("The pivot must be finite and within 10,000,000 mm of the origin on each axis.");
        progress?.Report(new("Preparing radial array"));cancellation.ThrowIfCancellationRequested();
        Vec3? acceptedCenter=null;
        Affine Placement(long index)
        {
            var angle=(index*request.AngleStepDegrees)%360;
            var degrees=request.Axis switch{RadialArrayAxis.X=>new Vec3(angle,0,0),RadialArrayAxis.Y=>new Vec3(0,angle,0),_=>new Vec3(0,0,angle)};
            var rotation=Affine.RotateAbout(degrees,pivot);
            if(request.RotateCopies)return rotation;
            if(acceptedCenter is null)
            {
                var targets=request.ObjectIds.ToHashSet();
                acceptedCenter=GroupEditBounds(captured.Scene.Objects.Where(o=>targets.Contains(o.Id)).ToImmutableArray(),cancellation,progress,"Measuring radial center").Center;
            }
            var center=acceptedCenter.Value;return Affine.Translation(rotation.Apply(center)-center);
        }
        var summary=FormattableString.Invariant($"Radial array preview: {request.Count} placements including the originals, {request.AngleStepDegrees:G6}° per step around world {request.Axis}; ")+
            (request.RotateCopies?"copies rotate with the selected set.":"copies keep their accepted orientation and internal offsets.");
        return PrepareArrayCopies(request,captured,request.Count-1L,Placement,summary,cancellation,progress);
    }
}
