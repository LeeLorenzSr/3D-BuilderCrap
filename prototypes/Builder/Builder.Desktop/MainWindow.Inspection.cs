using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace Builder.Desktop;

public partial class MainWindow
{
    // Drafts/points are adapter state; Core owns reflection geometry and distance results.
    private enum InspectionMode { Mirror, Measure }
    private InspectionMode? inspectionMode;
    private bool InspectionActive=>inspectionMode!=null;
    private long inspectionGeneration,inspectionBaseRevision,mirrorDraftVersion;
    private Guid? inspectionScope,ownedMirrorPreview;
    private bool mirrorDraftChanged,mirrorCancelRequested;
    private MirrorTransform? reviewedMirror;
    private Vec3? measurementFirst,measurementSecond;
    private PointMeasurement? measurementResult;

    private void MirrorClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable || SelectedTarget is not {} selected)return;
        CancelPendingPick();inspectionMode=InspectionMode.Mirror;inspectionGeneration++;
        inspectionBaseRevision=editor.Snapshot.Revision;inspectionScope=editor.Snapshot.EditingGroupId;
        ownedMirrorPreview=null;reviewedMirror=null;mirrorDraftVersion=0;mirrorDraftChanged=true;mirrorCancelRequested=false;
        refreshing=true;MirrorAxisChoice.SelectedIndex=0;MirrorCustomPlane.IsChecked=false;MirrorPlane.Text=Number(selected.Bounds.Center.X);refreshing=false;
        Refresh();MirrorAxisChoice.Focus();Message("Choose a world axis and mirror plane, then Preview. Apply accepts; Escape cancels.");
    }
    private void MirrorSettingsChanged(object sender,RoutedEventArgs e)
    {
        if(refreshing||inspectionMode!=InspectionMode.Mirror)return;
        if(ReferenceEquals(sender,MirrorCustomPlane)&&MirrorCustomPlane.IsChecked==true&&SelectedTarget is {} selected)
        {
            refreshing=true;MirrorPlane.Text=Number(MirrorAxisChoice.SelectedIndex switch{0=>selected.Bounds.Center.X,1=>selected.Bounds.Center.Y,_=>selected.Bounds.Center.Z});refreshing=false;
        }
        mirrorDraftVersion++;mirrorDraftChanged=true;RefreshInspection();
    }
    private MirrorTransform? ReadMirror()
    {
        if(SelectedTarget is not {} selected||MirrorAxisChoice.SelectedIndex is <0 or >2)return null;
        double? plane=null;
        if(MirrorCustomPlane.IsChecked==true)
        {
            if(!double.TryParse(MirrorPlane.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var value)||!double.IsFinite(value))
            {Message("Enter a finite world-plane position in millimeters.",true);return null;}
            plane=value;
        }
        return new MirrorTransform(selected.Id,(MirrorAxis)MirrorAxisChoice.SelectedIndex,plane){ObjectIds=selected.ObjectIds,MemberGroupId=inspectionScope};
    }
    private async void PreviewMirrorClick(object sender,RoutedEventArgs e)
    {
        if(inspectionMode!=InspectionMode.Mirror||operation!=null||mirrorCancelRequested)return;
        mirrorDraftChanged=true;
        if(ReadMirror()is not {} request){RefreshInspection();return;}
        var version=mirrorDraftVersion;
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshInspection();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        try
        {
            var result=await editor.PreviewTransformAsync(request,ownedMirrorPreview,cancellation.Token,progress,inspectionBaseRevision);
            if(result.Succeeded&&editor.Snapshot.Preview is {Request:MirrorTransform} preview)
            {ownedMirrorPreview=preview.Id;reviewedMirror=request;mirrorDraftChanged=version!=mirrorDraftVersion;}
            Message(result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(mirrorCancelRequested)FinishInspection();else{Refresh();if(editor.Snapshot.Preview?.Id==ownedMirrorPreview)FitScene();}
        }
    }
    private void ApplyMirrorClick(object sender,RoutedEventArgs e)
    {
        if(inspectionMode!=InspectionMode.Mirror||operation!=null||mirrorDraftChanged||reviewedMirror==null||
            editor.Snapshot.Preview is not {Request:MirrorTransform} preview||preview.Id!=ownedMirrorPreview)return;
        if(ReadMirror()!=reviewedMirror){mirrorDraftChanged=true;RefreshInspection();return;}
        var result=editor.ApplyPreview(preview.Id,inspectionBaseRevision);
        if(result.Succeeded){ClearInspection();Refresh();ViewportSurface.Focus();}else RefreshInspection();
        Message(result.Message,!result.Succeeded);
    }
    private void CancelMirrorClick(object sender,RoutedEventArgs e)=>EndInspection();
    private void EndInspection()
    {
        if(!InspectionActive)return;
        CancelPendingPick();inspectionGeneration++;
        if(inspectionMode==InspectionMode.Mirror&&operation!=null)
        {mirrorCancelRequested=true;operation.Cancel();RefreshInspection();Message("Cancelling mirror…");return;}
        FinishInspection();
    }
    private void FinishInspection()
    {
        if(inspectionMode==InspectionMode.Mirror&&editor.Snapshot.Preview is {Request:MirrorTransform} preview&&preview.Id==ownedMirrorPreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){mirrorCancelRequested=false;RefreshInspection();Message(result.Message,true);return;}
        }
        var measure=inspectionMode==InspectionMode.Measure;ClearInspection();Refresh();ViewportSurface.Focus();
        Message(measure?"Measurement closed. Your scene is unchanged.":"Mirror cancelled. Your scene is unchanged.");
    }
    private void ClearInspection()
    {
        CancelPendingPick();inspectionGeneration++;inspectionMode=null;ownedMirrorPreview=null;reviewedMirror=null;
        measurementFirst=measurementSecond=null;measurementResult=null;mirrorCancelRequested=mirrorDraftChanged=false;
    }
    private void MeasureClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable||editor.Snapshot.Scene.Objects.IsEmpty)return;
        CancelPendingPick();inspectionMode=InspectionMode.Measure;inspectionGeneration++;
        inspectionBaseRevision=editor.Snapshot.Revision;inspectionScope=editor.Snapshot.EditingGroupId;
        measurementFirst=measurementSecond=null;measurementResult=null;
        Refresh();ViewportSurface.Focus();Message("Click two surface points to measure in millimeters. Selection stays unchanged.");
    }
    private void ResetMeasurementClick(object sender,RoutedEventArgs e)
    {
        if(inspectionMode!=InspectionMode.Measure)return;
        CancelPendingPick();inspectionGeneration++;measurementFirst=measurementSecond=null;measurementResult=null;
        inspectionBaseRevision=editor.Snapshot.Revision;inspectionScope=editor.Snapshot.EditingGroupId;
        RefreshInspection();Message("Click the first surface point.");
    }
    private void DoneMeasurementClick(object sender,RoutedEventArgs e)=>EndInspection();
    private async Task PickMeasurementAt(Point point)
    {
        if(inspectionMode!=InspectionMode.Measure||operation!=null)return;
        if(measurementSecond!=null){Message("Choose Reset to measure another pair of surface points.");return;}
        CancelPendingPick();
        using var cancellation=new CancellationTokenSource();pendingPick=cancellation;
        var generation=pickGeneration;var cameraVersion=cameraGeneration;var toolVersion=inspectionGeneration;var snapshot=editor.Snapshot;
        try
        {
            if(!TryViewportRay(point,out var origin,out var direction,out var near,out var far))return;
            var hit=await editor.PickAsync(origin,direction,near,far,cancellation.Token);
            var current=editor.Snapshot;
            if(cancellation.IsCancellationRequested||inspectionMode!=InspectionMode.Measure||generation!=pickGeneration||cameraVersion!=cameraGeneration||
                toolVersion!=inspectionGeneration||current.Revision!=snapshot.Revision||current.EditingGroupId!=snapshot.EditingGroupId||
                snapshot.Revision!=inspectionBaseRevision||snapshot.EditingGroupId!=inspectionScope)return;
            if(hit==null){Message("No surface at that point. Your measurement is unchanged.");return;}
            if(hit.CapturedRevision!=snapshot.Revision||hit.EditingGroupId!=snapshot.EditingGroupId)return;
            if(!hit.IsSelectable){Message("That surface is outside the group being edited. Your measurement is unchanged.");return;}
            if(measurementFirst is not {} first)measurementFirst=hit.Position;
            else
            {
                // Core validates units/revision/scope and computes every displayed distance/delta.
                measurementResult=editor.MeasureDistance(first,hit.Position,snapshot.Revision,snapshot.EditingGroupId,cancellation.Token);
                measurementSecond=measurementResult.SecondMillimeters;
            }
            RefreshInspection();
            Message(measurementResult is {} result?$"Distance: {Number(result.DistanceMillimeters)} mm. Reset to measure another pair.":"Point A selected. Click the second surface point.");
        }
        catch(OperationCanceledException){}
        catch(ModelException e){Message(e.Message,true);}
        finally{if(ReferenceEquals(pendingPick,cancellation))pendingPick=null;}
    }
    private void RefreshInspection()
    {
        RefreshWorkspaceNavigation();
        if(MirrorPanel==null)return;
        var available=ArrangementSelectionAvailable;
        InspectionTools.Visibility=materialsActive||extrudeDownActive||separateActive||smoothActive||simplifyActive||hollowActive||imageReliefActive||textToolMode!=null||primitiveActive||constructionMode!=null||handleMode!=null||arrangementMode!=null||InspectionActive?Visibility.Collapsed:Visibility.Visible;
        MirrorButton.IsEnabled=available&&SelectedTarget!=null;
        MeasureButton.IsEnabled=available&&!editor.Snapshot.Scene.Objects.IsEmpty;
        var mirror=inspectionMode==InspectionMode.Mirror;var measure=inspectionMode==InspectionMode.Measure;
        MirrorPanel.Visibility=MirrorActions.Visibility=mirror?Visibility.Visible:Visibility.Collapsed;
        MeasurementPanel.Visibility=MeasurementActions.Visibility=MeasurementOverlay.Visibility=measure?Visibility.Visible:Visibility.Collapsed;
        if(!InspectionActive){MeasurementOverlay.Children.Clear();return;}
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;
        ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        if(mirror)
        {
            var candidate=editor.Snapshot.Preview is {Request:MirrorTransform} p&&p.Id==ownedMirrorPreview?p:null;
            MirrorPanel.IsEnabled=operation==null&&!mirrorCancelRequested;MirrorPlane.IsEnabled=MirrorCustomPlane.IsChecked==true;
            MirrorPlaneFields.Visibility=MirrorCustomPlane.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
            PreviewMirrorButton.IsEnabled=operation==null&&!mirrorCancelRequested;
            ApplyMirrorButton.IsEnabled=candidate!=null&&!mirrorDraftChanged&&operation==null&&!mirrorCancelRequested&&editor.Snapshot.Revision==inspectionBaseRevision;
            CancelMirrorButton.IsEnabled=!mirrorCancelRequested;
            MirrorSummary.Text=candidate==null?"Preview the reflection before applying it.":candidate.Summary+
                (mirrorDraftChanged?"\nSettings changed. Preview again before Apply.":"\nApply accepts one undoable reflection.");
            PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;PreviewLabel.Text="Mirror preview · not yet applied";
        }
        else
        {
            if(editor.Snapshot.Revision!=inspectionBaseRevision||editor.Snapshot.EditingGroupId!=inspectionScope)
            {
                CancelPendingPick();inspectionGeneration++;measurementFirst=measurementSecond=null;measurementResult=null;
                inspectionBaseRevision=editor.Snapshot.Revision;inspectionScope=editor.Snapshot.EditingGroupId;
            }
            MeasurementPrompt.Text=measurementSecond!=null?"Measurement complete. Reset to choose another pair.":measurementFirst!=null?"Click the second surface point.":"Click the first surface point.";
            MeasurementFirst.Text=measurementFirst is {} a?$"A   {Coordinates(a)}":"A   Not selected";
            MeasurementSecond.Text=measurementSecond is {} b?$"B   {Coordinates(b)}":"B   Not selected";
            MeasurementDistance.Text=measurementResult is {} result?$"{Number(result.DistanceMillimeters)} mm":"— mm";
            MeasurementDelta.Text=measurementResult is {} delta?$"ΔX {Number(delta.DeltaMillimeters.X)} mm\nΔY {Number(delta.DeltaMillimeters.Y)} mm\nΔZ {Number(delta.DeltaMillimeters.Z)} mm":"ΔX —   ΔY —   ΔZ —";
            DrawMeasurement();
        }
    }
    private static string Coordinates(Vec3 point)=>$"X {Number(point.X)} · Y {Number(point.Y)} · Z {Number(point.Z)} mm";
    private void DrawMeasurement()
    {
        if(MeasurementOverlay==null)return;
        MeasurementOverlay.Children.Clear();
        if(inspectionMode!=InspectionMode.Measure)return;
        var projection=TransformGizmo.Projection.Capture(Viewport.Camera as ProjectionCamera,new(Viewport.ActualWidth,Viewport.ActualHeight));
        var first=measurementFirst is {} a?projection?.Project(a):null;var second=measurementSecond is {} b?projection?.Project(b):null;
        if(first is {} p&&second is {} q)MeasurementOverlay.Children.Add(new Line{X1=p.X,Y1=p.Y,X2=q.X,Y2=q.Y,Stroke=Brushes.DarkSlateGray,StrokeThickness=2});
        void Endpoint(Point point,string label,Brush color)
        {
            var dot=new Ellipse{Width=10,Height=10,Fill=Brushes.White,Stroke=color,StrokeThickness=3};Canvas.SetLeft(dot,point.X-5);Canvas.SetTop(dot,point.Y-5);MeasurementOverlay.Children.Add(dot);
            var text=new TextBlock{Text=label,Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:color,Background=SystemParameters.HighContrast?SystemColors.WindowBrush:Brushes.White,FontWeight=FontWeights.SemiBold,Padding=new Thickness(3,0,3,0)};
            Canvas.SetLeft(text,point.X+8);Canvas.SetTop(text,point.Y-12);MeasurementOverlay.Children.Add(text);
        }
        if(first is {} f)Endpoint(f,"A",Brushes.SteelBlue);if(second is {} s)Endpoint(s,"B",Brushes.DarkOrange);
    }
}
