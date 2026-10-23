using Builder.Core;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static string SimplifyInputs=>Path.Combine(root,".local","evidence","simplify-20261020","inputs");
    private static async Task OpenSimplify(string file)
    {Ok(await editor.OpenAsync(Path.Combine(SimplifyInputs,file),decision:DirtyDecision.Discard));Refresh();Layout(1280,780);Click("Fit scene");}
    private static (Point3D Position,Vector3D Look,Vector3D Up,double Near,double Far,double Width) CameraPose()
    {
        var camera=(ProjectionCamera)Named<Viewport3D>("Viewport").Camera;
        return(camera.Position,camera.LookDirection,camera.UpDirection,camera.NearPlaneDistance,camera.FarPlaneDistance,camera is OrthographicCamera orthographic?orthographic.Width:0);
    }
    private static async Task PreviewSimplify()
    {
        Click(Named<Button>("PreviewSimplifyButton"));await Idle();
        Assert(editor.Snapshot.Preview is {Request:SimplifyRequest,Simplify:not null},"Simplify preview failed: "+Named<TextBlock>("Status").Text);
    }
    private static async Task RunSimplifyControls()
    {
        await OpenSimplify("dense-sphere.3mf");var before=editor.Snapshot;var target=before.SelectedObjectId!.Value;var original=before.Scene.Objects.Single();
        Click(Named<Button>("SimplifyButton"));Near(double.Parse(Named<TextBox>("SimplifyReduction").Text,CultureInfo.CurrentCulture),50);Near(double.Parse(Named<TextBox>("SimplifyDeviation").Text,CultureInfo.CurrentCulture),.1);
        Assert(!Named<Button>("ApplySimplifyButton").IsEnabled&&!Named<CheckBox>("SimplifyShowOriginal").IsEnabled,"Simplify acceptance/comparison enabled before preview.");
        InspectionFooterInside("PreviewSimplifyButton","ApplySimplifyButton","CancelSimplifyButton");Capture("simplify-settings-minimum",1040,680);var camera=CameraPose();
        await PreviewSimplify();SameAccepted(before);Assert(CameraPose()==camera,"Preview changed camera pose.");
        var candidate=editor.Snapshot.Preview!;var comparison=candidate.Simplify!;var result=candidate.Scene.Objects.Single();
        Assert(comparison.OriginalTriangleCount==original.Mesh.Triangles.Length&&comparison.ResultTriangleCount==result.Mesh.Triangles.Length&&comparison.ResultTriangleCount<comparison.OriginalTriangleCount,"UI candidate/count reduction mismatch.");
        Assert(comparison.CertifiedDeviationUpperBoundMillimeters<=comparison.MaximumDeviationMillimeters&&comparison.MaximumDeviationMillimeters==.1,"Result exceeded or changed the user deviation limit.");
        Assert(!comparison.TargetReached&&Named<FrameworkElement>("SimplifyTargetNotice").Visibility==Visibility.Visible&&Named<Button>("ApplySimplifyButton").IsEnabled,"Safe partial reduction should be conspicuous and applicable.");
        Assert(Named<TextBlock>("SimplifyReport").Text.Contains(comparison.OriginalTriangleCount.ToString("N0",CultureInfo.CurrentCulture))&&Named<TextBlock>("SimplifyReport").Text.Contains(comparison.ResultTriangleCount.ToString("N0",CultureInfo.CurrentCulture))&&Named<TextBlock>("SimplifyReport").Text.Contains("Volume change"),"Missing truthful before/result/volume review.");
        Assert(result.Id==original.Id&&result.Name==original.Name&&candidate.ResultObjectIds.SequenceEqual([target]),"Simplify lost target identity/name.");Capture("simplify-partial-result-minimum",1040,680);
        observations.Add(new{originalTriangles=comparison.OriginalTriangleCount,resultTriangles=comparison.ResultTriangleCount,requestedReductionPercent=comparison.RequestedReductionPercent,achievedReductionPercent=comparison.AchievedReductionPercent,comparison.TargetReached,comparison.MaximumDeviationMillimeters,comparison.CertifiedDeviationUpperBoundMillimeters,comparison.OriginalVolumeCubicMillimeters,comparison.ResultVolumeCubicMillimeters});
        Pass("Simplify keeps the explicit limit, truthfully reviews counts/deviation/volume and permits clearly labeled safe partial reduction at the minimum client");

        var resultGeometry=DrawnObject(target);Assert(resultGeometry.TriangleIndices.Count==result.Mesh.Triangles.Length*3,"Viewport did not show actual simplified result.");
        Named<CheckBox>("SimplifyShowOriginal").IsChecked=true;Assert(DrawnObject(target).TriangleIndices.Count==original.Mesh.Triangles.Length*3&&Named<TextBlock>("PreviewLabel").Text.Contains("Original"),"Original comparison did not show accepted geometry.");
        Assert(editor.Snapshot.Preview?.Id==candidate.Id&&CameraPose()==camera&&Named<Button>("ApplySimplifyButton").IsEnabled,"Comparison mutated candidate, camera or valid draft.");SameAccepted(before);Capture("simplify-original-minimum",1040,680);
        Named<CheckBox>("SimplifyShowOriginal").IsChecked=false;Assert(ReferenceEquals(DrawnObject(target),resultGeometry)&&CameraPose()==camera,"Result comparison was not restored at the same camera.");
        Named<CheckBox>("SimplifyShowOriginal").IsChecked=true;Click(Named<Button>("ApplySimplifyButton"));
        Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,result.Mesh)&&DrawnObject(target).TriangleIndices.Count==result.Mesh.Triangles.Length*3&&CameraPose()==camera,"Apply accepted original display instead of candidate or changed camera.");
        Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-simplify-partial-sphere.stl")));Ok(await editor.ExportSelectionAsync(Path.Combine(output,"ui-simplify-partial-sphere.3mf"),[target]));
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Simplify Undo lost exact scene/selection.");
        Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,result.Mesh),"Simplify Redo changed accepted result.");Click(Named<Button>("UndoButton"));
        Pass("Original/result comparison uses actual meshes without camera/state changes; Apply accepts the reviewed result even while original is displayed and Undo/Redo restores identity");

        await OpenSimplify("hollow-box.3mf");before=editor.Snapshot;target=before.SelectedObjectId!.Value;Click(Named<Button>("SimplifyButton"));await PreviewSimplify();candidate=editor.Snapshot.Preview!;comparison=candidate.Simplify!;camera=CameraPose();
        Assert(comparison.TargetReached&&comparison.CavityCount==1&&comparison.BoundaryCount==2&&comparison.ResultTriangleCount<comparison.OriginalTriangleCount,"Closed cavity or target reduction metadata lost.");
        Assert(Named<FrameworkElement>("SimplifyTargetNotice").Visibility==Visibility.Collapsed&&Named<TextBlock>("SimplifyReport").Text.Contains("Cavities: 1"),"Reached target/cavity review is wrong.");
        Named<TextBox>("SimplifyReduction").Text="NaN";Assert(!Named<Button>("ApplySimplifyButton").IsEnabled,"Invalid reduction draft allowed stale Apply.");Invoke("ApplySimplifyClick",window,new RoutedEventArgs());SameAccepted(before);
        Click(Named<Button>("PreviewSimplifyButton"));await Idle();Assert(editor.Snapshot.Preview?.Id==candidate.Id&&CameraPose()==camera,"Invalid draft erased prior candidate or moved camera.");
        Named<TextBox>("SimplifyReduction").Text="70";Named<TextBox>("SimplifyDeviation").Text="1e-30";Click(Named<Button>("PreviewSimplifyButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview?.Id==candidate.Id&&CameraPose()==camera&&!Named<Button>("ApplySimplifyButton").IsEnabled,"Below-floor failure erased candidate, altered limit, moved camera or allowed stale Apply.");
        Assert(Named<TextBox>("SimplifyDeviation").Text=="1e-30","Failed verification silently changed requested limit.");Named<TextBox>("SimplifyDeviation").Text=NumberForChecks(.1);await PreviewSimplify();
        Assert(editor.Snapshot.Preview!.Id!=candidate.Id&&editor.Snapshot.Preview.ResultObjectIds.SequenceEqual([target])&&CameraPose()==camera,"Replacement lost target, token freshness or camera.");
        var reviewed=editor.Snapshot.Preview!;Click(Named<Button>("ApplySimplifyButton"));Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,reviewed.Scene.Objects.Single().Mesh),"Hollow simplification acceptance changed result.");
        Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-simplify-hollow-box.stl")));Ok(await editor.ExportSelectionAsync(Path.Combine(output,"ui-simplify-hollow-box.3mf"),[target]));Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Hollow simplification Undo lost exact cavity geometry.");
        Pass("Cavity-preserving simplification reports boundaries/reached targets; edited and below-floor limits retain old candidates without stale acceptance, auto-adjustment or camera motion");

        before=editor.Snapshot;Click(Named<Button>("SimplifyButton"));camera=CameraPose();Click(Named<Button>("PreviewSimplifyButton"));Named<TextBox>("SimplifyReduction").Text="60";await Idle();
        Assert(editor.Snapshot.Preview!=null&&!Named<Button>("ApplySimplifyButton").IsEnabled,"In-flight draft change allowed stale simplification.");Click(Named<Button>("CancelSimplifyButton"));SameAccepted(before);Assert(CameraPose()==camera&&editor.Snapshot.Preview==null,"Ready cancel changed camera or left candidate.");
        Click(Named<Button>("SimplifyButton"));Click(Named<Button>("PreviewSimplifyButton"));Click(Named<Button>("CancelSimplifyButton"));await Idle();SameAccepted(before);
        Assert(Named<FrameworkElement>("SimplifyActions").Visibility==Visibility.Collapsed&&CameraPose()==camera,"In-flight cancel left tool/camera changes.");
        await Open("cube-20mm.stl");Ok(await editor.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Tetrahedron,new(20,20,20))));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();before=editor.Snapshot;
        Click(Named<Button>("SimplifyButton"));Click(Named<Button>("PreviewSimplifyButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview==null&&!Named<Button>("ApplySimplifyButton").IsEnabled,"No-op reduction published a candidate.");Click(Named<Button>("CancelSimplifyButton"));
        Pass("In-flight edits and ready/in-flight cancellation preserve accepted work and camera; no-count-reduction failure cannot be accepted");

        await OpenSimplify("hollow-box.3mf");before=editor.Snapshot;Click(Named<Button>("SimplifyButton"));await PreviewSimplify();
        foreach(var name in new[]{"SimplifyReduction","SimplifyDeviation"}){var field=Named<TextBox>(name);Assert(field.IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(field),"Simplify field lost local Undo/IME.");}
        CheckLocalTextUndo(Named<TextBox>("SimplifyReduction"),"50","60");SameAccepted(before);
        Invoke("HollowClick",window,new RoutedEventArgs());Invoke("RepairClick",window,new RoutedEventArgs());Invoke("MoveHandleClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());SameAccepted(before);
        Assert(Named<FrameworkElement>("SimplifyActions").Visibility==Visibility.Visible&&Named<FrameworkElement>("HollowActions").Visibility==Visibility.Collapsed,"Conflicting tool replaced simplification.");
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-simplify-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewSimplifyButton","ApplySimplifyButton","CancelSimplifyButton"})
            {var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted Simplify footer.");}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelSimplifyButton").RaiseEvent(escape);Assert(escape.Handled,"Simplify Escape not handled.");
        }
        SameAccepted(before);Assert(Named<CheckBox>("SimplifyShowOriginal").IsChecked==false,"Closed simplification retained original comparison state.");
        Pass("Simplify preserves local text Undo/IME, normal focused-button Enter, Escape and modal tool/history guards");

        await OpenSimplify("hollow-box.3mf");var first=editor.Snapshot.SelectedObjectId!.Value;Ok(editor.Execute(new AddCube()));var second=editor.Snapshot.SelectedObjectId!.Value;
        Ok(await editor.PreviewArrangementAsync(new GroupRequest([first,second])));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();Assert(!Named<Button>("SimplifyButton").IsEnabled,"Whole group enabled single-member simplification.");
        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,first));Refresh();before=editor.Snapshot;var sibling=before.Scene.Objects.Single(o=>o.Id==second);
        Click(Named<Button>("SimplifyButton"));await PreviewSimplify();candidate=editor.Snapshot.Preview!;
        Assert(candidate.EditingGroupId==group.Id&&candidate.ResultObjectIds.SequenceEqual([first])&&ReferenceEquals(candidate.Scene.Objects.Single(o=>o.Id==second),sibling),"Scoped simplification lost isolation/targets.");
        Click(Named<Button>("ApplySimplifyButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single()==group&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Simplify lost member group/selection.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Scoped Simplify Undo lost exact scene/scope.");Ok(editor.ExitGroup());Refresh();
        Pass("Simplify requires one independent object or scoped member and preserves sibling references, group identity, selection and scoped Undo");
    }
    private static string NumberForChecks(double value)=>value.ToString("0.######",CultureInfo.CurrentCulture);
}
