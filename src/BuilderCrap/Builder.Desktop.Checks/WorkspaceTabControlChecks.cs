using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;

internal static partial class Program
{
    private static readonly string[] WorkspaceNames=["Insert","Object","Edit","Paint","View"];
    private static async Task Workspace(int index)
    {
        var tab=Named<TabControl>("WorkspaceTabs");var item=(TabItem)tab.Items[index];
        Assert(item.IsEnabled,"Workspace unavailable: "+WorkspaceNames[index]);
        item.IsSelected=true;Layout(1040,720);await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);Layout(1040,720);
        Assert(tab.SelectedIndex==index,"Workspace selection did not change.");
    }
    private static void Reachable(string name)
    {
        surface.UpdateLayout(); // Tool entry invalidates layout before the next rendered frame.
        var element=Named<FrameworkElement>(name);DependencyObject? node=element;
        while(node is not null&&node!=surface){if(node is UIElement ui)Assert(ui.Visibility==Visibility.Visible,"Collapsed ancestor for "+name);node=VisualTreeHelper.GetParent(node);}
        Assert(node==surface,"Control is not in the active visual tree: "+name);
        var b=Bounds(element);Assert(b.Width>0&&b.Height>0&&b.Left>=-.1&&b.Top>=-.1&&b.Right<=surface.ActualWidth+.1&&b.Bottom<=surface.ActualHeight+.1,"Unreachable/clipped control "+name+": "+b);
    }
    private static async Task WorkspaceOpen(string file="cube-20mm.stl")
    {Ok(await editor.OpenAsync(Path.Combine(root,"fixtures/generated",file),decision:DirtyDecision.Discard));Refresh();await Workspace(1);}
    private static async Task RunWorkspaceTabs()
    {
        await WorkspaceOpen();var tab=Named<TabControl>("WorkspaceTabs");Assert(tab.Items.Count==5,"Expected five workspaces.");
        var beforeFile=Path.Combine(root,".local/evidence/workspace-tabs-20261020/source-before/MainWindow.xaml");
        Assert(File.Exists(beforeFile),"Missing frozen original controls inventory.");
        var beforeXml=XDocument.Load(beforeFile);var currentXml=XDocument.Load(Path.Combine(root,"src/BuilderCrap/Builder.Desktop/MainWindow.xaml"));
        static HashSet<string> Handlers(XDocument doc)=>doc.Descendants().Attributes().Where(a=>a.Name.LocalName=="Click").Select(a=>a.Value).ToHashSet();
        var omitted=Handlers(beforeXml).Except(Handlers(currentXml)).ToArray();Assert(omitted.Length==0,"Former bound handlers omitted: "+string.Join(", ",omitted));
        Assert(Named<StackPanel>("Toolbar").IsEnabled&&Named<Button>("SaveButton").IsEnabled&&Button("_Open…").IsEnabled,"Idle file strip unexpectedly disabled Open/Save.");
        var entries=new Dictionary<int,string[]>{
            [0]=["InsertButton","PaletteCubeButton","PaletteCylinderButton","PalettePyramidButton","PaletteConeButton","PaletteSphereButton","PaletteHexagonButton","PaletteWedgeButton","PaletteTorusButton","PaletteTetrahedronButton","NewTextButton","ImageReliefButton"],
            [1]=["MoveHandleButton","RotateHandleButton","ScaleHandleButton","BedButton","DuplicateButton","DeleteButton","EditMembersButton","GroupButton","UngroupButton","AlignButton","MirrorButton","MeasureButton"],
            [2]=["SplitButton","CombineButton","RepairButton","HollowButton","SimplifyButton","SmoothButton","SeparateButton","EmbossButton","ExtrudeDownButton"],
            [3]=["MaterialButton","SurfaceMaterialButton"],
            [4]=["ViewIsometricButton","ViewTopButton","ViewFrontButton","ViewRightButton","FitSceneButton","FitSelectionButton","AboutModelButton"]};
        foreach(var pair in entries){await Workspace(pair.Key);Reachable("FileStrip");Reachable("WorkspaceTabs");Reachable(WorkspaceNames[pair.Key]+"Commands");foreach(var name in pair.Value)Reachable(name);foreach(var name in new[]{"SaveButton","SaveAsButton","ExportButton","UndoButton","RedoButton"})Reachable(name);Assert(Named<FrameworkElement>("ViewportSurface").ActualHeight>=250,"Ribbon consumed the usable viewport.");Capture("workspace-"+WorkspaceNames[pair.Key].ToLowerInvariant(),1040,720);}
        observations.Add(new {test="Original control inventory",sourceBefore=beforeFile,oldClickHandlers=Handlers(beforeXml).Order().ToArray(),currentClickHandlers=Handlers(currentXml).Order().ToArray(),renderedEntries=entries});
        Pass("All former click handlers remain bound and intended Insert/Object/Edit/Paint/View entries render through real tab navigation; persistent file/history actions remain reachable");

        Ok(editor.Execute(new AddCube()));var entryIds=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToArray();
        foreach(var entry in new[]{("SplitButton","construction"),("CombineButton","construction"),("RepairButton","construction"),("HollowButton","CancelHollowButton"),("SimplifyButton","CancelSimplifyButton"),("SmoothButton","CancelSmoothButton"),("SeparateButton","CancelSeparateButton"),("EmbossButton","CancelTextButton"),("ExtrudeDownButton","CancelExtrudeDownButton")})
        {
            await Workspace(2);Ok(editor.SelectMany(entry.Item1=="CombineButton"?entryIds:[entryIds[0]]));Refresh();var entryBefore=editor.Snapshot;
            Reachable(entry.Item1);Click(Named<Button>(entry.Item1));await Idle();var cancel=entry.Item2=="construction"?ConstructionCancel():Named<Button>(entry.Item2);
            Assert(cancel.IsEnabled,"Tool cancellation unavailable: "+entry.Item1);var cancelBounds=Bounds(cancel);Assert(cancelBounds.Height>0&&cancelBounds.Bottom<=720,"Tool footer hidden after visible Edit entry: "+entry.Item1);Click(cancel);SameAccepted(entryBefore);
        }
        Pass("Every visible Edit entry opens its real tool and reachable Cancel footer without an accepted edit; Combine uses a valid two-object selection");

        await Workspace(0);var accepted=editor.Snapshot;Click(Named<Button>("PaletteTorusButton"));Reachable("PreviewPrimitiveButton");Reachable("AddPrimitiveButton");Reachable("CancelPrimitiveButton");
        Assert(!Named<StackPanel>("Toolbar").IsEnabled&&!Button("_Open…").IsEnabled,"Active tool left file strip enabled.");
        Named<TextBox>("PrimitiveMajorRadius").Text="12";Named<TextBox>("PrimitiveTubeRadius").Text="3";
        var draft=Named<TextBox>("PrimitiveMajorRadius").Text;await Workspace(4);Assert(Named<FrameworkElement>("PrimitivePanel").Visibility==Visibility.Visible&&Named<TextBox>("PrimitiveMajorRadius").Text==draft,"View discarded creation draft.");Reachable("CancelPrimitiveButton");await Workspace(0);
        Click(Named<Button>("PreviewPrimitiveButton"));await Idle();SameAccepted(accepted);var candidate=editor.Snapshot.Preview!;await Workspace(4);Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<TextBox>("PrimitiveMajorRadius").Text==draft,"View discarded reviewed creation.");
        Assert(!((TabItem)tab.Items[1]).IsEnabled&&!((TabItem)tab.Items[2]).IsEnabled&&!((TabItem)tab.Items[3]).IsEnabled,"Unrelated workspaces enabled during creation.");
        Layout(1040,680);Reachable("PreviewPrimitiveButton");Reachable("AddPrimitiveButton");Reachable("CancelPrimitiveButton");Capture("active-creation-minimum-client",1040,680);
        Click(Named<Button>("CancelPrimitiveButton"));SameAccepted(accepted);Assert(editor.Snapshot.Preview is null,"Cancel retained candidate.");
        Pass("Minimum-size ribbon/tool footer remains reachable; compatible View preserves draft and reviewed candidate while unrelated tabs cannot discard the tool");

        await Workspace(0);Click(Named<Button>("PaletteCubeButton"));Named<TextBox>("PrimitiveSizeX").Text="30";Click(Named<Button>("PreviewPrimitiveButton"));await Idle();var addedId=editor.Snapshot.Preview!.ResultObjectIds.Single();Click(Named<Button>("AddPrimitiveButton"));var added=editor.Snapshot;
        Assert(added.Scene.Objects.Any(o=>o.Id==addedId)&&added.SelectedObjectId==addedId,"Palette did not create reviewed object.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Creation Undo failed.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,added.Scene),"Creation Redo failed.");
        var save=Path.Combine(output,"workspace-created.3mf");Ok(await editor.SaveAsync(save));var reopened=new EditorSession();Ok(await reopened.OpenAsync(save));Assert(reopened.Snapshot.Scene.Objects.Length==accepted.Scene.Objects.Length+1,"Created scene did not reopen.");
        await Workspace(1);Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="42";var beforeMove=editor.Snapshot;
        Click(Named<Button>("PreviewTransformButton"));await Idle();SameAccepted(beforeMove);await Workspace(4);Reachable("CancelTransformButton");Click(Named<Button>("CancelTransformButton"));SameAccepted(beforeMove);
        await Workspace(1);Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="42";Click(Named<Button>("PreviewTransformButton"));await Idle();Click(Named<Button>("ApplyTransformButton"));Near(editor.Snapshot.Scene.Objects.Single(o=>o.Id==addedId).Bounds.Center.X,42);Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,beforeMove.Scene),"Object edit Undo failed.");Click(Named<Button>("RedoButton"));
        Ok(await editor.SaveAsync(Path.Combine(output,"workspace-moved.3mf")));
        Pass("Visible palette creation and Object Move support Cancel/Apply, exact scene Undo/Redo and native save/reopen without a UI-only edit path");

        await WorkspaceOpen("assembly-mm.3mf");Click(Named<Button>("SelectAllButton"));Click(Named<Button>("GroupButton"));Named<TextBox>("GroupName").Text="Workspace group";Click(Named<Button>("PreviewArrangementButton"));await Idle();Click(Named<Button>("ApplyArrangementButton"));var group=editor.Snapshot.Scene.Groups.Single();
        Reachable("EditMembersButton");Click(Named<Button>("EditMembersButton"));var scoped=editor.Snapshot;Assert(scoped.EditingGroupId==group.Id,"Edit members failed.");
        Assert((bool)Invoke("NavigateObjectTree",Key.End,ModifierKeys.None)!,"Member End navigation unhandled.");await Idle();Assert(editor.Snapshot.SelectedObjectId==group.ObjectIds[^1],"End did not choose last member.");
        Assert((bool)Invoke("NavigateObjectTree",Key.Up,ModifierKeys.None)!,"Member Up navigation unhandled.");await Idle();Assert(editor.Snapshot.SelectedObjectId==group.ObjectIds[0],"Up did not continue member navigation.");Reachable("DoneEditingButton");Click(Named<Button>("DoneEditingButton"));Assert(editor.Snapshot.EditingGroupId is null&&editor.Snapshot.SelectedObjectIds.Length==2,"Done lost whole-group selection.");
        Pass("Compact object list preserves grouping, independent member navigation, reachable Done and whole-group selection");

        await WorkspaceOpen();var beforeBusy=editor.Snapshot;
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {
            var job=editor.DuplicateSelectionAsync(editor.Snapshot.SelectedObjectIds,ct.Token,new ExpandedProgress(p=>{if(p.Stage=="Ready to duplicate"){entered.Set();Assert(release.Wait(10000),"Busy barrier timed out.");}}));
            try{Assert(entered.Wait(10000),"Busy operation did not start.");Refresh();Assert(tab.Items.Cast<TabItem>().All(t=>!t.IsEnabled)&&!Named<StackPanel>("Toolbar").IsEnabled,"Workspace navigation/file strip remained enabled while busy.");}
            finally{ct.Cancel();release.Set();}
            Assert((await job).Outcome==Outcome.Cancelled,"Busy operation did not cancel.");Refresh();await Idle();SameAccepted(beforeBusy);
        }
        Pass("All workspace tabs lock during cancellable work and release without accepted-state mutation");

        await Workspace(1);Click(Named<Button>("MoveHandleButton"));var unchanged=editor.Snapshot;CheckLocalTextUndo(Named<TextBox>("PositionX"),"10","-12.375");SameAccepted(unchanged);Click(Named<Button>("CancelTransformButton"));
        Reachable("ObjectName");CheckLocalTextUndo(Named<TextBox>("ObjectName"),"Part","部品 Δ");SameAccepted(unchanged);Assert(InputMethod.GetIsInputMethodEnabled(Named<TextBox>("ObjectName")),"Name field lost IME support.");
        Pass("Local numeric/name text Undo and name IME stay independent from scene history after workspace reorganization");
    }
}
