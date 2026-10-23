using System.Collections.Immutable;

namespace Builder.Core;

// Physical geometry is always double precision, in millimeters. No viewport types cross this boundary.
public readonly record struct Vec3(double X, double Y, double Z)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y*b.Z-a.Z*b.Y, a.Z*b.X-a.X*b.Z, a.X*b.Y-a.Y*b.X);
}

public readonly record struct Bounds3(Vec3 Min, Vec3 Max)
{
    public Vec3 Size => Max - Min;
    public Vec3 Center => (Min + Max) * 0.5;
    public static Bounds3 From(IEnumerable<Vec3> points)
    {
        var min = new Vec3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new Vec3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        var any = false;
        foreach (var p in points)
        {
            if (!p.IsFinite || Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) > 10_000_000)
                throw new ModelException("Coordinates must be finite and within 10,000,000 mm of the origin.");
            min = new(Math.Min(min.X,p.X), Math.Min(min.Y,p.Y), Math.Min(min.Z,p.Z));
            max = new(Math.Max(max.X,p.X), Math.Max(max.Y,p.Y), Math.Max(max.Z,p.Z));
            any = true;
        }
        return any ? new(min,max) : new(new(),new());
    }
}

// Basis vectors followed by translation: the same row-vector convention as 3MF.
public readonly record struct Affine(Vec3 X, Vec3 Y, Vec3 Z, Vec3 Offset)
{
    public static Affine Identity => new(new(1,0,0),new(0,1,0),new(0,0,1),new());
    public Vec3 Direction(Vec3 p) => X*p.X + Y*p.Y + Z*p.Z;
    public Vec3 Apply(Vec3 p) => Direction(p) + Offset;
    public Affine Then(Affine next) => new(next.Direction(X),next.Direction(Y),next.Direction(Z),next.Apply(Offset));
    public static Affine Translation(Vec3 delta) => Identity with { Offset = delta };
    public static Affine ScaleAbout(Vec3 factors, Vec3 center) => new(
        new(factors.X,0,0), new(0,factors.Y,0), new(0,0,factors.Z),
        new(center.X*(1-factors.X),center.Y*(1-factors.Y),center.Z*(1-factors.Z)));

    /// <summary>Relative right-handed world X→Y→Z rotation. Reduce degrees before converting to radians.</summary>
    public static Affine RotateAbout(Vec3 degrees, Vec3 center)
    {
        if (!degrees.IsFinite || !center.IsFinite) throw new ModelException("Enter a finite rotation for each axis.");
        static (double Sin,double Cos) Trig(double value)
        {
            var d = value % 360;
            // Exact cardinal values avoid drift on quarter/full turns and make full turns a true no-op.
            return d switch {
                0 => (0,1), 90 or -270 => (1,0), -90 or 270 => (-1,0), 180 or -180 => (0,-1),
                _ => Math.SinCos(d * (Math.PI/180)) };
        }
        var (sx,cx) = Trig(degrees.X); var (sy,cy) = Trig(degrees.Y); var (sz,cz) = Trig(degrees.Z);
        var x = new Affine(new(1,0,0),new(0,cx,sx),new(0,-sx,cx),new());
        var y = new Affine(new(cy,0,-sy),new(0,1,0),new(sy,0,cy),new());
        var z = new Affine(new(cz,sz,0),new(-sz,cz,0),new(0,0,1),new());
        var rotation = x.Then(y).Then(z);
        return rotation with { Offset = center - rotation.Direction(center) };
    }
}

public readonly record struct Triangle(int A, int B, int C);
public sealed class Mesh
{
    public ImmutableArray<Vec3> Vertices { get; }
    public ImmutableArray<Triangle> Triangles { get; }
    public Mesh(IEnumerable<Vec3> vertices, IEnumerable<Triangle> triangles)
    {
        Vertices = vertices.ToImmutableArray();
        Triangles = triangles.ToImmutableArray();
        if (Vertices.Length == 0 || Vertices.Length > ModelLimits.MaxVertices ||
            Triangles.Length == 0 || Triangles.Length > ModelLimits.MaxTriangles)
            throw new ModelException("This prototype supports 1–100,000 triangles and up to 300,000 vertices per scene.");
        _ = Bounds3.From(Vertices);
        if (Triangles.Any(t => t.A < 0 || t.B < 0 || t.C < 0 ||
            t.A >= Vertices.Length || t.B >= Vertices.Length || t.C >= Vertices.Length))
            throw new ModelException("A triangle refers to a vertex that does not exist.");
    }
    public static Mesh Cube => new(
        [new(0,0,0),new(20,0,0),new(20,20,0),new(0,20,0),new(0,0,20),new(20,0,20),new(20,20,20),new(0,20,20)],
        [new(0,2,1),new(0,3,2),new(4,5,6),new(4,6,7),new(0,1,5),new(0,5,4),new(1,2,6),new(1,6,5),new(2,3,7),new(2,7,6),new(3,0,4),new(3,4,7)]);
}

