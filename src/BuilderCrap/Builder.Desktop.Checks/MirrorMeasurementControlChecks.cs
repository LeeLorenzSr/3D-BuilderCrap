using Builder.Core;
using Builder.Desktop;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static PointMeasurement? CurrentMeasurement()=>(PointMeasurement?)typeof(MainWindow).GetField("measurementResult",PrivateInstance)!.GetValue(window);
    private static Vec3? FirstMeasurement()=>(Vec3?)typeof(MainWindow).GetField("measurementFirst",PrivateInstance)!.GetValue(window);
    private static Point ProjectMeasuredWorld(Vec3 point)
    {
        var viewport=Named<Viewport3D>("Viewport");
        return TransformGizmo.Layout((ProjectionCamera)viewport.Camera,new Size(viewport.ActualWidth,viewport.ActualHeight),point,HandleMode.Move).Project(point)
            ?? throw new InvalidOperationException("Test world point is outside viewport.");
    }
    private static async Task MeasureWorld(Vec3 point)=>await (Task)Invoke("PickMeasurementAt",ProjectMeasuredWorld(point))!;
    private static void InspectionFooterInside(params string[] names)
    {
        Layout(1040,680);
        foreach(var name in names)
        {
            var element=Named<FrameworkElement>(name);var bounds=element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
            Assert(element.Visibility==Visibility.Visible&&bounds.Width>0&&bounds.Height>0&&bounds.Top>=0&&bounds.Bottom<=680,"Inspection footer outside minimum client: "+name);
        }
    }
    private static async Task RunMirrorMeasurementControls()
    {
        await Open("cube-20mm.stl");
        Ok(await editor.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Wedge,new(30,20,10))));
        Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();Layout(1280,780);
        var before=editor.Snapshot;var target=before.Scene.Objects.Single(o=>o.Id==before.SelectedObjectId);
        Click(Named<Button>("MirrorButton"));
        Assert(Named<ComboBox>("MirrorAxisChoice").SelectedIndex==0&&Named<CheckBox>("MirrorCustomPlane").IsChecked==false,"Mirror must default to world X through selected bounds center.");
        Assert(!Named<Button>("ApplyMirrorButton").IsEnabled,"Mirror Apply enabled without preview.");
        InspectionFooterInside("PreviewMirrorButton","ApplyMirrorButton","CancelMirrorButton");Capture("mirror-default-minimum",1040,680);
        Click(Named<Button>("PreviewMirrorButton"));await Idle();SameAccepted(before);
        var originalCandidate=editor.Snapshot.Preview!;var request=(MirrorTransform)originalCandidate.Request;
        Assert(request.Axis==MirrorAxis.X,"Mirror preview axis mismatch.");Near(request.PlanePositionMillimeters!.Value,target.Bounds.Center.X);
        var mirrored=originalCandidate.Scene.Objects.Single(o=>o.Id==target.Id);
        var source=target.Transform.Apply(target.Mesh.Vertices[0]);var changed=mirrored.Transform.Apply(mirrored.Mesh.Vertices[0]);
        Near(changed.X,2*target.Bounds.Center.X-source.X);Near(changed.Y,source.Y);Near(changed.Z,source.Z);
        Named<ComboBox>("MirrorAxisChoice").SelectedIndex=2;Named<CheckBox>("MirrorCustomPlane").IsChecked=true;Named<TextBox>("MirrorPlane").Text="not a number";
        Assert(!Named<Button>("ApplyMirrorButton").IsEnabled,"Changed mirror draft allowed stale Apply.");
        Invoke("ApplyMirrorClick",window,new RoutedEventArgs());SameAccepted(before);
        Click(Named<Button>("PreviewMirrorButton"));await Idle();Assert(editor.Snapshot.Preview?.Id==originalCandidate.Id,"Invalid mirror draft erased prior candidate.");
        Named<TextBox>("MirrorPlane").Text="-5";Click(Named<Button>("PreviewMirrorButton"));await Idle();SameAccepted(before);
        var reviewed=editor.Snapshot.Preview!;Assert(reviewed.Id!=originalCandidate.Id,"Mirror replacement must receive a fresh preview token.");
        Near(reviewed.Scene.Objects.Single(o=>o.Id==target.Id).Bounds.Min.Z,-20);
        Assert(Named<TextBox>("MirrorPlane").IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(Named<TextBox>("MirrorPlane")),"Mirror numeric field lost local text Undo/IME.");
        Click(Named<Button>("ApplyMirrorButton"));Assert(editor.Snapshot.SelectedObjectId==target.Id&&editor.Snapshot.Preview==null,"Mirror Apply lost identity or left a preview.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&editor.Snapshot.FilePath==before.FilePath,"Mirror Undo did not restore exact scene/selection/path.");
        Click(Named<Button>("RedoButton"));Near(editor.Snapshot.Scene.Objects.Single(o=>o.Id==target.Id).Bounds.Min.Z,-20);Click(Named<Button>("UndoButton"));
        Pass("Asymmetric mirror defaults to center/X, validates offset drafts, accepts only current preview and preserves identity through Undo/Redo");

        before=editor.Snapshot;Click(Named<Button>("MirrorButton"));Click(Named<Button>("PreviewMirrorButton"));Click(Named<Button>("CancelMirrorButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview==null&&Named<FrameworkElement>("MirrorActions").Visibility==Visibility.Collapsed,"In-flight mirror cancel left a candidate/tool open.");
        Click(Named<Button>("MirrorButton"));Click(Named<Button>("PreviewMirrorButton"));await Idle();
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-inspection-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewMirrorButton","ApplyMirrorButton","CancelMirrorButton"})
            {
                var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted a focused mirror footer button.");SameAccepted(before);
            }
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            Named<Button>("CancelMirrorButton").RaiseEvent(escape);Assert(escape.Handled,"Mirror Escape not handled.");
        }
        SameAccepted(before);Pass("Mirror fixed footer, standard button Enter, Escape and in-flight cancellation preserve accepted state");

        await Open("assembly-mm.3mf");var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(await editor.PreviewArrangementAsync(new GroupRequest(ids)));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();
        var group=editor.Snapshot.Scene.Groups.Single();before=editor.Snapshot;
        Click(Named<Button>("MirrorButton"));Click(Named<Button>("PreviewMirrorButton"));await Idle();
        Assert(editor.Snapshot.Preview!.ResultObjectIds.ToHashSet().SetEquals(ids),"Whole-group mirror lost targets.");
        Click(Named<Button>("CancelMirrorButton"));SameAccepted(before);
        Ok(editor.EnterGroup(group.Id,ids[0]));Refresh();before=editor.Snapshot;var sibling=before.Scene.Objects.Single(o=>o.Id==ids[1]);
        Click(Named<Button>("MirrorButton"));Click(Named<Button>("PreviewMirrorButton"));await Idle();
        Assert(editor.Snapshot.Preview!.ResultObjectIds.SequenceEqual(new[]{ids[0]})&&ReferenceEquals(editor.Snapshot.Preview.Scene.Objects.Single(o=>o.Id==ids[1]),sibling),"Member mirror touched sibling geometry.");
        Click(Named<Button>("ApplyMirrorButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single()==group,"Member mirror lost scope/group.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Member mirror Undo lost exact scope/scene.");
        Ok(editor.ExitGroup());Refresh();Pass("Mirror respects whole-group targets and independently edited member scope without changing siblings/group identity");

        await Open("cube-20mm.stl");Ok(editor.SelectMany([]));Refresh();Click("Front");before=editor.Snapshot;
        Assert(Named<Button>("MeasureButton").IsEnabled,"Measurement unnecessarily requires a selection.");
        Click(Named<Button>("MeasureButton"));InspectionFooterInside("ResetMeasurementButton","DoneMeasurementButton");
        await MeasureWorld(new(5,0,5));SameAccepted(before);var first=FirstMeasurement()!.Value;Near(first.X,5);Near(first.Y,0);Near(first.Z,5);
        await (Task)Invoke("PickMeasurementAt",new Point(0,0))!;Assert(FirstMeasurement()==first&&CurrentMeasurement()==null,"Miss erased a chosen endpoint.");
        await MeasureWorld(new(15,0,5));SameAccepted(before);
        var measured=CurrentMeasurement()??throw new InvalidOperationException("Second surface pick did not measure.");
        Near(measured.DistanceMillimeters,10);Near(measured.DeltaMillimeters.X,10);Near(measured.DeltaMillimeters.Y,0);Near(measured.DeltaMillimeters.Z,0);
        Assert(Named<TextBlock>("Status").Text.Contains("10 mm"),"A successful measurement retained stale miss/status feedback.");
        Assert(measured.CapturedRevision==before.Revision&&measured.EditingGroupId==before.EditingGroupId,"Distance came from wrong accepted capture.");
        Assert(Named<Canvas>("MeasurementOverlay").Children.Count==5&&!Named<Canvas>("MeasurementOverlay").IsHitTestVisible,"Measurement overlay is incomplete or intercepts input.");
        Assert(Named<TextBlock>("MeasurementFirst").Text.Contains("X 5")&&Named<TextBlock>("MeasurementSecond").Text.Contains("X 15"),"World endpoint coordinates are not visible.");
        foreach(var view in new[]{"Top","Right","Isometric","Front"}){Click(view);Assert(CurrentMeasurement()==measured,"Camera changed measured world points.");SameAccepted(before);}
        Capture("measurement-known-points-minimum",1040,680);
        Click(Named<Button>("ResetMeasurementButton"));Assert(FirstMeasurement()==null&&CurrentMeasurement()==null&&Named<Canvas>("MeasurementOverlay").Children.Count==0,"Reset did not clear measurement presentation.");SameAccepted(before);
        Click(Named<Button>("DoneMeasurementButton"));SameAccepted(before);
        Assert(Named<Canvas>("MeasurementOverlay").Visibility==Visibility.Collapsed,"Done left annotation overlay visible.");
        Pass("Accepted-surface picks produce Core distance/deltas/world endpoints, misses preserve points, and camera/reset/Done remain document-neutral");

        Click(Named<Button>("MeasureButton"));Click("Front");await MeasureWorld(new(5,0,5));before=editor.Snapshot;
        using(var pending=new CancellationTokenSource())
        {
            var field=typeof(MainWindow).GetField("pendingPick",PrivateInstance)!;field.SetValue(window,pending);
            try{Click("Top");Assert(pending.IsCancellationRequested,"Camera transition did not cancel pending measurement pick.");}
            finally{field.SetValue(window,null);}
        }
        Assert(FirstMeasurement()!=null&&CurrentMeasurement()==null,"Camera cancellation erased accepted measurement points.");SameAccepted(before);
        Invoke("MirrorClick",window,new RoutedEventArgs());Invoke("MoveHandleClick",window,new RoutedEventArgs());Invoke("AddCubeClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());
        Assert(Named<FrameworkElement>("MirrorActions").Visibility==Visibility.Collapsed&&Named<FrameworkElement>("PrimitiveActions").Visibility==Visibility.Collapsed&&Named<FrameworkElement>("TransformActions").Visibility==Visibility.Collapsed,"Conflicting tool replaced active measurement.");SameAccepted(before);
        Click(Named<Button>("ResetMeasurementButton"));var pendingPick=(Task)Invoke("PickMeasurementAt",ProjectMeasuredWorld(new(5,5,20)))!;
        Click(Named<Button>("DoneMeasurementButton"));await pendingPick;
        Assert(FirstMeasurement()==null&&CurrentMeasurement()==null,"A late result revived a closed measurement.");SameAccepted(before);
        Click(Named<Button>("MeasureButton"));Click("Front");await MeasureWorld(new(5,0,5));
        Ok(editor.SelectMany([editor.Snapshot.Scene.Objects[0].Id]));var changedRevision=editor.Snapshot;Refresh();
        Assert(FirstMeasurement()==null&&CurrentMeasurement()==null,"Revision transition retained stale measurement points.");SameAccepted(changedRevision);
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-measurement-escape"){Width=1,Height=1,WindowStyle=0}))
        {var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("DoneMeasurementButton").RaiseEvent(key);Assert(key.Handled,"Measure Escape was not handled.");}
        SameAccepted(changedRevision);Pass("Measurement cancels camera/tool-generation picks, clears revision-stale presentation and guards conflicting edits with Escape safety");

        await Open("assembly-mm.3mf");ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(await editor.PreviewArrangementAsync(new GroupRequest(ids)));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));group=editor.Snapshot.Scene.Groups.Single();
        Ok(editor.Execute(new AddCube()));var outside=editor.Snapshot.SelectedObjectId!.Value;Ok(editor.Execute(new MoveObject(outside,new(10,-10,10))));
        Ok(editor.EnterGroup(group.Id,ids[0]));Refresh();Click("Front");before=editor.Snapshot;Click(Named<Button>("MeasureButton"));
        await MeasureWorld(new(10,0,10));Assert(FirstMeasurement()==null,"Out-of-group occluder supplied a measurement point.");SameAccepted(before);
        await MeasureWorld(new(35,0,5));await MeasureWorld(new(45,0,5));Near(CurrentMeasurement()!.DistanceMillimeters,10);
        Assert(CurrentMeasurement()!.EditingGroupId==group.Id,"Scoped measurement lost capture scope.");SameAccepted(before);
        Click(Named<Button>("DoneMeasurementButton"));Ok(editor.ExitGroup());Refresh();
        Pass("Member measurement rejects outside-group occluders and measures eligible members without changing selection or scope");
    }
}
