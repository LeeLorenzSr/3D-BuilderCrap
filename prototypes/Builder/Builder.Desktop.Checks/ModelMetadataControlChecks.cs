using Builder.Core;
using Builder.Desktop;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static T MetadataControl<T>(ModelMetadataDialog dialog,string name) where T:class => (T)dialog.FindName(name);
    private static void MetadataEdit(Action<ModelMetadataDialog> action)
    {
        navigationDialogs.MetadataAction=action;
        try{Click(Named<Button>("AboutModelButton"));}
        finally{navigationDialogs.MetadataAction=null;}
    }
    private static async Task MetadataFixture()
    {
        Ok(await editor.OpenAsync(Path.Combine(root,".local/evidence/model-metadata-20261021/inputs/painted-group.3mf"),decision:DirtyDecision.Discard));
        Refresh();await Workspace(4);
    }
    private static void CaptureMetadataDialog(ModelMetadataDialog dialog,string name,double height)
    {
        var content=(FrameworkElement)dialog.Content;
        content.Measure(new Size(520,height));content.Arrange(new Rect(0,0,520,height));content.UpdateLayout();
        foreach(var control in new[]{"ApplyModelMetadataButton","CancelModelMetadataButton"})
        {
            var button=MetadataControl<Button>(dialog,control);
            var bounds=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
            Assert(bounds.Width>=90&&bounds.Top>=0&&bounds.Right<=520&&bounds.Bottom<=height,"Metadata footer clipped: "+control+" "+bounds);
        }
        var scroll=MetadataControl<ScrollViewer>(dialog,"ModelMetadataScroll");scroll.ScrollToEnd();content.UpdateLayout();
        var license=MetadataControl<TextBox>(dialog,"ModelLicenseTerms");
        var end=license.TransformToAncestor(scroll).TransformBounds(new Rect(license.RenderSize));
        Assert(end.Bottom<=scroll.ActualHeight+1&&end.Top>=-1,"License field cannot be reached by scrolling.");
        var image=new RenderTargetBitmap(520,(int)height,96,96,PixelFormats.Pbgra32);image.Render(content);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);
    }
    private static async Task RunModelMetadataControls()
    {
        await MetadataFixture();Reachable("AboutModelButton");Capture("about-entry-minimum",1040,680);
        var before=editor.Snapshot;Named<TextBox>("ObjectName").Text="Unapplied object-name draft";
        MetadataEdit(dialog=>{
            Assert(!Named<FrameworkElement>("Toolbar").IsEnabled&&!Named<TabControl>("WorkspaceTabs").IsEnabled,"Metadata editor did not lock unrelated owner actions.");
            MetadataControl<TextBox>(dialog,"ModelTitle").Text="Cancelled model title";
            MetadataControl<TextBox>(dialog,"ModelDescription").Text="Local notes\nnot accepted";SameAccepted(before);
            CaptureMetadataDialog(dialog,"about-dialog-default",600);CaptureMetadataDialog(dialog,"about-dialog-minimum",440);
            Click(MetadataControl<Button>(dialog,"CancelModelMetadataButton"));
        });
        SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Unapplied object-name draft","About Cancel erased idle object-name draft.");
        MetadataEdit(dialog=>{MetadataControl<TextBox>(dialog,"ModelTitle").Text="Window-close draft";dialog.Close();});SameAccepted(before);
        MetadataEdit(dialog=>{
            using var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-metadata-keys"){Width=1,Height=1,WindowStyle=0});
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            dialog.RaiseEvent(key);Assert(key.Handled&&!dialog.Accepted,"Escape did not discard metadata drafts.");
        });SameAccepted(before);
        Assert(Named<Button>("AboutModelButton").IsEnabled&&Named<TabControl>("WorkspaceTabs").IsEnabled,"Metadata Cancel left owner locked.");
        Pass("About is reachable in View; default/minimum dialog scrolls to attribution while fixed Apply/Cancel fit, and Cancel/window close preserve accepted work and idle rename drafts");

        Ok(editor.UpdateModelMetadata(editor.Snapshot.Scene.Metadata with{Language="en-US"},editor.Snapshot.Revision));
        Ok(editor.SelectMany([]));Refresh();before=editor.Snapshot;
        MetadataEdit(dialog=>{
            MetadataControl<TextBox>(dialog,"ModelTitle").Text="Printer plate Ω";
            MetadataControl<TextBox>(dialog,"ModelDesigner").Text="設計者 & maker";
            MetadataControl<TextBox>(dialog,"ModelDescription").Text="Line one\r\nLine two\twith tabs";
            MetadataControl<TextBox>(dialog,"ModelCopyright").Text="© 2026 Example";
            MetadataControl<TextBox>(dialog,"ModelLicenseTerms").Text="Example terms\nAttribution requested";
            Click(MetadataControl<Button>(dialog,"ApplyModelMetadataButton"));Assert(dialog.Accepted,"Valid model metadata was rejected.");
        });
        var applied=editor.Snapshot;
        Assert(applied.Scene.Metadata.Title=="Printer plate Ω"&&applied.Scene.Metadata.Language=="en-US"&&applied.Scene.Metadata.Description=="Line one\nLine two\twith tabs","Metadata text or retained language changed.");
        Assert(applied.Scene.Objects.SequenceEqual(before.Scene.Objects)&&applied.Scene.Groups.SequenceEqual(before.Scene.Groups)&&applied.FilePath==before.FilePath&&applied.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Model title changed objects, file, groups or selection.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Metadata was not one exact undo step.");
        Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,applied.Scene),"Metadata Redo failed.");
        navigationDialogs.SavePath=Path.Combine(output,"model-info-roundtrip.3mf");Assert(await (Task<bool>)Invoke("Save",true)!,"Metadata native Save failed.");
        navigationDialogs.OpenPath=navigationDialogs.SavePath;await ChooseDocument();
        Assert(editor.Snapshot.Scene.Metadata==applied.Scene.Metadata&&editor.Snapshot.Scene.Objects.Select(o=>o.Name).SequenceEqual(applied.Scene.Objects.Select(o=>o.Name)),"3MF reopen lost model information or changed object names.");
        Pass("Five document fields apply without selection in one Undo/Redo step, retain original language/object names/materials, and roundtrip through the actual Save/Open adapters");

        before=editor.Snapshot;
        MetadataEdit(dialog=>{
            var field=MetadataControl<TextBox>(dialog,"ModelTitle");Assert(field.MaxLength==ModelMetadata.MaxTitleLength,"Title does not use Core limit.");
            field.Text=new string('x',ModelMetadata.MaxTitleLength+1);Click(MetadataControl<Button>(dialog,"ApplyModelMetadataButton"));
            Assert(!dialog.Accepted&&dialog.LastResult?.Outcome==Outcome.Invalid&&field.Text.Length==ModelMetadata.MaxTitleLength+1&&MetadataControl<TextBlock>(dialog,"ModelMetadataError").Visibility==Visibility.Visible,"Invalid Apply lost draft or accepted invalid metadata.");SameAccepted(before);
            Click(MetadataControl<Button>(dialog,"CancelModelMetadataButton"));
        });
        EditorSnapshot? newer=null;
        MetadataEdit(dialog=>{
            var field=MetadataControl<TextBox>(dialog,"ModelTitle");field.Text="Stale local title";
            Ok(editor.UpdateModelMetadata(editor.Snapshot.Scene.Metadata with{Designer="Newer accepted designer"},editor.Snapshot.Revision));newer=editor.Snapshot;
            Click(MetadataControl<Button>(dialog,"ApplyModelMetadataButton"));
            Assert(!dialog.Accepted&&dialog.LastResult?.Outcome==Outcome.Conflict&&field.Text=="Stale local title","Stale Apply did not retain its draft/error.");
            Click(MetadataControl<Button>(dialog,"CancelModelMetadataButton"));
        });SameAccepted(newer!);Refresh();
        Pass("Over-limit and stale metadata Apply leave the form draft/error available; newer accepted metadata is never overwritten");

        navigationDialogs.Decision=DirtyDecision.Discard;await NewDocument();await Workspace(4);
        Assert(editor.Snapshot.Scene.Metadata==ModelMetadata.Empty&&!editor.Snapshot.CanUndo,"New did not clear document metadata/history.");before=editor.Snapshot;
        MetadataEdit(dialog=>{MetadataControl<TextBox>(dialog,"ModelTitle").Text="Empty document notes";Click(MetadataControl<Button>(dialog,"ApplyModelMetadataButton"));});
        Assert(editor.Snapshot.Scene.Objects.IsEmpty&&editor.Snapshot.IsDirty&&editor.Snapshot.Scene.Metadata.Title=="Empty document notes","Metadata wrongly required a selected object.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Empty document metadata Undo failed.");
        Ok(editor.Execute(new AddCube()));Refresh();await Workspace(1);Click(Named<Button>("MoveHandleButton"));Click(Named<Button>("PreviewTransformButton"));await Idle();await Workspace(4);before=editor.Snapshot;var preview=before.Preview;var calls=0;
        navigationDialogs.MetadataAction=_=>calls++;Invoke("AboutModelClick",window,new RoutedEventArgs());navigationDialogs.MetadataAction=null;
        Assert(calls==0&&!Named<Button>("AboutModelButton").IsEnabled&&ReferenceEquals(editor.Snapshot.Preview,preview),"About bypassed active preview guard.");SameAccepted(before);Click(Named<Button>("CancelTransformButton"));
        Pass("New clears metadata; empty documents can edit/Undo information, while active tool/preview blocks About without losing its candidate");

        await MetadataFixture();var group=editor.Snapshot.Scene.Groups.First();Ok(editor.EnterGroup(group.Id,group.ObjectIds[^1]));Refresh();await Workspace(4);before=editor.Snapshot;
        MetadataEdit(dialog=>{
            var content=(FrameworkElement)dialog.Content;content.Measure(new Size(520,600));content.Arrange(new Rect(0,0,520,600));content.UpdateLayout();
            var title=MetadataControl<TextBox>(dialog,"ModelTitle");var description=MetadataControl<TextBox>(dialog,"ModelDescription");
            foreach(var field in new[]{title,description})Assert(field.IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(field),"Metadata field lost local Undo/IME.");
            CheckLocalTextUndo(title,"Local title","Local draft Ω");CheckLocalTextUndo(description,"Notes","Draft\nnotes");SameAccepted(before);dialog.Close();
        });SameAccepted(before);
        Named<TextBox>("ObjectName").Text="Still local member name";
        MetadataEdit(dialog=>Click(MetadataControl<Button>(dialog,"ApplyModelMetadataButton")));SameAccepted(before);
        Assert(Named<TextBox>("ObjectName").Text=="Still local member name","No-op metadata Apply erased idle rename draft.");
        MetadataEdit(dialog=>{MetadataControl<TextBox>(dialog,"ModelDesigner").Text="Member-scope document author";Click(MetadataControl<Button>(dialog,"ApplyModelMetadataButton"));});
        Assert(editor.Snapshot.EditingGroupId==before.EditingGroupId&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&editor.Snapshot.Scene.Objects.SequenceEqual(before.Scene.Objects),"Document-wide metadata changed member scope/geometry.");
        Pass("Metadata keeps local text Undo/IME and member selection/scope; unchanged Apply is history-neutral and preserves idle drafts");
    }
}
