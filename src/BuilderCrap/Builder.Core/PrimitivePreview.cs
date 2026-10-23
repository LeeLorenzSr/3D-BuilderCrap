using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>A scene-scope creation draft. Accepted geometry is unchanged until ApplyPreview.</summary>
public abstract record PrimitiveRequest : GeometryRequest
{
    public abstract PrimitiveKind Kind { get; }
}

/// <summary>Exact full XYZ extents, in millimeters. Cube, Pyramid, Hexagon, Wedge or Tetrahedron only.
/// Pyramid has an XY base and centered apex; wedge rises along +X. Hexagon is a Z prism with X tips
/// and Y flats. Tetrahedron has base (0,0),(X,0),(X/2,Y) and apex (X/2,Y/3,Z).
/// These are affine-scaled canonical solids, not necessarily regular polyhedra.</summary>
public sealed record PolyhedronPrimitiveRequest(PrimitiveKind Kind, Vec3 SizeMillimeters) : PrimitiveRequest
{
    public override PrimitiveKind Kind { get; } = Kind;
}

/// <summary>Cylinder or Cone only. Size is exact XYZ bounds, base on XY, apex/top along +Z.
/// Curved faces are inscribed n-gon approximations. RadialSegments is 4..256, a multiple of four.</summary>
public sealed record RadialPrimitiveRequest(PrimitiveKind Kind, Vec3 SizeMillimeters, int RadialSegments = 48) : PrimitiveRequest
{
    public override PrimitiveKind Kind { get; } = Kind;
}

/// <summary>Inscribed ellipsoid, exact full XYZ bounds. RadialSegments is 4..256/multiple of four;
/// LatitudeSegments is 4..128/even, including polar intervals. Defaults preserve the original sphere.</summary>
public sealed record SpherePrimitiveRequest(Vec3 SizeMillimeters, int RadialSegments = 48, int LatitudeSegments = 24) : PrimitiveRequest
{
    public override PrimitiveKind Kind => PrimitiveKind.Sphere;
}

/// <summary>Z-axis torus. Major radius is centerline radius R, tube radius is r, with R greater than r.
/// Bounds are (0,0,0)..(2(R+r),2(R+r),2r). Both segment counts are 4..256/multiple of four;
/// faces are inscribed approximations. Combined scene budgets may reject otherwise valid counts.</summary>
public sealed record TorusPrimitiveRequest(double MajorRadiusMillimeters, double TubeRadiusMillimeters,
    int RadialSegments = 48, int TubeSegments = 24) : PrimitiveRequest
{
    public override PrimitiveKind Kind => PrimitiveKind.Torus;
}

public sealed partial class EditorSession
{
    /// <summary>Appends one private primitive candidate at minimum bounds (0,0,0), on the bed.
    /// Replacements require the exact current primitive-preview token and retain its new object ID;
    /// every publication receives a fresh preview token. Changing shape is allowed. Apply accepts one
    /// history step; cancellation/failure/stale requests preserve accepted state and the previous preview.
    /// Creation is blocked inside member scope. Legacy AddCube/AddPrimitive remain immediate edits.</summary>
    public Task<OperationResult> PreviewPrimitiveAsync(PrimitiveRequest request, Guid? replacePreviewId = null,
        CancellationToken cancellation = default, IProgress<OperationProgress>? progress = null, long? expectedRevision = null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked) return Task.FromResult(blocked);
            if(request is null) return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose a primitive."));
            if(GuardMemberScope(request.MemberGroupId) is {} scopeConflict) return Task.FromResult(scopeConflict);
            if(editingGroupId.HasValue) return Task.FromResult(DoneWithGroup());
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId || current.Request is not PrimitiveRequest || current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This primitive preview is no longer current."));
            }
            else if(replacePreviewId.HasValue)
                return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no primitive preview to replace."));
            var captured=Snapshot;
            var objectId=preview?.ResultObjectIds.Single() ?? Guid.NewGuid();
            return RunIo(()=> {
                cancellation.ThrowIfCancellationRequested();
                progress?.Report(new("Preparing primitive")); cancellation.ThrowIfCancellationRequested();
                var counts=PrimitiveMeshes.Counts(request);
                if(captured.Scene.Objects.Length+1>ModelLimits.MaxObjects ||
                    captured.Scene.TriangleCount+counts.Triangles>ModelLimits.MaxTriangles ||
                    captured.Scene.Objects.Sum(o=>(long)o.Mesh.Vertices.Length)+counts.Vertices>ModelLimits.MaxVertices)
                    throw new ModelException("This primitive would exceed the scene object, triangle or vertex limit.");
                var mesh=PrimitiveMeshes.Create(request,cancellation,progress);
                var obj=new SceneObject(objectId,request.Kind.ToString(),mesh,Affine.Identity);
                var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Add(obj));
                progress?.Report(new("Validating primitive")); cancellation.ThrowIfCancellationRequested();
                candidate.Validate(cancellation);
                var size=obj.Bounds.Size;
                var summary=FormattableString.Invariant($"{request.Kind} preview: {size.X:G6} × {size.Y:G6} × {size.Z:G6} mm; {mesh.Triangles.Length:N0} triangles. Add accepts one new object at (0,0,0).");
                progress?.Report(new("Ready to preview primitive")); cancellation.ThrowIfCancellationRequested();
                lock(gate)
                {
                    cancellation.ThrowIfCancellationRequested();
                    preview=new(Guid.NewGuid(),captured.Revision,request,candidate,objectId,[objectId],summary)
                        {SelectedObjectIds=[objectId],EditingGroupId=null};
                }
                return summary;
            },expectedRevision,allowPreview:true);
        }
    }
}

