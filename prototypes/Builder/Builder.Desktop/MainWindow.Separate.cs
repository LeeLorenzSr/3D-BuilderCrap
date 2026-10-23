using Builder.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Builder.Desktop;

public partial class MainWindow
{
    private bool separateActive,separateCancelRequested;
    private long separateBaseRevision;
    private Guid? separateTarget,separateScope,ownedSeparatePreview,separateChoicesPreview,separateInspectId;
    private readonly Dictionary<Guid,int> separatePieceIndices=[];
    private sealed record SeparatePieceChoice(Guid? ObjectId,string Name,string Detail,Brush Swatch);
    private static readonly Color[] SeparateColors=[Color.FromRgb(44,128,174),Color.FromRgb(205,132,48),Color.FromRgb(49,139,121),Color.FromRgb(163,99,150),Color.FromRgb(103,118,177),Color.FromRgb(185,100,80),Color.FromRgb(101,143,76),Color.FromRgb(180,151,48)];
    private static Brush SeparateSwatch(int index)
    {var brush=new SolidColorBrush(SeparateColors[index%SeparateColors.Length]);brush.Freeze();return brush;}
    private GeometryPreview? CurrentSeparatePreview(EditorSnapshot snapshot)
        =>separateActive&&snapshot.Preview is {Request:SeparateRequest,Separate:not null} p&&p.Id==ownedSeparatePreview?p:null;

