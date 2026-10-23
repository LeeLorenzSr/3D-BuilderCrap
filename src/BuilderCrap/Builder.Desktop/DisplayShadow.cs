using Builder.Core;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

internal readonly record struct ShadowSurface(MeshGeometry3D Geometry,Affine Transform);

/// <summary>A display-only silhouette on the build plane. One filled path keeps overlapping
/// triangles/objects from darkening repeatedly. No mesh edits, picking or document resources.</summary>
internal static class DisplayShadow
{
    // The same incoming direction as the main viewport light. The receiver sits below the grid
    // (-0.02 mm) and model contact plane (0), so grid lines and resting surfaces remain visible.
    internal static readonly Vector3D LightDirection=new(-.7,.6,-.5);
    private const double ReceiverZ=-.03;

    internal static GeometryModel3D? Build(IEnumerable<ShadowSurface> surfaces)
    {
        var silhouette=new StreamGeometry{FillRule=FillRule.Nonzero};
        using(var drawing=silhouette.Open())
        {
            foreach(var surface in surfaces)
            {
                var mesh=surface.Geometry;
                for(var i=0;i+2<mesh.TriangleIndices.Count;i+=3)
                {
                    var triangle=new Vec3[3];
                    for(var corner=0;corner<3;corner++)
                    {
                        var p=mesh.Positions[mesh.TriangleIndices[i+corner]];
                        triangle[corner]=surface.Transform.Apply(new(p.X,p.Y,p.Z));
                    }
                    var polygon=AbovePlate(triangle);
                    if(polygon.Count<3)continue;
                    var points=polygon.Select(p=>new Point(p.X-p.Z*LightDirection.X/LightDirection.Z,p.Y-p.Z*LightDirection.Y/LightDirection.Z)).ToArray();
                    if(points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)))continue;
                    // Every contour has the same winding: Nonzero fill is union, not XOR. Open
                    // holes stay clear wherever no displayed triangle intercepts the light ray.
                    var area=0d;
                    for(var j=1;j+1<points.Length;j++)
                        area+=(points[j].X-points[0].X)*(points[j+1].Y-points[0].Y)-(points[j].Y-points[0].Y)*(points[j+1].X-points[0].X);
                    if(!double.IsFinite(area)||area==0)continue;
                    if(area<0)Array.Reverse(points);
                    drawing.BeginFigure(points[0],isFilled:true,isClosed:true);
                    drawing.PolyLineTo(points[1..],isStroked:false,isSmoothJoin:false);
                }
            }
        }
        silhouette.Freeze();
        var bounds=silhouette.Bounds;
        if(bounds.IsEmpty||bounds.Width<=0||bounds.Height<=0)return null;
        var ink=new SolidColorBrush(Color.FromArgb(55,35,48,58));ink.Freeze();
        var artwork=new GeometryDrawing(ink,null,silhouette);artwork.Freeze();
        var brush=new DrawingBrush(artwork){Viewbox=bounds,ViewboxUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill,TileMode=TileMode.None};brush.Freeze();
        var material=new DiffuseMaterial(brush);material.Freeze();
        var quad=new MeshGeometry3D
        {
            Positions=new Point3DCollection{new(bounds.Left,bounds.Top,ReceiverZ),new(bounds.Right,bounds.Top,ReceiverZ),new(bounds.Right,bounds.Bottom,ReceiverZ),new(bounds.Left,bounds.Bottom,ReceiverZ)},
            TextureCoordinates=new PointCollection{new(0,0),new(1,0),new(1,1),new(0,1)},
            TriangleIndices=new Int32Collection{0,1,2,0,2,3},
            Normals=new Vector3DCollection{new(0,0,1),new(0,0,1),new(0,0,1),new(0,0,1)}
        };quad.Freeze();
        var model=new GeometryModel3D(quad,material);model.Freeze();return model;
    }

    private static List<Vec3> AbovePlate(Vec3[] triangle)
    {
        var result=new List<Vec3>(4);var previous=triangle[^1];
        foreach(var current in triangle)
        {
            if((previous.Z>=0)!=(current.Z>=0))
            {
                var t=previous.Z/(previous.Z-current.Z);var crossing=previous+(current-previous)*t;
                result.Add(new(crossing.X,crossing.Y,0));
            }
            if(current.Z>=0)result.Add(current);
            previous=current;
        }
        return result;
    }
}
