using Builder.Core;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Builder.Desktop;

public partial class MainWindow
{
    private Scene? listedScene;
    private ImmutableArray<SceneTreeNode> listedTree=[];
    private Guid? listedEditingGroupId;
    private readonly ObjectTreeRows displayedTree=[];
    private readonly Dictionary<Guid,ObjectTreeRow> objectTreeRows=[];
    private ImmutableArray<SceneTreeNode> treeSelectionUnits=[];
    // Expansion and view anchors are presentation only. Core owns canonical selected leaf IDs.
    // Stable-ID expansion survives removal/Undo; row objects reference only the current graph.
    private readonly Dictionary<Guid,bool> objectTreeExpansion=[];
    private long treeDisclosureVersion,listedDisclosureVersion=-1;

    private void RefreshObjectTree(Scene scene,IEnumerable<Guid> selectedObjectIds)
    {
        var wasRefreshing=refreshing; refreshing=true;
        try
        {
            CaptureObjectTreeView();
            var scope=editor.Snapshot.EditingGroupId;
            if(!ReferenceEquals(listedScene,scene) || listedEditingGroupId!=scope || listedDisclosureVersion!=treeDisclosureVersion)
            {
                var oldScope=listedEditingGroupId;
                var createdPrimary=listedScene is {} prior && editor.Snapshot.SelectedObjectId is {} primary &&
                    !prior.Objects.Any(o=>o.Id==primary) ? primary : (Guid?)null;
                // Scope and disclosure change presentation only; immutable metadata belongs to this exact Scene.
                if(!ReferenceEquals(listedScene,scene)) listedTree=scene.GetObjectTree();
                listedScene=scene; listedEditingGroupId=scope; listedDisclosureVersion=treeDisclosureVersion;
                var tree=listedTree;
                treeSelectionUnits=scope is {} scopeGroupId ? tree.First(n=>n.Id==scopeGroupId).Children : tree;
                var currentIds=new HashSet<Guid>(); var visible=new List<ObjectTreeRow>();
                foreach(var root in tree)
                {
                    currentIds.Add(root.Id);
                    var header=TreeRow(root,root,null);
                    if(scope==null) visible.Add(header);
                    foreach(var member in root.Children)
                    {
                        currentIds.Add(member.Id);
                        var row=TreeRow(member,scope==root.Id ? member : root,root.Id);
                        if(scope==root.Id || scope==null && header.IsExpanded) visible.Add(row);
                    }
                }
                foreach(var id in objectTreeRows.Keys.Where(id=>!currentIds.Contains(id)).ToArray()) objectTreeRows.Remove(id);
                if(!ReferenceEquals(ObjectList.ItemsSource,displayedTree)) ObjectList.ItemsSource=displayedTree;
                if(displayedTree.Count!=visible.Count || displayedTree.Where((row,i)=>!ReferenceEquals(row,visible[i])).Any())
                {
                    var hadTreeFocus=ObjectList.IsKeyboardFocusWithin;
                    displayedTree.ReplaceWith(visible);
                    var reveal=createdPrimary is {} added ? visible.FirstOrDefault(row=>row.SelectionUnit.ObjectIds.Contains(added))?.SelectionUnit.Id : null;
                    QueueObjectTreeRestore(oldScope!=scope,hadTreeFocus,reveal);
                }
            }
            SynchronizeObjectTreeSelection(selectedObjectIds);
            ObjectTreeHint.Visibility=scope!=null || !scene.Groups.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            ObjectTreeHint.Text=scope!=null ? "Select members here or in the viewport. Done returns to whole-group selection." :
                "Expand a group to see its members. Enter or double-click a member to edit inside its group.";
            ObjectList.ToolTip=scope!=null ? "Select independent members. Ctrl toggles members; Shift selects a range." :
                "Member rows select their whole group. Ctrl toggles units; Shift selects a range. Enter or double-click edits members.";
        }
        finally {refreshing=wasRefreshing;}
    }
    private ObjectTreeRow TreeRow(SceneTreeNode node,SceneTreeNode owner,Guid? parentId)
    {
        var expanded=!node.Children.IsEmpty && objectTreeExpansion.GetValueOrDefault(node.Id,true);
        if(!objectTreeRows.TryGetValue(node.Id,out var row))
        {row=new(node,owner,parentId,expanded); objectTreeRows.Add(node.Id,row);}
        else row.Update(node,owner,parentId,expanded);
        return row;
    }
    private void SynchronizeObjectTreeSelection(IEnumerable<Guid> selectedIds)
    {
        var selected=selectedIds.ToHashSet();
        var state=CurrentTreeView;
        if(!state.Selection.SequenceEqual(selectedIds))
        {
            var primary=editor.Snapshot.SelectedObjectId;
            state.AnchorUnitId=treeSelectionUnits.FirstOrDefault(n=>n.ObjectIds.Contains(primary ?? Guid.Empty))?.Id;
            state.Selection=selectedIds.ToImmutableArray();
        }
        var units=treeSelectionUnits.Where(n=>n.ObjectIds.Any(selected.Contains)).Select(n=>n.Id).ToHashSet();
        foreach(var row in displayedTree) row.Select(units.Contains(row.SelectionUnit.Id));
        // One Selector entry per logical unit. Aliases share highlighting, never 1,000 selections.
        var desired=displayedTree.Where(row=>row.Id==row.SelectionUnit.Id && units.Contains(row.Id)).ToHashSet();
        foreach(var row in ObjectList.SelectedItems.OfType<ObjectTreeRow>().ToArray())
            if(!desired.Contains(row)) ObjectList.SelectedItems.Remove(row);
        foreach(var row in desired) if(!ObjectList.SelectedItems.Contains(row)) ObjectList.SelectedItems.Add(row);
    }
    private SceneTreeNode[] SelectedTreeUnits()=>ObjectList.SelectedItems.OfType<ObjectTreeRow>()
        .Select(row=>row.SelectionUnit).DistinctBy(node=>node.Id).ToArray();
    private void SetObjectTreeExpanded(ObjectTreeRow row,bool expanded)
    {
        if(!row.IsGroup || row.IsExpanded==expanded) return;
        CaptureObjectTreeView(); objectTreeExpansion[row.Id]=expanded; treeDisclosureVersion++;
        RefreshObjectTree(editor.Snapshot.Scene,editor.Snapshot.SelectedObjectIds);
    }
    private void ObjectTreeExpansionChanged(object sender,RoutedEventArgs e)
    {
        if(refreshing || sender is not ToggleButton {IsInitialized:true,DataContext:ObjectTreeRow row} toggle || !row.IsGroup) return;
        if(toggle.IsChecked==row.IsExpanded) return;
        BeginObjectTreeInteraction(); SetObjectTreeExpanded(row,toggle.IsChecked==true);
    }
    private SceneGroup? EditingGroup=>editor.Snapshot.Scene.Groups.FirstOrDefault(g=>g.Id==editor.Snapshot.EditingGroupId);
    private IEnumerable<SceneObject> ScopeObjects(EditorSnapshot snapshot)
    {
        if(snapshot.EditingGroupId is not {} id) return snapshot.Scene.Objects;
        var members=snapshot.Scene.Groups.First(g=>g.Id==id).ObjectIds.ToHashSet();
        return snapshot.Scene.Objects.Where(o=>members.Contains(o.Id));
    }
    private bool CanDeleteSelection=>!editor.Snapshot.SelectedObjectIds.IsEmpty &&
        (EditingGroup is not {} group || group.ObjectIds.Length-editor.Snapshot.SelectedObjectIds.Length>=2);
    private void RefreshMemberScope()
    {
        var group=EditingGroup; var available=ArrangementSelectionAvailable;
        MemberScopeBar.Visibility=group==null ? Visibility.Collapsed : Visibility.Visible;
        MemberScopeLabel.Text=group==null ? "" : $"Editing {group.Name}"; MemberScopeLabel.ToolTip=MemberScopeLabel.Text;
        DoneEditingButton.IsEnabled=group!=null && available;
        EditMembersButton.IsEnabled=group==null && SelectedGroup!=null && available;
        EditMembersButton.Visibility=group==null ? Visibility.Visible : Visibility.Collapsed;
        InsertButton.IsEnabled=AddShapeMenu.IsEnabled=group==null;
        DeleteButton.ToolTip=group!=null && !CanDeleteSelection ? "Keep at least two members in the group. Select fewer members, or choose Done and delete the whole group." :
            group!=null ? "Delete selected members while keeping their group (Delete)" : "Delete selected objects and groups (Delete)";
        DuplicateButton.ToolTip=group!=null ? "Duplicate selected members inside their group (Ctrl+D)" : "Duplicate selected objects and groups (Ctrl+D)";
        SelectAllButton.ToolTip=group==null ? "Select all objects and groups" : "Select all members of this group";
        ExportSelectionButton.ToolTip=group==null ? "Export selected objects and whole groups as 3MF, STL, OBJ, PLY or glTF/GLB" : "Export only selected group members as 3MF, STL, OBJ, PLY or glTF/GLB";
    }
    private void EnterGroupClick(object sender,RoutedEventArgs e)
    {if(SelectedGroup is {} group) EnterMemberScope(group.Id);}
    private void EnterMemberScope(Guid groupId,Guid? memberId=null)
    {
        if(!ArrangementSelectionAvailable || editor.Snapshot.EditingGroupId!=null) return;
        CaptureObjectTreeView();
        var result=editor.EnterGroup(groupId,memberId,editor.Snapshot.Revision);
        if(result.Succeeded)
        {
            revealEnteredMember=true; Refresh();
            // Enter/double-click from a row continues keyboard navigation in member scope.
            // The inspector button retains the existing viewport-focus policy.
            if(enteringFromTreeRow && displayedTree.FirstOrDefault(r=>r.Id==editor.Snapshot.SelectedObjectId) is {} row) FocusObjectTreeRow(row);
            else ViewportSurface.Focus();
        }
        Message(result.Message,!result.Succeeded);
    }
    private void ExitGroupClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable || editor.Snapshot.EditingGroupId==null) return;
        CaptureObjectTreeView();
        var result=editor.ExitGroup(editor.Snapshot.Revision);
        if(result.Succeeded) {Refresh(); ViewportSurface.Focus();}
        Message(result.Message,!result.Succeeded);
    }
    private static T? FindObjectTreeVisual<T>(DependencyObject root,Func<T,bool> predicate) where T:DependencyObject
    {
        if(root is T found && predicate(found)) return found;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if(FindObjectTreeVisual(VisualTreeHelper.GetChild(root,i),predicate) is {} child) return child;
        return null;
    }
}
