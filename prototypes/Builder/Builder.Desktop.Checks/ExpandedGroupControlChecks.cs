using Builder.Core;
using Builder.Desktop;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static async Task RunExpandedGroupControls()
    {
        await InitializeExpandedGroupHarness();await PrepareExpandedGroup(false);await CheckExpandedGroupSurface();
        await CheckExpandedRecycling();
        await CheckExpandedSelectionNavigation();
        await CheckExpandedScopeHistory();
        await CheckRapidExpandedRestoration();
        await CheckExpandedRenderColors();
        await CheckIncrementalExpandedScopeRows();
    }
    private static async Task RunExpandedGroupLimitControls()
    {
        await InitializeExpandedGroupHarness();await PrepareExpandedGroup(true);await CheckExpandedGroupSurface();
        var source=editor.Snapshot;var group=source.Scene.Groups.Single();
        AssertRenderedSelectionColors(group.ObjectIds);
        Ok(editor.EnterGroup(group.Id,group.ObjectIds[^1]));Refresh();await Idle();AssertRenderedSelectionColors([group.ObjectIds[^1]]);
        Ok(editor.ExitGroup());Refresh();await Idle();AssertRenderedSelectionColors(group.ObjectIds);
        Assert(editor.Snapshot.Scene==source.Scene && editor.Snapshot.IsDirty==source.IsDirty && editor.Snapshot.CanUndo==source.CanUndo,"Limit selection colors changed accepted geometry/history.");
        observations.Add(new{test="Limit viewport selection colors",allSelected=1000,scopedSelected=1,allRestored=1000});
        // Same adapter used by routed keyboard events, at the full limit; no OS focus claim.
        void Navigate(Key key)=>Assert((bool)Invoke("NavigateObjectTree",key,ModifierKeys.None)!,"Limit navigation was not handled: "+key);
        Navigate(Key.End);await Idle();var last=group.ObjectIds[^1];
        Assert(editor.Snapshot.SelectedObjectId==last && editor.Snapshot.SelectedObjectIds.SequenceEqual(group.ObjectIds),"End on a scene alias lost whole-group selection or final primary.");
        var sceneOffset=ObjectTreeVisuals(Named<ListBox>("ObjectList")).OfType<ScrollViewer>().First().VerticalOffset;
        Navigate(Key.Enter);await Idle();
        Assert(editor.Snapshot.EditingGroupId==group.Id && editor.Snapshot.SelectedObjectIds.SequenceEqual([last]),"End/Enter did not enter the final member at the limit.");
        AssertTreeMemberInsideViewport(last);
        Navigate(Key.Up);await Idle();Assert(editor.Snapshot.SelectedObjectId==group.ObjectIds[^2],"Up after limit entry did not continue from the final member.");
        Navigate(Key.End);await Idle();Click(Named<Button>("DoneEditingButton"));await Idle();
        Assert(editor.Snapshot.EditingGroupId==null && editor.Snapshot.SelectedObjectId==last && editor.Snapshot.SelectedObjectIds.SequenceEqual(group.ObjectIds),"Limit Done lost group/primary selection.");
        Near(ObjectTreeVisuals(Named<ListBox>("ObjectList")).OfType<ScrollViewer>().First().VerticalOffset,sceneOffset,.5);
        AssertTreeMemberInsideViewport(last);Navigate(Key.Home);await Idle();
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual(group.ObjectIds),"Home after Done selected only a member.");
        Assert(editor.Snapshot.Scene==source.Scene && editor.Snapshot.IsDirty==source.IsDirty && editor.Snapshot.CanUndo==source.CanUndo,"Limit row navigation changed accepted geometry/history.");
    }
    private static async Task InitializeExpandedGroupHarness()
    {
        if(window is null)
        {
            window=CreateCheckWindow();
            editor=(EditorSession)typeof(MainWindow).GetField("editor",PrivateInstance)!.GetValue(window)!;
            surface=(FrameworkElement)window.Content;
        }
        Refresh();Layout(1040,680);await Idle();
    }
    private static async Task PrepareExpandedGroup(bool limits)
    {
        if(limits)
        {
            var input=Path.Combine(root,".local/evidence/performance-20261019/inputs/combined-1000-100000.3mf");
            Assert(File.Exists(input),"Generate opt-in fixtures with python scripts/generate_fixtures.py --performance before --expanded-limits-only.");
            Ok(await editor.OpenAsync(input,decision:DirtyDecision.Discard));
        }
        else
        {
            Ok(await editor.OpenAsync(Path.Combine(root,"fixtures/generated/cube-20mm.stl"),decision:DirtyDecision.Discard));
            for(var i=1;i<80;i++)Ok(editor.Execute(new AddCube()));
            Ok(await editor.PreviewArrangementAsync(new GroupRequest(editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray(),"Bounded members")));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));
        }
        var group=editor.Snapshot.Scene.Groups.Single();var lastId=group.ObjectIds[^1];
        Ok(editor.EnterGroup(group.Id,lastId));
        Ok(editor.Execute(new RenameObject(lastId,"Virtual member 0999"){MemberGroupId=group.Id}));Ok(editor.ExitGroup());
        Refresh();Layout(1040,680);await Idle();
    }
    private static async Task CheckExpandedGroupSurface()
    {
        var group=editor.Snapshot.Scene.Groups.Single();
        var list=Named<ListBox>("ObjectList");
        var toggle=ObjectTreeToggle(group.Id);
        if(toggle.IsChecked!=true){toggle.IsChecked=true;Layout(1040,680);await Idle();}
        var accepted=editor.Snapshot;
        var names=accepted.Scene.Objects.Select(o=>o.Name).ToHashSet();
        int LeafLabels()=>ObjectTreeVisuals(list).OfType<TextBlock>().Count(t=>names.Contains(t.Text) && t.Visibility==Visibility.Visible && t.ActualHeight>0);
        var initialRealized=LeafLabels();var initialVisuals=ObjectTreeVisuals(list).Count();
        var scroll=ObjectTreeVisuals(list).OfType<ScrollViewer>().First();
        scroll.ScrollToEnd();Layout(1040,680);await Idle();
        var last=ObjectTreeVisuals(list).OfType<TextBlock>().SingleOrDefault(t=>t.Text=="Virtual member 0999" && t.ActualHeight>0);
        var bounds=last is null ? Rect.Empty : Bounds(last);var viewport=Bounds(list);
        var lastReachable=last!=null && bounds.Top>=viewport.Top-1 && bounds.Bottom<=viewport.Bottom+1;
        var finalRealized=LeafLabels();var finalVisuals=ObjectTreeVisuals(list).Count();
        SameAccepted(accepted);
        observations.Add(new{test="Expanded 1000-member visible-surface realization",members=group.ObjectIds.Length,triangles=accepted.Scene.TriangleCount,
            initialRealized,initialVisuals,finalRealized,finalVisuals,lastReachable,lastBounds=bounds.ToString(),listBounds=viewport.ToString(),scroll.VerticalOffset,scroll.ExtentHeight,scroll.ViewportHeight});
        Capture($"50-expanded-{group.ObjectIds.Length}-last-member",1040,680);
        Assert(lastReachable,"The final member of an expanded group is not reachable by scrolling.");
        var maximumRealized=Math.Min(80,group.ObjectIds.Length/2);
        Assert(initialRealized<=maximumRealized && finalRealized<=maximumRealized && initialVisuals<=2000 && finalVisuals<=2000,
            $"Expanded group realizes {initialRealized}/{finalRealized} leaf labels and {initialVisuals}/{finalVisuals} visuals for a 190px viewport; the visible surface must remain bounded.");
        Pass($"Expanded {group.ObjectIds.Length}-member group keeps realized leaf labels/visuals bounded while the final member remains reachable without document or selection changes");
    }

    private static async Task<(ImmutableArray<Guid> Leaves,ImmutableArray<Guid> Groups)> PrepareExpandedUnits()
    {
        Ok(await editor.OpenAsync(Path.Combine(root,"fixtures/generated/cube-20mm.stl"),decision:DirtyDecision.Discard));
        for(var i=1;i<82;i++)Ok(editor.Execute(new AddCube()));
        var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        for(var i=0;i<ids.Length;i++)Ok(editor.Execute(new RenameObject(ids[i],$"Row {i:00}")));
        for(var i=0;i<4;i++)
        {Ok(await editor.PreviewArrangementAsync(new GroupRequest(ids.Skip(i*20).Take(20).ToImmutableArray(),$"Group {i+1}")));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));}
        Refresh();Layout(1040,680);await Idle();
        return(ids,editor.Snapshot.Scene.Groups.Select(g=>g.Id).ToImmutableArray());
    }

    private static async Task CheckExpandedRecycling()
    {
        var fixture=await PrepareExpandedUnits();var before=editor.Snapshot;
        var list=Named<ListBox>("ObjectList");var scroll=ObjectTreeVisuals(list).OfType<ScrollViewer>().First();
        var expected=new Dictionary<Guid,bool>();
        for(var round=0;round<3;round++)
        {
            for(var i=0;i<fixture.Groups.Length;i++)
            {
                var id=fixture.Groups[i];var expanded=(i+round)%2==0;ObjectTreeToggle(id).IsChecked=expanded;expected[id]=expanded;
                Layout(1040,680);await Idle();
            }
            scroll.ScrollToEnd();Layout(1040,680);await Idle();scroll.ScrollToHome();Layout(1040,680);await Idle();
            foreach(var id in fixture.Groups)Assert(ObjectTreeToggle(id).IsChecked==expected[id],"Recycled disclosure inherited another group's expansion state.");
            var rows=list.Items.OfType<ObjectTreeRow>().ToArray();
            foreach(var id in fixture.Groups)
                Assert(rows.Count(r=>r.ParentGroupId==id)==(expected[id] ? 20 : 0),"Disclosure did not update its own visible child rows.");
            Assert(ObjectTreeVisuals(list).OfType<TextBlock>().Count(t=>t.Text.StartsWith("Row ",StringComparison.Ordinal) && t.ActualHeight>0)<=40,"Multi-group recycling realized an unbounded set of member labels.");
            SameAccepted(before);
        }
        var target=fixture.Groups[3];ObjectTreeToggle(target).IsChecked=true;expected[target]=true;Layout(1040,680);await Idle();
        var aliasContainer=ObjectTreeContainer(fixture.Leaves[60]);ChooseTreeRoot(target);await Idle();
        Assert(AutomationProperties.GetHelpText(aliasContainer).Contains("Member of Group 4",StringComparison.Ordinal),"Rendered alias has no owning-group help text.");
        var selected=editor.Snapshot;var offset=scroll.VerticalOffset;
        Named<TextBox>("ObjectName").Text="Renamed recycled group";Click(Named<Button>("RenameButton"));Layout(1040,680);await Idle();
        Assert(editor.Snapshot.Scene.Groups.Single(g=>g.Id==target).Name=="Renamed recycled group","Recycled group rename targeted a different unit.");
        Near(scroll.VerticalOffset,offset,.5);
        Assert(list.Items.OfType<ObjectTreeRow>().Where(r=>r.SelectionUnit.Id==target).All(r=>r.SelectionUnit.Name=="Renamed recycled group"),"Visible child aliases retain a stale group name after rename.");
        Assert(AutomationProperties.GetHelpText(aliasContainer).Contains("Member of Renamed recycled group",StringComparison.Ordinal),"Bound alias accessibility help did not update after its owner was renamed.");
        Click(Named<Button>("UndoButton"));await Idle();Assert(editor.Snapshot.Scene==selected.Scene,"Recycled group rename Undo lost source content.");Near(scroll.VerticalOffset,offset,.5);
        Assert(AutomationProperties.GetHelpText(aliasContainer).Contains("Member of Group 4",StringComparison.Ordinal),"Bound alias accessibility help retained the renamed owner after Undo.");
        foreach(var id in fixture.Groups)Assert(ObjectTreeToggle(id).IsChecked==expected[id],"Rename/Undo discarded another group's disclosure state.");
        observations.Add(new{test="Multi-group recycling",groups=4,leaves=82,rounds=3,renameScrollOffset=offset});Capture("51-expanded-recycling",1040,680);
        Pass("Multiple expanded/collapsed groups recycle bounded rows without sharing disclosure state; rename and Undo preserve group ownership, aliases and scroll position");
    }

    private static async Task CheckExpandedScopeHistory()
    {
        var fixture=await PrepareExpandedUnits();var group=fixture.Groups[3];var member=fixture.Leaves[79];
        var list=Named<ListBox>("ObjectList");var scroll=ObjectTreeVisuals(list).OfType<ScrollViewer>().First();
        ObjectTreeToggle(group).IsChecked=true;
        var memberContainer=ObjectTreeContainer(member);Layout(1040,680);await Idle();
        var label=ObjectTreeVisuals(memberContainer).OfType<TextBlock>().Single(t=>t.Text=="Row 79");
        var before=editor.Snapshot;var sceneOffset=scroll.VerticalOffset;
        list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=label});
        await Idle();var entered=editor.Snapshot;
        Assert(entered.EditingGroupId==group && entered.SelectedObjectId==member && entered.SelectedObjectIds.SequenceEqual(new[]{member}),"Member double-click did not enter its own group/member.");
        Assert(entered.Scene==before.Scene && entered.IsDirty==before.IsDirty && entered.CanUndo==before.CanUndo,"Member double-click changed accepted content/history.");
        Assert(list.Items.OfType<ObjectTreeRow>().All(r=>r.SelectionUnit.Id==r.Id && r.ObjectIds.Length==1) && list.Items.Count==20,"Member scope kept scene aliases or outsiders.");
        var entryTarget=ObjectTreeVisuals(list).OfType<TextBlock>().SingleOrDefault(t=>t.Text=="Row 79" && t.ActualHeight>0);
        var entryBounds=entryTarget is null ? Rect.Empty : Bounds(entryTarget);var listBounds=Bounds(list);
        Assert(entryTarget!=null && entryBounds.Top>=listBounds.Top-1 && entryBounds.Bottom<=listBounds.Bottom+1,"Explicit member entry did not reveal the selected last member before any test scrolling.");
        ObjectTreeContainer(member);Layout(1040,680);await Idle();var memberOffset=scroll.VerticalOffset;
        var beforeRename=editor.Snapshot;
        Named<TextBox>("ObjectName").Text="Edited final member";Click(Named<Button>("RenameButton"));await Idle();Near(scroll.VerticalOffset,memberOffset,.5);
        Assert(list.Items.OfType<ObjectTreeRow>().Single(r=>r.Id==member).Name=="Edited final member","Member rename updated a group/root alias instead of its row.");
        Click(Named<Button>("DoneEditingButton"));await Idle();
        Assert(editor.Snapshot.EditingGroupId is null && editor.Snapshot.SelectedObjectIds.SequenceEqual(fixture.Leaves.Skip(60).Take(20)),"Done lost whole-group selection.");
        Near(scroll.VerticalOffset,sceneOffset,.5);
        Click(Named<Button>("UndoButton"));await Idle();
        Assert(editor.Snapshot.Scene==beforeRename.Scene && editor.Snapshot.EditingGroupId==group && editor.Snapshot.SelectedObjectIds.SequenceEqual(beforeRename.SelectedObjectIds),"Undo after Done did not restore member scope/full selection.");
        Near(scroll.VerticalOffset,memberOffset,.5);
        Click(Named<Button>("RedoButton"));await Idle();Assert(editor.Snapshot.EditingGroupId is null,"Redo did not restore captured scene scope.");Near(scroll.VerticalOffset,sceneOffset,.5);
        var finalAlias=list.Items.OfType<ObjectTreeRow>().Single(r=>r.Id==member);
        Assert(finalAlias.Name=="Edited final member" && finalAlias.SelectionUnit.Id==group,"Scene alias was stale after scoped rename/Undo/Redo.");
        var different=fixture.Leaves[60];var differentContainer=ObjectTreeContainer(different);await Idle();
        var differentLabel=ObjectTreeVisuals(differentContainer).OfType<TextBlock>().Single(t=>t.Text=="Row 60");var differentSceneOffset=scroll.VerticalOffset;
        list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=differentLabel});await Idle();
        var differentVisible=ObjectTreeVisuals(list).OfType<TextBlock>().SingleOrDefault(t=>t.Text=="Row 60" && t.ActualHeight>0);
        var differentBounds=differentVisible is null ? Rect.Empty : Bounds(differentVisible);
        Assert(editor.Snapshot.SelectedObjectId==different && differentVisible!=null && differentBounds.Top>=Bounds(list).Top-1 && differentBounds.Bottom<=Bounds(list).Bottom+1,
            "Re-entering an already visited group restored an old member anchor and hid the new explicit entry target.");
        Click(Named<Button>("DoneEditingButton"));await Idle();Near(scroll.VerticalOffset,differentSceneOffset,.5);
        var row=ObjectTreeContainer(member);var currentLabel=ObjectTreeVisuals(row).OfType<TextBlock>().Single(t=>t.Text=="Edited final member");
        list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=currentLabel});await Idle();
        var guarded=editor.Snapshot;
        Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="50";Click(Named<Button>("PreviewTransformButton"));await Idle();var previewed=editor.Snapshot;
        Assert(previewed.Preview!=null,"Guard probe did not establish a preview.");
        list.SelectedItems.Clear();list.SelectedItems.Add(list.Items[0]);
        list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=currentLabel});
        SameAccepted(guarded);Assert(editor.Snapshot.Preview?.Id==previewed.Preview!.Id,"Tree input replaced or accepted a pending preview.");
        Click(Named<Button>("CancelTransformButton"));await Idle();SameAccepted(guarded);
        Assert(list.SelectedItems.OfType<ObjectTreeRow>().SelectMany(r=>r.SelectionUnit.ObjectIds).ToHashSet().SetEquals(guarded.SelectedObjectIds),"Cancel restored Core selection but left stale visual row selection.");
        observations.Add(new{test="Scene/member tree context restoration",sceneOffset,memberOffset,selection=guarded.SelectedObjectIds});Capture("52-expanded-scope-restored",1040,680);
        Pass("Member double-click, Done and scoped rename Undo/Redo preserve explicit member/whole-group selection and independent scene/member scroll anchors; preview/cancel guards reject tree selection changes");
    }

    private static async Task CheckExpandedSelectionNavigation()
    {
        var fixture=await PrepareExpandedUnits();var list=Named<ListBox>("ObjectList");var document=editor.Snapshot;
        ObjectTreeRow Row(Guid id)=>list.Items.OfType<ObjectTreeRow>().Single(r=>r.Id==id);
        void Select(Guid id,ModifierKeys modifiers)=>Invoke("SelectObjectTreeRow",Row(id),modifiers);
        void Navigate(Key key,ModifierKeys modifiers=ModifierKeys.None)=>Assert((bool)Invoke("NavigateObjectTree",key,modifiers)!,"Tree did not handle navigation: "+key);
        void Selection(IEnumerable<Guid> expected)
        {
            var target=expected.ToHashSet();var snapshot=editor.Snapshot;
            Assert(snapshot.SelectedObjectIds.ToHashSet().SetEquals(target),"Tree selection did not follow canonical whole-unit/member range policy.");
            Assert(snapshot.Scene==document.Scene && snapshot.IsDirty==document.IsDirty && snapshot.CanUndo==document.CanUndo && snapshot.CanRedo==document.CanRedo,"Tree navigation changed document/history.");
            var selectedUnits=list.SelectedItems.OfType<ObjectTreeRow>().ToArray();
            Assert(selectedUnits.All(r=>r.Id==r.SelectionUnit.Id) && selectedUnits.SelectMany(r=>r.ObjectIds).ToHashSet().SetEquals(target),"Selector retains aliases or omits a canonical selected unit.");
            foreach(var row in list.Items.OfType<ObjectTreeRow>())Assert(row.IsUnitSelected==row.ObjectIds.All(target.Contains),"Recycled alias highlight differs from its owning unit's selection.");
        }
        Select(fixture.Leaves[5],ModifierKeys.None);Selection(fixture.Leaves.Take(20));
        Select(fixture.Leaves[45],ModifierKeys.Shift);Selection(fixture.Leaves.Take(60));
        Select(fixture.Leaves[25],ModifierKeys.Control);Selection(fixture.Leaves.Take(20).Concat(fixture.Leaves.Skip(40).Take(20)));
        Select(fixture.Leaves[81],ModifierKeys.Control|ModifierKeys.Shift);Selection(fixture.Leaves);
        Select(fixture.Leaves[45],ModifierKeys.Control);Selection(fixture.Leaves.Take(40).Concat(fixture.Leaves.Skip(60)));
        Select(fixture.Leaves[19],ModifierKeys.None);Navigate(Key.Down);Selection(fixture.Leaves.Skip(20).Take(20));Assert(editor.Snapshot.SelectedObjectId==fixture.Leaves[20],"Down across a group boundary targeted an alias or wrong unit.");
        Navigate(Key.End);Selection([fixture.Leaves[81]]);Navigate(Key.Up);Selection([fixture.Leaves[80]]);Navigate(Key.Down);Selection([fixture.Leaves[81]]);
        Navigate(Key.PageUp);Selection(fixture.Leaves.Skip(60).Take(20));Navigate(Key.PageDown);Selection([fixture.Leaves[81]]);
        Navigate(Key.Home);Selection(fixture.Leaves.Take(20));var beforeDisclosure=editor.Snapshot;
        Navigate(Key.Left);Assert(ObjectTreeToggle(fixture.Groups[0]).IsChecked==false,"Left did not collapse focused group.");SameAccepted(beforeDisclosure);
        Navigate(Key.Right);Assert(ObjectTreeToggle(fixture.Groups[0]).IsChecked==true,"Right did not expand focused group.");SameAccepted(beforeDisclosure);
        Navigate(Key.Space);Assert(ObjectTreeToggle(fixture.Groups[0]).IsChecked==false,"Space did not toggle disclosure.");SameAccepted(beforeDisclosure);
        Navigate(Key.Space);Assert(ObjectTreeToggle(fixture.Groups[0]).IsChecked==true,"Second Space did not restore disclosure.");SameAccepted(beforeDisclosure);
        Navigate(Key.Right);Navigate(Key.Down);Selection(fixture.Leaves.Take(20));Assert(editor.Snapshot.SelectedObjectId==fixture.Leaves[1],"Right then Down did not navigate visible child rows independently of whole-group selection.");
        Select(fixture.Leaves[5],ModifierKeys.None);var focused=editor.Snapshot;Navigate(Key.Down,ModifierKeys.Control);SameAccepted(focused);Navigate(Key.Enter);await Idle();
        Assert(editor.Snapshot.EditingGroupId==fixture.Groups[0] && editor.Snapshot.SelectedObjectId==fixture.Leaves[6],"Control-only navigation changed selection or Enter activated the wrong focused member.");
        Select(fixture.Leaves[0],ModifierKeys.None);Selection([fixture.Leaves[0]]);
        Select(fixture.Leaves[5],ModifierKeys.Control);Selection([fixture.Leaves[0],fixture.Leaves[5]]);
        Select(fixture.Leaves[8],ModifierKeys.Shift);Selection(fixture.Leaves.Skip(5).Take(4));
        Select(fixture.Leaves[2],ModifierKeys.Control|ModifierKeys.Shift);Selection(fixture.Leaves.Skip(2).Take(7));
        var beforeBusy=editor.Snapshot;
        using(var barrier=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {
            var job=editor.DuplicateSelectionAsync([fixture.Leaves[2]],ct.Token,new ExpandedProgress(p=>{if(p.Stage=="Ready to duplicate"){barrier.Set();Assert(release.Wait(TimeSpan.FromSeconds(10)),"Tree busy barrier timed out.");}}),memberGroupId:fixture.Groups[0]);
            try
            {
                Assert(barrier.Wait(TimeSpan.FromSeconds(5)),"Tree busy lease was not acquired.");Refresh();var busy=editor.Snapshot;
                Select(fixture.Leaves[9],ModifierKeys.None);Invoke("NavigateObjectTree",Key.End,ModifierKeys.None);
                list.SelectedItems.Clear();list.SelectedItems.Add(Row(fixture.Leaves[10]));Assert(editor.Snapshot==busy,"Tree input changed accepted selection/scope during cancellable work.");
            }
            finally{ct.Cancel();release.Set();}
            Assert((await job).Outcome==Outcome.Cancelled,"Tree busy probe did not cancel.");Refresh();await Idle();SameAccepted(beforeBusy);
        }
        observations.Add(new{test="Tree logical selection/navigation adapter",sceneUnits=6,memberRangeCount=7,method="Explicit modifier/key arguments through the same adapter methods called by routed input; no OS keyboard focus claim"});
        Pass("Whole-unit aliases and independent members share Ctrl/Shift range policy; Home/End/Page/arrow/Enter/disclosure navigation respects focused rows, logical units and busy cancellation guards");
    }
    private sealed class ExpandedProgress(Action<OperationProgress> report):IProgress<OperationProgress>{public void Report(OperationProgress p)=>report(p);}

    private static async Task CheckRapidExpandedRestoration()
    {
        var fixture=await PrepareExpandedUnits();var group=fixture.Groups[3];ChooseTreeRoot(group);await Idle();
        var source=editor.Snapshot;var list=Named<ListBox>("ObjectList");var scroll=ObjectTreeVisuals(list).OfType<ScrollViewer>().First();
        for(var i=0;i<5;i++)
        {
            Click(Named<Button>("EditMembersButton"));
            Assert(editor.Snapshot.EditingGroupId==group,"Rapid entry failed to establish member scope.");
            Click(Named<Button>("DoneEditingButton"));Refresh();
            Assert(editor.Snapshot.EditingGroupId is null && editor.Snapshot.Scene==source.Scene && editor.Snapshot.SelectedObjectIds.SequenceEqual(source.SelectedObjectIds),"Rapid Done/Refresh lost scene scope or canonical whole-group selection.");
        }
        var accepted=editor.Snapshot;
        list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=UIElement.PreviewMouseWheelEvent,Source=list});
        scroll.ScrollToEnd();Layout(1040,680);await Idle();
        Near(scroll.VerticalOffset,scroll.ScrollableHeight,.5);SameAccepted(accepted);
        Assert(list.Items.OfType<ObjectTreeRow>().Count(r=>r.Id==r.SelectionUnit.Id)==6,"A stale member restoration replaced the current scene rows.");
        var last=ObjectTreeVisuals(list).OfType<TextBlock>().SingleOrDefault(t=>t.Text=="Row 81" && t.ActualHeight>0);
        Assert(last!=null && Bounds(last).Top>=Bounds(list).Top-1 && Bounds(last).Bottom<=Bounds(list).Bottom+1,"A deferred restoration overrode newer navigation to the final row.");
        observations.Add(new{test="Rapid scope/reset and newer navigation",transitions=10,scroll.VerticalOffset,scroll.ScrollableHeight});
        Pass("Rapid Enter/Done/Refresh cannot replay a stale member list or override newer routed wheel navigation; final scene rows, selection and last-row visibility remain coherent");
    }

    private static void AssertRenderedSelectionColors(IEnumerable<Guid> selected)
    {
        var expected=selected.ToHashSet();
        var map=(Dictionary<GeometryModel3D,Guid>)typeof(MainWindow).GetField("hitObjects",PrivateInstance)!.GetValue(window)!;
        Assert(map.Count==editor.Snapshot.Scene.Objects.Length,"Rendered leaf model count differs from accepted scene.");
        foreach(var (model,id) in map)
        {
            var color=expected.Contains(id) ? Color.FromRgb(38,132,197) : Color.FromRgb(153,170,185);
            Assert(model.Material is DiffuseMaterial {Brush:SolidColorBrush front} && front.Color==color,"Viewport material has stale or incorrect selected membership: "+id);
            Assert(model.BackMaterial is DiffuseMaterial {Brush:SolidColorBrush back} && back.Color==color,"Back material differs from current selected membership: "+id);
        }
    }
    private static async Task CheckExpandedRenderColors()
    {
        var fixture=await PrepareExpandedUnits();var original=editor.Snapshot;
        Click(Named<Button>("SelectAllButton"));await Idle();AssertRenderedSelectionColors(fixture.Leaves);
        ChooseTreeRoot(fixture.Groups[3]);await Idle();AssertRenderedSelectionColors(fixture.Leaves.Skip(60).Take(20));
        Click(Named<Button>("EditMembersButton"));await Idle();AssertRenderedSelectionColors([fixture.Leaves[60]]);
        ChooseTreeRoot(fixture.Leaves[79]);await Idle();AssertRenderedSelectionColors([fixture.Leaves[79]]);
        Click(Named<Button>("ClearSelectionButton"));await Idle();AssertRenderedSelectionColors([]);
        Click(Named<Button>("DoneEditingButton"));await Idle();AssertRenderedSelectionColors(fixture.Leaves.Skip(60).Take(20));
        Assert(editor.Snapshot.Scene==original.Scene && editor.Snapshot.IsDirty==original.IsDirty && editor.Snapshot.CanUndo==original.CanUndo,"Material selection refresh changed accepted scene/history.");
        observations.Add(new{test="Rendered model selection membership",leaves=82,stages=new[]{"all82","group20","member60","member79","none","wholegroup20"}});
        Pass("Actual front/back viewport materials track Select all, whole-group aliases, individual scoped members, Clear and Done without changing accepted geometry/history");
    }

    private static void AssertTreeMemberInsideViewport(Guid id)
    {
        var list=Named<ListBox>("ObjectList");var row=list.Items.OfType<ObjectTreeRow>().Single(r=>r.Id==id);
        var container=list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
        Assert(container!=null && Bounds(container).Top>=Bounds(list).Top-1 && Bounds(container).Bottom<=Bounds(list).Bottom+1,"Requested member is not realized inside the list viewport.");
    }

    private static async Task CheckIncrementalExpandedScopeRows()
    {
        await PrepareExpandedGroup(false);var list=Named<ListBox>("ObjectList");var group=editor.Snapshot.Scene.Groups.Single();
        var changes=new List<(NotifyCollectionChangedAction Action,int Index,int Count)>();
        var rows=(INotifyCollectionChanged)list.ItemsSource;
        void Changed(object? sender,NotifyCollectionChangedEventArgs e)=>changes.Add((e.Action,e.Action==NotifyCollectionChangedAction.Remove ? e.OldStartingIndex : e.NewStartingIndex,e.OldItems?.Count ?? e.NewItems?.Count ?? 0));
        rows.CollectionChanged+=Changed;
        try
        {
            var scroll=ObjectTreeVisuals(list).OfType<ScrollViewer>().First();
            scroll.ScrollToVerticalOffset(34*40+7.5);Layout(1040,680);await Idle();
            var sceneOffset=scroll.VerticalOffset;var anchor=list.Items.OfType<ObjectTreeRow>().ElementAt(40).Id;
            var anchorContainer=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.Items.OfType<ObjectTreeRow>().Single(r=>r.Id==anchor));
            var anchorTop=Bounds(anchorContainer).Top;var source=editor.Snapshot;
            for(var i=0;i<3;i++)
            {
                changes.Clear();Invoke("EnterMemberScope",group.Id,group.ObjectIds[50]);await Idle();
                Assert(changes.SequenceEqual(new[]{(NotifyCollectionChangedAction.Remove,0,1)}),"Removing only the expanded group header did not publish one normal Remove event.");
                Assert(editor.Snapshot.EditingGroupId==group.Id && editor.Snapshot.SelectedObjectIds.SequenceEqual([group.ObjectIds[50]]) && list.Items.Count==80,"Incremental entry lost canonical member scope or rows.");
                AssertTreeMemberInsideViewport(group.ObjectIds[50]);
                changes.Clear();Click(Named<Button>("DoneEditingButton"));await Idle();
                Assert(changes.SequenceEqual(new[]{(NotifyCollectionChangedAction.Add,0,1)}),"Restoring only the expanded group header did not publish one normal Add event.");
                Assert(editor.Snapshot.EditingGroupId==null && editor.Snapshot.SelectedObjectIds.SequenceEqual(group.ObjectIds) && list.SelectedItems.OfType<ObjectTreeRow>().Single().Id==group.Id,"Incremental Done retained a member Selector alias or lost whole-group selection.");
                Near(scroll.VerticalOffset,sceneOffset,.5);
                anchorContainer=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.Items.OfType<ObjectTreeRow>().Single(r=>r.Id==anchor));
                Near(Bounds(anchorContainer).Top,anchorTop,.5);
                Assert(editor.Snapshot.Scene==source.Scene && editor.Snapshot.IsDirty==source.IsDirty && editor.Snapshot.CanUndo==source.CanUndo,"Incremental scope transitions changed accepted content/history.");
            }
            var fixture=await PrepareExpandedUnits();ChooseTreeRoot(fixture.Groups[3]);var mixed=editor.Snapshot;changes.Clear();
            Click(Named<Button>("EditMembersButton"));await Idle();
            Assert(changes.Count==1 && changes[0].Action==NotifyCollectionChangedAction.Reset && list.Items.Count==20,"Mixed scope replacement did not retain the batched Reset fallback.");
            Assert(list.Items.OfType<ObjectTreeRow>().All(r=>r.SelectionUnit.Id==r.Id && r.ParentGroupId==fixture.Groups[3]),"Fallback Reset retained aliases or members from another group.");
            changes.Clear();Click(Named<Button>("DoneEditingButton"));await Idle();
            Assert(changes.Count==1 && changes[0].Action==NotifyCollectionChangedAction.Reset && list.Items.Count==86 && ObjectTreeRoots().Length==6,"Mixed Done fallback lost scene rows or canonical roots.");
            Assert(editor.Snapshot.Scene==mixed.Scene && editor.Snapshot.SelectedObjectIds.SequenceEqual(mixed.SelectedObjectIds) && editor.Snapshot.IsDirty==mixed.IsDirty && editor.Snapshot.CanUndo==mixed.CanUndo,"Fallback scope replacement changed accepted content/selection/history.");
            observations.Add(new{test="Incremental header before viewport anchor",cycles=3,sceneOffset,anchorTop,events="one Remove/Add for header; one Reset for mixed groups",method="Observed public collection notifications and realized visual bounds; no container identity or OS focus assertion"});
            Pass("One-row scope Insert/Remove before the viewport preserves its pixel anchor and canonical selection; mixed-group replacements retain bounded Reset behavior");
        }
        finally{rows.CollectionChanged-=Changed;}
    }
}
