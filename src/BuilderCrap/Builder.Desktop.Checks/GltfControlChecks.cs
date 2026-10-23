using Builder.Core;
using Builder.Desktop;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static string GltfInput(string file)=>Path.GetFullPath(Path.Combine(root,".local/evidence/gltf-20261021/inputs",file));
    private static async Task OpenGltf(string file="assembly.gltf",bool suggested=true,ModelUnit? unit=ModelUnit.Meters)
    {navigationDialogs.Decision=DirtyDecision.Discard;navigationDialogs.UseSuggestedUnits=suggested;navigationDialogs.Unit=unit;navigationDialogs.OpenPath=GltfInput(file);await ChooseDocument();await Workspace(4);}
    private static void GltfAssembly(EditorSnapshot snapshot)
    {Assert(snapshot.Scene.Objects.Length==2&&snapshot.Scene.Groups.Length==1&&snapshot.Scene.Objects.Sum(o=>o.Mesh.Triangles.Length)==24,"glTF assembly structure/triangles were lost.");Near(snapshot.Scene.Bounds.Size.X,50,.001);Near(snapshot.Scene.Bounds.Size.Y,20,.001);Near(snapshot.Scene.Bounds.Size.Z,20,.001);Assert(PlyColors(snapshot.Scene).Length==3,"glTF display colors were lost.");}
    private static void CaptureGltfUnits(bool exporting,string fileName,string name)
    {
        var dialog=new UnitsDialog(fileName,exporting:exporting);Assert(dialog.Unit==ModelUnit.Meters,"glTF dialog did not default to meters.");var content=(FrameworkElement)dialog.Content;
        content.Measure(new Size(392,double.PositiveInfinity));content.Arrange(new Rect(new Point(),content.DesiredSize));content.UpdateLayout();
        var guidance=Tree(content).OfType<TextBlock>().Select(t=>t.Text).Single(t=>t.Contains("Y-up"));Assert(guidance.Contains("meters")&&guidance.Contains(exporting?"physical size":"flat face colors"),"glTF unit guidance incorrect.");
        var units=Tree(content).OfType<ComboBox>().Single();for(var i=0;i<4;i++){units.SelectedIndex=i;Assert((int)dialog.Unit==i,"glTF units lost explicit override choices.");}units.SelectedIndex=3;
        foreach(var button in Tree(content).OfType<Button>()){var bounds=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));Assert(bounds.Right<=392.1&&bounds.Bottom<=content.ActualHeight+.1,"glTF unit button clipped.");}
        var bitmap=new RenderTargetBitmap(392,(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);dialog.Close();
    }
    private static async Task RunGltfControls()
    {
        await OpenGltf();GltfAssembly(editor.Snapshot);Assert(navigationDialogs.LastInitialUnit==ModelUnit.Meters&&editor.Snapshot.FilePath==null,"glTF import did not recommend meters/retain unsaved native path.");
        await OpenGltf("assembly.glb");GltfAssembly(editor.Snapshot);Assert(navigationDialogs.LastInitialUnit==ModelUnit.Meters,"GLB import did not recommend meters.");
        Pass("Both glTF and GLB Open recommend standard meters and retain physical dimensions, editable assembly leaves/groups and opaque display colors");

        await OpenGltf("raw-millimeters.gltf",false,ModelUnit.Millimeters);Near(editor.Snapshot.Scene.Bounds.Size.X,20,.001);Assert(navigationDialogs.LastInitialUnit==ModelUnit.Meters,"New nonstandard source did not begin with standard-meter recommendation.");
        var entry=RecentEntries().Single(i=>((RecentFile)i.Tag).Path==GltfInput("raw-millimeters.gltf"));Assert(((RecentFile)entry.Tag).ModelUnit==ModelUnit.Millimeters,"Nonstandard unit choice was not recorded.");
        var prompts=navigationDialogs.UnitPrompts;await NewDocument();await ClickRecent(RecentEntries().Single(i=>((RecentFile)i.Tag).Path==GltfInput("raw-millimeters.gltf")));Near(editor.Snapshot.Scene.Bounds.Size.X,20,.001);Assert(navigationDialogs.UnitPrompts==prompts,"Recent forgot explicit nonstandard source units.");
        await OpenGltf("raw-millimeters.gltf",false,ModelUnit.Millimeters);Assert(navigationDialogs.LastInitialUnit==ModelUnit.Millimeters,"Regular picker did not reuse remembered source units.");var before=editor.Snapshot;
        await OpenGltf("assembly.gltf",false,null);SameAccepted(before);await OpenGltf("bad-accessor.gltf");SameAccepted(before);await OpenGltf("truncated.glb");SameAccepted(before);
        Pass("Explicit nonstandard import units persist in Recent and picker defaults; cancelled units and malformed glTF/GLB preserve accepted work");

        await OpenGltf();var source=editor.Snapshot;navigationDialogs.OpenPath=GltfInput("hollow.glb");navigationDialogs.UseSuggestedUnits=true;await (Task)Invoke("ChooseModel",true)!;await Idle();var inserted=editor.Snapshot;
        Assert(inserted.Scene.Objects.Length==3&&inserted.Scene.Objects.Last().Mesh.Triangles.Length==24&&navigationDialogs.LastInitialUnit==ModelUnit.Meters,"GLB Insert lost cavity shells or default units.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,source.Scene),"GLB Insert was not one Undo step.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,inserted.Scene),"GLB Insert Redo failed.");
        navigationDialogs.SavePath=Path.Combine(output,"gltf-import-native.3mf");Assert(await (Task<bool>)Invoke("Save",true)!,"glTF import native save failed.");navigationDialogs.OpenPath=navigationDialogs.SavePath;await ChooseDocument();Assert(editor.Snapshot.Scene.Objects.Length==3&&editor.Snapshot.Scene.Groups.Length==1&&editor.Snapshot.Scene.Objects.Sum(o=>o.Mesh.Triangles.Length)==48,"Native save/reopen lost imported assembly/cavity shells.");
        Pass("GLB Insert retains all cavity shells and assembly context in one Undo/Redo step, then native 3MF Save/reopen retains objects/groups/colors");

        await OpenGltf();before=editor.Snapshot;var colors=PlyColors(before.Scene);var recents=RecentEntries().Select(i=>((RecentFile)i.Tag).Path).ToArray();var camera=CameraPose();
        var gltf=Path.Combine(output,"scene-mm.gltf");navigationDialogs.UseSuggestedUnits=false;await ExportViaDialog(false,gltf,ModelUnit.Millimeters);SameAccepted(before);Assert(navigationDialogs.LastInitialUnit==ModelUnit.Meters,"glTF export did not default coordinate units to meters.");
        var reopened=new EditorSession();Ok(await reopened.OpenAsync(gltf,ModelUnit.Meters));GltfAssembly(reopened.Snapshot);Assert(PlyColors(reopened.Snapshot.Scene).SequenceEqual(colors),"glTF scene colors changed.");
        var glb=Path.Combine(output,"scene-m.glb");navigationDialogs.UseSuggestedUnits=true;await ExportViaDialog(false,glb);SameAccepted(before);Assert(navigationDialogs.LastInitialUnit==ModelUnit.Meters,"GLB export default was not meters.");reopened=new EditorSession();Ok(await reopened.OpenAsync(glb,ModelUnit.Meters));GltfAssembly(reopened.Snapshot);
        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,group.ObjectIds[0]));Refresh();before=editor.Snapshot;var member=Path.Combine(output,"member-in.glb");navigationDialogs.UseSuggestedUnits=false;await ExportViaDialog(true,member,ModelUnit.Inches);SameAccepted(before);reopened=new EditorSession();Ok(await reopened.OpenAsync(member,ModelUnit.Meters));Assert(reopened.Snapshot.Scene.Objects.Length==1,"Member GLB exported an unselected sibling.");Near(reopened.Snapshot.Scene.Bounds.Size.X,20,.001);
        Assert(CameraPose()==camera&&RecentEntries().Select(i=>((RecentFile)i.Tag).Path).SequenceEqual(recents)&&Named<TextBlock>("Status").Text.Contains("standard meters"),"glTF export changed camera/Recent or omitted physical-size guidance.");
        Pass("Scene glTF/GLB and exact-member GLB exports preserve standard-meter physical size for mm/m/in coordinate choices without changing accepted document/history/path/Recent");

        Ok(editor.ExitGroup());Refresh();before=editor.Snapshot;Named<TextBox>("ObjectName").Text="Unapplied glTF export draft";await ExportViaDialog(false,null);SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Unapplied glTF export draft","Cancelled picker erased idle draft.");await ExportViaDialog(false,Path.Combine(output,"cancelled.glb"),null);SameAccepted(before);Assert(!File.Exists(Path.Combine(output,"cancelled.glb")),"Cancelled export units wrote a GLB.");
        EditorSnapshot? newer=null;navigationDialogs.DuringUnits=()=>{Ok(editor.UpdateModelMetadata(editor.Snapshot.Scene.Metadata with{Title="Newer than glTF units"},editor.Snapshot.Revision));newer=editor.Snapshot;};await ExportViaDialog(false,Path.Combine(output,"stale.gltf"));navigationDialogs.DuringUnits=null;SameAccepted(newer!);Assert(!File.Exists(Path.Combine(output,"stale.gltf")),"Stale units exported newer work.");
        await OpenGltf();Layout(1040,680);foreach(var name in new[]{"NewButton","RecentFilesMenu","ExportButton","SaveButton","UndoButton","RedoButton"})Reachable(name);Capture("gltf-file-strip-minimum",1040,680);CaptureGltfUnits(false,"assembly.glb","gltf-import-units");CaptureGltfUnits(true,"scene.gltf","gltf-export-units");
        Assert(DocumentDialogs.ModelOpenFilter.Contains("*.gltf")&&DocumentDialogs.ModelOpenFilter.Contains("*.glb")&&DocumentDialogs.SceneExportFilter.Contains("*.gltf")&&DocumentDialogs.SelectionExportFilter.Contains("*.glb"),"glTF/GLB missing from actual file filters.");
        await Workspace(1);Click(Named<Button>("MoveHandleButton"));before=editor.Snapshot;await ExportViaDialog(false,Path.Combine(output,"must-not-export-tool.glb"));SameAccepted(before);Assert(!File.Exists(Path.Combine(output,"must-not-export-tool.glb")),"glTF export abandoned active tool.");Click(Named<Button>("CancelTransformButton"));navigationDialogs.UseSuggestedUnits=false;
        File.WriteAllText(Path.Combine(output,"gltf-export-manifest.json"),System.Text.Json.JsonSerializer.Serialize(new{method="Actual Desktop adapter with injected path/unit dialogs; no native input",exports=new[]{new{file="scene-mm.gltf",coordinateUnit="Millimeters",reopenUnit="Meters",objects=2},new{file="scene-m.glb",coordinateUnit="Meters",reopenUnit="Meters",objects=2},new{file="member-in.glb",coordinateUnit="Inches",reopenUnit="Meters",objects=1}}},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}),Encoding.UTF8);
        Pass("Cancelled/stale glTF choices preserve drafts/state, active tools block export, and minimum toolbar plus shared meter/Y-up guidance remain readable");
    }
}
