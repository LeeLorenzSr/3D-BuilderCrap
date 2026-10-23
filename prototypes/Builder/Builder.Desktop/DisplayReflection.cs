using Builder.Core;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

internal readonly record struct ReflectionSurface(MeshGeometry3D Geometry,Affine Transform,Material Material);

/// <summary>Optional display copy below the build plane. World clipping and reflected normals
/// never alter the source geometry, its material assignment or the Core picking scene.</summary>
internal static class DisplayReflection
{
    internal const double OffsetMillimeters=.06;
    private readonly record struct Corner(Point3D Position,Vector3D Normal);

    internal static GeometryModel3D? Build(ReflectionSurface surface)
    {
        var source=surface.Geometry;var transform=surface.Transform;
        var x=new Vector3D(transform.X.X,transform.X.Y,transform.X.Z);var y=new Vector3D(transform.Y.X,transform.Y.Y,transform.Y.Z);var z=new Vector3D(transform.Z.X,transform.Z.Y,transform.Z.Z);
        var cx=Vector3D.CrossProduct(y,z);var cy=Vector3D.CrossProduct(z,x);var cz=Vector3D.CrossProduct(x,y);var determinant=Vector3D.DotProduct(x,cx);
        var points=new Point3DCollection();var normals=new Vector3DCollection();var indices=new Int32Collection();
        for(var face=0;face+2<source.TriangleIndices.Count;face+=3)
        {
            var input=new Corner[3];
            for(var i=0;i<3;i++)
            {
                var index=source.TriangleIndices[face+i];var p=source.Positions[index];var world=transform.Apply(new(p.X,p.Y,p.Z));
                var n=index<source.Normals.Count?source.Normals[index]:new Vector3D();
                // Inverse transpose handles nonuniform/sheared source transforms; reflecting Z
                // transforms the resulting world normal. Singular cases use the surviving face.
                var normal=determinant!=0?(cx*n.X+cy*n.Y+cz*n.Z)/determinant:new Vector3D();
                input[i]=new(new(world.X,world.Y,world.Z),Unit(normal));
            }
            var clipped=AbovePlate(input);
            for(var i=1;i+1<clipped.Count;i++)
            {
                var a=Reflect(clipped[0]);var b=Reflect(clipped[i]);var c=Reflect(clipped[i+1]);
                // Z reflection changes handedness. An imported negative source affine changes
                // it back, so only a positive source determinant needs this additional reversal.
                if(determinant>0)(b,c)=(c,b);
                var fallback=Unit(Vector3D.CrossProduct(b.Position-a.Position,c.Position-a.Position));
                if(fallback.LengthSquared==0)continue;
                foreach(var corner in new[]{a,b,c})
                {
                    indices.Add(points.Count);points.Add(corner.Position);
                    var normal=Unit(corner.Normal);normals.Add(normal.LengthSquared>0?normal:fallback);
                }
            }
        }
        if(indices.Count==0)return null;
        var geometry=new MeshGeometry3D{Positions=points,Normals=normals,TriangleIndices=indices};geometry.Freeze();
        // Existing displayed batches already hold the effective role/base/face color. Preserve
        // that RGB exactly; a fixed opacity makes this optional approximation subordinate.
        var color=((SolidColorBrush)((DiffuseMaterial)surface.Material).Brush).Color;
        color.A=(byte)Math.Round(color.A*.22);
        var brush=new SolidColorBrush(color);brush.Freeze();var material=new DiffuseMaterial(brush);material.Freeze();
        var model=new GeometryModel3D(geometry,material){BackMaterial=material};model.Freeze();return model;
    }
    private static Corner Reflect(Corner corner)=>new(new(corner.Position.X,corner.Position.Y,-corner.Position.Z-OffsetMillimeters),new(corner.Normal.X,corner.Normal.Y,-corner.Normal.Z));
    private static Vector3D Unit(Vector3D value)
    {
        if(!double.IsFinite(value.X)||!double.IsFinite(value.Y)||!double.IsFinite(value.Z)||!double.IsFinite(value.LengthSquared)||value.LengthSquared==0)return new();
        value.Normalize();return value;
    }
    private static List<Corner> AbovePlate(Corner[] input)
    {
        var result=new List<Corner>(4);var previous=input[^1];
        void Add(Corner corner){if(result.Count==0||result[^1].Position!=corner.Position)result.Add(corner);}
        foreach(var current in input)
        {
            if((previous.Position.Z>=0)!=(current.Position.Z>=0))
            {
                var t=previous.Position.Z/(previous.Position.Z-current.Position.Z);var crossing=previous.Position+(current.Position-previous.Position)*t;
                Add(new(new(crossing.X,crossing.Y,0),previous.Normal+(current.Normal-previous.Normal)*t));
            }
            if(current.Position.Z>=0)Add(current);
            previous=current;
        }
        if(result.Count>1&&result[0].Position==result[^1].Position)result.RemoveAt(result.Count-1);
        return result;
    }
}
