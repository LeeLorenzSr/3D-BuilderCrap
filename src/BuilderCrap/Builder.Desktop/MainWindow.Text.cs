using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Builder.Desktop;

public partial class MainWindow
{
    private enum TextToolMode { NewText, Emboss }
    private TextToolMode? textToolMode;
    private bool LocalModalToolActive=>primitiveActive||InspectionActive||textToolMode!=null||imageReliefActive||hollowActive||simplifyActive||smoothActive||separateActive||extrudeDownActive||materialsActive;
    private bool ModalToolActive=>LocalModalToolActive||mcpPreviewActive;
    private long textBaseRevision,textDraftVersion;
    private Guid? textScope,textTarget,ownedTextPreview;
    private bool textDraftChanged,textCancelRequested;
    private GeometryRequest? reviewedTextRequest;
    private void NewTextClick(object sender,RoutedEventArgs e)=>BeginTextTool(TextToolMode.NewText);
    private void EmbossClick(object sender,RoutedEventArgs e)=>BeginTextTool(TextToolMode.Emboss);
    private void BeginTextTool(TextToolMode mode)
    {
        if(!ArrangementSelectionAvailable)return;
        var snapshot=editor.Snapshot;
        if(mode==TextToolMode.NewText&&snapshot.EditingGroupId!=null){Message("Choose Done before adding text to the scene.");return;}
        if(mode==TextToolMode.Emboss&&!SingleUngroupedSelection){Message("Select one independent object or group member to emboss.");return;}
        System.Collections.Immutable.ImmutableArray<string> fonts;
        try { fonts=editor.GetFontFamilies(); }
        catch(ModelException error){Message(error.Message,true);return;}
        if(fonts.IsEmpty){Message("No installed outline fonts are available.",true);return;}
        CancelPendingPick();textToolMode=mode;textBaseRevision=snapshot.Revision;textScope=snapshot.EditingGroupId;textTarget=snapshot.SelectedObjectId;
        ownedTextPreview=null;reviewedTextRequest=null;textDraftVersion=0;textDraftChanged=true;textCancelRequested=false;
        refreshing=true;
        try
        {
            TextFont.ItemsSource=fonts;TextFont.SelectedItem=fonts.FirstOrDefault(f=>f=="Arial")??fonts[0];
            TextContent.Text=mode==TextToolMode.NewText?"Text":"O";
            TextHeight.Text=mode==TextToolMode.NewText?"10":"5";TextDepth.Text=mode==TextToolMode.NewText?"2":"1";
            TextFace.SelectedIndex=4;TextEmbossMode.SelectedIndex=0;TextOffsetU.Text=TextOffsetV.Text=TextRotation.Text="0";
        }
        finally{refreshing=false;}
        Refresh();TextContent.Focus();TextContent.SelectAll();
        Message("Enter text and physical dimensions, then Preview. The accepted scene stays unchanged until you apply.");
    }
    private void TextSelectionChanged(object sender,SelectionChangedEventArgs e)=>TextSettingsChanged(sender,e);
    private void TextSettingsChanged(object sender,RoutedEventArgs e)
    {
        if(refreshing||textToolMode==null)return;
        textDraftVersion++;textDraftChanged=true;RefreshTextTool();
    }
    private GeometryRequest? ReadTextRequest()
    {
        bool Read(TextBox field,string label,out double value)
        {
            if(double.TryParse(field.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out value)&&double.IsFinite(value))return true;
            Message($"Enter a finite {label}.",true);return false;
        }
        if(TextFont.SelectedItem is not string family){Message("Choose an installed font.",true);return null;}
        if(!Read(TextHeight,"text height in millimeters",out var height)||!Read(TextDepth,"depth in millimeters",out var depth))return null;
        if(height<0.1||height>1000||depth<0.05||depth>1000){Message("Text height must be 0.1–1,000 mm; depth must be 0.05–1,000 mm.",true);return null;}
        var source=new FontTextSource(new(TextContent.Text,family,height));
        if(textToolMode==TextToolMode.NewText)return new TextRequest(source,depth){MemberGroupId=textScope};
        if(textToolMode!=TextToolMode.Emboss||textTarget is not {} target||TextFace.SelectedIndex is <0 or >5||TextEmbossMode.SelectedIndex is <0 or >1)return null;
        if(!Read(TextOffsetU,"U offset in millimeters",out var u)||!Read(TextOffsetV,"V offset in millimeters",out var v)||!Read(TextRotation,"rotation in degrees",out var rotation))return null;
        return new EmbossRequest(target,source,new((EmbossFace)TextFace.SelectedIndex,new(u,v),rotation),depth,(EmbossMode)TextEmbossMode.SelectedIndex){MemberGroupId=textScope};
    }
    private async void PreviewTextClick(object sender,RoutedEventArgs e)
    {
        if(textToolMode==null||operation!=null||textCancelRequested)return;
        textDraftChanged=true;
        if(ReadTextRequest() is not {} request){RefreshTextTool();return;}
        var version=textDraftVersion;
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshTextTool();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        try
        {
            var result=request is TextRequest text
                ? await editor.PreviewTextAsync(text,ownedTextPreview,cancellation.Token,progress,textBaseRevision)
                : await editor.PreviewEmbossAsync((EmbossRequest)request,ownedTextPreview,cancellation.Token,progress,textBaseRevision);
            if(result.Succeeded&&editor.Snapshot.Preview is {Request:TextRequest or EmbossRequest} preview)
            {ownedTextPreview=preview.Id;reviewedTextRequest=request;textDraftChanged=version!=textDraftVersion;}
            Message(result.Succeeded?"Text preview ready. Review it, then accept or cancel.":result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(textCancelRequested)FinishCancelText();else{Refresh();if(editor.Snapshot.Preview?.Id==ownedTextPreview)FitScene();}
        }
    }
    private void ApplyTextClick(object sender,RoutedEventArgs e)
    {
        if(textToolMode==null||operation!=null||textDraftChanged||reviewedTextRequest==null||
            editor.Snapshot.Preview is not {Request:TextRequest or EmbossRequest} preview||preview.Id!=ownedTextPreview)return;
        if(ReadTextRequest()!=reviewedTextRequest){textDraftChanged=true;RefreshTextTool();return;}
        var result=editor.ApplyPreview(preview.Id,textBaseRevision);
        if(result.Succeeded){ClearTextTool();Refresh();FitScene();ViewportSurface.Focus();}else RefreshTextTool();
        Message(result.Message,!result.Succeeded);
    }
    private void CancelTextClick(object sender,RoutedEventArgs e)=>CancelTextTool();
    private void CancelTextTool()
    {
        if(textToolMode==null)return;
        if(operation!=null){textCancelRequested=true;operation.Cancel();RefreshTextTool();Message("Cancelling text preview…");}
        else FinishCancelText();
    }
    private void FinishCancelText()
    {
        if(editor.Snapshot.Preview is {Request:TextRequest or EmbossRequest} preview&&preview.Id==ownedTextPreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){textCancelRequested=false;RefreshTextTool();Message(result.Message,true);return;}
        }
        ClearTextTool();Refresh();ViewportSurface.Focus();Message("Text tool cancelled. Your scene is unchanged.");
    }
    private void ClearTextTool()
    {textToolMode=null;ownedTextPreview=textTarget=textScope=null;reviewedTextRequest=null;textCancelRequested=textDraftChanged=false;}
    private void RefreshTextTool()
    {
        RefreshWorkspaceNavigation();
        if(TextToolPanel==null)return;
        var active=textToolMode!=null;
        TextToolPanel.Visibility=TextToolActions.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        EmbossButton.IsEnabled=ArrangementSelectionAvailable&&SingleUngroupedSelection;
        if(!active)return;
        var emboss=textToolMode==TextToolMode.Emboss;
        TextToolTitle.Text=emboss?"Emboss selected object":"New text";
        TextEmbossFields.Visibility=emboss?Visibility.Visible:Visibility.Collapsed;
        TextPlacementNote.Text=emboss?"Text is centered on the selected bounds face. U/V offsets and rotation use that face's plane. Text must fit over solid material; unsupported or through-cut labels are rejected.":
            "New text starts at minimum X/Y/Z = 0 mm, on the bed. It may overlap existing objects. Move it after adding.";
        var candidate=editor.Snapshot.Preview is {Request:TextRequest or EmbossRequest} p&&p.Id==ownedTextPreview?p:null;
        var source=reviewedTextRequest is TextRequest text?text.Source:(reviewedTextRequest as EmbossRequest)?.Source;
        var depth=reviewedTextRequest is TextRequest solid?solid.DepthMillimeters:(reviewedTextRequest as EmbossRequest)?.DepthMillimeters;
        var description=source is FontTextSource font?$"{font.Specification.FontFamily} · ink height {Number(font.Specification.HeightMillimeters)} mm · depth {Number(depth!.Value)} mm":"Text preview ready.";
        TextToolSummary.Text=candidate==null?"Preview the text before accepting it.":description+
            (textDraftChanged?"\nSettings changed. Preview again before applying.":"\nAccepting creates one undoable edit.");
        ApplyTextButton.Content=emboss?"Apply":"Add text";
        TextToolPanel.IsEnabled=operation==null&&!textCancelRequested;
        PreviewTextButton.IsEnabled=operation==null&&!textCancelRequested;
        ApplyTextButton.IsEnabled=candidate!=null&&!textDraftChanged&&operation==null&&!textCancelRequested&&editor.Snapshot.Revision==textBaseRevision;
        CancelTextButton.IsEnabled=!textCancelRequested;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;
        ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=InspectionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;PreviewLabel.Text=emboss?"Emboss preview · not yet applied":"Text preview · not yet added";
        EmptyHint.Visibility=Visibility.Collapsed;
    }
}