internal static class PrimitiveMeshes
{
    internal readonly record struct MeshCounts(int Vertices,int Triangles);
    private static void Radial(int count)
    {
        if(count<4 || count>256 || count%4!=0) throw new ModelException("Enter a segment count from 4 to 256, in multiples of four.");
    }
    private static void Size(Vec3 size)
    {
        if(!size.IsFinite || size.X<=0 || size.Y<=0 || size.Z<=0 || Math.Max(size.X,Math.Max(size.Y,size.Z))>10_000_000)
            throw new ModelException("Enter positive, finite dimensions within 10,000,000 mm.");
    }
    public static MeshCounts Counts(PrimitiveRequest request)
    {
        MeshCounts counts;
        switch(request)
        {
            case PolyhedronPrimitiveRequest p:
                Size(p.SizeMillimeters);
                counts=p.Kind switch {
                    PrimitiveKind.Cube=>new(8,12), PrimitiveKind.Pyramid=>new(5,6),
                    PrimitiveKind.Hexagon=>new(14,24), PrimitiveKind.Wedge=>new(6,8),
                    PrimitiveKind.Tetrahedron=>new(4,4),
                    _=>throw new ModelException("Choose Cube, Pyramid, Hexagon, Wedge or Tetrahedron for this request.")};
                break;
            case RadialPrimitiveRequest p:
                Size(p.SizeMillimeters); Radial(p.RadialSegments);
                counts=p.Kind switch {PrimitiveKind.Cylinder=>new(2*p.RadialSegments+2,4*p.RadialSegments),
                    PrimitiveKind.Cone=>new(p.RadialSegments+2,2*p.RadialSegments),
                    _=>throw new ModelException("Choose Cylinder or Cone for this request.")};
                break;
            case SpherePrimitiveRequest p:
                Size(p.SizeMillimeters); Radial(p.RadialSegments);
                if(p.LatitudeSegments<4 || p.LatitudeSegments>128 || p.LatitudeSegments%2!=0)
                    throw new ModelException("Enter an even latitude interval count from 4 to 128.");
                counts=new(2+p.RadialSegments*(p.LatitudeSegments-1),2*p.RadialSegments*(p.LatitudeSegments-1));
                break;
            case TorusPrimitiveRequest p:
                if(!double.IsFinite(p.MajorRadiusMillimeters) || !double.IsFinite(p.TubeRadiusMillimeters) ||
                    p.TubeRadiusMillimeters<=0 || p.MajorRadiusMillimeters<=p.TubeRadiusMillimeters)
                    throw new ModelException("Torus major radius must be greater than a positive, finite tube radius.");
                Size(new(2*(p.MajorRadiusMillimeters+p.TubeRadiusMillimeters),2*(p.MajorRadiusMillimeters+p.TubeRadiusMillimeters),2*p.TubeRadiusMillimeters));
                Radial(p.RadialSegments); Radial(p.TubeSegments);
                counts=new(p.RadialSegments*p.TubeSegments,2*p.RadialSegments*p.TubeSegments);
                break;
            default: throw new ModelException("Choose a supported primitive request.");
        }
        if(counts.Vertices>ModelLimits.MaxVertices || counts.Triangles>ModelLimits.MaxTriangles)
            throw new ModelException("This primitive's tessellation exceeds the mesh triangle or vertex limit.");
        return counts;
    }

