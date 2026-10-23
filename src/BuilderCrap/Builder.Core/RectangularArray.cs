using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Repeats the complete accepted selection in a rectangular world-axis grid. Counts include the
/// original cell. Spacing is a signed translation step in millimeters, not a bounds gap. Root groups are
/// copied intact; explicit member copies join the current group. All counts of one is an exact no-op.</summary>
public sealed record RectangularArrayRequest(ImmutableArray<Guid> ObjectIds,int CountX,int CountY,int CountZ,
    Vec3 SpacingMillimeters):ArrangementRequest(ObjectIds);

public sealed partial class EditorSession
{
    private static (Scene Scene,string Summary,ImmutableArray<Guid> Selection,Guid? Primary) PrepareRectangularArray(
        RectangularArrayRequest request,EditorSnapshot captured,CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        if(request.CountX<1||request.CountY<1||request.CountZ<1)
            throw new ModelException("Array counts must be whole numbers of at least one, including the original cell.");
        var step=request.SpacingMillimeters;
        if(!step.IsFinite||request.CountX>1&&step.X==0||request.CountY>1&&step.Y==0||request.CountZ>1&&step.Z==0)
            throw new ModelException("Enter a finite, nonzero spacing for each repeated axis. Signed spacing sets the world direction.");
        progress?.Report(new("Preparing rectangular array"));cancellation.ThrowIfCancellationRequested();
        long repeats;
        try {repeats=checked((long)request.CountX*request.CountY*request.CountZ-1);}
        catch(OverflowException){throw new ModelException("This array is too large. Reduce the counts.");}
        Affine Placement(long index)
        {
            var x=index%request.CountX;var y=index/request.CountX%request.CountY;var z=index/request.CountX/request.CountY;
            return Affine.Translation(new(step.X*x,step.Y*y,step.Z*z));
        }
        return PrepareArrayCopies(request,captured,repeats,Placement,
            $"Array preview: {request.CountX} × {request.CountY} × {request.CountZ} cells including the originals. Spacing is a world-axis step in millimeters.",cancellation,progress);
    }
}
