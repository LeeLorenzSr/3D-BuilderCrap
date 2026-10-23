using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static async Task PreviewArray()
    {Click(Named<Button>("PreviewArrangementButton"));await Idle();Assert(editor.Snapshot.Preview?.Request is RectangularArrayRequest,"Array preview failed: "+Named<TextBlock>("Status").Text);}
    private static void ArrayNumber(string name,double value)=>Named<TextBox>(name).Text=value.ToString(CultureInfo.CurrentCulture);
    private static SceneObject[] ArrayCopies(EditorSnapshot accepted,GeometryPreview preview)
    {
        var originals=accepted.Scene.Objects.Select(o=>o.Id).ToHashSet();return preview.Scene.Objects.Where(o=>!originals.Contains(o.Id)).ToArray();
    }
    private static void CheckArrayCopies(EditorSnapshot accepted,GeometryPreview preview,Vec3[] cellSteps)
    {
        var selected=accepted.SelectedObjectIds.ToHashSet();var source=accepted.Scene.Objects.Where(o=>selected.Contains(o.Id)).ToArray();var copies=ArrayCopies(accepted,preview);
        Assert(copies.Length==source.Length*cellSteps.Length,"Array did not create the expected new leaf count.");
        foreach(var original in accepted.Scene.Objects)Assert(preview.Scene.Objects.Single(o=>o.Id==original.Id)==original,"Array mutated an original or unrelated object.");
        for(var cell=0;cell<cellSteps.Length;cell++)for(var i=0;i<source.Length;i++)
        {
            var original=source[i];var copy=copies[cell*source.Length+i];
            Assert(ReferenceEquals(copy.Mesh,original.Mesh)&&copy.Material==original.Material&&ReferenceEquals(copy.TriangleMaterials,original.TriangleMaterials)&&copy.Transform==original.Transform.Then(Affine.Translation(cellSteps[cell])),"Copied mesh/material/spacing differs from the reviewed source set.");
        }
        Assert(preview.Request is RectangularArrayRequest request&&request.ObjectIds.SequenceEqual(accepted.SelectedObjectIds),"Array request lost original source IDs to generated selection.");
        Assert(preview.SelectedObjectIds.SequenceEqual(copies.Select(o=>o.Id))&&preview.ResultObjectIds.SequenceEqual(preview.SelectedObjectIds),"Array candidate did not select only new copies.");
    }
    private static async Task RunRectangularArrayControls()
    {
        await OpenDistribution();Click(Named<Button>("ClearSelectionButton"));Assert(!Named<Button>("ArrayButton").IsEnabled,"Empty selection enables Array.");var one=editor.Snapshot.Scene.Objects.First();Ok(editor.Select(one.Id));Refresh();Assert(Named<Button>("ArrayButton").IsEnabled,"Single unit cannot start Array.");Click(Named<Button>("SelectAllButton"));
        Layout(1040,680);foreach(var name in new[]{"ArrayButton","DuplicateButton","DeleteButton","GroupButton","UngroupButton","AlignButton","DistributeButton","EditMembersButton","MirrorButton","MeasureButton"})Reachable(name);Capture("array-entry-minimum",1040,680);
        var accepted=editor.Snapshot;var size=accepted.Scene.Bounds.Size;Click(Named<Button>("ArrayButton"));Assert(Named<TextBox>("ArrayCountX").Text=="2"&&Named<TextBox>("ArrayCountY").Text=="1"&&Named<TextBox>("ArrayCountZ").Text=="1","Array counts do not include the original by default.");
        Near(double.Parse(Named<TextBox>("ArraySpacingX").Text,CultureInfo.CurrentCulture),size.X+5,.001);Near(double.Parse(Named<TextBox>("ArraySpacingY").Text,CultureInfo.CurrentCulture),size.Y+5,.001);Near(double.Parse(Named<TextBox>("ArraySpacingZ").Text,CultureInfo.CurrentCulture),size.Z+5,.001);
        var camera=CameraPose();await PreviewArray();var preview=editor.Snapshot.Preview!;CheckArrayCopies(accepted,preview,[new(size.X+5,0,0)]);ArrangementUnchanged(accepted);Assert(CameraPose()==camera,"Array preview auto-framed the camera.");ArrangementFooterVisible();foreach(var name in new[]{"ArrayCountX","ArrayCountY","ArrayCountZ","ArraySpacingX","ArraySpacingY","ArraySpacingZ"}){Reachable(name);Assert(Named<TextBox>(name).IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(Named<TextBox>(name)),"Array field changed local Undo/IME policy.");}
        Assert(Named<TextBlock>("ArrangementReview").Text.Contains("not an edge gap")&&Named<TextBlock>("ArrangementReview").Text.Contains("include the original"),"Array help hides step/count semantics.");
        using(var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-array-fit"){Width=1,Height=1,WindowStyle=0}))
        {Keyboard.ClearFocus();var fit=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.F){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=Named<FrameworkElement>("ViewportSurface")};Named<FrameworkElement>("ViewportSurface").RaiseEvent(fit);Assert(fit.Handled,"Array F did not reach fit command.");CameraContains(preview.Scene.Bounds);}
        Capture("array-default-preview-minimum",1040,680);Click(Named<Button>("CancelArrangementButton"));ArrangementUnchanged(accepted);
        Click(Named<Button>("ArrayButton"));ArrayNumber("ArrayCountX",1);foreach(var name in new[]{"ArraySpacingX","ArraySpacingY","ArraySpacingZ"})ArrayNumber(name,0);await PreviewArray();Assert(ReferenceEquals(editor.Snapshot.Preview!.Scene,accepted.Scene),"All-one array fabricated an edit.");Click(Named<Button>("ApplyArrangementButton"));ArrangementUnchanged(accepted);
        Pass("Array is discoverable beside Duplicate at minimum width, uses count2/1/1 and bounds+5mm defaults, preserves camera until F, retains text policy and treats all-one counts as an exact no-op");

        await OpenDistribution();accepted=editor.Snapshot;Click(Named<Button>("ArrayButton"));await PreviewArray();preview=editor.Snapshot.Preview!;ArrayNumber("ArrayCountX",0);Assert(!Named<Button>("ApplyArrangementButton").IsEnabled,"Changed count can apply stale copies.");Click(Named<Button>("PreviewArrangementButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,preview),"Invalid count replaced candidate.");ArrayNumber("ArrayCountX",2);ArrayNumber("ArraySpacingX",0);Click(Named<Button>("PreviewArrangementButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,preview)&&!Named<Button>("ApplyArrangementButton").IsEnabled,"Active zero spacing replaced candidate or enabled stale Apply.");
        ArrayNumber("ArraySpacingX",125);ArrayNumber("ArrayCountX",1001);Click(Named<Button>("PreviewArrangementButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Preview,preview)&&!Named<Button>("ApplyArrangementButton").IsEnabled,"Refused scene limit lost candidate or enabled Apply.");ArrangementUnchanged(accepted);camera=CameraPose();await Workspace(4);ViewToggle("ViewGrid",false);Assert(ReferenceEquals(editor.Snapshot.Preview,preview)&&Named<TextBox>("ArrayCountX").Text=="1001"&&CameraPose()==camera,"View changed array draft/candidate/camera.");ArrangementFooterVisible();Capture("array-refused-limit-view-minimum",1040,680);
        await Workspace(1);ArrayNumber("ArrayCountX",2);ArrayNumber("ArraySpacingX",-125.5);await PreviewArray();Assert(editor.Snapshot.Preview!.Id!=preview.Id&&Named<Button>("ApplyArrangementButton").IsEnabled,"Valid repreview did not renew ownership.");CheckArrayCopies(accepted,editor.Snapshot.Preview!,[new(-125.5,0,0)]);Click(Named<Button>("CancelArrangementButton"));ArrangementUnchanged(accepted);
        Pass("Count/step drafts invalidate Apply; invalid count, active zero and refused limits preserve the prior candidate, View retains drafts/token, and signed fractional repreview cancels safely");

        await OpenDistribution();accepted=editor.Snapshot;Click(Named<Button>("ArrayButton"));ArrayNumber("ArrayCountY",2);ArrayNumber("ArraySpacingY",-47);await PreviewArray();preview=editor.Snapshot.Preview!;CheckArrayCopies(accepted,preview,[new(125,0,0),new(0,-47,0),new(125,-47,0)]);Assert(preview.Scene.Groups.Length==accepted.Scene.Groups.Length*4,"Whole-group array flattened or omitted group copies.");
        var originalGroup=accepted.Scene.Groups.Single();var newIds=ArrayCopies(accepted,preview).Select(o=>o.Id).ToHashSet();Assert(preview.Scene.Groups.Where(g=>g.Id!=originalGroup.Id).All(g=>g.ObjectIds.Length==originalGroup.ObjectIds.Length&&g.ObjectIds.All(newIds.Contains)),"Copied groups own original or missing leaf IDs.");
        await Workspace(4);Click(Named<Button>("FitSceneButton"));Capture("array-grid-groups-preview",1040,680);Click(Named<Button>("ApplyArrangementButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,preview.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(preview.SelectedObjectIds),"Apply did not select only reviewed new copies.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds),"One Undo did not restore original scene/selection.");Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,preview.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(preview.SelectedObjectIds),"Redo lost copied groups/selection.");
        Pass("A signed2×2 grid repeats the selected set with materials and whole groups intact; Apply selects only new copies, and one Undo/Redo restores exact scene and selection");

        await OpenDistribution();var group=editor.Snapshot.Scene.Groups.Single();Ok(editor.SelectMany(group.ObjectIds));Refresh();Click(Named<Button>("EditMembersButton"));Click(Named<Button>("SelectAllButton"));accepted=editor.Snapshot;Click(Named<Button>("ArrayButton"));CheckLocalTextUndo(Named<TextBox>("ArraySpacingX"),"21","-21.5");ArrangementUnchanged(accepted);await PreviewArray();preview=editor.Snapshot.Preview!;CheckArrayCopies(accepted,preview,[new(21,0,0)]);Assert(preview.Request.MemberGroupId==group.Id&&preview.Scene.Groups.Single().ObjectIds.Length==group.ObjectIds.Length*2,"Member array did not append copies to captured group.");ArrangementFooterVisible();MemberScopeVisible();Capture("array-members-minimum",1040,680);
        using(var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-array-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewArrangementButton","ApplyArrangementButton","CancelArrangementButton","ArrayCountX","ArraySpacingX"})
            {var control=Named<Control>(name);var enter=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=control};control.RaiseEvent(enter);Assert(!enter.Handled&&ReferenceEquals(editor.Snapshot.Preview,preview),"Global Enter intercepted array control: "+name);ArrangementUnchanged(accepted);}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelArrangementButton").RaiseEvent(escape);Assert(escape.Handled,"Array did not handle Escape.");
        }
        ArrangementUnchanged(accepted);Click(Named<Button>("ArrayButton"));await PreviewArray();preview=editor.Snapshot.Preview!;Click(Named<Button>("ApplyArrangementButton"));Assert(editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.Scene.Groups.Single().ObjectIds.Length==6&&editor.Snapshot.SelectedObjectIds.SequenceEqual(preview.SelectedObjectIds),"Member Apply lost group/scope/new-copy selection.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&editor.Snapshot.EditingGroupId==group.Id&&editor.Snapshot.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds),"Member array Undo lost original scene/scope/selection.");
        Pass("Member arrays append independent copies inside the same group; local text Undo and routed footer Enter/Escape remain safe, and member Apply/Undo preserves original ownership and scope");
    }
}
