using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Builder.Desktop;

public sealed class McpAccessDialog:Window
{
    private readonly Func<string?> pipe;
    private readonly Func<string?> error;
    private readonly Action enable;
    private readonly Func<Task> stop;
    private readonly TextBlock status;
    private readonly TextBox configuration;
    private readonly Button enableButton,stopButton,copyButton;
    public McpAccessDialog(Func<string?> pipe,Func<string?> error,Action enable,Func<Task> stop)
    {
        SetResourceReference(StyleProperty,typeof(Window));
        this.pipe=pipe;this.error=error;this.enable=enable;this.stop=stop;
        Title="Local MCP access";Width=680;Height=560;MinWidth=560;MinHeight=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid{Margin=new(24)};root.SetResourceReference(Panel.BackgroundProperty,"ChromeFFFFFF");root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=new StackPanel();heading.Children.Add(new TextBlock{Text="Let a local MCP client edit this scene",FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        heading.Children.Add(new TextBlock{Text="Only enable clients you trust. They can inspect and edit this window's scene, use Undo/Redo, and read or replace supported model/image files with your Windows permissions. There is no folder sandbox. Do not run elevated or share access with untrusted software.",TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,10)});
        var explanation=new TextBlock{Text="Access is off at every launch. One same-user connection at a time; no network listener. Stop cancels pending work and removes that client's unapplied preview. Accepted edits stay undoable. Closing this panel keeps access enabled; Stop or close the app to disable it.",TextWrapping=TextWrapping.Wrap,FontSize=12};explanation.SetResourceReference(TextBlock.ForegroundProperty,"MutedInk");heading.Children.Add(explanation);
        status=new AccessibleStatusText{Margin=new(0,12,0,10),TextWrapping=TextWrapping.Wrap};heading.Children.Add(status);root.Children.Add(heading);
        configuration=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas"),FontSize=14,Margin=new(0,0,0,14)};
        System.Windows.Automation.AutomationProperties.SetName(configuration,"MCP client connection configuration");Grid.SetRow(configuration,1);root.Children.Add(configuration);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        enableButton=new(){Content="Enable access",MinWidth=110};copyButton=new(){Content="Copy config",MinWidth=105};stopButton=new(){Content="Stop access",MinWidth=105};var close=new Button{Content="Close",IsCancel=true,MinWidth=85,Margin=new(0)};
        enableButton.Click+=(_,_)=>{enable();Refresh();};copyButton.Click+=(_,_)=>{try{Clipboard.SetText(configuration.Text);}catch(System.Runtime.InteropServices.COMException){status.Text="Clipboard is busy. Select and copy the configuration text instead.";}};
        stopButton.Click+=async(_,_)=>{stopButton.IsEnabled=false;await stop();Refresh();};close.Click+=(_,_)=>Close();
        foreach(var b in new[]{enableButton,copyButton,stopButton,close})buttons.Children.Add(b);Grid.SetRow(buttons,2);root.Children.Add(buttons);Content=root;Refresh();
    }
    public void Refresh()
    {
        var name=pipe();enableButton.IsEnabled=name is null;stopButton.IsEnabled=copyButton.IsEnabled=name is not null;
        status.Text=error() is {} message?"Access could not continue: "+message:name is null?"Disabled. Enable only when you want a trusted client to connect.":"Enabled for this window. Configure your MCP client with the settings below.";
        configuration.Text=name is null?"Enable access to create this window's temporary connection settings.":JsonSerializer.Serialize(new{mcpServers=new Dictionary<string,object>{["3d-buildercrap"]=new{command=Path.Combine(AppContext.BaseDirectory,"Builder.Mcp.Server.exe"),args=new[]{"--attach",name}}}},new JsonSerializerOptions{WriteIndented=true});
    }
}
