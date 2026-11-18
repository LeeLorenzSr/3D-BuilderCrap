using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static async Task CheckMetricFloor()
    {
        await Open("cube-20mm.stl");
        await Workspace(4);
        ViewToggle("ViewGrid",true);
        var before = editor.Snapshot;
        var layer = Named<Border>("FloorLayer");
        var floorViewport = Named<Viewport3D>("FloorViewport");
        var visual = Named<ModelVisual3D>("FloorVisual");
        var floor = (Model3DGroup)visual.Content;
        Assert(ReferenceEquals(floorViewport.Camera,Named<Viewport3D>("Viewport").Camera)
            && !layer.IsHitTestVisible && floor.IsFrozen,"Floor must follow the scene camera outside picking.");
        ViewToggle("ViewGrid",false);
        Assert(layer.Visibility==Visibility.Collapsed,"Turning off Grid left the floor visible.");
        ViewToggle("ViewGrid",true);
        Assert(layer.Visibility==Visibility.Visible && ReferenceEquals(visual.Content,floor),"Grid toggle lost the retained floor.");
        SameAccepted(before);

        const int width = 560, height = 400;
        // Four pixels per millimeter. At this camera position (0,0) maps to (220,260).
        var top = RenderFloor("metric-floor-top",new OrthographicCamera(new(15,15,100),new(0,0,-100),new(0,1,0),140));
        var pixels = new byte[width*height*4];
        top.CopyPixels(pixels,width*4,0);
        int Level(double x,double y) => pixels[((int)(260-4*y)*width+(int)(220+4*x))*4];
        var dark = Level(-35,-15);
        var light = Level(-25,-15);
        Assert(Math.Abs(dark-light)>=12 && Level(-15,-15)==dark && Level(-35,-5)==light,
            "Alternating 10 mm floor tiles changed spacing or contrast.");
        bool Ink(double x,double y)
        {
            var min = Math.Min(dark,light);
            var max = Math.Max(dark,light);
            for (var iy = 1; iy < 10; iy++)
            for (var ix = 0; ix < 22; ix++)
            {
                var sample = Level(x+ix,y+iy);
                if (sample+8<min || sample>max+8) return true;
            }
            return false;
        }
        Assert(Ink(0,0) && Ink(50,0) && Ink(0,50) && !Ink(-50,0) && !Ink(0,-25),
            "Expected printed labels at the origin and each positive 50 mm mark only.");
        RenderFloor("metric-floor-perspective",new PerspectiveCamera(new(0,-140,55),new(0,170,-55),new(0,0,1),50));

        var focusField = typeof(Builder.Desktop.MainWindow).GetField("focus",PrivateInstance)!;
        var focus = focusField.GetValue(window)!;
        try
        {
            focusField.SetValue(window,new Point3D(9_000_000,-8_000_000,0));
            Invoke("RefreshFloor");
            var far = (Model3DGroup)visual.Content;
            Assert(far.Bounds.X<=9_000_000 && far.Bounds.X+far.Bounds.SizeX>=9_000_000 &&
                far.Children.OfType<GeometryModel3D>().Sum(m=>((MeshGeometry3D)m.Geometry).TriangleIndices.Count/3)<=2048,
                "Panning to a distant model lost the floor or grew its drawing workload.");
        }
        finally { focusField.SetValue(window,focus); Invoke("RefreshFloor"); }
        SameAccepted(before);
        Pass("10 mm checkerboard, origin and positive 50 mm floor rulers, grid toggle, shared camera, and bounded work far from origin");

        BitmapSource RenderFloor(string name,ProjectionCamera camera)
        {
            camera.NearPlaneDistance=.02; camera.FarPlaneDistance=10000;
            var viewport = new Viewport3D { Camera=camera };
            viewport.Children.Add(new ModelVisual3D { Content=new AmbientLight(Colors.White) });
            viewport.Children.Add(new ModelVisual3D { Content=floor });
            var host = new Border { Child=viewport, Background=layer.Background };
            host.Measure(new Size(width,height)); host.Arrange(new Rect(0,0,width,height)); host.UpdateLayout();
            var image = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            image.Render(host);
            using var stream = File.Create(Path.Combine(output,name+".png"));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
            return image;
        }
    }
}
