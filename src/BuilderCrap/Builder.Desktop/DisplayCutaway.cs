using Builder.Core;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

/// <summary>A display half-space in world millimeters. This never enters Core or changes a mesh.</summary>
public readonly record struct DisplayCutPlane(int Axis,double PositionMillimeters,bool KeepPositive);

/// <summary>Open display clipping of actual triangles, without a fabricated section cap or cavity.</summary>
public static class DisplayCutaway
{
    public static MeshGeometry3D Build(Mesh source,Affine transform,DisplayCutPlane plane)
    {
        if(plane.Axis is <0 or >2||!double.IsFinite(plane.PositionMillimeters))throw new ArgumentException("Choose a finite world-axis inspection plane.",nameof(plane));
        var points=new List<Point3D>(source.Triangles.Length*3);var normals=new List<Vector3D>(source.Triangles.Length*3);var indices=new List<int>(source.Triangles.Length*3);
        var input=new Vec3[3];var clipped=new List<Vec3>(4);
        double Distance(Vec3 p)=>(plane.Axis switch{0=>p.X,1=>p.Y,_=>p.Z})-plane.PositionMillimeters;
        bool Inside(double distance)=>plane.KeepPositive?distance>=0:distance<=0;
        Vec3 Crossing(Vec3 a,Vec3 b,double da,double db)
        {
            var value=a+(b-a)*(da/(da-db));
            return plane.Axis switch{0=>value with{X=plane.PositionMillimeters},1=>value with{Y=plane.PositionMillimeters},_=>value with{Z=plane.PositionMillimeters}};
        }
        foreach(var face in source.Triangles)
        {
            input[0]=transform.Apply(source.Vertices[face.A]);input[1]=transform.Apply(source.Vertices[face.B]);input[2]=transform.Apply(source.Vertices[face.C]);clipped.Clear();
            void Add(Vec3 p){if(clipped.Count==0||clipped[^1]!=p)clipped.Add(p);}
            var previous=input[^1];var previousDistance=Distance(previous);
            foreach(var current in input)
            {
                var currentDistance=Distance(current);var a=Inside(previousDistance);var b=Inside(currentDistance);
                if(a!=b)Add(Crossing(previous,current,previousDistance,currentDistance));
                if(b)Add(current);
                previous=current;previousDistance=currentDistance;
            }
            if(clipped.Count>1&&clipped[0]==clipped[^1])clipped.RemoveAt(clipped.Count-1);
            for(var i=1;i+1<clipped.Count;i++)
            {
                var a=clipped[0];var b=clipped[i];var c=clipped[i+1];var n=Vec3.Cross(b-a,c-a);var normal=new Vector3D(n.X,n.Y,n.Z);
                if(normal.LengthSquared==0)continue;normal.Normalize();
                foreach(var p in new[]{a,b,c}){indices.Add(points.Count);points.Add(new(p.X,p.Y,p.Z));normals.Add(normal);}
            }
        }
        var display=new MeshGeometry3D{Positions=new(points),Normals=new(normals),TriangleIndices=new(indices)};display.Freeze();return display;
    }
}
