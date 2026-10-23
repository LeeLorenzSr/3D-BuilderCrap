using System.Collections.Immutable;

namespace Builder.Core;

public enum SurfacePaintMode { ConnectedSurface, Triangle }
/// <summary>Paints one triangle or an edge-connected region whose world normals are within the
/// angle of the seed normal (not cumulative neighbor angles). Exact coincident world vertices are
/// welded for connectivity. Null removes overrides to the object fallback. First paint on an
/// unassigned object assigns Neutral fallback in the same candidate. Angle range is 0–90 degrees.</summary>
public sealed record SurfaceMaterialRequest(Guid ObjectId,int TriangleIndex,SurfaceMaterial? Material,
    SurfacePaintMode Mode=SurfacePaintMode.ConnectedSurface,double AngleToleranceDegrees=1):GeometryRequest;
public sealed record SurfaceMaterialComparison(Guid ObjectId,ImmutableArray<int> TriangleIndices);

public sealed partial class EditorSession
{
    /// <summary>Stages one accepted-source region without changing geometry or selection. Exact-token
    /// replacement can choose another seed on the same object. Group members require exact member scope.
    /// Failure/cancel preserves accepted work and the preceding candidate; Apply is one Undo step.</summary>
    public Task<OperationResult> PreviewSurfaceMaterialAsync(SurfaceMaterialRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(request is null)return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose a surface to paint."));
            if(GuardMemberScope(request.MemberGroupId) is {} scope)return Task.FromResult(scope);
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId||current.Request is not SurfaceMaterialRequest old||old.ObjectId!=request.ObjectId||current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This surface preview is no longer current or targets another object."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no surface preview to replace."));
            try{request=request with{Material=SurfaceMaterial.Canonical(request.Material)};}
            catch(ModelException e){return Task.FromResult(new OperationResult(Outcome.Invalid,e.Message));}
            var captured=Snapshot;
            return RunIo(()=>
            {
                progress?.Report(new("Preparing surface paint"));cancellation.ThrowIfCancellationRequested();
                var target=captured.Scene.Objects.FirstOrDefault(o=>o.Id==request.ObjectId)??throw new ModelException("The surface target no longer exists.");
                _=captured.Scene.ExpandObjectIds([target.Id],captured.EditingGroupId);
                if(captured.EditingGroupId is null&&captured.Scene.IsGrouped(target.Id))throw new ModelException("Choose Edit members before painting one group member's surface.");
                var indices=SurfacePainting.Region(target,request,cancellation,progress);
                var map=target.TriangleMaterials;var fallback=target.Material;
                if(request.Material is not null)fallback??=SurfaceMaterial.Neutral;
                foreach(var index in indices)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(request.Material is null||request.Material==fallback)map=map.Remove(index);
                    else if(!map.TryGetValue(index,out var old)||old!=request.Material)map=map.SetItem(index,request.Material);
                }
                var obj=fallback==target.Material&&ReferenceEquals(map,target.TriangleMaterials)?target:target with{Material=fallback,TriangleMaterials=map};
                var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>o.Id==target.Id?obj:o).ToImmutableArray());candidate.Validate(cancellation);
                var summary=request.Material is {} material?$"Surface preview: {indices.Length} triangle(s), {material.Name} {material.Color.Hex}.":$"Surface preview: clear {indices.Length} triangle(s) to the object material.";
                progress?.Report(new("Ready to preview surface paint"));cancellation.ThrowIfCancellationRequested();
                lock(gate)
                {
                    cancellation.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,captured.SelectedObjectId,[target.Id],summary)
                        {SelectedObjectIds=captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId,SurfaceMaterial=new(target.Id,indices)};
                }
                return summary;
            },expectedRevision,allowPreview:true);
        }
    }
}

internal static class SurfacePainting
{
    internal static void RequireUnpainted(SceneObject obj,string operation)
    {
        if(obj.TriangleMaterials.Count>0)throw new ModelException(operation+" cannot preserve surface colors yet. Use whole-object Paint or Clear material first to remove surface colors.");
    }
    internal static ImmutableArray<int> Region(SceneObject obj,SurfaceMaterialRequest request,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var count=obj.Mesh.Triangles.Length;
        if(request.TriangleIndex<0||request.TriangleIndex>=count||!Enum.IsDefined(request.Mode)||!double.IsFinite(request.AngleToleranceDegrees)||request.AngleToleranceDegrees<0||request.AngleToleranceDegrees>90)
            throw new ModelException("Choose an existing triangle, a paint mode and a surface angle from 0 to 90 degrees.");
        var world=obj.Mesh.Vertices.Select(obj.Transform.Apply).ToArray();
        Vec3 Normal(Triangle t)
        {
            var n=Vec3.Cross(world[t.B]-world[t.A],world[t.C]-world[t.A]);var scale=Math.Max(Math.Abs(n.X),Math.Max(Math.Abs(n.Y),Math.Abs(n.Z)));
            if(scale==0||!n.IsFinite)return default;n*=1/scale;var length=Math.Sqrt(n.X*n.X+n.Y*n.Y+n.Z*n.Z);return n*(1/length);
        }
        var seed=Normal(obj.Mesh.Triangles[request.TriangleIndex]);if(seed==default)throw new ModelException("A zero-area triangle cannot seed surface painting.");
        if(request.Mode==SurfacePaintMode.Triangle){ct.ThrowIfCancellationRequested();return[request.TriangleIndex];}
        var vertexIds=new Dictionary<Vec3,int>();var ids=new int[world.Length];
        for(var i=0;i<world.Length;i++){if(i%1024==0)ct.ThrowIfCancellationRequested();if(!vertexIds.TryGetValue(world[i],out var id)){id=vertexIds.Count;vertexIds.Add(world[i],id);}ids[i]=id;}
        var edges=new Dictionary<(int,int),List<int>>();var faceEdges=new (int,int)[3*count];var normals=new Vec3[count];
        for(var i=0;i<count;i++)
        {
            if(i%512==0){progress?.Report(new("Finding surface region",i,count));ct.ThrowIfCancellationRequested();}
            var t=obj.Mesh.Triangles[i];normals[i]=Normal(t);var v=new[]{ids[t.A],ids[t.B],ids[t.C]};
            for(var j=0;j<3;j++){var a=v[j];var b=v[(j+1)%3];var key=(Math.Min(a,b),Math.Max(a,b));faceEdges[3*i+j]=key;if(!edges.TryGetValue(key,out var incident))edges.Add(key,incident=[]);incident.Add(i);}
        }
        var cosine=Math.Cos(request.AngleToleranceDegrees*Math.PI/180);var seen=new HashSet<int>{request.TriangleIndex};var queue=new Queue<int>();queue.Enqueue(request.TriangleIndex);
        while(queue.TryDequeue(out var face))
        {
            ct.ThrowIfCancellationRequested();
            for(var e=0;e<3;e++)
            {
                var incident=edges[faceEdges[3*face+e]];
                if(incident.Count>2)throw new ModelException("This surface has a non-manifold edge. Choose single-triangle painting or another surface.");
                foreach(var next in incident)
                {
                    var normal=normals[next];var dot=normal.X*seed.X+normal.Y*seed.Y+normal.Z*seed.Z;
                    if(normal!=default&&dot>=cosine-1e-12&&seen.Add(next))queue.Enqueue(next);
                }
            }
        }
        progress?.Report(new("Finding surface region",count,count));ct.ThrowIfCancellationRequested();return seen.Order().ToImmutableArray();
    }
}
