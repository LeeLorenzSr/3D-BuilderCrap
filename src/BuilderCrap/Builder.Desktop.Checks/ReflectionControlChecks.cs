using Builder.Core;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static Model3DGroup Reflections()=>ViewGroup("reflectionModels");
    private static GeometryModel3D[] ReflectedModels()=>Reflections().Children.OfType<GeometryModel3D>().ToArray();
    private static Color ReflectionColor(GeometryModel3D model)=>((SolidColorBrush)((DiffuseMaterial)model.Material).Brush).Color;
    private static async Task OpenReflection(string file="raised-box.3mf")
    {
        Ok(await editor.OpenAsync(Path.Combine(root,".local/evidence/reflections-20261021/inputs",file),decision:DirtyDecision.Discard));Refresh();await Workspace(4);Click(Named<Button>("FitSceneButton"));Click(Named<Button>("ResetDisplayButton"));
    }
    private static GeometryModel3D? BuildReflection(MeshGeometry3D geometry,Affine transform)
    {
        var assembly=typeof(Builder.Desktop.MainWindow).Assembly;var type=assembly.GetType("Builder.Desktop.ReflectionSurface")!;
        var material=new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(50,100,150)));material.Freeze();
        var value=Activator.CreateInstance(type,geometry,transform,material);
        return (GeometryModel3D?)assembly.GetType("Builder.Desktop.DisplayReflection")!.GetMethod("Build",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,[value]);
    }
    private static void ReflectionNormals(GeometryModel3D model)
    {
        var geometry=(MeshGeometry3D)model.Geometry;Assert(model.IsFrozen&&geometry.Positions.All(p=>p.Z<=-.06+1e-9),"Reflection was mutable or crossed above the build plane.");
        for(var i=0;i<geometry.TriangleIndices.Count;i+=3)
        {
            var a=geometry.Positions[geometry.TriangleIndices[i]];var b=geometry.Positions[geometry.TriangleIndices[i+1]];var c=geometry.Positions[geometry.TriangleIndices[i+2]];var normal=Vector3D.CrossProduct(b-a,c-a);normal.Normalize();
            foreach(var index in geometry.TriangleIndices.Skip(i).Take(3))
            {var n=geometry.Normals[index];Assert(double.IsFinite(n.X)&&double.IsFinite(n.Y)&&double.IsFinite(n.Z)&&Math.Abs(n.Length-1)<1e-8&&Vector3D.DotProduct(n,normal)>0,"Reflected normal/winding disagreed after source affine or clipping.");}
        }
    }
    private static void ReflectionPixelsChanged()
    {
        static byte[] Pixels(string path)
        {var source=BitmapFrame.Create(new Uri(path),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);var bitmap=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return pixels;}
        var on=Pixels(Path.Combine(output,"reflection-raised-on.png"));var off=Pixels(Path.Combine(output,"reflection-raised-off.png"));var changed=0;
        for(var y=300;y<630;y++)for(var x=0;x<710;x++)
        {var i=(y*1040+x)*4;if(Math.Abs(on[i]-off[i])+Math.Abs(on[i+1]-off[i+1])+Math.Abs(on[i+2]-off[i+2])>12)changed++;}
        observations.Add(new{test="Actual reflection on/off viewport pixels",changed});Assert(changed>200,"Reflection resources existed without a visible on/off result.");
    }
    private static void CheckReflectedColors()
    {
        var originals=SeparateDrawn().Keys.Select(ReflectionColor).Select(c=>Color.FromArgb((byte)Math.Round(c.A*.22),c.R,c.G,c.B)).OrderBy(c=>c.ToString()).ToArray();
        var reflected=ReflectedModels().Select(ReflectionColor).OrderBy(c=>c.ToString()).ToArray();Assert(originals.SequenceEqual(reflected),"Reflections lost effective face/base RGB or fixed opacity.");
        Assert(ReflectedModels().All(m=>ReferenceEquals(m.Material,m.BackMaterial)&&!SeparateDrawn().ContainsKey(m)),"Reflected front/back color differs or reflection became a pick target.");
    }
    private static async Task RunReflectionControls()
    {
        await OpenReflection();Layout(1040,680);var before=editor.Snapshot;var camera=CameraPose();Assert(!editor.Presentation.Snapshot.Settings.Reflections&&Named<CheckBox>("ViewReflections").IsChecked==false&&Reflections().Children.Count==0,"Default reflections are not off.");
        foreach(var name in new[]{"ViewReflections","ViewShadows","ViewGrid","ViewXRay","ViewShadingSmooth","ResetDisplayButton"})Reachable(name);Assert(Named<FrameworkElement>("ViewportSurface").ActualHeight>=250,"Reflections consumed the minimum viewport.");
        Named<TextBox>("ObjectName").Text="Preserve local reflection draft";ViewToggle("ViewReflections",true);SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Preserve local reflection draft"&&CameraPose()==camera,"Reflection toggle reset idle draft/camera.");
        Ok(editor.Presentation.Update(editor.Presentation.Snapshot.Settings with{Grid=false,Shading=ShadingMode.Smooth}));ViewToggle("ViewReflections",false);Assert(!editor.Presentation.Snapshot.Settings.Grid&&editor.Presentation.Snapshot.Settings.Shading==ShadingMode.Smooth,"Reflection checkbox overwrote unrelated shared settings.");Click(Named<Button>("ResetDisplayButton"));Assert(editor.Presentation.Snapshot.Settings==ViewSettings.Default&&Reflections().Children.Count==0,"Reset kept reflections or lost other defaults.");Capture("reflections-controls-minimum",1040,680);
        Pass("Reflections default/reset off; compact View controls fit minimum layout and update only presentation while preserving camera, idle drafts and accepted state");

        ViewToggle("ViewGrid",false);ViewToggle("ViewShadows",false);var focusField=typeof(Builder.Desktop.MainWindow).GetField("focus",PrivateInstance)!;var focus=(Point3D)focusField.GetValue(window)!;focusField.SetValue(window,new Point3D(focus.X,focus.Y,0));var distanceField=typeof(Builder.Desktop.MainWindow).GetField("distance",PrivateInstance)!;distanceField.SetValue(window,(double)distanceField.GetValue(window)!*2.2);Invoke("UpdateCamera");ViewToggle("ViewReflections",true);
        var reflected=ReflectedModels().Single();var displayed=(MeshGeometry3D)reflected.Geometry;var obj=editor.Snapshot.Scene.Objects.Single();Near(displayed.Positions.Max(p=>p.Z),-obj.Bounds.Min.Z-.06);Near(displayed.Positions.Min(p=>p.Z),-obj.Bounds.Max.Z-.06);ReflectionNormals(reflected);Capture("reflection-raised-on",1040,680);ViewToggle("ViewReflections",false);Capture("reflection-raised-off",1040,680);ReflectionPixelsChanged();ViewToggle("ViewReflections",true);
        var triangle=new MeshGeometry3D{Positions=new Point3DCollection{new(0,0,1),new(1,0,1),new(0,1,2)},TriangleIndices=new Int32Collection{0,1,2},Normals=new Vector3DCollection{new(0,-1,1),new(0,-1,1),new(0,-1,1)}};triangle.Freeze();
        foreach(var scale in new[]{new Vec3(2,3,4),new Vec3(-2,3,4)}){var model=BuildReflection(triangle,Affine.ScaleAbout(scale,new()))!;ReflectionNormals(model);var n=((MeshGeometry3D)model.Geometry).Normals[0];Near(n.Y,-.8);Near(n.Z,-.6);}
        var clipping=new MeshGeometry3D{Positions=new Point3DCollection{new(0,0,-1),new(2,0,1),new(0,2,1)},TriangleIndices=new Int32Collection{0,1,2},Normals=new Vector3DCollection{new(-1,-1,1),new(-1,-1,1),new(-1,-1,1)}};clipping.Freeze();var clipped=BuildReflection(clipping,Affine.Identity)!;ReflectionNormals(clipped);Assert(((MeshGeometry3D)clipped.Geometry).Positions.Any(p=>Math.Abs(p.Z+.06)<1e-9),"Crossing face was not clipped at the plate.");Assert(BuildReflection(triangle,Affine.Translation(new(0,0,-10)))==null,"Below-bed source fabricated an above-bed reflection.");
        Ok((await editor.TransitionAsync(new NewDocumentTransition(editor.Snapshot.Revision,DirtyDecision.Discard))).Result);Refresh();Assert(Reflections().Children.Count==0,"New scene retained old reflection resources.");
        Pass("Elevated model has a real on/off mirrored image; world clipping, nonuniform/negative affine winding and finite normals place only visible source geometry below the plate, and New clears resources");

        await OpenSurfacePaint();await Workspace(4);ViewToggle("ViewReflections",true);SetShading(true);CheckReflectedColors();var normalModels=ReflectedModels();ViewToggle("ViewGrid",false);ViewToggle("ViewShadows",false);Assert(normalModels.SequenceEqual(ReflectedModels()),"Unrelated display flags rebuilt unchanged reflection resources.");ViewToggle("ViewXRay",true);CheckReflectedColors();ViewToggle("ViewColors",false);CheckReflectedColors();ViewToggle("ViewColors",true);CheckReflectedColors();ViewToggle("ViewXRay",false);CheckReflectedColors();SetShading(false);CheckReflectedColors();Assert(!normalModels.SequenceEqual(ReflectedModels()),"Shading/material change retained stale reflection normals.");
        Click(Named<Button>("ViewTopButton"));Layout(1040,680);var point=ProjectMeasuredWorld(new(6,11,20));var pick=await(Task<Guid?>)Invoke("PickViewportObject",point)!;before=editor.Snapshot;ViewToggle("ViewReflections",false);var plainPick=await(Task<Guid?>)Invoke("PickViewportObject",point)!;Assert(pick==plainPick&&pick==editor.Snapshot.Scene.Objects.Single().Id,"Reflection changed accepted mesh picking.");ViewToggle("ViewReflections",true);SameAccepted(before);
        Assert(ViewGroup("sceneModels").Children.IndexOf(Reflections())<ViewGroup("sceneModels").Children.IndexOf(SeparateDrawn().Keys.First()),"Reflections render after real geometry.");
        Pass("Painted batches retain exact RGB with fixed reflection alpha through Smooth/colors/X-ray; cache invalidates actual batches and reflection resources never affect Core picking or document state");

        await OpenReflection();ViewToggle("ViewReflections",true);before=editor.Snapshot;var original=Reflections().Bounds;await Workspace(1);Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionZ").Text="40";Click(Named<Button>("PreviewTransformButton"));await Idle();var candidate=editor.Snapshot.Preview!;Assert(Reflections().Bounds!=original,"Move preview did not update reflected geometry.");Named<TextBox>("PositionY").Text="14.25";await Workspace(4);camera=CameraPose();ViewToggle("ViewReflections",false);ViewToggle("ViewReflections",true);Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<TextBox>("PositionY").Text=="14.25"&&!Named<Button>("ApplyTransformButton").IsEnabled&&CameraPose()==camera,"Reflection toggle lost dirty candidate/draft or camera.");TransformFooterVisible();Click(Named<Button>("CancelTransformButton"));SameAccepted(before);Assert(Reflections().Bounds==original,"Cancel retained candidate reflection.");
        await Workspace(1);Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionZ").Text="40";Click(Named<Button>("PreviewTransformButton"));await Idle();candidate=editor.Snapshot.Preview!;Click(Named<Button>("ApplyTransformButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,candidate.Scene),"Apply lost exact candidate under reflections.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&Reflections().Bounds==original,"Undo lost accepted reflection.");
        await Workspace(2);Click(Named<Button>("HollowButton"));await PreviewHollow();var cut=ReflectedModels().SelectMany(m=>((MeshGeometry3D)m.Geometry).Positions).Max(p=>p.X);Named<CheckBox>("HollowCutaway").IsChecked=false;await HollowViewIdle();Assert(cut<ReflectedModels().SelectMany(m=>((MeshGeometry3D)m.Geometry).Positions).Max(p=>p.X),"Reflection ignored actual Hollow cutaway visibility.");Click(Named<Button>("CancelHollowButton"));
        Pass("Real move preview/dirty draft/Cancel/ApplyUndo update reflections without accepted edits; Hollow reflection follows actual cutaway rather than fabricating the hidden full mesh");
    }
}
