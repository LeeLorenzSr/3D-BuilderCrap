using System.Collections.Immutable;

namespace Builder.Core;

public enum DirtyDecision { RequireDecision, Save, Discard, Cancel }
/// <summary>A prompt decision is valid only for the captured revision. The default never discards dirty work.</summary>
public abstract record DocumentTransition(long ExpectedRevision,DirtyDecision Decision=DirtyDecision.RequireDecision,string? SavePath=null);
public sealed record NewDocumentTransition(long ExpectedRevision,DirtyDecision Decision=DirtyDecision.RequireDecision,string? SavePath=null)
    :DocumentTransition(ExpectedRevision,Decision,SavePath);
public sealed record OpenDocumentTransition(string FilePath,long ExpectedRevision,ModelUnit? Unit=null,
    DirtyDecision Decision=DirtyDecision.RequireDecision,string? SavePath=null):DocumentTransition(ExpectedRevision,Decision,SavePath)
{
    public StlImportMode StlMode { get; init; }=StlImportMode.Strict;
}
/// <summary>Checks readiness only; it does not close/reset the session. Adapters must recheck Revision,
/// busy and preview state in their final close callback before disposing their window/session.</summary>
public sealed record CloseDocumentTransition(long ExpectedRevision,DirtyDecision Decision=DirtyDecision.RequireDecision,string? SavePath=null)
    :DocumentTransition(ExpectedRevision,Decision,SavePath);
public sealed record DocumentTransitionResult(OperationResult Result,long Revision,bool RequiresDecision=false,
    bool SaveCommitted=false,bool ReadyToClose=false,string? SavedPath=null);

