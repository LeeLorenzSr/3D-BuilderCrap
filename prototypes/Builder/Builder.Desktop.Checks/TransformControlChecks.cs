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
        var (start,hit)=PickAxis(axis);
        var viewport=Named<System.Windows.Controls.Viewport3D>("Viewport"); var viewportSize=viewport.RenderSize;
        Assert((bool)Invoke("BeginHandleDrag",start)!,"Handle did not begin.");
        Layout(surface.ActualWidth,surface.ActualHeight);
        Assert(viewport.RenderSize==viewportSize && typeof(MainWindow).GetField("handleDrag",PrivateInstance)!.GetValue(window)!=null,
            "Progress controls resized the viewport or cancelled the active drag.");
        var end=start+hit.PixelsPerUnit*units;
        if(hit.ReadAngleDegrees!=null)
        {
            var center=CurrentGizmo().Center; var v=start-center; var angle=units*Math.PI/180;
            end=center+new Vector(v.X*Math.Cos(angle)+v.Y*Math.Sin(angle),-v.X*Math.Sin(angle)+v.Y*Math.Cos(angle));
        }
        Invoke("UpdateHandleDrag",end);
        Invoke("EndHandleDrag"); await Idle();
    }
    private static void TransformFooterVisible()
    {
        Layout(1040,680);
        foreach(var name in new[]{"PreviewTransformButton","ApplyTransformButton","CancelTransformButton"})
        {
            var b=Bounds(Named<FrameworkElement>(name));
            Assert(b.Top>=0 && b.Bottom<=680 && b.Right<=1040 && b.Width>0,"Clipped transform action "+name);
            observations.Add(new {control=name,bounds=b.ToString(),client="1040 x 680"});
        }
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

        await Open("cube-20mm.stl"); Click("Top"); Click(Named<Button>("MoveHandleButton"));
        TransformFooterVisible(); var before=editor.Snapshot;
        await DragAxis(0,4.4); SameAccepted(before);
        var p=editor.Snapshot.Preview!; Assert(p.Request is MoveTransform,"Move handle produced wrong request.");
        Near(p.Scene.Objects[0].Bounds.Center.X,14); Near(p.Scene.Objects[0].Bounds.Size.X,20);
        Capture("10-move-handles",1040,680);
        // A second stroke starts from the current candidate, while Core always derives from accepted geometry.
        await DragAxis(0,2.2); SameAccepted(before); Near(editor.Snapshot.Preview!.Scene.Objects[0].Bounds.Center.X,16);
        Click(Named<Button>("ApplyTransformButton")); Near(editor.Snapshot.Scene.Bounds.Center.X,16);
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"Handle previews created multiple undo entries.");
        Click(Named<Button>("RedoButton")); Ok(await editor.SaveAsync(Path.Combine(output,"handle-move.3mf")));
        Pass("Move handles snap in Core, replace candidates without drift, release previews and commit one undo step");

        await Open("cube-20mm.stl"); Click("Top"); Click(Named<Button>("RotateHandleButton")); before=editor.Snapshot;
        await DragAxis(2,37); SameAccepted(before); Near(((RotateTransform)editor.Snapshot.Preview!.Request).Degrees.Z,30);
        Capture("11-rotate-handles",1040,680); TransformFooterVisible();
        Click("Isometric"); var beforeAxisSwitch=editor.Snapshot;
        var otherAxis=PickAxis(0); Assert(!(bool)Invoke("BeginHandleDrag",otherAxis.Start)!,"Mixed-axis handle replay would change rotation order.");
        Assert(editor.Snapshot==beforeAxisSwitch,"Rejected axis change mutated preview.");
        Click(Named<Button>("ApplyTransformButton")); Ok(await editor.SaveAsync(Path.Combine(output,"handle-rotate.3mf")));
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"Rotation handle undo failed.");
        Pass("Rotation ring produces a snapped world-axis preview and undo restores exact geometry");

        await Open("cube-20mm.stl"); Click("Top"); Click(Named<Button>("ScaleHandleButton")); before=editor.Snapshot;
        await DragAxis(0,2.6); SameAccepted(before);
        var scale=(ScaleTransform)editor.Snapshot.Preview!.Request; Near(scale.Factors.X,1.25); Near(scale.Factors.Y,1.25); Near(scale.Factors.Z,1.25);
        Capture("12-scale-handles",1040,680); Click(Named<Button>("ApplyTransformButton"));
        Near(editor.Snapshot.Scene.Bounds.Size.X,25); Near(editor.Snapshot.Scene.Bounds.Min.Z,-2.5);
        Ok(await editor.SaveAsync(Path.Combine(output,"handle-scale.3mf")));
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"Scale handle undo failed.");
        Click(Named<Button>("ScaleHandleButton")); Named<CheckBox>("UniformScale").IsChecked=false; before=editor.Snapshot;
        await DragAxis(0,2.6); SameAccepted(before); scale=(ScaleTransform)editor.Snapshot.Preview!.Request;
        Near(scale.Factors.X,1.25); Near(scale.Factors.Y,1); Near(scale.Factors.Z,1);
        Named<CheckBox>("UniformScale").IsChecked=true; Click(Named<Button>("PreviewTransformButton")); await Idle();
        var beforeUniform=editor.Snapshot; var scalePick=PickAxis(0);
        Assert(!(bool)Invoke("BeginHandleDrag",scalePick.Start)!,"Uniform drag on nonuniform candidate would distort proportions after factor snapping.");
        Assert(editor.Snapshot==beforeUniform,"Uniform guard mutated candidate.");
        Named<CheckBox>("UniformScale").IsChecked=false; Click(Named<Button>("PreviewTransformButton")); await Idle();
        var oldPreview=editor.Snapshot.Preview;
        await DragAxis(0,-20); SameAccepted(before);
        Assert(editor.Snapshot.Preview!.Scene.Objects.SequenceEqual(oldPreview!.Scene.Objects) && !Named<Button>("ApplyTransformButton").IsEnabled,"Invalid scale changed candidate geometry or allowed stale Apply.");
        Click(Named<Button>("CancelTransformButton")); SameAccepted(before);
        Pass("Uniform/axis scale, percentage snapping, center pivot and invalid-draft cancellation preserve accepted state");

        Click(Named<Button>("MoveHandleButton")); before=editor.Snapshot;
        var pick=PickAxis(0); Assert((bool)Invoke("BeginHandleDrag",pick.Start)!,"Drag start failed.");
        for(var i=1;i<=50;i++) Invoke("UpdateHandleDrag",pick.Start+pick.Hit.PixelsPerUnit*i);
        // Calls the same cancellation handler as real mouse capture loss, without injecting OS input.
        Invoke("ViewportLostCapture",window,new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount));
        await Idle(); SameAccepted(before); Assert(editor.Snapshot.Preview==null,"Capture loss left a candidate.");
        Assert(!Named<FrameworkElement>("TransformActions").IsVisible || Named<FrameworkElement>("TransformActions").Visibility==Visibility.Collapsed,"Cancelled footer still active.");
        Click(Named<Button>("MoveHandleButton")); pick=PickAxis(0);
        Assert((bool)Invoke("BeginHandleDrag",pick.Start)!,"Drag start failed.");
        Invoke("UpdateHandleDrag",pick.Start+pick.Hit.PixelsPerUnit*8); Click(Named<Button>("CancelOperation")); await Idle();
        SameAccepted(before); Assert(editor.Snapshot.Preview==null,"In-flight Cancel retained candidate.");
        Pass("Coalesced pointer updates, lost-capture cancellation and in-flight Cancel preserve scene/history/selection");

        Click(Named<Button>("MoveHandleButton")); Named<TextBox>("PositionX").Text="15.4";
        Click(Named<Button>("PreviewTransformButton")); await Idle(); SameAccepted(before);
        Named<TextBox>("PositionX").Text="NaN";
        Assert(!Named<Button>("ApplyTransformButton").IsEnabled,"Unpreviewed field change permits Apply.");
        Click(Named<Button>("PreviewTransformButton")); SameAccepted(before);
        Click(Named<Button>("CancelTransformButton")); SameAccepted(before);
        Pass("Numeric and handle tools share preview ownership; changed/invalid fields cannot apply an older result");

        await Open("cube-20mm.stl"); Click("Top"); Click(Named<Button>("RotateHandleButton")); before=editor.Snapshot;
        var circular=PickAxis(2); var pivot=CurrentGizmo().Center; var radial=circular.Start-pivot;
        Assert((bool)Invoke("BeginHandleDrag",circular.Start)!,"Circular drag start failed.");
        foreach(var degrees in new[]{90d,170,190,270,350,370,450})
        {
            var radians=degrees*Math.PI/180;
            Invoke("UpdateHandleDrag",pivot+new Vector(radial.X*Math.Cos(radians)+radial.Y*Math.Sin(radians),-radial.X*Math.Sin(radians)+radial.Y*Math.Cos(radians)));
        }
        // Camera commands must not change the captured projection during a stroke.
        var oldCamera=Named<System.Windows.Controls.Viewport3D>("Viewport").Camera;
        var oldPosition=((ProjectionCamera)oldCamera).Position;
        Click("Isometric"); Assert(Named<System.Windows.Controls.Viewport3D>("Viewport").Camera==oldCamera && ((ProjectionCamera)oldCamera).Position==oldPosition,"View changed during an active stroke.");
        Invoke("EndHandleDrag"); await Idle(); SameAccepted(before);
        Near(((RotateTransform)editor.Snapshot.Preview!.Request).Degrees.Z,450);
        Click(Named<Button>("CancelTransformButton")); SameAccepted(before);
        Ok(editor.Execute(new MoveObject(editor.Snapshot.SelectedObjectId!.Value,new(10,10,10.3)))); Refresh(); Click("Top");
        before=editor.Snapshot; Click(Named<Button>("MoveHandleButton")); Named<CheckBox>("SnapTransforms").IsChecked=true;
        Click(Named<Button>("BedButton")); await Idle(); SameAccepted(before);
        Assert(Named<CheckBox>("SnapTransforms").IsChecked==false,"Exact bed placement must keep subsequent movement unsnapped.");
        Near(editor.Snapshot.Preview!.Scene.Bounds.Min.Z,0);
        await DragAxis(0,1.4); Near(editor.Snapshot.Preview!.Scene.Bounds.Min.Z,0); SameAccepted(before);
        Click(Named<Button>("CancelTransformButton")); SameAccepted(before);
        Pass("Circular rotation crosses angle wrap and multiple turns; exact bed preview remains on the bed during subsequent movement");
    }
}
