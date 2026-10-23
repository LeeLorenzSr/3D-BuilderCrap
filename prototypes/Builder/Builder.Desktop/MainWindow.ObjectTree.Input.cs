using Builder.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Builder.Desktop;

public partial class MainWindow
{
    private bool enteringFromTreeRow;
    private static DependencyObject? TreeParent(DependencyObject element)
        =>element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
    private ObjectTreeRow? ObjectTreeRowFrom(DependencyObject? element)
    {
        for(;element!=null;element=TreeParent(element))
        {
            if(element is FrameworkElement {DataContext:ObjectTreeRow row}) return row;
            if(ReferenceEquals(element,ObjectList)) break;
        }
        return null;
    }
    private bool IsTreeDisclosure(DependencyObject? element)
    {
        for(;element!=null && !ReferenceEquals(element,ObjectList);element=TreeParent(element))
            if(element is ToggleButton {Name:"TreeDisclosure"}) return true;
        return false;
    }
    private ObjectTreeRow? FocusedObjectTreeRow()=>ObjectTreeRowFrom(Keyboard.FocusedElement as DependencyObject) ??
        displayedTree.FirstOrDefault(row=>row.Id==CurrentTreeView.FocusRowId) ?? ObjectList.SelectedItem as ObjectTreeRow ?? displayedTree.FirstOrDefault();

