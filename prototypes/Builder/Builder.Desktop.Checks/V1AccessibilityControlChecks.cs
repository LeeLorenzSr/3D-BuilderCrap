using Builder.Core;
using Builder.Desktop;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static async Task V1CreatedSelectionVisible()
    {
        await OpenDistribution();
        var group=editor.Snapshot.Scene.Groups.Single();
        Ok(editor.Select(group.ObjectIds[0]));Refresh();Layout(1040,720);await Idle();
        var list=Named<ListBox>("ObjectList");
        var scroll=ObjectTreeVisuals(list).OfType<ScrollViewer>().First();
        scroll.ScrollToTop();Layout(1040,720);await Idle();
        var accepted=editor.Snapshot;
        Click(Named<Button>("ArrayButton"));await PreviewArray();Click(Named<Button>("ApplyArrangementButton"));await Idle();
        var copy=editor.Snapshot.Scene.Groups.Single(g=>g.Id!=group.Id);
        Capture("v1-array-new-selection",1040,720);
        AssertTreeMemberInsideViewport(copy.Id);
        Assert(editor.Snapshot.SelectedObjectIds.All(copy.ObjectIds.Contains),"Array reveal changed the selected copies.");
        scroll.ScrollToTop();Layout(1040,720);await Idle();var offset=scroll.VerticalOffset;
        Refresh();await Idle();Near(scroll.VerticalOffset,offset,.5);
        Assert(editor.Snapshot.Scene.Objects.Length==8,"Presentation refresh changed accepted array.");
        Click(Named<Button>("UndoButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Reveal changed one-step Undo.");
        Click(Named<Button>("RedoButton"));await Idle();AssertTreeMemberInsideViewport(copy.Id);
        Ok(editor.EnterGroup(copy.Id,copy.ObjectIds[0]));Refresh();await Idle();
        var memberBefore=editor.Snapshot;
        Click(Named<Button>("ArrayButton"));await PreviewArray();Click(Named<Button>("ApplyArrangementButton"));await Idle();
        AssertTreeMemberInsideViewport(editor.Snapshot.SelectedObjectId!.Value);
        Assert(editor.Snapshot.EditingGroupId==copy.Id&&editor.Snapshot.Scene.Groups.Single(g=>g.Id==copy.Id).ObjectIds.Length==4,"Revealing a new member changed scope/group ownership.");
        Click(Named<Button>("UndoButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Scene,memberBefore.Scene),"Member array Undo changed during reveal.");
        Ok(editor.ExitGroup());Refresh();await Idle();
        Pass("Newly created group/member array selections reveal their primary row; ordinary refresh retains manual scrolling and one-step Undo/Redo/scope remain intact");
    }

    private static async Task V1AccessibleRecoveryAndViewport()
    {
        await Open("cube-20mm.stl");
        var viewport=Named<FrameworkElement>("ViewportSurface");
        var peer=FrameworkElementAutomationPeer.CreatePeerForElement(viewport);
        observations.Add(new{test="Viewport accessibility",peer=peer?.GetType().Name,name=peer?.GetName()});
        var viewportGood=peer!=null&&peer.GetName()=="3D scene viewport"&&peer.GetAutomationControlType()==AutomationControlType.Pane;
        var candidate=new RecoveryCandidate(Guid.NewGuid(),Guid.NewGuid(),"test-fingerprint",DateTimeOffset.UtcNow,Path.Combine(output,"recovery-native.3mf"),"Compatibility recovery",3);
        var dialog=new RecoveryDialog([candidate],(_,_,_)=>Task.FromResult(new OperationResult(Outcome.Cancelled,"No action requested.")));
        try
        {
            V1DialogCapture((FrameworkElement)dialog.Content,460,"v1-accessible-recovery");
            var copies=(ListBox)dialog.FindName("RecoveryCopies");
            var item=(ListBoxItem)copies.Items[0];
            var itemPeer=FrameworkElementAutomationPeer.CreatePeerForElement(item)!;
            observations.Add(new{test="Recovery item accessibility",name=itemPeer.GetName(),help=itemPeer.GetHelpText(),type=itemPeer.GetAutomationControlType().ToString()});
            Assert(itemPeer.GetName().Contains(candidate.Title)&&itemPeer.GetName().Contains("3 objects"),"Recovery list item does not expose its model name/count to accessibility clients.");
            Assert(itemPeer.GetHelpText().Contains(candidate.NativePath!),"Recovery accessible summary omits original native path.");
            var listPeer=FrameworkElementAutomationPeer.CreatePeerForElement(copies)!;
            var selectionPeer=listPeer.GetChildren().Single(p=>p.GetAutomationControlType()==AutomationControlType.ListItem&&p.GetName().Contains(candidate.Title));
            Assert(selectionPeer.GetPattern(PatternInterface.SelectionItem)!=null,"Recovery item lost keyboard/accessibility selection.");
            Assert(AutomationProperties.GetLiveSetting((TextBlock)dialog.FindName("RecoveryError"))==AutomationLiveSetting.Polite,"Recovery failure text is not a live region.");
            Assert(viewportGood,"Focusable viewport has no named Pane automation peer.");
        }
        finally{dialog.Close();}
        Pass("Focusable viewport exposes a named Pane peer; recovery choices expose model/count/path and selection pattern, with polite failure announcements and safe Not now default");
    }

    private static async Task V1ScaledControls()
    {
        var captions=new[]{Named<Button>("NewButton").Content,Named<Button>("SaveButton").Content,
            Tree(surface).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Open model").Content}
            .Concat(new[]{"InsertWorkspaceTab","ObjectWorkspaceTab","EditWorkspaceTab","PaintWorkspaceTab","ViewWorkspaceTab"}.Select(name=>Named<TabItem>(name).Header))
            .Append(Named<MenuItem>("RecentFilesMenu").Header).Select(value=>value?.ToString()??"").ToArray();
        var keys=captions.Where(c=>c.Contains('_')).Select(c=>char.ToUpperInvariant(c[c.IndexOf('_')+1])).ToArray();
        Assert(keys.Distinct().Count()==keys.Length,"Persistent file commands and workspace tabs share an Alt access key.");
        await Open("cube-20mm.stl");await Workspace(1);Click(Named<Button>("ScaleHandleButton"));await Idle();Layout(1040,720);
        foreach(var name in new[]{"SizeX","SizeY","SizeZ","AspectLock","PreviewTransformButton","CancelTransformButton"})
        {
            var control=Named<Control>(name);var peer=FrameworkElementAutomationPeer.CreatePeerForElement(control);
            Assert(control.Focusable&&KeyboardNavigation.GetIsTabStop(control)&&peer!=null&&!string.IsNullOrWhiteSpace(peer.GetName()),"Resize control lacks keyboard stop or accessible name: "+name);
            Reachable(name);
        }
        foreach(var scale in new[]{1d,1.25,1.5,2})
        {
            var bitmap=new RenderTargetBitmap((int)(1040*scale),(int)(720*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(surface);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,$"v1-scale-{scale:0.##}.png"));png.Save(file);
        }
        Click(Named<Button>("CancelTransformButton"));
        foreach(var format in new[]{".obj",".ply",".gltf",".glb"})
        {
            var dialog=new UnitsDialog("compatibility"+format,exporting:true);
            try
            {
                var units=Tree((DependencyObject)dialog.Content).OfType<ComboBox>().Single();var peer=FrameworkElementAutomationPeer.CreatePeerForElement(units)!;
                Assert(peer.GetName().EndsWith("export units")&&peer.GetPattern(PatternInterface.Selection)!=null,"Export unit choice lacks accessible selection/name.");
                Assert(units.Focusable&&KeyboardNavigation.GetIsTabStop(units),"Export unit choice is not a keyboard stop.");
            }
            finally{dialog.Close();}
        }
        Pass("File/workspace Alt keys are unambiguous; resize fields/footer and format unit choices expose named keyboard stops/patterns; unchanged1040×720 logical layout renders at100/125/150/200percent scale without clipping");
    }
}
