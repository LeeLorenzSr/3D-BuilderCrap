using Builder.Core;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;

internal static partial class Program
{
    private static async Task RunInputPolicyControls()
    {
        await Open("cube-20mm.stl");
        var numericNames=new[]{"SizeX","SizeY","SizeZ","PositionX","PositionY","PositionZ","RotationX","RotationY","RotationZ","PlaneOffset","SnapStep","ExtrudeDownPlaneHeight"};
        foreach(var name in numericNames)
        {
            var box=Named<TextBox>(name);
            Assert(box.IsUndoEnabled,"Numeric field lost local text undo: "+name);
        }
        foreach(var name in new[]{"ObjectName","GroupName"})
        {
            var box=Named<TextBox>(name);
            Assert(InputMethod.GetIsInputMethodEnabled(box) && !InputMethod.GetIsInputMethodSuspended(box),"Name field lost IME composition support: "+name);
            Assert(box.IsUndoEnabled,"Name field lost local text undo: "+name);
        }
        SetTransformTab(1);var accepted=editor.Snapshot;
        CheckLocalTextUndo(Named<TextBox>("PositionX"),"10","-12.375");SameAccepted(accepted);
        Click(Named<Button>("CancelTransformButton"));SameAccepted(accepted);
        CheckLocalTextUndo(Named<TextBox>("ObjectName"),"Part","部品 Δ");SameAccepted(accepted);
        Refresh();
        observations.Add(new{test="Numeric/name text editing preservation",numericFields=numericNames,nameFields=new[]{"ObjectName","GroupName"},localUndo="Explicit routed Undo target; no OS keyboard or IME composition claim"});
        Pass("Numeric fields retain local text undo while object/group names retain IME; routed text Undo preserves accepted Core state");

        // Read actual culture-formatted negative/fractional drafts through the same preview controls.
        var beforeMove=editor.Snapshot;
        async Task PreviewSignedMove()
        {
            Click(Named<Button>("MoveHandleButton"));Named<CheckBox>("SnapTransforms").IsChecked=false;
            Named<TextBox>("PositionX").Text=(-12.375).ToString(CultureInfo.CurrentCulture);
            Named<TextBox>("PositionY").Text=4.625.ToString(CultureInfo.CurrentCulture);
            Named<TextBox>("PositionZ").Text=(-.875).ToString(CultureInfo.CurrentCulture);
            Click(Named<Button>("PreviewTransformButton"));await Idle();SameAccepted(beforeMove);
            var center=editor.Snapshot.Preview!.Scene.Bounds.Center;
            Near(center.X,-12.375);Near(center.Y,4.625);Near(center.Z,-.875);
        }
        await PreviewSignedMove();Click(Named<Button>("CancelTransformButton"));SameAccepted(beforeMove);
        await PreviewSignedMove();Click(Named<Button>("ApplyTransformButton"));
        Near(editor.Snapshot.Scene.Bounds.Center.X,-12.375);
        Click(Named<Button>("UndoButton"));
        Assert(ReferenceEquals(editor.Snapshot.Scene,beforeMove.Scene) && editor.Snapshot.SelectedObjectIds.SequenceEqual(beforeMove.SelectedObjectIds) && editor.Snapshot.IsDirty==beforeMove.IsDirty,"Scene Undo after numeric input did not restore exact accepted state.");
        Pass("Signed fractional numeric input previews/cancels safely and Apply uses scene Undo independently of local text Undo");

        await Open("assembly-mm.3mf");Click(Named<Button>("SelectAllButton"));var ungrouped=editor.Snapshot;
        Click(Named<Button>("GroupButton"));Layout(1040,680);await Idle();
        const string groupName="組立 Δ • сборка";
        const string renamedGroup="印刷部品 Ω • группа";
        const string memberName="部品 α • деталь 😀";
        Named<TextBox>("GroupName").Text=groupName;
        CheckLocalTextUndo(Named<TextBox>("GroupName"),groupName,groupName+" draft");SameAccepted(ungrouped);
        Click(Named<Button>("PreviewArrangementButton"));await Idle();SameAccepted(ungrouped);
        Assert(editor.Snapshot.Preview!.Scene.Groups.Single().Name==groupName,"Unicode group draft was changed before acceptance.");
        Click(Named<Button>("ApplyArrangementButton"));var grouped=editor.Snapshot;var group=grouped.Scene.Groups.Single();
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,ungrouped.Scene),"Unicode grouping Undo lost independent source objects.");
        Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,grouped.Scene),"Unicode grouping Redo changed accepted content.");
        Named<TextBox>("ObjectName").Text=renamedGroup;Click(Named<Button>("RenameButton"));var renamed=editor.Snapshot;
        Assert(renamed.Scene.Groups.Single().Name==renamedGroup,"Unicode group rename was not accepted verbatim.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,grouped.Scene),"Unicode group rename Undo failed.");
        Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,renamed.Scene),"Unicode group rename Redo failed.");
        Click(Named<Button>("EditMembersButton"));var scoped=editor.Snapshot;var member=scoped.SelectedObjectId!.Value;
        Named<TextBox>("ObjectName").Text=memberName;Click(Named<Button>("RenameButton"));var memberRenamed=editor.Snapshot;
        Assert(memberRenamed.Scene.Objects.Single(o=>o.Id==member).Name==memberName && memberRenamed.Scene.Groups.Single().Name==renamedGroup,"Unicode member rename changed text or group ownership.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,scoped.Scene) && editor.Snapshot.EditingGroupId==group.Id,"Unicode member rename Undo lost scope/content.");
        Click(Named<Button>("RedoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,memberRenamed.Scene) && editor.Snapshot.EditingGroupId==group.Id,"Unicode member rename Redo lost scope/content.");
        Click(Named<Button>("DoneEditingButton"));
        var path=Path.Combine(output,"input-policy-unicode.3mf");Ok(await editor.SaveAsync(path));
        var reopened=new EditorSession();Ok(await reopened.OpenAsync(path));
        Assert(reopened.Snapshot.Scene.Groups.Single().Name==renamedGroup && reopened.Snapshot.Scene.Objects.Any(o=>o.Name==memberName),"Unicode names did not survive native-file roundtrip.");
        Pass("Unicode group creation and group/member rename preserve names through local text Undo, scene Undo/Redo and 3MF roundtrip");
    }

    private static void CheckLocalTextUndo(TextBox box,string original,string draft)
    {
        box.Text=original;box.SelectAll();
        using(box.DeclareChangeBlock()) box.SelectedText=draft;
        Assert(box.Text==draft && box.CanUndo && ApplicationCommands.Undo.CanExecute(null,box),$"Text edit did not expose local routed Undo: {box.Name}, text={box.Text}, CanUndo={box.CanUndo}, command={ApplicationCommands.Undo.CanExecute(null,box)}, enabled={box.IsEnabled}.");
        ApplicationCommands.Undo.Execute(null,box);
        Assert(box.Text==original,"Routed text Undo did not restore its own draft.");
    }
}
