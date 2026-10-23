using System.Windows.Media.Media3D;

namespace Builder.Desktop;

/// <summary>Display-only area-weighted normals, within one geometry and exact coincident positions.
/// Positions, triangle order and corner indices are shared unchanged; no Core mesh is modified.</summary>
internal static class DisplayNormals
{
    internal static MeshGeometry3D Smooth(MeshGeometry3D flat)
    {
        var sums=new Dictionary<Point3D,Vector3D>();var fallback=new Vector3D[flat.Positions.Count];
        for(var i=0;i+2<flat.TriangleIndices.Count;i+=3)
        {
            var a=flat.TriangleIndices[i];var b=flat.TriangleIndices[i+1];var c=flat.TriangleIndices[i+2];
            var normal=Vector3D.CrossProduct(flat.Positions[b]-flat.Positions[a],flat.Positions[c]-flat.Positions[a]);
            if(!Finite(normal))normal=new();
            foreach(var corner in new[]{a,b,c})
            {
                var position=flat.Positions[corner];sums[position]=sums.GetValueOrDefault(position)+normal;
                if(Finite(normal)&&normal.LengthSquared>0)fallback[corner]=normal;
            }
        }
        var normals=new Vector3DCollection(flat.Positions.Count);
        for(var i=0;i<flat.Positions.Count;i++)
        {
            var normal=sums.GetValueOrDefault(flat.Positions[i]);
            if(!Finite(normal)||normal.LengthSquared==0)normal=fallback[i];
            if(!Finite(normal)||normal.LengthSquared==0)normal=i<flat.Normals.Count?flat.Normals[i]:new();
            if(!Finite(normal)||normal.LengthSquared==0)normal=new(0,0,1);
            normal.Normalize();normals.Add(normal);
        }
        var result=new MeshGeometry3D{Positions=flat.Positions,TriangleIndices=flat.TriangleIndices,Normals=normals,TextureCoordinates=flat.TextureCoordinates};result.Freeze();return result;
    }
    private static bool Finite(Vector3D value)=>double.IsFinite(value.X)&&double.IsFinite(value.Y)&&double.IsFinite(value.Z)&&double.IsFinite(value.LengthSquared);
}
