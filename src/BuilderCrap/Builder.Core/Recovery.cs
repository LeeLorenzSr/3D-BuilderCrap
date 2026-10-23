using System.Collections.Immutable;

namespace Builder.Core;

public sealed record RestoreDocumentTransition(RecoveryCandidate Candidate,long ExpectedRevision,
    DirtyDecision Decision=DirtyDecision.RequireDecision,string? SavePath=null):DocumentTransition(ExpectedRevision,Decision,SavePath);

public sealed partial class EditorSession : IDisposable
{
    private RecoveryStore? recovery;
    private bool disposeRequested;
    private long? closeReadyRevision;
    public string? RecoveryDirectory { get{lock(gate)return recovery?.DirectoryPath;} }
    /// <summary>Opt-in local recovery storage. Each configured session holds its own exclusive lock;
    /// no timer is created in Core. Call Dispose on shutdown to release ownership without discarding work.</summary>
    public OperationResult ConfigureRecovery(string directory)
    {
        lock(gate)
        {
            if(Guard(null) is {} blocked)return blocked;
            if(recovery is not null)return new(Outcome.Conflict,"Recovery is already configured for this session.");
            try{recovery=new(directory);return new(Outcome.Success,"Recovery checkpoints enabled.");}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or ModelException or NotSupportedException)
            {return new(Outcome.Failed,"Recovery is unavailable: "+e.Message);}
        }
    }
    /// <summary>Lists abandoned entries only. Active sessions/claims are omitted. Damaged bounded files
    /// remain visible and can be discarded by their exact fingerprint. I/O/cancellation throws, as for other read queries.</summary>
    public Task<ImmutableArray<RecoveryCandidate>> GetRecoveryCandidatesAsync(CancellationToken cancellation=default)
    {
        RecoveryStore store;lock(gate){if(disposeRequested)throw new ModelException("This editor session is closed.");store=recovery??throw new ModelException("Configure a recovery directory first.");}
        return Task.Run(()=>store.Discover(cancellation),cancellation);
    }
    /// <summary>Atomically checkpoints only accepted dirty content, including an empty delete-all scene.
    /// An active preview may coexist but its candidate is never serialized. Owns a transient busy lease;
    /// content/path/history/selection/scope/revision remain unchanged. A clean checkpoint call retires stale own recovery.</summary>
    public Task<OperationResult> CheckpointRecoveryAsync(CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
        =>RunIo(()=>
        {
            var captured=Snapshot;cancellation.ThrowIfCancellationRequested();var store=recovery!;
            if(!captured.IsDirty){store.RetireOwn();return "Scene is clean; stale recovery retired.";}
            store.Checkpoint(captured,cancellation,progress);return "Accepted work checkpointed for recovery.";
        },expectedRevision,allowPreview:true,preflight:()=>recovery is null?new(Outcome.Invalid,"Configure a recovery directory first."):null);
    /// <summary>Validates/claims the exact abandoned checkpoint before saving/discarding current work.
    /// On success adopts its lock/file, restores stable IDs/selection/scope/native path, starts fresh history
    /// and marks work dirty. The existing checkpoint remains crash-recoverable until Save or explicit discard.</summary>
    public Task<DocumentTransitionResult> RestoreRecoveryAsync(RecoveryCandidate candidate,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null,long? expectedRevision=null,DirtyDecision decision=DirtyDecision.RequireDecision,string? savePath=null)
        =>TransitionAsync(new RestoreDocumentTransition(candidate,expectedRevision??Snapshot.Revision,decision,savePath),cancellation,progress);
    public Task<OperationResult> DiscardRecoveryAsync(RecoveryCandidate candidate,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null)
        =>RunIo(()=>
        {
            using var claim=recovery!.Acquire(candidate,cancellation,allowDamaged:true);recovery.RetireClaim(claim,cancellation,progress);return "Recovery checkpoint discarded.";
        },null,preflight:()=>recovery is null?new(Outcome.Invalid,"Configure a recovery directory first."):null);
    /// <summary>Call only in the final close callback after successful Close readiness for this exact revision.
    /// Retirement failure leaves the editor open/editable. Success durably retires recovery and seals mutation,
    /// preventing late edits after close approval. Dispose alone never retires unsaved work.</summary>
    public OperationResult CompleteClose(long approvedRevision)
    {
        lock(gate)
        {
            if(Guard(approvedRevision) is {} blocked)return blocked;
            if(closeReadyRevision!=approvedRevision)return new(Outcome.Conflict,"Check document close readiness again before closing.");
            try{recovery?.RetireOwn();}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or ModelException)
            {return new(Outcome.Failed,"Could not retire recovery; this window remains open. "+e.Message);}
            disposeRequested=true;recovery?.Dispose();return new(Outcome.Success,"Document close confirmed.");
        }
    }
    private string RetireRecoveryAfterSave()
    {
        try{recovery?.RetireOwn();return "";}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ModelException)
        {return " Native save succeeded, but recovery cleanup failed: "+e.Message+". Cleanup will be retried; an older backup may still be listed after a restart.";}
    }
    public void Dispose()
    {
        lock(gate){disposeRequested=true;if(!busy)recovery?.Dispose();}
        GC.SuppressFinalize(this);
    }
}
