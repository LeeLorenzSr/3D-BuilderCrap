using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Translates the aggregate accepted selection's world XY bounds center to (0,0).
/// PlaceOnBed also translates its lowest world Z to zero; otherwise height is unchanged.
/// A requested translation within 0.001 mm on every axis is a no-op.
/// Whole groups expand at scene scope; explicit member scope confines the operation to selected members.</summary>
public sealed record CenterOnPlateRequest(ImmutableArray<Guid> ObjectIds,bool PlaceOnBed=false):ArrangementRequest(ObjectIds);

public sealed partial class EditorSession
{
    private static (Scene Scene,string Summary) PrepareCenterOnPlate(CenterOnPlateRequest request,EditorSnapshot captured,
        CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        progress?.Report(new("Preparing center on plate"));cancellation.ThrowIfCancellationRequested();
        var targets=request.ObjectIds.ToHashSet();var originals=captured.Scene.Objects.Where(o=>targets.Contains(o.Id)).ToImmutableArray();
        var bounds=GroupEditBounds(originals,cancellation,progress,"Measuring center bounds");
        var delta=new Vec3(-bounds.Center.X,-bounds.Center.Y,request.PlaceOnBed?-bounds.Min.Z:0);
        var summary=request.PlaceOnBed?"Center preview: selected set centered on build-plate X/Y and lowest point placed on Z=0. Internal offsets stay unchanged.":
            "Center preview: selected set centered on build-plate X/Y; world Z and internal offsets stay unchanged.";
        if(Math.Abs(delta.X)<=.001&&Math.Abs(delta.Y)<=.001&&Math.Abs(delta.Z)<=.001)return(captured.Scene,summary+" No change.");
        var replacements=new Dictionary<Guid,SceneObject>();var total=originals.Sum(o=>o.Mesh.Vertices.Length);var completed=0;
        foreach(var original in originals)
        {
            cancellation.ThrowIfCancellationRequested();
            var moved=original with{Transform=original.Transform.Then(Affine.Translation(delta))};
            _=GeometryCache.Bounds(moved,cancellation,progress,"Centering objects",completed,total);
            replacements.Add(original.Id,moved);completed+=original.Mesh.Vertices.Length;
        }
        var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>replacements.GetValueOrDefault(o.Id,o)).ToImmutableArray());
        candidate.Validate(cancellation);return(candidate,summary);
    }
}
