using Builder.Core;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.IO;

internal static partial class Program
{
    private static string SeparateInputs=>Path.Combine(root,".local","evidence","separate-20261020","inputs");
    private static async Task OpenSeparate(string file)
    {Ok(await editor.OpenAsync(Path.Combine(SeparateInputs,file),decision:DirtyDecision.Discard));Refresh();Layout(1280,780);Click("Fit scene");}
    private static async Task PreviewSeparate()
    {
        Click(Named<Button>("PreviewSeparateButton"));await Idle();
        Assert(editor.Snapshot.Preview is {Request:SeparateRequest,Separate:not null},"Separate preview failed: "+Named<TextBlock>("Status").Text);
    }
    private static Dictionary<GeometryModel3D,Guid> SeparateDrawn()
        =>(Dictionary<GeometryModel3D,Guid>)typeof(Builder.Desktop.MainWindow).GetField("hitObjects",PrivateInstance)!.GetValue(window)!;
    private static Color SeparateDrawnColor(Guid id)
        =>((SolidColorBrush)((DiffuseMaterial)SeparateDrawn().Single(p=>p.Value==id).Key.Material).Brush).Color;
    private static async Task RunSeparateControls()
    {
        await OpenSeparate("two-hollow-boxes.3mf");var sourceId=editor.Snapshot.SelectedObjectId!.Value;
        Ok(editor.Execute(new AddCube()));var contextId=editor.Snapshot.SelectedObjectId!.Value;Ok(editor.SelectMany([sourceId]));Refresh();Click("Fit scene");var before=editor.Snapshot;
        Click(Named<Button>("SeparateButton"));Assert(!Named<Button>("ApplySeparateButton").IsEnabled,"Separate accepts before review.");InspectionFooterInside("PreviewSeparateButton","ApplySeparateButton","CancelSeparateButton");Capture("separate-settings-minimum",1040,680);var camera=CameraPose();
        await PreviewSeparate();var candidate=editor.Snapshot.Preview!;var pieces=candidate.Separate!.Pieces;SameAccepted(before);
        Assert(pieces.Length==2&&pieces.All(p=>p.CavityCount==1&&p.BoundaryCount==2)&&pieces[0].ObjectId==sourceId&&pieces.Sum(p=>p.TriangleCount)==candidate.Separate.OriginalTriangleCount,"Piece/cavity/triangle review does not match Core.");
        var list=Named<ListBox>("SeparatePieceList");Assert(list.Items.Count==3&&VirtualizingPanel.GetIsVirtualizing(list)&&VirtualizingPanel.GetVirtualizationMode(list)==VirtualizationMode.Recycling&&ObjectTreeVisuals(list).OfType<VirtualizingStackPanel>().Any(),"Piece list does not use the existing virtualized/recycling presentation pattern.");
        Assert(SeparateDrawn().Values.ToHashSet().SetEquals(pieces.Select(p=>p.ObjectId).Append(contextId))&&SeparateDrawnColor(pieces[0].ObjectId)!=SeparateDrawnColor(pieces[1].ObjectId),"All-pieces preview did not show distinct piece colors and unrelated context.");Capture("separate-all-minimum",1040,680);
        list.SelectedIndex=2;Assert(SeparateDrawn().Values.ToHashSet().SetEquals([pieces[1].ObjectId,contextId])&&Named<TextBlock>("SeparateSummary").Text.Contains("all 2")&&Named<TextBlock>("PreviewLabel").Text.Contains("inspection only"),"Isolation hid context or implied Apply accepts only the displayed subset.");SameAccepted(before);
        Assert(candidate.Id==editor.Snapshot.Preview?.Id&&CameraPose()==camera,"Piece inspection changed candidate/Core/camera.");Capture("separate-one-piece-minimum",1040,680);
        Named<CheckBox>("SeparateShowOriginal").IsChecked=true;Assert(SeparateDrawn().Values.ToHashSet().SetEquals([sourceId,contextId])&&ReferenceEquals(candidate,editor.Snapshot.Preview)&&CameraPose()==camera,"Original comparison changed candidate/camera or kept split display.");
        Named<CheckBox>("SeparateShowOriginal").IsChecked=false;Assert(SeparateDrawn().Values.ToHashSet().SetEquals([pieces[1].ObjectId,contextId]),"Returning to result lost selected piece inspection.");
        Click(Named<Button>("CancelSeparateButton"));SameAccepted(before);Assert(editor.Snapshot.Preview==null&&SeparateDrawn().Values.ToHashSet().SetEquals([sourceId,contextId])&&CameraPose()==camera,"Cancel left partitioned display or changed accepted work/camera.");
        Pass("Minimum-size Separate review keeps cavities attached, colors actual pieces, isolates one without hiding unrelated context and compares/cancels at the same camera");

        Click(Named<Button>("SeparateButton"));Click(Named<Button>("PreviewSeparateButton"));Click(Named<Button>("CancelSeparateButton"));await Idle();SameAccepted(before);
        Click(Named<Button>("SeparateButton"));await PreviewSeparate();candidate=editor.Snapshot.Preview!;pieces=candidate.Separate!.Pieces;list.SelectedIndex=2;Click(Named<Button>("ApplySeparateButton"));
        Assert(editor.Snapshot.SelectedObjectIds.ToHashSet().SetEquals(pieces.Select(p=>p.ObjectId))&&editor.Snapshot.SelectedObjectId==pieces[0].ObjectId&&editor.Snapshot.Scene.Objects.Length==3&&CameraPose()==camera,"Apply did not keep/select ALL pieces and unchanged context/camera.");
        Assert(editor.Snapshot.Scene.Objects.Single(o=>o.Id==contextId)==before.Scene.Objects.Single(o=>o.Id==contextId),"Apply altered context.");
        Ok(await editor.ExportSelectionAsync(Path.Combine(output,"ui-separated-hollow-boxes.3mf"),pieces.Select(p=>p.ObjectId).ToImmutableArray()));Ok(await editor.ExportSelectionAsync(Path.Combine(output,"ui-separated-hollow-boxes.stl"),pieces.Select(p=>p.ObjectId).ToImmutableArray()));
        var accepted=editor.Snapshot.Scene;var second=accepted.Objects.Single(o=>o.Id==pieces[1].ObjectId);var first=accepted.Objects.Single(o=>o.Id==pieces[0].ObjectId);
        ChooseTreeRoot(second.Id);Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text=NumberForChecks(second.Bounds.Center.X+5);Click("Preview move");await Idle();Click(Named<Button>("ApplyTransformButton"));
        Assert(editor.Snapshot.Scene.Objects.Single(o=>o.Id==second.Id).Bounds.Center.X>second.Bounds.Center.X+4.999&&ReferenceEquals(editor.Snapshot.Scene.Objects.Single(o=>o.Id==first.Id),first),"Separated piece could not be edited independently through normal controls.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted),"Individual edit Undo lost separated scene.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Separate Undo did not restore exact source.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted),"Separate Redo changed IDs/geometry.");
        await OpenSeparate("one-hollow-box.3mf");before=editor.Snapshot;Click(Named<Button>("SeparateButton"));Click(Named<Button>("PreviewSeparateButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview==null&&!Named<Button>("ApplySeparateButton").IsEnabled,"One hollow material piece was incorrectly separated into cavity objects.");Click(Named<Button>("CancelSeparateButton"));
        Pass("Ready/in-flight Cancel is safe; Apply keeps all pieces, normal tree/Move edits one independently, Undo/Redo is exact, and a lone hollow piece cannot produce a no-op separation");

        await OpenSeparate("two-hollow-boxes.3mf");sourceId=editor.Snapshot.SelectedObjectId!.Value;Ok(editor.Execute(new AddCube()));var siblingId=editor.Snapshot.SelectedObjectId!.Value;Ok(await editor.PreviewArrangementAsync(new GroupRequest([sourceId,siblingId])));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();Assert(!Named<Button>("SeparateButton").IsEnabled,"Whole group enabled single-member Separate.");
        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,sourceId));Refresh();before=editor.Snapshot;var sibling=before.Scene.Objects.Single(o=>o.Id==siblingId);Click(Named<Button>("SeparateButton"));await PreviewSeparate();candidate=editor.Snapshot.Preview!;pieces=candidate.Separate!.Pieces;list.SelectedIndex=2;
        Assert(candidate.EditingGroupId==group.Id&&SeparateDrawn().Values.Contains(siblingId)&&ReferenceEquals(candidate.Scene.Objects.Single(o=>o.Id==siblingId),sibling),"Scoped preview/isolation lost sibling context or references.");Click(Named<Button>("ApplySeparateButton"));var after=editor.Snapshot;
        Assert(after.EditingGroupId==group.Id&&after.Scene.Groups.Single().Id==group.Id&&after.Scene.Groups.Single().ObjectIds.ToHashSet().SetEquals(pieces.Select(p=>p.ObjectId).Append(siblingId))&&after.SelectedObjectIds.ToHashSet().SetEquals(pieces.Select(p=>p.ObjectId)),"Scoped Apply lost membership or full result selection.");
        ChooseTreeRoot(pieces[1].ObjectId);Named<TextBox>("ObjectName").Text="Separated member";Click(Named<Button>("RenameButton"));Assert(editor.Snapshot.Scene.Objects.Single(o=>o.Id==pieces[1].ObjectId).Name=="Separated member","New group member is not independently editable.");Click(Named<Button>("UndoButton"));Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Scoped Separate Undo lost exact source/scope.");Ok(editor.ExitGroup());Refresh();
        Pass("Separate accepts only an independent leaf or explicit member and preserves group/sibling identity while new pieces become individually editable members");

