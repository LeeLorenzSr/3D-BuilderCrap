using System.Collections.Immutable;

namespace Builder.Core;

public sealed partial class EditorSession
{
    // Both array tools share accepted-source copies, resource budgets, group ownership and result selection.
    private static (Scene Scene,string Summary,ImmutableArray<Guid> Selection,Guid? Primary) PrepareArrayCopies(
        ArrangementRequest request,EditorSnapshot captured,long repeats,Func<long,Affine> placementAt,string summary,
        CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        var source=captured.Scene;var targets=request.ObjectIds.ToHashSet();
        var originals=source.Objects.Where(o=>targets.Contains(o.Id)).ToImmutableArray();
        var sourceGroups=captured.EditingGroupId.HasValue?[]:source.Groups.Where(g=>g.ObjectIds.Any(targets.Contains)).ToImmutableArray();
        long addedObjects,addedVertices;
        try
        {
            addedObjects=checked(repeats*originals.Length);addedVertices=checked(repeats*originals.Sum(o=>(long)o.Mesh.Vertices.Length));
            if(checked(source.Objects.Length+addedObjects)>ModelLimits.MaxObjects||
                checked(source.TriangleCount+repeats*originals.Sum(o=>(long)o.Mesh.Triangles.Length))>ModelLimits.MaxTriangles||
                checked(source.Objects.Sum(o=>(long)o.Mesh.Vertices.Length)+addedVertices)>ModelLimits.MaxVertices||
                checked(source.Groups.Length+repeats*sourceGroups.Length)>ModelLimits.MaxObjects/2)
                throw new ModelException("This array would exceed the scene object, triangle, vertex or group limit. Reduce the counts or selection.");
        }
        catch(OverflowException){throw new ModelException("This array is too large. Reduce the counts.");}
        if(repeats==0)return(source,summary+" No document change.",captured.SelectedObjectIds,captured.SelectedObjectId);
        // The native format shares identical copied resources, but each copied group needs its own resource.
        var resourceKeys=source.Objects.Concat(originals.Select(o=>o with{Name=CopyName(o.Name)})).Select(o=>
            (o.Mesh,o.Name,o.Material,Faces:string.Concat(o.TriangleMaterials.OrderBy(p=>p.Key).Select(p=>$"{p.Key}:{p.Value.Name.Length}:{p.Value.Name}{p.Value.Color.Hex};")))).Distinct().Count();
        if(resourceKeys+source.Groups.Length+repeats*sourceGroups.Length>ModelLimits.MaxObjects)
            throw new ModelException("Saving this array would exceed the 3MF group/resource limit. Reduce the counts or selection.");
        var copies=ImmutableArray.CreateBuilder<SceneObject>((int)addedObjects);var groups=source.Groups.ToBuilder();
        Guid? primary=null;var completed=0;
        for(long placementIndex=1;placementIndex<=repeats;placementIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            var placement=placementAt(placementIndex);var mapping=new Dictionary<Guid,Guid>();
            foreach(var original in originals)
            {
                progress?.Report(new("Creating array copies",completed,(int)addedVertices));cancellation.ThrowIfCancellationRequested();
                var copy=original with{Id=Guid.NewGuid(),Name=CopyName(original.Name),Transform=original.Transform.Then(placement)};
                _=GeometryCache.Bounds(copy,cancellation,progress,"Creating array copies",completed,(int)addedVertices);
                mapping.Add(original.Id,copy.Id);copies.Add(copy);completed+=original.Mesh.Vertices.Length;
                if(primary is null&&original.Id==captured.SelectedObjectId)primary=copy.Id;
            }
            foreach(var group in sourceGroups)
            {cancellation.ThrowIfCancellationRequested();groups.Add(new(Guid.NewGuid(),CopyName(group.Name),group.ObjectIds.Select(id=>mapping[id]).ToImmutableArray()));}
        }
        var selectedCopies=copies.Select(o=>o.Id).ToImmutableArray();
        var candidate=source.WithObjects(source.Objects.AddRange(copies)) with{Groups=groups.ToImmutable()};
        if(captured.EditingGroupId is {} scope)candidate=candidate.ReplaceMemberIds(scope,[],selectedCopies);
        candidate.Validate(cancellation);
        return(candidate,summary+$" {copies.Count} new objects.",selectedCopies,primary??selectedCopies[0]);
    }
}
