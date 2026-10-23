using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

internal static partial class Program
{
    private static async Task OpenMaterials()
    {
        Ok(await editor.OpenAsync(Path.Combine(root,".local/evidence/paint-materials-20261021/inputs/colored-assembly.3mf"),decision:DirtyDecision.Discard));
        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.SelectMany(group.ObjectIds));Refresh();
        await Workspace(4);Click(Named<Button>("FitSceneButton"));await Workspace(3);
    }
    private static async Task PreviewMaterials()
    {Click(Named<Button>("PreviewMaterialButton"));await Idle();Assert(editor.Snapshot.Preview is {Request:MaterialRequest},"Material preview failed: "+Named<TextBlock>("Status").Text);}
    private static void MaterialColor(Guid id,RgbColor color)
    {var actual=SeparateDrawnColor(id);Assert(actual==Color.FromRgb(color.R,color.G,color.B),$"Authored RGB was tinted/replaced for {id}: {actual} vs {color.Hex}.");}
    private static async Task RunMaterialControls()
    {
        await OpenMaterials();var before=editor.Snapshot;var group=before.Scene.Groups.Single();var context=before.Scene.Objects.Single(o=>!group.ObjectIds.Contains(o.Id));
        Assert(Named<TextBlock>("MaterialSelectionSummary").Text.Contains("Mixed"),"Mixed assignment summary missing.");Reachable("MaterialButton");Capture("paint-workspace-minimum",1040,720);
        foreach(var obj in before.Scene.Objects.Where(o=>o.Material!=null))MaterialColor(obj.Id,obj.Material!.Color);
        Assert(Named<Canvas>("MaterialSelectionOverlay").Children.Count>0,"Painted selection has no separate outline.");
        Click(Named<Button>("MaterialButton"));InspectionFooterInside("PreviewMaterialButton","ApplyMaterialButton","CancelMaterialButton");Capture("material-settings-minimum",1040,680);
        var swatches=ObjectTreeVisuals(Named<ItemsControl>("MaterialPalette")).OfType<Button>().Where(b=>b.Tag is string s&&s.StartsWith('#')).ToArray();
        Assert(swatches.Length==12&&swatches.All(b=>!string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))&&b.ToolTip!=null),"Palette lacks readable accessible swatches.");
        Click(swatches.Single(b=>(string)b.Tag=="#5F9B64"));Named<TextBox>("MaterialName").Text="Leaf green";var camera=CameraPose();await PreviewMaterials();SameAccepted(before);var candidate=editor.Snapshot.Preview!;
        foreach(var id in group.ObjectIds)MaterialColor(id,new(95,155,100));Assert(ReferenceEquals(candidate.Scene.Objects.Single(o=>o.Id==context.Id),context),"Painting group changed unrelated object.");
        Capture("material-preview-minimum",1040,680);Named<CheckBox>("MaterialShowOriginal").IsChecked=true;
        foreach(var obj in before.Scene.Objects.Where(o=>o.Material!=null))MaterialColor(obj.Id,obj.Material!.Color);
        Assert(CameraPose()==camera&&ReferenceEquals(editor.Snapshot.Preview,candidate),"Original comparison changed camera or candidate.");Named<CheckBox>("MaterialShowOriginal").IsChecked=false;
        await Workspace(4);Click(Named<Button>("ViewTopButton"));Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<FrameworkElement>("MaterialActions").Visibility==Visibility.Visible,"View discarded paint review.");await Workspace(3);
        Assert(((TabItem)Named<TabControl>("WorkspaceTabs").Items[0]).IsEnabled==false,"Unrelated workspace allowed during paint.");Click(Named<Button>("CancelMaterialButton"));SameAccepted(before);
        Pass("Paint workspace and accessible swatches fit the minimum window; actual RGB survives selection, and same-camera original/result plus compatible View preserve the private candidate");

        Click(Named<Button>("MaterialButton"));Named<TextBox>("MaterialHex").Text="#12ABEF";Named<TextBox>("MaterialName").Text="";await PreviewMaterials();candidate=editor.Snapshot.Preview!;
        Assert(((MaterialRequest)candidate.Request).Material!.Name=="Color #12ABEF","Blank name did not get a generated color name.");
        Named<TextBox>("MaterialHex").Text="#bad";Assert(!Named<Button>("ApplyMaterialButton").IsEnabled,"Invalid changed draft could apply stale candidate.");Invoke("ApplyMaterialClick",window,new RoutedEventArgs());Click(Named<Button>("PreviewMaterialButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview?.Id==candidate.Id,"Invalid RGB erased prior review.");
        Named<TextBox>("MaterialHex").Text="#123456";await PreviewMaterials();Assert(editor.Snapshot.Preview!.Id!=candidate.Id,"Replacement reused the preview token.");
        Click(Named<Button>("ClearMaterialButton"));Assert(!Named<Button>("ApplyMaterialButton").IsEnabled&&Named<TextBlock>("MaterialClearNotice").Visibility==Visibility.Visible,"Clear did not invalidate review.");await PreviewMaterials();Assert(((MaterialRequest)editor.Snapshot.Preview!.Request).Material==null,"Clear did not preview null assignment.");Click(Named<Button>("CancelMaterialButton"));SameAccepted(before);
        Pass("Hex/name drafts generate readable defaults, reject invalid or stale Apply, replace exact preview tokens, and clear/cancel without changing accepted work");

        Click(Named<Button>("MaterialButton"));Named<TextBox>("MaterialHex").Text="#9845CB";Named<TextBox>("MaterialName").Text="紫 Δ";await PreviewMaterials();candidate=editor.Snapshot.Preview!;var reviewed=candidate.Scene;
        Named<CheckBox>("MaterialShowOriginal").IsChecked=true;Click(Named<Button>("ApplyMaterialButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,reviewed)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Apply accepted display/original instead of reviewed material or changed selection.");
        foreach(var id in group.ObjectIds)MaterialColor(id,new(152,69,203));Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Material Undo did not restore exact accepted scene.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,reviewed),"Material Redo did not restore exact candidate.");
        Ok(editor.SelectMany([context.Id]));Refresh();foreach(var id in group.ObjectIds)MaterialColor(id,new(152,69,203));
        Ok(editor.SelectMany(group.ObjectIds));Refresh();await Workspace(1);Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="25";Click(Named<Button>("PreviewTransformButton"));await Idle();foreach(var id in group.ObjectIds)MaterialColor(id,new(152,69,203));Click(Named<Button>("CancelTransformButton"));
        var saved=Path.Combine(output,"painted-assembly.3mf");Ok(await editor.SaveAsync(saved));var reopened=new EditorSession();Ok(await reopened.OpenAsync(saved));Assert(reopened.Snapshot.Scene.Objects.Count(o=>o.Material?.Name=="紫 Δ")==2,"3MF lost named object colors.");
        var stl=Path.Combine(output,"painted-selection.stl");Ok(await (Task<OperationResult>)Invoke("ExportSelectionTo",stl,editor.Snapshot)!);Assert(File.Exists(stl)&&Named<TextBlock>("Status").Text.Contains("geometry only"),"STL adapter did not explain material loss.");
        Pass("Apply/Undo/Redo retain exact material scenes and full selection; unselected and transformed objects keep RGB, native 3MF reopens names/colors, and STL export explicitly reports geometry only");

        await OpenMaterials();group=editor.Snapshot.Scene.Groups.Single();var member=group.ObjectIds[0];var sibling=editor.Snapshot.Scene.Objects.Single(o=>o.Id==group.ObjectIds[1]);Ok(editor.EnterGroup(group.Id,member));Refresh();before=editor.Snapshot;
        Click(Named<Button>("MaterialButton"));Named<TextBox>("MaterialHex").Text="#E5C44D";Named<TextBox>("MaterialName").Text="Yellow member";await PreviewMaterials();candidate=editor.Snapshot.Preview!;
        Assert(candidate.EditingGroupId==group.Id&&((MaterialRequest)candidate.Request).MemberGroupId==group.Id&&candidate.ResultObjectIds.SequenceEqual([member])&&ReferenceEquals(candidate.Scene.Objects.Single(o=>o.Id==sibling.Id),sibling),"Scoped paint expanded to sibling or lost authority.");Assert(!Named<Button>("DoneEditingButton").IsEnabled,"Member Done allowed during paint.");
        Click(Named<Button>("ApplyMaterialButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single()==group&&editor.Snapshot.SelectedObjectIds.SequenceEqual([member]),"Member Apply lost membership or selection.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Scoped paint Undo lost accepted state.");Click(Named<Button>("DoneEditingButton"));
        Ok(editor.SelectMany(editor.Snapshot.Scene.Objects.Select(o=>o.Id)));Refresh();await Workspace(3);before=editor.Snapshot;Click(Named<Button>("MaterialButton"));Named<TextBox>("MaterialHex").Text="#D65248";await PreviewMaterials();Assert(((MaterialRequest)editor.Snapshot.Preview!.Request).ObjectIds.Length==3,"Multi/group selection did not canonicalize to all leaves.");Click(Named<Button>("CancelMaterialButton"));SameAccepted(before);
        Pass("Whole groups, mixed multi-selection and explicit members use shared scoped requests; siblings, group identity, selection and exact Undo remain intact");

        await OpenMaterials();group=editor.Snapshot.Scene.Groups.Single();before=editor.Snapshot;
        Click(Named<Button>("MaterialButton"));Click(Named<Button>("ClearMaterialButton"));await PreviewMaterials();Click(Named<Button>("ApplyMaterialButton"));
        Assert(editor.Snapshot.Scene.Objects.Where(o=>group.ObjectIds.Contains(o.Id)).All(o=>o.Material==null)&&editor.Snapshot.Scene.Objects.All(o=>ReferenceEquals(o.Mesh,before.Scene.Objects.Single(old=>old.Id==o.Id).Mesh)),"Clear Apply changed geometry or kept assigned materials.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Clear Undo did not restore named colors.");
        context=editor.Snapshot.Scene.Objects.Single(o=>!group.ObjectIds.Contains(o.Id));Ok(editor.SelectMany([context.Id]));Refresh();before=editor.Snapshot;Click(Named<Button>("MaterialButton"));Click(Named<Button>("ClearMaterialButton"));await PreviewMaterials();Click(Named<Button>("ApplyMaterialButton"));SameAccepted(before);Assert(editor.Snapshot.Preview==null,"No-op clear retained a preview.");
        Click(Named<Button>("MaterialButton"));CheckLocalTextUndo(Named<TextBox>("MaterialHex"),"#267CB5","#112233");CheckLocalTextUndo(Named<TextBox>("MaterialName"),"Blue","青 Δ");Assert(InputMethod.GetIsInputMethodEnabled(Named<TextBox>("MaterialName"))&&Named<TextBox>("MaterialName").IsUndoEnabled,"Material name lost IME or local Undo.");
        await PreviewMaterials();candidate=editor.Snapshot.Preview!;using(var keySource=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-material-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewMaterialButton","ApplyMaterialButton","CancelMaterialButton"}){var key=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted focused material footer.");}
            Invoke("UndoClick",window,new RoutedEventArgs());Invoke("MirrorClick",window,new RoutedEventArgs());SameAccepted(before);Assert(editor.Snapshot.Preview?.Id==candidate.Id,"Other modal/history action replaced material candidate.");
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelMaterialButton").RaiseEvent(escape);Assert(escape.Handled,"Escape did not cancel Paint.");
        }
        SameAccepted(before);Assert(editor.Snapshot.Preview==null,"Escape retained material candidate.");
        Pass("Clear Apply/Undo restores named colors; no-op clear stays history-neutral, material text retains local Undo/IME, and footer Enter, Escape, history/tool guards preserve accepted state");
    }
}
