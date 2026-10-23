using Builder.Core;
using System.Collections.Immutable;
using System.Globalization;
using System.Windows;

namespace Builder.Desktop;

public partial class MainWindow
{
    // These fields own UI drafts and a preview token only. Core owns groups, selection and acceptance.
    private enum ArrangementMode { Group, Ungroup, Align, Distribute, Array, CenterOnPlate, RadialArray }
    private ArrangementMode? arrangementMode;
    private ImmutableArray<Guid> arrangementTargets = [];
    private Guid? arrangementPrimary, ownedArrangementPreview, arrangementEditingGroupId;
    private long arrangementBaseRevision, arrangementDraftVersion;
    private bool arrangementsReady, arrangementDraftChanged, cancelArrangementRequested;

    private void InitializeArrangement() => arrangementsReady = true;
    private void GroupClick(object sender,RoutedEventArgs e) => BeginArrangement(ArrangementMode.Group);
    private void UngroupClick(object sender,RoutedEventArgs e) => BeginArrangement(ArrangementMode.Ungroup);
    private void AlignClick(object sender,RoutedEventArgs e) => BeginArrangement(ArrangementMode.Align);
    private void DistributeClick(object sender,RoutedEventArgs e) => BeginArrangement(ArrangementMode.Distribute);
    private void ArrayClick(object sender,RoutedEventArgs e) => BeginArrangement(ArrangementMode.Array);
    private void RadialArrayClick(object sender,RoutedEventArgs e) => BeginArrangement(ArrangementMode.RadialArray);
    private void CenterOnPlateClick(object sender,RoutedEventArgs e) => BeginArrangement(ArrangementMode.CenterOnPlate);

    private bool ArrangementSelectionAvailable => arrangementsReady && arrangementMode == null &&
        !ModalToolActive && constructionMode == null && handleMode == null && operation == null && editor.Snapshot.Preview == null;

