using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

public partial class MainWindow
{
    // These are UI drafts only. Core owns the actual job, candidate and acceptance rules.
    private enum ConstructionMode { Split, Boolean, Repair }
    private ConstructionMode? constructionMode;
    private Guid? constructionEditingGroupId;
    private ObjectDiagnosis? repairDiagnosis;
    private sealed record OperandChoice(Guid Id,string Label);
    private static readonly Color PositiveColor = Color.FromRgb(38,132,197), NegativeColor = Color.FromRgb(218,139,55);
    private void SplitClick(object sender,RoutedEventArgs e) => BeginConstruction(ConstructionMode.Split);
    private void CombineClick(object sender,RoutedEventArgs e) => BeginConstruction(ConstructionMode.Boolean);
    private async void RepairClick(object sender,RoutedEventArgs e)
    { BeginConstruction(ConstructionMode.Repair); if(constructionMode == ConstructionMode.Repair) await Diagnose(); }
    private async void DiagnoseClick(object sender,RoutedEventArgs e) => await Diagnose();
    private async Task Diagnose()
    {
        if(constructionMode != ConstructionMode.Repair || OperandA.SelectedValue is not Guid id || operation != null) return;
        await RunOperation(async (ct,progress) => {
            var result=await editor.DiagnoseAsync(id,ct,progress,editor.Snapshot.Revision);
            repairDiagnosis=result.Diagnosis; return result.Result;
        });
    }
    private void OriginalChanged(object sender,RoutedEventArgs e)
    { if(!refreshing && constructionMode == ConstructionMode.Repair) { RefreshConstruction(); RenderScene(); } }
    private void BeginConstruction(ConstructionMode mode)
    {
        if(ModalToolActive || operation != null || constructionMode != null || handleMode != null || arrangementMode != null) return;
        var snapshot = editor.Snapshot;
        var grouped=snapshot.Scene.Groups.SelectMany(g=>g.ObjectIds).ToHashSet();
        var eligible=ScopeObjects(snapshot).Where(o=>snapshot.EditingGroupId!=null || !grouped.Contains(o.Id)).ToArray();
        if(mode==ConstructionMode.Boolean && EditingGroup is {ObjectIds.Length:2})
        { Message("A group must keep at least two members. Duplicate a member, or choose Done and Ungroup before combining.",true); return; }
        if(eligible.Length < (mode == ConstructionMode.Boolean ? 2 : 1) || (mode!=ConstructionMode.Boolean && !SingleUngroupedSelection)) return;
        constructionMode = mode; constructionEditingGroupId=snapshot.EditingGroupId; refreshing = true;
        repairDiagnosis=null; ShowOriginal.IsChecked=false;
        // Ordinals distinguish identically named copies without exposing GUIDs in the UI.
        var choices = eligible.Select((o,i) => new OperandChoice(o.Id,$"{i+1}. {o.Name}")).ToArray();
        OperandA.ItemsSource = OperandB.ItemsSource = choices;
        var primary=choices.Any(o=>o.Id==snapshot.SelectedObjectId) ? snapshot.SelectedObjectId!.Value : choices[0].Id;
        OperandA.SelectedValue = primary;
        OperandB.SelectedValue = choices.FirstOrDefault(o=>o.Id!=primary && snapshot.SelectedObjectIds.Contains(o.Id))?.Id
            ?? choices.FirstOrDefault(o => o.Id != primary)?.Id;
        refreshing = false;
        CenterPlane(); Refresh(); Message("Choose the operation settings, then preview the result.");
    }
    private void RefreshConstruction()
    {
        RefreshWorkspaceNavigation();
        var snapshot = editor.Snapshot; var active = constructionMode != null; var p = snapshot.Preview;
        Toolbar.IsEnabled = !active && operation == null;
        ObjectList.IsEnabled = !active;
        SplitButton.IsEnabled = SingleUngroupedSelection;
        CombineButton.IsEnabled = ScopeObjects(snapshot).Count(o=>snapshot.EditingGroupId!=null || !snapshot.Scene.Groups.Any(g=>g.ObjectIds.Contains(o.Id))) >= 2 &&
            (EditingGroup is not {} group || group.ObjectIds.Length>=3);
        CombineButton.ToolTip=EditingGroup is {ObjectIds.Length:2} ? "A group must keep at least two members. Add a member with Duplicate, or choose Done and Ungroup before combining." :
            "Union, subtract or intersect two objects. The result uses operand A’s material.";
        RepairButton.IsEnabled = SingleUngroupedSelection;
        ConstructionTools.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        ConstructionPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ConstructionActions.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        EditPanel.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        SplitFields.Visibility = constructionMode == ConstructionMode.Split ? Visibility.Visible : Visibility.Collapsed;
        BooleanFields.Visibility = constructionMode == ConstructionMode.Boolean ? Visibility.Visible : Visibility.Collapsed;
        RepairFields.Visibility = constructionMode == ConstructionMode.Repair ? Visibility.Visible : Visibility.Collapsed;
        ConstructionTitle.Text = constructionMode switch { ConstructionMode.Split => "Split with a plane", ConstructionMode.Repair => "Check and repair", _ => "Combine objects" };
        OperandALabel.Text = constructionMode switch { ConstructionMode.Split => "Object to split", ConstructionMode.Repair => "Object to check", _ => "Object A · keep for subtraction" };
        DiagnoseButton.IsEnabled = p == null && operation == null;
        ShowOriginal.Visibility = p?.Repair != null ? Visibility.Visible : Visibility.Collapsed;
        if(constructionMode == ConstructionMode.Repair)
        {
            var assessment=p?.Repair?.Before ?? (repairDiagnosis is {} diagnosis && OperandA.SelectedValue is Guid id && diagnosis.ObjectId == id ? diagnosis.Assessment : null);
            RepairReport.Text=assessment == null ? "Check this object to see its mesh report." : FormatAssessment(assessment);
            if(p?.Repair is {} repair)
                RepairReport.Text += $"\n\nAfter repair: {repair.After.Triangles} faces, {repair.After.BoundaryEdges} open edges.\nSize: {SizeText(repair.Before.Bounds)} → {SizeText(repair.After.Bounds)} mm";
        }
        ApplyPreviewButton.IsEnabled = p != null && operation == null;
        PreviewButton.IsEnabled = p == null && operation == null;
        ConstructionSummary.Text = p?.Summary ?? "Preview the result before applying it.";
        PreviewBanner.Visibility = p != null ? Visibility.Visible : Visibility.Collapsed;
        PreviewLabel.Text = p?.Repair != null && ShowOriginal.IsChecked == true ? "Original · repair not yet applied" : p?.ResultObjectIds.Length == 0 ? "Preview · no solid remains" : "Preview · not yet applied";
        if(active) EmptyHint.Visibility = Visibility.Collapsed;
    }
    private static string SizeText(Bounds3 bounds)=>$"{Number(bounds.Size.X)} × {Number(bounds.Size.Y)} × {Number(bounds.Size.Z)}";
    private static string FormatAssessment(MeshAssessment a)=>
        $"{a.Triangles} faces · {a.Components} connected shells\n" +
        $"Open edges: {a.BoundaryEdges}\nDuplicate / zero-area faces: {a.DuplicateFaces} / {a.ZeroAreaFaces}\n" +
        $"Winding conflicts / inward shells: {a.WindingConflicts} / {a.InwardShells}\n" +
        $"Non-manifold edges / vertices: {a.NonManifoldEdges} / {a.NonManifoldVertices}\n" +
        $"Overlapping shell bounds: {a.OverlappingShellBounds}";
    private void CenterPlaneClick(object sender,RoutedEventArgs e) => CenterPlane();
    private void PlaneAxisChanged(object sender,RoutedEventArgs e)
    { if(!refreshing && constructionMode == ConstructionMode.Split) CenterPlane(); }
    private void CenterPlane()
    {
        if(constructionMode != ConstructionMode.Split || OperandA.SelectedValue is not Guid id) return;
        var center = editor.Snapshot.Scene.Objects.First(o => o.Id == id).Bounds.Center;
        PlaneOffset.Text = Number(PlaneAxis.SelectedIndex == 0 ? center.X : PlaneAxis.SelectedIndex == 1 ? center.Y : center.Z);
        ConstructionChanged(this,new RoutedEventArgs());
    }
    private void ConstructionChanged(object sender,RoutedEventArgs e)
    {
        if(refreshing || constructionMode == null || operation != null) return;
        if(editor.Snapshot.Preview is {} p) editor.DiscardPreview(p.Id);
        refreshing=true; ShowOriginal.IsChecked=false; refreshing=false;
        RefreshConstruction(); RenderScene();
        Message("Settings changed. Preview to review the new result.");
    }
    private Vec3 PlaneNormal => PlaneAxis.SelectedIndex == 0 ? new(1,0,0) : PlaneAxis.SelectedIndex == 1 ? new(0,1,0) : new(0,0,1);
    private async void PreviewClick(object sender,RoutedEventArgs e)
    {
        if(constructionMode == null || OperandA.SelectedValue is not Guid a || operation != null) return;
        GeometryRequest request;
        if(constructionMode == ConstructionMode.Split)
        {
            if(!double.TryParse(PlaneOffset.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var offset) || !double.IsFinite(offset))
            { Message("Enter a finite plane position in millimeters.",true); return; }
            request = new SplitRequest(a,PlaneNormal,offset,(SplitKeep)KeepSide.SelectedIndex);
        }
        else if(constructionMode == ConstructionMode.Repair) request = new RepairRequest(a,FillHoles.IsChecked == true);
        else
        {
            if(OperandB.SelectedValue is not Guid b) return;
            request = new BooleanRequest(a,b,(BooleanKind)BooleanOperation.SelectedIndex);
        }
        var captured=editor.Snapshot;
        request=request with {MemberGroupId=constructionEditingGroupId};
        await RunOperation((ct,progress) => editor.PreviewAsync(request,ct,progress,captured.Revision));
    }
    private void ApplyPreviewClick(object sender,RoutedEventArgs e) => EndConstruction(true);
    private void CancelConstructionClick(object sender,RoutedEventArgs e) => EndConstruction(false);
    private void EndConstruction(bool apply)
    {
        if(operation != null) return;
        if(editor.Snapshot.Preview is {} p)
        {
            var result = apply ? editor.ApplyPreview(p.Id,p.BaseRevision) : editor.DiscardPreview(p.Id);
            Message(result.Message,result.Outcome is not (Outcome.Success or Outcome.Cancelled));
            if(apply && !result.Succeeded) return;
        }
        else if(apply) return;
        else Message("Tool closed. Your scene is unchanged.");
        constructionMode = null; constructionEditingGroupId=null; Refresh(); ViewportSurface.Focus();
    }
    private Color ConstructionColor(SceneObject obj,EditorSnapshot snapshot,IReadOnlySet<Guid> selectedIds)
    {
        if(snapshot.Preview is {} p && p.ResultObjectIds.Contains(obj.Id))
        {
            if(p.Request is SplitRequest s)
            {
                var c = obj.Bounds.Center; // Desktop split normals are positive unit axes.
                return c.X*s.Normal.X+c.Y*s.Normal.Y+c.Z*s.Normal.Z < s.OffsetMillimeters ? NegativeColor : PositiveColor;
            }
            return PositiveColor;
        }
        if(constructionMode != null && snapshot.Preview == null)
        {
            if(OperandA.SelectedValue is Guid a && obj.Id == a) return PositiveColor;
            if(constructionMode == ConstructionMode.Boolean && OperandB.SelectedValue is Guid b && obj.Id == b) return NegativeColor;
        }
        return constructionMode == null && selectedIds.Contains(obj.Id) ? PositiveColor : Color.FromRgb(153,170,185);
    }
    private void AddCutPlane(Model3DGroup group)
    {
        if(constructionMode != ConstructionMode.Split || OperandA.SelectedValue is not Guid id ||
            !double.TryParse(PlaneOffset.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var offset) ||
            !double.IsFinite(offset) || Math.Abs(offset) > 10_000_000) return;
        var bounds = editor.Snapshot.Scene.Objects.First(o => o.Id == id).Bounds;
        var size = bounds.Size; var radius = Math.Max(2,Math.Max(size.X,Math.Max(size.Y,size.Z))*0.7);
        var center = bounds.Center;
        var normal = PlaneNormal;
        center = PlaneAxis.SelectedIndex == 0 ? center with {X=offset} : PlaneAxis.SelectedIndex == 1 ? center with {Y=offset} : center with {Z=offset};
        var u = PlaneAxis.SelectedIndex == 0 ? new Vec3(0,1,0) : new Vec3(1,0,0);
        var v = Vec3.Cross(normal,u);
        var mesh = new MeshGeometry3D();
        foreach(var point in new[] {center-u*radius-v*radius,center+u*radius-v*radius,center+u*radius+v*radius,center-u*radius+v*radius})
            mesh.Positions.Add(new(point.X,point.Y,point.Z));
        foreach(var index in new[] {0,1,2,0,2,3}) mesh.TriangleIndices.Add(index);
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(65,40,145,170)));
        group.Children.Add(new GeometryModel3D(mesh,material) {BackMaterial=material});
    }
}
