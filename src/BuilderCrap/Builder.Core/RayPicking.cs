namespace Builder.Core;

/// <summary>Triangle side relative to its transformed world-space vertex winding. Both matches the desktop's two-sided material.</summary>
public enum RayPickSides { Both, Front, Back }

/// <summary>A leaf hit in millimeters. Outside-group leaves remain occluders and report IsSelectable=false in member scope.</summary>
public sealed record RayPickHit(Guid ObjectId,Vec3 Position,double Distance,int TriangleIndex,bool IsSelectable)
{
    /// <summary>Session revision captured at invocation, or null for a pure Scene query.</summary>
    public long? CapturedRevision { get; init; }
    public Guid? EditingGroupId { get; init; }
}

public sealed partial record Scene
{
    /// <summary>Pure synchronous equivalent of EditorSession.PickAsync. No camera/session state; see its ray contract.</summary>
    public RayPickHit? PickRay(Vec3 origin,Vec3 direction,double minimumDistance=0,double maximumDistance=double.PositiveInfinity,
        CancellationToken cancellation=default,RayPickSides sides=RayPickSides.Both,Guid? memberGroupId=null)
    {
        Validate(cancellation);
        if(memberGroupId is {} scope && !Groups.Any(g=>g.Id==scope)) throw new ModelException("The edited group no longer exists.");
        return RayPicking.Pick(this,memberGroupId,origin,direction,minimumDistance,maximumDistance,cancellation,sides);
    }
}

public sealed partial class EditorSession
{
    /// <summary>
    /// Queries the accepted immutable scene captured at invocation, independently of camera, busy state and previews.
    /// The finite nonzero direction is normalized; minimumDistance/maximumDistance are inclusive world millimeters
    /// (maximum may be positive infinity). The nearest leaf wins, with document/triangle order breaking exact ties.
    /// All leaves are tested even in member scope, so an outside object occludes members. Concurrent edits do not
    /// change the captured query. No selection, revision, history or preview changes. Invalid input throws ModelException;
    /// cancellation throws OperationCanceledException. Singular transforms are tested as world triangles: collapsed
    /// triangles have no hit, while surviving planar faces remain pickable. Reflection affects winding naturally.
    /// </summary>
    public Task<RayPickHit?> PickAsync(Vec3 origin,Vec3 direction,double minimumDistance=0,
        double maximumDistance=double.PositiveInfinity,CancellationToken cancellation=default,RayPickSides sides=RayPickSides.Both)
    {
        Scene captured;Guid? scope;long capturedRevision;
        lock(gate) {captured=scene;scope=editingGroupId;capturedRevision=revision;}
        return Task.Run(()=>{
            var hit=captured.PickRay(origin,direction,minimumDistance,maximumDistance,cancellation,sides,scope);
            return hit is null ? null : hit with {CapturedRevision=capturedRevision,EditingGroupId=scope};
        },cancellation);
    }
}

internal static class RayPicking
{
    internal static RayPickHit? Pick(Scene scene,Guid? scope,Vec3 origin,Vec3 direction,double minimumDistance,
        double maximumDistance,CancellationToken cancellation,RayPickSides sides,double barycentricTolerance=0)
    {
        cancellation.ThrowIfCancellationRequested();
        var largest=Math.Max(Math.Abs(direction.X),Math.Max(Math.Abs(direction.Y),Math.Abs(direction.Z)));
        if(!origin.IsFinite || !direction.IsFinite || largest==0 || !double.IsFinite(minimumDistance) || minimumDistance<0 ||
            double.IsNaN(maximumDistance) || maximumDistance<minimumDistance || !Enum.IsDefined(sides) || !double.IsFinite(barycentricTolerance) || barycentricTolerance<0)
            throw new ModelException("Use a finite ray origin and nonzero direction, with a valid nonnegative distance range and triangle side.");
        // Scale before squaring: finite extremely large/small directions must not overflow/underflow normalization.
        direction=new(direction.X/largest,direction.Y/largest,direction.Z/largest);
        direction*=1/Math.Sqrt(Dot(direction,direction));
        var members=scope is {} groupId ? scene.Groups.Single(g=>g.Id==groupId).ObjectIds.ToHashSet() : null;
        RayPickHit? result=null;var nearest=maximumDistance;
        foreach(var obj in scene.Objects)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!Intersects(GeometryCache.Bounds(obj,cancellation),origin,direction,minimumDistance,nearest)) continue;
            for(var index=0;index<obj.Mesh.Triangles.Length;index++)
            {
                if(index%256==0) cancellation.ThrowIfCancellationRequested();
                var triangle=obj.Mesh.Triangles[index];
                var a=obj.Transform.Apply(obj.Mesh.Vertices[triangle.A]);
                var b=obj.Transform.Apply(obj.Mesh.Vertices[triangle.B]);
                var c=obj.Transform.Apply(obj.Mesh.Vertices[triangle.C]);
                var edge1=b-a;var edge2=c-a;var cross=Vec3.Cross(direction,edge2);var determinant=Dot(edge1,cross);
                if(determinant==0 || (sides==RayPickSides.Front && determinant<0) || (sides==RayPickSides.Back && determinant>0)) continue;
                var fromA=origin-a;var u=Dot(fromA,cross)/determinant;
                if(u< -barycentricTolerance || u>1+barycentricTolerance) continue;
                var q=Vec3.Cross(fromA,edge1);var v=Dot(direction,q)/determinant;
                if(v< -barycentricTolerance || u+v>1+barycentricTolerance) continue;
                var distance=Dot(edge2,q)/determinant;
                if(!double.IsFinite(distance) || distance<minimumDistance || distance>nearest || (result!=null && distance==nearest)) continue;
                // Barycentric position avoids subtracting two very large ray distances at the hit point.
                var position=a+edge1*u+edge2*v;
                if(!position.IsFinite) continue;
                nearest=distance;result=new(obj.Id,position,distance,index,members is null || members.Contains(obj.Id));
            }
        }
        cancellation.ThrowIfCancellationRequested();return result;
    }

    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static bool Intersects(Bounds3 bounds,Vec3 origin,Vec3 direction,double minimum,double maximum)
    {
        static bool Slab(double origin,double direction,double low,double high,ref double min,ref double max)
        {
            if(direction==0) return origin>=low && origin<=high;
            var a=(low-origin)/direction;var b=(high-origin)/direction;
            if(a>b) (a,b)=(b,a);min=Math.Max(min,a);max=Math.Min(max,b);return min<=max;
        }
        return Slab(origin.X,direction.X,bounds.Min.X,bounds.Max.X,ref minimum,ref maximum) &&
            Slab(origin.Y,direction.Y,bounds.Min.Y,bounds.Max.Y,ref minimum,ref maximum) &&
            Slab(origin.Z,direction.Z,bounds.Min.Z,bounds.Max.Z,ref minimum,ref maximum);
    }
}
