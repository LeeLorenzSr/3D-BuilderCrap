using Builder.Core;
using Builder.Desktop;
using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

internal static partial class Program
{
    private static IEnumerable<DependencyObject> ObjectTreeVisuals(DependencyObject parent)
    {
        yield return parent;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            foreach(var child in ObjectTreeVisuals(VisualTreeHelper.GetChild(parent,i))) yield return child;
    }

    private static ListBoxItem ObjectTreeContainer(Guid id)
    {
        var list=Named<ListBox>("ObjectList");
        var node=list.Items.OfType<ObjectTreeRow>().Single(n=>n.Id==id);
        list.ScrollIntoView(node);
        Layout(surface.ActualWidth,surface.ActualHeight);
        return (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(node);
    }

    private static ToggleButton ObjectTreeToggle(Guid id) => ObjectTreeVisuals(ObjectTreeContainer(id))
        .OfType<ToggleButton>().Single(t=>t.Name=="TreeDisclosure");

    private static ObjectTreeRow[] ObjectTreeRoots()=>Named<ListBox>("ObjectList").Items.OfType<ObjectTreeRow>().Where(r=>r.SelectionUnit.Id==r.Id).ToArray();
    private static bool? RecordedTreeExpansion(Guid id)=>ObjectTreeToggle(id).IsChecked;

    private static void RecordTreeExpansion(string stage,Guid id)
    {
        var toggle=ObjectTreeToggle(id);
        observations.Add(new {stage,groupId=id,recordedExpansion=RecordedTreeExpansion(id),renderedExpansion=toggle.IsChecked,toggle.IsInitialized,toggle.IsLoaded});
    }

    private static void ChooseTreeRoot(Guid id)
    {
        var list=Named<ListBox>("ObjectList");
        list.SelectedItems.Clear();
        list.SelectedItems.Add(list.Items.OfType<ObjectTreeRow>().Single(n=>n.Id==id));
    }

    private static async Task RunObjectTreeControls()
    {
        await Open("assembly-mm.3mf");
        var memberIds=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(editor.Execute(new AddCube()));
        Ok(editor.SelectMany(memberIds)); Refresh();
        Click(Named<Button>("GroupButton")); Named<TextBox>("GroupName").Text="Tree assembly";
        Click(Named<Button>("PreviewArrangementButton")); await Idle(); Click(Named<Button>("ApplyArrangementButton"));
        var list=Named<ListBox>("ObjectList");
        var group=editor.Snapshot.Scene.Groups.Single();
        var roots=ObjectTreeRoots().Select(r=>r.Node).ToArray();
        Assert(roots.Length==2 && roots[0].Id==group.Id && roots[0].Children.Length==2 && roots[1].Children.IsEmpty,"Object tree did not preserve root/member hierarchy and order.");
        Assert(roots[0].Children.Select(n=>n.Id).SequenceEqual(memberIds) && roots[0].ObjectIds.SequenceEqual(memberIds),"Tree member metadata lost stable leaf identities.");
        var toggle=ObjectTreeToggle(group.Id);
        Assert(toggle.IsChecked==true && toggle.Focusable && toggle.IsTabStop,"New group is not expanded or its disclosure is not keyboard accessible.");
        Assert(AutomationProperties.GetName(toggle).Contains(group.Name),"Disclosure has no group-specific accessible name.");
        var memberRows=list.Items.OfType<ObjectTreeRow>().Where(r=>r.ParentGroupId==group.Id).ToArray();
        Assert(memberRows.Select(r=>r.Id).SequenceEqual(memberIds) && memberRows.All(r=>r.SelectionUnit.Id==group.Id),"Visible member rows do not target their complete owning group.");
        Assert(ObjectTreeVisuals(ObjectTreeContainer(group.Id)).OfType<TextBlock>().Any(t=>t.Text.Contains("2 objects")),"Group row hides its member count.");
        Assert(Named<TextBlock>("ObjectTreeHint").Visibility==Visibility.Visible,"Whole-group editing hint missing.");
        var beforeExpansion=editor.Snapshot;
        toggle.IsChecked=false; ArrangementUnchanged(beforeExpansion);
        RecordTreeExpansion("collapsed",group.Id);
        Assert(RecordedTreeExpansion(group.Id)==false,"Collapsing the rendered group did not update presentation state.");
        Refresh(); Assert(ObjectTreeToggle(group.Id).IsChecked==false,"Refresh discarded group expansion state.");
        Click(Named<Button>("ClearSelectionButton"));
        Assert(list.SelectionMode==SelectionMode.Extended,"Tree removed Extended root selection.");
        list.SelectedItems.Add(list.Items[0]);
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual(memberIds),"Selecting a group root omitted a member.");
        list.SelectedItems.Add(list.Items[1]);
        Assert(editor.Snapshot.SelectedObjectIds.Length==3 && list.SelectedItems.Count==2,"Extended tree selection lost a root.");
        list.SelectedItems.Remove(list.Items[0]);
        Assert(editor.Snapshot.SelectedObjectIds.Length==1,"Toggling group root off left partially selected members.");
        ChooseTreeRoot(group.Id); Capture("30-object-tree",1040,680);
        Pass("Expandable tree roots expose ordered members/counts, retain presentation state and preserve whole-group Extended selection");

        var beforeRename=editor.Snapshot;
        Assert(Named<Button>("RenameButton").IsEnabled && Named<TextBlock>("ObjectNameLabel").Text.Contains("Group",StringComparison.OrdinalIgnoreCase),"Selected group has no rename field.");
        Named<TextBox>("ObjectName").Text="Renamed tree assembly"; Click(Named<Button>("RenameButton"));
        var renamed=editor.Snapshot;
        Assert(renamed.Scene.Groups.Single() is {Name:"Renamed tree assembly"} renamedGroup && renamedGroup.Id==group.Id,"Group rename lost name or identity.");
        SameObjects(beforeRename.Scene,renamed.Scene);
        RecordTreeExpansion("renamed",group.Id);
        Assert(RecordedTreeExpansion(group.Id)==false,"Rename overwrote cached presentation state.");
        Assert(ObjectTreeToggle(group.Id).IsChecked==false,"Rename discarded stable-ID expansion state.");
        Assert(ObjectTreeRoots().First().Name=="Renamed tree assembly","Renamed group not reflected in tree.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==beforeRename.Scene,"Group rename Undo failed.");
        Assert(ObjectTreeToggle(group.Id).IsChecked==false,"Undo discarded expansion state.");
        Click(Named<Button>("RedoButton")); Assert(editor.Snapshot.Scene==renamed.Scene,"Group rename Redo failed.");
        var beforeInvalidName=editor.Snapshot;
        Named<TextBox>("ObjectName").Text=""; Click(Named<Button>("RenameButton")); ArrangementUnchanged(beforeInvalidName);
        Named<TextBox>("ObjectName").Text="Renamed tree assembly";
        Capture("31-renamed-tree",1040,680);
        Pass("Group rename preserves member records/identity and expansion across Undo/Redo; invalid names leave accepted state intact");

        Click(Named<Button>("ClearSelectionButton")); Assert(!Named<Button>("ExportSelectionButton").IsEnabled,"Empty tree selection can export.");
        ChooseTreeRoot(group.Id);
        var beforeExport=editor.Snapshot;
        var expectedBounds=beforeExport.Scene.GetSelectionUnits(beforeExport.SelectedObjectIds).Single().Bounds;
        Assert(Named<Button>("ExportSelectionButton").IsEnabled,"Selected group cannot export.");
        foreach(var extension in new[]{"3mf","stl"})
        {
            var path=Path.Combine(output,"tree-selection."+extension);
            Ok(await (Task<OperationResult>)Invoke("ExportSelectionTo",path,beforeExport)!); await Idle();
            ArrangementUnchanged(beforeExport);
            var reopened=new EditorSession(); Ok(await reopened.OpenAsync(path));
            Assert(reopened.Snapshot.Scene.Objects.Length==(extension=="3mf" ? 2 : 1),"Selection export includes unselected objects or loses selected members.");
            Assert(reopened.Snapshot.Scene.Groups.Length==(extension=="3mf" ? 1 : 0),"Selection export format has incorrect group structure.");
            Near(reopened.Snapshot.Scene.Bounds.Min.X,expectedBounds.Min.X); Near(reopened.Snapshot.Scene.Bounds.Max.X,expectedBounds.Max.X);
            Near(reopened.Snapshot.Scene.Bounds.Min.Y,expectedBounds.Min.Y); Near(reopened.Snapshot.Scene.Bounds.Max.Y,expectedBounds.Max.Y);
            Near(reopened.Snapshot.Scene.Bounds.Min.Z,expectedBounds.Min.Z); Near(reopened.Snapshot.Scene.Bounds.Max.Z,expectedBounds.Max.Z);
        }
        Click(Named<Button>("MoveHandleButton"));
        Assert(!Named<Button>("ExportSelectionButton").IsEnabled && !Named<Button>("DuplicateButton").IsEnabled && !Named<Button>("DeleteButton").IsEnabled,"Transform mode leaves unrelated tree commands enabled.");
        Click(Named<Button>("CancelTransformButton"));
        Click(Named<Button>("SelectAllButton"));
        Click(Named<Button>("AlignButton")); Assert(!Named<Button>("ExportSelectionButton").IsEnabled,"Arrangement mode permits selection export.");
        Click(Named<Button>("CancelArrangementButton"));
        Assert(Named<Button>("ExportSelectionButton").IsEnabled,"Cancelling tool did not restore export availability.");
        Pass("Selection export adapter writes only selected whole groups as3MF/STL and preserves document/path/history; modal tools guard export");

        var beforeDuplicate=editor.Snapshot;
        Assert(beforeDuplicate.SelectedObjectIds.Length==3 && Named<Button>("DuplicateButton").IsEnabled,"Mixed root selection cannot duplicate.");
        Click(Named<Button>("DuplicateButton")); await Idle();
        var duplicated=editor.Snapshot;
        Assert(duplicated.Scene.Objects.Length==6 && duplicated.Scene.Groups.Length==2 && ObjectTreeRoots().Length==4,"Bulk duplicate lost objects/groups/tree roots.");
        Assert(beforeDuplicate.Scene.Objects.All(duplicated.Scene.Objects.Contains) && beforeDuplicate.Scene.Groups.All(duplicated.Scene.Groups.Contains),"Bulk duplicate changed original records.");
        Assert(duplicated.SelectedObjectIds.Length==3 && !duplicated.SelectedObjectIds.Any(id=>beforeDuplicate.Scene.Objects.Any(o=>o.Id==id)),"Duplicate did not select independent copies.");
        var copiedGroup=duplicated.Scene.Groups.Single(g=>g.Id!=group.Id);
        Assert(ObjectTreeToggle(copiedGroup.Id).IsChecked==true,"A newly copied group is not discoverably expanded.");
        Capture("32-duplicated-tree",1040,680);
        Click(Named<Button>("DeleteButton")); await Idle();
        Assert(editor.Snapshot.Scene.Objects.SequenceEqual(beforeDuplicate.Scene.Objects) && editor.Snapshot.Scene.Groups.SequenceEqual(beforeDuplicate.Scene.Groups),"Bulk Delete removed originals or left dangling group metadata.");
        Assert(editor.Snapshot.SelectedObjectIds.IsEmpty && !Named<Button>("ExportSelectionButton").IsEnabled,"Deleting selected copies retained invalid selection/export availability.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==duplicated.Scene && editor.Snapshot.SelectedObjectIds.SequenceEqual(duplicated.SelectedObjectIds),"Delete Undo lost copies/full selection.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==beforeDuplicate.Scene && editor.Snapshot.SelectedObjectIds.SequenceEqual(beforeDuplicate.SelectedObjectIds),"Duplicate was not one complete history step.");
        Click(Named<Button>("RedoButton")); Assert(editor.Snapshot.Scene==duplicated.Scene,"Duplicate Redo lost stable copy IDs.");
        Click(Named<Button>("RedoButton")); Assert(editor.Snapshot.Scene.Objects.SequenceEqual(beforeDuplicate.Scene.Objects),"Delete Redo failed.");
        Capture("33-delete-redo",1040,680);
        Pass("Mixed-selection Duplicate/Delete act on whole groups, protect source records and restore complete scene/selection through history");

        await Open("assembly-mm.3mf");
        for(var i=0;i<32;i++)
        {
            if(i>=editor.Snapshot.Scene.Objects.Length) Ok(editor.Execute(new AddCube()));
            Ok(editor.Execute(new RenameObject(editor.Snapshot.Scene.Objects[i].Id,$"Member {i:00}")));
        }
        var manyIds=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(editor.SelectMany(manyIds));
        Ok(await editor.PreviewArrangementAsync(new GroupRequest(manyIds,"Many members")));
        var manyPreview=editor.Snapshot.Preview!; Ok(editor.ApplyPreview(manyPreview.Id,manyPreview.BaseRevision));
        Refresh(); Layout(1040,680);
        var largeGroup=editor.Snapshot.Scene.Groups.Single();
        Assert(ObjectTreeToggle(largeGroup.Id).IsChecked==true,"Large group is not expanded.");
        var scroll=ObjectTreeVisuals(list).OfType<ScrollViewer>().First();
        Assert(scroll.ExtentHeight>scroll.ViewportHeight+200,"Large group does not exercise member scrolling.");
        scroll.ScrollToEnd(); Layout(1040,680); await Idle();
        var last=ObjectTreeVisuals(list).OfType<TextBlock>().Single(t=>t.Text=="Member 31");
        var lastBounds=Bounds(last); var listBounds=Bounds(list);
        Assert(scroll.VerticalOffset>0 && lastBounds.Top>=listBounds.Top && lastBounds.Bottom<=listBounds.Bottom,"Last member of a tall expanded group is unreachable by scrolling.");
        Capture("34-large-group-pixel-scroll",1040,680);
        var beforeCollapse=editor.Snapshot; ObjectTreeToggle(largeGroup.Id).IsChecked=false;
        ArrangementUnchanged(beforeCollapse); Refresh();
        Assert(ObjectTreeToggle(largeGroup.Id).IsChecked==false,"Tall-group collapse did not persist.");
        Pass("Pixel scrolling reaches the final member of an expanded group taller than the object list without document edits");
    }
}
