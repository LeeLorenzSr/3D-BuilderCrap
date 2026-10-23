using Builder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ScaleRequest=Builder.Core.ScaleTransform;

internal static partial class Program
{
    private static async Task RunLazyTransformFieldControls()
    {
        await Open("assembly-mm.3mf");Click(Named<Button>("SelectAllButton"));
        Click(Named<Button>("GroupButton"));Click(Named<Button>("PreviewArrangementButton"));await Idle();Click(Named<Button>("ApplyArrangementButton"));
        var group=editor.Snapshot.Scene.Groups.Single();
        SetTransformTab(0);Click(Named<Button>("CancelTransformButton"));
        Click(Named<Button>("EditMembersButton"));ChooseTreeRoot(group.ObjectIds[1]);
        SetTransformTab(1);AssertVisibleTransformVector(1,new(40,10,10));Click(Named<Button>("CancelTransformButton"));
        Click(Named<Button>("DoneEditingButton"));
        AssertAllCurrentTransformTabs(new(50,20,20),new(25,10,10));
        Click(Named<Button>("EditMembersButton"));ChooseTreeRoot(group.ObjectIds[1]);
        AssertAllCurrentTransformTabs(new(20,20,20),new(40,10,10));
        var accepted=editor.Snapshot;
        async Task PreviewMove()
        {
            Click(Named<Button>("MoveHandleButton"));Named<CheckBox>("SnapTransforms").IsChecked=false;
            Named<TextBox>("PositionX").Text=(-3.25).ToString(CultureInfo.CurrentCulture);
            Named<TextBox>("PositionY").Text=8.5.ToString(CultureInfo.CurrentCulture);
            Named<TextBox>("PositionZ").Text=11.75.ToString(CultureInfo.CurrentCulture);
            Click(Named<Button>("PreviewTransformButton"));await Idle();SameAccepted(accepted);
            AssertVisibleTransformVector(1,new(-3.25,8.5,11.75));
        }
        await PreviewMove();Click(Named<Button>("CancelTransformButton"));SameAccepted(accepted);
        AssertAllCurrentTransformTabs(new(20,20,20),new(40,10,10));
        await PreviewMove();Click(Named<Button>("ApplyTransformButton"));
        AssertAllCurrentTransformTabs(new(20,20,20),new(-3.25,8.5,11.75));
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Member move Undo lost exact accepted source.");
        AssertAllCurrentTransformTabs(new(20,20,20),new(40,10,10));
        Click(Named<Button>("DoneEditingButton"));
        Pass("Done, member selection, preview Cancel and Apply/Undo initialize each newly visible transform tab from current accepted geometry");

        await Open("cube-20mm.stl");var beforeDraft=editor.Snapshot;
        SetTransformTab(0);var size=Named<TextBox>("SizeX");size.Text="31.25";size.Select(1,2);
        SetTransformTab(1);Named<TextBox>("PositionX").Text="-11.5";
        SetTransformTab(2);Named<TextBox>("RotationZ").Text="45";
        SetTransformTab(0);Assert(size.Text=="31.25" && size.SelectionStart==1 && size.SelectionLength==2,"Returning to a clean size tab overwrote its draft or selection.");
        SetTransformTab(1);Assert(Named<TextBox>("PositionX").Text=="-11.5","Returning to Move overwrote its unpreviewed draft.");
        SetTransformTab(2);Assert(Named<TextBox>("RotationZ").Text=="45","Returning to Rotate overwrote its unpreviewed draft.");
        SameAccepted(beforeDraft);Assert(editor.Snapshot.Preview==null,"Tab navigation created an unsolicited preview.");
        Click(Named<Button>("CancelTransformButton"));SameAccepted(beforeDraft);
        AssertAllCurrentTransformTabs(new(20,20,20),new(10,10,10));
        Pass("Unpreviewed per-mode drafts and text selection survive tab return; Cancel resets every later-visible tab without accepting geometry");

        await Open("assembly-mm.3mf");var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToArray();
        SetTransformTab(1);AssertVisibleTransformVector(1,new(10,10,10));Click(Named<Button>("CancelTransformButton"));
        SetTransformTab(0);Click(Named<Button>("CancelTransformButton"));ChooseTreeRoot(ids[1]);
        var beforeLegacy=editor.Snapshot;
        Invoke("MoveClick",window,new RoutedEventArgs());await Idle();SameAccepted(beforeLegacy);
        Assert(editor.Snapshot.Preview?.Request is MoveTransform move && move.DeltaMillimeters==new Vec3(),"Direct legacy Move read a stale hidden center.");
        AssertVisibleTransformVector(1,new(40,10,10));Click(Named<Button>("CancelTransformButton"));
        // An accepted adapter edit arrives while Size is hidden; its old 20 mm text must not be read.
        Ok(editor.Execute(new ResizeObject(ids[1],new(30,40,50))));Refresh();beforeLegacy=editor.Snapshot;
        Invoke("ResizeClick",window,new RoutedEventArgs());await Idle();SameAccepted(beforeLegacy);
        Assert(editor.Snapshot.Preview?.Request is ScaleRequest scale && scale.Factors==new Vec3(1,1,1),"Direct legacy Size read stale hidden dimensions.");
        AssertVisibleTransformVector(0,new(30,40,50));Click(Named<Button>("CancelTransformButton"));
        SetTransformTab(2);Named<TextBox>("RotationZ").Text="45";SetTransformTab(1);Click(Named<Button>("CancelTransformButton"));beforeLegacy=editor.Snapshot;
        Invoke("RotateClick",window,new RoutedEventArgs());await Idle();SameAccepted(beforeLegacy);
        Assert(editor.Snapshot.Preview?.Request is RotateTransform rotate && rotate.Degrees==new Vec3(),"Direct legacy Rotate read an invalidated hidden draft.");
        AssertVisibleTransformVector(2,new());Click(Named<Button>("CancelTransformButton"));
        Pass("Direct legacy Move/Size/Rotate handlers initialize invalidated hidden fields before reading, without changing accepted state");
    }

    private static void SetTransformTab(int index)
    {
        var tab=Named<TabControl>("TransformTabs");tab.SelectedIndex=index;
        // A selected tab can remain visible after Apply/Cancel; its tool button is the real entry route.
        var expected=index switch{0=>"Scale",1=>"Move",_=>"Rotate"};
        // On the tabbed shell an actual inspector-tab change already opens the
        // matching mode and hides the idle ribbon. Only idle/same-tab entry needs its button.
        if(typeof(Builder.Desktop.MainWindow).GetField("handleMode",PrivateInstance)!.GetValue(window)?.ToString()!=expected)
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
        Click(Named<Button>("CancelTransformButton"));SameAccepted(accepted);
    }
}
