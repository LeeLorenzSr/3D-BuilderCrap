namespace Builder.Core;

/// <summary>
/// Cube is exact. Cylinder is a 48-sided elliptical prism; Sphere is a faceted ellipsoid with
/// 48 longitudes and 24 latitude intervals. Curved surfaces are inscribed approximations.
/// Size means full world-axis extents in millimeters, not radius; every primitive starts on z=0.
/// </summary>
public enum PrimitiveKind { Cube = 0, Cylinder = 1, Sphere = 2, Pyramid = 3, Cone = 4, Hexagon = 5, Wedge = 6, Torus = 7, Tetrahedron = 8 }

internal static class Primitives
{
    private const int Segments = 48, LatitudeIntervals = 24;

    public static Mesh Create(PrimitiveKind kind, Vec3 size)
    {
        // Preserve the original three generators and their defaults exactly. Torus requires explicit
        // centerline/tube radii; a size vector must never silently choose a hole or wall thickness.
        if(kind == PrimitiveKind.Torus) throw new ModelException("Create a torus with explicit major and tube radii using TorusPrimitiveRequest.");
        if(kind is PrimitiveKind.Pyramid or PrimitiveKind.Hexagon or PrimitiveKind.Wedge or PrimitiveKind.Tetrahedron)
            return PrimitiveMeshes.Create(new PolyhedronPrimitiveRequest(kind,size),default,null);
        if(kind == PrimitiveKind.Cone) return PrimitiveMeshes.Create(new RadialPrimitiveRequest(kind,size),default,null);
        if (!Enum.IsDefined(kind)) throw new ModelException("Choose a supported primitive.");
        if (!size.IsFinite || size.X <= 0 || size.Y <= 0 || size.Z <= 0)
            throw new ModelException("Enter a positive, finite size for each axis.");
        if (Math.Max(size.X,Math.Max(size.Y,size.Z)) > 10_000_000)
            throw new ModelException("Primitive dimensions must be within 10,000,000 mm.");
        if (kind == PrimitiveKind.Cube)
        {
            var cube = Mesh.Cube;
            return new(cube.Vertices.Select(v => new Vec3(v.X/20*size.X,v.Y/20*size.Y,v.Z/20*size.Z)),cube.Triangles);
        }
        var vertices = new List<Vec3>(); var triangles = new List<Triangle>();
        void Ring(double radius,double z)
        {
            for(var i=0;i<Segments;i++)
            {
                var (s,c) = i switch {
                    0 => (0d,1d), Segments/4 => (1d,0d), Segments/2 => (0d,-1d),
                    3*Segments/4 => (-1d,0d), _ => Math.SinCos(i*2*Math.PI/Segments) };
                vertices.Add(new(size.X*((1+radius*c)*0.5),size.Y*((1+radius*s)*0.5),z));
            }
        }
        if (kind == PrimitiveKind.Cylinder)
        {
            Ring(1,0); Ring(1,size.Z);
            var bottom = vertices.Count; vertices.Add(new(size.X*0.5,size.Y*0.5,0));
            var top = vertices.Count; vertices.Add(new(size.X*0.5,size.Y*0.5,size.Z));
            for(var i=0;i<Segments;i++)
            {
                var j=(i+1)%Segments;
                triangles.Add(new(i,j,j+Segments)); triangles.Add(new(i,j+Segments,i+Segments));
                triangles.Add(new(bottom,j,i)); triangles.Add(new(top,i+Segments,j+Segments));
            }
        }
        else
        {
            var north = vertices.Count; vertices.Add(new(size.X*0.5,size.Y*0.5,size.Z));
            for(var latitude=1;latitude<LatitudeIntervals;latitude++)
            {
                var (s,c) = latitude == LatitudeIntervals/2 ? (1d,0d) : Math.SinCos(latitude*Math.PI/LatitudeIntervals);
                Ring(s,size.Z*((1+c)*0.5));
            }
            var south = vertices.Count; vertices.Add(new(size.X*0.5,size.Y*0.5,0));
            for(var i=0;i<Segments;i++)
            {
                var j=(i+1)%Segments;
                triangles.Add(new(north,1+i,1+j));
                for(var latitude=0;latitude<LatitudeIntervals-2;latitude++)
                {
                    var a=1+latitude*Segments+i; var b=1+latitude*Segments+j;
                    var c=b+Segments; var d=a+Segments;
                    triangles.Add(new(a,d,c)); triangles.Add(new(a,c,b));
                }
                var last=1+(LatitudeIntervals-2)*Segments;
                triangles.Add(new(last+i,south,last+j));
            }
        }
        // Extremely small dimensions may round distinct sample points together. Reject that candidate
        // instead of admitting a closed index graph whose geometric triangles have collapsed.
        foreach(var triangle in triangles)
        {
            var a=vertices[triangle.A]; var u=vertices[triangle.B]-a; var v=vertices[triangle.C]-a;
            var scale=Math.Max(Math.Max(Math.Abs(u.X),Math.Abs(u.Y)),Math.Max(Math.Abs(u.Z),
                Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)))));
            if (scale == 0 || Vec3.Cross(new(u.X/scale,u.Y/scale,u.Z/scale),new(v.X/scale,v.Y/scale,v.Z/scale)) == new Vec3())
                throw new ModelException("These dimensions are too small or disproportionate for this primitive's tessellation.");
        }
        return new(vertices,triangles);
    }
}
