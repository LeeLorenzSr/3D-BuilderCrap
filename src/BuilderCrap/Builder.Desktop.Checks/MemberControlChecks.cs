using Builder.Core;
using Builder.Desktop;
using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static void MemberScopeVisible()
    {
        Layout(1040,680);
        var banner=Named<FrameworkElement>("MemberScopeBar");
        var done=Named<Button>("DoneEditingButton");
        Assert(banner.Visibility==Visibility.Visible,"Member scope banner is hidden.");
        foreach(var control in new[]{banner,done})
        {
            var bounds=Bounds(control);
            Assert(bounds.Width>0 && bounds.Height>0 && bounds.Top>=0 && bounds.Bottom<=680 && bounds.Right<=1040,"Member scope context/actions are clipped.");
        }
        Assert(Named<TextBlock>("MemberScopeLabel").Text.Contains(editor.Snapshot.Scene.Groups.Single(g=>g.Id==editor.Snapshot.EditingGroupId).Name),"Scope banner omits group name.");
    }

    private static async Task RunMemberControls()
    {
        await Open("assembly-mm.3mf");
        var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(editor.Execute(new AddCube()));
        var outsider=editor.Snapshot.Scene.Objects[^1];
        Ok(editor.Execute(new MoveObject(outsider.Id,new(80,-15,40))));
        Ok(editor.SelectMany(ids));
        Ok(await editor.PreviewArrangementAsync(new GroupRequest(ids,"Member assembly")));
        Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id)); Refresh();
        var group=editor.Snapshot.Scene.Groups.Single();
        var beforeEntry=editor.Snapshot;
        Assert(Named<Button>("EditMembersButton").IsEnabled,"Selected group has no member-edit entry.");
        Click(Named<Button>("EditMembersButton"));
        var scoped=editor.Snapshot;
        var list=Named<ListBox>("ObjectList");
        Assert(scoped.EditingGroupId==group.Id && scoped.SelectedObjectIds.Length==1 && list.Items.Count==2,"Entry does not select one member and show independent rows.");
        Assert(scoped.Scene==beforeEntry.Scene && scoped.IsDirty==beforeEntry.IsDirty && scoped.CanUndo==beforeEntry.CanUndo,"Entry changed accepted content/history.");
        Assert(list.Items.OfType<ObjectTreeRow>().All(r=>r.Node.Children.IsEmpty && r.ObjectIds.Length==1 && r.SelectionUnit.Id==r.Id),"Member list contains whole-group rows.");
        Assert(!Named<Button>("GroupButton").IsEnabled && !Named<Button>("UngroupButton").IsEnabled,"Structural group commands are enabled during member editing.");
        MemberScopeVisible(); Capture("40-member-scope",1040,680);
        Invoke("SelectViewportObject",outsider.Id,ModifierKeys.None); SameAccepted(scoped);
        Click(Named<Button>("SelectAllButton"));
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual(ids),"Select all escaped member scope.");
        ChooseTreeRoot(ids[1]);
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual([ids[1]]) && editor.Snapshot.GetSelectionUnits().Length==1,"Member row selection expanded the group.");
        Invoke("SelectViewportObject",ids[0],ModifierKeys.Control); Refresh();
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual(ids),"Viewport Ctrl selection cannot combine members.");
        ChooseTreeRoot(ids[0]);
        Pass("Member scope entry, visible context, independent rows, scope-local Select all and viewport confinement");

        var beforeMove=editor.Snapshot;
        Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="15"; Click("Preview move"); await Idle();
        SameAccepted(beforeMove);
        var preview=editor.Snapshot.Preview!;
        Assert(preview?.Request is MoveTransform {MemberGroupId:var moveGroup} && moveGroup==group.Id,"Member transform did not carry scope.");
        Near(preview!.Scene.Objects.Single(o=>o.Id==ids[0]).Bounds.Center.X,15);
        Assert(ReferenceEquals(preview.Scene.Objects.Single(o=>o.Id==ids[1]),beforeMove.Scene.Objects.Single(o=>o.Id==ids[1])) &&
            ReferenceEquals(preview.Scene.Objects.Single(o=>o.Id==outsider.Id),beforeMove.Scene.Objects.Single(o=>o.Id==outsider.Id)),"Transform changed an unedited sibling/outsider.");
        Assert(!Named<Button>("DoneEditingButton").IsEnabled,"Done can escape a transform preview.");
        MemberScopeVisible(); TransformFooterVisible(); Capture("41-member-move-preview",1040,680);
        Click(Named<Button>("CancelTransformButton")); SameAccepted(beforeMove);
        Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="15"; Click("Preview move"); await Idle(); Click(Named<Button>("ApplyTransformButton"));
        var moved=editor.Snapshot;
        Click(Named<Button>("DoneEditingButton"));
        Assert(editor.Snapshot.EditingGroupId==null && editor.Snapshot.SelectedObjectIds.SequenceEqual(ids),"Done did not restore whole-group selection.");
        Click(Named<Button>("UndoButton"));
        Assert(editor.Snapshot.Scene==beforeMove.Scene && editor.Snapshot.EditingGroupId==group.Id && editor.Snapshot.SelectedObjectIds.SequenceEqual(beforeMove.SelectedObjectIds),"Undo after Done did not restore member context.");
        Click(Named<Button>("RedoButton"));
        Assert(editor.Snapshot.Scene==moved.Scene && editor.Snapshot.EditingGroupId==null,"Redo did not restore captured scene-scope context.");
        Click(Named<Button>("EditMembersButton")); ChooseTreeRoot(ids[0]);
        Ok(await editor.SaveAsync(Path.Combine(output,"member-moved.3mf"))); Refresh();
        Pass("Member move Preview/Cancel/Apply leaves siblings intact; Done and Undo/Redo restore scope and full selection");

        var beforeRename=editor.Snapshot;
        Assert(Named<Button>("RenameButton").IsEnabled && Named<TextBlock>("ObjectNameLabel").Text.Contains("Member"),"Member rename remains a group rename.");
        Named<TextBox>("ObjectName").Text="Edited member"; Click(Named<Button>("RenameButton"));
        Assert(editor.Snapshot.Scene.Objects.Single(o=>o.Id==ids[0]).Name=="Edited member" && editor.Snapshot.Scene.Groups.Single().Name==group.Name,"Rename changed group metadata instead of member.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==beforeRename.Scene,"Member rename is not undoable.");
        var beforeCopy=editor.Snapshot;
        Click(Named<Button>("DuplicateButton")); await Idle();
        var copied=editor.Snapshot; var copyId=copied.SelectedObjectId!.Value;
        Assert(copied.Scene.Groups.Single().Id==group.Id && copied.Scene.Groups.Single().ObjectIds.Length==3 && copied.EditingGroupId==group.Id && list.Items.Count==3,"Member duplicate lost group or scope.");
        Click(Named<Button>("DeleteButton")); await Idle();
        Assert(editor.Snapshot.Scene.Objects.SequenceEqual(beforeCopy.Scene.Objects) && editor.Snapshot.Scene.Groups.Single().ObjectIds.SequenceEqual(ids),"Member Delete touched siblings or left dangling membership.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==copied.Scene && editor.Snapshot.SelectedObjectId==copyId,"Delete Undo lost member selection.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==beforeCopy.Scene,"Duplicate Undo lost original group.");
        var beforeRejectedDelete=editor.Snapshot;
        Assert(!Named<Button>("DeleteButton").IsEnabled,"UI offers deletion that would dissolve the group.");
        Invoke("DeleteClick",window,new RoutedEventArgs()); await Idle(); SameAccepted(beforeRejectedDelete);
        Assert(editor.Snapshot.Scene.Groups.Single().ObjectIds.Length==2,"Deleting into a singleton silently dissolved group.");
        Pass("Member rename, duplicate/delete, exact history and singleton-group rejection use shared scoped commands");

        var beforeExport=editor.Snapshot;
        foreach(var extension in new[]{"3mf","stl"})
        {
            var path=Path.Combine(output,"member-selection."+extension);
            Ok(await (Task<OperationResult>)Invoke("ExportSelectionTo",path,beforeExport)!); await Idle(); SameAccepted(beforeExport);
            var reopened=new EditorSession(); Ok(await reopened.OpenAsync(path));
            Assert(reopened.Snapshot.Scene.Objects.Length==1 && reopened.Snapshot.Scene.Groups.IsEmpty,"Member export expands to unselected siblings.");
            Near(reopened.Snapshot.Scene.Bounds.Min.X,5); Near(reopened.Snapshot.Scene.Bounds.Max.X,25);
        }
        Pass("Desktop selection export adapter writes only the edited member and preserves accepted scope/path/history");

        Click(Named<Button>("SelectAllButton")); var beforeAlign=editor.Snapshot;
        Click(Named<Button>("AlignButton")); Click(Named<Button>("PreviewArrangementButton")); await Idle();
        var aligned=editor.Snapshot.Preview!;
        Assert(aligned.Request is AlignmentRequest {MemberGroupId:var alignGroup} && alignGroup==group.Id,"Member alignment omitted scope.");
        foreach(var id in ids) Near(aligned.Scene.Objects.Single(o=>o.Id==id).Bounds.Center.X,27.5);
        Assert(ReferenceEquals(aligned.Scene.Objects.Single(o=>o.Id==outsider.Id),beforeAlign.Scene.Objects.Single(o=>o.Id==outsider.Id)),"Member alignment changed outside object.");
        MemberScopeVisible(); ArrangementFooterVisible(); Capture("42-member-alignment-preview",1040,680);
        Click(Named<Button>("CancelArrangementButton")); SameAccepted(beforeAlign);
        Pass("Member alignment treats members independently, preserves outsiders and retains visible context/Cancel at minimum size");

        ChooseTreeRoot(ids[0]);
        var beforeDraft=editor.Snapshot;
        Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="20"; Click("Preview move");
        // Simulate an adapter/settings update before the awaited worker continuation.
        Named<TextBox>("PositionX").Text="123"; await Idle();
        Assert(Named<TextBox>("PositionX").Text=="123" && !Named<Button>("ApplyTransformButton").IsEnabled,"Completed older work overwrote a newer draft or enabled stale Apply.");
        Near(editor.Snapshot.Preview!.Scene.Objects.Single(o=>o.Id==ids[0]).Bounds.Center.X,20);
        SameAccepted(beforeDraft);
        Click(Named<Button>("PreviewTransformButton")); await Idle();
        Assert(Named<Button>("ApplyTransformButton").IsEnabled,"Fresh draft cannot produce a current member preview.");
        Near(editor.Snapshot.Preview!.Scene.Objects.Single(o=>o.Id==ids[0]).Bounds.Center.X,123);
        Click(Named<Button>("CancelTransformButton")); SameAccepted(beforeDraft);
        Pass("An asynchronous member transform cannot overwrite newer field drafts or enable stale acceptance");

        ChooseTreeRoot(ids[0]);
        var beforeSplit=editor.Snapshot;
        Click("Split…"); Click(Named<Button>("PreviewButton")); await Idle();
        var split=editor.Snapshot.Preview!;
        Assert(split.Request is SplitRequest {MemberGroupId:var splitGroup} && splitGroup==group.Id && split.Scene.Groups.Single().ObjectIds.Length==3,"Member split failed to replace group membership.");
        SameAccepted(beforeSplit); MemberScopeVisible(); FooterVisible(); Capture("43-member-split-preview",1040,680);
        Click(ConstructionCancel()); SameAccepted(beforeSplit);
        Pass("Member split preview uses shared scope and replaces membership privately; Cancel retains original member/group");

        await Open("missing-top.stl"); Ok(editor.Execute(new AddCube()));
        var repairIds=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(editor.SelectMany(repairIds)); Ok(await editor.PreviewArrangementAsync(new GroupRequest(repairIds,"Repair members")));
        Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id)); Refresh(); Click(Named<Button>("EditMembersButton")); ChooseTreeRoot(repairIds[0]);
        var beforeRepair=editor.Snapshot;
        Click("Repair…"); await Idle(); Click(Named<Button>("PreviewButton")); await Idle();
        var repaired=editor.Snapshot.Preview!;
        Assert(repaired.Request is RepairRequest {MemberGroupId:var repairGroup} && repairGroup==beforeRepair.EditingGroupId && repaired.Repair!=null,"Member repair adapter omitted scope or failed.");
        SameAccepted(beforeRepair); MemberScopeVisible(); Capture("44-member-repair-preview",1040,680);
        Click(Named<Button>("ApplyPreviewButton"));
        Assert(editor.Snapshot.Scene.Groups.Single().Id==beforeRepair.EditingGroupId && editor.Snapshot.EditingGroupId==beforeRepair.EditingGroupId,"Repair changed group/scope.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==beforeRepair.Scene && editor.Snapshot.EditingGroupId==beforeRepair.EditingGroupId,"Repair Undo lost group/member content.");
        Pass("Member diagnosis/repair Preview/Apply/Undo retain stable group, member context and untouched sibling");
    }
}
