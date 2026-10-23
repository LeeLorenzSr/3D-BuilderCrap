using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static async Task BeginImageOutput(int mode)
    {await Workspace(0);Click(Named<Button>("ImageReliefButton"));Named<ComboBox>("ImageConversionMode").SelectedIndex=mode;ReliefFile(Path.Combine(ContourInputs,"ring-island.png"));}
    private static async Task PreviewImageOutput<T>() where T:GeometryRequest
    {Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(editor.Snapshot.Preview?.Request is T,"Image output failed: "+Named<TextBlock>("Status").Text);}
    private static async Task RunImageEdgeStampControls()
    {
        await Open("cube-20mm.stl");var before=editor.Snapshot;await BeginImageOutput(2);
        Assert(Named<ComboBox>("ImageConversionMode").Items.Count==4&&Named<FrameworkElement>("ImageEdgeFields").Visibility==Visibility.Visible&&Named<FrameworkElement>("ImageContourFields").Visibility==Visibility.Visible&&Named<FrameworkElement>("ImageStampFields").Visibility==Visibility.Collapsed,"Edge did not expose only relevant fields.");
        Assert(Named<TextBox>("ImageEdgeStrokeWidth").Text=="2"&&Named<TextBox>("ImageContourThickness").Text=="3","Edge defaults incorrect.");InspectionFooterInside("PreviewImageReliefButton","AddImageReliefButton","CancelImageReliefButton");Capture("edge-settings-minimum",1040,680);
        await PreviewImageOutput<ImageEdgeRequest>();SameAccepted(before);var preview=editor.Snapshot.Preview!;var id=preview.ResultObjectIds.Single();var edge=preview.Scene.Objects.Single(o=>o.Id==id);var request=(ImageEdgeRequest)preview.Request;
        Assert(request.StrokeWidthMillimeters==2&&request.Threshold==.5&&edge.Material==null&&preview.Summary.Contains("stroke",StringComparison.OrdinalIgnoreCase),"Edge request/review omitted actual stroke or assigned a material.");Near(edge.Bounds.Size.Z,3,.001);Capture("edge-preview-minimum",1040,680);
        var fieldBounds=Bounds(Named<TextBox>("ImageEdgeStrokeWidth"));Assert(fieldBounds.Bottom<=Bounds(Named<System.Windows.Controls.Border>("ImageReliefActions")).Top,"Edge stroke input is hidden behind the fixed footer.");
        await Workspace(4);Click(Named<Button>("ViewTopButton"));Assert(ReferenceEquals(editor.Snapshot.Preview,preview)&&Named<FrameworkElement>("ImageReliefActions").Visibility==Visibility.Visible,"View discarded Edge.");Capture("edge-top",1280,780);await Workspace(0);
        Click(Named<Button>("AddImageReliefButton"));var accepted=editor.Snapshot;Assert(accepted.Scene.Objects.Length==before.Scene.Objects.Length+1&&accepted.SelectedObjectId==id,"Edge Add lost object identity.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Edge Undo did not restore accepted scene.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Edge Redo changed the reviewed result.");
        var edgeSave=Path.Combine(output,"ui-image-edge.3mf");Ok(await editor.SaveAsync(edgeSave));Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-image-edge.stl")));var reopen=new EditorSession();Ok(await reopen.OpenAsync(edgeSave));Assert(reopen.Snapshot.Scene.Objects.Length==accepted.Scene.Objects.Length,"Edge native save/reopen lost object.");
        Pass("Edge exposes stroke/thickness and real Core preview, fits minimum fixed actions, retains compatible View, and supports Add/Undo/Redo/native save/reopen");

        await Open("cube-20mm.stl");before=editor.Snapshot;await BeginImageOutput(3);
        Assert(Named<FrameworkElement>("ImageStampFields").Visibility==Visibility.Visible&&Named<FrameworkElement>("ImageContourFields").Visibility==Visibility.Collapsed&&Named<CheckBox>("ImageStampMirror").IsChecked==true,"Stamp fields/mirror default incorrect.");
        Assert(Named<TextBox>("ImageStampHeight").Text=="2"&&Named<TextBox>("ImageStampBacking").Text=="2","Stamp physical defaults incorrect.");Capture("stamp-settings-minimum",1040,680);await PreviewImageOutput<ImageStampRequest>();SameAccepted(before);preview=editor.Snapshot.Preview!;id=preview.ResultObjectIds.Single();var stamp=preview.Scene.Objects.Single(o=>o.Id==id);
        Assert(((ImageStampRequest)preview.Request).Mirror&&stamp.Material==null,"Stamp did not send mirror/default material correctly.");Near(stamp.Bounds.Size.X,80,.001);Near(stamp.Bounds.Size.Y,60,.001);Near(stamp.Bounds.Min.Z,0,.001);Near(stamp.Bounds.Size.Z,4,.001);await Workspace(4);Click(Named<Button>("ViewIsometricButton"));await Workspace(0);Capture("stamp-preview-minimum",1040,680);
        Named<CheckBox>("ImageStampMirror").IsChecked=false;Assert(!Named<Button>("AddImageReliefButton").IsEnabled,"Mirror change allowed stale Add.");await PreviewImageOutput<ImageStampRequest>();Assert(!((ImageStampRequest)editor.Snapshot.Preview!.Request).Mirror&&editor.Snapshot.Preview.Id!=preview.Id&&editor.Snapshot.Preview.ResultObjectIds.Single()==id,"Mirror replacement lost explicit request or stable identity.");
        await Workspace(4);Click(Named<Button>("ViewTopButton"));Capture("stamp-top-unmirrored",1280,780);await Workspace(0);Click(Named<Button>("AddImageReliefButton"));accepted=editor.Snapshot;Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Stamp Undo failed.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Stamp Redo changed reviewed object.");
        var stampSave=Path.Combine(output,"ui-image-stamp.3mf");Ok(await editor.SaveAsync(stampSave));Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-image-stamp.stl")));reopen=new EditorSession();Ok(await reopen.OpenAsync(stampSave));Assert(reopen.Snapshot.Scene.Objects.Any(o=>Math.Abs(o.Bounds.Size.Z-4)<.001),"Stamp backing/height did not survive native save.");
        Pass("Stamp uses full-frame backing, separate physical raised height and mirror-for-stamping default; changed mirror requires a new review and Add/Undo/Redo/save preserve actual geometry");

        await Open("cube-20mm.stl");before=editor.Snapshot;await BeginImageOutput(0);Named<TextBox>("ImageReliefDetail").Text="32";await PreviewImageOutput<ImageReliefRequest>();preview=editor.Snapshot.Preview!;id=preview.ResultObjectIds.Single();
        foreach(var mode in new[]{1,2,3,0})
        {
            Named<ComboBox>("ImageConversionMode").SelectedIndex=mode;Assert(!Named<Button>("AddImageReliefButton").IsEnabled&&ReferenceEquals(editor.Snapshot.Preview,preview),"Mode switch erased review or allowed stale Add.");Click(Named<Button>("PreviewImageReliefButton"));await Idle();var next=editor.Snapshot.Preview!;Assert(next.Id!=preview.Id&&next.ResultObjectIds.Single()==id,"Four-mode replacement lost stable identity/fresh token.");preview=next;
        }
        Named<ComboBox>("ImageConversionMode").SelectedIndex=2;Named<TextBox>("ImageEdgeStrokeWidth").Text="NaN";Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,preview),"Invalid Edge draft erased prior Relief.");
        Named<TextBox>("ImageEdgeStrokeWidth").Text="2";Named<Slider>("ImageContourThreshold").Value=100;var camera=CameraPose();Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,preview)&&CameraPose()==camera&&!Named<Button>("AddImageReliefButton").IsEnabled,"Empty Edge changed candidate/camera or allowed stale Add.");
        Named<ComboBox>("ImageConversionMode").SelectedIndex=3;Named<TextBox>("ImageStampHeight").Text="1000";Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,preview),"Invalid Stamp draft erased prior review.");Click(Named<Button>("CancelImageReliefButton"));SameAccepted(before);
        Pass("All four modes share one exact-owned candidate; mode/invalid/empty drafts cannot accept stale work, failed requests retain candidate/camera, and Cancel preserves accepted state");

        await BeginImageOutput(2);Click(Named<Button>("PreviewImageReliefButton"));Named<TextBox>("ImageEdgeStrokeWidth").Text="3";await Idle();Assert(!Named<Button>("AddImageReliefButton").IsEnabled,"In-flight Edge draft enabled stale Add.");Click(Named<Button>("CancelImageReliefButton"));SameAccepted(before);
        await BeginImageOutput(3);Click(Named<Button>("PreviewImageReliefButton"));Click(Named<Button>("CancelImageReliefButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview==null,"In-flight Stamp Cancel retained candidate.");
        await BeginImageOutput(3);foreach(var name in new[]{"ImageStampHeight","ImageStampBacking"}){Assert(Named<TextBox>(name).IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(Named<TextBox>(name)),"Stamp input lost local Undo/IME.");CheckLocalTextUndo(Named<TextBox>(name),"2","3.5");}
        await PreviewImageOutput<ImageStampRequest>();preview=editor.Snapshot.Preview!;Invoke("UndoClick",window,new RoutedEventArgs());Invoke("MaterialClick",window,new RoutedEventArgs());SameAccepted(before);Assert(ReferenceEquals(editor.Snapshot.Preview,preview),"Conflicting action replaced Stamp.");
        using(var keySource=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-edge-stamp-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewImageReliefButton","AddImageReliefButton","CancelImageReliefButton"}){var key=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted image footer.");}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelImageReliefButton").RaiseEvent(escape);Assert(escape.Handled,"Stamp Escape not handled.");
        }
        SameAccepted(before);Assert(editor.Snapshot.Preview==null,"Escape retained image output.");
        Pass("Edge/Stamp use the existing in-flight draft/cancel guards, local text Undo/IME, compatible modal policy, standard button Enter and Escape");
    }
}
