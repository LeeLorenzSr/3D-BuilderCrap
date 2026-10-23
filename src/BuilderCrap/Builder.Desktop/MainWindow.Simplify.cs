using Builder.Core;
using System.Globalization;
using System.Windows;

namespace Builder.Desktop;

public partial class MainWindow
{
    private bool simplifyActive,simplifyDraftChanged,simplifyCancelRequested;
    private long simplifyBaseRevision,simplifyDraftVersion;
    private Guid? simplifyTarget,simplifyScope,ownedSimplifyPreview;
    private SimplifyRequest? reviewedSimplify;

    private void SimplifyClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable||!SingleUngroupedSelection)return;
        var snapshot=editor.Snapshot;CancelPendingPick();simplifyActive=true;simplifyBaseRevision=snapshot.Revision;
        simplifyTarget=snapshot.SelectedObjectId;simplifyScope=snapshot.EditingGroupId;ownedSimplifyPreview=null;reviewedSimplify=null;
        simplifyDraftVersion=0;simplifyDraftChanged=true;simplifyCancelRequested=false;
        var wasRefreshing=refreshing;refreshing=true;
        try{SimplifyReduction.Text="50";SimplifyDeviation.Text=Number(.1);SimplifyShowOriginal.IsChecked=false;}
        finally{refreshing=wasRefreshing;}
        Refresh();SimplifyReduction.Focus();SimplifyReduction.SelectAll();Message("Set a reduction target and maximum surface deviation, then Preview. Apply accepts; Escape cancels.");
    }
    private void SimplifySettingsChanged(object sender,RoutedEventArgs e)
    {if(refreshing||!simplifyActive)return;simplifyDraftVersion++;simplifyDraftChanged=true;RefreshSimplify();}
    private SimplifyRequest? ReadSimplify()
    {
        if(simplifyTarget is not {} target)return null;
        if(!double.TryParse(SimplifyReduction.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var reduction)||!double.IsFinite(reduction)||reduction<=0||reduction>=100)
        {Message("Enter a finite reduction target greater than 0 and less than 100 percent.",true);return null;}
        if(!double.TryParse(SimplifyDeviation.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var deviation)||!double.IsFinite(deviation)||deviation<=0||deviation>1000)
        {Message("Enter a finite maximum deviation greater than 0 and no more than 1,000 mm.",true);return null;}
        return new(target,reduction,deviation){MemberGroupId=simplifyScope};
    }
    private async void PreviewSimplifyClick(object sender,RoutedEventArgs e)
    {
        if(!simplifyActive||operation!=null||simplifyCancelRequested)return;
        simplifyDraftChanged=true;if(ReadSimplify() is not {} request){RefreshSimplify();return;}
        var version=simplifyDraftVersion;
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshSimplify();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        try
        {
            var result=await editor.PreviewSimplifyAsync(request,ownedSimplifyPreview,cancellation.Token,progress,simplifyBaseRevision);
            if(result.Succeeded&&editor.Snapshot.Preview is {Request:SimplifyRequest} preview)
            {
                ownedSimplifyPreview=preview.Id;reviewedSimplify=request;simplifyDraftChanged=version!=simplifyDraftVersion;
                var wasRefreshing=refreshing;refreshing=true;SimplifyShowOriginal.IsChecked=false;refreshing=wasRefreshing;
            }
            Message(result.Succeeded?"Simplify preview ready. Compare original and result before applying.":result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(simplifyCancelRequested)FinishCancelSimplify();else Refresh();
        }
    }
    private void ApplySimplifyClick(object sender,RoutedEventArgs e)
    {
        if(!simplifyActive||operation!=null||simplifyDraftChanged||reviewedSimplify==null||editor.Snapshot.Preview is not {Request:SimplifyRequest} preview||preview.Id!=ownedSimplifyPreview)return;
        if(ReadSimplify()!=reviewedSimplify){simplifyDraftChanged=true;RefreshSimplify();return;}
        var result=editor.ApplyPreview(preview.Id,simplifyBaseRevision);
        if(result.Succeeded){ClearSimplify();Refresh();ViewportSurface.Focus();}else RefreshSimplify();Message(result.Message,!result.Succeeded);
    }
    private void CancelSimplifyClick(object sender,RoutedEventArgs e)=>CancelSimplify();
    private void CancelSimplify()
    {
        if(!simplifyActive)return;
        if(operation!=null){simplifyCancelRequested=true;operation.Cancel();RefreshSimplify();Message("Cancelling simplify preview…");}
        else FinishCancelSimplify();
    }
    private void FinishCancelSimplify()
    {
        if(editor.Snapshot.Preview is {Request:SimplifyRequest} preview&&preview.Id==ownedSimplifyPreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){simplifyCancelRequested=false;RefreshSimplify();Message(result.Message,true);return;}
        }
        ClearSimplify();Refresh();ViewportSurface.Focus();Message("Simplify cancelled. Your scene is unchanged.");
    }
    private void ClearSimplify()
    {simplifyActive=simplifyDraftChanged=simplifyCancelRequested=false;simplifyTarget=simplifyScope=ownedSimplifyPreview=null;reviewedSimplify=null;SimplifyShowOriginal.IsChecked=false;}
    private void SimplifyOriginalChanged(object sender,RoutedEventArgs e)
    {if(!refreshing&&simplifyActive){RefreshSimplify();RenderScene();}}
    private void RefreshSimplifyReport(GeometryPreview? candidate,SceneObject? target)
    {
        var comparison=candidate?.Simplify;
        var volumeChange=comparison is {OriginalVolumeCubicMillimeters:>0}?100*(comparison.ResultVolumeCubicMillimeters-comparison.OriginalVolumeCubicMillimeters)/comparison.OriginalVolumeCubicMillimeters:0;
        SimplifyReport.Text=comparison==null?$"Original: {target?.Mesh.Triangles.Length.ToString("N0",CultureInfo.CurrentCulture)??"—"} triangles":
            $"Triangles: {comparison.OriginalTriangleCount:N0} → {comparison.ResultTriangleCount:N0}\n"+
            $"Reduction: {comparison.AchievedReductionPercent:0.##}% (target {comparison.RequestedReductionPercent:0.##}%)\n"+
            $"Surface deviation bound: ≈ {comparison.CertifiedDeviationUpperBoundMillimeters.ToString("G6",CultureInfo.CurrentCulture)} mm\n"+
            $"Limit: {Number(comparison.MaximumDeviationMillimeters)} mm · Cavities: {comparison.CavityCount}\n"+
            $"Volume change: {volumeChange.ToString("+0.##;-0.##;0",CultureInfo.CurrentCulture)}%";
        SimplifyTargetNotice.Visibility=comparison is {TargetReached:false}&&!simplifyDraftChanged?Visibility.Visible:Visibility.Collapsed;
        SimplifySummary.Visibility=SimplifyTargetNotice.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
    }
    private void RefreshSimplify()
    {
        RefreshWorkspaceNavigation();
        if(SimplifyPanel==null)return;
        SimplifyPanel.Visibility=SimplifyActions.Visibility=simplifyActive?Visibility.Visible:Visibility.Collapsed;
        SimplifyButton.IsEnabled=ArrangementSelectionAvailable&&SingleUngroupedSelection;
        if(!simplifyActive)return;
        var snapshot=editor.Snapshot;var candidate=snapshot.Preview is {Request:SimplifyRequest} p&&p.Id==ownedSimplifyPreview?p:null;
        var target=snapshot.Scene.Objects.FirstOrDefault(o=>o.Id==simplifyTarget);SimplifyTarget.Text=target?.Name??"Selected object";
        // The detailed report is populated from Core's certified comparison, not viewport estimates.
        RefreshSimplifyReport(candidate,target);
        SimplifySummary.Text=candidate==null?"Preview the result before applying it.":simplifyDraftChanged?"Settings changed. Preview again before Apply.":"Compare original and result. Apply accepts one undoable edit.";
        SimplifyPanel.IsEnabled=operation==null&&!simplifyCancelRequested;
        SimplifyShowOriginal.IsEnabled=candidate!=null&&operation==null&&!simplifyCancelRequested;
        PreviewSimplifyButton.IsEnabled=operation==null&&!simplifyCancelRequested;
        ApplySimplifyButton.IsEnabled=candidate!=null&&!simplifyDraftChanged&&operation==null&&!simplifyCancelRequested&&snapshot.Revision==simplifyBaseRevision;
        CancelSimplifyButton.IsEnabled=!simplifyCancelRequested;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=InspectionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;
        PreviewLabel.Text=SimplifyShowOriginal.IsChecked==true?"Original · simplify not yet applied":"Simplify result preview · not yet applied";EmptyHint.Visibility=Visibility.Collapsed;
    }
}
