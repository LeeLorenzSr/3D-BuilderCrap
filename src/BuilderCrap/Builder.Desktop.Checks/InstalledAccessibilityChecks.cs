using Builder.Core;
using Builder.Desktop;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

internal static partial class Program
{
    private static Color BrushColor(Brush brush)=>((SolidColorBrush)brush).Color;
    private static double Contrast(Brush foreground,Brush background)
    {
        static double L(Color c)
        {
            static double Channel(byte v){var s=v/255d;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);}
            return .2126*Channel(c.R)+.7152*Channel(c.G)+.0722*Channel(c.B);
        }
        var a=L(BrushColor(foreground));var b=L(BrushColor(background));return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }
    private static async Task V1AccessibleChrome()
    {
        await V1MovePreview();var before=editor.Snapshot;
        var ink=(Brush)window.FindResource("Ink");var background=(Brush)window.FindResource("ChromeFFFFFF");
        Assert(BrushColor(window.Foreground)==BrushColor(ink),"Derived MainWindow does not apply the shared Window foreground style.");
        Assert(Contrast(ink,background)>=4.5,"Application text has insufficient contrast against its panel.");
        Assert(Contrast((Brush)window.FindResource("MutedInk"),background)>=4.5,"Help/summary text lost contrast.");
        var apply=Named<Button>("ApplyTransformButton");
        Assert(Contrast(apply.Foreground,apply.Background)>=4.5,"Primary action text lost contrast.");
        Assert(Named<TextBlock>("Status") is AccessibleStatusText,"Operation status does not publish notification events.");
        var status=Named<TextBlock>("Status");status.Text="Preview ready. Apply or cancel.";
        var peer=FrameworkElementAutomationPeer.CreatePeerForElement(status)!;
        Assert(peer.GetName().Contains("Preview ready"),"Status automation name is stale.");
        if(SystemParameters.HighContrast)
        {
            Assert(BrushColor(background)==SystemColors.WindowColor&&BrushColor(ink)==SystemColors.WindowTextColor,"Windows contrast colors were not applied.");
            Assert(BrushColor(apply.Background)==SystemColors.HighlightColor&&BrushColor(apply.Foreground)==SystemColors.HighlightTextColor,"Primary action does not use paired Windows highlight colors.");
        }
        Assert(ReferenceEquals(before.Scene,editor.Snapshot.Scene)&&ReferenceEquals(before.Preview,editor.Snapshot.Preview)&&before.Revision==editor.Snapshot.Revision,"Accessible presentation changed the held preview/accepted scene.");
        observations.Add(new{test="Installed accessibility chrome",contrastTheme=SystemParameters.HighContrast,ink=BrushColor(ink).ToString(),background=BrushColor(background).ToString(),contrast=Contrast(ink,background)});
        Capture(SystemParameters.HighContrast?"accessibility-contrast-preview":"accessibility-normal-preview",1040,720);
        Click(Named<Button>("CancelTransformButton"));
        Pass("Actual Windows palette gives readable panel/summary/primary-action pairs; derived Window style and announcing status peer retain accepted scene and held preview");
    }
    private static Task V1AccessibleDialogs()
    {
        Window[] dialogs=[new HelpWindow(),new UnitsDialog("model.obj"),new ModelMetadataDialog(new ModelMetadata(),_=>new(Outcome.Cancelled,"No edit")),new RecoveryDialog([],(_,_,_)=>Task.FromResult(new OperationResult(Outcome.Cancelled,"No edit"))),new McpAccessDialog(()=>null,()=>null,()=>{},()=>Task.CompletedTask),new SaveChangesDialog(editor.Snapshot,"starting a new scene")];
        foreach(var dialog in dialogs)
        {
            try
            {
                var panel=(FrameworkElement)dialog.Content;
                panel.Measure(new Size(dialog.Width,900));panel.Arrange(new Rect(0,0,dialog.Width,900));panel.UpdateLayout();
                var background=panel is Panel p?p.Background:panel is Border b?b.Background:null;
                background??=dialog.Background;
                Assert(dialog.Style!=null&&Contrast(dialog.Foreground,background)>=4.5,"Dialog foreground/background mismatch: "+dialog.Title);
                foreach(var button in Tree(panel).OfType<Button>().Where(b=>b.IsEnabled&&b.Visibility==Visibility.Visible))
                    Assert(!string.IsNullOrWhiteSpace(FrameworkElementAutomationPeer.CreatePeerForElement(button)?.GetName()),"Unnamed dialog action: "+dialog.Title);
            }
            finally{dialog.Close();}
        }
        Pass("Help, source units, model metadata, recovery, MCP and Save/Discard/Cancel dialogs inherit readable window colors and named actions in the actual Windows theme");
        return Task.CompletedTask;
    }
}
