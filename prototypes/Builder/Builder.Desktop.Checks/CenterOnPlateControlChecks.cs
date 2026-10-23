using Builder.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static async Task PreviewCenterOnPlate()
    {Click(Named<Button>("PreviewArrangementButton"));await Idle();Assert(editor.Snapshot.Preview?.Request is CenterOnPlateRequest,"Center preview failed: "+Named<TextBlock>("Status").Text);}
    private static async Task ElevateCenterSelection(double z)
    {
        var before=editor.Snapshot;Ok(await editor.PreviewTransformAsync(new MoveTransform(before.SelectedObjectId!.Value,new(0,0,z)){ObjectIds=before.SelectedObjectIds,MemberGroupId=before.EditingGroupId}));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id,before.Revision));Refresh();
    }
    private static void CheckCenterOnPlate(EditorSnapshot accepted,GeometryPreview preview,bool bed)
    {
        var units=accepted.GetSelectionUnits();var bounds=Bounds3.From(units.SelectMany(u=>new[]{u.Bounds.Min,u.Bounds.Max}));var delta=new Vec3(-bounds.Center.X,-bounds.Center.Y,bed?-bounds.Min.Z:0);var ids=accepted.SelectedObjectIds.ToHashSet();
        foreach(var original in accepted.Scene.Objects)
        {
            var result=preview.Scene.Objects.Single(o=>o.Id==original.Id);
            if(!ids.Contains(original.Id)){Assert(result==original,"Center moved an unselected sibling/object.");continue;}
            Assert(result.Transform==original.Transform.Then(Affine.Translation(delta))&&ReferenceEquals(result.Mesh,original.Mesh)&&result.Name==original.Name&&result.Material==original.Material&&ReferenceEquals(result.TriangleMaterials,original.TriangleMaterials),"Center changed rigid offsets, identity or mesh/material ownership.");
        }
        var after=Bounds3.From(preview.Scene.GetSelectionUnits(accepted.SelectedObjectIds,accepted.EditingGroupId).SelectMany(u=>new[]{u.Bounds.Min,u.Bounds.Max}));Near(after.Center.X,0,.001);Near(after.Center.Y,0,.001);Near(after.Min.Z,bed?0:bounds.Min.Z,.001);
        Assert(preview.Scene.Groups.SequenceEqual(accepted.Scene.Groups)&&preview.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds)&&preview.EditingGroupId==accepted.EditingGroupId,"Center changed groups, selection or member scope.");
    }
    private static async Task RunCenterOnPlateControls()
    {
        await OpenDistribution();Click(Named<Button>("ClearSelectionButton"));Assert(!Named<Button>("CenterOnPlateButton").IsEnabled,"Empty selection enables Center.");Ok(editor.Select(editor.Snapshot.Scene.Objects.First().Id));Refresh();Assert(Named<Button>("CenterOnPlateButton").IsEnabled,"Single unit cannot center.");Click(Named<Button>("SelectAllButton"));Layout(1040,680);
        foreach(var name in new[]{"CenterOnPlateButton","BedButton","MoveHandleButton","ArrayButton","DuplicateButton","DistributeButton","MirrorButton","MeasureButton"})Reachable(name);Assert(Named<FrameworkElement>("ViewportSurface").ActualHeight>=250,"Center entry consumed the minimum viewport.");Capture("center-entry-minimum",1040,680);
        var accepted=editor.Snapshot;Click(Named<Button>("CenterOnPlateButton"));Assert(Named<CheckBox>("CenterPlaceOnBed").IsChecked==false&&!Named<Button>("ApplyArrangementButton").IsEnabled,"Center default unexpectedly places on bed or allows unreviewed Apply.");var camera=CameraPose();await PreviewCenterOnPlate();var preview=editor.Snapshot.Preview!;CheckCenterOnPlate(accepted,preview,false);ArrangementUnchanged(accepted);Assert(CameraPose()==camera,"Center preview reframed the camera.");ArrangementFooterVisible();Reachable("CenterPlaceOnBed");Assert(Named<TextBlock>("ArrangementReview").Text.Contains("world X=0")&&Named<TextBlock>("ArrangementReview").Text.Contains("Z stays unchanged"),"Center panel implies a finite visible grid/unspecified Z target.");Capture("center-default-preview-minimum",1040,680);Click(Named<Button>("CancelArrangementButton"));ArrangementUnchanged(accepted);
        Pass("Center is reachable near Place on bed at minimum width, gates empty selection, defaults to worldXY0 with Z unchanged, and previews the rigid set without camera or accepted-state edits");

        await OpenDistribution();await ElevateCenterSelection(12);accepted=editor.Snapshot;Click(Named<Button>("CenterOnPlateButton"));await PreviewCenterOnPlate();preview=editor.Snapshot.Preview!;CheckCenterOnPlate(accepted,preview,false);Named<CheckBox>("CenterPlaceOnBed").IsChecked=true;Assert(!Named<Button>("ApplyArrangementButton").IsEnabled&&ReferenceEquals(editor.Snapshot.Preview,preview),"Bed option did not invalidate the reviewed candidate.");camera=CameraPose();await Workspace(4);ViewToggle("ViewGrid",false);Assert(ReferenceEquals(editor.Snapshot.Preview,preview)&&Named<CheckBox>("CenterPlaceOnBed").IsChecked==true&&CameraPose()==camera,"View abandoned the center draft/candidate or changed camera.");ArrangementFooterVisible();Capture("center-changed-bed-view-minimum",1040,680);
        await Workspace(1);await PreviewCenterOnPlate();var replacement=editor.Snapshot.Preview!;Assert(replacement.Id!=preview.Id&&Named<Button>("ApplyArrangementButton").IsEnabled,"Center repreview failed to renew ownership.");CheckCenterOnPlate(accepted,replacement,true);ArrangementUnchanged(accepted);Click(Named<Button>("CancelArrangementButton"));ArrangementUnchanged(accepted);
        Pass("Place on bed moves the entire elevated set's lowest point to Z0; changing it retains prior preview but disables Apply, View preserves the draft/token and explicit repreview/cancel remain safe");

        await OpenDistribution();var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.SelectMany(group.ObjectIds));Refresh();await ElevateCenterSelection(12);accepted=editor.Snapshot;Click(Named<Button>("CenterOnPlateButton"));Named<CheckBox>("CenterPlaceOnBed").IsChecked=true;await PreviewCenterOnPlate();preview=editor.Snapshot.Preview!;CheckCenterOnPlate(accepted,preview,true);Capture("center-group-and-bed-preview",1040,680);Click(Named<Button>("ApplyArrangementButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,preview.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds),"Center Apply lost exact scene or selected identities.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"One Undo did not restore raised group scene.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,preview.Scene),"Redo did not restore centered group.");
        var centered=editor.Snapshot;Click(Named<Button>("CenterOnPlateButton"));await PreviewCenterOnPlate();Assert(ReferenceEquals(editor.Snapshot.Preview!.Scene,centered.Scene),"Already-centered selection fabricated a new scene.");Click(Named<Button>("ApplyArrangementButton"));ArrangementUnchanged(centered);Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"No-op center added an Undo step.");
        Pass("Selected groups center rigidly with materials/IDs intact while unselected objects stay fixed; bed placement, exact Apply/Undo/Redo and already-centered no-op history remain coherent");

        await OpenDistribution();group=editor.Snapshot.Scene.Groups.Single();Ok(editor.SelectMany(group.ObjectIds));Refresh();Click(Named<Button>("EditMembersButton"));Ok(editor.SelectMany(group.ObjectIds.Take(2)));Refresh();accepted=editor.Snapshot;Assert(Named<Button>("CenterOnPlateButton").IsEnabled,"Selected members cannot center.");Click(Named<Button>("CenterOnPlateButton"));await PreviewCenterOnPlate();preview=editor.Snapshot.Preview!;Assert(preview.Request.MemberGroupId==group.Id,"Center omitted explicit member authority.");CheckCenterOnPlate(accepted,preview,false);ArrangementFooterVisible();MemberScopeVisible();Capture("center-members-minimum",1040,680);
        using(var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-center-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewArrangementButton","ApplyArrangementButton","CancelArrangementButton","CenterPlaceOnBed"})
            {var control=Named<Control>(name);var enter=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=control};control.RaiseEvent(enter);Assert(!enter.Handled&&ReferenceEquals(editor.Snapshot.Preview,preview),"Global Enter intercepted Center control: "+name);ArrangementUnchanged(accepted);}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelArrangementButton").RaiseEvent(escape);Assert(escape.Handled,"Center did not handle Escape.");
        }
        ArrangementUnchanged(accepted);Click(Named<Button>("CenterOnPlateButton"));await PreviewCenterOnPlate();preview=editor.Snapshot.Preview!;Click(Named<Button>("ApplyArrangementButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single().ObjectIds.SequenceEqual(group.ObjectIds),"Member center changed group/scope.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds),"Member center Undo lost exact scene/scope/selection.");
        Pass("Explicit members center as one selected set without moving siblings or leaving their group; routed control Enter/Escape and member Apply/Undo preserve scope and ownership");
    }
}
