using Builder.Core;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static Model3DGroup Shadows()=>ViewGroup("shadowModels");
    private static GeometryModel3D ShadowModel()=>Shadows().Children.OfType<GeometryModel3D>().Single();
    private static GeometryDrawing ShadowDrawing(GeometryModel3D model)=>(GeometryDrawing)((DrawingBrush)((DiffuseMaterial)model.Material).Brush).Drawing;
    private static Geometry ShadowShape()=>ShadowDrawing(ShadowModel()).Geometry;
    private static async Task OpenShadow(string file)
    {
        Ok(await editor.OpenAsync(Path.Combine(root,".local/evidence/shadows-20261021/inputs",file),decision:DirtyDecision.Discard));Refresh();await Workspace(4);Click(Named<Button>("FitSceneButton"));Click(Named<Button>("ResetDisplayButton"));
    }
    private static GeometryModel3D? BuildShadow(params (MeshGeometry3D Geometry,Affine Transform)[] values)
    {
        var assembly=typeof(Builder.Desktop.MainWindow).Assembly;var type=assembly.GetType("Builder.Desktop.ShadowSurface")!;var array=Array.CreateInstance(type,values.Length);
        for(var i=0;i<values.Length;i++)array.SetValue(Activator.CreateInstance(type,values[i].Geometry,values[i].Transform),i);
        return (GeometryModel3D?)assembly.GetType("Builder.Desktop.DisplayShadow")!.GetMethod("Build",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,[array]);
    }
    private static void ShadowPixelsDarken(string on,string off)
    {
        static byte[] Pixels(string file)
        {
            var source=BitmapFrame.Create(new Uri(file),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var bitmap=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return pixels;
        }
        var a=Pixels(Path.Combine(output,on+".png"));var b=Pixels(Path.Combine(output,off+".png"));var dark=0;var light=0;
        // Exclude ribbon/sidebar/labels; the model and camera are identical in the paired captures.
        for(var y=300;y<575;y++)for(var x=0;x<710;x++)
        {var i=(y*1040+x)*4;var delta=(a[i]+a[i+1]+a[i+2])-(b[i]+b[i+1]+b[i+2]);if(delta< -12)dark++;if(delta>12)light++;}
        observations.Add(new{test="Shadow actual on/off viewport pixels",darkened=dark,lightened=light});
        Assert(dark>100&&dark>light,"Actual build-plate shadow did not visibly darken the unchanged viewport.");
    }
    private static async Task RunShadowControls()
    {
        await OpenShadow("raised-box.3mf");Layout(1040,680);var before=editor.Snapshot;var camera=CameraPose();
        Assert(editor.Presentation.Snapshot.Settings.Shadows&&Named<CheckBox>("ViewShadows").IsChecked==true,"Shadows are not enabled by default.");
        foreach(var name in new[]{"ViewShadows","ViewGrid","ViewWireframe","ViewXRay","ViewShadingFlat","ViewShadingSmooth","ResetDisplayButton"})Reachable(name);
        Assert(Named<FrameworkElement>("ViewportSurface").ActualHeight>=250,"View controls consumed the minimum viewport.");Named<TextBox>("ObjectName").Text="Keep this local name draft";
        ViewToggle("ViewShadows",false);Assert(Shadows().Children.Count==0,"Off left stale shadow geometry.");SameAccepted(before);Assert(CameraPose()==camera&&Named<TextBox>("ObjectName").Text=="Keep this local name draft","Shadow toggle reset camera or idle field draft.");
        Ok(editor.Presentation.Update(editor.Presentation.Snapshot.Settings with{Colors=false,Shading=ShadingMode.Smooth}));ViewToggle("ViewShadows",true);
        Assert(!editor.Presentation.Snapshot.Settings.Colors&&editor.Presentation.Snapshot.Settings.Shading==ShadingMode.Smooth,"Shadow toggle overwrote unrelated shared settings.");Click(Named<Button>("ResetDisplayButton"));Assert(editor.Presentation.Snapshot.Settings==ViewSettings.Default,"Reset did not restore default shadows.");Capture("shadows-controls-minimum",1040,680);
        Pass("Shadows default/reset on, controls fit the minimum View layout, and toggling only display state preserves idle drafts, camera, selection and history");

        var geometry=ShadowShape();Assert(geometry.FillContains(new Point(-30,45))&&!geometry.FillContains(new Point(10,0)),"Raised-box shadow has wrong directional projection or phantom footprint.");
        var quad=(MeshGeometry3D)ShadowModel().Geometry;Assert(quad.Positions.All(p=>Math.Abs(p.Z+.03)<1e-9),"Shadow receiver does not stay below grid/bed.");Assert(!SeparateDrawn().ContainsKey(ShadowModel()),"Shadow became an object pick target.");
        ViewToggle("ViewGrid",false);var modelBounds=editor.Snapshot.Scene.Bounds;var shadeBounds=ShadowShape().Bounds;typeof(Builder.Desktop.MainWindow).GetField("focus",PrivateInstance)!.SetValue(window,new Point3D((Math.Min(modelBounds.Min.X,shadeBounds.Left)+Math.Max(modelBounds.Max.X,shadeBounds.Right))/2,(Math.Min(modelBounds.Min.Y,shadeBounds.Top)+Math.Max(modelBounds.Max.Y,shadeBounds.Bottom))/2,modelBounds.Max.Z/2));var yawField=typeof(Builder.Desktop.MainWindow).GetField("yaw",PrivateInstance)!;yawField.SetValue(window,(double)yawField.GetValue(window)!+Math.PI);typeof(Builder.Desktop.MainWindow).GetField("distance",PrivateInstance)!.SetValue(window,(double)typeof(Builder.Desktop.MainWindow).GetField("distance",PrivateInstance)!.GetValue(window)!*1.8);Invoke("UpdateCamera");Capture("shadows-raised-box-on",1040,680);ViewToggle("ViewShadows",false);Capture("shadows-raised-box-off",1040,680);ShadowPixelsDarken("shadows-raised-box-on","shadows-raised-box-off");ViewToggle("ViewShadows",true);ViewToggle("ViewGrid",true);Capture("shadows-raised-box-grid",1040,680);
        Ok((await editor.TransitionAsync(new NewDocumentTransition(editor.Snapshot.Revision,DirtyDecision.Discard))).Result);Refresh();Assert(Shadows().Children.Count==0,"Empty scene retained a prior object's shadow.");await Open("cube-20mm.stl");Assert(ShadowShape().Bounds!=geometry.Bounds,"Scene replacement reused previous shadow footprint.");
        Pass("Actual elevated mesh casts a directionally shifted build-plate silhouette; off, empty scene and replacement clear or update the independent non-pickable receiver");

        await OpenShadow("raised-torus.3mf");var obj=editor.Snapshot.Scene.Objects.Single();var center=obj.Bounds.Center;var hole=new Point(center.X-1.4*center.Z,center.Y+1.2*center.Z);var rim=new Point(hole.X+10,hole.Y);
        Assert(!ShadowShape().FillContains(hole)&&ShadowShape().FillContains(rim),"Projected torus hole was filled or its material ring omitted.");Capture("shadows-torus-hole",1040,680);
        var flat=(MeshGeometry3D)Invoke("DisplayMesh",obj.Mesh)!;var one=BuildShadow((flat,obj.Transform))!;var twice=BuildShadow((flat,obj.Transform),(flat,obj.Transform))!;var firstDrawing=ShadowDrawing(one);var duplicateDrawing=ShadowDrawing(twice);
        Assert(firstDrawing.Brush.Opacity==duplicateDrawing.Brush.Opacity&&firstDrawing.Geometry.FillContains(rim)&&duplicateDrawing.Geometry.FillContains(rim)&&!duplicateDrawing.Geometry.FillContains(hole),"Overlapping projected faces changed silhouette coverage or opacity.");
        Assert(BuildShadow((flat,obj.Transform.Then(Affine.Translation(new(0,0,-100)))))==null,"Below-bed geometry cast a phantom shadow.");
        await OpenSurfacePaint();await Workspace(4);Click(Named<Button>("ViewTopButton"));Layout(1040,680);obj=editor.Snapshot.Scene.Objects.Single();ViewFaces(obj,255);var point=ProjectMeasuredWorld(new(6,11,20));var pick=await(Task<Guid?>)Invoke("PickViewportObject",point)!;before=editor.Snapshot;ViewToggle("ViewShadows",false);ViewFaces(obj,255);var afterPick=await (Task<Guid?>)Invoke("PickViewportObject",point)!;Assert(afterPick==pick&&pick==obj.Id,"Shadow toggle changed accepted-mesh pick.");ViewToggle("ViewShadows",true);ViewFaces(obj,255);SameAccepted(before);
        Pass("Torus light-ray hole remains open, overlapping surfaces use one uniform silhouette, below-bed meshes cast nothing, and authored face colors/Core picking remain unchanged");

        await OpenShadow("raised-box.3mf");before=editor.Snapshot;var acceptedBounds=ShadowShape().Bounds;await Workspace(1);Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionZ").Text="40";Click(Named<Button>("PreviewTransformButton"));await Idle();var candidate=editor.Snapshot.Preview!;var previewBounds=ShadowShape().Bounds;
        Assert(previewBounds!=acceptedBounds,"Move preview did not update its actual shadow.");Named<TextBox>("PositionY").Text="14.25";await Workspace(4);ViewToggle("ViewShadows",false);ViewToggle("ViewShadows",true);
        Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<TextBox>("PositionY").Text=="14.25"&&!Named<Button>("ApplyTransformButton").IsEnabled,"Shadow display invalidated candidate/draft or enabled stale Apply.");TransformFooterVisible();Click(Named<Button>("CancelTransformButton"));SameAccepted(before);Assert(ShadowShape().Bounds==acceptedBounds,"Cancel did not restore accepted shadow.");
        await Workspace(1);Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionZ").Text="40";Click(Named<Button>("PreviewTransformButton"));await Idle();candidate=editor.Snapshot.Preview!;Click(Named<Button>("ApplyTransformButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,candidate.Scene),"Apply lost actual transform candidate.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&ShadowShape().Bounds==acceptedBounds,"Undo did not restore original geometry/shadow.");
        Pass("Owned move preview updates the shadow; display toggles retain dirty drafts/token, Cancel restores accepted coverage and Apply/Undo restores geometry with its shadow");
    }
}
