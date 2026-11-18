using Builder.Core;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

internal readonly record struct OutlineSource(Guid Id,MeshGeometry3D Mesh,Affine Transform);

public partial class MainWindow
{
    private readonly Model3DGroup selectionOutlines = new();
    private readonly Dictionary<Guid,SelectionSilhouette> outlineShapes = [];
    private OutlineSource[] outlineSources = [];
    private (Point3D Eye,Vector3D Look,double Resolution,double ViewWidth,bool Ortho,bool XRay)? lastOutlineView;

    private void SetOutlineSources(List<OutlineSource> sources)
    {
        if (sources.SequenceEqual(outlineSources)) return;
        var ids = sources.Select(s=>s.Id).ToHashSet();
        foreach (var id in outlineShapes.Keys.Where(id=>!ids.Contains(id)).ToArray()) outlineShapes.Remove(id);
        foreach (var source in sources)
            if (!outlineShapes.TryGetValue(source.Id,out var existing) || existing.Source != source)
                outlineShapes[source.Id] = new SelectionSilhouette(source);
        outlineSources = sources.ToArray();
        lastOutlineView = null;
    }

    private void DrawSelectionOutline()
    {
        if (Viewport.Camera is not ProjectionCamera view || Viewport.ActualWidth<=0) return;
        var orthographic = view is OrthographicCamera;
        var width = orthographic ? ((OrthographicCamera)view).Width : 2*Math.Tan(((PerspectiveCamera)view).FieldOfView*Math.PI/360);
        var xray = editor.Presentation.Snapshot.Settings.XRay;
        var key = (view.Position,view.LookDirection,Viewport.ActualWidth,width,orthographic,xray);
        if (lastOutlineView == key) return;
        lastOutlineView = key;
        selectionOutlines.Children.Clear();
        if (outlineShapes.Count == 0) return;
        var mesh = new MeshGeometry3D();
        var forward = view.LookDirection;
        forward.Normalize();
        foreach (var shape in outlineShapes.Values)
            shape.Draw(mesh,view.Position,forward,width/Viewport.ActualWidth,orthographic);
        if (mesh.Positions.Count==0) return;
        // Drawn in the model viewport for ordinary depth testing, independently of face
        // materials, flat/smooth shading, shadows, reflections and accepted-mesh picking.
        var opacity=xray ? (byte)145 : (byte)255;
        var material=new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(opacity,0,0,0))));
        material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(opacity,108,186,242))));
        var edges = new GeometryModel3D(mesh,material) { BackMaterial=material };
        edges.Freeze();
        selectionOutlines.Children.Add(edges);
    }
}

// Build adjacency only when the selected instance's displayed geometry or placement changes.
// Coincident STL triangle corners are welded for edge classification without editing the mesh.
internal sealed class SelectionSilhouette
{
    internal OutlineSource Source { get; }
    private readonly Point3D[] points;
    internal IReadOnlyList<Point3D> WorldVertices => points;
    private readonly record struct Face(Point3D Point,Vector3D Normal);
    private readonly record struct Edge(int Start,int End,int First,int Second);
    private readonly Face[] faces;
    private readonly Edge[] edges;
    private readonly bool[] facing;

    internal SelectionSilhouette(OutlineSource source)
    {
        Source = source;
        var mesh = source.Mesh;
        var unique = new Dictionary<Point3D,int>();
        var ids = new int[mesh.Positions.Count];
        var world = new List<Point3D>();
        for (var i=0;i<ids.Length;i++)
        {
            var p = mesh.Positions[i];
            if (!unique.TryGetValue(p,out var id))
            {
                id=world.Count;
                unique.Add(p,id);
                var point=source.Transform.Apply(new Vec3(p.X,p.Y,p.Z));
                world.Add(new(point.X,point.Y,point.Z));
            }
            ids[i]=id;
        }
        points=world.ToArray();
        faces=new Face[mesh.TriangleIndices.Count/3];
        facing=new bool[faces.Length];
        var adjacency = new Dictionary<(int,int),Edge>();
        for (var i=0;i<faces.Length;i++)
        {
            var a=ids[mesh.TriangleIndices[i*3]];
            var b=ids[mesh.TriangleIndices[i*3+1]];
            var c=ids[mesh.TriangleIndices[i*3+2]];
            var normal=Vector3D.CrossProduct(points[b]-points[a],points[c]-points[a]);
            faces[i]=new(points[a],normal);
            if (normal.LengthSquared==0) continue;
            Connect(a,b,i); Connect(b,c,i); Connect(c,a,i);
        }
        edges=adjacency.Values.ToArray();
        void Connect(int a,int b,int face)
        {
            var key = a<b ? (a,b) : (b,a);
            if (adjacency.TryGetValue(key,out var edge)) adjacency[key]=edge with { Second=face };
            else adjacency.Add(key,new(a,b,face,-1));
        }
    }

    internal void Draw(MeshGeometry3D mesh,Point3D eye,Vector3D forward,double pixelScale,bool orthographic)
    {
        for (var i=0;i<faces.Length;i++)
        {
            var toEye=orthographic ? -forward : eye-faces[i].Point;
            facing[i]=Vector3D.DotProduct(faces[i].Normal,toEye)>0;
        }
        foreach (var edge in edges)
        {
            if (edge.Second>=0 && facing[edge.First]==facing[edge.Second]) continue;
            var a=points[edge.Start]; var b=points[edge.End];
            var center=a+(b-a)*.5;
            var depth=Vector3D.DotProduct(center-eye,forward);
            if (depth<=0) continue;
            var toEye=orthographic ? -forward : eye-center;
            var side=Vector3D.CrossProduct(toEye,b-a);
            if (side.LengthSquared==0) continue;
            var unit=pixelScale*(orthographic ? 1 : depth);
            side.Normalize(); side*=unit;
            toEye.Normalize();
            var lift=toEye*(unit*.3);
            var first=mesh.Positions.Count;
            mesh.Positions.Add(a+lift-side); mesh.Positions.Add(b+lift-side);
            mesh.Positions.Add(b+lift+side); mesh.Positions.Add(a+lift+side);
            mesh.TriangleIndices.Add(first); mesh.TriangleIndices.Add(first+1); mesh.TriangleIndices.Add(first+2);
            mesh.TriangleIndices.Add(first); mesh.TriangleIndices.Add(first+2); mesh.TriangleIndices.Add(first+3);
        }
    }
}
