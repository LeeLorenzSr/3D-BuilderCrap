using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private static async Task RunLazyTransformFieldControls()
    {
        await Open("assembly-mm.3mf");Click(Named<Button>("SelectAllButton"));
        Click(Named<Button>("GroupButton"));Click(Named<Button>("PreviewArrangementButton"));await Idle();Click(Named<Button>("ApplyArrangementButton"));
        var group=editor.Snapshot.Scene.Groups.Single();
        SetTransformTab(0);
        Click(Named<Button>("EditMembersButton"));ChooseTreeRoot(group.ObjectIds[1]);
        SetTransformTab(1);AssertVisibleTransformVector(1,new(40,10,10));
        Click(Named<Button>("DoneEditingButton"));
        AssertAllCurrentTransformTabs(new(50,20,20),new(25,10,10));
        Click(Named<Button>("EditMembersButton"));ChooseTreeRoot(group.ObjectIds[1]);
        AssertAllCurrentTransformTabs(new(20,20,20),new(40,10,10));
        var accepted=editor.Snapshot;
        void DraftMove()
        {
            Click(Named<Button>("MoveHandleButton"));Named<CheckBox>("SnapTransforms").IsChecked=false;
            Named<TextBox>("PositionX").Text=(-3.25).ToString(CultureInfo.CurrentCulture);
            Named<TextBox>("PositionY").Text=8.5.ToString(CultureInfo.CurrentCulture);
            Named<TextBox>("PositionZ").Text=11.75.ToString(CultureInfo.CurrentCulture);
            SameAccepted(accepted);
            AssertVisibleTransformVector(1,new(-3.25,8.5,11.75));
        }
        DraftMove();Click(Named<Button>("DiscardTransformButton"));SameAccepted(accepted);
        AssertAllCurrentTransformTabs(new(20,20,20),new(40,10,10));
        DraftMove();Click(Named<Button>("CommitTransformButton"));await Idle();
        AssertAllCurrentTransformTabs(new(20,20,20),new(-3.25,8.5,11.75));
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Member move Undo lost exact accepted source.");
        AssertAllCurrentTransformTabs(new(20,20,20),new(40,10,10));
        Click(Named<Button>("DoneEditingButton"));
        Pass("Done, member selection, draft Cancel and Apply/Undo initialize each newly visible transform mode from current accepted geometry");

        await Open("cube-20mm.stl");var beforeDraft=editor.Snapshot;
        SetTransformTab(0);Named<TextBox>("SizeX").Text="31.25";
        SetTransformTab(1);AssertVisibleTransformVector(1,new(10,10,10));
        SetTransformTab(0);AssertVisibleTransformVector(0,new(20,20,20));
        SameAccepted(beforeDraft);Assert(editor.Snapshot.Preview==null,"Switching a numeric draft created an unsolicited edit.");
        Pass("Switching transform modes restores accepted values and leaves the scene and history unchanged");

        await Open("assembly-mm.3mf");var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToArray();
        SetTransformTab(1);AssertVisibleTransformVector(1,new(10,10,10));
        SetTransformTab(0);ChooseTreeRoot(ids[1]);SetTransformTab(1);AssertVisibleTransformVector(1,new(40,10,10));
        Ok(editor.Execute(new ResizeObject(ids[1],new(30,40,50))));Refresh();
        var beforeSwitch=editor.Snapshot;
        SetTransformTab(0);AssertVisibleTransformVector(0,new(30,40,50));SameAccepted(beforeSwitch);
        SetTransformTab(2);AssertVisibleTransformVector(2,new());SameAccepted(beforeSwitch);
        Pass("New selection and adapter changes initialize position, dimensions and relative angles when choosing a tool");
    }

    private static void SetTransformTab(int index)
    {
        Click(Named<Button>(index switch{0=>"ScaleHandleButton",1=>"MoveHandleButton",_=>"RotateHandleButton"}));
        Layout(1040,680);
    }
    private static void AssertVisibleTransformVector(int index,Vec3 expected)
    {
        Assert(Named<TabControl>("TransformTabs").SelectedIndex==index,"Checked an inactive transform tab.");
        var prefix=index switch{0=>"Size",1=>"Position",_=>"Rotation"};
        Near(double.Parse(Named<TextBox>(prefix+"X").Text,CultureInfo.CurrentCulture),expected.X);
        Near(double.Parse(Named<TextBox>(prefix+"Y").Text,CultureInfo.CurrentCulture),expected.Y);
        Near(double.Parse(Named<TextBox>(prefix+"Z").Text,CultureInfo.CurrentCulture),expected.Z);
    }
    private static void AssertAllCurrentTransformTabs(Vec3 size,Vec3 center)
    {
        var accepted=editor.Snapshot;
        for(var index=0;index<3;index++)
        {SetTransformTab(index);AssertVisibleTransformVector(index,index switch{0=>size,1=>center,_=>new()});SameAccepted(accepted);}
        SameAccepted(accepted);
    }
}
