using Builder.Core;
using Builder.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

internal static partial class Program
{
    private sealed class InlineProgress<T>(Action<T> report):IProgress<T>{public void Report(T value)=>report(value);}
    private static async Task Wait(Func<bool> condition,string failure)
    {
        using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while(!condition()){try{await Task.Delay(20,wait.Token);}catch(OperationCanceledException){throw new InvalidOperationException(failure);}}
    }
    private static async Task SharedPipe()
    {
        using var editor=new EditorSession(Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe"));
        Check(editor.Execute(new AddCube()).Succeeded,"Seed cube");
        string? pauseStage=null;
        McpDispatcher dispatch=async(invocation,ct)=>
        {
            var progress=new InlineProgress<OperationProgress>(p=>
            {
                invocation.Progress?.Report(p);
                if(p.Stage==Volatile.Read(ref pauseStage)){ct.WaitHandle.WaitOne();ct.ThrowIfCancellationRequested();}
            });
            return await invocation.Run(ct,progress);
        };
        await using var host=new DesktopMcpHost(editor,dispatch);
        await using(var probe=new NamedPipeClientStream(".",host.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly))
        {
            await probe.ConnectAsync(Ct);var acl=probe.GetAccessControl();var user=WindowsIdentity.GetCurrent().User!;
            var rules=acl.GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            Check(acl.AreAccessRulesProtected&&Equals(acl.GetOwner(typeof(SecurityIdentifier)),user),"Pipe owner/inheritance policy mismatch");
            Check(rules.Any(r=>r.AccessControlType==AccessControlType.Deny&&r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid,null))),"Remote/network identity not denied");
            Check(rules.Where(r=>r.AccessControlType==AccessControlType.Allow).All(r=>r.IdentityReference.Equals(user)),"Pipe allows another identity");
        }
        await Wait(()=>host.Connection is null,"Security probe connection did not close");
        Pass("Actual Windows named-pipe ACL is protected, owned/allowed only by the current user and explicitly denies network logons; proxy validates same-user ownership");
        var client=await Client("--attach",host.PipeName);
        try
        {
            var scene=await Scene(client);Check(Primary(scene)==editor.Snapshot.SelectedObjectId,"Attachment did not share exact identity");
            var old=editor.Snapshot;Check(editor.Execute(new RenameObject(old.SelectedObjectId!.Value,"Direct Core edit"),old.Revision).Succeeded,"Direct edit");
            await Call(client,"builder_resize",new{edit=new ResizeObject(old.SelectedObjectId!.Value,new(30,20,20)),expectedRevision=old.Revision},"Conflict");
            scene=await Scene(client);var id=Primary(scene);var preview=await Preview(client,"move",new MoveTransform(id,new(4,0,0)));
            var held=editor.Snapshot;Check(held.Preview?.Id==PreviewId(preview),"Proxy owns another session");
            pauseStage="Ready to preview";
            using(var cancelled=new CancellationTokenSource())
            {
                var seen=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending=Call(client,"builder_preview_move",new{request=new MoveTransform(id,new(9,0,0)),expectedRevision=held.Revision,replacePreviewId=held.Preview!.Id},outcome:"Cancelled",progress:new InlineProgress<ProgressNotificationValue>(p=>{if(p.Message?.StartsWith(pauseStage,StringComparison.Ordinal)==true)seen.TrySetResult();}),cancellation:cancelled.Token);
                await seen.Task.WaitAsync(TimeSpan.FromSeconds(8),Ct);
                var during=await Scene(client);Check(during["state"]!["isBusy"]!.GetValue<bool>(),"Busy query missing");
                await Call(client,"builder_undo",new{expectedRevision=held.Revision},"Busy");
                var job=during["state"]!["operation"]!["id"]!.GetValue<string>();
                await Call(client,"builder_cancel_operation",new{operationId=job});
                await pending; // Core reports Cancelled in the tool result.
                await Wait(()=>!editor.Snapshot.IsBusy,"Cancelled preview did not release Core lease");
            }
            pauseStage=null;Check(ReferenceEquals(editor.Snapshot.Scene,held.Scene)&&editor.Snapshot.Preview?.Id==held.Preview?.Id,"Cancelled replacement lost accepted or held preview");
            await Finish(client,preview);scene=await Scene(client);
            var native=Path.Combine(output,"pipe-shared.3mf");scene=await Call(client,"builder_save",new{path=native,expectedRevision=Revision(scene)});
            Pass("Stdio attachment controls the exact existing EditorSession; out-of-band edits reject stale intent, progress travels through MCP, and in-flight preview cancellation preserves the held candidate while releasing busy state");
            var target=Path.Combine(output,"cancelled-export.stl");await File.WriteAllTextAsync(target,"last-good export",Ct);var bytes=await File.ReadAllBytesAsync(target,Ct);var before=editor.Snapshot;
            pauseStage="Ready to save";
            using(var cancelled=new CancellationTokenSource())
            {
                var seen=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requestId=new RequestId("cancel-export-"+Guid.NewGuid().ToString("N"));
                var progressToken="file-cancel-progress";
                await using var progressHandler=client.RegisterNotificationHandler("notifications/progress",(notification,_)=>
                {if(notification.Params?["progressToken"]?.GetValue<string>()==progressToken&&notification.Params?["message"]?.GetValue<string>()?.StartsWith(pauseStage,StringComparison.Ordinal)==true)seen.TrySetResult();return ValueTask.CompletedTask;});
                var pending=client.SendRequestAsync(new JsonRpcRequest{Id=requestId,Method="tools/call",Params=JsonSerializer.SerializeToNode(new{name="builder_export",arguments=new{path=target,unit=ModelUnit.Millimeters,expectedRevision=before.Revision},_meta=new{progressToken}},McpJson.Options)},Ct);
                await seen.Task.WaitAsync(TimeSpan.FromSeconds(8),Ct);
                await client.SendNotificationAsync("notifications/cancelled",new{requestId=requestId.ToString()},cancellationToken:Ct);
                try{await pending;}catch(OperationCanceledException){ }
                await Wait(()=>!editor.Snapshot.IsBusy,"Cancelled save lease did not release");
            }
            pauseStage=null;var afterBytes=await File.ReadAllBytesAsync(target,Ct);Check(bytes.SequenceEqual(afterBytes)&&ReferenceEquals(before.Scene,editor.Snapshot.Scene)&&before.FilePath==editor.Snapshot.FilePath,"Cancelled export damaged state/file");
            Check(!Directory.EnumerateFiles(output,".cancelled-export.stl.*.tmp").Any(),"Temporary export leaked");
            Pass("A real MCP cancellation notification at the Core pre-replace file stage preserves existing destination bytes, native path and scene and removes temporary output");
            var accepted=await Finish(client,await Preview(client,"move",new MoveTransform(id,new(3,0,0))),true);
            var content=editor.Snapshot.Scene;await Preview(client,"move",new MoveTransform(id,new(20,0,0)));
            await client.DisposeAsync();await Wait(()=>host.Connection is null,"Disconnected client still owns session");
            Check(editor.Snapshot.Preview is null&&ReferenceEquals(editor.Snapshot.Scene,content)&&editor.Snapshot.CanUndo,"Disconnect lost accepted work/history or leaked preview");
            client=await Client("--attach",host.PipeName);scene=await Scene(client);Check(scene["state"]!["contentId"]!.GetValue<string>()==accepted["state"]!["contentId"]!.GetValue<string>(),"Reconnect created a new document");
            await Preview(client,"move",new MoveTransform(id,new(11,0,0)));await host.DisposeAsync();
            Check(editor.Snapshot.Preview is null&&ReferenceEquals(editor.Snapshot.Scene,content),"Stop changed accepted scene");
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8),Ct);
            Pass("Disconnect/Stop discard only the owning client's unapplied preview, retain accepted edits/history and allow reconnect to the same document; local proxy terminates on pipe shutdown");
        }
        finally{pauseStage=null;await client.DisposeAsync();}
        var recovery=Path.Combine(output,"recovery");
        Check(editor.ConfigureRecovery(recovery).Succeeded,"Configure seed recovery");Check((await editor.CheckpointRecoveryAsync()).Succeeded,"Checkpoint seed");editor.Dispose();
        await using var recovered=await Client("--headless","--recovery",recovery);
        var list=await Call(recovered,"builder_recovery_list");var candidate=list["data"]!.AsArray().Single()!;
        var restored=await Call(recovered,"builder_recovery_restore",new{candidate=JsonSerializerNode(candidate),expectedRevision=0});
        Check(restored["state"]!["isDirty"]!.GetValue<bool>()&&restored["state"]!["filePath"]!.GetValue<string>()==Path.Combine(output,"pipe-shared.3mf")&&!restored["state"]!["canUndo"]!.GetValue<bool>(),"Recovery path/history mismatch");
        await Call(recovered,"builder_save",new{expectedRevision=Revision(restored)});
        Pass("Headless recovery uses Core checkpoint ownership/fingerprints, restores native path with fresh history and saves normally without transport-specific document data");
    }
    private static System.Text.Json.JsonElement JsonSerializerNode(JsonNode node)=>System.Text.Json.JsonSerializer.SerializeToElement(node);
}