public sealed record SceneObject(Guid Id, string Name, Mesh Mesh, Affine Transform)
{
    /// <summary>Opaque display material, or null for the unassigned neutral appearance.</summary>
    public SurfaceMaterial? Material { get; init; }
    /// <summary>Sparse overrides keyed by original mesh triangle index; absence inherits Material.</summary>
    public ImmutableDictionary<int,SurfaceMaterial> TriangleMaterials { get; init; }=ImmutableDictionary<int,SurfaceMaterial>.Empty;
    public SurfaceMaterial? GetTriangleMaterial(int triangleIndex)
    {
        if(triangleIndex<0||triangleIndex>=Mesh.Triangles.Length)throw new ModelException("Triangle index is outside this object.");
        return TriangleMaterials.TryGetValue(triangleIndex,out var material)?material:Material;
    }
    public Bounds3 Bounds => GeometryCache.Bounds(this);
}
public sealed partial record Scene(Guid ContentId, ImmutableArray<SceneObject> Objects)
{
    public ModelMetadata Metadata { get; init; }=ModelMetadata.Empty;
    public static Scene Empty => new(Guid.NewGuid(), []);
    public Bounds3 Bounds => GeometryCache.Bounds(this);
    public int TriangleCount => Objects.Sum(o => o.Mesh.Triangles.Length);
    public void Validate()=>Validate(default);
    /// <summary>Validates immutable scene invariants and exact world coordinates; cancellation never caches a partial result.</summary>
    public void Validate(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(GeometryCache.IsValidated(this)) return;
        if(ModelMetadata.Canonical(Metadata)!=Metadata)throw new ModelException("Model information must use normalized line-feed newlines.");
        if (Objects.IsDefault || Objects.Length > ModelLimits.MaxObjects || TriangleCount > ModelLimits.MaxTriangles ||
            Objects.Sum(o => (long)o.Mesh.Vertices.Length) > ModelLimits.MaxVertices)
            throw new ModelException("This prototype supports up to 1,000 objects, 100,000 triangles and 300,000 vertices per scene.");
        ValidateGroups();
        var materials=new HashSet<SurfaceMaterial>();
        foreach(var obj in Objects)
        {
            cancellation.ThrowIfCancellationRequested();
            if(SurfaceMaterial.Canonical(obj.Material)!=obj.Material)
                throw new ModelException("Material names must be trimmed.");
            if(obj.Material is {} material)materials.Add(material);
            if(obj.TriangleMaterials is null || obj.TriangleMaterials.Count>obj.Mesh.Triangles.Length || obj.TriangleMaterials.Count>0&&obj.Material is null)
                throw new ModelException("Surface materials need valid triangle indices and an explicit object fallback material.");
            foreach(var (index,value) in obj.TriangleMaterials)
            {
                cancellation.ThrowIfCancellationRequested();
                if(index<0||index>=obj.Mesh.Triangles.Length||value is null||SurfaceMaterial.Canonical(value)!=value)
                    throw new ModelException("Surface material indices and names must be valid.");
                materials.Add(value);
            }
            if(materials.Count>ModelLimits.MaxMaterials)throw new ModelException("A scene supports up to 1,000 distinct materials.");
        }
        _ = GeometryCache.Bounds(this,cancellation);
        cancellation.ThrowIfCancellationRequested();
        GeometryCache.MarkValidated(this);
    }
}
public static class ModelLimits
{
    public const int MaxTriangles = 100_000, MaxVertices = 300_000, MaxObjects = 1_000;
    /// <summary>Maximum combined entries across supported 3MF base-material resources.</summary>
    public const int MaxMaterials = 1_000;
    public const long MaxFileBytes = 64 * 1024 * 1024;
}
public sealed class ModelException(string message) : Exception(message);
