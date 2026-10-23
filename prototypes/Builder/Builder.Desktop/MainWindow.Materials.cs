using Builder.Core;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace Builder.Desktop;

public partial class MainWindow
{
    private bool materialsActive,materialsDirty,materialsCancelRequested,materialClearDraft;
    private long materialsBaseRevision,materialsDraftVersion;
    private Guid? materialsScope,ownedMaterialsPreview;
    private ImmutableArray<Guid> materialsTargets=[];
    private GeometryRequest? reviewedMaterial;
    private enum PaintMode { Object, Surface, Brush }
    private PaintMode paintMode;
    private sealed record SurfaceSeed(Guid ObjectId,int TriangleIndex);
    private SurfaceSeed? materialSurfaceSeed;
    private static bool MaterialToolRequest(GeometryRequest request)=>request is MaterialRequest or SurfaceMaterialRequest or SurfaceBrushRequest;
    private sealed record ColorChoice(string Label,string Hex,Brush Brush){public string Description=>$"{Label} · {Hex}";}
    private void InitializeMaterials()
    {
        MaterialPalette.ItemsSource=new[]{("White","#F3F3F0"),("Gray","#9CA5AD"),("Black","#292D32"),("Red","#D65248"),("Orange","#E68A36"),("Yellow","#E5C44D"),("Green","#5F9B64"),("Teal","#3A9C98"),("Blue","#267CB5"),("Purple","#8B70B3"),("Pink","#CF85A4"),("Brown","#9B7253")}.Select(p=>{
            var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Item2));brush.Freeze();return new ColorChoice(p.Item1,p.Item2,brush);}).ToArray();
    }
    private string SelectionMaterialDescription()
    {
        var snapshot=editor.Snapshot;var ids=snapshot.SelectedObjectIds.ToHashSet();var selected=snapshot.Scene.Objects.Where(o=>ids.Contains(o.Id)).ToArray();
        if(selected.Any(o=>!o.TriangleMaterials.IsEmpty))return "Surface colors in the selection.";
        var values=selected.Select(o=>o.Material).Distinct().ToArray();
        return values.Length==0?"Select objects or a group.":values.Length>1?"Mixed materials in the selection.":values[0] is {} m?$"{m.Name} · {m.Color.Hex}":"No material assigned.";
    }
    private void MaterialClick(object sender,RoutedEventArgs e)=>BeginMaterialTool(PaintMode.Object);
    private void SurfaceMaterialClick(object sender,RoutedEventArgs e)=>BeginMaterialTool(PaintMode.Surface);
    private void BrushMaterialClick(object sender,RoutedEventArgs e)=>BeginMaterialTool(PaintMode.Brush);
    private void BeginMaterialTool(PaintMode mode)
    {
        if(!ArrangementSelectionAvailable||(mode==PaintMode.Object&&editor.Snapshot.SelectedObjectIds.IsEmpty)||editor.Snapshot.Scene.Objects.IsEmpty)return;
        paintMode=mode;materialSurfaceSeed=null;brushStrokes=[];brushTarget=null;CancelBrushCapture();
        var snapshot=editor.Snapshot;CancelPendingPick();materialsActive=true;materialsTargets=snapshot.SelectedObjectIds;materialsScope=snapshot.EditingGroupId;materialsBaseRevision=snapshot.Revision;
        materialsDirty=true;materialsCancelRequested=materialClearDraft=false;materialsDraftVersion=0;ownedMaterialsPreview=null;reviewedMaterial=null;
        var values=snapshot.Scene.Objects.Where(o=>materialsTargets.Contains(o.Id)).Select(o=>o.Material).Distinct().ToArray();var current=values.Length==1?values[0]:null;
        var was=refreshing;refreshing=true;MaterialHex.Text=current?.Color.Hex??"#267CB5";MaterialName.Text=current?.Name??"";MaterialShowOriginal.IsChecked=false;BrushDiameter.Text="5";BrushErase.IsChecked=false;refreshing=was;
        Refresh();
        if(mode!=PaintMode.Object)ViewportSurface.Focus();else{MaterialHex.Focus();MaterialHex.SelectAll();}
        Message(mode==PaintMode.Brush?"Choose a color and diameter, then drag on a surface. Release to preview; Apply accepts all strokes.":mode==PaintMode.Surface?"Choose a color, then click a flat surface to preview its connected region.":"Choose a display color, then Preview. Apply accepts; Escape cancels.");
    }
    private void MaterialSettingsChanged(object sender,RoutedEventArgs e)
    {if(refreshing||!materialsActive)return;CancelPendingPick();CancelBrushCapture();materialClearDraft=false;if(paintMode!=PaintMode.Brush){materialsDirty=true;materialsDraftVersion++;}RefreshMaterials();}
    private void MaterialSwatchClick(object sender,RoutedEventArgs e)
    {
        if(!materialsActive||operation!=null||sender is not Button {Tag:string hex})return;
        CancelPendingPick();MaterialHex.Text=hex;materialClearDraft=false;if(paintMode==PaintMode.Brush)BrushErase.IsChecked=false;else{materialsDirty=true;materialsDraftVersion++;}RefreshMaterials();
    }
    private void ClearMaterialClick(object sender,RoutedEventArgs e)
    {if(!materialsActive||operation!=null)return;CancelPendingPick();materialClearDraft=true;materialsDirty=true;materialsDraftVersion++;RefreshMaterials();}
    private GeometryRequest? ReadMaterial()
    {
        if(paintMode==PaintMode.Brush)return brushTarget is {} target&&!brushStrokes.IsEmpty?new SurfaceBrushRequest(target,brushStrokes){MemberGroupId=materialsScope}:null;
        if(!TryReadPaintMaterial(out var material))return null;
        if(paintMode==PaintMode.Surface)
        {
            if(materialSurfaceSeed is not {} seed){Message("Click a flat surface to choose the region first.");return null;}
            return new SurfaceMaterialRequest(seed.ObjectId,seed.TriangleIndex,material){MemberGroupId=materialsScope};
        }
        return new MaterialRequest(materialsTargets,material){MemberGroupId=materialsScope};
    }
    private bool TryReadPaintMaterial(out SurfaceMaterial? material)
    {
        material=null;
        if(!materialClearDraft)
        {
            if(!RgbColor.TryParse(MaterialHex.Text,out var rgb)){Message("Enter a color as #RRGGBB, such as #267CB5.",true);return false;}
            var name=MaterialName.Text.Trim();if(name.Length==0)name="Color "+rgb.Hex;
            if(name.Length>100||name.Any(char.IsControl)){Message("Use a material name of at most 100 characters without control characters.",true);return false;}
            material=new SurfaceMaterial(name,rgb);
        }
        return true;
    }
    private async void PreviewMaterialClick(object sender,RoutedEventArgs e)=>await PreviewMaterial();
    private async Task PreviewMaterial()
    {
        if(!materialsActive||brushGesture!=null||operation!=null||materialsCancelRequested)return;if(paintMode==PaintMode.Brush){await ReplayBrush();return;}CancelPendingPick();materialsDirty=true;
        if(ReadMaterial() is not {} request){RefreshMaterials();return;}var version=materialsDraftVersion;
        using var cancellation=new CancellationTokenSource();operation=cancellation;RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;RefreshMaterials();
        try
        {
            var result=await (request is SurfaceMaterialRequest surface
                ?editor.PreviewSurfaceMaterialAsync(surface,ownedMaterialsPreview,cancellation.Token,null,materialsBaseRevision)
                :editor.PreviewMaterialAsync((MaterialRequest)request,ownedMaterialsPreview,cancellation.Token,null,materialsBaseRevision));
            if(result.Succeeded&&editor.Snapshot.Preview is {} p&&MaterialToolRequest(p.Request)){ownedMaterialsPreview=p.Id;reviewedMaterial=request;materialsDirty=version!=materialsDraftVersion;}
            Message(result.Succeeded?"Material preview ready. Apply keeps this display material.":result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
        }
        finally
        {
            operation=null;RefreshWorkspaceNavigation();CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(materialsCancelRequested)FinishCancelMaterials();else Refresh();
        }
    }
    private void ApplyMaterialClick(object sender,RoutedEventArgs e)
    {
        if(!materialsActive||brushGesture!=null||operation!=null||materialsDirty||reviewedMaterial==null||editor.Snapshot.Preview is not {} p||!MaterialToolRequest(p.Request)||p.Id!=ownedMaterialsPreview)return;
        if(paintMode!=PaintMode.Brush&&ReadMaterial()!=reviewedMaterial){materialsDirty=true;RefreshMaterials();return;}
        var result=editor.ApplyPreview(p.Id,materialsBaseRevision);if(result.Succeeded){ClearMaterials();Refresh();ViewportSurface.Focus();}else RefreshMaterials();Message(result.Message,!result.Succeeded);
    }
    private void CancelMaterialClick(object sender,RoutedEventArgs e)=>CancelMaterials();
    private void CancelMaterials()
    {if(!materialsActive)return;CancelBrushCapture();if(operation!=null){materialsCancelRequested=true;operation.Cancel();RefreshMaterials();}else FinishCancelMaterials();}
    private void FinishCancelMaterials()
    {
        if(editor.Snapshot.Preview is {} p&&MaterialToolRequest(p.Request)&&p.Id==ownedMaterialsPreview){var result=editor.DiscardPreview(p.Id);if(result.Outcome!=Outcome.Cancelled){materialsCancelRequested=false;RefreshMaterials();Message(result.Message,true);return;}}
        ClearMaterials();Refresh();ViewportSurface.Focus();Message("Material cancelled. Your scene is unchanged.");
    }
    private void ClearMaterials()
    {CancelPendingPick();CancelBrushCapture();paintMode=PaintMode.Object;brushStrokes=[];brushTarget=null;brushLastHit=null;materialSurfaceSeed=null;materialsActive=materialsDirty=materialsCancelRequested=materialClearDraft=false;materialsTargets=[];materialsScope=ownedMaterialsPreview=null;reviewedMaterial=null;MaterialShowOriginal.IsChecked=false;}
    private void MaterialOriginalChanged(object sender,RoutedEventArgs e)
    {if(refreshing||!materialsActive)return;CancelBrushCapture();RefreshMaterials();RenderScene();}
    private void RefreshMaterials()
    {
        RefreshWorkspaceNavigation();if(MaterialPanel==null)return;
        MaterialButton.IsEnabled=ArrangementSelectionAvailable&&!editor.Snapshot.SelectedObjectIds.IsEmpty;
        BrushMaterialButton.IsEnabled=ArrangementSelectionAvailable&&!editor.Snapshot.Scene.Objects.IsEmpty;
        SurfaceMaterialButton.IsEnabled=ArrangementSelectionAvailable&&!editor.Snapshot.Scene.Objects.IsEmpty;
        MaterialSelectionSummary.Text=SelectionMaterialDescription();MaterialPanel.Visibility=MaterialActions.Visibility=materialsActive?Visibility.Visible:Visibility.Collapsed;if(!materialsActive)return;
        var snapshot=editor.Snapshot;var candidate=snapshot.Preview is {} p&&MaterialToolRequest(p.Request)&&p.Id==ownedMaterialsPreview?p:null;
        MaterialTitle.Text=paintMode switch{PaintMode.Surface=>"Surface color",PaintMode.Brush=>"Surface brush",_=>"Object material"};
        BrushFields.Visibility=paintMode==PaintMode.Brush?Visibility.Visible:Visibility.Collapsed;ClearMaterialButton.Visibility=paintMode==PaintMode.Brush?Visibility.Collapsed:Visibility.Visible;
        MaterialSurfaceHelp.Visibility=paintMode==PaintMode.Surface?Visibility.Visible:Visibility.Collapsed;
        MaterialCurrent.Text=paintMode==PaintMode.Brush
            ?candidate?.SurfaceBrush is {} brush?$"{snapshot.Scene.Objects.Single(o=>o.Id==brush.ObjectId).Name} · {brushStrokes.Length} strokes · {brush.TriangleIndices.Length} triangles":"Drag on an independent object or active group member."
            :paintMode==PaintMode.Surface
            ?candidate?.SurfaceMaterial is {} region?$"{snapshot.Scene.Objects.Single(o=>o.Id==region.ObjectId).Name} · {region.TriangleIndices.Length} triangles in this region":"Click a flat surface on an independent object or active group member."
            :$"{materialsTargets.Length} selected object{(materialsTargets.Length==1?"":"s")} · {SelectionMaterialDescription()}";
        MaterialClearNotice.Text=paintMode==PaintMode.Surface?"Preview returns this region to the object's base color.":"Preview will remove the assignment. Choose a color to assign one again.";
        var valid=RgbColor.TryParse(MaterialHex.Text,out var rgb);MaterialDraftSwatch.Background=!materialClearDraft&&valid?new SolidColorBrush(Color.FromRgb(rgb.R,rgb.G,rgb.B)):Brushes.Transparent;
        MaterialClearNotice.Visibility=materialClearDraft?Visibility.Visible:Visibility.Collapsed;
        MaterialSummary.Text=paintMode==PaintMode.Brush?(candidate==null?"Release a stroke to preview. Apply accepts all reviewed strokes.":$"{brushStrokes.Length} reviewed strokes · {candidate.SurfaceBrush?.TriangleIndices.Length??0} triangles."):candidate==null?"Preview the result before applying it.":materialsDirty?"Settings changed. Preview again before Apply.":ReviewedSurfaceMaterial() is {} material?$"{material.Name} · {material.Color.Hex}":paintMode==PaintMode.Surface?"Apply returns this region to the object base color.":"Apply removes the material assignment.";
        MaterialPanel.IsEnabled=operation==null&&brushGesture==null&&!materialsCancelRequested;MaterialShowOriginal.IsEnabled=candidate!=null&&operation==null&&brushGesture==null;
        PreviewMaterialButton.IsEnabled=brushGesture==null&&operation==null&&!materialsCancelRequested&&(paintMode==PaintMode.Brush?!brushStrokes.IsEmpty:paintMode!=PaintMode.Surface||materialSurfaceSeed!=null);ApplyMaterialButton.IsEnabled=brushGesture==null&&candidate!=null&&!materialsDirty&&operation==null&&snapshot.Revision==materialsBaseRevision;CancelMaterialButton.IsEnabled=!materialsCancelRequested;
        Toolbar.IsEnabled=ObjectList.IsEnabled=false;ObjectsOverview.Visibility=EditPanel.Visibility=ConstructionTools.Visibility=InspectionTools.Visibility=HandleTools.Visibility=Visibility.Collapsed;
        PreviewBanner.Visibility=candidate==null?Visibility.Collapsed:Visibility.Visible;PreviewLabel.Text=MaterialShowOriginal.IsChecked==true?"Original · material not yet applied":"Material preview · not yet applied";EmptyHint.Visibility=Visibility.Collapsed;
    }
    private SurfaceMaterial? ReviewedSurfaceMaterial()=>reviewedMaterial switch
    {MaterialRequest request=>request.Material,SurfaceMaterialRequest request=>request.Material,_=>null};
    private async Task PickSurfaceMaterialAt(Point point)
    {
        if(!materialsActive||paintMode!=PaintMode.Surface||operation!=null||MaterialShowOriginal.IsChecked==true)return;
        CancelPendingPick();using var cancellation=new CancellationTokenSource();pendingPick=cancellation;
        var generation=pickGeneration;var cameraVersion=cameraGeneration;var captured=editor.Snapshot;
        try
        {
            if(!TryViewportRay(point,out var origin,out var direction,out var near,out var far))return;
            var hit=await editor.PickAsync(origin,direction,near,far,cancellation.Token);var current=editor.Snapshot;
            if(cancellation.IsCancellationRequested||!materialsActive||paintMode!=PaintMode.Surface||operation!=null||generation!=pickGeneration||cameraVersion!=cameraGeneration||
                current.Revision!=captured.Revision||current.EditingGroupId!=captured.EditingGroupId||captured.Revision!=materialsBaseRevision||captured.EditingGroupId!=materialsScope)return;
            if(hit==null){Message("No surface at that point. Your current preview is unchanged.");return;}
            if(hit.CapturedRevision!=captured.Revision||hit.EditingGroupId!=captured.EditingGroupId)return;
            if(!hit.IsSelectable){Message("Choose a surface inside the group being edited.");return;}
            if(materialsScope==null&&captured.Scene.Groups.Any(g=>g.ObjectIds.Contains(hit.ObjectId))){Message("Choose Edit members before painting a surface inside a group.");return;}
            if(current.Preview is {Request:SurfaceMaterialRequest existing}&&existing.ObjectId!=hit.ObjectId){Message("Apply or cancel this region before painting a different object.");return;}
            materialSurfaceSeed=new(hit.ObjectId,hit.TriangleIndex);materialsDirty=true;materialsDraftVersion++;
            await PreviewMaterial();
        }
        catch(OperationCanceledException){}
        catch(ModelException error){Message(error.Message,true);}
        finally{if(ReferenceEquals(pendingPick,cancellation))pendingPick=null;}
    }
    private bool HasMaterialRoleColor(SceneObject obj,EditorSnapshot snapshot)
    {
        if(snapshot.Preview?.Request is MaterialRequest or SurfaceMaterialRequest or SurfaceBrushRequest)return false;
        return SeparateDisplayColor(obj,snapshot)!=null ||
            snapshot.Preview is {Request:SplitRequest or BooleanRequest} p&&p.ResultObjectIds.Contains(obj.Id) ||
            constructionMode!=null&&snapshot.Preview==null&&(OperandA.SelectedValue is Guid a&&a==obj.Id||constructionMode==ConstructionMode.Boolean&&OperandB.SelectedValue is Guid b&&b==obj.Id);
    }
    private Color ObjectDisplayColor(SceneObject obj,EditorSnapshot snapshot,IReadOnlySet<Guid> selectedIds)
    {
        if(HasMaterialRoleColor(obj,snapshot))return SeparateDisplayColor(obj,snapshot)??ConstructionColor(obj,snapshot,selectedIds);
        return editor.Presentation.Snapshot.Settings.Colors&&obj.Material is {} material?Color.FromRgb(material.Color.R,material.Color.G,material.Color.B):ConstructionColor(obj,snapshot,selectedIds);
    }
    private void DrawMaterialSelection()
    {
        MaterialSelectionOverlay.Children.Clear();var snapshot=editor.Snapshot;var ids=(snapshot.Preview?.SelectedObjectIds??snapshot.SelectedObjectIds).ToHashSet();if(ids.Count==0)ids=snapshot.SelectedObjectIds.ToHashSet();
        var projection=TransformGizmo.Projection.Capture(Viewport.Camera as ProjectionCamera,Viewport.RenderSize);if(projection==null)return;
        foreach(var obj in DisplayedScene(snapshot).Objects.Where(o=>(o.Material!=null||!o.TriangleMaterials.IsEmpty)&&ids.Contains(o.Id)&&SeparateDisplayVisible(o,snapshot)))
        {
            var b=obj.Bounds;var corners=new Vec3[8];for(var i=0;i<8;i++)corners[i]=new((i&1)==0?b.Min.X:b.Max.X,(i&2)==0?b.Min.Y:b.Max.Y,(i&4)==0?b.Min.Z:b.Max.Z);
            for(var i=0;i<8;i++)foreach(var axis in new[]{1,2,4})if((i&axis)==0&&projection.Project(corners[i]) is {} start&&projection.Project(corners[i|axis]) is {} end)
            {
                MaterialSelectionOverlay.Children.Add(new Line{X1=start.X,Y1=start.Y,X2=end.X,Y2=end.Y,Stroke=Brushes.White,StrokeThickness=3.5});
                MaterialSelectionOverlay.Children.Add(new Line{X1=start.X,Y1=start.Y,X2=end.X,Y2=end.Y,Stroke=new SolidColorBrush(Color.FromRgb(35,51,61)),StrokeThickness=1.3,StrokeDashArray=new(){4,3}});
            }
        }
    }
}
