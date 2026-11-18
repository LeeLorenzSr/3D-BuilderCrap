using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RotateTransform = Builder.Core.RotateTransform;
using ScaleTransform = Builder.Core.ScaleTransform;

namespace Builder.Desktop;

public partial class MainWindow
{
    private HandleMode? handleMode;
    private HandleMode preferredHandleMode = HandleMode.Move;
    private HandleMode DisplayedTransformMode => handleMode ?? preferredHandleMode;
    private bool settling;
    private bool ShowTransformHud => transformsReady && !settling && SelectedTarget != null && !ModalToolActive &&
        constructionMode == null && arrangementMode == null && !mcpOperationActive &&
        (editor.Snapshot.Preview == null || ownedTransformPreview == editor.Snapshot.Preview?.Id);
    private GizmoLayout? gizmoLayout;
    private sealed record HandleGesture(Point Start,GizmoHit Hit,TransformRequest Initial,Vec3 BaseSize,bool Uniform,double SnapStep,Size ViewportSize)
    {
        public double LastAngle {get;set;}
        public double AngleDelta {get;set;}
        public bool HasValidPointer {get;set;}=true;
    }
    private HandleGesture? handleDrag;
    private TransformRequest? pendingTransform;
    private Guid? ownedTransformPreview;
    private long transformBaseRevision, transformDraftVersion;
    private Guid? transformEditingGroupId;
    private bool transformsReady, transformUpdating, cancelTransformRequested, transformDraftChanged;
    private bool commitTransformWhenReady;
    private readonly bool[] transformFieldsDirty=[true,true,true];
    private static readonly Color[] AxisColors=[Color.FromRgb(210,78,60),Color.FromRgb(46,159,91),Color.FromRgb(40,143,211)];

