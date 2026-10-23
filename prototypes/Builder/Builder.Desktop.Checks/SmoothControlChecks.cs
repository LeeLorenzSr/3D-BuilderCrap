using Builder.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static string SmoothInputs=>Path.Combine(root,".local","evidence","smooth-20261020","inputs");
    private static async Task OpenSmooth(string file)
    {
        Ok(await editor.OpenAsync(Path.Combine(SmoothInputs,file),decision:DirtyDecision.Discard));Refresh();Layout(1280,780);
        if(window.FindName("WorkspaceTabs") is TabControl){await Workspace(4);Click(Named<Button>("FitSceneButton"));await Workspace(2);}
        else Click("Fit scene");
    }
    private static async Task PreviewSmooth()
    {
        Click(Named<Button>("PreviewSmoothButton"));await Idle();
        Assert(editor.Snapshot.Preview is {Request:SmoothRequest,Smooth:not null},"Smooth preview failed: "+Named<TextBlock>("Status").Text);
    }
    private static void SmoothReportVisible()
    {
        var report=Named<TextBlock>("SmoothReport");var footer=Named<FrameworkElement>("SmoothActions");
        Assert(report.TranslatePoint(new(0,report.ActualHeight),surface).Y<=footer.TranslatePoint(new(0,0),surface).Y,"Minimum client hid the movement/cavity/volume report below the fixed footer.");
    }
    private static async Task RunSmoothControls()
    {
        await OpenSmooth("noisy-sphere.3mf");var before=editor.Snapshot;var target=before.SelectedObjectId!.Value;var original=before.Scene.Objects.Single();var originalDisplay=DrawnObject(target);
        Click(Named<Button>("SmoothButton"));Near(double.Parse(Named<TextBox>("SmoothStrength").Text,CultureInfo.CurrentCulture),50);Near(double.Parse(Named<TextBox>("SmoothDeviation").Text,CultureInfo.CurrentCulture),.2);
        Assert(Named<CheckBox>("SmoothPreserveSharp").IsChecked==true&&!Named<Button>("ApplySmoothButton").IsEnabled&&!Named<CheckBox>("SmoothShowOriginal").IsEnabled,"Smooth default/review guard incorrect.");
        InspectionFooterInside("PreviewSmoothButton","ApplySmoothButton","CancelSmoothButton");Capture("smooth-settings-minimum",1040,680);var camera=CameraPose();
        await PreviewSmooth();SameAccepted(before);Assert(CameraPose()==camera,"Smoothing preview moved camera.");
        var candidate=editor.Snapshot.Preview!;var comparison=candidate.Smooth!;var result=candidate.Scene.Objects.Single();
        Assert(comparison.TriangleCount==original.Mesh.Triangles.Length&&result.Mesh.Triangles.Length==comparison.TriangleCount,"Smoothing changed triangle count.");
        Assert(comparison.MaximumVertexDisplacementMillimeters>0&&comparison.CertifiedDeviationUpperBoundMillimeters<=.2&&comparison.MaximumDeviationMillimeters==.2,"Smoothing did not move or exceeded/changed the user limit.");
        Assert(result.Id==original.Id&&result.Name==original.Name&&candidate.ResultObjectIds.SequenceEqual([target])&&!ReferenceEquals(result.Mesh,original.Mesh),"Smoothing lost identity or did not produce actual geometry.");
        Assert(Named<TextBlock>("SmoothReport").Text.Contains("unchanged")&&Named<TextBlock>("SmoothReport").Text.Contains("Volume change"),"Smoothing omitted topology/volume review.");Capture("smooth-result-minimum",1040,680);SmoothReportVisible();
        observations.Add(new{comparison.TriangleCount,comparison.VertexCount,comparison.StrengthPercent,comparison.MaximumDeviationMillimeters,comparison.MaximumVertexDisplacementMillimeters,comparison.CertifiedDeviationUpperBoundMillimeters,comparison.FixedSharpVertexCount,comparison.ClippedVertexCount,comparison.BoundaryCount,comparison.CavityCount,comparison.OriginalVolumeCubicMillimeters,comparison.ResultVolumeCubicMillimeters});
        Pass("Smooth defaults and minimum-window actions review real moved geometry with unchanged triangle count, explicit physical limit and truthful movement/volume metadata");

        var resultDisplay=DrawnObject(target);Assert(!ReferenceEquals(resultDisplay,originalDisplay)&&!resultDisplay.Positions.SequenceEqual(originalDisplay.Positions),"Viewport did not show the actual changed surface.");
        Named<CheckBox>("SmoothShowOriginal").IsChecked=true;Assert(ReferenceEquals(DrawnObject(target),originalDisplay)&&Named<TextBlock>("PreviewLabel").Text.Contains("Original"),"Original comparison did not use accepted geometry.");SameAccepted(before);
        Assert(editor.Snapshot.Preview?.Id==candidate.Id&&CameraPose()==camera&&Named<Button>("ApplySmoothButton").IsEnabled,"Comparison mutated camera/candidate/draft.");Capture("smooth-original-minimum",1040,680);
        Named<CheckBox>("SmoothShowOriginal").IsChecked=false;Assert(ReferenceEquals(DrawnObject(target),resultDisplay),"Result comparison did not restore actual candidate.");
        Named<CheckBox>("SmoothShowOriginal").IsChecked=true;Click(Named<Button>("ApplySmoothButton"));Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,result.Mesh)&&CameraPose()==camera,"Apply accepted original display or moved camera.");
        Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-smooth-noisy-sphere.stl")));Ok(await editor.ExportSelectionAsync(Path.Combine(output,"ui-smooth-noisy-sphere.3mf"),[target]));
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Smoothing Undo lost exact accepted scene/selection.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,result.Mesh),"Smoothing Redo changed result.");Click(Named<Button>("UndoButton"));
        Pass("Actual original/result surface comparison preserves camera and Core state; Apply, saved geometry and exact Undo/Redo accept the reviewed result");

        before=editor.Snapshot;Click(Named<Button>("SmoothButton"));Named<TextBox>("SmoothDeviation").Text=NumberForChecks(.01);await PreviewSmooth();candidate=editor.Snapshot.Preview!;camera=CameraPose();
        Assert(candidate.Smooth!.ClippedVertexCount>0&&Named<FrameworkElement>("SmoothLimitNotice").Visibility==Visibility.Visible&&Named<Button>("ApplySmoothButton").IsEnabled,"Clamped movement must remain applicable and be explicitly disclosed.");Capture("smooth-limited-minimum",1040,680);SmoothReportVisible();
        Named<TextBox>("SmoothStrength").Text="NaN";Assert(!Named<Button>("ApplySmoothButton").IsEnabled,"Dirty strength allowed stale Apply.");Invoke("ApplySmoothClick",window,new RoutedEventArgs());Click(Named<Button>("PreviewSmoothButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview?.Id==candidate.Id&&CameraPose()==camera,"Invalid draft erased prior candidate or changed camera.");
        Named<TextBox>("SmoothStrength").Text="75";Named<TextBox>("SmoothDeviation").Text="1e-30";Click(Named<Button>("PreviewSmoothButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview?.Id==candidate.Id&&!Named<Button>("ApplySmoothButton").IsEnabled&&Named<TextBox>("SmoothDeviation").Text=="1e-30"&&CameraPose()==camera,"Failed sub-floor request altered prior candidate/limit/camera or enabled stale Apply.");
        Named<TextBox>("SmoothDeviation").Text=NumberForChecks(.2);await PreviewSmooth();Assert(editor.Snapshot.Preview!.Id!=candidate.Id&&CameraPose()==camera,"Replayed settings did not create a fresh token at the same camera.");
        Click(Named<Button>("CancelSmoothButton"));SameAccepted(before);Assert(CameraPose()==camera&&editor.Snapshot.Preview==null,"Ready Cancel changed accepted state/camera.");
        Click(Named<Button>("SmoothButton"));Click(Named<Button>("PreviewSmoothButton"));Named<TextBox>("SmoothStrength").Text="60";await Idle();Assert(!Named<Button>("ApplySmoothButton").IsEnabled,"In-flight field edit accepted stale smoothing.");Click(Named<Button>("CancelSmoothButton"));SameAccepted(before);
        Click(Named<Button>("SmoothButton"));Click(Named<Button>("PreviewSmoothButton"));Click(Named<Button>("CancelSmoothButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview==null&&Named<FrameworkElement>("SmoothActions").Visibility==Visibility.Collapsed,"In-flight Cancel left a candidate/tool.");
        Pass("Movement limiting is disclosed without changing the entered limit; invalid/replacement/in-flight drafts and cancellation retain accepted geometry and camera");

        await Open("cube-20mm.stl");before=editor.Snapshot;Click(Named<Button>("SmoothButton"));Click(Named<Button>("PreviewSmoothButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview==null&&!Named<Button>("ApplySmoothButton").IsEnabled,"All-fixed sharp cube published a no-op candidate.");
        Named<CheckBox>("SmoothPreserveSharp").IsChecked=false;await PreviewSmooth();Assert(!editor.Snapshot.Preview!.Smooth!.PreserveSharpEdges&&editor.Snapshot.Preview.Smooth.FixedSharpVertexCount==0,"Sharp option did not reach Core.");Click(Named<Button>("CancelSmoothButton"));SameAccepted(before);
        await OpenSmooth("noisy-hollow-sphere.3mf");before=editor.Snapshot;target=before.SelectedObjectId!.Value;Click(Named<Button>("SmoothButton"));await PreviewSmooth();candidate=editor.Snapshot.Preview!;
        Assert(candidate.Smooth!.CavityCount==1&&candidate.Smooth.BoundaryCount==2&&candidate.Smooth.TriangleCount==before.Scene.Objects.Single().Mesh.Triangles.Length&&Named<TextBlock>("SmoothReport").Text.Contains("Cavities: 1"),"Smoothing lost cavity/topology metadata.");
        Click(Named<Button>("ApplySmoothButton"));Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-smooth-hollow-sphere.stl")));Ok(await editor.ExportSelectionAsync(Path.Combine(output,"ui-smooth-hollow-sphere.3mf"),[target]));Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Hollow smoothing Undo lost accepted cavity.");
        Pass("Original sharp-edge locks reject no useful movement, disabling locks uses Core smoothing, and hollow surfaces preserve cavities and triangle counts through Apply and Undo");

        before=editor.Snapshot;Click(Named<Button>("SmoothButton"));await PreviewSmooth();
        foreach(var name in new[]{"SmoothStrength","SmoothDeviation"}){var field=Named<TextBox>(name);Assert(field.IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(field),"Smooth numeric field lost local Undo/IME.");}
        CheckLocalTextUndo(Named<TextBox>("SmoothStrength"),"50","60");SameAccepted(before);
        Invoke("SimplifyClick",window,new RoutedEventArgs());Invoke("HollowClick",window,new RoutedEventArgs());Invoke("RepairClick",window,new RoutedEventArgs());Invoke("MoveHandleClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());SameAccepted(before);
        Assert(Named<FrameworkElement>("SmoothActions").Visibility==Visibility.Visible&&Named<FrameworkElement>("SimplifyActions").Visibility==Visibility.Collapsed,"Conflicting command replaced Smooth.");
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-smooth-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewSmoothButton","ApplySmoothButton","CancelSmoothButton"})
            {var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted Smooth footer.");}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelSmoothButton").RaiseEvent(escape);Assert(escape.Handled,"Smooth Escape not handled.");
        }
        SameAccepted(before);Assert(Named<CheckBox>("SmoothShowOriginal").IsChecked==false,"Closed smoothing kept original comparison mode.");
        Pass("Smooth keeps text Undo/IME, normal focused-button Enter, Escape and modal tool/history guards");

        await OpenSmooth("noisy-sphere.3mf");var first=editor.Snapshot.SelectedObjectId!.Value;Ok(editor.Execute(new AddCube()));var second=editor.Snapshot.SelectedObjectId!.Value;
        Ok(await editor.PreviewArrangementAsync(new GroupRequest([first,second])));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();Assert(!Named<Button>("SmoothButton").IsEnabled,"Whole group enabled single-member smoothing.");
        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,first));Refresh();before=editor.Snapshot;var sibling=before.Scene.Objects.Single(o=>o.Id==second);Click(Named<Button>("SmoothButton"));await PreviewSmooth();candidate=editor.Snapshot.Preview!;
        Assert(candidate.EditingGroupId==group.Id&&candidate.ResultObjectIds.SequenceEqual([first])&&ReferenceEquals(candidate.Scene.Objects.Single(o=>o.Id==second),sibling),"Scoped smoothing lost target/sibling isolation.");Click(Named<Button>("ApplySmoothButton"));
        Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single()==group&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Smoothing lost member scope/group/selection.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Scoped Smooth Undo lost exact scene/scope.");Ok(editor.ExitGroup());Refresh();
        Pass("Smooth requires one independent leaf or scoped member and preserves full selection, siblings, group metadata and exact scoped Undo");
    }
}
