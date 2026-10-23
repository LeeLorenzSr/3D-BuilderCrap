using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Builder.Desktop;

public sealed class HelpWindow : Window
{
    public HelpWindow()
    {
        SetResourceReference(StyleProperty,typeof(Window));
        Title="3D BuilderCrap — Help and version";Width=680;Height=600;MinWidth=520;MinHeight=420;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new(24)};
        void Text(string value,int size=14,bool bold=false)=>body.Children.Add(new TextBlock{Text=value,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,14)});
        var version=typeof(HelpWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]??"unknown";
        Text("3D BuilderCrap",24,true);Text($"Version {version} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}");
        var display=new TextBlock{Margin=new(0,0,0,14),TextWrapping=TextWrapping.Wrap};body.Children.Add(display);
        void DisplayInfo(){var dpi=VisualTreeHelper.GetDpi(this);display.Text=$"Display scaling: {dpi.DpiScaleX*100:0}% · Contrast theme: {(SystemParameters.HighContrast?"On":"Off")}";}
        Loaded+=(_,_)=>DisplayInfo();DpiChanged+=(_,_)=>DisplayInfo();
        Text("Open → edit → undo → save",17,true);
        Text("Open a model and choose its source units. Use Object for placement, size and groups; Edit for mesh tools; Paint for colors. Preview first, then Apply or Cancel. F6 / Shift+F6 moves between file commands, workspace tabs, viewport and Objects/tool options; Tab explores each pane. Undo/Redo changes accepted edits. Save 3MF to keep groups and colors; Export makes a slicer or interchange file.");
        Text("Recovery",17,true);
        Text("Recovery copies are separate from your saved model. At startup or View → Recover unsaved work, choose Restore, Discard or Not now. Restore starts with fresh Undo history; save normally afterward. Unapplied previews are not recovered. Uninstall keeps settings and recovery copies.");
        Text("Local MCP",17,true);
        Text("View → MCP access → Enable access → Copy config connects a trusted client to this scene. Access is off at launch. Clients have your supported-model file permissions, with no folder sandbox. Stop access when finished. A separate headless session is also available; see the full guide.");
        var message=new AccessibleStatusText{TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)};message.SetResourceReference(TextBlock.ForegroundProperty,"ErrorInk");
        var buttons=new WrapPanel();
        void Open(string label,string relative)
        {
            var button=new Button{Content=label,Margin=new(0,0,8,8),Padding=new(12,7,12,7)};
            button.Click+=(_,_)=>{try{var path=Path.Combine(AppContext.BaseDirectory,relative);if(!File.Exists(path))throw new IOException("This file is missing. Run the installer again to repair the installation.");Process.Start(new ProcessStartInfo(path){UseShellExecute=true});message.Text="";}catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException){message.Text=e.Message;}};
            buttons.Children.Add(button);
        }
        Open("Full offline guide",Path.Combine("Help","index.html"));Open("Third-party notices","THIRD-PARTY-NOTICES.txt");
        var close=new Button{Content="Close",IsCancel=true,Padding=new(12,7,12,7),Margin=new(0,0,0,8)};close.Click+=(_,_)=>Close();buttons.Children.Add(close);
        body.Children.Add(buttons);body.Children.Add(message);Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
}
