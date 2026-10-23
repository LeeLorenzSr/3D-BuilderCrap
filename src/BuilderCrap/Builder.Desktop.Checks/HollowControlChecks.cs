using Builder.Core;
using Builder.Desktop;
using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static double DisplayArea(MeshGeometry3D mesh)
    {
        double sum=0;
        for(var i=0;i<mesh.TriangleIndices.Count;i+=3)
        {var a=mesh.Positions[mesh.TriangleIndices[i]];var b=mesh.Positions[mesh.TriangleIndices[i+1]];var c=mesh.Positions[mesh.TriangleIndices[i+2]];sum+=Vector3D.CrossProduct(b-a,c-a).Length/2;}
        return sum;
    }
    private static MeshGeometry3D DrawnObject(Guid id)
        =>(MeshGeometry3D)((Dictionary<GeometryModel3D,Guid>)typeof(MainWindow).GetField("hitObjects",PrivateInstance)!.GetValue(window)!).Single(p=>p.Value==id).Key.Geometry;
    private static async Task PreviewHollow()
    {
        Click(Named<Button>("PreviewHollowButton"));await Idle();
        Assert(editor.Snapshot.Preview is {Request:HollowRequest,Hollow:not null},"Hollow preview failed: "+Named<TextBlock>("Status").Text);
    }
    private static async Task HollowViewIdle()
    {
        await Idle();
        // Hollow intentionally coalesces view changes at Background priority. Core idle alone
        // does not promise that this separate presentation update has completed.
        await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Background);
    }
    private static async Task RunHollowControls()
    {
        var triangle=new Mesh([new(-1,0,0),new(1,0,0),new(0,2,0)],[new(0,1,2)]);var transform=Affine.Translation(new(5,0,0));
        var negative=DisplayCutaway.Build(triangle,transform,new(0,5,false));var positive=DisplayCutaway.Build(triangle,transform,new(0,5,true));
        Near(DisplayArea(negative),1);Near(DisplayArea(positive),1);
        Assert(negative.Positions.All(p=>p.X<=5)&&positive.Positions.All(p=>p.X>=5)&&negative.Normals.All(n=>n.Z>0),"Display clipping changed side, transform or winding.");
        var cube=Mesh.Cube;var originalVertices=cube.Vertices;var cut=DisplayCutaway.Build(cube,Affine.Identity,new(0,10,false));
        Near(DisplayArea(cut),1200);Assert(cut.IsFrozen&&cut.Positions.All(p=>p.X<=10)&&cube.Vertices==originalVertices,"Cutaway fabricated a cap or changed source geometry.");
        Assert(DisplayCutaway.Build(cube,Affine.Identity,new(0,-1,false)).TriangleIndices.Count==0,"Excluded display half-space should be empty.");
        Pass("Display-only half-space clipping preserves transformed actual triangle planes/winding, interpolates crossings and adds no cap or source mutation");

        await Open("cube-20mm.stl");var before=editor.Snapshot;var target=before.SelectedObjectId!.Value;
        Click(Named<Button>("HollowButton"));Assert(Named<TextBox>("HollowThickness").Text=="2"&&!Named<Button>("ApplyHollowButton").IsEnabled,"Hollow default/review guard incorrect.");
        InspectionFooterInside("PreviewHollowButton","ApplyHollowButton","CancelHollowButton");Capture("hollow-settings-minimum",1040,680);
        await PreviewHollow();SameAccepted(before);var candidate=editor.Snapshot.Preview!;var result=candidate.Scene.Objects.Single();
        Assert(result.Id==target&&result.Name==before.Scene.Objects.Single().Name&&candidate.ResultObjectIds.SequenceEqual([target]),"Hollow preview changed target identity/name.");
        var comparison=candidate.Hollow!;Assert(comparison.RemovedVolumeCubicMillimeters>0&&comparison.CavityCount>=1,"Preview has no actual cavity.");
        var geometry=DrawnObject(target);Assert(geometry.Positions.All(p=>p.X<=10+1e-8),"Default X-center cutaway was not rendered.");
        var inner=comparison.Cavity.Vertices.First(p=>p.X<10-1e-6);
        Assert(geometry.Positions.Any(p=>(p-new Point3D(inner.X,inner.Y,inner.Z)).Length<1e-6),"Cutaway omitted the actual inner cavity surface.");
        Assert(Named<TextBlock>("PreviewLabel").Text.Contains("inspection only")&&Named<TextBlock>("HollowSummary").Text.Contains("sealed"),"Display cutaway/sealed output is not labeled.");
        Capture("hollow-actual-cavity-minimum",1040,680);observations.Add(new{requestedWallMillimeters=comparison.RequestedWallThicknessMillimeters,maximumKernelRadiusMillimeters=comparison.MaximumKernelRadiusMillimeters,removedVolumeCubicMillimeters=comparison.RemovedVolumeCubicMillimeters,cavities=comparison.CavityCount});
        Pass("Hollow previews the real sealed cavity with stable identity, exact accepted-state preservation, useful labeled cutaway and fixed minimum-window actions");

        var coreMesh=result.Mesh;
        foreach(var direction in Enumerable.Range(0,6))
        {
            Named<ComboBox>("HollowCutDirection").SelectedIndex=direction;Named<Slider>("HollowCutPosition").Value=35;await HollowViewIdle();
            var shown=DrawnObject(target);var axis=direction/2;var plane=7d;var keepPositive=direction%2!=0;
            Assert(shown.Positions.All(p=>{var coordinate=axis switch{0=>p.X,1=>p.Y,_=>p.Z};return keepPositive?coordinate>=plane-1e-7:coordinate<=plane+1e-7;}),$"Inspection direction/position did not clip the actual mesh: direction={direction}, expected plane={plane}, displayed={Named<TextBlock>("HollowSectionLabel").Text}, min={shown.Positions.Min(p=>axis==0?p.X:axis==1?p.Y:p.Z)}, max={shown.Positions.Max(p=>axis==0?p.X:axis==1?p.Y:p.Z)}.");
            Assert(editor.Snapshot.Preview?.Id==candidate.Id&&ReferenceEquals(editor.Snapshot.Preview.Scene.Objects.Single().Mesh,coreMesh)&&Named<Button>("ApplyHollowButton").IsEnabled,"View controls dirtied or replaced the Core candidate.");SameAccepted(before);
        }
        Named<CheckBox>("HollowCutaway").IsChecked=false;await HollowViewIdle();
        Assert(DrawnObject(target).TriangleIndices.Count==coreMesh.Triangles.Length*3,"Turning cutaway off did not restore full candidate display.");
        Named<CheckBox>("HollowCutaway").IsChecked=true;await HollowViewIdle();var cached=DrawnObject(target);
        Click("Top");Assert(ReferenceEquals(cached,DrawnObject(target)),"Camera change rebuilt or changed the inspection mesh.");SameAccepted(before);
        Click(Named<Button>("ApplyHollowButton"));Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,coreMesh)&&editor.Snapshot.Preview==null,"Apply accepted an inspection cut instead of the full shell.");
        Assert(DrawnObject(target).TriangleIndices.Count==coreMesh.Triangles.Length*3,"Apply kept the cutaway display.");
        Ok(await editor.ExportStlAsync(Path.Combine(output,"ui-hollow-full-shell.stl")));Ok(await editor.ExportSelectionAsync(Path.Combine(output,"ui-hollow-full-shell.3mf"),[target]));
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Hollow Undo did not restore exact scene/selection.");
        Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,coreMesh),"Hollow Redo changed accepted shell.");Click(Named<Button>("UndoButton"));
        Pass("Cutaway axis/side/position and camera are presentation-only; disabling/Apply restores full geometry and saved shell remains intact through Undo/Redo");

        before=editor.Snapshot;Click(Named<Button>("HollowButton"));await PreviewHollow();candidate=editor.Snapshot.Preview!;
        Named<TextBox>("HollowThickness").Text="NaN";Assert(!Named<Button>("ApplyHollowButton").IsEnabled,"Invalid thickness allowed stale Apply.");Invoke("ApplyHollowClick",window,new RoutedEventArgs());SameAccepted(before);
        Click(Named<Button>("PreviewHollowButton"));await Idle();Assert(editor.Snapshot.Preview?.Id==candidate.Id,"Invalid draft discarded prior Hollow candidate.");
        Named<TextBox>("HollowThickness").Text="1000";Click(Named<Button>("PreviewHollowButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview?.Id==candidate.Id&&!Named<Button>("ApplyHollowButton").IsEnabled,"Empty-cavity failure changed candidate or accepted stale settings.");
        Named<TextBox>("HollowThickness").Text="3";await PreviewHollow();Assert(editor.Snapshot.Preview!.Id!=candidate.Id&&editor.Snapshot.Preview.ResultObjectIds.SequenceEqual([target]),"Replacement Hollow token/identity incorrect.");
        Named<Slider>("HollowCutPosition").Value=70;Click(Named<Button>("CancelHollowButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview==null&&DrawnObject(target).TriangleIndices.Count==before.Scene.Objects.Single().Mesh.Triangles.Length*3&&typeof(MainWindow).GetField("hollowDisplayGeometry",PrivateInstance)!.GetValue(window)==null,"Cancel left a clipped display/cache or stale queued view update.");
        Click(Named<Button>("HollowButton"));Click(Named<Button>("PreviewHollowButton"));Named<TextBox>("HollowThickness").Text="4";await Idle();
        Assert(!Named<Button>("ApplyHollowButton").IsEnabled,"In-flight thickness edit accepted old geometry.");Click(Named<Button>("CancelHollowButton"));SameAccepted(before);
        Click(Named<Button>("HollowButton"));Click(Named<Button>("PreviewHollowButton"));Click(Named<Button>("CancelHollowButton"));await Idle();SameAccepted(before);
        Pass("Thickness drafts and empty-cavity failures retain prior preview without stale acceptance; queued-view and in-flight cancel restore accepted geometry/history");

        Click(Named<Button>("HollowButton"));await PreviewHollow();var thickness=Named<TextBox>("HollowThickness");
        Assert(thickness.IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(thickness),"Hollow thickness lost local Undo/IME.");CheckLocalTextUndo(thickness,"2","3");SameAccepted(before);
        Invoke("NewTextClick",window,new RoutedEventArgs());Invoke("ImageReliefClick",window,new RoutedEventArgs());Invoke("RepairClick",window,new RoutedEventArgs());Invoke("MoveHandleClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());SameAccepted(before);
        Assert(Named<FrameworkElement>("HollowActions").Visibility==Visibility.Visible&&Named<FrameworkElement>("ConstructionActions").Visibility==Visibility.Collapsed,"Conflicting tool replaced Hollow.");
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-hollow-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewHollowButton","ApplyHollowButton","CancelHollowButton"})
            {var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted Hollow footer.");}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelHollowButton").RaiseEvent(escape);Assert(escape.Handled,"Hollow Escape was not handled.");
        }
        SameAccepted(before);await Open("missing-top.stl");before=editor.Snapshot;Click(Named<Button>("HollowButton"));Click(Named<Button>("PreviewHollowButton"));await Idle();
        Assert(editor.Snapshot.Preview==null&&!Named<Button>("ApplyHollowButton").IsEnabled,"Invalid open source produced an accepted cavity.");SameAccepted(before);Click(Named<Button>("CancelHollowButton"));
        Pass("Hollow retains ordinary local text Undo/IME and footer Enter/Escape, guards competing edits and rejects unsupported open meshes without acceptance");

        await Open("assembly-mm.3mf");var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();Ok(await editor.PreviewArrangementAsync(new GroupRequest(ids)));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();
        Assert(!Named<Button>("HollowButton").IsEnabled,"Whole group incorrectly enabled single-target Hollow.");var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.EnterGroup(group.Id,ids[0]));Refresh();before=editor.Snapshot;
        var sibling=before.Scene.Objects.Single(o=>o.Id==ids[1]);Click(Named<Button>("HollowButton"));await PreviewHollow();candidate=editor.Snapshot.Preview!;
        Assert(candidate.EditingGroupId==group.Id&&candidate.ResultObjectIds.SequenceEqual([ids[0]])&&ReferenceEquals(candidate.Scene.Objects.Single(o=>o.Id==ids[1]),sibling),"Scoped Hollow lost member/sibling isolation.");
        Click(Named<Button>("ApplyHollowButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single()==group&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Hollow acceptance lost scope/group/selection.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.EditingGroupId==group.Id,"Member Hollow Undo lost exact scene/scope.");Ok(editor.ExitGroup());Refresh();
        Pass("Hollow requires one independent leaf or explicit scoped member and preserves identity, siblings, group metadata and scoped Undo");
    }
}
