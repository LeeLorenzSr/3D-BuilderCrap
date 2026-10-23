using Builder.Core;
using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Builder.Desktop;

public partial class MainWindow
{
    // The UI owns a draft and its token; Core alone decodes, generates and accepts geometry.
    private bool imageReliefActive,imageReliefDraftChanged,imageReliefCancelRequested;
    private string? imageReliefPath;
    private long imageReliefBaseRevision,imageReliefDraftVersion;
    private Guid? ownedImageReliefPreview;
    private GeometryRequest? reviewedImageRelief;
    private enum ImageConversionKind { Relief,Contour,Edge,Stamp }
    private ImageConversionKind ImageMode=>(ImageConversionKind)Math.Clamp(ImageConversionMode.SelectedIndex,0,3);
    private string ImageConversionName=>ImageMode.ToString().ToLowerInvariant();
    private static bool ImageConversionRequest(GeometryRequest request)=>request is ImageReliefRequest or ImageContourRequest or ImageEdgeRequest or ImageStampRequest;
    private static string ImageRequestName(GeometryRequest request)=>request switch
    {ImageContourRequest=>"contour",ImageEdgeRequest=>"edge",ImageStampRequest=>"stamp",_=>"relief"};

    private void ImageReliefClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable)return;
        if(editor.Snapshot.EditingGroupId!=null){Message("Choose Done before adding an image conversion.");return;}
        CancelPendingPick();imageReliefActive=true;imageReliefBaseRevision=editor.Snapshot.Revision;
        imageReliefDraftVersion=0;imageReliefDraftChanged=true;imageReliefCancelRequested=false;
        imageReliefPath=null;ownedImageReliefPreview=null;reviewedImageRelief=null;
        var wasRefreshing=refreshing;refreshing=true;
        try
        {
            ImageReliefFile.Text="No image selected";ImageReliefFile.ToolTip=null;
            ImageConversionMode.SelectedIndex=0;
            ImageReliefWidth.Text="80";ImageReliefDepth.Text="3";ImageReliefBacking.Text="1";ImageReliefDetail.Text="96";ImageReliefInvert.IsChecked=false;
            ImageContourThickness.Text="3";ImageContourThreshold.Value=50;
            ImageEdgeStrokeWidth.Text="2";ImageStampHeight.Text="2";ImageStampBacking.Text="2";ImageStampMirror.IsChecked=true;
        }
        finally{refreshing=wasRefreshing;}
        Refresh();ChooseReliefImageButton.Focus();Message("Choose a PNG, JPEG or BMP, set its physical width, then Preview.");
    }
    private void ChooseReliefImageClick(object sender,RoutedEventArgs e)
    {
        if(!imageReliefActive||operation!=null)return;
        var dialog=new OpenFileDialog{Title="Choose image to convert",Filter="Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",CheckFileExists=true,Multiselect=false};
        if(dialog.ShowDialog(this)==true)SetReliefImage(dialog.FileName);
    }
    private void SetReliefImage(string path)
    {
        if(!imageReliefActive||operation!=null)return;
        imageReliefPath=Path.GetFullPath(path);ImageReliefFile.Text=Path.GetFileName(imageReliefPath);ImageReliefFile.ToolTip=imageReliefPath;
        ImageReliefSettingsChanged(this,new RoutedEventArgs());Message("Image chosen. Preview reads the image and retains its aspect ratio.");
    }
    private void ImageReliefSettingsChanged(object sender,RoutedEventArgs e)
    {
        if(refreshing||!imageReliefActive)return;
        imageReliefDraftVersion++;imageReliefDraftChanged=true;RefreshImageRelief();
    }
    private GeometryRequest? ReadImageRelief()
    {
        bool Read(TextBox field,string label,double minimum,double maximum,out double value)
        {
            if(double.TryParse(field.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out value)&&double.IsFinite(value)&&value>=minimum&&value<=maximum)return true;
            Message($"Enter {label} from {Number(minimum)} to {Number(maximum)} mm.",true);return false;
        }
        if(imageReliefPath==null){Message("Choose a PNG, JPEG or BMP image first.",true);return null;}
        if(!Read(ImageReliefWidth,"image width",.1,1000,out var width))return null;
        if(!int.TryParse(ImageReliefDetail.Text,NumberStyles.Integer,CultureInfo.CurrentCulture,out var samples)||samples<2||samples>128)
        {Message("Detail must be a whole number from 2 to 128 along the longer edge.",true);return null;}
        var source=new ImageFileSource(imageReliefPath);var threshold=ImageContourThreshold.Value/100;var invert=ImageReliefInvert.IsChecked==true;
        if(ImageMode is ImageConversionKind.Contour or ImageConversionKind.Edge)
        {
            if(!Read(ImageContourThickness,ImageConversionName+" thickness",.1,100,out var thickness))return null;
            if(ImageMode==ImageConversionKind.Edge)
            {
                if(!Read(ImageEdgeStrokeWidth,"stroke width",.1,100,out var stroke))return null;
                return new ImageEdgeRequest(source,width,thickness,stroke,threshold,samples,invert);
            }
            return new ImageContourRequest(source,width,thickness,threshold,samples,invert);
        }
        if(ImageMode==ImageConversionKind.Stamp)
        {
            if(!Read(ImageStampHeight,"raised height",.1,100,out var height)||!Read(ImageStampBacking,"backing thickness",.1,100,out var backingThickness))return null;
            return new ImageStampRequest(source,width,height,backingThickness,threshold,samples,invert,ImageStampMirror.IsChecked==true);
        }
        if(!Read(ImageReliefDepth,"relief depth",0,100,out var depth)||!Read(ImageReliefBacking,"backing thickness",.1,100,out var backing))return null;
        return new ImageReliefRequest(new ImageFileSource(imageReliefPath),width,depth,backing,samples,ImageReliefInvert.IsChecked==true);
    }
    private async void PreviewImageReliefClick(object sender,RoutedEventArgs e)
    {
        if(!imageReliefActive||operation!=null||imageReliefCancelRequested)return;
        imageReliefDraftChanged=true;
        if(ReadImageRelief() is not {} request){RefreshImageRelief();return;}
        var version=imageReliefDraftVersion;var outputName=ImageRequestName(request);
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshImageRelief();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        var prepared=false;
        try
        {
            // Core permits exact-owned image mode replacement and retains the candidate leaf ID.
            var result=await (request switch
            {
                ImageContourRequest contour=>editor.PreviewImageContourAsync(contour,ownedImageReliefPreview,cancellation.Token,progress,imageReliefBaseRevision),
                ImageEdgeRequest edge=>editor.PreviewImageEdgeAsync(edge,ownedImageReliefPreview,cancellation.Token,progress,imageReliefBaseRevision),
                ImageStampRequest stamp=>editor.PreviewImageStampAsync(stamp,ownedImageReliefPreview,cancellation.Token,progress,imageReliefBaseRevision),
                _=>editor.PreviewImageReliefAsync((ImageReliefRequest)request,ownedImageReliefPreview,cancellation.Token,progress,imageReliefBaseRevision)
            });
            if(result.Succeeded&&editor.Snapshot.Preview is {} preview&&ImageConversionRequest(preview.Request))
            {prepared=true;ownedImageReliefPreview=preview.Id;reviewedImageRelief=request;imageReliefDraftChanged=version!=imageReliefDraftVersion;}
            Message(result.Succeeded?$"Image {outputName} preview ready. Review it, then Add {outputName} or Cancel.":result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(imageReliefCancelRequested)FinishCancelImageRelief();else{Refresh();if(prepared&&editor.Snapshot.Preview?.Id==ownedImageReliefPreview)FitScene();}
        }
    }
    private void AddImageReliefClick(object sender,RoutedEventArgs e)
    {
        if(!imageReliefActive||operation!=null||imageReliefDraftChanged||reviewedImageRelief==null||
            editor.Snapshot.Preview is not {} preview||!ImageConversionRequest(preview.Request)||preview.Id!=ownedImageReliefPreview)return;
        if(ReadImageRelief()!=reviewedImageRelief){imageReliefDraftChanged=true;RefreshImageRelief();return;}
        var result=editor.ApplyPreview(preview.Id,imageReliefBaseRevision);
        if(result.Succeeded){ClearImageRelief();Refresh();FitScene();ViewportSurface.Focus();}else RefreshImageRelief();
        Message(result.Message,!result.Succeeded);
    }
    private void CancelImageReliefClick(object sender,RoutedEventArgs e)=>CancelImageRelief();
    private void CancelImageRelief()
    {
        if(!imageReliefActive)return;
        if(operation!=null){imageReliefCancelRequested=true;operation.Cancel();RefreshImageRelief();Message("Cancelling image conversion…");}
        else FinishCancelImageRelief();
    }
    private void FinishCancelImageRelief()
    {
        if(editor.Snapshot.Preview is {} preview&&ImageConversionRequest(preview.Request)&&preview.Id==ownedImageReliefPreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){imageReliefCancelRequested=false;RefreshImageRelief();Message(result.Message,true);return;}
        }
        ClearImageRelief();Refresh();ViewportSurface.Focus();Message("Image conversion cancelled. Your scene is unchanged.");
    }
    private void ClearImageRelief()
    {imageReliefActive=imageReliefDraftChanged=imageReliefCancelRequested=false;ownedImageReliefPreview=null;reviewedImageRelief=null;imageReliefPath=null;}
    private void RefreshImageRelief()
    {
        RefreshWorkspaceNavigation();
        if(ImageReliefPanel==null)return;
        ImageReliefPanel.Visibility=ImageReliefActions.Visibility=imageReliefActive?Visibility.Visible:Visibility.Collapsed;
        if(!imageReliefActive)return;
        var binary=ImageMode!=ImageConversionKind.Relief;
        ImageReliefFields.Visibility=binary?Visibility.Collapsed:Visibility.Visible;
        ImageContourFields.Visibility=ImageMode is ImageConversionKind.Contour or ImageConversionKind.Edge?Visibility.Visible:Visibility.Collapsed;
        var edgeMode=ImageMode==ImageConversionKind.Edge;
        ImageEdgeFields.Visibility=edgeMode?Visibility.Visible:Visibility.Collapsed;
        ImageContourFields.ColumnDefinitions[1].Width=new GridLength(edgeMode?12:0);
        ImageContourFields.ColumnDefinitions[2].Width=edgeMode?new GridLength(1,GridUnitType.Star):new GridLength(0);
        ImageStampFields.Visibility=ImageMode==ImageConversionKind.Stamp?Visibility.Visible:Visibility.Collapsed;
        ImageThresholdFields.Visibility=binary?Visibility.Visible:Visibility.Collapsed;
        ImageContourThresholdValue.Text=$"{Number(ImageContourThreshold.Value)}%";
        ImageDetailLabel.Text=binary?"Detail · cells on longer edge":"Detail · samples on longer edge";
        ImageDetailHelp.Text=binary?"2–128. More cells preserve finer outlines and add triangles.":"2–128. More samples preserve finer detail and add triangles.";
        ImageConversionHelp.Text=ImageMode switch
        {
            ImageConversionKind.Contour=>"Bright areas become solid. Invert uses dark areas. Transparent background stays empty; holes stay open. Outlines follow the grid cells.",
            ImageConversionKind.Edge=>"Creates the inner border of bright areas. Invert uses dark areas. Stroke width rounds up to grid cells; narrow areas may become solid.",
            ImageConversionKind.Stamp=>"Bright artwork is raised on a full rectangular backing. Invert uses dark areas. Holes stop at the backing. Mirror reverses left and right for stamping.",
            _=>"White raises the surface. Transparent areas stay at the backing height. This creates a rectangular solid; transparency does not cut holes."
        };
        AddImageReliefButton.Content="Add";
        AddImageReliefButton.ToolTip="Add reviewed image "+ImageConversionName;
        System.Windows.Automation.AutomationProperties.SetName(AddImageReliefButton,"Add reviewed image "+ImageConversionName);
        var candidate=editor.Snapshot.Preview is {} p&&ImageConversionRequest(p.Request)&&p.Id==ownedImageReliefPreview?p:null;
        ImageReliefSummary.Text=candidate==null?$"Choose an image and Preview before adding {ImageConversionName}.":candidate.Summary+
            (imageReliefDraftChanged?$"\nSettings changed. Preview again before Add {ImageConversionName}.":$"\nAdd {ImageConversionName} accepts one undoable object.");
        ImageReliefPanel.IsEnabled=operation==null&&!imageReliefCancelRequested;
        PreviewImageReliefButton.IsEnabled=operation==null&&!imageReliefCancelRequested;
        AddImageReliefButton.IsEnabled=candidate!=null&&!imageReliefDraftChanged&&operation==null&&!imageReliefCancelRequested&&editor.Snapshot.Revision==imageReliefBaseRevision;
        CancelImageReliefButton.IsEnabled=!imageReliefCancelRequested;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;
        ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=InspectionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;
        PreviewLabel.Text=$"Image {(candidate==null?ImageConversionName:ImageRequestName(candidate.Request))} preview · not yet added";EmptyHint.Visibility=Visibility.Collapsed;
    }
}