    private void InitializeTransforms()
    {
        TransformTabs.SelectedIndex=TransformFieldIndex(preferredHandleMode);
        transformsReady=true;
        foreach(var box in new[]{SizeX,SizeY,SizeZ,PositionX,PositionY,PositionZ,RotationX,RotationY,RotationZ})
            box.TextChanged+=TransformDraftChanged;
        Viewport.SizeChanged+=(_,_)=>{
            CancelBrushInput(); CancelPendingPick();
            if(handleDrag is {} stroke)
                Dispatcher.InvokeAsync(() => {
                    if(ReferenceEquals(handleDrag,stroke) && Viewport.RenderSize!=stroke.ViewportSize) CancelTransform();
                },System.Windows.Threading.DispatcherPriority.Render);
            else DrawTransformHandles();
            DrawMeasurement(); DrawSelectionOutline();
        };
        Deactivated+=(_,_)=>{ CancelBrushInput();CancelPendingPick(); if(handleDrag!=null) CancelTransform(); };
    }
    private void MoveHandleClick(object sender,RoutedEventArgs e)=>ChooseTransformMode(HandleMode.Move);
    private void RotateHandleClick(object sender,RoutedEventArgs e)=>ChooseTransformMode(HandleMode.Rotate);
    private void ScaleHandleClick(object sender,RoutedEventArgs e)=>ChooseTransformMode(HandleMode.Scale);
    private void TransformTabChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!transformsReady || refreshing || e.Source!=TransformTabs) return;
        ChooseTransformMode(TransformTabs.SelectedIndex switch {0=>HandleMode.Scale,1=>HandleMode.Move,_=>HandleMode.Rotate});
    }
    private bool ChooseTransformMode(HandleMode mode)
    {
        if(!ShowTransformHud || operation!=null || handleDrag!=null) return false;
        if(handleMode is {} previous && previous!=mode) FinishCancelTransform();
        EnsureTransformFields(mode);
        if(preferredHandleMode!=mode || TransformTabs.SelectedIndex!=TransformFieldIndex(mode))
        {
            preferredHandleMode=mode;
            refreshing=true;
            SnapStep.Text=mode switch {HandleMode.Move=>"1",HandleMode.Rotate=>"15",_=>"5"};
            TransformTabs.SelectedIndex=TransformFieldIndex(mode);
            refreshing=false;
        }
        RefreshTransforms(); DrawTransformHandles();
        Message("Drag a handle and release to apply, or type a value and press Enter.");
        return true;
    }
    private void StartTransformDraft(HandleMode mode)
    {
        if(handleMode.HasValue) return;
        var snapshot=editor.Snapshot;
        handleMode=mode;
        transformBaseRevision=snapshot.Revision;
        transformEditingGroupId=snapshot.EditingGroupId;
        transformDraftChanged=false;
        transformDraftVersion=0;
    }
    private void TransformDraftChanged(object sender,TextChangedEventArgs e)
    {
        if(refreshing || !ShowTransformHud || !IsDisplayedTransformField(sender) || transformUpdating) return;
        StartTransformDraft(preferredHandleMode);
        transformDraftVersion++; transformDraftChanged=true; RefreshTransforms();
    }
    private bool IsDisplayedTransformField(object? field) => DisplayedTransformMode switch
    {
        HandleMode.Move => field == PositionX || field == PositionY || field == PositionZ,
        HandleMode.Rotate => field == RotationX || field == RotationY || field == RotationZ,
        _ => field == SizeX || field == SizeY || field == SizeZ
    };
    private bool ReadSnap(out double step)
    {
        step=0;
        if(SnapTransforms.IsChecked!=true) return true;
        if(double.TryParse(SnapStep.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out step) && double.IsFinite(step) && step>0) return true;
        Message("Enter a positive, finite snap step, or turn snapping off. Your scene is unchanged.",true);
        return false;
    }
    private void CommitTransformClick(object sender,RoutedEventArgs e)=>CommitNumericTransform(DisplayedTransformMode);
    private void PreviewOnBed()
    {
        if(!ChooseTransformMode(HandleMode.Move) || SelectedTarget is not {} obj) return;
        StartTransformDraft(HandleMode.Move);
        settling=true;
        QueueTransform(new MoveTransform(obj.Id,new(0,0,-obj.Bounds.Min.Z)) {ObjectIds=obj.ObjectIds});
    }
    private void CommitNumericTransform(HandleMode mode)
    {
        if(!ChooseTransformMode(mode) || SelectedTarget is not {} obj) return;
        StartTransformDraft(mode);
        TransformRequest request;
        if(mode==HandleMode.Move)
        {
            if(!ReadVector(PositionX,PositionY,PositionZ,out var center)) return;
            request=new MoveTransform(obj.Id,center-obj.Bounds.Center);
        }
        else if(mode==HandleMode.Rotate)
        {
            if(!ReadVector(RotationX,RotationY,RotationZ,out var degrees)) return;
            if(SelectionOrientation(editor.Snapshot.Scene,obj.ObjectIds) is {} orientation)
            {
                // Carry the original precision of angles the user left at their displayed values.
                var current=orientation.Degrees;
                double Target(TextBox field,double typed,double previous)=>field.Text==Number(previous) ? previous : typed;
                degrees=orientation.RotationTo(new(Target(RotationX,degrees.X,current.X),
                    Target(RotationY,degrees.Y,current.Y),Target(RotationZ,degrees.Z,current.Z)));
            }
            request=new RotateTransform(obj.Id,degrees);
        }
        else
        {
            if(!ReadVector(SizeX,SizeY,SizeZ,out var size)) return;
            var b=obj.Bounds.Size;
            if(b.X<=0 || b.Y<=0 || b.Z<=0)
            { Message("A flat object cannot use three-axis size fields. Use a non-flat axis handle instead.",true); return; }
            request=new ScaleTransform(obj.Id,new(size.X/b.X,size.Y/b.Y,size.Z/b.Z));
        }
        commitTransformWhenReady=true;
        QueueTransform(request with {ObjectIds=obj.ObjectIds});
    }
    private void QueueTransform(TransformRequest request)
    {
        pendingTransform=request with {MemberGroupId=transformEditingGroupId}; transformDraftVersion++; transformDraftChanged=true;
        if(transformUpdating) return;
        transformUpdating=true; RefreshWorkspaceNavigation(); _=PumpTransforms();
    }
    private async Task PumpTransforms()
    {
        using var cancellation=new CancellationTokenSource(); operation=cancellation; RefreshWorkspaceNavigation();
        RefreshTransforms();
        try
        {
            while(pendingTransform is {} request && !cancelTransformRequested)
            {
                pendingTransform=null;
                var draftVersion=transformDraftVersion;
                var result=await editor.PreviewTransformAsync(request,ownedTransformPreview,cancellation.Token,
                    expectedRevision:transformBaseRevision);
                if(result.Succeeded && editor.Snapshot.Preview is {Request:TransformRequest} p)
                {
                    ownedTransformPreview=p.Id; transformDraftChanged=transformDraftVersion!=draftVersion;
                    if(pendingTransform==null && !transformDraftChanged) SyncTransformFields((TransformRequest)p.Request);
                }
                else transformDraftChanged=true;
                if(!result.Succeeded || handleDrag==null && !commitTransformWhenReady)
                    Message(result.Message,result.Outcome is not (Outcome.Success or Outcome.Cancelled));
                RefreshTransforms();
                RefreshViewportModels();
                DrawSelectionOutline();
                DrawTransformHandles();
                if(cancellation.IsCancellationRequested) break;
            }
        }
        finally
        {
            pendingTransform=null; operation=null; transformUpdating=false;
            if(cancelTransformRequested) FinishCancelTransform();
            else if(commitTransformWhenReady) CompleteDirectTransform();
            else RefreshTransforms();
        }
    }
    private void SyncTransformFields(TransformRequest request)
    {
        if(editor.Snapshot.Preview is not {} preview) return;
        refreshing=true;
        var b=SelectionBounds(preview.Scene,request.ObjectIds.IsDefaultOrEmpty ? [request.ObjectId] : request.ObjectIds);
        var angles=request is RotateTransform r ? r.Degrees : new Vec3();
        var mode=handleMode ?? TransformTabs.SelectedIndex switch {0=>HandleMode.Scale,1=>HandleMode.Move,_=>HandleMode.Rotate};
        Array.Fill(transformFieldsDirty,true);
        WriteTransformFields(mode,b,angles,preview.Scene);
        transformFieldsDirty[TransformFieldIndex(mode)]=false;
        refreshing=false;
    }
    private static int TransformFieldIndex(HandleMode mode)=>mode switch {HandleMode.Scale=>0,HandleMode.Move=>1,_=>2};
    private void RefreshTransformFieldPresentation()
    {
        // A document/selection refresh invalidates every mode. Hidden editors initialize when opened,
        // while tab switches without a refresh retain their existing per-mode user drafts.
        Array.Fill(transformFieldsDirty,true);
        EnsureTransformFields(TransformTabs.SelectedIndex switch {0=>HandleMode.Scale,1=>HandleMode.Move,_=>HandleMode.Rotate});
    }
    private void EnsureTransformFields(HandleMode mode)
    {
        var index=TransformFieldIndex(mode);
        if(!transformFieldsDirty[index]) return;
        var wasRefreshing=refreshing; refreshing=true;
        try
        {
            WriteTransformFields(mode,SelectedTarget?.Bounds,new Vec3(),editor.Snapshot.Scene);
            transformFieldsDirty[index]=false;
        }
        finally {refreshing=wasRefreshing;}
    }
    private static ObjectOrientation? SelectionOrientation(Scene scene,System.Collections.Immutable.ImmutableArray<Guid> ids) =>
        ids.Length==1 && scene.Objects.FirstOrDefault(obj=>obj.Id==ids[0]) is {} obj ? ObjectOrientation.From(obj.Transform) : null;

    private void WriteTransformFields(HandleMode mode,Bounds3? bounds,Vec3 angles,Scene scene)
    {
        if(mode==HandleMode.Rotate)
        {
            var orientation=SelectionOrientation(scene,editor.Snapshot.SelectedObjectIds);
            var absolute=orientation!=null;
            if(absolute) angles=orientation!.Degrees;
            RotationUnits.Text=absolute ? "°" : "Δ°";
            RotationUnits.ToolTip=absolute ? "Object orientation in degrees (Roll, Pitch, Yaw)." :
                "Relative world rotation in degrees. This selection has no single orientation.";
            foreach(var (field,axis) in new[]{(RotationX,"Roll (X)"),(RotationY,"Pitch (Y)"),(RotationZ,"Yaw (Z)")})
            {
                var label=axis+(absolute ? " orientation in degrees" : " change in degrees");
                field.ToolTip=label+". Press Enter to apply.";
                System.Windows.Automation.AutomationProperties.SetName(field,label);
            }
        }
        var fields=mode switch {HandleMode.Scale=>new[]{SizeX,SizeY,SizeZ},HandleMode.Move=>new[]{PositionX,PositionY,PositionZ},_=>new[]{RotationX,RotationY,RotationZ}};
        var values=mode switch {HandleMode.Scale=>bounds?.Size,HandleMode.Move=>bounds?.Center,_=>angles};
        fields[0].Text=values is {} x ? Number(x.X) : "";
        fields[1].Text=values is {} y ? Number(y.Y) : "";
        fields[2].Text=values is {} z ? Number(z.Z) : "";
    }
    private void ApplyTransformClick(object sender,RoutedEventArgs e)
    {
        if(transformUpdating || handleDrag!=null || transformDraftChanged || editor.Snapshot.Preview is not {Request:TransformRequest} p || p.Id!=ownedTransformPreview) return;
        var result=editor.ApplyPreview(p.Id,transformBaseRevision);
        if(result.Succeeded) { ResetTransformDraft(); Refresh(); ViewportSurface.Focus(); }
        Message(result.Message,!result.Succeeded);
    }
    private void CompleteDirectTransform()
    {
        commitTransformWhenReady=false;
        if(!transformDraftChanged && editor.Snapshot.Preview is {Request:TransformRequest} p && p.Id==ownedTransformPreview)
        {
            var result=editor.ApplyPreview(p.Id,transformBaseRevision);
            Message(result.Message,!result.Succeeded);
        }
        // The last pointer sample may have failed after an earlier successful preview.
        // Never apply that older candidate or leave it blocking the next gesture.
        if(ownedTransformPreview is {} id && editor.Snapshot.Preview?.Id==id) editor.DiscardPreview(id);
        ResetTransformDraft();
        Refresh();
        ViewportSurface.Focus();
    }
    private void ResetTransformDraft()
    {
        handleMode=null; ownedTransformPreview=null; settling=false;
        cancelTransformRequested=false; commitTransformWhenReady=false; transformDraftChanged=false;
    }
    private void CancelTransformClick(object sender,RoutedEventArgs e)=>CancelTransform();
    private void CancelTransform()
    {
        handleDrag=null; pendingTransform=null; commitTransformWhenReady=false; cancelTransformRequested=true;
        if(ViewportSurface.IsMouseCaptured) ViewportSurface.ReleaseMouseCapture();
        if(transformUpdating) { operation?.Cancel(); Message("Cancelling transform…"); RefreshTransforms(); }
        else FinishCancelTransform();
    }
    private void FinishCancelTransform()
    {
        if(editor.Snapshot.Preview is {Request:TransformRequest} p && p.Id==ownedTransformPreview) editor.DiscardPreview(p.Id);
        ResetTransformDraft();
        Refresh(); ViewportSurface.Focus(); Message("Transform cancelled. Your scene is unchanged.");
    }
    private void RefreshTransforms()
    {
        RefreshWorkspaceNavigation();
        if(!transformsReady) return;
        var active=handleMode!=null; var snapshot=editor.Snapshot; var p=snapshot.Preview;
        var mode=DisplayedTransformMode;
        TransformHud.Visibility=ShowTransformHud ? Visibility.Visible : Visibility.Collapsed;
        HandleTools.Visibility=SelectedTarget!=null && constructionMode==null && arrangementMode==null ? Visibility.Visible : Visibility.Collapsed;
        var typing=active && !settling && handleDrag==null && !commitTransformWhenReady && transformDraftChanged;
        TransformDraftActions.Visibility=typing ? Visibility.Visible : Visibility.Collapsed;
        CommitTransformButton.IsEnabled=typing && !transformUpdating;
        DiscardTransformButton.IsEnabled=!cancelTransformRequested;
        SettleActions.Visibility=settling ? Visibility.Visible : Visibility.Collapsed;
        RenameFields.Visibility=ObjectSummary.Visibility=active ? Visibility.Collapsed : Visibility.Visible;
        ConstructionTools.Visibility=active || constructionMode!=null ? Visibility.Collapsed : Visibility.Visible;
        SnapUnit.Text=mode switch {HandleMode.Rotate=>"degrees",HandleMode.Scale=>"% change",_=>"mm"};
        SettleConfirmButton.IsEnabled=settling && p?.Request is MoveTransform && p.Id==ownedTransformPreview && !transformDraftChanged && !transformUpdating;
        foreach(var (button,choice) in new[]{(MoveHandleButton,HandleMode.Move),(RotateHandleButton,HandleMode.Rotate),(ScaleHandleButton,HandleMode.Scale)})
        {
            button.IsEnabled=ShowTransformHud && !transformUpdating && handleDrag==null;
            button.FontWeight=choice==mode ? FontWeights.Bold : FontWeights.Normal;
        }
        TransformTabs.IsEnabled=!transformUpdating && handleDrag==null;
        HandleSettings.IsEnabled=!transformUpdating && handleDrag==null;
        if(active)
        {
            Toolbar.IsEnabled=false; ObjectList.IsEnabled=false; ConstructionTools.IsEnabled=false;
            RenameButton.IsEnabled=DuplicateButton.IsEnabled=DeleteButton.IsEnabled=false;
            BedButton.IsEnabled=handleMode==HandleMode.Move && !transformUpdating && handleDrag==null;
            PreviewBanner.Visibility=Visibility.Collapsed;
        }
        else
        {
            ConstructionTools.IsEnabled=true;
            ObjectName.IsEnabled=RenameButton.IsEnabled=CanRenameSelection;
            DuplicateButton.IsEnabled=!editor.Snapshot.SelectedObjectIds.IsEmpty;
            DeleteButton.IsEnabled=CanDeleteSelection;
            BedButton.IsEnabled=SelectedTarget!=null;
            Toolbar.IsEnabled=constructionMode==null && arrangementMode==null && operation==null;
            ObjectList.IsEnabled=constructionMode==null && arrangementMode==null && operation==null;
        }
        RefreshArrangement();
    }
    private static double Component(Vec3 v,int axis)=>axis==0 ? v.X : axis==1 ? v.Y : v.Z;
    private static Vec3 ChangeAxis(Vec3 v,int axis,double value)=>axis==0 ? v with {X=value} : axis==1 ? v with {Y=value} : v with {Z=value};
    private bool BeginHandleDrag(Point point)
    {
        if(!ShowTransformHud || SelectedTarget is not {} obj || operation!=null || !ReadSnap(out var snap) || gizmoLayout?.HitTest(point) is not {} hit) return false;
        var mode=DisplayedTransformMode;
        if(transformDraftChanged)
        { Message("Press Enter to use the typed value or Escape to discard it before dragging."); return false; }
        var uniform=hit.Proportional || AspectLock.IsChecked==true;
        TransformRequest initial=mode switch {
            HandleMode.Move=>new MoveTransform(obj.Id,new()),
            HandleMode.Rotate=>new RotateTransform(obj.Id,new()),
            _=>new ScaleTransform(obj.Id,new(1,1,1)) };
        initial=initial with {ObjectIds=obj.ObjectIds};
        if(mode==HandleMode.Scale && Component(obj.Bounds.Size,hit.Axis)<=0)
        { Message("This axis has zero size. Choose another handle.",true); return false; }
        var angle=hit.ReadAngleDegrees?.Invoke(point);
        if(mode==HandleMode.Rotate && angle==null) return false;
        StartTransformDraft(mode);
        handleDrag=new(point,hit,initial,obj.Bounds.Size,uniform,snap,Viewport.RenderSize) {LastAngle=angle ?? 0};
        RefreshTransforms();
        return true;
    }
    private void UpdateHandleDrag(Point point)
    {
        if(handleDrag is not {} gesture || cancelTransformRequested) return;
        gesture.HasValidPointer=false;
        var v=gesture.Hit.PixelsPerUnit; var delta=Vector.Multiply(point-gesture.Start,v)/v.LengthSquared;
        if(gesture.Hit.ReadAngleDegrees is {} readAngle)
        {
            if(readAngle(point) is not {} angle) return;
            var difference=angle-gesture.LastAngle;
            if(difference>180) difference-=360; else if(difference < -180) difference+=360;
            gesture.AngleDelta+=difference; gesture.LastAngle=angle; delta=gesture.AngleDelta;
        }
        if(!double.IsFinite(delta)) return;
        var axis=gesture.Hit.Axis;
        // Snap only this stroke. Previously typed values and exact bed contact survive
        // a later drag on another axis.
        try
        {
            delta = gesture.Initial is ScaleTransform
                ? TransformRequest.SnapIncrement(2 * delta / Component(gesture.BaseSize,axis), gesture.SnapStep / 100)
                : TransformRequest.SnapIncrement(delta, gesture.SnapStep);
        }
        catch (ModelException error) { Message(error.Message,true); return; }
        TransformRequest request=gesture.Initial switch {
            MoveTransform move=>move with {DeltaMillimeters=ChangeAxis(move.DeltaMillimeters,axis,Component(move.DeltaMillimeters,axis)+delta)},
            RotateTransform rotate=>rotate with {Degrees=ChangeAxis(rotate.Degrees,axis,Component(rotate.Degrees,axis)+delta)},
            ScaleTransform scale=>ScaleGesture(scale,gesture,delta),
            _=>throw new InvalidOperationException("Unsupported handle mode.") };
        gesture.HasValidPointer=true;
        QueueTransform(request);
    }
    private static ScaleTransform ScaleGesture(ScaleTransform scale,HandleGesture gesture,double delta)
    {
        var axis=gesture.Hit.Axis;
        var next=Component(scale.Factors,axis)+delta;
        var factors=gesture.Uniform ? scale.Factors*(next/Component(scale.Factors,axis)) : ChangeAxis(scale.Factors,axis,next);
        return scale with {Factors=factors};
    }
    private void EndHandleDrag()
    {
        if(handleDrag==null) return;
        if(!handleDrag.HasValidPointer || Viewport.RenderSize!=handleDrag.ViewportSize) { CancelTransform(); return; }
        handleDrag=null; commitTransformWhenReady=true;
        if(!transformUpdating) CompleteDirectTransform();
    }

    private void DrawTransformHandles()
    {
        if (!transformsReady) return;
        TransformOverlay.Children.Clear();
        gizmoLayout = null;
        if (!ShowTransformHud || SelectedTarget is not {} target ||
            Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;

        var mode = DisplayedTransformMode;
        var bounds = SelectionBounds(VisibleScene, target.ObjectIds);
        gizmoLayout = TransformGizmo.Layout((System.Windows.Media.Media3D.ProjectionCamera)Viewport.Camera,
            new(Viewport.ActualWidth,Viewport.ActualHeight), bounds.Center, mode, bounds,
            outlineShapes.Values.SelectMany(shape => shape.WorldVertices));
        if (gizmoLayout.ScaleFrame is {} frame)
        {
            var outline = new Rectangle {Width=frame.Width, Height=frame.Height, Stroke=Brushes.White,
                StrokeThickness=1, Opacity=.85};
            Canvas.SetLeft(outline,frame.Left); Canvas.SetTop(outline,frame.Top);
            TransformOverlay.Children.Add(outline);
            foreach (var corner in gizmoLayout.ProportionalHandles)
            {
                var grip = new Rectangle {Width=8, Height=8, RadiusX=1.5, RadiusY=1.5, Fill=Brushes.White,
                    Stroke=Brushes.DimGray, StrokeThickness=1};
                Canvas.SetLeft(grip,corner.X-4); Canvas.SetTop(grip,corner.Y-4);
                TransformOverlay.Children.Add(grip);
            }
        }
        foreach (var stroke in gizmoLayout.Strokes)
        {
            var brush = new SolidColorBrush(AxisColors[stroke.Axis]);
            if (mode == HandleMode.Rotate)
            {
                var points = new PointCollection(stroke.Points);
                TransformOverlay.Children.Add(new Polyline {Points=points, Stroke=Brushes.White, StrokeThickness=2.6, Opacity=.7});
                TransformOverlay.Children.Add(new Polyline {Points=points, Stroke=brush, StrokeThickness=1.5});
                // A tangential grip makes each ring discoverable without covering the model with lettering.
                var middle = stroke.Points.Count*(stroke.Axis*2+1)/8;
                var tangent = stroke.Points[middle+1]-stroke.Points[middle-1];
                if (tangent.Length > 0)
                {
                    tangent.Normalize();
                    DrawMoveGrip(stroke.Points[middle]-tangent*10, stroke.Points[middle]+tangent*10, brush);
                }
            }
            else
            {
                DrawLinearGrip(stroke.Points,mode,brush);
                if (stroke.Opposite is {} opposite) DrawLinearGrip(opposite,mode,brush);
            }
        }
    }

    private void DrawLinearGrip(IReadOnlyList<Point> points, HandleMode mode, Brush brush)
    {
        var start = points[0];
        var end = points[^1];
        if (mode == HandleMode.Move)
        {
            DrawMoveGrip(start,end,brush);
            return;
        }
        var direction = end-start;
        direction.Normalize();
        DrawArrowhead(end,direction,brush);
        DrawArrowhead(end-direction*9,direction,brush);
    }

    private void DrawMoveGrip(Point start, Point end, Brush brush)
    {
        var direction = end-start;
        direction.Normalize();
        TransformOverlay.Children.Add(new Line {X1=start.X, Y1=start.Y, X2=end.X, Y2=end.Y,
            Stroke=Brushes.White, StrokeThickness=4});
        TransformOverlay.Children.Add(new Line {X1=start.X, Y1=start.Y, X2=end.X, Y2=end.Y,
            Stroke=brush, StrokeThickness=2.7});
        DrawArrowhead(start,-direction,brush);
        DrawArrowhead(end,direction,brush);
    }

    private void DrawArrowhead(Point tip, Vector direction, Brush brush)
    {
        var normal = new Vector(-direction.Y,direction.X);
        TransformOverlay.Children.Add(new Polygon {Fill=brush, Stroke=Brushes.White, StrokeThickness=.75,
            StrokeLineJoin=PenLineJoin.Round,
            Points=new(){tip,tip-direction*8+normal*4.5,tip-direction*8-normal*4.5}});
    }
}
