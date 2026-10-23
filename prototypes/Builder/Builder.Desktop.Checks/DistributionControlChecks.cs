using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static async Task OpenDistribution()
    {
        Ok(await editor.OpenAsync(Path.Combine(root,".local/fixtures/distribution/distribution-assembly.3mf"),decision:DirtyDecision.Discard));Refresh();await Workspace(4);Click(Named<Button>("FitSceneButton"));await Workspace(1);Click(Named<Button>("SelectAllButton"));
    }
    private static async Task PreviewDistribution()
    {
        Click(Named<Button>("PreviewArrangementButton"));await Idle();Assert(editor.Snapshot.Preview?.Request is DistributionRequest,"Distribution preview failed: "+Named<TextBlock>("Status").Text);
    }
    private static double DistributionComponent(Vec3 p,DistributionAxis axis)=>axis switch{DistributionAxis.X=>p.X,DistributionAxis.Y=>p.Y,_=>p.Z};
    private static void CheckDistribution(EditorSnapshot accepted,GeometryPreview preview,DistributionAxis axis,DistributionSpacing spacing)
    {
        var units=accepted.GetSelectionUnits().OrderBy(u=>DistributionComponent(u.Bounds.Center,axis)).ToArray();var after=preview.Scene.GetSelectionUnits(accepted.SelectedObjectIds,accepted.EditingGroupId).ToDictionary(u=>u.Id);
        foreach(var unit in new[]{units[0],units[^1]})foreach(var id in unit.ObjectIds)Assert(preview.Scene.Objects.Single(o=>o.Id==id).Transform==accepted.Scene.Objects.Single(o=>o.Id==id).Transform,"Distribution moved a fixed outer unit.");
        if(spacing==DistributionSpacing.Centers)
        {
            var first=DistributionComponent(units[0].Bounds.Center,axis);var step=(DistributionComponent(units[^1].Bounds.Center,axis)-first)/(units.Length-1);
            for(var i=0;i<units.Length;i++)Near(DistributionComponent(after[units[i].Id].Bounds.Center,axis),first+i*step,.001);
        }
        else
        {
            var gap=DistributionComponent(after[units[1].Id].Bounds.Min,axis)-DistributionComponent(after[units[0].Id].Bounds.Max,axis);Assert(gap>=0,"Equal gaps overlapped units.");
            for(var i=2;i<units.Length;i++)Near(DistributionComponent(after[units[i].Id].Bounds.Min,axis)-DistributionComponent(after[units[i-1].Id].Bounds.Max,axis),gap,.001);
        }
        Assert(preview.Scene.Groups.SequenceEqual(accepted.Scene.Groups),"Distribution changed group ownership.");
        foreach(var original in accepted.Scene.Objects.Where(o=>!accepted.SelectedObjectIds.Contains(o.Id)))Assert(preview.Scene.Objects.Single(o=>o.Id==original.Id)==original,"Distribution moved an unrelated object.");
        foreach(var original in accepted.Scene.Objects)
        {var result=preview.Scene.Objects.Single(o=>o.Id==original.Id);Assert(ReferenceEquals(result.Mesh,original.Mesh)&&result.Name==original.Name&&result.Material==original.Material&&ReferenceEquals(result.TriangleMaterials,original.TriangleMaterials),"Distribution changed mesh/name/material ownership.");}
        if(accepted.EditingGroupId==null)foreach(var group in accepted.Scene.Groups)
        {
            var deltas=group.ObjectIds.Select(id=>preview.Scene.Objects.Single(o=>o.Id==id).Transform.Offset-accepted.Scene.Objects.Single(o=>o.Id==id).Transform.Offset).Distinct();Assert(deltas.Count()==1,"A whole group lost internal spacing.");
        }
    }
    private static async Task RunDistributionControls()
    {
        await OpenDistribution();var accepted=editor.Snapshot;var group=accepted.Scene.Groups.Single();Assert(accepted.GetSelectionUnits().Length==3,"Fixture must have three logical selection units.");
        Ok(editor.SelectMany(group.ObjectIds));Refresh();Assert(!Named<Button>("DistributeButton").IsEnabled,"One three-leaf group counted as three independent root units.");
        var outsider=accepted.Scene.Objects.First(o=>!group.ObjectIds.Contains(o.Id));Ok(editor.SelectMany(group.ObjectIds.Add(outsider.Id)));Refresh();Assert(!Named<Button>("DistributeButton").IsEnabled,"Two canonical units enabled distribution.");Click(Named<Button>("SelectAllButton"));
        Layout(1040,680);foreach(var name in new[]{"DistributeButton","AlignButton","GroupButton","UngroupButton","DuplicateButton","DeleteButton","EditMembersButton","MirrorButton","MeasureButton"})Reachable(name);Capture("distribution-entry-minimum",1040,680);
        accepted=editor.Snapshot;Click(Named<Button>("DistributeButton"));Assert(Named<ComboBox>("DistributionAxisChoice").SelectedIndex==0&&Named<ComboBox>("DistributionSpacingChoice").SelectedIndex==0&&!Named<Button>("ApplyArrangementButton").IsEnabled,"Distribution defaults or pre-preview Apply guard differ.");
        await PreviewDistribution();var preview=editor.Snapshot.Preview!;CheckDistribution(accepted,preview,DistributionAxis.X,DistributionSpacing.Centers);ArrangementUnchanged(accepted);ArrangementFooterVisible();Reachable("DistributionAxisChoice");Reachable("DistributionSpacingChoice");Assert(Named<TextBlock>("ArrangementReview").Text.Contains("stay fixed")&&Named<TextBlock>("ArrangementReview").Text.Contains("overlap"),"Panel omits fixed endpoints/center overlap semantics.");Capture("distribution-centers-minimum",1040,680);Click(Named<Button>("CancelArrangementButton"));ArrangementUnchanged(accepted);
        Pass("Distribute is reachable beside Align at minimum width, counts canonical groups correctly, defaults X/Centers and previews fixed endpoints/rigid colored groups with a reachable Cancel footer");

        await OpenDistribution();accepted=editor.Snapshot;Click(Named<Button>("DistributeButton"));await PreviewDistribution();preview=editor.Snapshot.Preview!;Named<ComboBox>("DistributionAxisChoice").SelectedIndex=1;Named<ComboBox>("DistributionSpacingChoice").SelectedIndex=1;
        Assert(!Named<Button>("ApplyArrangementButton").IsEnabled,"Changed distribution draft can apply an older candidate.");Click(Named<Button>("PreviewArrangementButton"));await Idle();Assert(editor.Snapshot.Preview?.Id==preview.Id&&!Named<Button>("ApplyArrangementButton").IsEnabled,"Insufficient Y gaps lost prior candidate or enabled stale Apply.");ArrangementUnchanged(accepted);
        var camera=CameraPose();await Workspace(4);ViewToggle("ViewGrid",false);ViewToggle("ViewColors",false);Assert(ReferenceEquals(editor.Snapshot.Preview,preview)&&Named<ComboBox>("DistributionAxisChoice").SelectedIndex==1&&Named<ComboBox>("DistributionSpacingChoice").SelectedIndex==1&&CameraPose()==camera,"Compatible View navigation changed distribution candidate/draft/camera.");ArrangementFooterVisible();Capture("distribution-failed-gaps-view-minimum",1040,680);
        await Workspace(1);Named<ComboBox>("DistributionAxisChoice").SelectedIndex=0;Named<ComboBox>("DistributionSpacingChoice").SelectedIndex=0;await PreviewDistribution();Assert(editor.Snapshot.Preview!.Id!=preview.Id&&Named<Button>("ApplyArrangementButton").IsEnabled,"Successful replacement did not renew distribution ownership.");Click(Named<Button>("CancelArrangementButton"));ArrangementUnchanged(accepted);
        Pass("Axis/spacing changes invalidate Apply; insufficient equal-gap room retains the last candidate, compatible View preserves draft/token, and repreview renews ownership before safe Cancel");

        await OpenDistribution();accepted=editor.Snapshot;Click(Named<Button>("DistributeButton"));Named<ComboBox>("DistributionSpacingChoice").SelectedIndex=1;await PreviewDistribution();preview=editor.Snapshot.Preview!;CheckDistribution(accepted,preview,DistributionAxis.X,DistributionSpacing.Gaps);ArrangementUnchanged(accepted);Capture("distribution-equal-gaps-preview",1040,680);Click(Named<Button>("ApplyArrangementButton"));
        Assert(ReferenceEquals(editor.Snapshot.Scene,preview.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds),"Apply lost reviewed distribution or selected identities.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"One Undo did not restore exact original arrangement.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,preview.Scene),"Redo did not restore equal gaps.");
        Pass("Equal gaps accounts for unequal unit widths, preserves mesh/color/group ownership and selection, and Apply is one exact undoable/redoable edit");

        await OpenDistribution();group=editor.Snapshot.Scene.Groups.Single();Ok(editor.SelectMany(group.ObjectIds));Refresh();Click(Named<Button>("EditMembersButton"));Click(Named<Button>("SelectAllButton"));accepted=editor.Snapshot;Assert(accepted.GetSelectionUnits().Length==3&&Named<Button>("DistributeButton").IsEnabled,"Explicit member scope did not expose three independent members.");Click(Named<Button>("DistributeButton"));await PreviewDistribution();preview=editor.Snapshot.Preview!;Assert(preview.Request.MemberGroupId==group.Id,"Member distribution omitted its scope authority.");CheckDistribution(accepted,preview,DistributionAxis.X,DistributionSpacing.Centers);ArrangementFooterVisible();MemberScopeVisible();Capture("distribution-members-minimum",1040,680);
        using(var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-distribution-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewArrangementButton","ApplyArrangementButton","CancelArrangementButton","DistributionAxisChoice","DistributionSpacingChoice"})
            {var control=Named<Control>(name);var enter=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=control};control.RaiseEvent(enter);Assert(!enter.Handled&&ReferenceEquals(editor.Snapshot.Preview,preview),"Global arrangement Enter intercepted a focused control: "+name);ArrangementUnchanged(accepted);}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelArrangementButton").RaiseEvent(escape);Assert(escape.Handled,"Distribution did not handle routed Escape.");
        }
        ArrangementUnchanged(accepted);Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Preview==null,"Escape left candidate or exited member scope.");Click(Named<Button>("DistributeButton"));await PreviewDistribution();preview=editor.Snapshot.Preview!;Click(Named<Button>("ApplyArrangementButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single().ObjectIds.SequenceEqual(group.ObjectIds),"Member Apply changed scope/membership.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Member distribution Undo lost original scene/scope.");
        Pass("Edit members distributes selected leaves while retaining their group/scope; routed control Enter remains standard, Escape safely cancels, and member Apply/Undo preserves exact ownership");
    }
}