public sealed partial class EditorSession
{
    /// <summary>Serializes save and New/Open/Close readiness under one job lease. Failed/cancelled save
    /// preserves the whole accepted document. After a committed save, later failure/cancel preserves the
    /// current scene as saved and reports SaveCommitted/SavedPath explicitly. Replacement resets history,
    /// selection and member scope only after full validation and final cancellation checks.</summary>
    public async Task<DocumentTransitionResult> TransitionAsync(DocumentTransition request,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null)
    {
        var requiresDecision=false;var saveCommitted=false;var ready=false;string? savedPath=null;long? resultRevision=null;var recoveryWarning="";
        var result=await RunIo(()=>
        {
            cancellation.ThrowIfCancellationRequested();
            lock(gate)closeReadyRevision=null;
            var captured=Snapshot;
            if(request.Decision==DirtyDecision.Cancel)throw new OperationCanceledException();
            using var restoreClaim=request is RestoreDocumentTransition restoring?recovery!.Acquire(restoring.Candidate,cancellation):null;
            Scene? recoveryScene=null;Guid? recoveryPrimary=null;ImmutableArray<Guid> recoverySelection=[];Guid? recoveryScope=null;string? recoveryPath=null;
            if(restoreClaim is not null)
            {
                (recoveryScene,recoveryPrimary,recoverySelection,recoveryScope,recoveryPath)=RecoveryStore.Restore(restoreClaim.Envelope,cancellation);
                progress?.Report(new("Recovery validated"));cancellation.ThrowIfCancellationRequested();
            }
            var openPath=request is OpenDocumentTransition open?Path.GetFullPath(open.FilePath):null;
            if(captured.IsDirty&&request.Decision==DirtyDecision.Save)
            {
                var destination=request.SavePath??captured.FilePath;
                if(string.IsNullOrWhiteSpace(destination))throw new ModelException("Choose a .3mf path to save the current document before continuing.");
                var fullPath=Path.GetFullPath(destination);
                if(!Path.GetExtension(fullPath).Equals(".3mf",StringComparison.OrdinalIgnoreCase))throw new ModelException("Use .3mf to save the current document.");
                if(captured.Scene.Objects.IsEmpty)throw new ModelException("There are no objects to save. Cancel to add an object or use Undo, or choose Discard to continue.");
                ModelFiles.WriteAtomic(fullPath,captured.Scene,false,cancellation,progress);
                lock(gate)
                {
                    // File replacement already committed; do not claim rollback if cancellation arrives now.
                    path=fullPath;savedContent=captured.Scene.ContentId;revision++;resultRevision=revision;
                    saveCommitted=true;savedPath=fullPath;
                }
                recoveryWarning=RetireRecoveryAfterSave();
                progress?.Report(new("Current document saved"));cancellation.ThrowIfCancellationRequested();
            }
            if(request is CloseDocumentTransition)
            {
                progress?.Report(new("Ready to close"));cancellation.ThrowIfCancellationRequested();
                lock(gate){cancellation.ThrowIfCancellationRequested();ready=true;resultRevision=revision;closeReadyRevision=revision;}
                return "Ready to close this document.";
            }
            Scene candidate;
            if(restoreClaim is not null)
            {
                candidate=recoveryScene!;progress?.Report(new("Ready to restore recovery"));cancellation.ThrowIfCancellationRequested();
            }
            else if(request is OpenDocumentTransition opening)
            {
                candidate=ModelFiles.Read(openPath!,opening.Unit,cancellation,progress,opening.StlMode);candidate.Validate(cancellation);
                progress?.Report(new("Ready to open"));cancellation.ThrowIfCancellationRequested();
            }
            else
            {
                candidate=Scene.Empty;candidate.Validate(cancellation);
                progress?.Report(new("Ready to create new scene"));cancellation.ThrowIfCancellationRequested();
            }
            lock(gate)
            {
                cancellation.ThrowIfCancellationRequested();
                recovery?.RetireOwn();
                if(restoreClaim is not null)
                {
                    recovery!.Adopt(restoreClaim);scene=candidate;selected=recoveryPrimary;selectedIds=recoverySelection;editingGroupId=recoveryScope;path=recoveryPath;savedContent=Guid.Empty;
                }
                else
                {
                    scene=candidate;selected=candidate.Objects.FirstOrDefault()?.Id;editingGroupId=null;
                    selectedIds=selected is {} id?candidate.ExpandObjectIds([id]):[];
                    path=openPath is not null&&Path.GetExtension(openPath).Equals(".3mf",StringComparison.OrdinalIgnoreCase)?openPath:null;savedContent=scene.ContentId;
                }
                undo.Clear();redo.Clear();revision++;resultRevision=revision;closeReadyRevision=null;
            }
            return restoreClaim is not null?"Recovered accepted work. Review it and save when ready.":request is OpenDocumentTransition opened?"Opened "+Path.GetFileName(openPath)+"."+
                (opened.StlMode==StlImportMode.GeometryOnly&&Path.GetExtension(openPath!).Equals(".stl",StringComparison.OrdinalIgnoreCase)?" Geometry only: STL attribute/color data was not imported.":""):"New empty scene created.";
        },request?.ExpectedRevision,preflight:()=>
        {
            if(request is not (NewDocumentTransition or OpenDocumentTransition or CloseDocumentTransition or RestoreDocumentTransition)||!Enum.IsDefined(request.Decision))
                return new(Outcome.Invalid,"Choose New, Open, Restore or Close and a valid dirty-work decision.");
            if(request is RestoreDocumentTransition&&recovery is null)return new(Outcome.Invalid,"Configure a recovery directory first.");
            if(request is OpenDocumentTransition stl&&!Enum.IsDefined(stl.StlMode))return new(Outcome.Invalid,"Choose Strict or Geometry only for STL import.");
            if(request is OpenDocumentTransition open&&open.Unit is {} unit&&!Enum.IsDefined(unit))return new(Outcome.Invalid,"Choose valid model units.");
            if(scene.ContentId!=savedContent&&request.Decision==DirtyDecision.RequireDecision)
            {requiresDecision=true;return new(Outcome.Conflict,"This document has unsaved changes. Choose Save, Discard or Cancel.");}
            return null;
        }).ConfigureAwait(false);
        if(saveCommitted&&!result.Succeeded)
            result=result with{Message="The current document was saved to "+savedPath+". "+(result.Outcome==Outcome.Cancelled?"The requested transition was cancelled; the current scene remains open and saved.":"The requested transition failed; the current scene remains open and saved. "+result.Message)};
        if(recoveryWarning.Length>0)result=result with{Message=result.Message+recoveryWarning};
        return new(result,resultRevision??Snapshot.Revision,requiresDecision,saveCommitted,ready&&result.Succeeded,savedPath);
    }
}
