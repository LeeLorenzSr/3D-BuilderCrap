using Builder.Core;
using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Builder.Desktop;

public partial class MainWindow
{
    // Only successfully reviewed strokes are retained. Core replays these rays against accepted geometry.
    private ImmutableArray<SurfaceBrushStroke> brushStrokes=[];
    private Guid? brushTarget;
    private BrushGesture? brushGesture;
    private Vec3? brushLastHit;
    private sealed class BrushGesture(long revision,Guid? scope,long camera,Size size,double diameter,SurfaceMaterial? material,Point start)
    {
        public long Revision {get;}=revision;
        public Guid? Scope {get;}=scope;
        public long Camera {get;}=camera;
        public Size Size {get;}=size;
        public double Diameter {get;}=diameter;
        public SurfaceMaterial? Material {get;}=material;
        public Point Last {get;set;}=start;
        public List<SurfaceBrushSample> Samples {get;}=[];
        public bool Truncated {get;set;}
    }
    private bool BrushActive=>materialsActive&&paintMode==PaintMode.Brush;
    private void BrushSettingsChanged(object sender,RoutedEventArgs e)
    {if(refreshing||!BrushActive)return;CancelBrushCapture();RefreshMaterials();}
    private bool ReadBrushDiameter(out double diameter)
    {
        if(double.TryParse(BrushDiameter.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out diameter)&&double.IsFinite(diameter)&&diameter>=.001&&diameter<=10000)return true;
        Message("Enter a brush diameter from 0.001 to 10,000 mm.",true);return false;
    }
    private void BrushSmallerClick(object sender,RoutedEventArgs e)=>ScaleBrushDiameter(.8);
    private void BrushLargerClick(object sender,RoutedEventArgs e)=>ScaleBrushDiameter(1.25);
    private void ScaleBrushDiameter(double factor)
    {if(BrushActive&&operation==null&&ReadBrushDiameter(out var size))BrushDiameter.Text=Number(Math.Clamp(size*factor,.001,10000));}
    private bool BeginBrushStroke(Point point)
    {
        if(!BrushActive||operation!=null||materialsCancelRequested||MaterialShowOriginal.IsChecked==true)return false;
        var snapshot=editor.Snapshot;
        if(snapshot.Revision!=materialsBaseRevision||snapshot.EditingGroupId!=materialsScope){Message("The document changed. Cancel this brush session before painting again.",true);return false;}
        if(brushStrokes.Length>=128||brushStrokes.Sum(s=>s.Samples.Length)>=8192){Message("Apply or cancel these strokes before adding more.");return false;}
        if(!ReadBrushDiameter(out var diameter))return false;
        SurfaceMaterial? material=null;if(BrushErase.IsChecked!=true&&!TryReadPaintMaterial(out material))return false;
        brushGesture=new(snapshot.Revision,snapshot.EditingGroupId,cameraGeneration,Viewport.RenderSize,diameter,material,point);
        AddBrushRay(brushGesture,point);RefreshWorkspaceNavigation();RefreshMaterials();DrawBrushCursor(point);
        return true;
    }
    private bool CurrentBrushGesture(BrushGesture gesture)=>BrushActive&&gesture.Revision==editor.Snapshot.Revision&&gesture.Scope==editor.Snapshot.EditingGroupId&&gesture.Camera==cameraGeneration&&gesture.Size==Viewport.RenderSize&&MaterialShowOriginal.IsChecked!=true;
    private void AddBrushRay(BrushGesture gesture,Point point)
    {
        var remaining=8192-brushStrokes.Sum(s=>s.Samples.Length);
        if(gesture.Samples.Count>=Math.Min(4096,remaining)){gesture.Truncated=true;return;}
        if(TryViewportRay(point,out var origin,out var direction,out var near,out var far))gesture.Samples.Add(new(origin,direction,near,far));
    }
    private void ExtendBrushStroke(Point point)
    {
        if(brushGesture is not {} gesture)return;
        if(!CurrentBrushGesture(gesture)){CancelBrushCapture();return;}
        var delta=point-gesture.Last;var steps=Math.Max(1,(int)Math.Ceiling(delta.Length/4));
        // Interpolation is screen-space sampling only. Core owns all triangle coverage and occlusion.
        steps=Math.Min(steps,4097);
        for(var i=1;i<=steps;i++)AddBrushRay(gesture,gesture.Last+delta*(i/(double)steps));
        gesture.Last=point;DrawBrushCursor(point);
    }
    private async Task EndBrushStroke(Point point)
    {
        if(brushGesture is not {} gesture)return;
        ExtendBrushStroke(point);if(!ReferenceEquals(brushGesture,gesture))return;
        brushGesture=null;ViewportSurface.ReleaseMouseCapture();RefreshWorkspaceNavigation();RefreshMaterials();
        if(!CurrentBrushGesture(gesture)||gesture.Truncated||gesture.Samples.Count==0)
        {Message(gesture.Truncated?"Stroke too long. Use a shorter stroke; the reviewed preview is unchanged.":"Stroke cancelled. The reviewed preview is unchanged.");return;}
        using var cancellation=new CancellationTokenSource();operation=cancellation;RefreshWorkspaceNavigation();RefreshMaterials();CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;
        try
        {
            Guid? target=brushTarget;var foundTarget=false;
            foreach(var sample in gesture.Samples)
            {
                var hit=await editor.PickAsync(sample.Origin,sample.Direction,sample.MinimumDistance,sample.MaximumDistance,cancellation.Token);
                if(!CurrentBrushGesture(gesture)||cancellation.IsCancellationRequested)return;
                if(hit==null)continue;
                if(hit.CapturedRevision!=gesture.Revision||hit.EditingGroupId!=gesture.Scope)return;
                if(!hit.IsSelectable)continue;
                if(gesture.Scope==null&&editor.Snapshot.Scene.Groups.Any(g=>g.ObjectIds.Contains(hit.ObjectId))){Message("Choose Edit members before brushing inside a group.");return;}
                if(target!=null&&target!=hit.ObjectId)continue;
                target=hit.ObjectId;brushLastHit=hit.Position;foundTarget=true;break;
            }
            if(!foundTarget||target==null){Message(brushTarget==null?"No editable surface under this stroke. The reviewed preview is unchanged.":"This stroke missed the current object. Apply or cancel before choosing another object.");return;}
            var strokes=brushStrokes.Add(new(gesture.Diameter,gesture.Material,gesture.Samples.ToImmutableArray()));
            await PublishBrush(new SurfaceBrushRequest(target.Value,strokes){MemberGroupId=materialsScope},cancellation.Token);
        }
        catch(OperationCanceledException){Message("Stroke cancelled. The reviewed preview is unchanged.");}
        catch(ModelException error){Message(error.Message,true);}
        finally{FinishBrushOperation();}
    }
    private async Task ReplayBrush()
    {
        if(brushTarget is not {} target||brushStrokes.IsEmpty||operation!=null)return;
        using var cancellation=new CancellationTokenSource();operation=cancellation;RefreshWorkspaceNavigation();RefreshMaterials();CancelOperation.Visibility=Progress.Visibility=Visibility.Visible;
        try{await PublishBrush(new SurfaceBrushRequest(target,brushStrokes){MemberGroupId=materialsScope},cancellation.Token);}
        catch(OperationCanceledException){Message("Brush preview cancelled. The previous review is unchanged.");}
        finally{FinishBrushOperation();}
    }
    private async Task PublishBrush(SurfaceBrushRequest request,CancellationToken cancellation)
    {
        var result=await editor.PreviewSurfaceBrushAsync(request,ownedMaterialsPreview,cancellation,null,materialsBaseRevision);
        if(result.Succeeded&&editor.Snapshot.Preview is {Request:SurfaceBrushRequest} preview)
        {ownedMaterialsPreview=preview.Id;reviewedMaterial=request;brushStrokes=request.Strokes;brushTarget=request.ObjectId;materialsDirty=false;}
        Message(result.Message,result.Outcome is not(Outcome.Success or Outcome.Cancelled));
    }
    private void FinishBrushOperation()
    {
        operation=null;CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;RefreshWorkspaceNavigation();
        if(materialsCancelRequested)FinishCancelMaterials();else{RefreshMaterials();RenderScene();}
    }
    private void CancelBrushInput()
    {CancelBrushCapture();if(BrushActive)operation?.Cancel();}
    private void CancelBrushCapture()
    {
        BrushCursorOverlay?.Children.Clear();if(brushGesture==null)return;
        brushGesture=null;ViewportSurface.ReleaseMouseCapture();RefreshWorkspaceNavigation();RefreshMaterials();
    }
    private void DrawBrushCursor(Point point)
    {
        BrushCursorOverlay.Children.Clear();
        if(!BrushActive||MaterialShowOriginal.IsChecked==true||brushLastHit is not {} hit||!double.TryParse(BrushDiameter.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var diameter)||!double.IsFinite(diameter)||diameter<=0)return;
        var projection=TransformGizmo.Projection.Capture(Viewport.Camera as ProjectionCamera,Viewport.RenderSize);
        if(projection==null)return;
        var look=camera.LookDirection;look.Normalize();var right=Vector3D.CrossProduct(look,camera.UpDirection);right.Normalize();
        var a=projection.Project(hit);var b=projection.Project(hit+new Vec3(right.X,right.Y,right.Z)*(diameter/2));
        if(a==null||b==null)return;var radius=(b.Value-a.Value).Length;if(!double.IsFinite(radius)||radius>5000)return;
        var ring=new Ellipse{Width=radius*2,Height=radius*2,Stroke=Brushes.White,StrokeThickness=2,StrokeDashArray=new(){3,2}};
        Canvas.SetLeft(ring,point.X-radius);Canvas.SetTop(ring,point.Y-radius);BrushCursorOverlay.Children.Add(ring);
    }
}
