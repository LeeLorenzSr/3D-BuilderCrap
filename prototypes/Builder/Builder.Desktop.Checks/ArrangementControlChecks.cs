using Builder.Core;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private static void ArrangementUnchanged(EditorSnapshot before)
    {
        SameAccepted(before);
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Arrangement changed accepted multi-selection before Apply.");
    }

    private static void ArrangementFooterVisible()
    {
        Layout(1040,680);
        foreach(var name in new[]{"ArrangementActions","PreviewArrangementButton","ApplyArrangementButton","CancelArrangementButton"})
        {
            var element=Named<FrameworkElement>(name);
            var bounds=Bounds(element);
            Assert(element.Visibility==Visibility.Visible && bounds.Width>0 && bounds.Height>0,"Missing arrangement footer: "+name);
            Assert(bounds.Left>=0 && bounds.Top>=0 && bounds.Right<=1040.1 && bounds.Bottom<=680.1,"Clipped arrangement footer: "+name);
            observations.Add(new {control=name,client="1040 x 680",bounds=bounds.ToString()});
        }
        Assert(Named<FrameworkElement>("ObjectsOverview").Visibility==Visibility.Collapsed,"Arrangement did not replace object overview.");
    }

    private static void SameObjects(Scene before,Scene after)
    {
        Assert(before.Objects.SequenceEqual(after.Objects),"Grouping changed object identity, mesh, name or transform.");
    }

    private static async Task RunArrangementControls()
    {
        await Open("assembly-mm.3mf");
        var accepted=editor.Snapshot;
        var list=Named<ListBox>("ObjectList");
        Assert(list.SelectionMode==SelectionMode.Extended,"Object list does not support Ctrl/Shift multi-selection.");
        Click(Named<Button>("ClearSelectionButton"));
        Assert(editor.Snapshot.SelectedObjectIds.IsEmpty,"Clear did not clear Core selection.");
        Assert(!Named<Button>("GroupButton").IsEnabled && !Named<Button>("AlignButton").IsEnabled,"Empty selection enables multi-object commands.");
        list.SelectedItems.Add(list.Items[0]);
        list.SelectedItems.Add(list.Items[1]);
        Assert(editor.Snapshot.SelectedObjectIds.Length==2 && list.SelectedItems.Count==2,"List selection did not reach Core or did not survive refresh.");
        Assert(editor.Snapshot.SelectedObjectId is {} primary && editor.Snapshot.SelectedObjectIds.Contains(primary),"Primary is outside multi-selection.");
        Click(Named<Button>("ClearSelectionButton"));
        Click(Named<Button>("SelectAllButton"));
        Assert(editor.Snapshot.SelectedObjectIds.Length==2,"Select all did not select every object.");
        Assert(editor.Snapshot.Scene==accepted.Scene && editor.Snapshot.IsDirty==accepted.IsDirty &&
            editor.Snapshot.CanUndo==accepted.CanUndo && editor.Snapshot.CanRedo==accepted.CanRedo,"Selection controls changed document content/history.");
        Capture("20-multiselection");
        Pass("Extended object-list selection, Select all/Clear and primary selection share Core without content/history edits");

        var beforeGroup=editor.Snapshot;
        Click(Named<Button>("GroupButton"));
        Named<TextBox>("GroupName").Text="Print assembly";
        ArrangementUnchanged(beforeGroup);
        Assert(Named<TextBlock>("ArrangementReview").Text.Contains("meshes remain separate"),"Group review hides mesh semantics.");
        Click(Named<Button>("PreviewArrangementButton")); await Idle();
        var groupPreview=editor.Snapshot.Preview!;
        Assert(groupPreview.Request is GroupRequest && groupPreview.Scene.Groups.Length==1,"Group preview missing.");
        SameObjects(beforeGroup.Scene,groupPreview.Scene); ArrangementUnchanged(beforeGroup);
        ArrangementFooterVisible(); Capture("21-group-review-minimum",1040,680);
        Named<TextBox>("GroupName").Text="";
        Assert(!Named<Button>("ApplyArrangementButton").IsEnabled,"Changed group name can apply an older candidate.");
        Click(Named<Button>("PreviewArrangementButton")); await Idle();
        Assert(editor.Snapshot.Preview?.Id==groupPreview.Id && !Named<Button>("ApplyArrangementButton").IsEnabled,"Invalid group-name replacement lost prior candidate or enabled Apply.");
        ArrangementUnchanged(beforeGroup);
        Click(Named<Button>("CancelArrangementButton")); ArrangementUnchanged(beforeGroup);
        Assert(editor.Snapshot.Preview==null && editor.Snapshot.Scene.Groups.IsEmpty,"Group cancellation changed structure.");
        Click(Named<Button>("GroupButton")); Named<TextBox>("GroupName").Text="Print assembly";
        Click(Named<Button>("PreviewArrangementButton")); await Idle();
        Click(Named<Button>("ApplyArrangementButton"));
        var grouped=editor.Snapshot;
        Assert(grouped.Scene.Groups.Single().Name=="Print assembly" && ObjectTreeRoots().Length==1,"Accepted group is not one named selection unit.");
        SameObjects(beforeGroup.Scene,grouped.Scene);
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==beforeGroup.Scene && list.Items.Count==2,"Grouping Undo did not restore independent units.");
        Click(Named<Button>("RedoButton")); Assert(editor.Snapshot.Scene==grouped.Scene && ObjectTreeRoots().Length==1,"Grouping Redo lost stable group structure.");
        Capture("22-grouped");
        Pass("Grouping reviews separate meshes, retains prior candidate on invalid name, cancels safely and applies one undoable group");

        var beforeUngroup=editor.Snapshot;
        Assert(Named<Button>("UngroupButton").IsEnabled && !Named<Button>("GroupButton").IsEnabled,"Single group availability is incorrect.");
        Click(Named<Button>("UngroupButton")); Click(Named<Button>("PreviewArrangementButton")); await Idle();
        var ungroupPreview=editor.Snapshot.Preview;
        Assert(ungroupPreview?.Request is UngroupRequest && ungroupPreview.Scene.Groups.IsEmpty,"Ungroup preview did not expose independent members.");
        SameObjects(beforeUngroup.Scene,ungroupPreview!.Scene); ArrangementUnchanged(beforeUngroup);
        ArrangementFooterVisible(); Capture("23-ungroup-review-minimum",1040,680);
        Click(Named<Button>("CancelArrangementButton")); ArrangementUnchanged(beforeUngroup);
        Click(Named<Button>("UngroupButton")); Click(Named<Button>("PreviewArrangementButton")); await Idle();
        Click(Named<Button>("ApplyArrangementButton"));
        Assert(editor.Snapshot.Scene.Groups.IsEmpty && list.Items.Count==2,"Ungroup Apply did not restore independent rows.");
        SameObjects(beforeUngroup.Scene,editor.Snapshot.Scene);
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==beforeUngroup.Scene && ObjectTreeRoots().Length==1,"Ungroup Undo did not restore group identity.");
        Click(Named<Button>("RedoButton")); Assert(editor.Snapshot.Scene.Groups.IsEmpty && list.Items.Count==2,"Ungroup Redo failed.");
        Pass("Ungroup review, Cancel, Apply and history preserve member IDs, meshes and transforms");

        await Open("assembly-mm.3mf"); Click(Named<Button>("SelectAllButton"));
        var beforeAlign=editor.Snapshot;
        var combinedCenter=beforeAlign.Scene.Bounds.Center.X;
        var referenceCenter=beforeAlign.Scene.Objects.Single(o=>o.Id==beforeAlign.SelectedObjectId).Bounds.Center.X;
        Click(Named<Button>("AlignButton")); Click(Named<Button>("PreviewArrangementButton")); await Idle();
        var firstAlignment=editor.Snapshot.Preview!;
        Assert(firstAlignment.Request is AlignmentRequest,"Alignment preview missing.");
        foreach(var obj in firstAlignment.Scene.Objects) Near(obj.Bounds.Center.X,combinedCenter);
        ArrangementUnchanged(beforeAlign);
        Named<ComboBox>("AlignmentX").SelectedIndex=-1;
        Assert(!Named<Button>("ApplyArrangementButton").IsEnabled,"Invalid alignment draft can apply old candidate.");
        Click(Named<Button>("PreviewArrangementButton")); await Idle();
        Assert(editor.Snapshot.Preview?.Id==firstAlignment.Id && !Named<Button>("ApplyArrangementButton").IsEnabled,"Failed alignment draft replaced candidate or enabled Apply.");
        Invoke("ApplyArrangementClick",window,new RoutedEventArgs()); ArrangementUnchanged(beforeAlign);
        ArrangementFooterVisible(); Capture("24-align-invalid-draft-minimum",1040,680);
        Named<ComboBox>("AlignmentX").SelectedIndex=2;
        Named<ComboBox>("AlignmentReference").SelectedIndex=1;
        Click(Named<Button>("PreviewArrangementButton")); await Idle();
        var replacement=editor.Snapshot.Preview!;
        Assert(replacement.Id!=firstAlignment.Id && Named<Button>("ApplyArrangementButton").IsEnabled,"Alignment replacement did not renew preview ownership.");
        foreach(var obj in replacement.Scene.Objects) Near(obj.Bounds.Center.X,referenceCenter);
        ArrangementUnchanged(beforeAlign); Capture("25-align-primary-preview-minimum",1040,680);
        Click(Named<Button>("CancelArrangementButton")); ArrangementUnchanged(beforeAlign);
        Click(Named<Button>("AlignButton")); Click(Named<Button>("PreviewArrangementButton")); await Idle();
        Click(Named<Button>("ApplyArrangementButton"));
        foreach(var obj in editor.Snapshot.Scene.Objects) Near(obj.Bounds.Center.X,combinedCenter);
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual(beforeAlign.SelectedObjectIds),"Alignment acceptance changed selected member identities.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==beforeAlign.Scene,"Alignment Undo did not restore exact accepted content.");
        Capture("26-alignment-undo");
        Pass("Alignment combined/primary references, failed-draft retention, fresh preview tokens, explicit acceptance and exact Undo");
    }
}
