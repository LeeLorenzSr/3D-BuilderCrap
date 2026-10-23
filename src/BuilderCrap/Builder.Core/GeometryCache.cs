using System.Runtime.CompilerServices;

namespace Builder.Core;

// Reference identity is essential: record equality and `with` cloning must never copy cached geometry.
// Values contain no key references; weak keys let discarded previews/history release their entries.
internal static class GeometryCache
{
    private sealed record BoundsValue(Bounds3 Value);
    private sealed class Validated;
    private static readonly ConditionalWeakTable<SceneObject,BoundsValue> ObjectBounds = new();
    private static readonly ConditionalWeakTable<Scene,BoundsValue> SceneBounds = new();
    private static readonly ConditionalWeakTable<Scene,Validated> ValidScenes = new();

    internal static Bounds3 Bounds(SceneObject obj,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null,string stage="Measuring bounds",int completed=0,int total=0)
    {
        cancellation.ThrowIfCancellationRequested();
        if(ObjectBounds.TryGetValue(obj,out var cached))
        {
            progress?.Report(new(stage,completed,total)); cancellation.ThrowIfCancellationRequested();
            progress?.Report(new(stage,completed+obj.Mesh.Vertices.Length,total)); cancellation.ThrowIfCancellationRequested();
            return cached.Value;
        }
        var min=new Vec3(double.PositiveInfinity,double.PositiveInfinity,double.PositiveInfinity);
        var max=new Vec3(double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity);
        for(var i=0;i<obj.Mesh.Vertices.Length;i++)
        {
            if(i%1024==0) {progress?.Report(new(stage,completed+i,total));cancellation.ThrowIfCancellationRequested();}
            var p=obj.Transform.Apply(obj.Mesh.Vertices[i]);
            if(!p.IsFinite || Math.Max(Math.Abs(p.X),Math.Max(Math.Abs(p.Y),Math.Abs(p.Z)))>10_000_000)
                throw new ModelException("Coordinates must be finite and within 10,000,000 mm of the origin.");
            min=new(Math.Min(min.X,p.X),Math.Min(min.Y,p.Y),Math.Min(min.Z,p.Z));
            max=new(Math.Max(max.X,p.X),Math.Max(max.Y,p.Y),Math.Max(max.Z,p.Z));
        }
        progress?.Report(new(stage,completed+obj.Mesh.Vertices.Length,total));
        cancellation.ThrowIfCancellationRequested();
        var result=new Bounds3(min,max);
        // A cancelled/invalid walk is never published. Concurrent read-only queries may compute twice.
        ObjectBounds.AddOrUpdate(obj,new(result));
        return result;
    }

    internal static Bounds3 Bounds(Scene scene,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(SceneBounds.TryGetValue(scene,out var cached)) return cached.Value;
        var result=Union(WorldBounds(scene,cancellation));
        cancellation.ThrowIfCancellationRequested(); SceneBounds.AddOrUpdate(scene,new(result));return result;

        static IEnumerable<Bounds3> WorldBounds(Scene scene,CancellationToken cancellation)
        {
            foreach(var obj in scene.Objects) yield return Bounds(obj,cancellation);
        }
    }

    // Union of exact per-object world bounds is exact for the scene. No transformed local AABB corners.
    internal static Bounds3 Union(IEnumerable<Bounds3> bounds)
    {
        var any=false;var min=new Vec3(double.PositiveInfinity,double.PositiveInfinity,double.PositiveInfinity);
        var max=new Vec3(double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity);
        foreach(var b in bounds)
        {
            min=new(Math.Min(min.X,b.Min.X),Math.Min(min.Y,b.Min.Y),Math.Min(min.Z,b.Min.Z));
            max=new(Math.Max(max.X,b.Max.X),Math.Max(max.Y,b.Max.Y),Math.Max(max.Z,b.Max.Z));any=true;
        }
        return any ? new(min,max) : new(new(),new());
    }
    internal static bool IsValidated(Scene scene)=>ValidScenes.TryGetValue(scene,out _);
    internal static void MarkValidated(Scene scene)=>ValidScenes.GetValue(scene,static _=>new());
}
