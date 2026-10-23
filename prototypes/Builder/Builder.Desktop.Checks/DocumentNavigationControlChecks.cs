using Builder.Core;
using Builder.Desktop;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private sealed class DialogScript : IDocumentDialogs
    {
        public DirtyDecision Decision=DirtyDecision.Cancel;
        public string? OpenPath,SavePath,ExportPath;
        public ModelUnit? Unit=ModelUnit.Millimeters;
        public StlImportMode ImportStlMode=StlImportMode.Strict;
        public bool? LastImportInsert;
        public StlImportMode? LastInitialStlMode;
        public ModelUnit LastInitialUnit;
        public bool UseSuggestedUnits;
        public int UnitPrompts,DirtyPrompts;
        public Action? DuringConfirm,DuringSave,DuringUnits;
        public bool? LastExportSelection,LastUnitsExporting;
        public Action<ModelMetadataDialog>? MetadataAction;
        public Func<RecoveryDialog,Task>? RecoveryAction;
        public int RecoveryPrompts;
        public Task ShowRecoveryAsync(Window owner,RecoveryDialog dialog){RecoveryPrompts++;if(RecoveryAction is {} action)return action(dialog);dialog.Close();return Task.CompletedTask;}
        public void ShowModelMetadata(Window owner,ModelMetadataDialog dialog){if(MetadataAction is {} action)action(dialog);else dialog.Close();}
        public DirtyDecision ConfirmChanges(Window owner,EditorSnapshot snapshot,string action){DirtyPrompts++;DuringConfirm?.Invoke();return Decision;}
        public string? ChooseOpenPath(Window owner,bool insert)=>OpenPath;
        public string? ChooseSavePath(Window owner){DuringSave?.Invoke();return SavePath;}
        public ModelUnit? ChooseModelUnit(Window owner,string path,ModelUnit initialUnit,bool exporting=false){UnitPrompts++;LastInitialUnit=initialUnit;LastUnitsExporting=exporting;DuringUnits?.Invoke();return UseSuggestedUnits?initialUnit:Unit;}
        public ModelImportChoice? ChooseImportOptions(Window owner,string path,ModelUnit initialUnit,bool insert=false,StlImportMode initialStlMode=StlImportMode.Strict)
        {LastImportInsert=insert;LastInitialStlMode=initialStlMode;var unit=ChooseModelUnit(owner,path,initialUnit);return unit is {} chosen?new(chosen,ImportStlMode):null;}
        public string? ChooseExportPath(Window owner,bool selection,string suggestedName){LastExportSelection=selection;return ExportPath;}
    }
    private static readonly DialogScript navigationDialogs=new();
    private static MainWindow CreateCheckWindow()=>new(new JsonRecentFileStore(Path.Combine(output,"isolated-recents.json")),navigationDialogs);
    private static MenuItem[] RecentEntries()=>Named<MenuItem>("RecentFilesMenu").Items.OfType<MenuItem>().Where(i=>i.Tag is RecentFile).ToArray();
    private static async Task ChooseDocument()=>await (Task)Invoke("ChooseModel",false)!;
    private static async Task ClickRecent(MenuItem item){item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,item));await Idle();}
    private static string NavigationInput(string name)=>Path.GetFullPath(Path.Combine(root,".local/evidence/document-navigation-20261021/inputs",name));
    private static async Task NewDocument(){Click(Named<Button>("NewButton"));await Idle();}
    private static void CaptureSaveDialog(EditorSnapshot snapshot,string name)
    {
        var dialog=new SaveChangesDialog(snapshot,"opening another model");var content=(FrameworkElement)dialog.Content;
        content.Measure(new Size(442,double.PositiveInfinity));content.Arrange(new Rect(0,0,442,content.DesiredSize.Height));content.UpdateLayout();
        var buttons=Tree(content).OfType<Button>().ToArray();var cancel=buttons.Single(b=>b.Content?.ToString()=="Cancel");
        Assert(cancel.IsDefault&&cancel.IsCancel&&dialog.Decision==DirtyDecision.Cancel,"Prompt is not conservative for Enter/Escape/window close.");
        Assert(Tree(content).OfType<TextBlock>().All(t=>t.TextWrapping==TextWrapping.Wrap),"Prompt guidance can clip instead of wrapping.");
        Assert(buttons.Single(b=>b.Content?.ToString()=="Save").IsEnabled==!snapshot.Scene.Objects.IsEmpty,"Empty-scene prompt Save policy incorrect.");
        var image=new RenderTargetBitmap(442,(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);
    }
    private static async Task RunDocumentNavigationControls()
    {
        await Open("cube-20mm.stl");Ok(editor.Execute(new RenameObject(editor.Snapshot.SelectedObjectId!.Value,"Navigation draft")));Refresh();var before=editor.Snapshot;
        Layout(1040,680);foreach(var name in new[]{"NewButton","RecentFilesMenu","SaveButton","SaveAsButton","ExportButton","UndoButton","RedoButton"})Reachable(name);Capture("document-file-strip-minimum",1040,680);CaptureSaveDialog(before,"save-discard-cancel-dialog");
        Named<TextBox>("ObjectName").Text="Unapplied rename draft";navigationDialogs.Decision=DirtyDecision.Cancel;await NewDocument();SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Unapplied rename draft","Prompt Cancel erased idle rename draft.");
        navigationDialogs.Decision=DirtyDecision.Save;navigationDialogs.SavePath=Path.Combine(output,"saved-before-new.3mf");await NewDocument();
        Assert(editor.Snapshot.Scene.Objects.IsEmpty&&!editor.Snapshot.IsDirty&&editor.Snapshot.FilePath==null&&!editor.Snapshot.CanUndo&&!editor.Snapshot.CanRedo&&File.Exists(navigationDialogs.SavePath),"Save+New did not leave a clean empty document/reset history.");Assert(RecentEntries().Any(i=>((RecentFile)i.Tag).Path==navigationDialogs.SavePath),"Committed save was not recorded.");
        Ok(editor.Execute(new AddCube()));Refresh();navigationDialogs.Decision=DirtyDecision.Discard;await NewDocument();Assert(editor.Snapshot.Scene.Objects.IsEmpty&&!editor.Snapshot.IsDirty,"Discard+New failed.");
        Pass("New and Recent fit the minimum persistent strip; explicit wrapped Save/Discard/Cancel defaults safely, and Save+New/Discard+New reset clean document/path/history through Core");

        navigationDialogs.OpenPath=NavigationInput("colored-cube.3mf");await ChooseDocument();Assert(editor.Snapshot.Scene.Objects.Length==1&&RecentEntries().Any(i=>((RecentFile)i.Tag).Path==navigationDialogs.OpenPath),"Successful Open was not recorded.");
        navigationDialogs.OpenPath=NavigationInput("cube-20mm.stl");navigationDialogs.Unit=ModelUnit.Inches;await ChooseDocument();Near(editor.Snapshot.Scene.Bounds.Size.X,508,.001);var prompts=navigationDialogs.UnitPrompts;
        var stl=RecentEntries().Single(i=>((RecentFile)i.Tag).Path==navigationDialogs.OpenPath);Assert(((RecentFile)stl.Tag).ModelUnit==ModelUnit.Inches&&stl.ToolTip!.ToString()!.Contains(navigationDialogs.OpenPath),"Recent STL unit/full path missing.");
        await NewDocument();stl=RecentEntries().Single(i=>((RecentFile)i.Tag).Path==navigationDialogs.OpenPath);await ClickRecent(stl);Near(editor.Snapshot.Scene.Bounds.Size.X,508,.001);Assert(navigationDialogs.UnitPrompts==prompts,"Recent STL forgot explicit units and prompted/reinterpreted scale.");
        var missing=Path.Combine(output,"soon_missing_model.3mf");File.Copy(NavigationInput("colored-cube.3mf"),missing);navigationDialogs.OpenPath=missing;await ChooseDocument();File.Delete(missing);Ok(editor.Execute(new RenameObject(editor.Snapshot.SelectedObjectId!.Value,"Keep on failed Recent")));Refresh();before=editor.Snapshot;var camera=CameraPose();var order=RecentEntries().Select(i=>((RecentFile)i.Tag).Path).ToArray();await ClickRecent(RecentEntries().First());SameAccepted(before);
        Assert(CameraPose()==camera&&RecentEntries().Select(i=>((RecentFile)i.Tag).Path).SequenceEqual(order),"Missing Recent changed viewport or promoted/pruned metadata.");
        Pass("Successful Open records canonical paths; Recent reuses remembered STL units, while missing files preserve dirty document, history, camera and recent ordering");

        await Open("cube-20mm.stl");Ok(editor.Execute(new RenameObject(editor.Snapshot.SelectedObjectId!.Value,"Save before failed open")));Refresh();
        navigationDialogs.Decision=DirtyDecision.Save;navigationDialogs.SavePath=Path.Combine(output,"saved-before-failed-open.3mf");navigationDialogs.OpenPath=NavigationInput("invalid.3mf");before=editor.Snapshot;camera=CameraPose();await ChooseDocument();
        Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&!editor.Snapshot.IsDirty&&editor.Snapshot.FilePath==navigationDialogs.SavePath&&CameraPose()==camera&&File.Exists(navigationDialogs.SavePath),"Save succeeded/open failed boundary was misreported or changed scene/camera.");Assert(Named<TextBlock>("Status").Text.Contains("saved",StringComparison.OrdinalIgnoreCase)&&RecentEntries().All(i=>((RecentFile)i.Tag).Path!=navigationDialogs.OpenPath),"Failed Open was promoted or committed save omitted from feedback.");
        await Open("cube-20mm.stl");Ok(editor.Execute(new RenameObject(editor.Snapshot.SelectedObjectId!.Value,"Unsaved again")));Refresh();before=editor.Snapshot;var blocked=Path.Combine(output,"blocked.3mf");Directory.CreateDirectory(blocked);navigationDialogs.SavePath=blocked;await NewDocument();SameAccepted(before);
        navigationDialogs.SavePath=null;await NewDocument();SameAccepted(before);navigationDialogs.OpenPath=null;await ChooseDocument();SameAccepted(before);
        navigationDialogs.Decision=DirtyDecision.Discard;navigationDialogs.DuringConfirm=()=>{Ok(editor.Execute(new RenameObject(editor.Snapshot.SelectedObjectId!.Value,"Newer than prompt")));};await NewDocument();navigationDialogs.DuringConfirm=null;Assert(editor.Snapshot.Scene.Objects.Single().Name=="Newer than prompt","Stale Discard decision replaced newer accepted work.");
        navigationDialogs.SavePath=Path.Combine(output,"stale-save.3mf");navigationDialogs.DuringSave=()=>Ok(editor.Execute(new RenameObject(editor.Snapshot.SelectedObjectId!.Value,"Newer than Save dialog")));Assert(!await (Task<bool>)Invoke("Save",true)!&&!File.Exists(navigationDialogs.SavePath),"Stale Save dialog wrote newer state without its revision.");navigationDialogs.DuringSave=null;
        Pass("Failed/cancelled saves and pickers preserve accepted work; a committed Save remains honest when Open fails, and stale prompt/Save-path decisions cannot discard or write newer state");

        before=editor.Snapshot;navigationDialogs.Decision=DirtyDecision.Cancel;Assert(!await (Task<bool>)Invoke("RequestCloseDocument")!,"Close Cancel approved disposal.");SameAccepted(before);navigationDialogs.Decision=DirtyDecision.Discard;Assert(await (Task<bool>)Invoke("RequestCloseDocument")!,"Close Discard did not return typed readiness.");SameAccepted(before);
        Ok(editor.Execute(new RenameObject(editor.Snapshot.SelectedObjectId!.Value,"After close approval")));Refresh();before=editor.Snapshot;navigationDialogs.Decision=DirtyDecision.Cancel;var closing=new CancelEventArgs();typeof(MainWindow).GetMethod("OnClosing",PrivateInstance,null,[typeof(object),typeof(CancelEventArgs)],null)!.Invoke(window,[window,closing]);await Idle();Assert(closing.Cancel,"Old close approval bypassed a newer revision.");SameAccepted(before);
        Ok(await editor.DeleteSelectionAsync(editor.Snapshot.SelectedObjectIds));Refresh();before=editor.Snapshot;CaptureSaveDialog(before,"empty-dirty-save-dialog");navigationDialogs.Decision=DirtyDecision.Save;navigationDialogs.SavePath=Path.Combine(output,"must-not-save-empty.3mf");await NewDocument();SameAccepted(before);Assert(!File.Exists(navigationDialogs.SavePath)&&Named<TextBlock>("Status").Text.Contains("no objects",StringComparison.OrdinalIgnoreCase),"Empty dirty Save emitted invalid file or lost Undo guidance.");
        navigationDialogs.Decision=DirtyDecision.Discard;await NewDocument();Ok(editor.Execute(new AddCube()));Refresh();await Workspace(1);Click(Named<Button>("MoveHandleButton"));before=editor.Snapshot;var calls=navigationDialogs.DirtyPrompts;Invoke("NewClick",window,new RoutedEventArgs());await Idle();SameAccepted(before);Assert(navigationDialogs.DirtyPrompts==calls&&Named<FrameworkElement>("TransformActions").Visibility==Visibility.Visible,"New bypassed active-tool guard.");Click(Named<Button>("CancelTransformButton"));
        Pass("Close approval is revision-bound, Cancel preserves the scene, dirty-empty Save retains Undo without an invalid 3MF, and active tools cannot be abandoned by New");

        var storePath=Path.Combine(output,"adapter-recents.json");var store=new JsonRecentFileStore(storePath);var paths=Enumerable.Range(0,12).Select(i=>new RecentFile(Path.Combine(output,"folder_"+i,"same_name.stl"),ModelUnit.Centimeters)).ToArray();store.Save(new[]{paths[0],paths[0] with{Path=paths[0].Path.ToUpperInvariant()}}.Concat(paths).ToArray());var loaded=store.Load();Assert(loaded.Count==10&&loaded.Select(e=>e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==10&&loaded.All(e=>e.ModelUnit==ModelUnit.Centimeters),"Recent adapter cap/canonical dedup/unit persistence incorrect.");
        File.WriteAllText(storePath,"{ broken json",System.Text.Encoding.UTF8);var localDialogs=new DialogScript{OpenPath=NavigationInput("colored-cube.3mf")};var local=new MainWindow(store,localDialogs);Assert(((TextBlock)local.FindName("Status")).Text.Contains("could not be loaded"),"Corrupt store was not handled nonfatally.");
        var localEditor=(EditorSession)typeof(MainWindow).GetField("editor",PrivateInstance)!.GetValue(local)!;Assert(localEditor.Snapshot.Scene.Objects.IsEmpty&&File.ReadAllText(storePath).Contains("broken"),"Corrupt-store read changed document or silently rewrote metadata.");
        var failing=new MainWindow(new FailingRecentStore(),localDialogs);await (Task)typeof(MainWindow).GetMethod("ChooseModel",PrivateInstance)!.Invoke(failing,[false])!;var failingEditor=(EditorSession)typeof(MainWindow).GetField("editor",PrivateInstance)!.GetValue(failing)!;Assert(failingEditor.Snapshot.Scene.Objects.Length==1&&((TextBlock)failing.FindName("Status")).Text.Contains("could not be saved"),"Store write failure became a document failure.");
        foreach(var extra in new[]{local,failing}){var session=(EditorSession)typeof(MainWindow).GetField("editor",PrivateInstance)!.GetValue(extra)!;typeof(MainWindow).GetField("closeApproved",PrivateInstance)!.SetValue(extra,true);typeof(MainWindow).GetField("closeApprovedRevision",PrivateInstance)!.SetValue(extra,session.Snapshot.Revision);extra.Close();}
        var acceptedScene=editor.Snapshot;Invoke("ClearRecentFilesClick",window,new RoutedEventArgs());SameAccepted(acceptedScene);Assert(RecentEntries().Length==0,"Clear recent changed document or retained entries.");
        Pass("Isolated recent metadata caps/deduplicates canonical paths and remembers units; corruption/write failures are nonfatal, underscores remain literal TextBlocks, and Clear touches metadata only");
    }
    private sealed class FailingRecentStore : IRecentFileStore
    {public IReadOnlyList<RecentFile> Load()=>[];public void Save(IReadOnlyList<RecentFile> entries)=>throw new IOException("Isolated simulated write failure");}
}
