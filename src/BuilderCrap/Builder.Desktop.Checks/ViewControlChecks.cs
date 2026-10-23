using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static void ViewToggle(string name,bool value)=>Named<CheckBox>(name).IsChecked=value;
    private static Model3DGroup ViewGroup(string field)=>(Model3DGroup)typeof(Builder.Desktop.MainWindow).GetField(field,PrivateInstance)!.GetValue(window)!;
    private static void ViewFaces(SceneObject obj,byte alpha)
    {
        var mesh=obj.Mesh;var source=mesh.Triangles.Select((t,i)=>(key:(mesh.Vertices[t.A],mesh.Vertices[t.B],mesh.Vertices[t.C]),i)).ToDictionary(v=>v.key,v=>v.i);var seen=new HashSet<int>();
        foreach(var model in SeparateDrawn().Where(p=>p.Value==obj.Id).Select(p=>p.Key))
        {
            var part=(MeshGeometry3D)model.Geometry;var actual=((SolidColorBrush)((DiffuseMaterial)model.Material).Brush).Color;
            Assert(ReferenceEquals(model.Material,model.BackMaterial),"X-ray front/back materials differ.");
            for(var i=0;i<part.TriangleIndices.Count;i+=3)
            {
                Vec3 Vertex(int index){var p=part.Positions[part.TriangleIndices[index]];return new(p.X,p.Y,p.Z);}
                Assert(source.TryGetValue((Vertex(i),Vertex(i+1),Vertex(i+2)),out var face)&&seen.Add(face),"Display toggles omitted/reordered/duplicated source faces.");var expected=obj.GetTriangleMaterial(face)!.Color;
                Assert(actual==Color.FromArgb(alpha,expected.R,expected.G,expected.B),"Cached face RGB/alpha did not match requested display settings.");
            }
        }
        Assert(seen.Count==mesh.Triangles.Length,"Face-color display omitted geometry.");
    }
    private static async Task RunViewControls()
    {
        await OpenSurfacePaint();await Workspace(4);Click(Named<Button>("ResetDisplayButton"));var before=editor.Snapshot;var presentation=editor.Presentation.Snapshot;
        Assert(presentation.Settings==ViewSettings.Default&&Named<CheckBox>("ViewGrid").IsChecked==true&&Named<CheckBox>("ViewColors").IsChecked==true&&Named<CheckBox>("ViewWireframe").IsChecked==false&&Named<CheckBox>("ViewXRay").IsChecked==false,"View defaults differ from Core or controls.");
        Named<TextBox>("ObjectName").Text="Local display-test draft";var camera=CameraPose();ViewToggle("ViewGrid",false);Assert(!ViewGroup("sceneModels").Children.Contains(ViewGroup("gridModels")),"Grid remained attached when disabled.");SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Local display-test draft"&&CameraPose()==camera,"Display toggle reset idle draft/camera.");
        ViewToggle("ViewGrid",true);Assert(ViewGroup("sceneModels").Children.Contains(ViewGroup("gridModels")),"Grid did not return.");Layout(1040,680);
        foreach(var name in new[]{"ViewGrid","ViewColors","ViewWireframe","ViewXRay","ResetDisplayButton","AboutModelButton","RecoverButton","FitSceneButton"})Reachable(name);
        Assert(Named<FrameworkElement>("ViewportSurface").ActualHeight>=250,"View controls consumed the minimum viewport.");Capture("view-default-minimum",1040,680);
        Pass("View defaults are Core-owned; grid toggles only its presentation node, idle drafts/camera/history survive, and compact controls retain a usable minimum viewport");

        var obj=editor.Snapshot.Scene.Objects.Single();ViewFaces(obj,255);ViewToggle("ViewColors",false);SameAccepted(before);Assert(SeparateDrawn().Where(p=>p.Value==obj.Id).Select(p=>((SolidColorBrush)((DiffuseMaterial)p.Key.Material).Brush).Color).Distinct().Count()==1&&Named<Border>("DisplayNotice").Visibility==Visibility.Visible,"Colors off kept authored face colors or omitted its paint notice.");
        ViewToggle("ViewColors",true);ViewFaces(obj,255);ViewToggle("ViewXRay",true);ViewFaces(obj,72);ViewToggle("ViewColors",false);ViewToggle("ViewColors",true);ViewFaces(obj,72);ViewToggle("ViewXRay",false);ViewFaces(obj,255);SameAccepted(before);
        Assert(Named<Border>("DisplayNotice").Visibility==Visibility.Collapsed,"Colors notice remained after reset.");
        Pass("Assigned base/sparse face RGB can hide and return exactly; X-ray alpha invalidates front/back material caches even across color toggles without changing geometry or authored colors");

        Ok(await editor.OpenAsync(Path.Combine(root,".local/evidence/hollow-20261020/core/hollow-box-20.3mf"),decision:DirtyDecision.Discard));Refresh();await Workspace(4);Click(Named<Button>("FitSceneButton"));Click(Named<Button>("ResetDisplayButton"));ViewToggle("ViewGrid",false);before=editor.Snapshot;camera=CameraPose();Capture("view-hollow-shaded",1040,680);
        ViewToggle("ViewXRay",true);Capture("view-hollow-xray",1040,680);ViewToggle("ViewWireframe",true);
        var wire=ViewGroup("wireframeModels");Assert(wire.Children.Count>0&&ViewGroup("sceneModels").Children.Contains(wire),"Wireframe did not attach actual triangle-edge geometry.");
        Assert(wire.Children.OfType<GeometryModel3D>().Sum(m=>((MeshGeometry3D)m.Geometry).TriangleIndices.Count/3)==before.Scene.Objects.Sum(o=>o.Mesh.Triangles.Length)*6,"Wireframe does not contain all displayed triangle edges.");
        Assert(!SeparateDrawn().Keys.Any(m=>wire.Children.Contains(m)),"Wireframe inspection geometry became a pick target.");SameAccepted(before);Assert(CameraPose()==camera,"X-ray/wireframe reframed the model.");Capture("view-hollow-xray-wireframe",1040,680);Click(Named<Button>("ResetDisplayButton"));SameAccepted(before);Assert(wire.Children.Count==0&&editor.Presentation.Snapshot.Settings==ViewSettings.Default,"Reset left a stale wireframe/X-ray node.");
        Pass("X-ray and depth-tested triangle ribbons inspect the actual nested-shell mesh, combine with grid/colors, remain outside picking and reset without changing accepted content or camera");

        await Open("cube-20mm.stl");Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="27.5";Click(Named<Button>("PreviewTransformButton"));await Idle();before=editor.Snapshot;var candidate=before.Preview!;Named<TextBox>("PositionY").Text="13.25";var draft=Named<TextBox>("PositionY").Text;await Workspace(4);
        foreach(var toggle in new[]{"ViewGrid","ViewColors","ViewWireframe","ViewXRay"})ViewToggle(toggle,toggle is "ViewWireframe" or "ViewXRay");
        SameAccepted(before);Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<TextBox>("PositionY").Text==draft&&!Named<Button>("ApplyTransformButton").IsEnabled,"Display changed transform candidate/draft or enabled stale Apply.");Reachable("CancelTransformButton");Click(Named<Button>("CancelTransformButton"));SameAccepted(before);Click(Named<Button>("ResetDisplayButton"));
        await OpenSurfacePaint();Click(Named<Button>("SurfaceMaterialButton"));Named<TextBox>("MaterialHex").Text="#5F9B64";await PaintWorld(new(6,11,20));before=editor.Snapshot;candidate=before.Preview!;await Workspace(4);ViewToggle("ViewColors",false);ViewToggle("ViewWireframe",true);ViewToggle("ViewXRay",true);Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<Button>("ApplyMaterialButton").IsEnabled&&Named<Border>("DisplayNotice").Visibility==Visibility.Visible,"Display hid/invalidated the owned paint preview.");
        Click(Named<Button>("ApplyMaterialButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,candidate.Scene),"Apply did not accept the reviewed paint with colors hidden.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Undo after display changes lost original paint.");Click(Named<Button>("ResetDisplayButton"));
        Pass("Transform drafts/owned previews survive View changes and cancel safely; paint can hide visually yet retain exact Apply/Undo semantics and visible notice");

        await OpenSurfacePaint();await Workspace(4);Click(Named<Button>("ViewTopButton"));Layout(1040,680);var point=ProjectMeasuredWorld(new(6,11,20));var expected=await (Task<Guid?>)Invoke("PickViewportObject",point)!;Assert(expected==editor.Snapshot.Scene.Objects.Single().Id,"Baseline Core adapter pick missed.");before=editor.Snapshot;
        ViewToggle("ViewGrid",false);ViewToggle("ViewColors",false);ViewToggle("ViewWireframe",true);ViewToggle("ViewXRay",true);Layout(1040,680);var actualPick=await (Task<Guid?>)Invoke("PickViewportObject",point)!;Assert(actualPick==expected,"View inspection flags changed accepted-mesh picking.");SameAccepted(before);Click(Named<Button>("ResetDisplayButton"));
        Ok(editor.Presentation.Update(editor.Presentation.Snapshot.Settings with{Colors=false}));ViewToggle("ViewWireframe",true);Assert(!editor.Presentation.Snapshot.Settings.Colors&&editor.Presentation.Snapshot.Settings.Wireframe,"One checkbox overwrote an unrelated shared presentation update.");
        var settings=editor.Presentation.Snapshot;await Open("cube-20mm.stl");Assert(editor.Presentation.Snapshot==settings,"Opening a document reset session display settings.");Click(Named<Button>("ResetDisplayButton"));
        Pass("All display combinations retain the Core ray-picking result and document state; opening another scene keeps separate session presentation settings");
    }
}
