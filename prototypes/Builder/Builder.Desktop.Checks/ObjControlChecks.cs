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
    private static string ObjInput(string file)=>Path.GetFullPath(Path.Combine(root,".local/evidence/obj-20261021/inputs",file));
    private static async Task OpenObj()
    {navigationDialogs.Decision=DirtyDecision.Discard;navigationDialogs.Unit=ModelUnit.Inches;navigationDialogs.OpenPath=ObjInput("assembly-in.obj");await ChooseDocument();await Workspace(4);}
    private static async Task ExportViaDialog(bool selection,string? path,ModelUnit? unit=ModelUnit.Millimeters)
    {navigationDialogs.ExportPath=path;navigationDialogs.Unit=unit;await (Task)Invoke("ExportDocument",selection)!;await Idle();}
    private static void CaptureModelUnits(bool exporting,string name,string fileName="assembly-in.obj")
    {
        var dialog=new UnitsDialog(fileName,ModelUnit.Inches,exporting);var content=(FrameworkElement)dialog.Content;
        content.Measure(new Size(392,double.PositiveInfinity));content.Arrange(new Rect(new Point(),content.DesiredSize));content.UpdateLayout();
        var combo=Tree(content).OfType<ComboBox>().Single();for(var i=0;i<4;i++){combo.SelectedIndex=i;Assert((int)dialog.Unit==i,"Shared unit ordinal mapping changed.");}combo.SelectedIndex=2;
        Assert(Tree(content).OfType<TextBlock>().Any(t=>t.Text.Contains(exporting?(fileName.EndsWith(".ply",StringComparison.OrdinalIgnoreCase)?"combined into one mesh":"companion MTL"):"do not store units")),"Units guidance is missing.");
        var buttons=Tree(content).OfType<Button>().ToArray();Assert(buttons.Any(b=>b.IsCancel)&&buttons.Any(b=>b.IsDefault),"Units dialog keyboard actions missing.");
        foreach(var button in buttons){var bounds=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));Assert(bounds.Right<=392.1&&bounds.Bottom<=content.ActualHeight+.1,"Units dialog button clipped.");}
        var bitmap=new RenderTargetBitmap(392,(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);dialog.Close();
    }
    private static async Task RunObjControls()
    {
        await OpenObj();var opened=editor.Snapshot;
        Assert(opened.Scene.Objects.Length==2&&opened.Scene.Groups.Length==1&&opened.FilePath==null,"OBJ Open did not create an unsaved native scene/group.");Near(opened.Scene.Bounds.Size.X,50,.001);Near(opened.Scene.Bounds.Size.Y,20,.001);
        Assert(opened.Scene.Objects.Any(o=>!o.TriangleMaterials.IsEmpty)&&opened.Scene.Objects.Any(o=>o.Material!=null),"OBJ/MTL colors missing from imported objects.");
        var entry=RecentEntries().Single(i=>((RecentFile)i.Tag).Path==ObjInput("assembly-in.obj"));Assert(((RecentFile)entry.Tag).ModelUnit==ModelUnit.Inches,"OBJ unit not recorded in Recent.");
        var prompts=navigationDialogs.UnitPrompts;navigationDialogs.Decision=DirtyDecision.Discard;await NewDocument();await ClickRecent(RecentEntries().Single(i=>((RecentFile)i.Tag).Path==ObjInput("assembly-in.obj")));Near(editor.Snapshot.Scene.Bounds.Size.X,50,.001);Assert(navigationDialogs.UnitPrompts==prompts,"Recent OBJ forgot explicit units.");
        var before=editor.Snapshot;navigationDialogs.OpenPath=ObjInput("assembly-in.obj");navigationDialogs.Unit=null;await ChooseDocument();SameAccepted(before);
        navigationDialogs.OpenPath=ObjInput("bad-index.obj");navigationDialogs.Unit=ModelUnit.Millimeters;await ChooseDocument();SameAccepted(before);
        Pass("OBJ Open uses explicit units and MTL colors, keeps native path unsaved, and Recent remembers scale; cancelled units and invalid input preserve the scene");

        await NewDocument();Ok(editor.Execute(new AddCube()));Refresh();var source=editor.Snapshot;
        navigationDialogs.OpenPath=ObjInput("assembly-in.obj");navigationDialogs.Unit=ModelUnit.Inches;await (Task)Invoke("ChooseModel",true)!;await Idle();
        var inserted=editor.Snapshot;Assert(inserted.Scene.Objects.Length==3&&inserted.Scene.Groups.Length==1,"OBJ Insert did not retain destination/add imported group.");Near(inserted.Scene.Bounds.Size.X,50,.001);
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,source.Scene),"OBJ Insert was not one Undo step.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,inserted.Scene),"OBJ Insert Redo failed.");
        Pass("OBJ Insert uses shared units/colors and adds the assembly in one undoable transaction");

        await OpenObj();before=editor.Snapshot;var camera=CameraPose();var recents=RecentEntries().Select(i=>((RecentFile)i.Tag).Path).ToArray();var path=Path.Combine(output,"whole-scene-cm.obj");
        await ExportViaDialog(false,path,ModelUnit.Centimeters);SameAccepted(before);Assert(File.Exists(path)&&navigationDialogs.LastExportSelection==false&&navigationDialogs.LastUnitsExporting==true,"Scene OBJ export adapter or explicit units were skipped.");
        var exported=new EditorSession();Ok(await exported.OpenAsync(path,ModelUnit.Centimeters));Near(exported.Snapshot.Scene.Bounds.Size.X,50,.001);Assert(exported.Snapshot.Scene.Objects.Length==2&&exported.Snapshot.Scene.Objects.Any(o=>!o.TriangleMaterials.IsEmpty),"Scene OBJ colors/object partition did not roundtrip.");
        Assert(CameraPose()==camera&&RecentEntries().Select(i=>((RecentFile)i.Tag).Path).SequenceEqual(recents)&&Named<TextBlock>("Status").Text.Contains("MTL"),"Export changed camera/Recent or hid companion-file guidance.");
        Pass("Scene Export chooses OBJ units and writes companion colors without changing native path, dirty state, selection, history, camera or Recent");

        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,group.ObjectIds[0]));Refresh();before=editor.Snapshot;
        var selected=Path.Combine(output,"member-in.obj");await ExportViaDialog(true,selected,ModelUnit.Inches);SameAccepted(before);
        exported=new EditorSession();Ok(await exported.OpenAsync(selected,ModelUnit.Inches));Assert(exported.Snapshot.Scene.Objects.Length==1,"Scoped selection OBJ exported hidden siblings.");Near(exported.Snapshot.Scene.Bounds.Size.X,20,.001);
        await ExportViaDialog(true,Path.Combine(output,"member.3mf"));SameAccepted(before);await ExportViaDialog(true,Path.Combine(output,"member.stl"));SameAccepted(before);Assert(Named<TextBlock>("Status").Text.Contains("geometry only"),"STL material loss not explained.");
        Ok(editor.ExitGroup());Refresh();before=editor.Snapshot;var directory=Directory.GetFiles(output).Length;
        Named<TextBox>("ObjectName").Text="Unapplied export-time draft";await ExportViaDialog(false,null);SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Unapplied export-time draft","Cancelled format picker erased draft.");
        await ExportViaDialog(false,Path.Combine(output,"cancelled-units.obj"),null);SameAccepted(before);Assert(!File.Exists(Path.Combine(output,"cancelled-units.obj"))&&Directory.GetFiles(output).Length==directory,"Cancelled OBJ unit prompt wrote output.");
        var blocked=Path.Combine(output,"blocked.obj");Directory.CreateDirectory(blocked);await ExportViaDialog(false,blocked);SameAccepted(before);Assert(Directory.Exists(blocked),"Failed export replaced its blocked destination.");
        EditorSnapshot? newer=null;navigationDialogs.DuringUnits=()=>{Ok(editor.UpdateModelMetadata(editor.Snapshot.Scene.Metadata with{Title="Newer than export units"},editor.Snapshot.Revision));newer=editor.Snapshot;};
        await ExportViaDialog(false,Path.Combine(output,"stale-export.obj"));navigationDialogs.DuringUnits=null;SameAccepted(newer!);Assert(!File.Exists(Path.Combine(output,"stale-export.obj")),"Stale export unit decision wrote a newer scene.");
        Pass("Selection export supports 3MF/STL/OBJ and exact member scope; cancelled dialogs retain drafts, and stale unit choices cannot export newer accepted work");

        await Workspace(4);Layout(1040,680);foreach(var name in new[]{"NewButton","RecentFilesMenu","ExportButton","SaveButton","UndoButton","RedoButton"})Reachable(name);Capture("obj-file-strip-minimum",1040,680);CaptureModelUnits(false,"obj-import-units");CaptureModelUnits(true,"obj-export-units");
        Assert(DocumentDialogs.ModelOpenFilter.Contains("*.obj")&&DocumentDialogs.SceneExportFilter.Contains("*.obj")&&DocumentDialogs.SelectionExportFilter.Contains("*.3mf")&&DocumentDialogs.SelectionExportFilter.Contains("*.obj"),"File-format choices missing from real dialog adapter.");
        var recentPath=Path.Combine(output,"legacy-recents.json");File.WriteAllText(recentPath,System.Text.Json.JsonSerializer.Serialize(new[]{new{Path=ObjInput("assembly-in.obj"),StlUnit=2}}),Encoding.UTF8);
        var store=new JsonRecentFileStore(recentPath);Assert(store.Load().Single().ModelUnit==ModelUnit.Inches,"Legacy Recent STL unit JSON was not retained.");store.Save(store.Load());Assert(File.ReadAllText(recentPath).Contains("\"StlUnit\": 2"),"Recent schema key changed on rewrite.");
        await Workspace(1);Click(Named<Button>("MoveHandleButton"));before=editor.Snapshot;await ExportViaDialog(false,Path.Combine(output,"must-not-export-tool.obj"));SameAccepted(before);Assert(!File.Exists(Path.Combine(output,"must-not-export-tool.obj")),"Export abandoned an active tool.");Click(Named<Button>("CancelTransformButton"));
        File.WriteAllText(Path.Combine(output,"obj-export-manifest.json"),System.Text.Json.JsonSerializer.Serialize(new{
            method="Actual Desktop export adapter with injected path/unit dialogs; no native input",exports=new[]{
                new{file="whole-scene-cm.obj",unit="Centimeters",scope="scene",objects=2},
                new{file="member-in.obj",unit="Inches",scope="one active group member",objects=1},
                new{file="member.3mf",unit="Millimeters",scope="one active group member",objects=1},
                new{file="member.stl",unit="Millimeters",scope="one active group member",objects=1}}
        },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}),Encoding.UTF8);
        Pass("Minimum toolbar and shared import/export units are readable; all four unit choices map correctly, legacy Recent JSON survives and active tools block export");
    }
}
