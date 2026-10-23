using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static async Task OpenExtrudeDown(string file)
    {
        Ok(await editor.OpenAsync(Path.Combine(root,".local/evidence/extrude-down-20261020/inputs",file),decision:DirtyDecision.Discard));
        Refresh();await Workspace(4);Click(Named<Button>("FitSceneButton"));await Workspace(2);
    }
    private static async Task PreviewExtrudeDown()
    {
        Click(Named<Button>("PreviewExtrudeDownButton"));await Idle();
        Assert(editor.Snapshot.Preview is {Request:ExtrudeDownRequest,ExtrudeDown:not null},"Extrude down failed: "+Named<TextBlock>("Status").Text);
    }
    private static Model3DGroup ExtrudeDownPlaneGroup()
        =>(Model3DGroup)typeof(Builder.Desktop.MainWindow).GetField("cutPlaneModels",PrivateInstance)!.GetValue(window)!;
    private static async Task RunExtrudeDownControls()
    {
        await OpenExtrudeDown("raised-box.3mf");Reachable("ExtrudeDownButton");Capture("extrude-down-edit-entry",1040,720);var before=editor.Snapshot;var source=before.Scene.Objects.Single();var originalDisplay=DrawnObject(source.Id);
        Click(Named<Button>("ExtrudeDownButton"));Assert(Named<TextBox>("ExtrudeDownPlaneHeight").Text=="0"&&!Named<Button>("ApplyExtrudeDownButton").IsEnabled,"Extrude down default/review guard incorrect.");InspectionFooterInside("PreviewExtrudeDownButton","ApplyExtrudeDownButton","CancelExtrudeDownButton");Capture("extrude-down-settings-minimum",1040,680);var camera=CameraPose();
        var plane=(MeshGeometry3D)((GeometryModel3D)ExtrudeDownPlaneGroup().Children.Single()).Geometry;Assert(plane.Positions.All(p=>p.Z==0),"Display target plane is not at Z=0.");SameAccepted(before);
        Named<TextBox>("ExtrudeDownPlaneHeight").Text="5";Click(Named<Button>("ExtrudeDownBuildPlateButton"));Assert(Named<TextBox>("ExtrudeDownPlaneHeight").Text=="0","Build plate shortcut did not reset the draft.");
        await PreviewExtrudeDown();SameAccepted(before);var candidate=editor.Snapshot.Preview!;var result=candidate.Scene.Objects.Single();var comparison=candidate.ExtrudeDown!;
        Assert(result.Id==source.Id&&result.Name==source.Name&&candidate.ResultObjectIds.SequenceEqual([source.Id])&&comparison.AddedVolumeCubicMillimeters>0&&comparison.PlaneHeightMillimeters==0,"Preview lost target or added-material metadata.");Near(result.Bounds.Min.Z,0,.001);Assert(CameraPose()==camera&&Named<TextBlock>("ExtrudeDownReport").Text.Contains("Added material"),"Preview moved camera or omitted result review.");Capture("extrude-down-result-minimum",1040,680);
        var resultDisplay=DrawnObject(source.Id);Named<CheckBox>("ExtrudeDownShowOriginal").IsChecked=true;Assert(ReferenceEquals(DrawnObject(source.Id),originalDisplay)&&ReferenceEquals(editor.Snapshot.Preview,candidate)&&CameraPose()==camera,"Original comparison changed candidate or camera.");
        Named<CheckBox>("ExtrudeDownShowOriginal").IsChecked=false;Assert(ReferenceEquals(DrawnObject(source.Id),resultDisplay),"Returning from original did not show the actual candidate.");
        Pass("Visible Edit entry and minimum-size controls preview real downward extension with a display-only green plane, build-plate shortcut and same-camera original/result review");

        Named<TextBox>("ExtrudeDownPlaneHeight").Text="NaN";Assert(!Named<Button>("ApplyExtrudeDownButton").IsEnabled,"Changed plane allowed stale Apply.");Invoke("ApplyExtrudeDownClick",window,new RoutedEventArgs());Click(Named<Button>("PreviewExtrudeDownButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview?.Id==candidate.Id,"Invalid draft erased the reviewed candidate.");
        Named<TextBox>("ExtrudeDownPlaneHeight").Text="35";Click(Named<Button>("PreviewExtrudeDownButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview?.Id==candidate.Id&&!Named<Button>("ApplyExtrudeDownButton").IsEnabled&&CameraPose()==camera,"No-added-material failure changed accepted work/camera or enabled stale Apply.");
        Named<TextBox>("ExtrudeDownPlaneHeight").Text="5";await PreviewExtrudeDown();Assert(editor.Snapshot.Preview!.Id!=candidate.Id,"Replacement did not refresh the preview token.");Near(editor.Snapshot.Preview.Scene.Objects.Single().Bounds.Min.Z,5,.001);
        Click(Named<Button>("CancelExtrudeDownButton"));SameAccepted(before);Assert(editor.Snapshot.Preview==null&&ExtrudeDownPlaneGroup().Children.Count==0&&CameraPose()==camera,"Ready Cancel left a preview/guide or changed camera.");
        Click(Named<Button>("ExtrudeDownButton"));Click(Named<Button>("PreviewExtrudeDownButton"));Assert(!Named<TabControl>("WorkspaceTabs").IsEnabled&&Named<Button>("CancelExtrudeDownButton").IsEnabled,"Busy work did not lock navigation or keep cancellation reachable.");Named<TextBox>("ExtrudeDownPlaneHeight").Text="2";await Idle();Assert(!Named<Button>("ApplyExtrudeDownButton").IsEnabled,"In-flight draft change enabled stale Apply.");Click(Named<Button>("CancelExtrudeDownButton"));SameAccepted(before);
        Click(Named<Button>("ExtrudeDownButton"));Click(Named<Button>("PreviewExtrudeDownButton"));Click(Named<Button>("CancelExtrudeDownButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview==null&&Named<FrameworkElement>("ExtrudeDownActions").Visibility==Visibility.Collapsed,"In-flight Cancel retained a candidate/tool.");
        Pass("Changed/invalid/no-op/replacement drafts cannot apply stale geometry; busy navigation locks while ready/in-flight cancellation preserves accepted work and camera");

        await OpenExtrudeDown("raised-torus.3mf");before=editor.Snapshot;source=before.Scene.Objects.Single();Click(Named<Button>("ExtrudeDownButton"));await PreviewExtrudeDown();candidate=editor.Snapshot.Preview!;result=candidate.Scene.Objects.Single();camera=CameraPose();
        await Workspace(4);Click(Named<Button>("ViewTopButton"));Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<FrameworkElement>("ExtrudeDownActions").Visibility==Visibility.Visible&&Named<TextBox>("ExtrudeDownPlaneHeight").Text=="0","Compatible View discarded extrusion candidate/draft/actions.");await Workspace(2);Capture("extrude-down-torus-top",1040,720);
        Named<CheckBox>("ExtrudeDownShowOriginal").IsChecked=true;Click(Named<Button>("ApplyExtrudeDownButton"));Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,result.Mesh)&&editor.Snapshot.SelectedObjectId==source.Id&&editor.Snapshot.Preview==null&&ExtrudeDownPlaneGroup().Children.Count==0,"Apply accepted the comparison display or left an inspection plane.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Extrude down Undo did not restore exact accepted source.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,result.Mesh),"Extrude down Redo changed reviewed geometry.");
        var saved=Path.Combine(output,"ui-extruded-torus.3mf");Ok(await editor.SaveAsync(saved));Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-extruded-torus.stl")));var reopened=new EditorSession();Ok(await reopened.OpenAsync(saved));Near(reopened.Snapshot.Scene.Objects.Single().Bounds.Min.Z,0,.001);
        Pass("Compatible camera views retain the extrusion preview; Apply accepts actual geometry with stable identity, exact Undo/Redo and shared native save/reopen");

        await OpenExtrudeDown("raised-hollow-box.3mf");var targetId=editor.Snapshot.SelectedObjectId!.Value;Ok(editor.Execute(new AddCube()));var siblingId=editor.Snapshot.SelectedObjectId!.Value;Ok(await editor.PreviewArrangementAsync(new GroupRequest([targetId,siblingId])));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();Assert(!Named<Button>("ExtrudeDownButton").IsEnabled,"Whole group enabled single-member extrusion.");
        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,targetId));Refresh();before=editor.Snapshot;var sibling=before.Scene.Objects.Single(o=>o.Id==siblingId);Click(Named<Button>("ExtrudeDownButton"));await PreviewExtrudeDown();candidate=editor.Snapshot.Preview!;
        Assert(candidate.EditingGroupId==group.Id&&candidate.ResultObjectIds.SequenceEqual([targetId])&&ReferenceEquals(candidate.Scene.Objects.Single(o=>o.Id==siblingId),sibling)&&!Named<Button>("DoneEditingButton").IsEnabled,"Scoped extrusion lost member/sibling authority or allowed Done mid-tool.");Click(Named<Button>("ApplyExtrudeDownButton"));
        Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single()==group&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Member Apply lost group/selection/scope.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Scoped Undo lost accepted member state.");Click(Named<Button>("DoneEditingButton"));
        Pass("Extrude down supports one independent leaf or explicit member, retaining group identity, full selection and sibling references through Apply/Undo/Done");

        await OpenExtrudeDown("raised-box.3mf");before=editor.Snapshot;Click(Named<Button>("ExtrudeDownButton"));await PreviewExtrudeDown();candidate=editor.Snapshot.Preview!;var field=Named<TextBox>("ExtrudeDownPlaneHeight");Assert(field.IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(field),"Plane input lost local Undo/IME.");CheckLocalTextUndo(field,"0","-2.5");SameAccepted(before);
        var draft=field.Text;await Workspace(4);Assert(field.Text==draft&&editor.Snapshot.Preview?.Id==candidate.Id,"View reset local plane text or preview.");await Workspace(2);Invoke("SmoothClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());SameAccepted(before);
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-extrude-down-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewExtrudeDownButton","ApplyExtrudeDownButton","CancelExtrudeDownButton"}){var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted extrusion footer.");}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelExtrudeDownButton").RaiseEvent(escape);Assert(escape.Handled,"Extrude down Escape not handled.");
        }
        SameAccepted(before);Assert(editor.Snapshot.Preview==null,"Escape retained extrusion candidate.");
        Pass("Plane text keeps local Undo/IME and compatible View drafts; modal guards, standard focused-button Enter and Escape preserve accepted state");
    }
}
