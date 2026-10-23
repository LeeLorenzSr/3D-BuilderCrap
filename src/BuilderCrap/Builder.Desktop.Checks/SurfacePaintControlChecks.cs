using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

internal static partial class Program
{
    private static async Task OpenSurfacePaint(string file="surface-cube.3mf")
    {
        Ok(await editor.OpenAsync(Path.Combine(root,".local/evidence/surface-paint-20261021/inputs",file),decision:DirtyDecision.Discard));Refresh();
        await Workspace(4);Click(Named<Button>("ViewIsometricButton"));await Workspace(3);
    }
    private static async Task PaintWorld(Vec3 point)
    {
        await Idle(); // Tool entry changes viewport bounds; wait for the real layout before projecting the click.
        await (Task)Invoke("PickSurfaceMaterialAt",ProjectMeasuredWorld(point))!;await Idle();
        Assert(editor.Snapshot.Preview is {Request:SurfaceMaterialRequest,SurfaceMaterial:not null},"Surface click did not preview: "+Named<TextBlock>("Status").Text);
    }
    private static void CheckRenderedSurface(SceneObject obj)
    {
        var source=obj.Mesh;var faces=source.Triangles.Select((t,i)=>(key:(source.Vertices[t.A],source.Vertices[t.B],source.Vertices[t.C]),i)).ToDictionary(p=>p.key,p=>p.i);
        var seen=new HashSet<int>();
        foreach(var part in SeparateDrawn().Where(p=>p.Value==obj.Id).Select(p=>p.Key))
        {
            var geometry=(MeshGeometry3D)part.Geometry;var actual=((SolidColorBrush)((DiffuseMaterial)part.Material).Brush).Color;
            for(var i=0;i<geometry.TriangleIndices.Count;i+=3)
            {
                Vec3 PointAt(int index){var p=geometry.Positions[geometry.TriangleIndices[index]];return new(p.X,p.Y,p.Z);}
                Assert(faces.TryGetValue((PointAt(i),PointAt(i+1),PointAt(i+2)),out var face)&&seen.Add(face),"Color batching changed or duplicated source triangles.");
                var expected=obj.GetTriangleMaterial(face)!.Color;Assert(actual==Color.FromRgb(expected.R,expected.G,expected.B),"Rendered surface RGB differs from Core effective material.");
            }
        }
        Assert(seen.Count==source.Triangles.Length,"Color batching omitted source triangles.");
    }
    private static async Task RunSurfacePaintControls()
    {
        await OpenSurfacePaint();var before=editor.Snapshot;var source=before.Scene.Objects.Single();CheckRenderedSurface(source);Reachable("SurfaceMaterialButton");
        Assert(Named<Canvas>("MaterialSelectionOverlay").Children.Count>0,"Painted selection lost independent outline.");Click(Named<Button>("SurfaceMaterialButton"));
        Assert(!Named<Button>("PreviewMaterialButton").IsEnabled&&!Named<Button>("ApplyMaterialButton").IsEnabled,"Surface review enabled without a picked seed.");
        Named<TextBox>("MaterialHex").Text="#5F9B64";Named<TextBox>("MaterialName").Text="Green top";var camera=CameraPose();await PaintWorld(new(6,11,20));SameAccepted(before);var candidate=editor.Snapshot.Preview!;
        Assert(candidate.SurfaceMaterial!.TriangleIndices.SequenceEqual([2,3])&&candidate.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Flat click did not choose exactly the top region or changed selection.");
        var painted=candidate.Scene.Objects.Single();Assert(ReferenceEquals(painted.Mesh,source.Mesh)&&painted.Transform==source.Transform&&painted.Id==source.Id,"Surface color changed geometry/identity.");CheckRenderedSurface(painted);
        InspectionFooterInside("PreviewMaterialButton","ApplyMaterialButton","CancelMaterialButton");Capture("surface-preview-minimum",1040,680);
        Assert(Named<TextBlock>("MaterialCurrent").Text.Contains("2 triangles")&&Named<Button>("ApplyMaterialButton").IsEnabled,"Surface target/count review or Apply missing.");
        Named<CheckBox>("MaterialShowOriginal").IsChecked=true;CheckRenderedSurface(source);Assert(CameraPose()==camera&&ReferenceEquals(editor.Snapshot.Preview,candidate),"Original comparison changed camera or candidate.");Named<CheckBox>("MaterialShowOriginal").IsChecked=false;CheckRenderedSurface(painted);
        await Workspace(4);Click(Named<Button>("ViewTopButton"));Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<FrameworkElement>("MaterialActions").Visibility==Visibility.Visible,"View discarded surface review.");await Workspace(3);Click(Named<Button>("CancelMaterialButton"));SameAccepted(before);
        Pass("Actual Paint entry and Core ray pick preview a two-triangle flat region; disjoint exact-color rendering, selection outline, minimum footer and original/View comparison preserve geometry and selection");

        await OpenSurfacePaint();before=editor.Snapshot;source=before.Scene.Objects.Single();Click(Named<Button>("SurfaceMaterialButton"));Named<TextBox>("MaterialHex").Text="#5F9B64";await PaintWorld(new(6,11,20));candidate=editor.Snapshot.Preview!;
        Named<TextBox>("MaterialHex").Text="#bad";Assert(!Named<Button>("ApplyMaterialButton").IsEnabled,"Changed surface color allowed stale Apply.");Invoke("ApplyMaterialClick",window,new RoutedEventArgs());Click(Named<Button>("PreviewMaterialButton"));await Idle();SameAccepted(before);Assert(ReferenceEquals(editor.Snapshot.Preview,candidate),"Invalid surface draft erased prior preview.");
        Named<TextBox>("MaterialHex").Text="#D65248";Click(Named<Button>("PreviewMaterialButton"));await Idle();Assert(editor.Snapshot.Preview!.Id!=candidate.Id,"Surface color replacement reused token.");
        await PaintWorld(new(20,8,6));candidate=editor.Snapshot.Preview!;Assert(candidate.SurfaceMaterial!.TriangleIndices.SequenceEqual([6,7]),"Another click did not replace the seed region on the same object.");
        Assert(candidate.Scene.Objects.Single().GetTriangleMaterial(2)==source.GetTriangleMaterial(2),"Second region accumulated an unaccepted first stroke.");CheckRenderedSurface(candidate.Scene.Objects.Single());
        await (Task)Invoke("PickSurfaceMaterialAt",new Point(0,0))!;Assert(ReferenceEquals(editor.Snapshot.Preview,candidate),"A missed click discarded surface review.");Click(Named<Button>("CancelMaterialButton"));SameAccepted(before);
        Pass("Color edits/invalid values cannot stale-Apply; Preview and same-object clicks replace the exact owned region without accumulating unaccepted strokes, and misses/Cancel retain accepted work");

        await OpenSurfacePaint();before=editor.Snapshot;source=before.Scene.Objects.Single();Click(Named<Button>("SurfaceMaterialButton"));Named<TextBox>("MaterialHex").Text="#5F9B64";Named<TextBox>("MaterialName").Text="Green top";await PaintWorld(new(6,11,20));candidate=editor.Snapshot.Preview!;
        Click(Named<Button>("ApplyMaterialButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,candidate.Scene),"Surface Apply did not accept exact candidate.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Surface Undo lost exact original.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,candidate.Scene),"Surface Redo changed reviewed colors.");CheckRenderedSurface(editor.Snapshot.Scene.Objects.Single());
        var saved=Path.Combine(output,"ui-surface-painted.3mf");Ok(await editor.SaveAsync(saved));var reopened=new EditorSession();Ok(await reopened.OpenAsync(saved));var reloaded=reopened.Snapshot.Scene.Objects.Single();Assert(reloaded.GetTriangleMaterial(2)!.Name=="Green top"&&reloaded.GetTriangleMaterial(6)==source.GetTriangleMaterial(6),"Surface 3MF lost changed or untouched colors.");
        Click(Named<Button>("SurfaceMaterialButton"));Click(Named<Button>("ClearMaterialButton"));await PaintWorld(new(6,11,20));Assert(editor.Snapshot.Preview!.Scene.Objects.Single().GetTriangleMaterial(2)==source.Material&&Named<TextBlock>("MaterialSummary").Text.Contains("base color"),"Clear surface did not return to base with clear review.");Click(Named<Button>("ApplyMaterialButton"));CheckRenderedSurface(editor.Snapshot.Scene.Objects.Single());
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,candidate.Scene),"Clear-region Undo lost painted scene.");Click(Named<Button>("MaterialButton"));Named<TextBox>("MaterialHex").Text="#9845CB";Click(Named<Button>("PreviewMaterialButton"));await Idle();Assert(editor.Snapshot.Preview!.Scene.Objects.Single().TriangleMaterials.IsEmpty,"Whole-object material did not replace surface overrides.");CheckRenderedSurface(editor.Snapshot.Preview.Scene.Objects.Single());Click(Named<Button>("CancelMaterialButton"));
        Pass("Apply/Undo/Redo and native 3MF save/reopen retain changed and untouched surface colors; Clear returns a region to base, while whole-object material explicitly replaces overrides");

        await OpenSurfacePaint("surface-group.3mf");before=editor.Snapshot;foreach(var obj in before.Scene.Objects)CheckRenderedSurface(obj);var group=before.Scene.Groups.Single();var target=before.Scene.Objects.First();var sibling=before.Scene.Objects.Last();
        Click(Named<Button>("SurfaceMaterialButton"));await Idle();await (Task)Invoke("PickSurfaceMaterialAt",ProjectMeasuredWorld(new(6,11,20)))!;Assert(editor.Snapshot.Preview==null&&Named<TextBlock>("Status").Text.Contains("Edit members"),"Scene group member was painted without explicit scope.");Click(Named<Button>("CancelMaterialButton"));SameAccepted(before);
        Ok(editor.EnterGroup(group.Id,target.Id));Refresh();before=editor.Snapshot;Click(Named<Button>("SurfaceMaterialButton"));Named<TextBox>("MaterialHex").Text="#E5C44D";await PaintWorld(new(6,11,20));candidate=editor.Snapshot.Preview!;
        Assert(((SurfaceMaterialRequest)candidate.Request).MemberGroupId==group.Id&&candidate.EditingGroupId==group.Id&&ReferenceEquals(candidate.Scene.Objects.Single(o=>o.Id==sibling.Id),sibling),"Member surface paint lost scope or touched sibling.");
        await (Task)Invoke("PickSurfaceMaterialAt",ProjectMeasuredWorld(new(36,11,20)))!;Assert(ReferenceEquals(editor.Snapshot.Preview,candidate),"Another object replaced the current surface target.");
        Click(Named<Button>("ApplyMaterialButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&editor.Snapshot.Scene.Groups.Single()==group,"Member Apply changed group/selection/scope.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Member surface Undo lost exact original.");Click(Named<Button>("DoneEditingButton"));
        Pass("Per-face 3MF objects render independently; grouped surfaces require Edit members, sibling picks cannot change the owned target, and scoped Apply/Undo preserves group, full selection and siblings");

        await Open("cube-20mm.stl");await Workspace(4);Click(Named<Button>("ViewIsometricButton"));await Workspace(3);before=editor.Snapshot;Click(Named<Button>("SurfaceMaterialButton"));
        CheckLocalTextUndo(Named<TextBox>("MaterialHex"),"#267CB5","#112233");CheckLocalTextUndo(Named<TextBox>("MaterialName"),"Blue","青 Δ");Assert(InputMethod.GetIsInputMethodEnabled(Named<TextBox>("MaterialName")),"Surface name lost IME.");await PaintWorld(new(6,11,20));candidate=editor.Snapshot.Preview!;
        painted=candidate.Scene.Objects.Single();Assert(painted.Material!.Color==new RgbColor(153,170,185),"Unassigned surface paint did not promote a neutral base.");CheckRenderedSurface(painted);SameAccepted(before);
        using(var pending=new CancellationTokenSource())
        {var field=typeof(Builder.Desktop.MainWindow).GetField("pendingPick",PrivateInstance)!;field.SetValue(window,pending);try{Invoke("ViewTopClick",window,new RoutedEventArgs());Assert(pending.IsCancellationRequested,"Camera transition did not cancel pending surface pick.");}finally{field.SetValue(window,null);}}
        Named<TextBox>("MaterialHex").Text="#E5C44D";Click(Named<Button>("PreviewMaterialButton"));Click(Named<Button>("CancelMaterialButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview==null,"In-flight paint Cancel left candidate.");
        Click(Named<Button>("SurfaceMaterialButton"));using(var keySource=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-surface-keys"){Width=1,Height=1,WindowStyle=0}))
        {foreach(var name in new[]{"PreviewMaterialButton","ApplyMaterialButton","CancelMaterialButton"}){var key=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted surface footer.");}var escape=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelMaterialButton").RaiseEvent(escape);Assert(escape.Handled,"Surface Escape not handled.");}
        SameAccepted(before);
        Pass("Surface inputs retain local Undo/IME, first paint gives an unassigned object a neutral base, camera changes cancel pending picks, and in-flight Cancel/standard Enter/Escape preserve accepted work");
    }
}
