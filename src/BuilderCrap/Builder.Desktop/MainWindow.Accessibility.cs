using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Builder.Desktop;

public partial class MainWindow
{
    private void CycleWorkspaceFocus(bool backwards)
    {
        // Standard pane navigation avoids tabbing through every ribbon command. Focus never commits a tool.
        FrameworkElement[] panes=[FileStrip,WorkspaceTabs,ViewportSurface,Inspector];
        var current=Array.FindIndex(panes,p=>p.IsKeyboardFocusWithin);
        for(var step=1;step<=panes.Length;step++)
        {
            var index=(current+(backwards?-step:step)+panes.Length*2)%panes.Length;
            var pane=panes[index];if(!pane.IsVisible||!pane.IsEnabled)continue;
            if(ReferenceEquals(pane,ViewportSurface)){pane.Focus();return;}
            if(ReferenceEquals(pane,WorkspaceTabs)&&WorkspaceTabs.SelectedItem is TabItem tab){tab.Focus();return;}
            if(pane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))return;
        }
    }
}