    private static (double Sin,double Cos) Circle(int index,int count)
        => index==0 ? (0,1) : index==count/4 ? (1,0) : index==count/2 ? (0,-1)
            : index==3*count/4 ? (-1,0) : Math.SinCos(index*2*Math.PI/count);

    public static Mesh Create(PrimitiveRequest request,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var counts=Counts(request);
        void Check(int completed=0)
        { progress?.Report(new("Generating primitive",completed,counts.Triangles)); ct.ThrowIfCancellationRequested(); }
        Check();
        // Original defaults are compatibility contracts, including vertex/triangle ordering.
        Mesh? legacy=request switch {
            PolyhedronPrimitiveRequest {Kind:PrimitiveKind.Cube} p=>Primitives.Create(p.Kind,p.SizeMillimeters),
            RadialPrimitiveRequest {Kind:PrimitiveKind.Cylinder,RadialSegments:48} p=>Primitives.Create(p.Kind,p.SizeMillimeters),
            SpherePrimitiveRequest {RadialSegments:48,LatitudeSegments:24} p=>Primitives.Create(PrimitiveKind.Sphere,p.SizeMillimeters),
            _=>null};
        if(legacy!=null)
        {
            for(var i=0;i<legacy.Triangles.Length;i++)
            {
                if(i%256==0) Check(i);
                var t=legacy.Triangles[i]; ValidateFace(legacy.Vertices[t.A],legacy.Vertices[t.B],legacy.Vertices[t.C]);
            }
            Check(counts.Triangles); return legacy;
        }
        var v=new List<Vec3>(counts.Vertices); var f=new List<Triangle>(counts.Triangles);
        void Face(int a,int b,int c) { f.Add(new(a,b,c)); if(f.Count%256==0) Check(f.Count); }
        void Ring(Vec3 size,int n,double radius,double z)
        {
            for(var i=0;i<n;i++) { var(s,c)=Circle(i,n); v.Add(new(size.X*(1+radius*c)*.5,size.Y*(1+radius*s)*.5,z)); }
            ct.ThrowIfCancellationRequested();
        }
        switch(request)
        {
            case PolyhedronPrimitiveRequest p:
                var s=p.SizeMillimeters;
                switch(p.Kind)
                {
                    case PrimitiveKind.Pyramid:
                        v.AddRange([new(0,0,0),new(s.X,0,0),new(s.X,s.Y,0),new(0,s.Y,0),new(s.X/2,s.Y/2,s.Z)]);
                        f.AddRange([new(0,2,1),new(0,3,2),new(0,1,4),new(1,2,4),new(2,3,4),new(3,0,4)]); break;
                    case PrimitiveKind.Wedge:
                        v.AddRange([new(0,0,0),new(s.X,0,0),new(s.X,s.Y,0),new(0,s.Y,0),new(s.X,0,s.Z),new(s.X,s.Y,s.Z)]);
                        f.AddRange([new(0,2,1),new(0,3,2),new(1,2,5),new(1,5,4),new(0,4,5),new(0,5,3),new(0,1,4),new(3,5,2)]); break;
                    case PrimitiveKind.Tetrahedron:
                        v.AddRange([new(0,0,0),new(s.X,0,0),new(s.X/2,s.Y,0),new(s.X/2,s.Y/3,s.Z)]);
                        f.AddRange([new(0,2,1),new(0,1,3),new(1,2,3),new(2,0,3)]); break;
                    case PrimitiveKind.Hexagon:
                        Vec3[] points=[new(s.X,s.Y/2,0),new(3*s.X/4,s.Y,0),new(s.X/4,s.Y,0),new(0,s.Y/2,0),new(s.X/4,0,0),new(3*s.X/4,0,0)];
                        v.AddRange(points); v.AddRange(points.Select(q=>q with {Z=s.Z}));
                        v.Add(new(s.X/2,s.Y/2,0)); v.Add(new(s.X/2,s.Y/2,s.Z));
                        for(var i=0;i<6;i++) {var j=(i+1)%6;Face(i,j,j+6);Face(i,j+6,i+6);Face(12,j,i);Face(13,i+6,j+6);} break;
                }
                break;
            case RadialPrimitiveRequest p:
                var n=p.RadialSegments; var size=p.SizeMillimeters;
                Ring(size,n,1,0);
                if(p.Kind==PrimitiveKind.Cylinder)
                {
                    Ring(size,n,1,size.Z); v.Add(new(size.X/2,size.Y/2,0)); v.Add(new(size.X/2,size.Y/2,size.Z));
                    for(var i=0;i<n;i++) {var j=(i+1)%n;Face(i,j,j+n);Face(i,j+n,i+n);Face(2*n,j,i);Face(2*n+1,i+n,j+n);}
                }
                else
                {
                    v.Add(new(size.X/2,size.Y/2,size.Z)); v.Add(new(size.X/2,size.Y/2,0));
                    for(var i=0;i<n;i++) {var j=(i+1)%n;Face(i,j,n);Face(n+1,j,i);}
                }
                break;
            case SpherePrimitiveRequest p:
                var rn=p.RadialSegments; var lat=p.LatitudeSegments; var ss=p.SizeMillimeters;
                v.Add(new(ss.X/2,ss.Y/2,ss.Z));
                for(var row=1;row<lat;row++)
                { var(sin,cos)=row==lat/2 ? (1d,0d) : Math.SinCos(row*Math.PI/lat); Ring(ss,rn,sin,ss.Z*(1+cos)*.5); Check(f.Count); }
                var south=v.Count; v.Add(new(ss.X/2,ss.Y/2,0));
                for(var i=0;i<rn;i++)
                {
                    var j=(i+1)%rn; Face(0,1+i,1+j);
                    for(var row=0;row<lat-2;row++) {var a=1+row*rn+i;var b=1+row*rn+j;Face(a,a+rn,b+rn);Face(a,b+rn,b);}
                    var last=1+(lat-2)*rn;Face(last+i,south,last+j);
                }
                break;
            case TorusPrimitiveRequest p:
                var radial=p.RadialSegments; var tube=p.TubeSegments;var major=p.MajorRadiusMillimeters;var minor=p.TubeRadiusMillimeters;var outer=major+minor;
                for(var i=0;i<radial;i++)
                {
                    var(sin,cos)=Circle(i,radial);
                    for(var j=0;j<tube;j++) {var(ts,tc)=Circle(j,tube);var radius=major+minor*tc;v.Add(new(outer+radius*cos,outer+radius*sin,minor+minor*ts));}
                    Check(f.Count);
                }
                for(var i=0;i<radial;i++) for(var j=0;j<tube;j++)
                {var a=i*tube+j;var b=((i+1)%radial)*tube+j;var c=((i+1)%radial)*tube+(j+1)%tube;var d=i*tube+(j+1)%tube;Face(a,b,c);Face(a,c,d);}
                break;
        }
        if(v.Count!=counts.Vertices || f.Count!=counts.Triangles) throw new ModelException("Primitive generation produced unexpected counts.");
        for(var i=0;i<f.Count;i++)
        {
            if(i%256==0) { Check(i); }
            var t=f[i]; ValidateFace(v[t.A],v[t.B],v[t.C]);
        }
        Check(f.Count); return new(v,f);
    }
    private static void ValidateFace(Vec3 a,Vec3 b,Vec3 c)
    {
        var u=b-a;var w=c-a;
        var scale=Math.Max(Math.Max(Math.Abs(u.X),Math.Abs(u.Y)),Math.Max(Math.Abs(u.Z),Math.Max(Math.Abs(w.X),Math.Max(Math.Abs(w.Y),Math.Abs(w.Z)))));
        if(scale==0 || Vec3.Cross(new(u.X/scale,u.Y/scale,u.Z/scale),new(w.X/scale,w.Y/scale,w.Z/scale))==new Vec3())
            throw new ModelException("These dimensions or radii collapse primitive faces at this tessellation.");
    }
}
