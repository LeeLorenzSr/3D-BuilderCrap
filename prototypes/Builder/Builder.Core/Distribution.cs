using System.Collections.Immutable;

namespace Builder.Core;

public enum DistributionAxis { X=0, Y=1, Z=2 }
public enum DistributionSpacing { Centers=0, Gaps=1 }
/// <summary>Distributes at least three accepted selection units along one world axis. Root-scope groups
/// move rigidly; exact member scope treats selected leaves independently. Units sort by accepted bounds
/// center with document-order ties; first/last units stay fixed. Centers may overlap. Gaps refuse overlap.</summary>
public sealed record DistributionRequest(ImmutableArray<Guid> ObjectIds,DistributionAxis Axis=DistributionAxis.X,
    DistributionSpacing Spacing=DistributionSpacing.Centers):ArrangementRequest(ObjectIds);

public sealed partial class EditorSession
{
    private static double DistributionCoordinate(Vec3 point,DistributionAxis axis)=>axis switch
    {DistributionAxis.X=>point.X,DistributionAxis.Y=>point.Y,_=>point.Z};
    private static (Scene Scene,string Summary) PrepareDistribution(DistributionRequest request,EditorSnapshot captured,
        CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        if(!Enum.IsDefined(request.Axis)||!Enum.IsDefined(request.Spacing))throw new ModelException("Choose X, Y or Z and either Centers or Gaps.");
        progress?.Report(new("Preparing distribution"));cancellation.ThrowIfCancellationRequested();
        var units=captured.Scene.GetSelectionUnits(request.ObjectIds,cancellation,progress,captured.EditingGroupId)
            .Select((unit,index)=>(Unit:unit,Index:index))
            .OrderBy(p=>DistributionCoordinate(p.Unit.Bounds.Center,request.Axis)).ThenBy(p=>p.Index).Select(p=>p.Unit).ToArray();
        if(units.Length<3)throw new ModelException("Choose at least three independent objects or groups to distribute.");
        double Center(SceneSelectionUnit unit)=>DistributionCoordinate(unit.Bounds.Center,request.Axis);
        double Min(SceneSelectionUnit unit)=>DistributionCoordinate(unit.Bounds.Min,request.Axis);
        double Max(SceneSelectionUnit unit)=>DistributionCoordinate(unit.Bounds.Max,request.Axis);
        var gap=0d;var cursor=Max(units[0]);
        if(request.Spacing==DistributionSpacing.Gaps)
        {
            var interiorWidth=units.Skip(1).Take(units.Length-2).Sum(u=>Max(u)-Min(u));
            var available=Min(units[^1])-Max(units[0])-interiorWidth;
            if(available<0)throw new ModelException("There is not enough room for non-overlapping equal gaps. Move the outer items apart or choose Centers.");
            gap=available/(units.Length-1);
        }
        var originals=captured.Scene.Objects.ToDictionary(o=>o.Id);var replacements=new Dictionary<Guid,SceneObject>();
        var total=units.Skip(1).Take(units.Length-2).SelectMany(u=>u.ObjectIds).Sum(id=>originals[id].Mesh.Vertices.Length);var completed=0;
        for(var i=1;i<units.Length-1;i++)
        {
            var unit=units[i];cancellation.ThrowIfCancellationRequested();
            var target=request.Spacing==DistributionSpacing.Centers
                ? Center(units[0])+(Center(units[^1])-Center(units[0]))*i/(units.Length-1)
                : cursor+gap+(Max(unit)-Min(unit))/2;
            var distance=target-Center(unit);
            if(request.Spacing==DistributionSpacing.Gaps)cursor=target+(Max(unit)-Min(unit))/2;
            foreach(var id in unit.ObjectIds)
            {
                progress?.Report(new("Distributing objects",completed,total));cancellation.ThrowIfCancellationRequested();
                var original=originals[id];
                if(distance!=0)
                {
                    var delta=request.Axis switch{DistributionAxis.X=>new Vec3(distance,0,0),DistributionAxis.Y=>new Vec3(0,distance,0),_=>new Vec3(0,0,distance)};
                    var moved=original with{Transform=original.Transform.Then(Affine.Translation(delta))};
                    _=GeometryCache.Bounds(moved,cancellation,progress,"Distributing objects",completed,total);replacements.Add(id,moved);
                }
                completed+=original.Mesh.Vertices.Length;
                progress?.Report(new("Distributing objects",completed,total));cancellation.ThrowIfCancellationRequested();
            }
        }
        var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>replacements.GetValueOrDefault(o.Id,o)).ToImmutableArray());candidate.Validate(cancellation);
        var summary=request.Spacing==DistributionSpacing.Centers
            ? $"Distribute preview: {units.Length} units, equal centers on world {request.Axis}. Outer units stay fixed; objects may overlap."
            : FormattableString.Invariant($"Distribute preview: {units.Length} units, equal {gap:G6} mm gaps on world {request.Axis}. Outer units stay fixed.");
        return(candidate,summary);
    }
}