    private void SeparateClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable||!SingleUngroupedSelection)return;
        var snapshot=editor.Snapshot;CancelPendingPick();separateActive=true;separateBaseRevision=snapshot.Revision;
        separateTarget=snapshot.SelectedObjectId;separateScope=snapshot.EditingGroupId;ownedSeparatePreview=null;separateCancelRequested=false;
        separateChoicesPreview=separateInspectId=null;separatePieceIndices.Clear();SeparatePieceList.ItemsSource=null;
        var wasRefreshing=refreshing;refreshing=true;SeparateShowOriginal.IsChecked=false;refreshing=wasRefreshing;
        Refresh();PreviewSeparateButton.Focus();Message("Preview disconnected pieces. Closed cavities stay with their surrounding piece.");
    }
    private async void PreviewSeparateClick(object sender,RoutedEventArgs e)
    {
        if(!separateActive||operation!=null||separateCancelRequested||separateTarget is not {} target)return;
        using var cancellation=new CancellationTokenSource();operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshSeparate();
        var progress=new Progress<OperationProgress>(p=>{if(ReferenceEquals(operation,cancellation))Message(p.Stage+"…");});
        try
        {
            var result=await editor.PreviewSeparateAsync(new(target){MemberGroupId=separateScope},ownedSeparatePreview,cancellation.Token,progress,separateBaseRevision);
            if(result.Succeeded&&editor.Snapshot.Preview is {Request:SeparateRequest,Separate:not null} preview)
            {
                ownedSeparatePreview=preview.Id;
                var wasRefreshing=refreshing;refreshing=true;SeparateShowOriginal.IsChecked=false;refreshing=wasRefreshing;
            }
            Message(result.Succeeded?"Separate preview ready. Inspect pieces, then Apply to keep them all.":result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(separateCancelRequested)FinishCancelSeparate();else Refresh();
        }
    }
    private void ApplySeparateClick(object sender,RoutedEventArgs e)
    {
        var snapshot=editor.Snapshot;
        if(!separateActive||operation!=null||separateCancelRequested||CurrentSeparatePreview(snapshot) is not {} preview||snapshot.Revision!=separateBaseRevision||snapshot.EditingGroupId!=separateScope)return;
        var result=editor.ApplyPreview(preview.Id,separateBaseRevision);
        if(result.Succeeded){ClearSeparate();Refresh();ViewportSurface.Focus();}else RefreshSeparate();Message(result.Message,!result.Succeeded);
    }
    private void CancelSeparateClick(object sender,RoutedEventArgs e)=>CancelSeparate();
    private void CancelSeparate()
    {
        if(!separateActive)return;
        if(operation!=null){separateCancelRequested=true;operation.Cancel();RefreshSeparate();Message("Cancelling separation…");}
        else FinishCancelSeparate();
    }
    private void FinishCancelSeparate()
    {
        if(CurrentSeparatePreview(editor.Snapshot) is {} preview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome!=Outcome.Cancelled){separateCancelRequested=false;RefreshSeparate();Message(result.Message,true);return;}
        }
        ClearSeparate();Refresh();ViewportSurface.Focus();Message("Separate cancelled. Your scene is unchanged.");
    }
    private void ClearSeparate()
    {
        separateActive=separateCancelRequested=false;separateTarget=separateScope=ownedSeparatePreview=separateChoicesPreview=separateInspectId=null;
        separatePieceIndices.Clear();SeparatePieceList.ItemsSource=null;SeparateShowOriginal.IsChecked=false;
    }
    private void SeparateViewChanged(object sender,RoutedEventArgs e)
    {
        if(refreshing||!separateActive||operation!=null)return;
        separateInspectId=(SeparatePieceList.SelectedItem as SeparatePieceChoice)?.ObjectId;
        RefreshSeparateViewLabel();RenderScene();
    }
    private void RefreshSeparateViewLabel()
    {
        var preview=CurrentSeparatePreview(editor.Snapshot);if(preview==null){SeparateInspectionLabel.Text="";return;}
        var all=separateInspectId==null;
        SeparateInspectionLabel.Text=SeparateShowOriginal.IsChecked==true?"Showing the original object.":all?"Showing all pieces. Colors are preview-only.":"Showing only this piece; other scene objects stay visible.";
        PreviewLabel.Text=SeparateShowOriginal.IsChecked==true?"Original · separation not yet applied":all?"Separate preview · pieces remain in place":"One piece shown · inspection only · Apply keeps all pieces";
    }
    private void RefreshSeparate()
    {
        RefreshWorkspaceNavigation();
        if(SeparatePanel==null)return;
        SeparatePanel.Visibility=SeparateActions.Visibility=separateActive?Visibility.Visible:Visibility.Collapsed;
        SeparateButton.IsEnabled=ArrangementSelectionAvailable&&SingleUngroupedSelection;
        if(!separateActive)return;
        var snapshot=editor.Snapshot;var candidate=CurrentSeparatePreview(snapshot);var target=snapshot.Scene.Objects.FirstOrDefault(o=>o.Id==separateTarget);
        SeparateTarget.Text=target?.Name??"Selected object";
        var comparison=candidate?.Separate;
        if(candidate!=null&&separateChoicesPreview!=candidate.Id)
        {
            // Core owns piece identity and membership. This list owns only display isolation/colors.
            separateChoicesPreview=candidate.Id;separateInspectId=null;separatePieceIndices.Clear();
            var choices=new List<SeparatePieceChoice>{new(null,"All pieces",$"{comparison!.Pieces.Length:N0} editable objects after Apply",Brushes.Transparent)};
            var objects=candidate.Scene.Objects.ToDictionary(o=>o.Id);
            for(var i=0;i<comparison.Pieces.Length;i++)
            {
                var piece=comparison.Pieces[i];separatePieceIndices.Add(piece.ObjectId,i);
                var obj=objects[piece.ObjectId];
                choices.Add(new(piece.ObjectId,obj.Name,$"{piece.TriangleCount:N0} triangles · {piece.CavityCount:N0} cavit{(piece.CavityCount==1?"y":"ies")}",SeparateSwatch(i)));
            }
            var wasRefreshing=refreshing;refreshing=true;SeparatePieceList.ItemsSource=choices;SeparatePieceList.SelectedIndex=0;refreshing=wasRefreshing;
        }
        SeparateReport.Text=comparison==null?$"Original: {target?.Mesh.Triangles.Length.ToString("N0")??"—"} triangles":$"{comparison.Pieces.Length:N0} material pieces · {comparison.Pieces.Sum(p=>p.CavityCount):N0} cavities\n{comparison.OriginalTriangleCount:N0} triangles retained";
        SeparatePieceList.Visibility=comparison==null?Visibility.Collapsed:Visibility.Visible;
        SeparateSummary.Text=comparison==null?"Preview the result before applying it.":$"Apply accepts all {comparison.Pieces.Length:N0} pieces.";
        SeparatePanel.IsEnabled=operation==null&&!separateCancelRequested;
        SeparateShowOriginal.IsEnabled=candidate!=null&&operation==null&&!separateCancelRequested;
        PreviewSeparateButton.IsEnabled=operation==null&&!separateCancelRequested;
        ApplySeparateButton.IsEnabled=candidate!=null&&operation==null&&!separateCancelRequested&&snapshot.Revision==separateBaseRevision&&snapshot.EditingGroupId==separateScope;
        CancelSeparateButton.IsEnabled=!separateCancelRequested;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=InspectionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;EmptyHint.Visibility=Visibility.Collapsed;RefreshSeparateViewLabel();
    }
    private bool SeparateDisplayVisible(SceneObject obj,EditorSnapshot snapshot)
        =>CurrentSeparatePreview(snapshot)==null||SeparateShowOriginal.IsChecked==true||separateInspectId==null||!separatePieceIndices.ContainsKey(obj.Id)||obj.Id==separateInspectId;
    private Color? SeparateDisplayColor(SceneObject obj,EditorSnapshot snapshot)
        =>CurrentSeparatePreview(snapshot)!=null&&SeparateShowOriginal.IsChecked!=true&&separatePieceIndices.TryGetValue(obj.Id,out var index)?SeparateColors[index%SeparateColors.Length]:null;
}
