namespace Builder.Core;

/// <summary>Right-handed world X→Y→Z orientation of an affine placement with orthogonal local axes.</summary>
public sealed class ObjectOrientation
{
    private readonly Affine rotation;
    private ObjectOrientation(Affine rotation) => this.rotation=rotation;

    /// <summary>Returns null for a collapsed, reflected or sheared placement. Positive local scale and translation are independent of orientation.</summary>
    public static ObjectOrientation? From(Affine placement)
    {
        static Vec3 Unit(Vec3 axis)
        {
            var largest=Math.Max(Math.Abs(axis.X),Math.Max(Math.Abs(axis.Y),Math.Abs(axis.Z)));
            if(largest==0 || !double.IsFinite(largest)) return default;
            var v=new Vec3(axis.X/largest,axis.Y/largest,axis.Z/largest);
            return v*(1/Math.Sqrt(Dot(v,v)));
        }
        var x=Unit(placement.X); var y=Unit(placement.Y); var z=Unit(placement.Z);
        const double tolerance=1e-10;
        if(Math.Abs(Dot(x,y))>tolerance || Math.Abs(Dot(y,z))>tolerance || Math.Abs(Dot(z,x))>tolerance ||
            Dot(Vec3.Cross(x,y),z)<1-tolerance) return null;
        return new(new(x,y,z,default));
    }

    public Vec3 Degrees => Angles(rotation);

    /// <summary>Relative world rotation to pass to RotateTransform to obtain the requested absolute angles.</summary>
    public Vec3 RotationTo(Vec3 targetDegrees)
    {
        var target=Affine.RotateAbout(targetDegrees,default);
        var r=rotation;
        // Row-vector placement: current * delta = target. A rotation's inverse is its transpose.
        var inverse=new Affine(new(r.X.X,r.Y.X,r.Z.X),new(r.X.Y,r.Y.Y,r.Z.Y),new(r.X.Z,r.Y.Z,r.Z.Z),default);
        var delta=inverse.Then(target);
        return Angles(delta);
    }

    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static Vec3 Angles(Affine r)
    {
        var horizontal=Math.Sqrt(r.X.X*r.X.X+r.X.Y*r.X.Y);
        var pitch=Math.Atan2(-r.X.Z,horizontal);
        // At a pitch pole only the combined roll/yaw can be recovered. Choose yaw=0 consistently.
        var roll=horizontal>1e-12 ? Math.Atan2(r.Y.Z,r.Z.Z) : Math.Atan2(-r.Z.Y,r.Y.Y);
        var yaw=horizontal>1e-12 ? Math.Atan2(r.X.Y,r.X.X) : 0;
        static double ToDegrees(double radians)=>Math.Abs(radians)<1e-14 ? 0 : radians*(180/Math.PI);
        return new(ToDegrees(roll),ToDegrees(pitch),ToDegrees(yaw));
    }
}
