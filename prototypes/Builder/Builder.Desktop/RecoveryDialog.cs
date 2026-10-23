using Builder.Core;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Builder.Desktop;

public enum RecoveryAction { Restore,Discard }

/// <summary>Candidate identity stays in Core. This form owns only selection and in-flight cancellation.</summary>
public sealed class RecoveryDialog : Window
{
    private readonly ListBox copies;
    private readonly Button restore,discard,later;
    private readonly TextBlock error;
    private readonly Func<RecoveryCandidate,RecoveryAction,CancellationToken,Task<OperationResult>> action;
    private CancellationTokenSource? cancellation;
    private bool closeAfterAction;
    public Task CurrentAction {get;private set;}=Task.CompletedTask;
    public bool Restored {get;private set;}
    public OperationResult? LastResult {get;private set;}
    public RecoveryDialog(IReadOnlyList<RecoveryCandidate> candidates,Func<RecoveryCandidate,RecoveryAction,CancellationToken,Task<OperationResult>> action)
    {
        this.action=action;Title="Recover unsaved work";Width=620;Height=520;MinWidth=500;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty,typeof(Window));
        NameScope.SetNameScope(this,new NameScope());
        var root=new Grid();root.SetResourceReference(Panel.BackgroundProperty,"ChromeFFFFFF");root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=new StackPanel{Margin=new(22,20,22,14)};heading.Children.Add(new TextBlock{Text="Recover unsaved work",FontSize=20,FontWeight=FontWeights.SemiBold});heading.Children.Add(new TextBlock{Text="Choose a recovery copy left by a previous session. Restore opens it for editing with a fresh history. Discard removes only the selected copy. Not now keeps copies for later.",TextWrapping=TextWrapping.Wrap,Margin=new(0,9,0,0)});root.Children.Add(heading);
        copies=new ListBox{Margin=new(22,0,22,14),SelectionMode=SelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};RegisterName("RecoveryCopies",copies);System.Windows.Automation.AutomationProperties.SetName(copies,"Available recovery copies");
        foreach(var candidate in candidates)
        {
            var content=new StackPanel{Margin=new(6)};
            var name=string.IsNullOrEmpty(candidate.Title)?candidate.NativePath==null?"Untitled scene":Path.GetFileName(candidate.NativePath):candidate.Title;
            content.Children.Add(new TextBlock{Text=name,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            content.Children.Add(new TextBlock{Text=candidate.NativePath??"Not yet saved as a native scene",TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new(0,3,0,0)});
            content.Children.Add(new TextBlock{Text=$"{candidate.UpdatedUtc.ToLocalTime():g} · {candidate.ObjectCount} object{(candidate.ObjectCount==1?"":"s")}",FontSize=12,Margin=new(0,3,0,0)});
            if(!string.IsNullOrEmpty(candidate.Problem)){var problem=new TextBlock{Text=candidate.Problem,TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new(0,4,0,0)};problem.SetResourceReference(TextBlock.ForegroundProperty,"ErrorInk");content.Children.Add(problem);}
            var item=new ListBoxItem{Content=content,Tag=candidate,ToolTip=candidate.NativePath};
            System.Windows.Automation.AutomationProperties.SetName(item,$"{name} · {candidate.ObjectCount} object{(candidate.ObjectCount==1?"":"s")}");
            System.Windows.Automation.AutomationProperties.SetHelpText(item,$"{candidate.NativePath??"Not yet saved as a native scene"} · {candidate.UpdatedUtc.ToLocalTime():g}"+(string.IsNullOrEmpty(candidate.Problem)?"":" · "+candidate.Problem));
            copies.Items.Add(item);
        }
        copies.SelectedIndex=copies.Items.Count==0?-1:0;Grid.SetRow(copies,1);root.Children.Add(copies);
        var footer=new StackPanel{Margin=new(22,12,22,18)};error=new AccessibleStatusText{TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed,Margin=new(0,0,0,10)};error.SetResourceReference(TextBlock.ForegroundProperty,"ErrorInk");RegisterName("RecoveryError",error);footer.Children.Add(error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        restore=new Button{Content="Restore",MinWidth=100};restore.SetResourceReference(StyleProperty,"PrimaryButton");discard=new Button{Content="Discard copy",MinWidth=100};later=new Button{Content="Not now",MinWidth=100,IsDefault=true,IsCancel=true,Margin=new(0)};
        RegisterName("RestoreRecoveryButton",restore);RegisterName("DiscardRecoveryButton",discard);RegisterName("LaterRecoveryButton",later);
        restore.Click+=(_,_)=>CurrentAction=Act(RecoveryAction.Restore);discard.Click+=(_,_)=>CurrentAction=Act(RecoveryAction.Discard);later.Click+=(_,_)=>Close();
        buttons.Children.Add(restore);buttons.Children.Add(discard);buttons.Children.Add(later);footer.Children.Add(buttons);var border=new Border{BorderThickness=new(0,1,0,0),Child=footer};border.SetResourceReference(Border.BorderBrushProperty,"Line");Grid.SetRow(border,2);root.Children.Add(border);Content=root;
        copies.SelectionChanged+=(_,_)=>RefreshButtons();ContentRendered+=(_,_)=>later.Focus();Closing+=OnClosing;
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};RefreshButtons();
    }
    private void RefreshButtons()
    {
        copies.IsEnabled=cancellation==null;
        var verifiable=copies.SelectedItem is ListBoxItem{Tag:RecoveryCandidate candidate}&&!string.IsNullOrEmpty(candidate.Fingerprint);
        restore.IsEnabled=discard.IsEnabled=cancellation==null&&verifiable;
        discard.ToolTip=verifiable?"Discard only this selected recovery copy. The current document is unchanged.":"This copy cannot be verified. Choose Not now and try again later.";
    }
    private async Task Act(RecoveryAction kind)
    {
        if(cancellation!=null||copies.SelectedItem is not ListBoxItem{Tag:RecoveryCandidate candidate} item)return;
        using var pending=new CancellationTokenSource();cancellation=pending;RefreshButtons();error.Visibility=Visibility.Collapsed;
        try
        {
            LastResult=await action(candidate,kind,pending.Token);
            if(LastResult.Succeeded)
            {
                if(kind==RecoveryAction.Restore){Restored=true;closeAfterAction=true;}
                else{copies.Items.Remove(item);if(copies.Items.Count>0)copies.SelectedIndex=0;else closeAfterAction=true;}
            }
            else{error.Text=LastResult.Message;error.Visibility=Visibility.Visible;}
        }
        catch(OperationCanceledException){error.Text="Cancelled. Your current work and recovery copy are unchanged.";error.Visibility=Visibility.Visible;}
        catch(Exception failure)when(failure is IOException or UnauthorizedAccessException or ModelException){error.Text="This recovery copy could not be opened. Try again, discard it, or choose Not now.";error.Visibility=Visibility.Visible;}
        finally{cancellation=null;RefreshButtons();if(closeAfterAction)Close();}
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {if(cancellation!=null){e.Cancel=true;closeAfterAction=true;cancellation.Cancel();}}
}
