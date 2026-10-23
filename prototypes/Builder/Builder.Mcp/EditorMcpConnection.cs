using Builder.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Collections.Immutable;
using System.Text.Json;

namespace Builder.Mcp;

/// <summary>One client's tool catalog and preview ownership over an existing session.</summary>
public sealed partial class EditorMcpConnection : IAsyncDisposable
{
    private readonly EditorSession editor;
    private readonly McpDispatcher dispatcher;
    private readonly SemaphoreSlim mutation=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private readonly object ownership=new();
    private Guid? ownedPreview;
    private (Guid Id,string Name,CancellationTokenSource Cancellation)? pendingOperation;
    private readonly List<McpServerTool> tools=[];
    public IReadOnlyList<McpServerTool> Tools=>tools;
    public Guid? OwnedPreviewId {get{lock(ownership)return ownedPreview;}}
    public EditorMcpConnection(EditorSession editor,McpDispatcher? dispatcher=null)
    {
        this.editor=editor;
        this.dispatcher=dispatcher??((invocation,ct)=>invocation.Run(ct,invocation.Progress));
        RegisterQueries();RegisterEdits();RegisterPreviews();RegisterFiles();
    }
    private McpState State(EditorSnapshot snapshot)
    {
        var state=Describe(snapshot,editor.Presentation.Snapshot);
        lock(ownership)return state with{Operation=state.IsBusy&&pendingOperation is {} job?new(job.Id,job.Name,job.Cancellation.IsCancellationRequested):null};
    }
    public McpState State()=>State(editor.Snapshot);
    public static McpState Describe(EditorSession editor)=>Describe(editor.Snapshot,editor.Presentation.Snapshot);
    private static McpState Describe(EditorSnapshot s,PresentationSnapshot presentation)
    {
        var p=s.Preview;
        return new(s.Revision,s.Scene.ContentId,"millimeters",s.FilePath,s.IsDirty,s.CanUndo,s.CanRedo,s.IsBusy,
            s.SelectedObjectId,s.SelectedObjectIds,s.EditingGroupId,s.Scene.Objects.Length,s.Scene.TriangleCount,
            p is null?null:new(p.Id,p.BaseRevision,p.Request.GetType().Name,p.Summary,p.Scene.Objects.Length,p.Scene.TriangleCount,
                p.ResultObjectIds,p.SelectedObjectIds,p.EditingGroupId,new{p.Repair,
                    hollow=p.Hollow is {} h?new{h.RequestedWallThicknessMillimeters,h.MaximumKernelRadiusMillimeters,h.RemovedVolumeCubicMillimeters,h.CavityCount}:null,
                    p.Simplify,p.Smooth,p.Separate,p.ExtrudeDown,p.SurfaceMaterial,p.SurfaceBrush}),presentation);
    }
    public McpReply Reply(OperationResult result,object? data=null)=>new(result,State(),data);
    private McpReply Success(object? data=null,EditorSnapshot? snapshot=null)=>new(new(Outcome.Success,"Query complete."),snapshot is null?State():State(snapshot),data);
    private static string Absolute(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))throw new ModelException("Use an absolute file or directory path. Relative paths depend on the caller's working directory.");
        return Path.GetFullPath(path);
    }
    private EditorSnapshot Context(long revision,Guid? scope,bool checkScope=true)
    {
        var s=editor.Snapshot;
        if(s.Revision!=revision)throw new McpConflict("The scene changed. Read builder_scene and retry with its revision.");
        if(checkScope&&s.EditingGroupId!=scope)throw new McpConflict("The member scope changed. Supply the exact memberGroupId, or null for scene scope.");
        return s;
    }
    private sealed class McpConflict(string message):Exception(message);
    private void Add<T>(string name,string description,bool mutating,
        Func<T,CancellationToken,IProgress<OperationProgress>?,Task<McpReply>> run,bool? readOnly=null)
    {
        var schema=ToolSchema.For(typeof(T));
        tools.Add(new SessionTool(new(){Name=name,Description=description,InputSchema=JsonSerializer.SerializeToElement(schema),
            Annotations=new(){ReadOnlyHint=readOnly??!mutating,DestructiveHint=mutating,OpenWorldHint=false}},
            (arguments,progress,ct)=>Invoke(name,mutating,()=>JsonSerializer.Deserialize<T>(arguments,McpJson.Options)??throw new JsonException("Supply tool arguments."),run,progress,ct)));
    }
    private async Task<McpReply> Invoke<T>(string name,bool mutating,Func<T> parse,
        Func<T,CancellationToken,IProgress<OperationProgress>?,Task<McpReply>> run,IProgress<OperationProgress>? progress,CancellationToken cancellation)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation);
        var ct=linked.Token;var acquired=false;
        try
        {
            ct.ThrowIfCancellationRequested();var request=parse();
            if(mutating)
            {
                acquired=await mutation.WaitAsync(0,ct).ConfigureAwait(false);
                if(!acquired)return Reply(new(Outcome.Busy,"This client already has an operation running. Cancel it or wait for completion."));
                lock(ownership)pendingOperation=(Guid.NewGuid(),name,linked);
            }
            return await dispatcher(new(name,mutating,(token,p)=>run(request,token,p),progress),ct).ConfigureAwait(false);
        }
        catch(OperationCanceledException){return Reply(new(Outcome.Cancelled,"Request cancelled. Re-read the scene: any edit or atomic save already committed remains accepted."));}
        catch(McpConflict e){return Reply(new(Outcome.Conflict,e.Message));}
        catch(Exception e)when(e is JsonException or ModelException or ArgumentException or NotSupportedException or FormatException)
        {return Reply(new(Outcome.Invalid,e.Message));}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return Reply(new(Outcome.Failed,e.Message));}
        finally{if(acquired){lock(ownership)pendingOperation=null;mutation.Release();}}
    }
    public Task<McpReply> CompleteOwnedPreviewAsync(bool apply,CancellationToken cancellation=default)
    {
        var id=OwnedPreviewId;
        if(id is null)return Task.FromResult(Reply(new(Outcome.Conflict,"This connection has no preview.")));
        return Invoke(apply?"builder_apply_preview":"builder_cancel_preview",true,
            ()=>new PreviewTokenArguments(id.Value,editor.Snapshot.Revision),
            (a,ct,_)=>Task.FromResult(CompletePreview(a,apply,ct)),null,cancellation);
    }
    private McpReply CompletePreview(PreviewTokenArguments args,bool apply,CancellationToken ct)
    {
        Context(args.ExpectedRevision,null,false);ct.ThrowIfCancellationRequested();
        if(OwnedPreviewId!=args.PreviewId)return Reply(new(Outcome.Conflict,"Only this connection's current preview token can be applied or cancelled."));
        var result=apply?editor.ApplyPreview(args.PreviewId,args.ExpectedRevision):editor.DiscardPreview(args.PreviewId);
        if(editor.Snapshot.Preview?.Id!=args.PreviewId)lock(ownership)ownedPreview=null;
        return Reply(result);
    }
    public void CancelPending()=>lifetime.Cancel();
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();await mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            if(OwnedPreviewId is {} id)
                await dispatcher(new("connection_cleanup",false,(_,_)=>
                {
                    if(editor.Snapshot.Preview?.Id==id)editor.DiscardPreview(id);
                    lock(ownership)ownedPreview=null;
                    return Task.FromResult(Success());
                },null),CancellationToken.None).ConfigureAwait(false);
        }
        finally{mutation.Release();}
        // Tokens and the gate remain usable by already queued SDK callbacks, which observe cancellation.
    }
}
