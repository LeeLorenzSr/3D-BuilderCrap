namespace Builder.Core;

public enum MirrorAxis { X, Y, Z }

/// <summary>Reflects accepted targets in a world-axis plane. Null position resolves to that axis of
/// the combined accepted target bounds center. An explicit position is millimeters within ±10,000,000.
/// Targets follow TransformRequest group/member rules. Result vertices are baked to world coordinates
/// with identity transform; face winding compensates for the composed determinant, preserving the
/// source mesh's authored orientation (this operation does not repair an inward or invalid source).</summary>
public sealed record MirrorTransform(Guid ObjectId, MirrorAxis Axis, double? PlanePositionMillimeters = null) : TransformRequest(ObjectId);

public sealed partial class EditorSession
{
    private static (Affine Change,MirrorTransform Effective,string Summary) MirrorChange(MirrorTransform request,Bounds3 bounds)
    {
        if(!Enum.IsDefined(request.Axis)) throw new ModelException("Choose world X, Y or Z as the mirror axis.");
        var plane=request.PlanePositionMillimeters ?? request.Axis switch {MirrorAxis.X=>bounds.Center.X,MirrorAxis.Y=>bounds.Center.Y,_=>bounds.Center.Z};
        if(!double.IsFinite(plane) || Math.Abs(plane)>10_000_000)
            throw new ModelException("Enter a finite mirror plane position within 10,000,000 mm of the origin.");
        var center=request.Axis switch {MirrorAxis.X=>new Vec3(plane,0,0),MirrorAxis.Y=>new Vec3(0,plane,0),_=>new Vec3(0,0,plane)};
        var factors=request.Axis switch {MirrorAxis.X=>new Vec3(-1,1,1),MirrorAxis.Y=>new Vec3(1,-1,1),_=>new Vec3(1,1,-1)};
        return (Affine.ScaleAbout(factors,center),request with {PlanePositionMillimeters=plane},
            FormattableString.Invariant($"Mirror preview: world {request.Axis} plane at {plane:G6} mm. Apply reflects the selected objects; Undo restores them."));
    }

    private static SceneObject BakeMirror(SceneObject original,Affine reflection,CancellationToken cancellation,
        IProgress<OperationProgress>? progress,int completed,int total)
    {
        var composed=original.Transform.Then(reflection);
        // Normalize each basis independently before the triple product. Only its sign matters; this
        // avoids overflow/underflow from otherwise usable nonuniform scales and imported reflections.
        static Vec3 Basis(Vec3 v)
        {
            var largest=Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)));
            if(!v.IsFinite || largest==0) throw new ModelException("A singular transform cannot preserve mirrored face orientation.");
            return new(v.X/largest,v.Y/largest,v.Z/largest);
        }
        var x=Basis(composed.X);var y=Basis(composed.Y);var z=Basis(composed.Z);var cross=Vec3.Cross(x,y);
        var determinant=cross.X*z.X+cross.Y*z.Y+cross.Z*z.Z;
        if(!double.IsFinite(determinant) || determinant==0) throw new ModelException("A singular transform cannot preserve mirrored face orientation.");
        var vertices=new Vec3[original.Mesh.Vertices.Length];
        var sourceWorld=new Vec3[vertices.Length];
        for(var i=0;i<vertices.Length;i++)
        {
            if(i%1024==0) {progress?.Report(new("Mirroring vertices",completed+i,total));cancellation.ThrowIfCancellationRequested();}
            sourceWorld[i]=original.Transform.Apply(original.Mesh.Vertices[i]);
            var point=reflection.Apply(sourceWorld[i]);
            if(!point.IsFinite || Math.Max(Math.Abs(point.X),Math.Max(Math.Abs(point.Y),Math.Abs(point.Z)))>10_000_000)
                throw new ModelException("Mirrored coordinates must be finite and within 10,000,000 mm of the origin.");
            vertices[i]=point;
        }
        progress?.Report(new("Mirroring vertices",completed+vertices.Length,total));cancellation.ThrowIfCancellationRequested();
        var triangles=new Triangle[original.Mesh.Triangles.Length];
        for(var i=0;i<triangles.Length;i++)
        {
            if(i%1024==0) {progress?.Report(new("Orienting mirrored faces",i,triangles.Length));cancellation.ThrowIfCancellationRequested();}
            var t=original.Mesh.Triangles[i];
            if(HasArea(sourceWorld[t.A],sourceWorld[t.B],sourceWorld[t.C]) && !HasArea(vertices[t.A],vertices[t.B],vertices[t.C]))
                throw new ModelException("This mirror plane is too far away to preserve the object's face geometry at double precision.");
            triangles[i]=determinant<0 ? new(t.A,t.C,t.B) : t;
        }
        progress?.Report(new("Orienting mirrored faces",triangles.Length,triangles.Length));cancellation.ThrowIfCancellationRequested();
        var mesh=new Mesh(vertices,triangles);cancellation.ThrowIfCancellationRequested();
        return original with {Mesh=mesh,Transform=Affine.Identity};
    }

    private static bool HasArea(Vec3 a,Vec3 b,Vec3 c)
    {
        var u=b-a;var v=c-a;
        var scale=Math.Max(Math.Max(Math.Abs(u.X),Math.Abs(u.Y)),Math.Max(Math.Abs(u.Z),Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)))));
        return scale!=0 && Vec3.Cross(new(u.X/scale,u.Y/scale,u.Z/scale),new(v.X/scale,v.Y/scale,v.Z/scale))!=new Vec3();
    }
}
