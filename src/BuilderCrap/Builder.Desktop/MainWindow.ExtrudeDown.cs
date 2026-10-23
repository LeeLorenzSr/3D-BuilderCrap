using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

public partial class MainWindow
{
    private bool extrudeDownActive,extrudeDownDraftChanged,extrudeDownCancelRequested;
    private long extrudeDownBaseRevision,extrudeDownDraftVersion;
    private Guid? extrudeDownTarget,extrudeDownScope,ownedExtrudeDownPreview;
    private ExtrudeDownRequest? reviewedExtrudeDown;

    private void ExtrudeDownClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable||!SingleUngroupedSelection)return;
        var snapshot=editor.Snapshot;CancelPendingPick();extrudeDownActive=true;extrudeDownBaseRevision=snapshot.Revision;
        extrudeDownTarget=snapshot.SelectedObjectId;extrudeDownScope=snapshot.EditingGroupId;ownedExtrudeDownPreview=null;reviewedExtrudeDown=null;
        extrudeDownDraftVersion=0;extrudeDownDraftChanged=true;extrudeDownCancelRequested=false;
        var wasRefreshing=refreshing;refreshing=true;
        try{ExtrudeDownPlaneHeight.Text="0";ExtrudeDownShowOriginal.IsChecked=false;}
        finally{refreshing=wasRefreshing;}
        Refresh();ExtrudeDownPlaneHeight.Focus();ExtrudeDownPlaneHeight.SelectAll();Message("Set the target plane height, then Preview. Apply accepts; Escape cancels.");
    }
    private void ExtrudeDownSettingsChanged(object sender,RoutedEventArgs e)
    {if(refreshing||!extrudeDownActive)return;extrudeDownDraftVersion++;extrudeDownDraftChanged=true;RefreshExtrudeDown();RenderScene();}
    private void ExtrudeDownBuildPlateClick(object sender,RoutedEventArgs e)
    {if(extrudeDownActive&&operation==null)ExtrudeDownPlaneHeight.Text="0";}
    private ExtrudeDownRequest? ReadExtrudeDown()
    {
        if(extrudeDownTarget is not {} target)return null;
        if(!double.TryParse(ExtrudeDownPlaneHeight.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var plane)||!double.IsFinite(plane)||Math.Abs(plane)>10_000_000)
        {Message("Enter a finite target plane height within ±10,000,000 mm.",true);return null;}
        return new(target,plane){MemberGroupId=extrudeDownScope};
    }
    private async void PreviewExtrudeDownClick(object sender,RoutedEventArgs e)
    {
        if(!extrudeDownActive||operation!=null||extrudeDownCancelRequested)return;
        extrudeDownDraftChanged=true;if(ReadExtrudeDown() is not {} request){RefreshExtrudeDown();return;}
        var version=extrudeDownDraftVersion;
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshExtrudeDown();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        try
        {
            var result=await editor.PreviewExtrudeDownAsync(request,ownedExtrudeDownPreview,cancellation.Token,progress,extrudeDownBaseRevision);
            if(result.Succeeded&&editor.Snapshot.Preview is {Request:ExtrudeDownRequest} preview)
            {
                ownedExtrudeDownPreview=preview.Id;reviewedExtrudeDown=request;extrudeDownDraftChanged=version!=extrudeDownDraftVersion;
                var wasRefreshing=refreshing;refreshing=true;ExtrudeDownShowOriginal.IsChecked=false;refreshing=wasRefreshing;
            }
            Message(result.Succeeded?"Extrude down preview ready. Compare original and result before applying.":result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(extrudeDownCancelRequested)FinishCancelExtrudeDown();else Refresh();
        }
    }
    private void ApplyExtrudeDownClick(object sender,RoutedEventArgs e)
    {
        if(!extrudeDownActive||operation!=null||extrudeDownDraftChanged||reviewedExtrudeDown==null||editor.Snapshot.Preview is not {Request:ExtrudeDownRequest} preview||preview.Id!=ownedExtrudeDownPreview)return;
        if(ReadExtrudeDown()!=reviewedExtrudeDown){extrudeDownDraftChanged=true;RefreshExtrudeDown();return;}
        var result=editor.ApplyPreview(preview.Id,extrudeDownBaseRevision);
        if(result.Succeeded){ClearExtrudeDown();Refresh();ViewportSurface.Focus();}else RefreshExtrudeDown();Message(result.Message,!result.Succeeded);
    }
    private void CancelExtrudeDownClick(object sender,RoutedEventArgs e)=>CancelExtrudeDown();
    private void CancelExtrudeDown()
    {
        if(!extrudeDownActive)return;
        if(operation!=null){extrudeDownCancelRequested=true;operation.Cancel();RefreshExtrudeDown();Message("Cancelling extrusion…");}
        else FinishCancelExtrudeDown();
    }
    private void FinishCancelExtrudeDown()
    {
        if(editor.Snapshot.Preview is {Request:ExtrudeDownRequest} preview&&preview.Id==ownedExtrudeDownPreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){extrudeDownCancelRequested=false;RefreshExtrudeDown();Message(result.Message,true);return;}
        }
        ClearExtrudeDown();Refresh();ViewportSurface.Focus();Message("Extrude down cancelled. Your scene is unchanged.");
    }
    private void ClearExtrudeDown()
    {extrudeDownActive=extrudeDownDraftChanged=extrudeDownCancelRequested=false;extrudeDownTarget=extrudeDownScope=ownedExtrudeDownPreview=null;reviewedExtrudeDown=null;ExtrudeDownShowOriginal.IsChecked=false;}
    private void ExtrudeDownOriginalChanged(object sender,RoutedEventArgs e)
    {if(!refreshing&&extrudeDownActive){RefreshExtrudeDown();RenderScene();}}
    private void RefreshExtrudeDownReport(GeometryPreview? candidate,SceneObject? target)
    {
        var comparison=candidate?.ExtrudeDown;
        ExtrudeDownReport.Text=comparison==null?$"Original: {target?.Mesh.Triangles.Length.ToString("N0",CultureInfo.CurrentCulture)??"—"} triangles":
            $"Triangles: {comparison.OriginalTriangleCount:N0} → {comparison.ResultTriangleCount:N0}\n"+
            $"Plane Z: {Number(comparison.PlaneHeightMillimeters)} mm\n"+
            $"Added material: {comparison.AddedVolumeCubicMillimeters.ToString("0.###",CultureInfo.CurrentCulture)} mm³";
    }
    private void AddExtrudeDownPlane(Model3DGroup group)
    {
        if(!extrudeDownActive||extrudeDownTarget is not {} id||!double.TryParse(ExtrudeDownPlaneHeight.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var plane)||!double.IsFinite(plane)||Math.Abs(plane)>10_000_000)return;
        var target=editor.Snapshot.Scene.Objects.FirstOrDefault(o=>o.Id==id);if(target==null)return;
        var bounds=target.Bounds;var margin=Math.Max(2,Math.Max(bounds.Size.X,bounds.Size.Y)*.12);
        var mesh=new MeshGeometry3D();
        foreach(var point in new Point3D[]{new(bounds.Min.X-margin,bounds.Min.Y-margin,plane),new(bounds.Max.X+margin,bounds.Min.Y-margin,plane),new(bounds.Max.X+margin,bounds.Max.Y+margin,plane),new(bounds.Min.X-margin,bounds.Max.Y+margin,plane)})mesh.Positions.Add(point);
        foreach(var index in new[]{0,1,2,0,2,3})mesh.TriangleIndices.Add(index);mesh.Freeze();
        var material=new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(65,42,154,82)));material.Freeze();
        group.Children.Add(new GeometryModel3D(mesh,material){BackMaterial=material});
    }
    private void RefreshExtrudeDown()
    {
        RefreshWorkspaceNavigation();
        if(ExtrudeDownPanel==null)return;
        ExtrudeDownPanel.Visibility=ExtrudeDownActions.Visibility=extrudeDownActive?Visibility.Visible:Visibility.Collapsed;
        ExtrudeDownButton.IsEnabled=ArrangementSelectionAvailable&&SingleUngroupedSelection;
        if(!extrudeDownActive)return;
        var snapshot=editor.Snapshot;var candidate=snapshot.Preview is {Request:ExtrudeDownRequest} p&&p.Id==ownedExtrudeDownPreview?p:null;
        var target=snapshot.Scene.Objects.FirstOrDefault(o=>o.Id==extrudeDownTarget);ExtrudeDownTarget.Text=target?.Name??"Selected object";
        // The detailed report is populated from Core's comparison, not viewport estimates.
        RefreshExtrudeDownReport(candidate,target);
        ExtrudeDownSummary.Text=candidate==null?"Preview the result before applying it.":extrudeDownDraftChanged?"Settings changed. Preview again before Apply.":"Apply keeps the reviewed extension.";
        ExtrudeDownPanel.IsEnabled=operation==null&&!extrudeDownCancelRequested;
        ExtrudeDownShowOriginal.IsEnabled=candidate!=null&&operation==null&&!extrudeDownCancelRequested;
        PreviewExtrudeDownButton.IsEnabled=operation==null&&!extrudeDownCancelRequested;
        ApplyExtrudeDownButton.IsEnabled=candidate!=null&&!extrudeDownDraftChanged&&operation==null&&!extrudeDownCancelRequested&&snapshot.Revision==extrudeDownBaseRevision;
        CancelExtrudeDownButton.IsEnabled=!extrudeDownCancelRequested;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=InspectionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;
        PreviewLabel.Text=ExtrudeDownShowOriginal.IsChecked==true?"Original · extrusion not yet applied":"Extrude down preview · not yet applied";EmptyHint.Visibility=Visibility.Collapsed;
    }
}
