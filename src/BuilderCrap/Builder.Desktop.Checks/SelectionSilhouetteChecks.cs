using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static async Task CheckSelectionSilhouette()
    {
        await Open("cube-20mm.stl");
        var cube = editor.Snapshot.SelectedObjectId!.Value;
        Ok(editor.Execute(new AddCube()));
        var cover = editor.Snapshot.SelectedObjectId!.Value;
        Ok(editor.Execute(new ResizeObject(cover,new(10,12,4))));
        Ok(editor.Execute(new MoveObject(cover,new(20,10,24))));
        Ok(editor.Select(cube)); Refresh();
        await Workspace(4); Click(Named<Button>("ViewTopButton"));
        Layout(1040,680);
        var image = Frame("silhouette-overlap");
        Assert(BlueNear(image,new(20,2,20)),"Exposed selected cube edge has no blue silhouette.");
        Assert(!BlueNear(image,new(20,10,20)),"Selection outline showed through a nearer unselected part.");
        var fill = Pixel(image,ProjectMeasuredWorld(new(4,4,20)));
        Assert(fill.A>=250 && Math.Max(fill.R,Math.Max(fill.G,fill.B))-Math.Min(fill.R,Math.Min(fill.G,fill.B))<35,
            "Selecting an unpainted part tinted its face blue.");
        var hit = await (Task<Guid?>)Invoke("PickViewportObject",ProjectMeasuredWorld(new(20,10,26)))!;
        Assert(hit==cover,"The presentation outline intercepted a pick.");

        var cleared = await editor.TransitionAsync(new NewDocumentTransition(editor.Snapshot.Revision,DirtyDecision.Discard));
        Ok(cleared.Result);
        Ok(await editor.PreviewAsync(new TorusPrimitiveRequest(12,3,32,12)));
        Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));
        var ring = editor.Snapshot.Scene.Objects.Single();
        var material = new SurfaceMaterial("Coral",new(206,90,72));
        Ok(await editor.PreviewMaterialAsync(new([ring.Id],material)));
        Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));
        Refresh(); Click(Named<Button>("ViewTopButton")); Layout(1040,680);
        image=Frame("silhouette-painted-ring");
        var center=ring.Bounds.Center;
        Assert(BlueNear(image,center+new Vec3(15,0,0)) && BlueNear(image,center+new Vec3(9,0,0)),
            "The selected ring needs visible inner and outer contours.");
        fill=Pixel(image,ProjectMeasuredWorld(center+new Vec3(12,0,3)));
        Assert(fill.R>fill.B+30 && SeparateDrawnColor(ring.Id)==Color.FromRgb(206,90,72),
            "Selection changed the assigned face color.");
        var snapshot=editor.Snapshot;
        ViewToggle("ViewXRay",true); ViewToggle("ViewWireframe",true); SetShading(true);
        Assert(ViewGroup("selectionOutlines").Children.Count==1 && ViewGroup("wireframeModels").Children.Count>0,
            "The selected silhouette replaced another inspection mode.");
        SameAccepted(snapshot); Click(Named<Button>("ResetDisplayButton"));
        Pass("Selected neutral and painted objects retain their face colors, including a ring's inner and outer blue silhouettes; a nearer part occludes the stroke and receives the pick");

        RenderTargetBitmap Frame(string name)
        {
            var viewport=Named<Viewport3D>("Viewport");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(viewport.ActualWidth),(int)Math.Ceiling(viewport.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(viewport);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(output,name+".png")); encoder.Save(file);
            return bitmap;
        }
        bool BlueNear(BitmapSource frame,Vec3 world)
        {
            var screen=ProjectMeasuredWorld(world);
            for (var dy=-3;dy<=3;dy++)
            for (var dx=-3;dx<=3;dx++)
            {
                var pixel=Pixel(frame,new(screen.X+dx,screen.Y+dy));
                if (pixel.A>100 && pixel.B>pixel.R+45 && pixel.G>pixel.R+20) return true;
            }
            return false;
        }
        static Color Pixel(BitmapSource frame,Point point)
        {
            var bytes=new byte[4];
            frame.CopyPixels(new Int32Rect((int)Math.Round(point.X),(int)Math.Round(point.Y),1,1),bytes,4,0);
            return Color.FromArgb(bytes[3],bytes[2],bytes[1],bytes[0]);
        }
    }
}
