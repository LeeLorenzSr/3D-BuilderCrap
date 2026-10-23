using Builder.Core;
using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;

internal static partial class Program
{
    private static string ContourInputs=>Path.Combine(root,".local/evidence/image-contour-20261021/inputs");
    private static async Task BeginContour(string file="ring-island.png")
    {
        await Workspace(0);Reachable("ImageReliefButton");Click(Named<Button>("ImageReliefButton"));
        Assert(Named<ComboBox>("ImageConversionMode").SelectedIndex==0,"Image conversion no longer defaults to existing Relief.");
        Named<ComboBox>("ImageConversionMode").SelectedIndex=1;ReliefFile(Path.Combine(ContourInputs,file));
    }
    private static async Task PreviewContour()
    {Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(editor.Snapshot.Preview is {Request:ImageContourRequest},"Contour failed: "+Named<TextBlock>("Status").Text);}
    private static async Task RunImageContourControls()
    {
        await Open("cube-20mm.stl");var before=editor.Snapshot;await BeginContour();
        Assert(Named<FrameworkElement>("ImageContourFields").Visibility==Visibility.Visible&&Named<FrameworkElement>("ImageReliefFields").Visibility==Visibility.Collapsed,"Contour shows dormant relief fields.");
        Assert(Named<TextBox>("ImageContourThickness").Text=="3"&&Named<Slider>("ImageContourThreshold").Value==50&&Named<Slider>("ImageContourThreshold").SmallChange==1&&Named<Slider>("ImageContourThreshold").IsSnapToTickEnabled,"Contour defaults/one-percent step incorrect.");
        var slider=Named<Slider>("ImageContourThreshold");Slider.IncreaseSmall.Execute(null,slider);Assert(slider.Value==51&&Named<TextBlock>("ImageContourThresholdValue").Text=="51%","Threshold keyboard command did not increase one percent or update readout.");slider.Value=50;
        InspectionFooterInside("PreviewImageReliefButton","AddImageReliefButton","CancelImageReliefButton");Capture("contour-settings-minimum",1040,680);
        await PreviewContour();SameAccepted(before);var candidate=editor.Snapshot.Preview!;var id=candidate.ResultObjectIds.Single();var result=candidate.Scene.Objects.Single(o=>o.Id==id);
        Assert(result.Name=="Image contour"&&result.Material==null&&candidate.Summary.Contains("2 island(s)")&&candidate.Summary.Contains("1 hole(s)")&&candidate.Summary.Contains("cells"),"Contour preview omits real islands/holes/grid review or inherits a material.");Near(result.Bounds.Min.Z,0,.001);Near(result.Bounds.Size.Z,3,.001);
        Capture("contour-ring-preview-minimum",1040,680);var pose=CameraPose();await Workspace(4);Click(Named<Button>("ViewTopButton"));Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<FrameworkElement>("ImageReliefActions").Visibility==Visibility.Visible,"Compatible View discarded contour.");Capture("contour-ring-top",1280,780);await Workspace(0);
        Click(Named<Button>("AddImageReliefButton"));Assert(editor.Snapshot.SelectedObjectId==id&&editor.Snapshot.Scene.Objects.Length==before.Scene.Objects.Length+1&&editor.Snapshot.FilePath==before.FilePath,"Add contour lost identity or native path.");
        var accepted=editor.Snapshot;Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Contour Undo did not restore exact scene and selection.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Contour Redo did not restore exact reviewed scene.");
        var saved=Path.Combine(output,"ui-ring-contour.3mf");Ok(await editor.SaveAsync(saved));Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-ring-contour.stl")));var reopened=new EditorSession();Ok(await reopened.OpenAsync(saved));Assert(reopened.Snapshot.Scene.Objects.Length==accepted.Scene.Objects.Length,"Contour native save/reopen lost created object.");
        Pass("Visible Image mode, one-percent keyboard threshold, minimum fixed actions and true ring/island preview support compatible View, Add, exact Undo/Redo and shared save/reopen");