    private void SelectAllClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable) return;
        var snapshot=editor.Snapshot;
        var result=editor.SelectMany(ScopeObjects(snapshot).Select(o=>o.Id),snapshot.SelectedObjectId,snapshot.Revision);
        if(result.Succeeded) Refresh();
        Message(result.Message,!result.Succeeded);
    }

    private void ClearSelectionClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable) return;
        var result=editor.SelectMany([],expectedRevision:editor.Snapshot.Revision);
        if(result.Succeeded) Refresh();
        Message(result.Message,!result.Succeeded);
    }

    private void BeginArrangement(ArrangementMode mode)
    {
        if(!ArrangementSelectionAvailable) return;
        var snapshot=editor.Snapshot;
        if(snapshot.EditingGroupId!=null && (mode==ArrangementMode.Group || mode==ArrangementMode.Ungroup))
        { Message("Choose Done before grouping or ungrouping objects."); return; }
        var units=snapshot.GetSelectionUnits().ToArray();
        var groupCount=units.Count(u=>snapshot.Scene.Groups.Any(g=>g.Id==u.Id));
        var minimum=mode switch{ArrangementMode.Distribute=>3,ArrangementMode.Array or ArrangementMode.CenterOnPlate or ArrangementMode.RadialArray=>1,ArrangementMode.Ungroup=>0,_=>2};
        if(units.Length<minimum)
        { Message($"Select at least {minimum} objects, groups or editable members first."); return; }
        if(mode == ArrangementMode.Ungroup && groupCount == 0)
        { Message("Select a group to ungroup first."); return; }

        arrangementMode=mode;
        arrangementTargets=snapshot.SelectedObjectIds;
        arrangementPrimary=snapshot.SelectedObjectId;
        arrangementBaseRevision=snapshot.Revision;
        arrangementEditingGroupId=snapshot.EditingGroupId;
        arrangementDraftVersion=0;
        ownedArrangementPreview=null;
        cancelArrangementRequested=false;
        arrangementDraftChanged=true;
        var wasRefreshing=refreshing;
        refreshing=true;
        try
        {
            GroupName.Text="Group";
            if(mode==ArrangementMode.RadialArray)
            {RadialCount.Text="4";RadialAngle.Text="90";RadialAxis.SelectedIndex=2;RadialPivotX.Text=RadialPivotY.Text=RadialPivotZ.Text="0";RadialRotateCopies.IsChecked=true;}
            if(mode==ArrangementMode.CenterOnPlate)CenterPlaceOnBed.IsChecked=false;
            if(mode==ArrangementMode.Array)
            {
                var size=Bounds3.From(units.SelectMany(u=>new[]{u.Bounds.Min,u.Bounds.Max})).Size;
                ArrayCountX.Text="2";ArrayCountY.Text=ArrayCountZ.Text="1";
                ArraySpacingX.Text=Number(size.X+5);ArraySpacingY.Text=Number(size.Y+5);ArraySpacingZ.Text=Number(size.Z+5);
            }
            DistributionAxisChoice.SelectedIndex=DistributionSpacingChoice.SelectedIndex=0;
            var reference=units.FirstOrDefault(u=>u.ObjectIds.Contains(arrangementPrimary ?? Guid.Empty));
            ((System.Windows.Controls.ComboBoxItem)AlignmentReference.Items[1]).Content=$"Keep {reference?.Name ?? "primary selection"} fixed";
            AlignmentX.SelectedIndex=2;
            AlignmentY.SelectedIndex=AlignmentZ.SelectedIndex=AlignmentReference.SelectedIndex=0;
        }
        finally { refreshing=wasRefreshing; }

        ArrangementTitle.Text=mode switch {
            ArrangementMode.Group=>"Group objects", ArrangementMode.Ungroup=>"Ungroup objects", ArrangementMode.Distribute=>"Distribute selection", ArrangementMode.Array=>"Rectangular array", ArrangementMode.RadialArray=>"Radial array", ArrangementMode.CenterOnPlate=>"Center on plate", _=>"Align selection" };
        ArrangementSelection.Text=$"{units.Length} selected unit{(units.Length==1 ? "" : "s")} · {arrangementTargets.Length} object{(arrangementTargets.Length==1 ? "" : "s")}\n"+
            string.Join("\n",units.Take(8).Select(u=>u.ObjectIds.Length>1 ? $"{u.Name} ({u.ObjectIds.Length} objects)" : u.Name))+
            (units.Length>8 ? $"\n… and {units.Length-8} more" : "");
        if(mode==ArrangementMode.RadialArray)ArrangementSelection.Text=$"{units.Length} selected units · {arrangementTargets.Length} objects";
        ArrangementReview.Text=mode switch {
            ArrangementMode.Group=>$"Create one group from {units.Length} selected units. Their meshes remain separate and their positions stay unchanged.",
            ArrangementMode.Ungroup=>$"Make members of {groupCount} selected group{(groupCount==1 ? "" : "s")} independently selectable. Their meshes and positions stay unchanged.",
            ArrangementMode.CenterOnPlate=>"Center the selection bounds on the plate origin (world X=0, Y=0). Z stays unchanged unless Place on bed is selected. The set moves together; groups keep their spacing. Press F to fit the result.",
            ArrangementMode.RadialArray=>"Count includes the original. Angle is the step around the world-axis line through the pivot. Copies may overlap. Uncheck Rotate copies to orbit the set’s center while keeping orientation and internal offsets. Apply selects new copies. Press F to fit.",
            ArrangementMode.Array=>"Counts include the original set. Spacing is the signed step between matching points, not an edge gap; copies may overlap. Apply selects new copies. Press F to fit the array.",
            ArrangementMode.Distribute=>"The outer two units in center order stay fixed. Groups move together. Center spacing may overlap; equal gaps needs enough room.",
            _=>snapshot.EditingGroupId!=null ? "Align the selected members on world axes while keeping their group. A primary-member reference stays fixed." :
                "Move each selected unit as a whole on world axes. Groups keep their member spacing. A primary-unit reference stays fixed." };
        RefreshArrangement();
        if(mode==ArrangementMode.Distribute)DistributionAxisChoice.Focus();
        else if(mode==ArrangementMode.RadialArray){RadialCount.Focus();RadialCount.SelectAll();}
        else if(mode==ArrangementMode.Array){ArrayCountX.Focus();ArrayCountX.SelectAll();}
        else if(mode==ArrangementMode.CenterOnPlate)CenterPlaceOnBed.Focus();
        Message("Review the settings, then Preview. Apply accepts; Escape cancels.");
    }

    private void ArrangementSettingsChanged(object sender,RoutedEventArgs e)
    {
        if(!arrangementsReady || refreshing || arrangementMode == null) return;
        // Preserve the previous candidate for comparison, but never accept it for changed drafts.
        arrangementDraftVersion++;
        arrangementDraftChanged=true;
        RefreshArrangement();
    }

    private void RadialFullCircleClick(object sender,RoutedEventArgs e)
    {
        if(arrangementMode!=ArrangementMode.RadialArray||operation!=null)return;
        if(!int.TryParse(RadialCount.Text,out var count)||count<1){Message("Enter a whole-number count of at least 1 first.",true);return;}
        RadialAngle.Text=(360d/count).ToString("G17",CultureInfo.CurrentCulture);
    }
    private void RadialOriginClick(object sender,RoutedEventArgs e)=>SetRadialPivot(new());
    private void RadialSelectionCenterClick(object sender,RoutedEventArgs e)
    {
        if(arrangementMode!=ArrangementMode.RadialArray||operation!=null)return;
        var objects=editor.Snapshot.Scene.Objects.Where(o=>arrangementTargets.Contains(o.Id));
        SetRadialPivot(Bounds3.From(objects.SelectMany(o=>new[]{o.Bounds.Min,o.Bounds.Max})).Center);
    }
    private void SetRadialPivot(Vec3 pivot)
    {
        if(arrangementMode!=ArrangementMode.RadialArray||operation!=null)return;
        RadialPivotX.Text=Number(pivot.X);RadialPivotY.Text=Number(pivot.Y);RadialPivotZ.Text=Number(pivot.Z);
    }

    private ArrangementRequest? ReadArrangementRequest()
    {
        if(arrangementMode == ArrangementMode.Group) return new GroupRequest(arrangementTargets,GroupName.Text) {MemberGroupId=arrangementEditingGroupId};
        if(arrangementMode == ArrangementMode.Ungroup) return new UngroupRequest(arrangementTargets) {MemberGroupId=arrangementEditingGroupId};
        if(arrangementMode==ArrangementMode.CenterOnPlate)return new CenterOnPlateRequest(arrangementTargets,CenterPlaceOnBed.IsChecked==true){MemberGroupId=arrangementEditingGroupId};
        if(arrangementMode==ArrangementMode.RadialArray)
        {
            if(!int.TryParse(RadialCount.Text,out var count)||count<1){Message("Enter a whole-number count of at least 1. Count includes the original.",true);return null;}
            if(!double.TryParse(RadialAngle.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var angle)||!double.IsFinite(angle)||Math.Abs(angle)>360||count>1&&angle==0)
            {Message("Enter an angle step from −360° to 360°, nonzero when Count is greater than 1.",true);return null;}
            if(RadialAxis.SelectedIndex is <0 or >2){Message("Choose a world axis.",true);return null;}
            if(!ReadVector(RadialPivotX,RadialPivotY,RadialPivotZ,out var pivot))return null;
            return new RadialArrayRequest(arrangementTargets,count,angle,(RadialArrayAxis)RadialAxis.SelectedIndex,pivot,RadialRotateCopies.IsChecked==true){MemberGroupId=arrangementEditingGroupId};
        }
        if(arrangementMode==ArrangementMode.Array)
        {
            if(!int.TryParse(ArrayCountX.Text,out var x)||!int.TryParse(ArrayCountY.Text,out var y)||!int.TryParse(ArrayCountZ.Text,out var z)||x<1||y<1||z<1)
            {Message("Enter a whole-number count of at least 1 for each axis. Counts include the original.",true);return null;}
            if(!ReadVector(ArraySpacingX,ArraySpacingY,ArraySpacingZ,out var spacing))return null;
            if(x>1&&spacing.X==0||y>1&&spacing.Y==0||z>1&&spacing.Z==0)
            {Message("Use nonzero spacing on each axis with a count greater than 1.",true);return null;}
            return new RectangularArrayRequest(arrangementTargets,x,y,z,spacing){MemberGroupId=arrangementEditingGroupId};
        }
        if(arrangementMode==ArrangementMode.Distribute)
        {
            if(DistributionAxisChoice.SelectedIndex is <0 or >2 || DistributionSpacingChoice.SelectedIndex is <0 or >1)
            {Message("Choose a world axis and Centers or Equal gaps.",true);return null;}
            return new DistributionRequest(arrangementTargets,(DistributionAxis)DistributionAxisChoice.SelectedIndex,
                (DistributionSpacing)DistributionSpacingChoice.SelectedIndex){MemberGroupId=arrangementEditingGroupId};
        }
        if(arrangementMode != ArrangementMode.Align) return null;
        if(AlignmentX.SelectedIndex is < 0 or > 3 || AlignmentY.SelectedIndex is < 0 or > 3 ||
            AlignmentZ.SelectedIndex is < 0 or > 3 || AlignmentReference.SelectedIndex is < 0 or > 1 ||
            AlignmentReference.SelectedIndex == 1 && arrangementPrimary == null)
        { Message("Choose an alignment and a valid reference for each axis.",true); return null; }
        return new AlignmentRequest(arrangementTargets,(AxisAlignment)AlignmentX.SelectedIndex,
            (AxisAlignment)AlignmentY.SelectedIndex,(AxisAlignment)AlignmentZ.SelectedIndex,
            AlignmentReference.SelectedIndex==1 ? arrangementPrimary : null) {MemberGroupId=arrangementEditingGroupId};
    }

    private async void PreviewArrangementClick(object sender,RoutedEventArgs e)
    {
        if(arrangementMode == null || operation != null || cancelArrangementRequested) return;
        arrangementDraftChanged=true;
        if(ReadArrangementRequest() is not {} request) { RefreshArrangement(); return; }
        var draftVersion=arrangementDraftVersion;
        using var cancellation=new CancellationTokenSource();
        operation=cancellation; RefreshWorkspaceNavigation();
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;
        RefreshArrangement();
        // This job keeps camera/presentation input available. Document and settings controls are guarded.
        var progress=new Progress<OperationProgress>(p=> {
            if(ReferenceEquals(operation,cancellation)) Message(p.Stage+"…");
        });
        try
        {
            var result=await editor.PreviewArrangementAsync(request,ownedArrangementPreview,
                cancellation.Token,progress,arrangementBaseRevision);
            if(result.Succeeded && editor.Snapshot.Preview is {Request:ArrangementRequest} preview)
            { ownedArrangementPreview=preview.Id; arrangementDraftChanged=arrangementDraftVersion!=draftVersion; }
            Message(result.Message,result.Outcome is not (Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null; RefreshWorkspaceNavigation();
            CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(cancelArrangementRequested) FinishCancelArrangement();
            else { RefreshArrangement(); RenderScene(); }
        }
    }

    private void ApplyArrangementClick(object sender,RoutedEventArgs e)
    {
        if(arrangementMode == null || operation != null || arrangementDraftChanged ||
            editor.Snapshot.Preview is not {Request:ArrangementRequest} preview || preview.Id!=ownedArrangementPreview) return;
        var result=editor.ApplyPreview(preview.Id,arrangementBaseRevision);
        if(result.Succeeded)
        {
            ClearArrangementDraft();
            Refresh();
            ViewportSurface.Focus();
        }
        else RefreshArrangement();
        Message(result.Message,!result.Succeeded);
    }

    private void CancelArrangementClick(object sender,RoutedEventArgs e) => CancelArrangement();
    private void CancelArrangement()
    {
        if(arrangementMode == null) return;
        if(operation != null)
        {
            cancelArrangementRequested=true;
            operation.Cancel();
            RefreshArrangement();
            Message("Cancelling arrangement…");
        }
        else FinishCancelArrangement();
    }

    private void FinishCancelArrangement()
    {
        if(editor.Snapshot.Preview is {Request:ArrangementRequest} preview && preview.Id==ownedArrangementPreview)
        {
            var result=editor.DiscardPreview(preview.Id);
            if(result.Outcome != Outcome.Cancelled)
            {
                cancelArrangementRequested=false;
                RefreshArrangement();
                Message(result.Message,true);
                return;
            }
        }
        ClearArrangementDraft();
        Refresh();
        ViewportSurface.Focus();
        Message("Arrangement cancelled. Your scene is unchanged.");
    }

    private void ClearArrangementDraft()
    {
        arrangementMode=null;
        arrangementTargets=[];
        arrangementPrimary=ownedArrangementPreview=arrangementEditingGroupId=null;
        cancelArrangementRequested=arrangementDraftChanged=false;
    }

    private void RefreshArrangement()
    {
        RefreshWorkspaceNavigation();
        if(!arrangementsReady) return;
        var snapshot=editor.Snapshot;
        var active=arrangementMode != null;
        UpdateSelectionPresentation();
        var unitCount=selectionUnitCount;
        var available=ArrangementSelectionAvailable;
        GroupButton.IsEnabled=available && snapshot.EditingGroupId==null && unitCount>=2;
        AlignButton.IsEnabled=available && unitCount>=2;
        DistributeButton.IsEnabled=available && unitCount>=3;
        ArrayButton.IsEnabled=available && unitCount>0;
        CenterOnPlateButton.IsEnabled=available && unitCount>0;
        RadialArrayButton.IsEnabled=available && unitCount>0;
        UngroupButton.IsEnabled=available && selectionHasGroups;
        SelectAllButton.IsEnabled=available && snapshot.Scene.Objects.Length>0;
        ClearSelectionButton.IsEnabled=available && snapshot.SelectedObjectIds.Length>0;
        ExportSelectionButton.IsEnabled=available && snapshot.SelectedObjectIds.Length>0;
        ArrangementPanel.Visibility=ArrangementActions.Visibility=active ? Visibility.Visible : Visibility.Collapsed;
        GroupFields.Visibility=arrangementMode==ArrangementMode.Group ? Visibility.Visible : Visibility.Collapsed;
        AlignmentFields.Visibility=arrangementMode==ArrangementMode.Align ? Visibility.Visible : Visibility.Collapsed;
        DistributionFields.Visibility=arrangementMode==ArrangementMode.Distribute ? Visibility.Visible : Visibility.Collapsed;
        ArrayFields.Visibility=arrangementMode==ArrangementMode.Array ? Visibility.Visible : Visibility.Collapsed;
        RadialArrayFields.Visibility=arrangementMode==ArrangementMode.RadialArray ? Visibility.Visible : Visibility.Collapsed;
        CenterOnPlateFields.Visibility=arrangementMode==ArrangementMode.CenterOnPlate ? Visibility.Visible : Visibility.Collapsed;
        ArrangementPanel.IsEnabled=operation==null;
        PreviewArrangementButton.IsEnabled=active && operation==null && !cancelArrangementRequested;
        ApplyArrangementButton.IsEnabled=active && operation==null && !arrangementDraftChanged &&
            snapshot.Preview is {Request:ArrangementRequest} candidate && candidate.Id==ownedArrangementPreview;
        CancelArrangementButton.IsEnabled=!cancelArrangementRequested;
        RefreshMemberScope();
        RefreshInspection();
        if(!active) return;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;
        EditPanel.Visibility=ConstructionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        var preview=snapshot.Preview is {Request:ArrangementRequest} p && p.Id==ownedArrangementPreview ? p : null;
        ArrangementSummary.Text=preview==null ? "Preview this arrangement before applying it. Apply accepts one undoable edit."
            : preview.Summary+(arrangementDraftChanged ? "\nSettings changed. Preview again before Apply." : "\nApply accepts; Cancel keeps the accepted document.");
        PreviewBanner.Visibility=preview==null ? Visibility.Collapsed : Visibility.Visible;
        PreviewLabel.Text=arrangementMode switch{ArrangementMode.Align=>"Alignment preview · not yet applied",ArrangementMode.Distribute=>"Distribution preview · not yet applied",ArrangementMode.Array=>"Array preview · not yet applied",ArrangementMode.RadialArray=>"Radial array preview · not yet applied",ArrangementMode.CenterOnPlate=>"Center on plate preview · not yet applied",_=>"Group structure preview · meshes stay separate"};
    }
}
