using Builder.Core;
using Builder.Mcp;
using System.Windows;

namespace Builder.Desktop;

public partial class MainWindow
{
    private DesktopMcpHost? mcpHost;
    private McpAccessDialog? mcpDialog;
    private bool mcpPreviewActive,mcpOperationActive,mcpStopping;
    private void InitializeMcp()=>Closed+=(_,_)=>mcpDialog?.Close();
    private void McpAccessClick(object sender,RoutedEventArgs e)
    {
        if(mcpDialog is not null){mcpDialog.Activate();return;}
        mcpDialog=new(()=>mcpHost?.PipeName,()=>mcpHost?.LastError,EnableMcp,StopMcp){Owner=this};
        mcpDialog.Closed+=(_,_)=>mcpDialog=null;mcpDialog.Show();
    }
    private void EnableMcp()
    {
        if(mcpHost is not null||mcpStopping)return;
        mcpHost=new(editor,DispatchMcp,()=>
        {
            if(!Dispatcher.HasShutdownStarted)_=Dispatcher.BeginInvoke(new Action(()=>{RefreshMcpPresentation();RefreshWorkspaceNavigation();mcpDialog?.Refresh();}));
        });
        Message("Local MCP access enabled for trusted same-user clients. View → MCP access can stop it.");
    }
    private async Task StopMcp()
    {
        if(mcpHost is not {} host||mcpStopping)return;
        mcpStopping=true;
        try{await host.DisposeAsync();mcpHost=null;Refresh();Message("Local MCP access stopped. Accepted edits are retained.");}
        finally{mcpStopping=false;mcpDialog?.Refresh();}
    }
    private Task<McpReply> DispatchMcp(McpInvocation invocation,CancellationToken ct)
        =>Dispatcher.CheckAccess()?ExecuteMcp(invocation,ct):Dispatcher.InvokeAsync(()=>ExecuteMcp(invocation,ct)).Task.Unwrap();
    private McpReply McpRefusal(Outcome outcome,string message)=>new(new(outcome,message),EditorMcpConnection.Describe(editor));
    private async Task<McpReply> ExecuteMcp(McpInvocation invocation,CancellationToken ct)
    {
        if(invocation.ToolName=="connection_cleanup")
        {
            var cleaned=await invocation.Run(ct,invocation.Progress);Refresh();return cleaned;
        }
        ct.ThrowIfCancellationRequested();
        if(!invocation.Mutating)
        {
            var read=await invocation.Run(ct,invocation.Progress);
            if(invocation.ToolName is "builder_view" or "builder_reset_view"){SyncViewControls();RenderScene();}
            return read;
        }
        if(mcpStopping||recoveryClosePending||closeApproved)return McpRefusal(Outcome.Conflict,"The desktop is stopping access or closing. Reopen access when ready.");
        if(documentPromptActive||LocalModalToolActive||constructionMode!=null||handleMode!=null||arrangementMode!=null)
            return McpRefusal(Outcome.Conflict,"Finish or cancel the desktop tool or file prompt before changing the shared session through MCP.");
        if(CanRenameSelection&&ObjectName.Text!=(SelectedGroup?.Name??SelectedTarget?.Name??""))
            return McpRefusal(Outcome.Conflict,"Accept or discard the desktop's object/group name draft before changing the scene through MCP.");
        if(operation is not null||editor.Snapshot.IsBusy)return McpRefusal(Outcome.Busy,"The desktop already has an operation running. Cancel it or wait.");
        McpReply? reply=null;mcpOperationActive=true;
        try
        {
            var result=await RunOperation(async(token,uiProgress)=>
            {
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,ct);
                var progress=new McpUiProgress(p=>{uiProgress.Report(p);invocation.Progress?.Report(p);});
                reply=await invocation.Run(linked.Token,progress);return reply.Result;
            });
            return reply??McpRefusal(result.Outcome,result.Message);
        }
        finally{mcpOperationActive=false;RefreshMcpPresentation();}
    }
    private sealed class McpUiProgress(Action<OperationProgress> report):IProgress<OperationProgress>
    {public void Report(OperationProgress value)=>report(value);}
    private void RefreshMcpPresentation()
    {
        var preview=editor.Snapshot.Preview;
        mcpPreviewActive=preview is not null&&preview.Id==mcpHost?.Connection?.OwnedPreviewId;
        McpPreviewActions.Visibility=mcpPreviewActive?Visibility.Visible:Visibility.Collapsed;
        McpPreviewSummary.Text=mcpPreviewActive?preview!.Summary:"";
        ApplyMcpPreviewButton.IsEnabled=CancelMcpPreviewButton.IsEnabled=operation is null&&!mcpStopping;
    }
    private async Task CompleteRemotePreview(bool apply)
    {if(mcpHost?.Connection is {} connection){await connection.CompleteOwnedPreviewAsync(apply);Refresh();}}
    private async void ApplyMcpPreviewClick(object sender,RoutedEventArgs e)=>await CompleteRemotePreview(true);
    private async void CancelMcpPreviewClick(object sender,RoutedEventArgs e)=>await CompleteRemotePreview(false);
}
