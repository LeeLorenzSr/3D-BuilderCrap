using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>A viewport-independent world ray with inclusive world-mm clipping distances.</summary>
public sealed record SurfaceBrushSample(Vec3 Origin,Vec3 Direction,double MinimumDistance=0,double MaximumDistance=double.PositiveInfinity);
public sealed record SurfaceBrushStroke(double DiameterMillimeters,SurfaceMaterial? Material,ImmutableArray<SurfaceBrushSample> Samples);
/// <summary>Replays the complete ordered stroke list from accepted content. Each visible target hit seeds an
/// edge-connected brush sphere. Whole intersecting triangles are painted, subject to centroid line-of-sight
/// from that sample's origin; the visible seed is always included. This is a whole-triangle approximation,
/// not texture painting. Other objects and back surfaces remain occluders regardless of winding.</summary>
public sealed record SurfaceBrushRequest(Guid ObjectId,ImmutableArray<SurfaceBrushStroke> Strokes):GeometryRequest;
public sealed record SurfaceBrushComparison(Guid ObjectId,ImmutableArray<int> TriangleIndices,int SampleCount);

public sealed partial class EditorSession
{
    /// <summary>Stages a complete accepted-source brush replay. Replacing requires the current brush token,
    /// same target and exact scope. Failure/cancellation retains accepted work and the preceding candidate.</summary>
    public Task<OperationResult> PreviewSurfaceBrushAsync(SurfaceBrushRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(request is null)return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose a surface brush target and strokes."));
            if(GuardMemberScope(request.MemberGroupId) is {} scope)return Task.FromResult(scope);
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId||current.Request is not SurfaceBrushRequest old||old.ObjectId!=request.ObjectId||current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This brush preview is no longer current or targets another object."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no brush preview to replace."));
            var captured=Snapshot;
            return RunIo(()=>
            {
                progress?.Report(new("Preparing surface brush"));cancellation.ThrowIfCancellationRequested();
                request=SurfaceBrushCoverage.Validate(request,cancellation);
                var target=captured.Scene.Objects.FirstOrDefault(o=>o.Id==request.ObjectId)??throw new ModelException("The brush target no longer exists.");
                _=captured.Scene.ExpandObjectIds([target.Id],captured.EditingGroupId);
                if(captured.EditingGroupId is null&&captured.Scene.IsGrouped(target.Id))throw new ModelException("Choose Edit members before brushing one group member.");
                captured.Scene.Validate(cancellation);
                var coverage=new SurfaceBrushCoverage(captured.Scene,captured.EditingGroupId,target,cancellation,progress);
                var map=target.TriangleMaterials;var fallback=target.Material;var affected=new HashSet<int>();var sampleCount=0;
                foreach(var stroke in request.Strokes)
                {
                    var indices=coverage.Stroke(stroke,cancellation,progress);sampleCount+=stroke.Samples.Length;
                    if(indices.Count==0)throw new ModelException("A brush stroke did not hit a visible surface of this object. Aim at the object and try again.");
                    if(stroke.Material is not null)fallback??=SurfaceMaterial.Neutral;
                    foreach(var index in indices)
                    {
                        cancellation.ThrowIfCancellationRequested();affected.Add(index);
                        if(stroke.Material is null||stroke.Material==fallback)map=map.Remove(index);
                        else map=map.SetItem(index,stroke.Material);
                    }
                }
                // A replay can repaint and then clear the same region. Recover original immutable ownership
                // when its final material state is identical, so Apply does not create a no-op history entry.
                if(map.Count==target.TriangleMaterials.Count&&map.All(p=>target.TriangleMaterials.TryGetValue(p.Key,out var old)&&old==p.Value))map=target.TriangleMaterials;
                var obj=fallback==target.Material&&ReferenceEquals(map,target.TriangleMaterials)?target:target with{Material=fallback,TriangleMaterials=map};
                var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>o.Id==target.Id?obj:o).ToImmutableArray());candidate.Validate(cancellation);
                var comparison=new SurfaceBrushComparison(target.Id,affected.Order().ToImmutableArray(),sampleCount);
                var summary=$"Brush preview: {request.Strokes.Length} stroke(s), {sampleCount} ray sample(s), {comparison.TriangleIndices.Length} whole triangle(s). Visibility uses triangle centers; mesh detail sets edge precision.";
                progress?.Report(new("Ready to preview surface brush"));cancellation.ThrowIfCancellationRequested();
                lock(gate)
                {
                    cancellation.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,captured.SelectedObjectId,[target.Id],summary)
                        {SelectedObjectIds=captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId,SurfaceBrush=comparison};
                }
                return summary;
            },expectedRevision,allowPreview:true);
        }
    }
}

