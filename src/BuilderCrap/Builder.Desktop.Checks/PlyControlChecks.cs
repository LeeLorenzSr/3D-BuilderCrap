using Builder.Core;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private static string PlyInput(string file)=>Path.GetFullPath(Path.Combine(root,".local/evidence/ply-20261021/inputs",file));
    private static string[] PlyColors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>o.GetTriangleMaterial(i)?.Color.Hex??"unassigned")).Distinct().Order().ToArray();
    private static async Task OpenPly(string file="assembly-in-le.ply",ModelUnit unit=ModelUnit.Inches)
    {navigationDialogs.Decision=DirtyDecision.Discard;navigationDialogs.Unit=unit;navigationDialogs.OpenPath=PlyInput(file);await ChooseDocument();await Workspace(4);}
    private static async Task RunPlyControls()
    {
        await OpenPly();var imported=editor.Snapshot;
        Assert(imported.Scene.Objects.Length==1&&imported.Scene.Groups.IsEmpty&&imported.Scene.Objects.Single().Mesh.Triangles.Length==24&&imported.FilePath==null,"PLY did not retain all shells as one unsaved editable object.");Near(imported.Scene.Bounds.Size.X,50,.001);Near(imported.Scene.Bounds.Size.Y,20,.001);
        Assert(PlyColors(imported.Scene).Length==3&&!PlyColors(imported.Scene).Contains("#00FF00"),"PLY face RGB did not override vertex RGB.");
        var recent=RecentEntries().Single(i=>((Builder.Desktop.RecentFile)i.Tag).Path==PlyInput("assembly-in-le.ply"));Assert(((Builder.Desktop.RecentFile)recent.Tag).ModelUnit==ModelUnit.Inches&&recent.ToolTip!.ToString()!.Contains("Source units"),"PLY Recent omitted units.");
        var prompts=navigationDialogs.UnitPrompts;await NewDocument();await ClickRecent(RecentEntries().Single(i=>((Builder.Desktop.RecentFile)i.Tag).Path==PlyInput("assembly-in-le.ply")));Near(editor.Snapshot.Scene.Bounds.Size.X,50,.001);Assert(navigationDialogs.UnitPrompts==prompts,"Recent PLY reinterpreted source units.");
        var before=editor.Snapshot;navigationDialogs.OpenPath=PlyInput("assembly-mm.ply");navigationDialogs.Unit=null;await ChooseDocument();SameAccepted(before);
        navigationDialogs.OpenPath=PlyInput("bad-index.ply");navigationDialogs.Unit=ModelUnit.Millimeters;await ChooseDocument();SameAccepted(before);
        Pass("PLY Open keeps both colored shells in one editable object with explicit source units; Recent retains scale, cancelled units and malformed input preserve accepted work");

        await OpenPly("assembly-mm.ply",ModelUnit.Millimeters);var source=editor.Snapshot;
        navigationDialogs.OpenPath=PlyInput("hollow-mm.ply");navigationDialogs.Unit=ModelUnit.Millimeters;await (Task)Invoke("ChooseModel",true)!;await Idle();
        var inserted=editor.Snapshot;Assert(inserted.Scene.Objects.Length==2&&inserted.Scene.Objects.Last().Mesh.Triangles.Length==24,"PLY Insert did not retain the hollow mesh and destination.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,source.Scene),"PLY Insert was not one Undo step.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,inserted.Scene),"PLY Insert Redo failed.");
        navigationDialogs.SavePath=Path.Combine(output,"ply-import-native.3mf");Assert(await (Task<bool>)Invoke("Save",true)!,"PLY import native Save failed.");
        navigationDialogs.OpenPath=navigationDialogs.SavePath;await ChooseDocument();Assert(editor.Snapshot.Scene.Objects.Length==2&&editor.Snapshot.Scene.Objects.Sum(o=>o.Mesh.Triangles.Length)==48,"3MF reopen lost PLY shells.");
        Pass("PLY Insert adds one object with all cavity shells in one Undo/Redo step, and native 3MF Save/reopen retains imported objects/colors");

        await OpenObj();before=editor.Snapshot;var colors=PlyColors(before.Scene);var camera=CameraPose();var recents=RecentEntries().Select(i=>((Builder.Desktop.RecentFile)i.Tag).Path).ToArray();
        var scenePath=Path.Combine(output,"scene-cm.ply");await ExportViaDialog(false,scenePath,ModelUnit.Centimeters);SameAccepted(before);
        var reopened=new EditorSession();Ok(await reopened.OpenAsync(scenePath,ModelUnit.Centimeters));Assert(reopened.Snapshot.Scene.Objects.Length==1&&reopened.Snapshot.Scene.Groups.IsEmpty&&reopened.Snapshot.Scene.Objects.Single().Mesh.Triangles.Length==24,"Scene PLY did not deliberately flatten objects/groups into one mesh.");Near(reopened.Snapshot.Scene.Bounds.Size.X,50,.001);
        Assert(PlyColors(reopened.Snapshot.Scene).SequenceEqual(colors)&&CameraPose()==camera&&RecentEntries().Select(i=>((Builder.Desktop.RecentFile)i.Tag).Path).SequenceEqual(recents),"PLY export lost colors or changed camera/Recent.");Assert(Named<TextBlock>("Status").Text.Contains("combined into one mesh")&&Named<TextBlock>("Status").Text.Contains("3MF"),"PLY structural loss not explained.");
        Pass("Scene PLY export uses explicit units, preserves display colors and explains combined objects; native path, dirty identity, history, selection, camera and Recent stay unchanged");

        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,group.ObjectIds[0]));Refresh();before=editor.Snapshot;
        var memberPath=Path.Combine(output,"member-in.ply");await ExportViaDialog(true,memberPath,ModelUnit.Inches);SameAccepted(before);
        reopened=new EditorSession();Ok(await reopened.OpenAsync(memberPath,ModelUnit.Inches));Assert(reopened.Snapshot.Scene.Objects.Length==1&&reopened.Snapshot.Scene.Objects.Single().Mesh.Triangles.Length==12,"Member PLY selection included an unselected sibling.");Near(reopened.Snapshot.Scene.Bounds.Size.X,20,.001);
        Ok(editor.ExitGroup());Refresh();before=editor.Snapshot;Named<TextBox>("ObjectName").Text="Unapplied PLY export draft";await ExportViaDialog(false,null);SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Unapplied PLY export draft","Cancelled export picker lost idle draft.");
        var cancelled=Path.Combine(output,"cancelled.ply");await ExportViaDialog(false,cancelled,null);SameAccepted(before);Assert(!File.Exists(cancelled),"Cancelled PLY units wrote output.");
        var blocked=Path.Combine(output,"blocked.ply");Directory.CreateDirectory(blocked);await ExportViaDialog(false,blocked);SameAccepted(before);Assert(Directory.Exists(blocked),"Failed export changed destination.");
        EditorSnapshot? newer=null;navigationDialogs.DuringUnits=()=>{Ok(editor.UpdateModelMetadata(editor.Snapshot.Scene.Metadata with{Title="Newer than PLY units"},editor.Snapshot.Revision));newer=editor.Snapshot;};await ExportViaDialog(false,Path.Combine(output,"stale.ply"));navigationDialogs.DuringUnits=null;SameAccepted(newer!);Assert(!File.Exists(Path.Combine(output,"stale.ply")),"Stale PLY units exported newer work.");
        Pass("Selected-member PLY export is scope-exact; cancelled pickers/units retain drafts and failed/stale output attempts preserve accepted state");

        await OpenPly();Layout(1040,680);foreach(var name in new[]{"NewButton","RecentFilesMenu","ExportButton","SaveButton","UndoButton","RedoButton"})Reachable(name);Capture("ply-file-strip-minimum",1040,680);CaptureModelUnits(false,"ply-import-units","assembly-in-le.ply");CaptureModelUnits(true,"ply-export-units","scene.ply");
        Assert(Builder.Desktop.DocumentDialogs.ModelOpenFilter.Contains("*.ply")&&Builder.Desktop.DocumentDialogs.SceneExportFilter.Contains("*.ply")&&Builder.Desktop.DocumentDialogs.SelectionExportFilter.Contains("*.ply"),"PLY is missing from real file dialog choices.");
        await Workspace(1);Click(Named<Button>("MoveHandleButton"));before=editor.Snapshot;await ExportViaDialog(false,Path.Combine(output,"must-not-export-tool.ply"));SameAccepted(before);Assert(!File.Exists(Path.Combine(output,"must-not-export-tool.ply")),"PLY export abandoned an active tool.");Click(Named<Button>("CancelTransformButton"));
        File.WriteAllText(Path.Combine(output,"ply-export-manifest.json"),System.Text.Json.JsonSerializer.Serialize(new{method="Actual Desktop export adapter with injected path/unit dialogs; no native input",exports=new[]{new{file="scene-cm.ply",unit="Centimeters",scope="whole scene, two source objects combined",triangles=24},new{file="member-in.ply",unit="Inches",scope="one active group member",triangles=12}}},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}),Encoding.UTF8);
        Pass("PLY appears in shared format choices; minimum toolbar and units/flattening guidance remain readable, and active tools block export");
    }
}
