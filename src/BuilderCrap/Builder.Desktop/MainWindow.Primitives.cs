using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Builder.Desktop;

public partial class MainWindow
{
    // Drafts/tokens are presentation state. Core alone generates and accepts the new object.
    private sealed record PrimitiveChoice(PrimitiveKind Kind,string Label);
    private bool primitiveActive,primitiveReady,primitiveCancelRequested,primitiveDraftChanged;
    private Guid? ownedPrimitivePreview;
    private long primitiveBaseRevision,primitiveDraftVersion;
    private PrimitiveRequest? reviewedPrimitive;
    private void InitializePrimitives()
    {
        PrimitiveChoices.ItemsSource=new[]{
            new PrimitiveChoice(PrimitiveKind.Cube,"Cube"),new PrimitiveChoice(PrimitiveKind.Cylinder,"Cylinder"),
            new PrimitiveChoice(PrimitiveKind.Pyramid,"Pyramid"),new PrimitiveChoice(PrimitiveKind.Cone,"Cone"),
            new PrimitiveChoice(PrimitiveKind.Sphere,"Sphere"),new PrimitiveChoice(PrimitiveKind.Hexagon,"Hexagon"),
            new PrimitiveChoice(PrimitiveKind.Wedge,"Wedge"),new PrimitiveChoice(PrimitiveKind.Torus,"Torus"),
            new PrimitiveChoice(PrimitiveKind.Tetrahedron,"Tetrahedron")};
        primitiveReady=true;
    }
    private void AddShapeClick(object sender,RoutedEventArgs e)
    {if(sender is FrameworkElement {Tag:string kind} && Enum.TryParse<PrimitiveKind>(kind,out var shape)) BeginPrimitive(shape);}
    private void BeginPrimitive(PrimitiveKind kind)
    {
        if(!primitiveReady || primitiveActive || !ArrangementSelectionAvailable) return;
        if(editor.Snapshot.EditingGroupId!=null) {Message("Choose Done before adding a shape.");return;}
        CancelPendingPick();
        primitiveActive=true;primitiveBaseRevision=editor.Snapshot.Revision;primitiveDraftVersion=0;
        ownedPrimitivePreview=null;reviewedPrimitive=null;primitiveCancelRequested=false;primitiveDraftChanged=true;
        var wasRefreshing=refreshing;refreshing=true;
        try
        {
            PrimitiveChoices.SelectedItem=PrimitiveChoices.Items.Cast<PrimitiveChoice>().Single(c=>c.Kind==kind);
            PrimitiveSizeX.Text=PrimitiveSizeY.Text=PrimitiveSizeZ.Text="20";
            PrimitiveMajorRadius.Text="15";PrimitiveTubeRadius.Text="5";
            PrimitiveSegments.Text="48";PrimitiveLatitude.Text=PrimitiveTubeSegments.Text="24";
        }
        finally{refreshing=wasRefreshing;}
        Refresh();PrimitiveChoices.Focus();
        Message("Choose a shape and dimensions, then Preview. Add accepts one undoable object; Escape cancels.");
    }
    private PrimitiveKind PrimitiveShape=>((PrimitiveChoice)PrimitiveChoices.SelectedItem).Kind;
    private void PrimitiveShapeChanged(object sender,SelectionChangedEventArgs e)=>PrimitiveSettingsChanged(sender,e);
    private void PrimitiveSettingsChanged(object sender,RoutedEventArgs e)
    {
        if(!primitiveReady || refreshing || !primitiveActive) return;
        primitiveDraftVersion++;primitiveDraftChanged=true;
        RefreshPrimitives();
    }
    private PrimitiveRequest? ReadPrimitive()
    {
        bool Scalar(TextBox field,string label,out double value)
        {
            if(double.TryParse(field.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out value)&&double.IsFinite(value)&&value>0)return true;
            Message($"Enter a positive finite {label} in millimeters.",true);return false;
        }
        bool Count(TextBox field,string label,int max,int multiple,out int value)
        {
            if(int.TryParse(field.Text,NumberStyles.Integer,CultureInfo.CurrentCulture,out value)&&value>=4&&value<=max&&value%multiple==0)return true;
            Message($"{label} must be 4–{max}, in steps of {multiple}.",true);return false;
        }
        var kind=PrimitiveShape;
        if(kind==PrimitiveKind.Torus)
        {
            if(!Scalar(PrimitiveMajorRadius,"major radius",out var major)||!Scalar(PrimitiveTubeRadius,"tube radius",out var tube))return null;
            if(major<=tube){Message("Major radius must be larger than tube radius.",true);return null;}
            if(!Count(PrimitiveSegments,"Radial segments",256,4,out var radial)||!Count(PrimitiveTubeSegments,"Tube segments",256,4,out var ring))return null;
            return new TorusPrimitiveRequest(major,tube,radial,ring);
        }
        if(!Scalar(PrimitiveSizeX,"X size",out var x)||!Scalar(PrimitiveSizeY,"Y size",out var y)||!Scalar(PrimitiveSizeZ,"Z size",out var z))return null;
        var size=new Vec3(x,y,z);
        if(kind is PrimitiveKind.Cylinder or PrimitiveKind.Cone or PrimitiveKind.Sphere)
        {
            if(!Count(PrimitiveSegments,"Radial segments",256,4,out var radial))return null;
            if(kind==PrimitiveKind.Sphere)
            {
                if(!Count(PrimitiveLatitude,"Latitude segments",128,2,out var latitude))return null;
                return new SpherePrimitiveRequest(size,radial,latitude);
            }
            return new RadialPrimitiveRequest(kind,size,radial);
        }
        return new PolyhedronPrimitiveRequest(kind,size);
    }
    private async void PreviewPrimitiveClick(object sender,RoutedEventArgs e)
    {
        if(!primitiveActive || operation!=null || primitiveCancelRequested)return;
        primitiveDraftChanged=true;
        if(ReadPrimitive() is not {} request){RefreshPrimitives();return;}
        var draft=primitiveDraftVersion;
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshPrimitives();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        try
        {
            var result=await editor.PreviewPrimitiveAsync(request,ownedPrimitivePreview,cancellation.Token,progress,primitiveBaseRevision);
            if(result.Succeeded&&editor.Snapshot.Preview is {Request:PrimitiveRequest} preview)
            {ownedPrimitivePreview=preview.Id;reviewedPrimitive=request;primitiveDraftChanged=draft!=primitiveDraftVersion;}
            Message(result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(primitiveCancelRequested)FinishCancelPrimitive();
            else{Refresh();if(editor.Snapshot.Preview?.Id==ownedPrimitivePreview)FitScene();}
        }
    }
    private void AddPrimitiveClick(object sender,RoutedEventArgs e)
    {
        if(!primitiveActive || operation!=null || primitiveDraftChanged || reviewedPrimitive==null ||
            editor.Snapshot.Preview is not {Request:PrimitiveRequest} preview || preview.Id!=ownedPrimitivePreview)return;
        // Re-read the visible draft as well as checking its version: acceptance cannot use stale fields.
        if(ReadPrimitive()!=reviewedPrimitive){primitiveDraftChanged=true;RefreshPrimitives();return;}
        var result=editor.ApplyPreview(preview.Id,primitiveBaseRevision);
        if(result.Succeeded){ClearPrimitive();Refresh();FitScene();ViewportSurface.Focus();}
        else RefreshPrimitives();
        Message(result.Message,!result.Succeeded);
    }
    private void CancelPrimitiveClick(object sender,RoutedEventArgs e)=>CancelPrimitive();
    private void CancelPrimitive()
    {
        if(!primitiveActive)return;
        if(operation!=null){primitiveCancelRequested=true;operation.Cancel();RefreshPrimitives();Message("Cancelling shape preview…");}
        else FinishCancelPrimitive();
    }
    private void FinishCancelPrimitive()
    {
        if(editor.Snapshot.Preview is {Request:PrimitiveRequest} preview&&preview.Id==ownedPrimitivePreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){primitiveCancelRequested=false;RefreshPrimitives();Message(result.Message,true);return;}
        }
        ClearPrimitive();Refresh();ViewportSurface.Focus();Message("Shape creation cancelled. Your scene is unchanged.");
    }
    private void ClearPrimitive()
    {primitiveActive=primitiveCancelRequested=primitiveDraftChanged=false;ownedPrimitivePreview=null;reviewedPrimitive=null;}
    private void RefreshPrimitives()
    {
        RefreshWorkspaceNavigation();
        if(!primitiveReady)return;
        PrimitivePanel.Visibility=PrimitiveActions.Visibility=primitiveActive?Visibility.Visible:Visibility.Collapsed;
        if(!primitiveActive){ObjectsOverview.ClearValue(VisibilityProperty);return;}
        var kind=PrimitiveShape;var curved=kind is PrimitiveKind.Cylinder or PrimitiveKind.Cone or PrimitiveKind.Sphere or PrimitiveKind.Torus;
        PrimitiveDimensionFields.Visibility=kind==PrimitiveKind.Torus?Visibility.Collapsed:Visibility.Visible;
        PrimitiveTorusFields.Visibility=kind==PrimitiveKind.Torus?Visibility.Visible:Visibility.Collapsed;
        PrimitiveResolutionFields.Visibility=curved?Visibility.Visible:Visibility.Collapsed;
        PrimitiveLatitudeFields.Visibility=kind==PrimitiveKind.Sphere?Visibility.Visible:Visibility.Collapsed;
        PrimitiveTubeFields.Visibility=kind==PrimitiveKind.Torus?Visibility.Visible:Visibility.Collapsed;
        PrimitiveDefinition.Text=kind switch{
            PrimitiveKind.Pyramid=>"Rectangular base in XY, centered apex at +Z.",PrimitiveKind.Hexagon=>"Six-sided prism along Z. X and Y set its bounds.",
            PrimitiveKind.Wedge=>"Ramp rises from Z = 0 at X = 0 to full height at +X.",PrimitiveKind.Tetrahedron=>"Four triangular faces, scaled to the requested X/Y/Z bounds.",
            PrimitiveKind.Torus=>"Major radius is from the center to the tube centerline. Tube radius is the circular cross-section radius.",
            PrimitiveKind.Sphere=>"X, Y and Z set the ellipsoid bounds. Latitude runs from pole to pole.",
            PrimitiveKind.Cone=>"Elliptical base in XY, centered apex at +Z.",PrimitiveKind.Cylinder=>"Elliptical cylinder along Z. X and Y are diameters.",_=>"Rectangular solid with the requested X/Y/Z bounds."};
        var candidate=editor.Snapshot.Preview is {Request:PrimitiveRequest} p&&p.Id==ownedPrimitivePreview?p:null;
        PrimitiveSummary.Text=candidate==null?"Preview before adding. The accepted scene stays unchanged.":candidate.Summary+
            (primitiveDraftChanged?"\nSettings changed. Preview again before Add.":"\nAdd accepts this preview as one undoable object.");
        PreviewPrimitiveButton.IsEnabled=operation==null&&!primitiveCancelRequested;
        AddPrimitiveButton.IsEnabled=operation==null&&!primitiveCancelRequested&&!primitiveDraftChanged&&candidate!=null;
        CancelPrimitiveButton.IsEnabled=!primitiveCancelRequested;PrimitivePanel.IsEnabled=operation==null&&!primitiveCancelRequested;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;
        ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;PreviewLabel.Text="New shape preview · not yet added";
        EmptyHint.Visibility=Visibility.Collapsed;
    }
}
