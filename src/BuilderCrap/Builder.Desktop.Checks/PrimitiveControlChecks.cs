using Builder.Core;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static void BeginShape(string shape)
    {
        var item=Tree(window).OfType<MenuItem>().Single(m=>m.Header?.ToString()==shape);
        Assert(item.IsEnabled,"Disabled shape entry: "+shape);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,item));
        Assert(Named<FrameworkElement>("PrimitivePanel").Visibility==Visibility.Visible,"Shape panel failed to open.");
        Assert(Named<ListBox>("PrimitiveChoices").Items.Count==9,"Palette must expose all nine shapes.");
    }
    private static void PrimitiveFooterInside()
    {
        Layout(1040,680);
        foreach(var name in new[]{"PreviewPrimitiveButton","AddPrimitiveButton","CancelPrimitiveButton"})
        {
            var button=Named<Button>(name);var bounds=button.TransformToAncestor(surface).TransformBounds(new Rect(button.RenderSize));
            Assert(bounds.Width>0&&bounds.Height>0&&bounds.Top>=0&&bounds.Bottom<=680,"Creation footer out of minimum client bounds: "+name);
        }
    }
    private static async Task RunPrimitiveControls()
    {
        await Open("cube-20mm.stl");var before=editor.Snapshot;
        BeginShape("Torus");PrimitiveFooterInside();Capture("primitive-torus-minimum",1040,680);
        Assert(Named<FrameworkElement>("PrimitiveTorusFields").Visibility==Visibility.Visible&&Named<FrameworkElement>("PrimitiveDimensionFields").Visibility==Visibility.Collapsed,"Torus shows irrelevant box dimensions.");
        Assert(!Named<Button>("AddPrimitiveButton").IsEnabled,"Add enabled before preview.");
        Click(Named<Button>("CancelPrimitiveButton"));SameAccepted(before);
        Assert(Named<FrameworkElement>("ObjectsOverview").Visibility==Visibility.Visible,"Object overview stayed hidden after creation cancel.");
        Pass("Nine-shape palette uses relevant fields and fixed minimum-window Preview/Add/Cancel footer");

        foreach(var shape in new[]{"Cube","Cylinder","Pyramid","Cone","Sphere","Hexagon","Wedge","Torus","Tetrahedron"})
        {
            before=editor.Snapshot;BeginShape(shape);
            Named<TextBox>("PrimitiveSizeX").Text="30";Named<TextBox>("PrimitiveSizeY").Text="24";Named<TextBox>("PrimitiveSizeZ").Text="18";
            Named<TextBox>("PrimitiveSegments").Text="12";Named<TextBox>("PrimitiveLatitude").Text="8";Named<TextBox>("PrimitiveTubeSegments").Text="12";
            Named<TextBox>("PrimitiveMajorRadius").Text="12";Named<TextBox>("PrimitiveTubeRadius").Text="3";
            Click(Named<Button>("PreviewPrimitiveButton"));await Idle();SameAccepted(before);
            var candidate=editor.Snapshot.Preview??throw new InvalidOperationException("No primitive candidate: "+shape);
            var id=candidate.ResultObjectIds.Single();var obj=candidate.Scene.Objects.Single(o=>o.Id==id);
            var expected=shape=="Torus"?new Vec3(30,30,6):new Vec3(30,24,18);
            Near(obj.Bounds.Min.X,0);Near(obj.Bounds.Min.Y,0);Near(obj.Bounds.Min.Z,0);
            Near(obj.Bounds.Size.X,expected.X);Near(obj.Bounds.Size.Y,expected.Y);Near(obj.Bounds.Size.Z,expected.Z);
            CameraContains(candidate.Scene.Bounds);PrimitiveFooterInside();
            Click(Named<Button>("AddPrimitiveButton"));
            var accepted=editor.Snapshot;
            Assert(accepted.Scene.Objects.Length==before.Scene.Objects.Length+1&&accepted.SelectedObjectId==id&&accepted.SelectedObjectIds.SequenceEqual(new[]{id}),"Add lost shape/selection identity: "+shape);
            Assert(ReferenceEquals(accepted.Scene.Objects.Single(o=>o.Id==id).Mesh,obj.Mesh),"Add regenerated different mesh from the reviewed candidate.");
            Click(Named<Button>("UndoButton"));
            Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&editor.Snapshot.FilePath==before.FilePath&&editor.Snapshot.IsDirty==before.IsDirty,"Creation Undo did not restore exact accepted document: "+shape);
            Click(Named<Button>("RedoButton"));Assert(editor.Snapshot.Scene.Objects.Any(o=>o.Id==id),"Redo lost stable creation ID.");
            Click(Named<Button>("UndoButton"));
        }
        Capture("primitive-nine-created-undo",1280,780);
        Pass("All nine actual menu entries preview bounded mm shapes, Add reviewed stable objects and Undo/Redo atomically");

        before=editor.Snapshot;BeginShape("Torus");
        Click(Named<Button>("PreviewPrimitiveButton"));await Idle();var old=editor.Snapshot.Preview!;
        Named<TextBox>("PrimitiveMajorRadius").Text="2";
        Assert(!Named<Button>("AddPrimitiveButton").IsEnabled,"Changed fields left stale Add enabled.");
        Invoke("AddPrimitiveClick",window,new RoutedEventArgs());SameAccepted(before);
        Click(Named<Button>("PreviewPrimitiveButton"));await Idle();
        Assert(editor.Snapshot.Preview?.Id==old.Id&&!Named<Button>("AddPrimitiveButton").IsEnabled,"Invalid replacement lost prior preview or re-enabled stale Add.");
        Named<TextBox>("PrimitiveMajorRadius").Text="20";Named<TextBox>("PrimitiveSegments").Text="7";
        Click(Named<Button>("PreviewPrimitiveButton"));await Idle();Assert(editor.Snapshot.Preview?.Id==old.Id,"Invalid resolution replaced candidate.");
        Named<TextBox>("PrimitiveSegments").Text="16";Click(Named<Button>("PreviewPrimitiveButton"));await Idle();
        var updated=editor.Snapshot.Preview!;Assert(updated.Id!=old.Id&&updated.ResultObjectIds.SequenceEqual(old.ResultObjectIds),"Replacement must have fresh token and stable candidate object ID.");
        Click(Named<Button>("AddPrimitiveButton"));Near(editor.Snapshot.Scene.Objects.Single(o=>o.Id==updated.ResultObjectIds[0]).Bounds.Size.X,50);
        Click(Named<Button>("UndoButton"));
        Pass("Changed/invalid dimension and resolution drafts retain prior candidate but cannot Add stale geometry");

        before=editor.Snapshot;BeginShape("Sphere");
        Click(Named<Button>("PreviewPrimitiveButton"));Named<TextBox>("PrimitiveSizeX").Text="36";await Idle();
        Assert(!Named<Button>("AddPrimitiveButton").IsEnabled,"Draft changed during generation accepted a stale result.");SameAccepted(before);
        Click(Named<Button>("PreviewPrimitiveButton"));await Idle();Assert(Named<Button>("AddPrimitiveButton").IsEnabled,"Current re-preview did not enable Add.");
        using(var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-primitive-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var buttonName in new[]{"PreviewPrimitiveButton","AddPrimitiveButton","CancelPrimitiveButton"})
            {
                var button=Named<Button>(buttonName);
                var enter=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=button};
                button.RaiseEvent(enter);
                Assert(!enter.Handled,"Global creation Enter intercepted the footer button: "+buttonName);SameAccepted(before);
            }
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            Named<Button>("CancelPrimitiveButton").RaiseEvent(escape);
            Assert(escape.Handled,"Creation did not handle routed Escape.");
        }
        SameAccepted(before);
        BeginShape("Sphere");Named<TextBox>("PrimitiveSegments").Text="128";Named<TextBox>("PrimitiveLatitude").Text="64";
        Click(Named<Button>("PreviewPrimitiveButton"));Click(Named<Button>("CancelPrimitiveButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview==null&&Named<FrameworkElement>("PrimitiveActions").Visibility==Visibility.Collapsed,"Cancel left creation active/candidate installed.");
        Pass("In-flight draft versions reject stale generation; Escape and cancellation preserve accepted document and prior history");

        before=editor.Snapshot;BeginShape("Cube");
        Invoke("MoveHandleClick",window,new RoutedEventArgs());Invoke("SplitClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());
        Invoke("SelectAllClick",window,new RoutedEventArgs());
        Assert(Named<FrameworkElement>("TransformActions").Visibility==Visibility.Collapsed&&Named<FrameworkElement>("ConstructionActions").Visibility==Visibility.Collapsed,"Another tool replaced active creation.");SameAccepted(before);
        Click(Named<Button>("CancelPrimitiveButton"));
        Ok(editor.Execute(new AddCube()));Ok(editor.Execute(new AddCube()));var ids=editor.Snapshot.Scene.Objects.Take(2).Select(o=>o.Id).ToImmutableArray();
        Ok(await editor.PreviewArrangementAsync(new GroupRequest(ids)));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));
        var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,ids[0]));Refresh();before=editor.Snapshot;
        Invoke("AddCubeClick",window,new RoutedEventArgs());
        Assert(Named<FrameworkElement>("PrimitiveActions").Visibility==Visibility.Collapsed&&!Named<MenuItem>("AddShapeMenu").IsEnabled,"Member scope allowed scene creation.");SameAccepted(before);
        Ok(editor.ExitGroup());Refresh();
        Pass("Creation guards conflicting tools/selection/history and remains unavailable inside member editing");
    }
}
