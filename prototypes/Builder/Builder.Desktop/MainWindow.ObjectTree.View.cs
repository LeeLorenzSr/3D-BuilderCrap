using Builder.Core;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Builder.Desktop;

public partial class MainWindow
{
    private const double ObjectTreeRowHeight=34;
    private readonly record struct TreeScope(Guid? GroupId);
    private sealed class TreeViewState
    {
        public Guid? TopRowId,TopOwnerId,FocusRowId,AnchorUnitId;
        public double OffsetWithinRow;
        public int PreviousTopIndex;
        public ImmutableArray<Guid> Selection=[];
    }
    private sealed record TreeRestore(long Generation,long Navigation,TreeScope Scope,Guid? TopId,Guid? OwnerId,
        int PreviousIndex,double Offset,Guid? FocusId,bool RestoreFocus,IInputElement? FocusAfterReset,Guid? RevealId);
    private readonly Dictionary<TreeScope,TreeViewState> objectTreeViews=[];
    private TreeRestore? pendingTreeRestore;
    private long treeGeneration,treeNavigationGeneration;
    private bool restoringTreeView;
    private bool revealEnteredMember;
    private TreeViewState CurrentTreeView
    {
        get
        {
            var scope=new TreeScope(listedEditingGroupId);
            if(!objectTreeViews.TryGetValue(scope,out var state)) {state=new(); objectTreeViews.Add(scope,state);}
            return state;
        }
    }
    private void BeginObjectTreeInteraction()
    {treeNavigationGeneration++; pendingTreeRestore=null;}
    private ScrollViewer? ObjectTreeScroll()=>FindObjectTreeVisual<ScrollViewer>(ObjectList,_=>true);

    private void CaptureObjectTreeView()
    {
        if(pendingTreeRestore!=null || restoringTreeView || displayedTree.Count==0 || ObjectList.ActualHeight<=0 ||
            ObjectsOverview.Visibility==Visibility.Collapsed || ObjectTreeScroll() is not {} scroll) return;
        var state=CurrentTreeView;
        var index=Math.Clamp((int)Math.Floor(scroll.VerticalOffset/ObjectTreeRowHeight),0,displayedTree.Count-1);
        state.TopRowId=displayedTree[index].Id; state.TopOwnerId=displayedTree[index].SelectionUnit.Id;
        state.PreviousTopIndex=index; state.OffsetWithinRow=scroll.VerticalOffset-index*ObjectTreeRowHeight;
    }
    private void QueueObjectTreeRestore(bool scopeChanged,bool hadTreeFocus,Guid? createdSelection=null)
    {
        var state=CurrentTreeView;
        if(state.TopRowId==null && displayedTree.Count>0)
        {
            var primary=editor.Snapshot.SelectedObjectId;
            var first=scopeChanged && listedEditingGroupId!=null ? displayedTree.FirstOrDefault(r=>r.Id==primary) ?? displayedTree[0] : displayedTree[0];
            state.TopRowId=first.Id; state.TopOwnerId=first.SelectionUnit.Id; state.FocusRowId??=first.Id;
        }
        var reveal=revealEnteredMember ? editor.Snapshot.SelectedObjectId : createdSelection;
        revealEnteredMember=false;
        if(reveal!=null) state.FocusRowId=reveal;
        pendingTreeRestore=new(++treeGeneration,treeNavigationGeneration,new(listedEditingGroupId),state.TopRowId,state.TopOwnerId,
            state.PreviousTopIndex,state.OffsetWithinRow,state.FocusRowId,hadTreeFocus,Keyboard.FocusedElement,reveal);
    }
    private void ObjectTreeLayoutUpdated(object? sender,EventArgs e)
    {
        if(refreshing || restoringTreeView || pendingTreeRestore is not {} restore || ObjectList.ActualHeight<=0 ||
            ObjectsOverview.Visibility==Visibility.Collapsed) return;
        if(restore.Generation!=treeGeneration || restore.Navigation!=treeNavigationGeneration || restore.Scope!=new TreeScope(editor.Snapshot.EditingGroupId))
        {pendingTreeRestore=null; return;}
        if(ObjectTreeScroll() is not {} scroll) return;
        var index=displayedTree.ToList().FindIndex(r=>r.Id==restore.TopId);
        var offset=restore.Offset;
        if(index<0) {index=displayedTree.ToList().FindIndex(r=>r.Id==restore.OwnerId); offset=0;}
        if(index<0) index=Math.Clamp(restore.PreviousIndex,0,Math.Max(0,displayedTree.Count-1));
        var desiredOffset=index*ObjectTreeRowHeight+offset;
        if(restore.RevealId is {} reveal)
        {
            var revealIndex=displayedTree.ToList().FindIndex(r=>r.Id==reveal);
            var height=Math.Max(ObjectTreeRowHeight,scroll.ViewportHeight);
            if(revealIndex>=0)
            {
                var top=revealIndex*ObjectTreeRowHeight;
                if(top<desiredOffset) desiredOffset=top;
                else if(top+ObjectTreeRowHeight>desiredOffset+height) desiredOffset=top+ObjectTreeRowHeight-height;
            }
        }
        pendingTreeRestore=null; restoringTreeView=true;
        try
        {
            scroll.ScrollToVerticalOffset(desiredOffset);
            // Reset may evict the focused container. Restore that loss only while its resulting focus
            // is unchanged; a newer toolbar, viewport or preview focus always wins.
            if(restore.RestoreFocus && (ObjectList.IsKeyboardFocusWithin ||
                ReferenceEquals(Keyboard.FocusedElement,restore.FocusAfterReset)) && restore.FocusId is {} id &&
                displayedTree.FirstOrDefault(r=>r.Id==id) is {} row) FocusObjectTreeRow(row,false);
        }
        finally {restoringTreeView=false;}
    }
    private void ObjectTreeScrollChanged(object sender,ScrollChangedEventArgs e)
    {if(!refreshing && !restoringTreeView && pendingTreeRestore==null) CaptureObjectTreeView();}
    private void ObjectTreeMouseWheel(object sender,MouseWheelEventArgs e)=>BeginObjectTreeInteraction();
    private void ObjectTreeGotKeyboardFocus(object sender,KeyboardFocusChangedEventArgs e)
    {
        if(refreshing || restoringTreeView) return;
        if(ObjectTreeRowFrom(e.NewFocus as DependencyObject) is {} row) CurrentTreeView.FocusRowId=row.Id;
    }
    private void FocusObjectTreeRow(ObjectTreeRow row,bool ensureVisible=true)
    {
        if(!displayedTree.Contains(row)) return;
        var previous=refreshing; refreshing=true;
        try
        {
            if(ensureVisible) {ObjectList.ScrollIntoView(row); ObjectList.UpdateLayout();}
            if(ObjectList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container) container.Focus();
            SynchronizeObjectTreeSelection(editor.Snapshot.SelectedObjectIds);
        }
        finally {refreshing=previous;}
        CurrentTreeView.FocusRowId=row.Id;
        CaptureObjectTreeView();
    }
}
