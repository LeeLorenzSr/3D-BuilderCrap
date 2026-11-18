using Builder.Core;
using Builder.Desktop;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using ScaleTransform = Builder.Core.ScaleTransform;
using RotateTransform = Builder.Core.RotateTransform;

internal static partial class Program
{
    private static object? Invoke(string name,params object[] values)=>typeof(MainWindow).GetMethod(name,PrivateInstance)!.Invoke(window,values);
    private static GizmoLayout CurrentGizmo()=>(GizmoLayout)typeof(MainWindow).GetField("gizmoLayout",PrivateInstance)!.GetValue(window)!;
    private static (Point Start,GizmoHit Hit) PickAxis(int axis)
    {
        var layout=CurrentGizmo(); var stroke=layout.Strokes.Single(s=>s.Axis==axis);
        var point=stroke.Points.Count==2 ? stroke.Points[0]+(stroke.Points[1]-stroke.Points[0])*0.85 : stroke.Points.Skip(1).First(p=>layout.HitTest(p)?.Axis==axis);
        var hit=layout.HitTest(point);
        Assert(hit?.Axis==axis,"Could not uniquely pick requested axis "+axis);
        return (point,hit!);
    }
    private static async Task DragAxis(int axis,double units)
    {
        // Settle the preceding tab/tool change before measuring the drag's projection.
        Layout(surface.ActualWidth,surface.ActualHeight);
        var (start,hit)=PickAxis(axis);
        var viewport=Named<System.Windows.Controls.Viewport3D>("Viewport"); var viewportSize=viewport.RenderSize;
        string ChromeSizes()=>string.Join(" ",new[]{"WorkspaceTabs","ObjectCommands","FileStrip","Status","EditActions","HandleTools","ConstructionTools","ItemsPane"}
            .Select(name=>name+"="+Named<FrameworkElement>(name).ActualHeight.ToString("0.##")+"/"+Named<FrameworkElement>(name).Visibility));
        var initialChrome=ChromeSizes();
        Assert((bool)Invoke("BeginHandleDrag",start)!,"Handle did not begin.");
        Layout(surface.ActualWidth,surface.ActualHeight);
        Assert(viewport.RenderSize==viewportSize && typeof(MainWindow).GetField("handleDrag",PrivateInstance)!.GetValue(window)!=null,
            $"Transform changed the viewport ({viewportSize} to {viewport.RenderSize}) or cancelled the active drag. Before {initialChrome}. After {ChromeSizes()}.");
        var end=start+hit.PixelsPerUnit*units;
        if(hit.ReadAngleDegrees!=null)
        {
            var center=CurrentGizmo().Center; var v=start-center; var angle=units*Math.PI/180;
            end=center+new Vector(v.X*Math.Cos(angle)+v.Y*Math.Sin(angle),-v.X*Math.Sin(angle)+v.Y*Math.Cos(angle));
        }
        Invoke("UpdateHandleDrag",end);
        Invoke("EndHandleDrag"); await Idle();
    }
    private static async Task CheckScaleFootprint()
    {
        await Open("cube-20mm.stl");
        foreach (var shape in new[]{"cube","pyramid"})
        {
            if (shape=="pyramid")
            {
                var cleared=await editor.TransitionAsync(new NewDocumentTransition(editor.Snapshot.Revision,DirtyDecision.Discard));
                Ok(cleared.Result); Refresh(); await Add("Pyramid");
            }
            await Workspace(4);
            Click(Named<Button>("ViewIsometricButton")); Click(Named<Button>("FitSelectionButton"));
            Named<FrameworkElement>("ViewportSurface").RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120)
                {RoutedEvent=UIElement.MouseWheelEvent});
            Click(Named<Button>("ScaleHandleButton")); await Idle();
            var layout=CurrentGizmo();
            var frame=layout.ScaleFrame ?? throw new InvalidOperationException("No scale frame for "+shape);
            var obj=editor.Snapshot.Scene.Objects.Single();
            var points=obj.Mesh.Vertices.Distinct().Select(obj.Transform.Apply).Select(v=>layout.Project(v)!.Value).ToArray();
            Near(frame.Top,points.Min(p=>p.Y)-3); Near(frame.Bottom,points.Max(p=>p.Y)+3);
            Near(frame.Left,points.Min(p=>p.X)-3); Near(frame.Right,points.Max(p=>p.X)+3);
            var projectedPivot=layout.Project(obj.Bounds.Center)!.Value;
            Near((layout.Center-projectedPivot).Length,0);
            Assert(layout.ProportionalHandles.Count==4 && layout.ProportionalHandles.All(p=>layout.HitTest(p)?.Proportional==true),
                shape+" scale corners must be visible and pickable.");
            var viewport=Named<Viewport3D>("Viewport");
            var maxReach=Math.Max(48,Math.Min(viewport.ActualWidth,viewport.ActualHeight)*.42);
            Assert(layout.Strokes.Count==3 && layout.Strokes.All(s=>s.Opposite!=null),shape+" needs both sides of each isometric axis.");
            foreach (var stroke in layout.Strokes)
            foreach (var shaft in new[]{stroke.Points,stroke.Opposite!})
            {
                var direction=shaft[^1]-projectedPivot;
                var reach=direction.Length; direction.Normalize();
                var extent=points.Max(p=>Vector.Multiply(p-projectedPivot,direction));
                Near(reach,Math.Clamp(extent+24,43,maxReach));
            }
            if (shape=="pyramid")
            {
                var apex=obj.Mesh.Vertices.MaxBy(p=>p.Z);
                Near(frame.Top,layout.Project(obj.Transform.Apply(apex))!.Value.Y-3);
            }
            Capture("scale-frame-"+shape,surface.ActualWidth,surface.ActualHeight);
        }
        Pass("Isometric cube and pyramid frames follow the projected mesh, including the pyramid tip. Axes use the same footprint and all four corners select proportional scale.");
    }
    private static async Task TransformControls()
    {
        var camera=new OrthographicCamera(new(0,0,100),new(0,0,-100),new(0,1,0),200) {NearPlaneDistance=0.1,FarPlaneDistance=1000};
        var layout=TransformGizmo.Layout(camera,new(800,600),new(),HandleMode.Move);
        Near(layout.Center.X,400); Near(layout.Center.Y,300);
        Assert(layout.Strokes.Count==2 && layout.Strokes.All(s=>s.Axis!=2),"Depth axis should be hidden.");
        var x=layout.Strokes.Single(s=>s.Axis==0); Near(x.Points[^1].X,490); Near(x.Points[^1].Y,300);
        var hit=layout.HitTest(new(480,300)); Assert(hit?.Axis==0,"X hit failed."); Near(hit!.PixelsPerUnit.X,4); Near(hit.PixelsPerUnit.Y,0);
        var rotation=TransformGizmo.Layout(camera,new(800,600),new(),HandleMode.Rotate);
        Assert(rotation.Strokes.Count==1 && rotation.Strokes[0].Axis==2,"Top view should expose only unambiguous Z ring.");
        var turn=rotation.HitTest(new(490,300)); Assert(turn?.Axis==2,"Z ring pick failed."); Near(turn!.PixelsPerUnit.X,0); Near(turn.PixelsPerUnit.Y,-Math.PI/2);
        Near(turn.ReadAngleDegrees!(new(400,210))!.Value,90);
        Near(Math.Abs(turn.ReadAngleDegrees(new(310,300))!.Value),180);
        Assert(turn.ReadAngleDegrees(new(400,300))==null,"Ring center should not yield an angle.");
        Near(turn.ReadAngleDegrees(new(900,300))!.Value,0);
        var perspective=new PerspectiveCamera(new(60,-70,80),new(-60,70,-80),new(0,0,1),45) {NearPlaneDistance=.1,FarPlaneDistance=1000};
        var oblique=TransformGizmo.Layout(perspective,new(800,600),new(),HandleMode.Rotate);
        foreach(var ring in oblique.Strokes)
        {
            var rayHit=ring.Points.Select(point=>oblique.HitTest(point)).First(h=>h?.Axis==ring.Axis)!;
            var projected=oblique.Project(ring.Axis switch {0=>new Vec3(0,5,5),1=>new Vec3(5,0,5),_=>new Vec3(5,5,0)})!.Value;
            Near(rayHit.ReadAngleDegrees!(projected)!.Value,45);
        }
        camera.Position=new(20,0,100); Near(layout.Project(new())!.Value.X,400);
        Assert(layout.Project(new(0,0,200))==null && layout.HitTest(new(double.NaN,0))==null,"Invalid/frustum point admitted.");
        camera.Position=new(0,0,100); camera.Transform=new TranslateTransform3D(10,0,0);
        Near(TransformGizmo.Layout(camera,new(800,600),new(10,0,0),HandleMode.Scale).Center.X,400);
        Pass("Gizmo projection, immutable camera snapshot, hidden depth axes, analytic drag mapping and finite guards");

        using var keys = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-transform-keys")
            {Width=1,Height=1,WindowStyle=0});
        void KeyOn(FrameworkElement target,Key key)
        {
            var evt=new KeyEventArgs(Keyboard.PrimaryDevice,keys,Environment.TickCount,key)
                {RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=target};
            target.RaiseEvent(evt);
            Assert(evt.Handled,"Transform input did not handle "+key);
        }

        await Open("cube-20mm.stl"); Click("Top");
        var before=editor.Snapshot;
        Assert(CurrentGizmo()!=null && Named<FrameworkElement>("TransformHud").Visibility==Visibility.Visible,
            "A selected object needs handles and its chooser before the first tool click.");
        Assert(Named<FrameworkElement>("Toolbar").IsEnabled && editor.Snapshot.Preview==null,"The resting gizmo locked the editor.");
        Named<CheckBox>("SnapTransforms").IsChecked=true;
        await DragAxis(0,4.4);
        Near(editor.Snapshot.Scene.Bounds.Center.X,14);
        Assert(editor.Snapshot.Preview==null && Named<FrameworkElement>("Toolbar").IsEnabled,"Release left a modal transform.");
        var firstMove=editor.Snapshot;
        await DragAxis(0,2.2);
        Near(editor.Snapshot.Scene.Bounds.Center.X,16);
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==firstMove.Scene,"Second drag must undo independently.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"First drag must undo independently.");
        Click(Named<Button>("RedoButton"));
        Click(Named<Button>("RedoButton"));
        Ok(await editor.SaveAsync(Path.Combine(output,"handle-move.3mf")));
        Pass("Selected move handles commit each snapped drag at release; repeated gestures, Undo, Redo and Save stay available");

        foreach(var tab in new[]{0,2,3,4})
        {
            await Workspace(tab);
            var original=editor.Snapshot;
            await DragAxis(0,1);
            Near(editor.Snapshot.Scene.Bounds.Center.X,original.Scene.Bounds.Center.X+1);
            Click(Named<Button>("UndoButton"));
            Assert(editor.Snapshot.Scene==original.Scene,"Workspace "+tab+" lost direct-drag Undo.");
        }
        await Workspace(1);
        Pass("Move handles keep the viewport steady and commit from every workspace");

        Click(Named<Button>("RotateHandleButton"));
        before=editor.Snapshot;
        var circular=PickAxis(2); var pivot=CurrentGizmo().Center; var radial=circular.Start-pivot;
        Assert((bool)Invoke("BeginHandleDrag",circular.Start)!,"Circular drag did not start.");
        foreach(var angle in new[]{90d,170,190,270,350,370,450})
        {
            var a=angle*Math.PI/180;
            Invoke("UpdateHandleDrag",pivot+new Vector(radial.X*Math.Cos(a)+radial.Y*Math.Sin(a),-radial.X*Math.Sin(a)+radial.Y*Math.Cos(a)));
        }
        await Idle();
        Near(((RotateTransform)editor.Snapshot.Preview!.Request).Degrees.Z,450);
        AssertVisibleTransformVector(2,new(0,0,90));
        Invoke("EndHandleDrag"); await Idle();
        AssertVisibleTransformVector(2,new(0,0,90));
        Assert(editor.Snapshot.Preview==null && editor.Snapshot.Scene!=before.Scene,"Rotation did not commit.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"One Undo must restore the full rotation.");
        circular=PickAxis(2); pivot=CurrentGizmo().Center;
        Assert((bool)Invoke("BeginHandleDrag",circular.Start)!,"Invalid release sample did not start.");
        Invoke("UpdateHandleDrag",circular.Start+circular.Hit.PixelsPerUnit*12);
        await Idle(); Assert(editor.Snapshot.Preview!=null && editor.Snapshot.Scene==before.Scene,"Expected an intermediate rotation without changing the document.");
        Invoke("UpdateHandleDrag",pivot);
        Invoke("EndHandleDrag"); await Idle();
        Assert(editor.Snapshot.Scene==before.Scene && editor.Snapshot.Preview==null,"Releasing at an undefined ring angle committed an old rotation.");
        Pass("Ring tracks through angle wrap and multiple turns, then releases as a single undoable rotation");

        Click(Named<Button>("ScaleHandleButton"));
        Named<CheckBox>("AspectLock").IsChecked=false;
        await DragAxis(0,2.6);
        Near(editor.Snapshot.Scene.Bounds.Size.X,25); Near(editor.Snapshot.Scene.Bounds.Size.Y,20);
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"Axis scale Undo failed.");
        Named<CheckBox>("AspectLock").IsChecked=true;
        await DragAxis(0,2.6);
        Near(editor.Snapshot.Scene.Bounds.Size.X,25); Near(editor.Snapshot.Scene.Bounds.Size.Y,25);
        Click(Named<Button>("UndoButton"));
        Named<CheckBox>("AspectLock").IsChecked=false;
        Layout(surface.ActualWidth,surface.ActualHeight);
        var scalePick=PickAxis(0);
        Assert((bool)Invoke("BeginHandleDrag",scalePick.Start)!,"Invalid-final-sample test did not start.");
        Invoke("UpdateHandleDrag",scalePick.Start+scalePick.Hit.PixelsPerUnit*2);
        await Idle();
        Assert(editor.Snapshot.Preview!=null,"Expected a valid interim scale.");
        Invoke("UpdateHandleDrag",scalePick.Start-scalePick.Hit.PixelsPerUnit*20);
        Invoke("EndHandleDrag"); await Idle();
        Assert(editor.Snapshot.Scene==before.Scene && editor.Snapshot.Preview==null,"Release accepted an earlier scale after its final sample failed.");
        Pass("Single-axis and proportional scale use one undo step; a failed final sample clears its interim preview");

        Click(Named<Button>("MoveHandleButton")); before=editor.Snapshot;
        var pick=PickAxis(0);
        Assert((bool)Invoke("BeginHandleDrag",pick.Start)!,"Capture-loss drag did not start.");
        for(var i=1;i<=50;i++) Invoke("UpdateHandleDrag",pick.Start+pick.Hit.PixelsPerUnit*i);
        Invoke("ViewportLostCapture",window,new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount));
        await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview==null && Named<FrameworkElement>("Toolbar").IsEnabled,"Capture loss left a blocking candidate.");
        pick=PickAxis(0);
        Assert((bool)Invoke("BeginHandleDrag",pick.Start)!,"Escape drag did not start.");
        Invoke("UpdateHandleDrag",pick.Start+pick.Hit.PixelsPerUnit*8);
        KeyOn(Named<FrameworkElement>("ViewportSurface"),Key.Escape);
        await Idle(); SameAccepted(before);
        Assert(editor.Snapshot.Preview==null,"Escape retained an in-flight preview.");
        Pass("Lost capture and Escape cancel coalesced drag updates and preserve selection, history and accepted geometry");

        var xField=Named<TextBox>("PositionX");
        ArrayNumber("PositionX",23.456);
        KeyOn(xField,Key.Enter);await Idle();
        Near(editor.Snapshot.Scene.Bounds.Center.X,23.456);
        Assert(editor.Snapshot.Preview==null,"Numeric Enter left a preview.");
        Click(Named<Button>("UndoButton"));Assert(editor.Snapshot.Scene==before.Scene,"A single Enter must create a single Undo step.");
        before=editor.Snapshot;
        xField.Text="NaN";KeyOn(xField,Key.Enter);await Idle();
        SameAccepted(before);KeyOn(xField,Key.Escape);await Idle();SameAccepted(before);
        Assert(Named<FrameworkElement>("Toolbar").IsEnabled,"Invalid numeric input kept the editor locked after Escape.");
        // A caller can change the Core directly between the local text draft and Enter.
        ArrayNumber("PositionX",30);
        Ok(editor.Execute(new MoveObject(before.SelectedObjectId!.Value,new(23,10,10))));
        var intervening=editor.Snapshot;
        KeyOn(xField,Key.Enter);await Idle();
        SameAccepted(intervening);
        Assert(editor.Snapshot.Preview==null,"The stale numeric request leaked a preview.");
        Pass("Numeric Enter commits an exact unsnapped value; invalid and stale values preserve the accepted document");
        await CheckOrientationFields(KeyOn);
    }

    private static async Task CheckOrientationFields(Action<FrameworkElement,Key> keyOn)
    {
        Ok((await editor.TransitionAsync(new NewDocumentTransition(editor.Snapshot.Revision,DirtyDecision.Discard))).Result);
        Ok(editor.Execute(new AddPrimitive(PrimitiveKind.Pyramid,new(20,20,20))));Refresh();
        Click(Named<Button>("RotateHandleButton"));
        async Task TypeAngle(string axis,double angle)
        {
            var field="Rotation"+axis;
            ArrayNumber(field,angle);
            keyOn(Named<TextBox>(field),Key.Enter);
            await Idle();
            Assert(editor.Snapshot.Preview==null,"Typing a rotation left a preview.");
        }
        AssertVisibleTransformVector(2,new());
        await TypeAngle("Z",7);AssertVisibleTransformVector(2,new(0,0,7));
        await TypeAngle("Z",12);AssertVisibleTransformVector(2,new(0,0,12));
        Click(Named<Button>("UndoButton"));AssertVisibleTransformVector(2,new(0,0,7));
        Click(Named<Button>("UndoButton"));AssertVisibleTransformVector(2,new());
        Pass("Pyramid Yaw reads 0°, 7° and 12° after successive Enter edits, then 7° and 0° through Undo");

        var id=editor.Snapshot.SelectedObjectId!.Value;
        Ok(editor.Execute(new ResizeObject(id,new(40,30,10))));
        Ok(editor.Execute(new RotateObject(id,new(25,-31,48))));Refresh();
        var path=Path.Combine(output,"oriented-pyramid.3mf");
        Ok(await editor.SaveAsync(path));Ok(await editor.OpenAsync(path));Refresh();
        AssertVisibleTransformVector(2,new(25,-31,48));
        await TypeAngle("Y",14.25);AssertVisibleTransformVector(2,new(25,14.25,48));
        var expected=Affine.ScaleAbout(new(2,1.5,.5),default).Then(Affine.RotateAbout(new(25,14.25,48),default));
        var result=editor.Snapshot.Scene.Objects.Single().Transform;
        void SameAxes(Affine a,Affine b)
        {
            foreach(var (first,second) in new[]{(a.X,b.X),(a.Y,b.Y),(a.Z,b.Z)})
            { Near(first.X,second.X);Near(first.Y,second.Y);Near(first.Z,second.Z); }
        }
        SameAxes(expected,result);
        Ok(await editor.SaveAsync(path));Ok(await editor.OpenAsync(path));Refresh();
        AssertVisibleTransformVector(2,new(25,14.25,48));
        SameAxes(expected,editor.Snapshot.Scene.Objects.Single().Transform);
        foreach(var pitch in new[]{90d,-90d})
        {
            var pole=Affine.RotateAbout(new(23,pitch,-35),default);
            var representation=ObjectOrientation.From(pole)!.Degrees;
            SameAxes(pole,Affine.RotateAbout(representation,default));
        }
        Pass("Absolute Pitch edits preserve Roll, Yaw and nonuniform local scale on a reopened 3MF; pole angles reconstruct the same rotation");

        id=editor.Snapshot.SelectedObjectId!.Value;
        Ok(await editor.PreviewTransformAsync(new ScaleTransform(id,new(1.5,1,1))));
        Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();
        Assert(Named<TextBlock>("RotationUnits").Text=="Δ°","Sheared selection needs a relative rotation marker.");
        AssertVisibleTransformVector(2,new());
        var sheared=editor.Snapshot.Scene.Objects.Single().Transform;
        await TypeAngle("Z",12);
        SameAxes(sheared.Then(Affine.RotateAbout(new(0,0,12),default)),editor.Snapshot.Scene.Objects.Single().Transform);
        AssertVisibleTransformVector(2,new());
        Click(Named<Button>("UndoButton"));SameAxes(sheared,editor.Snapshot.Scene.Objects.Single().Transform);
        Assert(ObjectOrientation.From(Affine.ScaleAbout(new(-1,1,1),default))==null &&
            ObjectOrientation.From(Affine.ScaleAbout(new(1,1,0),default))==null,"Reflected or collapsed basis reported an absolute orientation.");
        await Open("assembly-mm.3mf");Click(Named<Button>("SelectAllButton"));Click(Named<Button>("RotateHandleButton"));
        Assert(Named<TextBlock>("RotationUnits").Text=="Δ°","A multi-object selection needs a relative rotation marker.");
        Pass("Sheared objects and multiple selections show Δ°; relative edits preserve shear and Undo, and mirrored or collapsed bases have no absolute readout");
    }
}