internal sealed class SurfaceBrushCoverage
{
    private readonly Scene scene;
    private readonly Guid? scope;
    private readonly SceneObject target;
    private readonly Vec3[] world;
    private readonly List<int>[] neighbors;
    private readonly int[] visited;
    private int stamp;
    internal SurfaceBrushCoverage(Scene scene,Guid? scope,SceneObject target,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        this.scene=scene;this.scope=scope;this.target=target;world=new Vec3[target.Mesh.Vertices.Length];
        var weld=new Dictionary<Vec3,int>();var ids=new int[world.Length];
        for(var i=0;i<world.Length;i++)
        {if(i%1024==0)ct.ThrowIfCancellationRequested();var point=world[i]=target.Transform.Apply(target.Mesh.Vertices[i]);if(!weld.TryGetValue(point,out var id)){id=weld.Count;weld.Add(point,id);}ids[i]=id;}
        var count=target.Mesh.Triangles.Length;neighbors=new List<int>[count];visited=new int[count];var edges=new Dictionary<(int,int),List<int>>();
        for(var i=0;i<count;i++)
        {
            if(i%512==0){progress?.Report(new("Preparing brush connectivity",i,count));ct.ThrowIfCancellationRequested();}neighbors[i]=[];var t=target.Mesh.Triangles[i];var v=new[]{ids[t.A],ids[t.B],ids[t.C]};
            for(var e=0;e<3;e++){var a=v[e];var b=v[(e+1)%3];var key=(Math.Min(a,b),Math.Max(a,b));if(!edges.TryGetValue(key,out var incident))edges.Add(key,incident=[]);incident.Add(i);}
        }
        foreach(var edge in edges.Values)
        {ct.ThrowIfCancellationRequested();if(edge.Count>2)throw new ModelException("The brush needs an edge-manifold surface. Choose another object or repair its topology first.");if(edge.Count==2){neighbors[edge[0]].Add(edge[1]);neighbors[edge[1]].Add(edge[0]);}}
    }
    internal static SurfaceBrushRequest Validate(SurfaceBrushRequest request,CancellationToken ct)
    {
        if(request.Strokes.IsDefaultOrEmpty||request.Strokes.Length>128)throw new ModelException("Use 1–128 brush strokes per preview.");
        var strokes=ImmutableArray.CreateBuilder<SurfaceBrushStroke>(request.Strokes.Length);var total=0;
        foreach(var stroke in request.Strokes)
        {
            ct.ThrowIfCancellationRequested();
            if(stroke is null||!double.IsFinite(stroke.DiameterMillimeters)||stroke.DiameterMillimeters<.001||stroke.DiameterMillimeters>10000||stroke.Samples.IsDefaultOrEmpty||stroke.Samples.Length>4096)
                throw new ModelException("Each brush stroke needs a diameter from 0.001 to 10,000 mm and 1–4,096 ray samples.");
            total+=stroke.Samples.Length;if(total>8192)throw new ModelException("Use at most 8,192 brush ray samples per preview.");
            foreach(var sample in stroke.Samples)
            {
                ct.ThrowIfCancellationRequested();
                if(sample is null||!sample.Origin.IsFinite||!sample.Direction.IsFinite||sample.Direction==default||!double.IsFinite(sample.MinimumDistance)||sample.MinimumDistance<0||double.IsNaN(sample.MaximumDistance)||sample.MaximumDistance<sample.MinimumDistance)
                    throw new ModelException("Brush rays need finite origins/nonzero directions and valid nonnegative clipping distances.");
            }
            strokes.Add(stroke with{Material=SurfaceMaterial.Canonical(stroke.Material)});
        }
        return request with{Strokes=strokes.ToImmutable()};
    }
    internal HashSet<int> Stroke(SurfaceBrushStroke stroke,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var covered=new HashSet<int>();var radiusSquared=stroke.DiameterMillimeters*stroke.DiameterMillimeters/4;var completed=0;
        foreach(var sample in stroke.Samples)
        {
            progress?.Report(new("Painting brush samples",completed++,stroke.Samples.Length));ct.ThrowIfCancellationRequested();
            var seed=RayPicking.Pick(scene,scope,sample.Origin,sample.Direction,sample.MinimumDistance,sample.MaximumDistance,ct,RayPickSides.Both,1e-10);
            if(seed is null||seed.ObjectId!=target.Id||!seed.IsSelectable)continue;
            var queue=new Queue<int>();queue.Enqueue(seed.TriangleIndex);visited[seed.TriangleIndex]=++stamp;
            while(queue.TryDequeue(out var index))
            {
                ct.ThrowIfCancellationRequested();var t=target.Mesh.Triangles[index];var a=world[t.A];var b=world[t.B];var c=world[t.C];
                if(index!=seed.TriangleIndex&&DistanceSquared(seed.Position,a,b,c)>radiusSquared)continue;
                if(index==seed.TriangleIndex||Visible(sample,index,(a+b+c)*(1d/3),ct))covered.Add(index);
                foreach(var next in neighbors[index])if(visited[next]!=stamp){visited[next]=stamp;queue.Enqueue(next);}
            }
        }
        ct.ThrowIfCancellationRequested();return covered;
    }
    // A conservative barycentric edge tolerance prevents visibility rays from slipping between
    // adjacent triangles at a shared diagonal. Public PickRay keeps its established zero-tolerance policy.
    private bool Visible(SurfaceBrushSample sample,int index,Vec3 centroid,CancellationToken ct)
    {
        var direction=centroid-sample.Origin;var distance=Math.Sqrt(Dot(direction,direction));
        if(!double.IsFinite(distance)||distance<sample.MinimumDistance||distance>sample.MaximumDistance||distance==0)return false;
        var nearest=RayPicking.Pick(scene,scope,sample.Origin,direction,sample.MinimumDistance,Math.Min(sample.MaximumDistance,distance+.001),ct,RayPickSides.Both,1e-10);
        if(nearest is null||nearest.ObjectId!=target.Id||!nearest.IsSelectable)return false;
        return nearest.TriangleIndex==index||neighbors[index].Contains(nearest.TriangleIndex)&&Dot(nearest.Position-centroid,nearest.Position-centroid)<=.001*.001;
    }
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    // Closest point on a triangle (with segment fallback for zero-area authored faces).
    private static double DistanceSquared(Vec3 p,Vec3 a,Vec3 b,Vec3 c)
    {
        static double Segment(Vec3 p,Vec3 a,Vec3 b){var e=b-a;var length=Dot(e,e);var t=length==0?0:Math.Clamp(Dot(p-a,e)/length,0,1);var d=p-(a+e*t);return Dot(d,d);}
        var ab=b-a;var ac=c-a;var normal=Vec3.Cross(ab,ac);
        if(Dot(normal,normal)==0)return Math.Min(Segment(p,a,b),Math.Min(Segment(p,a,c),Segment(p,b,c)));
        var ap=p-a;var d1=Dot(ab,ap);var d2=Dot(ac,ap);if(d1<=0&&d2<=0)return Dot(ap,ap);
        var bp=p-b;var d3=Dot(ab,bp);var d4=Dot(ac,bp);if(d3>=0&&d4<=d3)return Dot(bp,bp);
        var vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0){var d=p-(a+ab*(d1/(d1-d3)));return Dot(d,d);}
        var cp=p-c;var d5=Dot(ab,cp);var d6=Dot(ac,cp);if(d6>=0&&d5<=d6)return Dot(cp,cp);
        var vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0){var d=p-(a+ac*(d2/(d2-d6)));return Dot(d,d);}
        var va=d3*d6-d5*d4;if(va<=0&&d4-d3>=0&&d5-d6>=0){var d=p-(b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6))));return Dot(d,d);}
        var inverse=1/(va+vb+vc);var offset=p-(a+ab*(vb*inverse)+ac*(vc*inverse));return Dot(offset,offset);
    }
}
