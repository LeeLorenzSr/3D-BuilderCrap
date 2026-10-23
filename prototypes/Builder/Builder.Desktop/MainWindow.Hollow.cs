using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Builder.Desktop;

public partial class MainWindow
{
    private bool hollowActive,hollowDraftChanged,hollowCancelRequested;
    private long hollowBaseRevision,hollowDraftVersion;
    private Guid? hollowTarget,hollowScope,ownedHollowPreview;
    private HollowRequest? reviewedHollow;
    private Mesh? hollowDisplaySource;
    private Affine hollowDisplayTransform;
    private DisplayCutPlane? hollowDisplayPlane;
    private Guid? hollowDisplayPreview;
    private MeshGeometry3D? hollowDisplayGeometry;
    private DispatcherOperation? hollowViewUpdate;

    private void HollowClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable||!SingleUngroupedSelection)return;
        var snapshot=editor.Snapshot;CancelPendingPick();hollowActive=true;hollowBaseRevision=snapshot.Revision;
        hollowTarget=snapshot.SelectedObjectId;hollowScope=snapshot.EditingGroupId;ownedHollowPreview=null;reviewedHollow=null;
        hollowDraftVersion=0;hollowDraftChanged=true;hollowCancelRequested=false;ClearHollowDisplay();
        var wasRefreshing=refreshing;refreshing=true;
        try{HollowThickness.Text="2";HollowCutaway.IsChecked=true;HollowCutDirection.SelectedIndex=0;HollowCutPosition.Value=50;}
        finally{refreshing=wasRefreshing;}
        Refresh();HollowThickness.Focus();HollowThickness.SelectAll();Message("Set the requested minimum wall thickness, then Preview. Apply accepts a sealed shell; Escape cancels.");
    }
    private void HollowSettingsChanged(object sender,RoutedEventArgs e)
    {if(refreshing||!hollowActive)return;hollowDraftVersion++;hollowDraftChanged=true;RefreshHollow();}
    private HollowRequest? ReadHollow()
    {
        if(hollowTarget is not {} target)return null;
        if(!double.TryParse(HollowThickness.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var thickness)||!double.IsFinite(thickness)||thickness<.05||thickness>1000)
        {Message("Enter a finite wall thickness from 0.05 to 1,000 mm.",true);return null;}
        return new(target,thickness){MemberGroupId=hollowScope};
    }
    private async void PreviewHollowClick(object sender,RoutedEventArgs e)
    {
        if(!hollowActive||operation!=null||hollowCancelRequested)return;
        hollowDraftChanged=true;if(ReadHollow() is not {} request){RefreshHollow();return;}
        var version=hollowDraftVersion;
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshHollow();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        try
        {
            var result=await editor.PreviewHollowAsync(request,ownedHollowPreview,cancellation.Token,progress,hollowBaseRevision);
            if(result.Succeeded&&editor.Snapshot.Preview is {Request:HollowRequest} preview)
            {ownedHollowPreview=preview.Id;reviewedHollow=request;hollowDraftChanged=version!=hollowDraftVersion;ClearHollowDisplay();}
            Message(result.Succeeded?"Hollow preview ready. Cutaway is inspection only; Apply keeps the complete sealed shell.":result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(hollowCancelRequested)FinishCancelHollow();else{Refresh();if(editor.Snapshot.Preview?.Id==ownedHollowPreview)FitScene();}
        }
    }
    private void ApplyHollowClick(object sender,RoutedEventArgs e)
    {
        if(!hollowActive||operation!=null||hollowDraftChanged||reviewedHollow==null||editor.Snapshot.Preview is not {Request:HollowRequest} preview||preview.Id!=ownedHollowPreview)return;
        if(ReadHollow()!=reviewedHollow){hollowDraftChanged=true;RefreshHollow();return;}
        var result=editor.ApplyPreview(preview.Id,hollowBaseRevision);
        if(result.Succeeded){ClearHollow();Refresh();ViewportSurface.Focus();}else RefreshHollow();Message(result.Message,!result.Succeeded);
    }
    private void CancelHollowClick(object sender,RoutedEventArgs e)=>CancelHollow();
    private void CancelHollow()
    {
        if(!hollowActive)return;
        if(operation!=null){hollowCancelRequested=true;operation.Cancel();RefreshHollow();Message("Cancelling hollow preview…");}
        else FinishCancelHollow();
    }
    private void FinishCancelHollow()
    {
        if(editor.Snapshot.Preview is {Request:HollowRequest} preview&&preview.Id==ownedHollowPreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){hollowCancelRequested=false;RefreshHollow();Message(result.Message,true);return;}
        }
        ClearHollow();Refresh();ViewportSurface.Focus();Message("Hollow cancelled. Your scene is unchanged.");
    }
    private void ClearHollow()
    {hollowActive=hollowDraftChanged=hollowCancelRequested=false;hollowTarget=hollowScope=ownedHollowPreview=null;reviewedHollow=null;ClearHollowDisplay();}
    private void ClearHollowDisplay()
    {
        hollowViewUpdate?.Abort();hollowViewUpdate=null;hollowDisplaySource=null;hollowDisplayPlane=null;hollowDisplayPreview=null;hollowDisplayGeometry=null;
    }
    private DisplayCutPlane HollowPlane(SceneObject target)
    {
        var choice=Math.Clamp(HollowCutDirection.SelectedIndex,0,5);var axis=choice/2;var bounds=target.Bounds;
        var minimum=axis switch{0=>bounds.Min.X,1=>bounds.Min.Y,_=>bounds.Min.Z};var size=axis switch{0=>bounds.Size.X,1=>bounds.Size.Y,_=>bounds.Size.Z};
        return new(axis,minimum+size*HollowCutPosition.Value/100,choice%2!=0);
    }
    private void HollowInspectionChanged(object sender,RoutedEventArgs e)
    {
        if(refreshing||!hollowActive)return;
        RefreshHollow();
        // View controls read current values once per dispatcher turn. They never replace a Core
        // candidate, dirty the thickness draft or replay a captured old plane after tool closure.
        hollowViewUpdate??=Dispatcher.BeginInvoke(new Action(()=>{hollowViewUpdate=null;if(hollowActive)RenderScene();}),DispatcherPriority.Background);
    }
    private MeshGeometry3D? HollowDisplayMesh(SceneObject target,EditorSnapshot snapshot)
    {
        if(!hollowActive||HollowCutaway.IsChecked!=true||target.Id!=hollowTarget||snapshot.Preview is not {Request:HollowRequest} preview||preview.Id!=ownedHollowPreview)return null;
        var plane=HollowPlane(target);
        if(hollowDisplayGeometry!=null&&ReferenceEquals(hollowDisplaySource,target.Mesh)&&hollowDisplayTransform==target.Transform&&hollowDisplayPlane==plane&&hollowDisplayPreview==preview.Id)return hollowDisplayGeometry;
        hollowDisplayGeometry=DisplayCutaway.Build(target.Mesh,target.Transform,plane);hollowDisplaySource=target.Mesh;
        hollowDisplayTransform=target.Transform;hollowDisplayPlane=plane;hollowDisplayPreview=preview.Id;return hollowDisplayGeometry;
    }
    private void RefreshHollow()
    {
        RefreshWorkspaceNavigation();
        if(HollowPanel==null)return;
        HollowPanel.Visibility=HollowActions.Visibility=hollowActive?Visibility.Visible:Visibility.Collapsed;
        HollowButton.IsEnabled=ArrangementSelectionAvailable&&SingleUngroupedSelection;
        if(!hollowActive)return;
        var snapshot=editor.Snapshot;var candidate=snapshot.Preview is {Request:HollowRequest} p&&p.Id==ownedHollowPreview?p:null;
        var target=snapshot.Scene.Objects.FirstOrDefault(o=>o.Id==hollowTarget);HollowTarget.Text=target?.Name??"Selected object";
        var comparison=candidate?.Hollow;
        var description=comparison==null?"Hollow preview ready.":$"Requested minimum wall {Number(comparison.RequestedWallThicknessMillimeters)} mm. " +
            $"Removed {comparison.RemovedVolumeCubicMillimeters.ToString("0.##",CultureInfo.CurrentCulture)} mm³ in {comparison.CavityCount} sealed cavit{(comparison.CavityCount==1?"y":"ies")}. Exterior unchanged.";
        HollowSummary.Text=candidate==null?"Preview the result before applying it.":description+(hollowDraftChanged?"\nThickness changed. Preview again before Apply.":"\nApply accepts one undoable, sealed shell.");
        HollowPanel.IsEnabled=operation==null&&!hollowCancelRequested;HollowInspection.IsEnabled=candidate!=null;
        HollowCutFields.IsEnabled=HollowCutaway.IsChecked==true;
        PreviewHollowButton.IsEnabled=operation==null&&!hollowCancelRequested;
        ApplyHollowButton.IsEnabled=candidate!=null&&!hollowDraftChanged&&operation==null&&!hollowCancelRequested&&snapshot.Revision==hollowBaseRevision;
        CancelHollowButton.IsEnabled=!hollowCancelRequested;
        if(candidate?.Scene.Objects.FirstOrDefault(o=>o.Id==hollowTarget) is {} shown)
        {var plane=HollowPlane(shown);HollowSectionLabel.Text=$"Inspection plane: {"XYZ"[plane.Axis]} = {Number(plane.PositionMillimeters)} mm";}
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=InspectionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;
        PreviewLabel.Text=HollowCutaway.IsChecked==true?"Cutaway inspection only · result remains sealed":"Hollow preview · sealed shell not yet applied";EmptyHint.Visibility=Visibility.Collapsed;
    }
}
