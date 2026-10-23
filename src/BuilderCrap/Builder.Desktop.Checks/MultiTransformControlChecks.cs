using Builder.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static async Task MultiTransformControls()
    {
        await Open("assembly-mm.3mf");
        var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToArray();
        Invoke("SelectViewportObject",ids[1],ModifierKeys.Control); Refresh();
        Assert(editor.Snapshot.SelectedObjectIds.Length==2,"Ctrl viewport selection did not add.");
        Invoke("SelectViewportObject",ids[1],ModifierKeys.Control); Refresh();
        Assert(editor.Snapshot.SelectedObjectIds.SequenceEqual(new[]{ids[0]}),"Ctrl viewport selection did not toggle off.");
        Invoke("SelectViewportObject",ids[1],ModifierKeys.Shift); Refresh();
        Assert(editor.Snapshot.SelectedObjectIds.Length==2,"Shift viewport selection did not add.");
        Assert(Named<Button>("DuplicateButton").IsEnabled && Named<Button>("DeleteButton").IsEnabled && !Named<Button>("RepairButton").IsEnabled,"Whole-selection commands or singleton repair availability is incorrect.");
        Click("Top"); Click(Named<Button>("MoveHandleButton")); Named<CheckBox>("SnapTransforms").IsChecked=true; var before=editor.Snapshot;
        Assert(!Named<Button>("GroupButton").IsEnabled,"Group button enabled during transform.");
        await DragAxis(0,4.4); SameAccepted(before);
        foreach(var obj in editor.Snapshot.Preview!.Scene.Objects)
            Near(obj.Bounds.Center.X,before.Scene.Objects.Single(o=>o.Id==obj.Id).Bounds.Center.X+4);
        Assert(((TransformRequest)editor.Snapshot.Preview.Request).ObjectIds.Length==2,"Handle target set omitted selection.");
        Near(double.Parse(Named<TextBox>("PositionX").Text),before.Scene.Bounds.Center.X+4);
        Capture("27-multiselect-move",1040,680); Click(Named<Button>("CancelTransformButton")); SameAccepted(before);
        Pass("Viewport Ctrl toggle/Shift add route to Core; multi-selection handles/fields use aggregate bounds and cancel safely");

        Click(Named<Button>("GroupButton")); Click(Named<Button>("PreviewArrangementButton")); await Idle(); Click(Named<Button>("ApplyArrangementButton"));
        var grouped=editor.Snapshot;
        Invoke("SelectViewportObject",ids[1],ModifierKeys.None); Refresh();
        Assert(editor.Snapshot.SelectedObjectIds.Length==2,"Group member viewport pick did not expand.");
        Assert(!Named<Button>("SplitButton").IsEnabled && !Named<Button>("RepairButton").IsEnabled && Named<Button>("DeleteButton").IsEnabled,"Grouped singleton tools or whole-group delete availability is incorrect.");
        Click(Named<Button>("RotateHandleButton")); before=editor.Snapshot;
        await DragAxis(2,90); SameAccepted(before);
        Near(editor.Snapshot.Preview!.Scene.Bounds.Size.X,20); Near(editor.Snapshot.Preview.Scene.Bounds.Size.Y,50);
        Capture("28-group-rotate",1040,680); Click(Named<Button>("ApplyTransformButton"));
        Assert(editor.Snapshot.Scene.Groups.Single()==grouped.Scene.Groups.Single(),"Group transform changed metadata.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene && editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Group rotate Undo lost scene/selection.");
        Click(Named<Button>("ScaleHandleButton")); Named<CheckBox>("UniformScale").IsChecked=true; before=editor.Snapshot;
        await DragAxis(0,6.25); SameAccepted(before);
        Near(editor.Snapshot.Preview!.Scene.Bounds.Size.X,62.5); Near(editor.Snapshot.Preview.Scene.Bounds.Size.Y,25);
        Click(Named<Button>("ApplyTransformButton"));
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"Group scale Undo failed.");
        Pass("Group member picks, aggregate-pivot rotation/scale handles, protected singleton tools and full-selection Undo");
    }
}