        await Open("cube-20mm.stl");before=editor.Snapshot;await Workspace(0);Click(Named<Button>("ImageReliefButton"));ReliefFile(Path.Combine(ContourInputs,"ring-island.png"));Named<TextBox>("ImageReliefDetail").Text="32";await PreviewRelief();candidate=editor.Snapshot.Preview!;id=candidate.ResultObjectIds.Single();
        Named<ComboBox>("ImageConversionMode").SelectedIndex=1;Assert(!Named<Button>("AddImageReliefButton").IsEnabled&&ReferenceEquals(editor.Snapshot.Preview,candidate),"Mode switch discarded review or allowed stale Add.");
        Named<TextBox>("ImageContourThickness").Text="NaN";Invoke("AddImageReliefClick",window,new RoutedEventArgs());Click(Named<Button>("PreviewImageReliefButton"));await Idle();SameAccepted(before);Assert(ReferenceEquals(editor.Snapshot.Preview,candidate),"Invalid contour draft erased prior relief.");
        Named<TextBox>("ImageContourThickness").Text="3";slider.Value=100;pose=CameraPose();Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&!Named<Button>("AddImageReliefButton").IsEnabled&&CameraPose()==pose,"Empty threshold changed prior candidate/camera or allowed stale Add.");
        slider.Value=50;await PreviewContour();var contour=editor.Snapshot.Preview!;Assert(contour.Id!=candidate.Id&&contour.ResultObjectIds.Single()==id,"Relief to contour replacement lost stable candidate identity/fresh token.");
        Named<ComboBox>("ImageConversionMode").SelectedIndex=0;Named<TextBox>("ImageReliefDepth").Text="NaN";Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,contour)&&!Named<Button>("AddImageReliefButton").IsEnabled,"Failed return to Relief lost contour or allowed stale Add.");
        Named<TextBox>("ImageReliefDepth").Text="3";await PreviewRelief();Assert(editor.Snapshot.Preview!.Id!=contour.Id&&editor.Snapshot.Preview.ResultObjectIds.Single()==id,"Contour to relief replacement lost identity/fresh token.");Click(Named<Button>("CancelImageReliefButton"));SameAccepted(before);
        Pass("Relief and Contour share exact-owned replacement; mode changes invalidate Add, failed/empty requests preserve prior candidate and camera, and both directions retain candidate object identity");

        await BeginContour("white.png");await PreviewContour();candidate=editor.Snapshot.Preview!;result=candidate.Scene.Objects.Single(o=>o.Id==candidate.ResultObjectIds.Single());Near(result.Bounds.Size.X,80,.001);Near(result.Bounds.Size.Y,40,.001);Near(result.Bounds.Size.Z,3,.001);
        ReliefFile(Path.Combine(ContourInputs,"black.png"));slider.Value=0;Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&!Named<Button>("AddImageReliefButton").IsEnabled,"Black threshold-zero was not empty or replaced prior candidate.");
        Named<CheckBox>("ImageReliefInvert").IsChecked=true;slider.Value=50;await PreviewContour();Assert(((ImageContourRequest)editor.Snapshot.Preview!.Request).Invert,"Invert was not sent to shared Core.");candidate=editor.Snapshot.Preview;
        ReliefFile(Path.Combine(ContourInputs,"white.png"));Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&!Named<Button>("AddImageReliefButton").IsEnabled,"Inverted white image did not remain empty.");
        ReliefFile(Path.Combine(ContourInputs,"invalid.png"));Click(Named<Button>("PreviewImageReliefButton"));await Idle();SameAccepted(before);Assert(ReferenceEquals(editor.Snapshot.Preview,candidate),"Malformed file erased contour review.");Click(Named<Button>("CancelImageReliefButton"));SameAccepted(before);
        Pass("Real white/black files obey strict bright-foreground and inversion rules; empty and malformed image requests retain the prior review and accepted document");

        await BeginContour();Named<TextBox>("ImageReliefDetail").Text="32";Click(Named<Button>("PreviewImageReliefButton"));Named<TextBox>("ImageContourThickness").Text="4";await Idle();Assert(!Named<Button>("AddImageReliefButton").IsEnabled,"In-flight changed thickness enabled stale Add.");SameAccepted(before);Click(Named<Button>("CancelImageReliefButton"));
        await BeginContour();Click(Named<Button>("PreviewImageReliefButton"));Click(Named<Button>("CancelImageReliefButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview==null&&Named<FrameworkElement>("ImageReliefActions").Visibility==Visibility.Collapsed,"In-flight Cancel retained contour/tool.");
        await BeginContour();foreach(var name in new[]{"ImageReliefWidth","ImageContourThickness","ImageReliefDetail"})Assert(Named<TextBox>(name).IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(Named<TextBox>(name)),"Image field lost local Undo/IME: "+name);
        CheckLocalTextUndo(Named<TextBox>("ImageContourThickness"),"3","2.5");SameAccepted(before);await PreviewContour();candidate=editor.Snapshot.Preview!;
        Invoke("MaterialClick",window,new RoutedEventArgs());Invoke("MirrorClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());SameAccepted(before);Assert(ReferenceEquals(editor.Snapshot.Preview,candidate),"Conflicting modal/history path replaced contour.");
        using(var keySource=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-contour-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewImageReliefButton","AddImageReliefButton","CancelImageReliefButton"}){var key=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted image footer.");}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelImageReliefButton").RaiseEvent(escape);Assert(escape.Handled,"Contour Escape not handled.");
        }
        SameAccepted(before);Assert(editor.Snapshot.Preview==null,"Escape retained contour.");
        Pass("Changed in-flight drafts, ready/in-flight cancellation, local text Undo/IME, standard footer Enter, Escape and conflicting tools retain accepted work");

        await Open("assembly-mm.3mf");Ok(await editor.PreviewArrangementAsync(new GroupRequest(editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray())));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Ok(editor.EnterGroup(editor.Snapshot.Scene.Groups.Single().Id));Refresh();before=editor.Snapshot;
        Assert(!Named<Button>("ImageReliefButton").IsEnabled,"Image creation enabled within member scope.");Invoke("ImageReliefClick",window,new RoutedEventArgs());SameAccepted(before);Assert(Named<FrameworkElement>("ImageReliefActions").Visibility==Visibility.Collapsed,"Image conversion bypassed member guard.");Ok(editor.ExitGroup());Refresh();
        Pass("Image conversion remains scene-only and cannot bypass explicit member scope or change group selection");
    }
}
