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
    private GizmoLayout? gizmoLayout;
    private sealed record HandleGesture(Point Start,GizmoHit Hit,TransformRequest Initial,Vec3 BaseSize,bool Uniform)
    {
        public double LastAngle {get;set;}
        public double AngleDelta {get;set;}
    }
    private HandleGesture? handleDrag;
    private TransformRequest? pendingTransform;
    private Guid? ownedTransformPreview;
    private long transformBaseRevision, transformDraftVersion;
    private Guid? transformEditingGroupId;
    private bool transformsReady, transformUpdating, cancelTransformRequested, transformDraftChanged;
    private readonly bool[] transformFieldsDirty=[true,true,true];
    private static readonly Color[] AxisColors=[Color.FromRgb(184,54,53),Color.FromRgb(39,125,76),Color.FromRgb(39,105,192)];

    private void InitializeTransforms()
    {
        transformsReady=true;
        foreach(var box in new[]{SizeX,SizeY,SizeZ,PositionX,PositionY,PositionZ,RotationX,RotationY,RotationZ})
            box.TextChanged+=TransformDraftChanged;
        Viewport.SizeChanged+=(_,_)=>{ CancelBrushInput();CancelPendingPick(); if(handleDrag!=null) CancelTransform(); else DrawTransformHandles(); DrawMeasurement(); DrawMaterialSelection(); };
        Deactivated+=(_,_)=>{ CancelBrushInput();CancelPendingPick(); if(handleDrag!=null) CancelTransform(); };
    }
    private void MoveHandleClick(object sender,RoutedEventArgs e)=>BeginTransformMode(HandleMode.Move);
    private void RotateHandleClick(object sender,RoutedEventArgs e)=>BeginTransformMode(HandleMode.Rotate);
    private void ScaleHandleClick(object sender,RoutedEventArgs e)=>BeginTransformMode(HandleMode.Scale);
    private void TransformTabChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!transformsReady || refreshing || e.Source!=TransformTabs) return;
        BeginTransformMode(TransformTabs.SelectedIndex switch {0=>HandleMode.Scale,1=>HandleMode.Move,_=>HandleMode.Rotate});
    }
    private bool BeginTransformMode(HandleMode mode)
    {
        if(!transformsReady || ModalToolActive || SelectedTarget==null || constructionMode!=null || arrangementMode!=null || operation!=null || handleDrag!=null) return false;
        if(editor.Snapshot.Preview!=null && handleMode!=mode)
        { Message("Apply or cancel this preview before switching tools."); return false; }
        EnsureTransformFields(mode);
        if(handleMode!=mode)
        {
            handleMode=mode; transformBaseRevision=editor.Snapshot.Revision; transformEditingGroupId=editor.Snapshot.EditingGroupId; transformDraftChanged=false; transformDraftVersion=0;
            refreshing=true;
            SnapStep.Text=mode switch {HandleMode.Move=>"1",HandleMode.Rotate=>"15",_=>"5"};
            TransformTabs.SelectedIndex=mode switch {HandleMode.Scale=>0,HandleMode.Move=>1,_=>2};
            refreshing=false;
        }
        RefreshTransforms(); DrawTransformHandles();
        Message("Drag a labeled handle or enter values, then preview. Apply accepts; Escape cancels.");
        return true;
    }
    private void TransformDraftChanged(object sender,TextChangedEventArgs e)
    {
        if(!transformsReady || refreshing || handleMode==null) return;
        transformDraftVersion++; transformDraftChanged=true; RefreshTransforms();
    }
    private void TransformSettingsChanged(object sender,RoutedEventArgs e)
    {
        if(!transformsReady || refreshing) return;
        transformDraftVersion++; transformDraftChanged=true; RefreshTransforms();
    }
    private bool ReadSnap(out double step)
    {
        step=0;
        if(SnapTransforms.IsChecked!=true) return true;
        if(double.TryParse(SnapStep.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out step) && double.IsFinite(step) && step>0) return true;
        Message("Enter a positive, finite snap step, or turn snapping off. Your scene is unchanged.",true);
        transformDraftChanged=true; RefreshTransforms(); return false;
    }
    private void PreviewTransformClick(object sender,RoutedEventArgs e)
    { if(handleMode is {} mode) PreviewNumericTransform(mode); }
    private void PreviewOnBed()
    {
        if(!BeginTransformMode(HandleMode.Move) || SelectedTarget is not {} obj) return;
        var delta=(editor.Snapshot.Preview?.Request as MoveTransform)?.DeltaMillimeters ?? new Vec3();
        // Exact bed contact may be between snap increments. Keep later handle strokes
        // on the same unsnapped request rather than silently resnapping its Z offset.
        refreshing=true; SnapTransforms.IsChecked=false; refreshing=false;
        QueueTransform(new MoveTransform(obj.Id,delta with {Z=-obj.Bounds.Min.Z}) {ObjectIds=obj.ObjectIds});
    }
    private void PreviewNumericTransform(HandleMode mode)
    {
        if(!BeginTransformMode(mode) || SelectedTarget is not {} obj || !ReadSnap(out var snap)) return;
        TransformRequest request;
        if(mode==HandleMode.Move)
        {
            if(!ReadVector(PositionX,PositionY,PositionZ,out var center)) return;
            request=new MoveTransform(obj.Id,center-obj.Bounds.Center,snap);
        }
        else if(mode==HandleMode.Rotate)
        {
            if(!ReadVector(RotationX,RotationY,RotationZ,out var degrees)) return;
            request=new RotateTransform(obj.Id,degrees,snap);
        }
        else
        {
            if(!ReadVector(SizeX,SizeY,SizeZ,out var size)) return;
            var b=obj.Bounds.Size;
            if(b.X<=0 || b.Y<=0 || b.Z<=0)
            { Message("A flat object cannot use three-axis size fields. Use a non-flat axis handle instead.",true); return; }
            request=new ScaleTransform(obj.Id,new(size.X/b.X,size.Y/b.Y,size.Z/b.Z),snap);
        }
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
        CancelOperation.Visibility=Progress.Visibility=Visibility.Visible; RefreshTransforms();
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
                Message(result.Message,result.Outcome is not (Outcome.Success or Outcome.Cancelled));
                RefreshTransforms(); RenderScene();
                if(cancellation.IsCancellationRequested) break;
            }
        }
        finally
        {
            pendingTransform=null; operation=null; RefreshWorkspaceNavigation(); transformUpdating=false;
            CancelOperation.Visibility=Progress.Visibility=Visibility.Collapsed;
            if(cancelTransformRequested) FinishCancelTransform();
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
        WriteTransformFields(mode,b,angles);
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
            WriteTransformFields(mode,SelectedTarget?.Bounds,new Vec3());
            transformFieldsDirty[index]=false;
        }
        finally {refreshing=wasRefreshing;}
    }
    private void WriteTransformFields(HandleMode mode,Bounds3? bounds,Vec3 angles)
    {
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
        if(result.Succeeded) { handleMode=null; ownedTransformPreview=null; Refresh(); ViewportSurface.Focus(); }
        Message(result.Message,!result.Succeeded);
    }
    private void CancelTransformClick(object sender,RoutedEventArgs e)=>CancelTransform();
    private void CancelTransform()
    {
        handleDrag=null; pendingTransform=null; cancelTransformRequested=true;
        if(ViewportSurface.IsMouseCaptured) ViewportSurface.ReleaseMouseCapture();
        if(transformUpdating) { operation?.Cancel(); Message("Cancelling transform…"); RefreshTransforms(); }
        else FinishCancelTransform();
    }
    private void FinishCancelTransform()
    {
        if(editor.Snapshot.Preview is {Request:TransformRequest} p && p.Id==ownedTransformPreview) editor.DiscardPreview(p.Id);
        handleMode=null; ownedTransformPreview=null; cancelTransformRequested=false; transformDraftChanged=false;
        Refresh(); ViewportSurface.Focus(); Message("Transform cancelled. Your scene is unchanged.");
    }
    private void RefreshTransforms()
    {
        RefreshWorkspaceNavigation();
        if(!transformsReady) return;
        var active=handleMode!=null; var snapshot=editor.Snapshot; var p=snapshot.Preview;
        HandleTools.Visibility=SelectedTarget!=null && constructionMode==null && arrangementMode==null ? Visibility.Visible : Visibility.Collapsed;
        TransformActions.Visibility=active ? Visibility.Visible : Visibility.Collapsed;
        HandleTitle.Visibility=active ? Visibility.Visible : Visibility.Collapsed;
        HandleTitle.Text=$"{handleMode} · {SelectedTarget?.Name}";
        RenameFields.Visibility=EditActions.Visibility=EditActionsDivider.Visibility=ObjectSummary.Visibility=active ? Visibility.Collapsed : Visibility.Visible;
        ConstructionTools.Visibility=active || constructionMode!=null ? Visibility.Collapsed : Visibility.Visible;
        EditPanel.Margin=active ? new(0) : new(0,20,0,0);
        TransformTabs.Margin=active ? new(0) : new(0,16,0,0);
        HandleSettings.Visibility=active ? Visibility.Visible : Visibility.Collapsed;
        UniformScale.Visibility=handleMode==HandleMode.Scale ? Visibility.Visible : Visibility.Collapsed;
        SnapUnit.Text=handleMode switch {HandleMode.Rotate=>"degrees",HandleMode.Scale=>"% change",_=>"mm"};
        PreviewTransformButton.IsEnabled=active && !transformUpdating && handleDrag==null;
        ApplyTransformButton.IsEnabled=p?.Request is TransformRequest && p.Id==ownedTransformPreview && !transformDraftChanged && !transformUpdating && handleDrag==null;
        CancelTransformButton.IsEnabled=!cancelTransformRequested;
        foreach(var (button,mode) in new[]{(MoveHandleButton,HandleMode.Move),(RotateHandleButton,HandleMode.Rotate),(ScaleHandleButton,HandleMode.Scale)})
        {
            button.IsEnabled=!transformUpdating && handleDrag==null && (p==null || mode==handleMode);
            button.FontWeight=mode==handleMode ? FontWeights.Bold : FontWeights.Normal;
        }
        TransformTabs.IsEnabled=!transformUpdating && handleDrag==null;
        // Changing tabs would abandon the current candidate; use Apply or Cancel first.
        foreach(TabItem tab in TransformTabs.Items) tab.IsEnabled=p==null || tab.IsSelected;
        HandleSettings.IsEnabled=!transformUpdating && handleDrag==null;
        if(active)
        {
            Toolbar.IsEnabled=false; ObjectList.IsEnabled=false; ConstructionTools.IsEnabled=false;
            RenameButton.IsEnabled=DuplicateButton.IsEnabled=DeleteButton.IsEnabled=false;
            BedButton.IsEnabled=handleMode==HandleMode.Move && !transformUpdating && handleDrag==null;
            TransformSummary.Text=p?.Request is TransformRequest ? p.Summary+(transformDraftChanged ? "\nValues changed. Preview again before Apply." : handleMode==HandleMode.Rotate ? "\nApply before rotating on another axis." : "\nRelease leaves a preview. Apply accepts it.")
                : "Drag X, Y or Z, or enter values and preview. Apply accepts one undoable edit; Escape cancels.";
            PreviewBanner.Visibility=p?.Request is TransformRequest ? Visibility.Visible : Visibility.Collapsed;
            PreviewLabel.Text="Transform preview · not yet applied";
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
        if(handleMode is not {} mode || SelectedTarget is not {} obj || operation!=null || !ReadSnap(out var snap) || gizmoLayout?.HitTest(point) is not {} hit) return false;
        if(transformDraftChanged && editor.Snapshot.Preview!=null)
        { Message("Preview the changed values or cancel before dragging."); return false; }
        var current=editor.Snapshot.Preview?.Request as TransformRequest;
        if(current is RotateTransform existing && Enumerable.Range(0,3).Any(axis=>axis!=hit.Axis && Component(existing.Degrees,axis)%360!=0))
        { Message("Apply or cancel this rotation before dragging a different world axis."); return false; }
        if(mode==HandleMode.Scale && UniformScale.IsChecked==true && current is ScaleTransform prior &&
            (prior.Factors.X!=prior.Factors.Y || prior.Factors.Y!=prior.Factors.Z))
        { Message("Apply or cancel the axis scale before scaling all axes with a handle."); return false; }
        TransformRequest initial=mode switch {
            HandleMode.Move=>new MoveTransform(obj.Id,(current as MoveTransform)?.DeltaMillimeters ?? new(),snap),
            HandleMode.Rotate=>new RotateTransform(obj.Id,(current as RotateTransform)?.Degrees ?? new(),snap),
            _=>new ScaleTransform(obj.Id,(current as ScaleTransform)?.Factors ?? new(1,1,1),snap) };
        initial=initial with {ObjectIds=obj.ObjectIds};
        if(mode==HandleMode.Scale && Component(obj.Bounds.Size,hit.Axis)<=0)
        { Message("This axis has zero size. Choose another handle.",true); return false; }
        var angle=hit.ReadAngleDegrees?.Invoke(point);
        if(mode==HandleMode.Rotate && angle==null) return false;
        handleDrag=new(point,hit,initial,obj.Bounds.Size,UniformScale.IsChecked==true) {LastAngle=angle ?? 0};
        QueueTransform(initial); return true;
    }
    private void UpdateHandleDrag(Point point)
    {
        if(handleDrag is not {} gesture || cancelTransformRequested) return;
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
        TransformRequest request=gesture.Initial switch {
            MoveTransform move=>move with {DeltaMillimeters=ChangeAxis(move.DeltaMillimeters,axis,Component(move.DeltaMillimeters,axis)+delta)},
            RotateTransform rotate=>rotate with {Degrees=ChangeAxis(rotate.Degrees,axis,Component(rotate.Degrees,axis)+delta)},
            ScaleTransform scale=>ScaleGesture(scale,gesture,delta),
            _=>throw new InvalidOperationException("Unsupported handle mode.") };
        QueueTransform(request);
    }
    private static ScaleTransform ScaleGesture(ScaleTransform scale,HandleGesture gesture,double delta)
    {
        var axis=gesture.Hit.Axis;
        var next=Component(scale.Factors,axis)+2*delta/Component(gesture.BaseSize,axis);
        var factors=gesture.Uniform ? scale.Factors*(next/Component(scale.Factors,axis)) : ChangeAxis(scale.Factors,axis,next);
        return scale with {Factors=factors};
    }
    private void EndHandleDrag()
    { handleDrag=null; RefreshTransforms(); if(!transformUpdating) Message("Preview ready. Apply accepts it; Escape cancels."); }

    private void DrawTransformHandles()
    {
        if(!transformsReady) return;
        TransformOverlay.Children.Clear(); gizmoLayout=null;
        if(handleMode is not {} mode || SelectedTarget is not {} accepted || constructionMode!=null || arrangementMode!=null || Viewport.ActualWidth<=0 || Viewport.ActualHeight<=0) return;
        var b=SelectionBounds(VisibleScene,accepted.ObjectIds);
        gizmoLayout=TransformGizmo.Layout((System.Windows.Media.Media3D.ProjectionCamera)Viewport.Camera,
            new(Viewport.ActualWidth,Viewport.ActualHeight),b.Center,mode);
        if(!gizmoLayout.IsVisible) return;
        var corners=new Vec3[8];
        for(var i=0;i<8;i++) corners[i]=new((i&1)==0?b.Min.X:b.Max.X,(i&2)==0?b.Min.Y:b.Max.Y,(i&4)==0?b.Min.Z:b.Max.Z);
        for(var i=0;i<8;i++) foreach(var axis in new[]{1,2,4}) if((i&axis)==0 && gizmoLayout.Project(corners[i]) is {} a && gizmoLayout.Project(corners[i|axis]) is {} end)
            TransformOverlay.Children.Add(new Line {X1=a.X,Y1=a.Y,X2=end.X,Y2=end.Y,Stroke=new SolidColorBrush(Color.FromRgb(75,102,120)),StrokeThickness=1,StrokeDashArray=new(){3,3},Opacity=0.65});
        foreach(var stroke in gizmoLayout.Strokes)
        {
            if(mode==HandleMode.Rotate && editor.Snapshot.Preview?.Request is RotateTransform rotation &&
                Enumerable.Range(0,3).Any(axis=>axis!=stroke.Axis && Component(rotation.Degrees,axis)%360!=0)) continue;
            var brush=new SolidColorBrush(AxisColors[stroke.Axis]);
            TransformOverlay.Children.Add(new Polyline {Stroke=Brushes.White,StrokeThickness=6,Points=new PointCollection(stroke.Points),StrokeLineJoin=PenLineJoin.Round,Opacity=0.95});
            var line=new Polyline {Stroke=brush,StrokeThickness=3,Points=new PointCollection(stroke.Points),StrokeLineJoin=PenLineJoin.Round};
            TransformOverlay.Children.Add(line);
            var tip=stroke.Points[^1];
            if(mode==HandleMode.Scale)
            {
                var box=new Rectangle {Width=12,Height=12,Fill=brush,Stroke=Brushes.White,StrokeThickness=1};
                Canvas.SetLeft(box,tip.X-6); Canvas.SetTop(box,tip.Y-6); TransformOverlay.Children.Add(box);
            }
            else if(mode==HandleMode.Move && stroke.Points.Count>=2)
            {
                var direction=tip-stroke.Points[0]; direction.Normalize(); var normal=new Vector(-direction.Y,direction.X);
                TransformOverlay.Children.Add(new Polygon {Fill=brush,Points=new(){tip,tip-direction*13+normal*6,tip-direction*13-normal*6}});
            }
            var label=new TextBlock {Text="XYZ"[stroke.Axis].ToString(),FontWeight=FontWeights.Bold,Foreground=SystemParameters.HighContrast?SystemColors.WindowTextBrush:brush,Background=SystemParameters.HighContrast?SystemColors.WindowBrush:Brushes.White,Padding=new(3,1,3,1),FontSize=13};
            Canvas.SetLeft(label,stroke.LabelPoint.X+5); Canvas.SetTop(label,stroke.LabelPoint.Y-10); TransformOverlay.Children.Add(label);
        }
    }
}
