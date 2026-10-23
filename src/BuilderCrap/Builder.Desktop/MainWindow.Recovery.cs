using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Builder.Desktop;

public partial class MainWindow
{
    private string? recoveryDirectory;
    private DispatcherTimer? recoveryTimer;
    private bool recoveryConfigured,recoveryStartupChecked,recoveryClosePending;
    private CancellationTokenSource? recoveryCheckpointCancellation;
    private Task recoveryCheckpointTask=Task.CompletedTask;
    private void InitializeRecovery(string? directory)
    {
        recoveryDirectory=directory;
        if(directory==null)return; // Injected adapters opt in explicitly; never touch current-user storage.
        EnsureRecoveryConfigured();
        recoveryTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(10)};
        recoveryTimer.Tick+=async(_,_)=>await CheckpointRecovery();
        Loaded+=async(_,_)=>{recoveryTimer.Start();if(!recoveryStartupChecked){recoveryStartupChecked=true;await ShowRecovery(true);}};
        Closed+=(_,_)=>{recoveryTimer.Stop();recoveryCheckpointCancellation?.Cancel();editor.Dispose();};
        RefreshWorkspaceNavigation();
    }
    private bool EnsureRecoveryConfigured()
    {
        if(recoveryConfigured)return true;
        if(recoveryDirectory==null)return false;
        var result=editor.ConfigureRecovery(recoveryDirectory);recoveryConfigured=result.Succeeded;
        if(!result.Succeeded)RecoveryWarning.Visibility=Visibility.Visible;
        return recoveryConfigured;
    }
    private Task CheckpointRecovery()
    {
        if(recoveryDirectory==null||recoveryClosePending||WorkspaceToolActive||documentPromptActive||operation!=null||editor.Snapshot.IsBusy||editor.Snapshot.Preview!=null||!recoveryCheckpointTask.IsCompleted)return Task.CompletedTask;
        return recoveryCheckpointTask=CheckpointRecoveryCore();
    }
    private async Task CheckpointRecoveryCore()
    {
        if(!EnsureRecoveryConfigured())return;
        using var cancellation=new CancellationTokenSource();recoveryCheckpointCancellation=cancellation;
        try
        {
            var result=await editor.CheckpointRecoveryAsync(cancellation.Token,expectedRevision:editor.Snapshot.Revision);
            if(result.Succeeded)RecoveryWarning.Visibility=Visibility.Collapsed;
            else if(result.Outcome is not (Outcome.Busy or Outcome.Conflict or Outcome.Cancelled))RecoveryWarning.Visibility=Visibility.Visible;
            // Checkpoints do not Refresh: accepted identity, preview, camera and local field drafts stay intact.
        }
        catch(OperationCanceledException){}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ModelException){RecoveryWarning.Visibility=Visibility.Visible;}
        finally{recoveryCheckpointCancellation=null;RefreshWorkspaceNavigation();}
    }
    private async void RecoverClick(object sender,RoutedEventArgs e)=>await ShowRecovery(false);
    private async Task ShowRecovery(bool startup)
    {
        if(recoveryDirectory==null||recoveryClosePending||!DocumentNavigationAvailable)return;
        if(!EnsureRecoveryConfigured()){Message("Recovery copies could not be opened. Save your work normally and try again later.",true);return;}
        var captured=editor.Snapshot;var expectedRevision=captured.Revision;
        documentPromptActive=true;RefreshWorkspaceNavigation();
        RecoveryDialog? dialog=null;
        try
        {
            var candidates=await editor.GetRecoveryCandidatesAsync();
            if(editor.Snapshot.Revision!=expectedRevision){Message("The scene changed. Open recovery again when you are ready.");return;}
            if(candidates.IsEmpty){if(!startup)Message("No abandoned recovery copies were found.");return;}
            dialog=new RecoveryDialog(candidates,async(candidate,action,cancellation)=>
            {
                if(action==RecoveryAction.Discard)return await editor.DiscardRecoveryAsync(candidate,cancellation);
                var current=editor.Snapshot;
                if(current.Revision!=expectedRevision)return new(Outcome.Conflict,"The scene changed. Choose Not now, then open recovery again.");
                var decision=DirtyDecision.RequireDecision;string? savePath=null;
                if(current.IsDirty)
                {
                    decision=documentDialogs.ConfirmChanges(dialog is null?this:dialog,current,"restoring recovered work");
                    if(decision==DirtyDecision.Cancel)return new(Outcome.Cancelled,"Restore cancelled. Current work and the recovery copy are unchanged.");
                    if(decision==DirtyDecision.Save)
                    {
                        savePath=current.FilePath??documentDialogs.ChooseSavePath(dialog is null?this:dialog);
                        if(savePath==null)return new(Outcome.Cancelled,"Save cancelled. Current work and the recovery copy are unchanged.");
                    }
                }
                var result=await editor.RestoreRecoveryAsync(candidate,cancellation,expectedRevision:expectedRevision,decision:decision,savePath:savePath);
                if(result.SaveCommitted)
                {
                    if(result.SavedPath is {} saved)RememberFile(saved);
                    expectedRevision=result.Revision;
                }
                if(result.SaveCommitted&&!result.Result.Succeeded)return result.Result with{Message="The current scene was saved. "+result.Result.Message};
                return result.Result;
            });
            await documentDialogs.ShowRecoveryAsync(this,dialog);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ModelException)
        {Message("Recovery copies could not be opened. Save your work normally and try again later.",true);}
        finally{documentPromptActive=false;RefreshWorkspaceNavigation();}
        if(dialog?.Restored==true)
        {
            CancelPendingPick();useOrthographic=false;yaw=-.85;pitch=.55;Refresh();FitScene();ViewportSurface.Focus();
            Message("Recovered work restored with a fresh history. Save normally to keep it.");
        }
        else if(editor.Snapshot.Revision!=captured.Revision)Refresh(); // A requested Save may commit even if Restore fails.
    }
}
