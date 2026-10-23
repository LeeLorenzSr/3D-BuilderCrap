using Builder.Core;
using Builder.Desktop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static async Task RunPerformanceControls()
    {
        await Open("assembly-mm.3mf");
        var first=editor.Snapshot.Scene.Objects[0].Id;
        Ok(editor.Execute(new RotateObject(first,new(17,23,11))));
        Refresh(); Layout(1280,780);
        var viewport=Named<Viewport3D>("Viewport");
        var pick=typeof(MainWindow).GetMethod("PickViewportObject",PrivateInstance)!;
        Assert(pick!=null,"Desktop has no shared Core picking adapter.");
        var compared=0; var hitCount=0;
        foreach(var view in new[]{"Top","Front","Right","Isometric"})
        {
            Click(view); Layout(1280,780);
            var before=editor.Snapshot;
            foreach(var fx in new[]{.1,.25,.4,.5,.6,.75,.9}) foreach(var fy in new[]{.1,.25,.4,.5,.6,.75,.9})
            {
                var point=new Point(viewport.ActualWidth*fx,viewport.ActualHeight*fy);
                var expected=DesktopWpfPick(point);
                var value=pick!.Invoke(window,[point]);
                var actual=value is Task<Guid?> pending ? await pending : (Guid?)value;
                Assert(actual==expected,$"Core camera-ray adapter differs from WPF at {view} {point}: {actual} != {expected}");
                compared++; if(actual!=null)hitCount++;
            }
            SameAccepted(before);
        }
        Assert(hitCount>0 && hitCount<compared,"Picking samples did not exercise both hits and misses.");
        var beforeResize=editor.Snapshot;
        using(var pendingCancellation=new CancellationTokenSource())
        {
            var pendingField=typeof(MainWindow).GetField("pendingPick",PrivateInstance)!;
            pendingField.SetValue(window,pendingCancellation);
            try
            {
                Layout(1040,680);
                Assert(pendingCancellation.IsCancellationRequested,"Viewport resize did not cancel a pending ray query.");
                SameAccepted(beforeResize);
            }
            finally { pendingField.SetValue(window,null); }
        }
        observations.Add(new {test="WPF/Core picking equivalence",compared,hitCount});
        Pass("Portable picking agrees with WPF nearest leaves across four views/rotated objects/hits/misses; resizing cancels a pending query without changing the scene");

        // Enough rows to prove bounded realization without making ordinary checks depend on opt-in files.
        for(var i=2;i<80;i++) Ok(editor.Execute(new AddCube()));
        Refresh(); Layout(1040,680);
        var list=Named<ListBox>("ObjectList");
        var realized=Enumerable.Range(0,list.Items.Count).Count(i=>list.ItemContainerGenerator.ContainerFromIndex(i)!=null);
        Assert(list.Items.Count==80 && realized<40,$"Object list realizes {realized}/{list.Items.Count} rows.");
        var last=list.Items[^1]; list.ScrollIntoView(last); Layout(1040,680);
        var lastContainer=(FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(last)!;
        Assert(lastContainer!=null,"Last virtualized object could not be realized.");
        var lastBounds=Bounds(lastContainer!); var listBounds=Bounds(list);
        Assert(lastBounds.Bottom>listBounds.Top && lastBounds.Top<listBounds.Bottom,"Last virtualized object is unreachable.");
        var beforeSelection=editor.Snapshot;
        list.SelectedItems.Clear(); list.SelectedItems.Add(last);
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual(((ObjectTreeRow)last).ObjectIds),"Virtualized last-row selection did not reach shared Core.");
        Assert(editor.Snapshot.Scene==beforeSelection.Scene && editor.Snapshot.CanUndo==beforeSelection.CanUndo,"Virtualized selection changed document/history.");
        observations.Add(new {test="Object list virtualization",total=list.Items.Count,realized,lastReachable=true});
        Capture("45-virtualized-object-list",1040,680);
        Pass("Object list realizes a bounded visible set, scrolls to the last row and preserves Core selection and history");

        var scroll=ObjectTreeVisuals(list).OfType<ScrollViewer>().First();
        var offset=scroll.VerticalOffset; var beforeMove=editor.Snapshot;
        var lastId=((ObjectTreeRow)last).Id;
        Ok(editor.Execute(new MoveObject(lastId,new(70,10,10)))); Refresh(); Layout(1040,680);
        Near(scroll.VerticalOffset,offset);
        Assert(list.SelectedItems.OfType<ObjectTreeRow>().Single().Id==lastId,"Transform lost visible last-row selection.");
        Near(editor.Snapshot.Scene.Objects.Single(o=>o.Id==lastId).Bounds.Center.X,70);
        Click(Named<Button>("UndoButton")); Layout(1040,680);
        Near(scroll.VerticalOffset,offset);
        Assert(editor.Snapshot.Scene==beforeMove.Scene && editor.Snapshot.SelectedObjectIds.SequenceEqual(beforeMove.SelectedObjectIds),"Undo lost scene/selection.");
        Pass("Transform and Undo retain a scrolled object-list position and selection while updating the accepted geometry");
    }

    private static Guid? DesktopWpfPick(Point point)
    {
        var map=(Dictionary<GeometryModel3D,Guid>)typeof(MainWindow).GetField("hitObjects",PrivateInstance)!.GetValue(window)!;
        Guid? selected=null; var nearest=double.PositiveInfinity;
        VisualTreeHelper.HitTest(Named<Viewport3D>("Viewport"),null,result=> {
            if(result is RayMeshGeometry3DHitTestResult hit && hit.DistanceToRayOrigin<nearest &&
                hit.ModelHit is GeometryModel3D model && map.TryGetValue(model,out var id))
            {selected=id;nearest=hit.DistanceToRayOrigin;}
            return HitTestResultBehavior.Continue;
        },new PointHitTestParameters(point));
        return selected;
    }
}
