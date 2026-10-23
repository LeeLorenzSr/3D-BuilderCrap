using Builder.Core;
using System.Globalization;
using System.Windows;

namespace Builder.Desktop;

public partial class MainWindow
{
    private bool smoothActive,smoothDraftChanged,smoothCancelRequested;
    private long smoothBaseRevision,smoothDraftVersion;
    private Guid? smoothTarget,smoothScope,ownedSmoothPreview;
    private SmoothRequest? reviewedSmooth;

    private void SmoothClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable||!SingleUngroupedSelection)return;
        var snapshot=editor.Snapshot;CancelPendingPick();smoothActive=true;smoothBaseRevision=snapshot.Revision;
        smoothTarget=snapshot.SelectedObjectId;smoothScope=snapshot.EditingGroupId;ownedSmoothPreview=null;reviewedSmooth=null;
        smoothDraftVersion=0;smoothDraftChanged=true;smoothCancelRequested=false;
        var wasRefreshing=refreshing;refreshing=true;
        try{SmoothStrength.Text="50";SmoothDeviation.Text=Number(.2);SmoothPreserveSharp.IsChecked=true;SmoothShowOriginal.IsChecked=false;}
        finally{refreshing=wasRefreshing;}
        Refresh();SmoothStrength.Focus();SmoothStrength.SelectAll();Message("Set smoothing strength and maximum surface deviation, then Preview. Apply accepts; Escape cancels.");
    }
    private void SmoothSettingsChanged(object sender,RoutedEventArgs e)
    {if(refreshing||!smoothActive)return;smoothDraftVersion++;smoothDraftChanged=true;RefreshSmooth();}
    private SmoothRequest? ReadSmooth()
    {
        if(smoothTarget is not {} target)return null;
        if(!double.TryParse(SmoothStrength.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var strength)||!double.IsFinite(strength)||strength<=0||strength>100)
        {Message("Enter a finite smoothing strength greater than 0 and no more than 100 percent.",true);return null;}
        if(!double.TryParse(SmoothDeviation.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var deviation)||!double.IsFinite(deviation)||deviation<=0||deviation>1000)
        {Message("Enter a finite maximum deviation greater than 0 and no more than 1,000 mm.",true);return null;}
        return new(target,strength,deviation,SmoothPreserveSharp.IsChecked==true){MemberGroupId=smoothScope};
    }
    private async void PreviewSmoothClick(object sender,RoutedEventArgs e)
    {
        if(!smoothActive||operation!=null||smoothCancelRequested)return;
        smoothDraftChanged=true;if(ReadSmooth() is not {} request){RefreshSmooth();return;}
        var version=smoothDraftVersion;
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshSmooth();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        try
        {
            var result=await editor.PreviewSmoothAsync(request,ownedSmoothPreview,cancellation.Token,progress,smoothBaseRevision);
            if(result.Succeeded&&editor.Snapshot.Preview is {Request:SmoothRequest} preview)
            {
                ownedSmoothPreview=preview.Id;reviewedSmooth=request;smoothDraftChanged=version!=smoothDraftVersion;
                var wasRefreshing=refreshing;refreshing=true;SmoothShowOriginal.IsChecked=false;refreshing=wasRefreshing;
            }
            Message(result.Succeeded?"Smooth preview ready. Compare original and result before applying.":result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(smoothCancelRequested)FinishCancelSmooth();else Refresh();
        }
    }
    private void ApplySmoothClick(object sender,RoutedEventArgs e)
    {
        if(!smoothActive||operation!=null||smoothDraftChanged||reviewedSmooth==null||editor.Snapshot.Preview is not {Request:SmoothRequest} preview||preview.Id!=ownedSmoothPreview)return;
        if(ReadSmooth()!=reviewedSmooth){smoothDraftChanged=true;RefreshSmooth();return;}
        var result=editor.ApplyPreview(preview.Id,smoothBaseRevision);
        if(result.Succeeded){ClearSmooth();Refresh();ViewportSurface.Focus();}else RefreshSmooth();Message(result.Message,!result.Succeeded);
    }
    private void CancelSmoothClick(object sender,RoutedEventArgs e)=>CancelSmooth();
    private void CancelSmooth()
    {
        if(!smoothActive)return;
        if(operation!=null){smoothCancelRequested=true;operation.Cancel();RefreshSmooth();Message("Cancelling smooth preview…");}
        else FinishCancelSmooth();
    }
    private void FinishCancelSmooth()
    {
        if(editor.Snapshot.Preview is {Request:SmoothRequest} preview&&preview.Id==ownedSmoothPreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){smoothCancelRequested=false;RefreshSmooth();Message(result.Message,true);return;}
        }
        ClearSmooth();Refresh();ViewportSurface.Focus();Message("Smooth cancelled. Your scene is unchanged.");
    }
    private void ClearSmooth()
    {smoothActive=smoothDraftChanged=smoothCancelRequested=false;smoothTarget=smoothScope=ownedSmoothPreview=null;reviewedSmooth=null;SmoothShowOriginal.IsChecked=false;}
    private void SmoothOriginalChanged(object sender,RoutedEventArgs e)
    {if(!refreshing&&smoothActive){RefreshSmooth();RenderScene();}}
    private void RefreshSmoothReport(GeometryPreview? candidate,SceneObject? target)
    {
        var comparison=candidate?.Smooth;
        var volumeChange=comparison is {OriginalVolumeCubicMillimeters:>0}?100*(comparison.ResultVolumeCubicMillimeters-comparison.OriginalVolumeCubicMillimeters)/comparison.OriginalVolumeCubicMillimeters:0;
        SmoothReport.Text=comparison==null?$"Original: {target?.Mesh.Triangles.Length.ToString("N0",CultureInfo.CurrentCulture)??"—"} triangles":
            $"Triangles: {comparison.TriangleCount:N0} (unchanged)\n"+
            $"Maximum vertex movement: ≈ {comparison.MaximumVertexDisplacementMillimeters.ToString("G6",CultureInfo.CurrentCulture)} mm\n"+
            $"Surface deviation bound: ≈ {comparison.CertifiedDeviationUpperBoundMillimeters.ToString("G6",CultureInfo.CurrentCulture)} mm\n"+
            $"Limit: {Number(comparison.MaximumDeviationMillimeters)} mm · Cavities: {comparison.CavityCount}\n"+
            $"Volume change: {volumeChange.ToString("+0.##;-0.##;0",CultureInfo.CurrentCulture)}%";
        SmoothLimitNotice.Visibility=comparison is {ClippedVertexCount:>0}&&!smoothDraftChanged?Visibility.Visible:Visibility.Collapsed;
        SmoothSummary.Visibility=SmoothLimitNotice.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
    }
    private void RefreshSmooth()
    {
        RefreshWorkspaceNavigation();
        if(SmoothPanel==null)return;
        SmoothPanel.Visibility=SmoothActions.Visibility=smoothActive?Visibility.Visible:Visibility.Collapsed;
        SmoothButton.IsEnabled=ArrangementSelectionAvailable&&SingleUngroupedSelection;
        if(!smoothActive)return;
        var snapshot=editor.Snapshot;var candidate=snapshot.Preview is {Request:SmoothRequest} p&&p.Id==ownedSmoothPreview?p:null;
        var target=snapshot.Scene.Objects.FirstOrDefault(o=>o.Id==smoothTarget);SmoothTarget.Text=target?.Name??"Selected object";
        // The detailed report is populated from Core's certified comparison, not viewport estimates.
        RefreshSmoothReport(candidate,target);
        SmoothSummary.Text=candidate==null?"Preview the result before applying it.":smoothDraftChanged?"Settings changed. Preview again before Apply.":"Apply accepts one undoable edit.";
        SmoothPanel.IsEnabled=operation==null&&!smoothCancelRequested;
        SmoothShowOriginal.IsEnabled=candidate!=null&&operation==null&&!smoothCancelRequested;
        PreviewSmoothButton.IsEnabled=operation==null&&!smoothCancelRequested;
        ApplySmoothButton.IsEnabled=candidate!=null&&!smoothDraftChanged&&operation==null&&!smoothCancelRequested&&snapshot.Revision==smoothBaseRevision;
        CancelSmoothButton.IsEnabled=!smoothCancelRequested;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=InspectionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;
        PreviewLabel.Text=SmoothShowOriginal.IsChecked==true?"Original · smooth not yet applied":"Smooth result preview · not yet applied";EmptyHint.Visibility=Visibility.Collapsed;
    }
}