        await OpenSeparate("nested-island.3mf");before=editor.Snapshot;Click(Named<Button>("SeparateButton"));await PreviewSeparate();candidate=editor.Snapshot.Preview!;pieces=candidate.Separate!.Pieces;var island=pieces.OrderBy(p=>p.VolumeCubicMillimeters).First();list.SelectedIndex=pieces.IndexOf(island)+1;
        Assert(SeparateDrawn().Values.SequenceEqual([island.ObjectId])&&Named<TextBlock>("SeparateSummary").Text.Contains("all 2"),"Nested material-island inspection did not show its actual piece or confused acceptance scope.");Capture("separate-nested-island-minimum",1040,680);
        foreach(var name in new[]{"ObjectName","GroupName"}){var field=Named<TextBox>(name);Assert(field.IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(field),"Separate changed text Undo/IME policy.");}
        Invoke("SmoothClick",window,new RoutedEventArgs());Invoke("RepairClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());SameAccepted(before);Assert(Named<FrameworkElement>("SeparateActions").Visibility==Visibility.Visible&&Named<FrameworkElement>("SmoothActions").Visibility==Visibility.Collapsed,"Competing command replaced Separate.");
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-separate-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewSeparateButton","ApplySeparateButton","CancelSeparateButton"}){var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted Separate footer.");}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelSeparateButton").RaiseEvent(escape);Assert(escape.Handled,"Separate Escape not handled.");
        }
        SameAccepted(before);Assert(editor.Snapshot.Preview==null,"Escape retained a separation preview.");
        Pass("Actual nested-island inspection stays display-only; standard footer Enter/Escape, modal guards and existing text Undo/IME remain intact");
    }
}