    // All real mouse/key selection uses the same logical-unit policy. Physical child rows are aliases
    // in scene scope; none can bypass Core's whole-group selection or member-scope guards.
    private void SelectObjectTreeRow(ObjectTreeRow row,ModifierKeys modifiers)
    {
        if(!ArrangementSelectionAvailable || !displayedTree.Contains(row)) return;
        var snapshot=editor.Snapshot; var state=CurrentTreeView; var anchor=state.AnchorUnitId;
        var selected=snapshot.SelectedObjectIds.ToHashSet();
        var ctrl=modifiers.HasFlag(ModifierKeys.Control); var shift=modifiers.HasFlag(ModifierKeys.Shift);
        if(shift)
        {
            var start=ObjectTreeUnitIndex(anchor ?? row.SelectionUnit.Id);
            var end=ObjectTreeUnitIndex(row.SelectionUnit.Id);
            if(start<0) start=end;
            if(!ctrl) selected.Clear();
            foreach(var unit in treeSelectionUnits.Skip(Math.Min(start,end)).Take(Math.Abs(end-start)+1)) selected.UnionWith(unit.ObjectIds);
        }
        else if(ctrl && row.ObjectIds.All(selected.Contains)) selected.ExceptWith(row.ObjectIds);
        else {if(!ctrl) selected.Clear(); selected.UnionWith(row.ObjectIds);}
        Guid? primary=selected.Contains(row.Node.Id) ? row.Node.Id : row.ObjectIds.FirstOrDefault(id=>selected.Contains(id));
        if(primary==Guid.Empty) primary=selected.Contains(snapshot.SelectedObjectId ?? Guid.Empty) ? snapshot.SelectedObjectId : null;
        var result=editor.SelectMany(selected,primary,snapshot.Revision);
        if(result.Succeeded)
        {
            Refresh();
            state.AnchorUnitId=shift ? anchor ?? row.SelectionUnit.Id : row.SelectionUnit.Id;
            FocusObjectTreeRow(row);
        }
        else Message(result.Message,true);
    }
    private int ObjectTreeUnitIndex(Guid id)
    {for(var i=0;i<treeSelectionUnits.Length;i++) if(treeSelectionUnits[i].Id==id) return i; return -1;}
    private void ActivateObjectTreeRow(ObjectTreeRow row)
    {
        if(editor.Snapshot.EditingGroupId!=null || !ArrangementSelectionAvailable) return;
        enteringFromTreeRow=true;
        try
        {
            if(row.IsGroup) EnterMemberScope(row.Id);
            else if(row.ParentGroupId is {} groupId) EnterMemberScope(groupId,row.Node.Id);
        }
        finally {enteringFromTreeRow=false;}
    }
    private void ObjectTreeMouseDown(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left || ObjectTreeRowFrom(e.OriginalSource as DependencyObject) is not {} row) return;
        BeginObjectTreeInteraction(); e.Handled=true;
        if(IsTreeDisclosure(e.OriginalSource as DependencyObject))
        {SetObjectTreeExpanded(row,!row.IsExpanded); FocusObjectTreeRow(row); return;}
        SelectObjectTreeRow(row,Keyboard.Modifiers);
        if(e.ClickCount==2) ActivateObjectTreeRow(row);
    }
    private void ObjectTreeDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left || IsTreeDisclosure(e.OriginalSource as DependencyObject) ||
            ObjectTreeRowFrom(e.OriginalSource as DependencyObject) is not {} row) return;
        BeginObjectTreeInteraction(); ActivateObjectTreeRow(row); e.Handled=true;
    }
    private bool NavigateObjectTree(Key key,ModifierKeys modifiers)
    {
        if(!ArrangementSelectionAvailable || FocusedObjectTreeRow() is not {} row) return false;
        BeginObjectTreeInteraction();
        if(key==Key.Enter) {ActivateObjectTreeRow(row); return true;}
        if(key is Key.Left or Key.Right)
        {
            if(row.IsGroup)
            {
                if(key==Key.Left || !row.IsExpanded) {SetObjectTreeExpanded(row,key==Key.Right); FocusObjectTreeRow(row);}
                else if(displayedTree.FirstOrDefault(r=>r.IsAlias && r.ParentGroupId==row.Id) is {} child) FocusObjectTreeRow(child);
            }
            else if(key==Key.Left && row.IsAlias && displayedTree.FirstOrDefault(r=>r.Id==row.ParentGroupId) is {} parent) FocusObjectTreeRow(parent);
            return true;
        }
        if(key==Key.Space)
        {
            if(row.IsGroup && modifiers==ModifierKeys.None) {SetObjectTreeExpanded(row,!row.IsExpanded); FocusObjectTreeRow(row);}
            else SelectObjectTreeRow(row,modifiers);
            return true;
        }
        var index=displayedTree.IndexOf(row);
        var page=Math.Max(1,(int)(ObjectList.ActualHeight/ObjectTreeRowHeight)-1);
        var next=key switch {Key.Up=>index-1,Key.Down=>index+1,Key.Home=>0,Key.End=>displayedTree.Count-1,Key.PageUp=>index-page,Key.PageDown=>index+page,_=>-1};
        if(key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)) return false;
        var target=displayedTree[Math.Clamp(next,0,displayedTree.Count-1)];
        if(modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Shift)) FocusObjectTreeRow(target);
        else SelectObjectTreeRow(target,modifiers);
        return true;
    }
    private void ObjectTreeKeyDown(object sender,KeyEventArgs e)
    {if(!e.Handled && NavigateObjectTree(e.Key,Keyboard.Modifiers)) e.Handled=true;}

    private void ObjectTreeSelectionChanged(SelectionChangedEventArgs e)
    {
        if(refreshing) return;
        if(!ArrangementSelectionAvailable) {RefreshObjectTree(editor.Snapshot.Scene,editor.Snapshot.SelectedObjectIds); return;}
        var snapshot=editor.Snapshot;
        var ids=SelectedTreeUnits().SelectMany(n=>n.ObjectIds).Distinct().ToArray();
        var added=e.AddedItems.OfType<ObjectTreeRow>().LastOrDefault();
        Guid? primary=added is {IsGroup:false} ? added.Id : added?.ObjectIds[0] ??
            (ids.Contains(snapshot.SelectedObjectId ?? Guid.Empty) ? snapshot.SelectedObjectId : null);
        var result=editor.SelectMany(ids,primary,snapshot.Revision); Refresh();
        if(added!=null) CurrentTreeView.AnchorUnitId=added.SelectionUnit.Id;
        if(!result.Succeeded) Message(result.Message,true);
    }
}
