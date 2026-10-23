using Builder.Core;

namespace Builder.Mcp;

public sealed partial class EditorMcpConnection
{
    private static ModelUnit? SourceUnit(string path,ModelUnit? unit)
    {
        if(!Path.GetExtension(path).Equals(".3mf",StringComparison.OrdinalIgnoreCase)&&unit is null)
            throw new ModelException("Choose explicit source units; standard glTF/GLB uses Meters. Native3MF stores its units.");
        return unit;
    }
    private void RegisterFiles()
    {
        Add<OpenArguments>("builder_open","Open a validated model into this session. Absolute path, explicit source units for non3MF, Strict STL by default. Dirty content requires an explicit Save/Discard/Cancel decision; default RequireDecision preserves it. SavePath, when supplied, is an absolute native3MF destination. Opening resets history only after success.",true,async(a,ct,p)=>
        {
            var path=Absolute(a.Path);var save=a.SavePath is null?null:Absolute(a.SavePath);
            var r=await editor.TransitionAsync(new OpenDocumentTransition(path,a.ExpectedRevision,SourceUnit(path,a.Unit),a.Decision,save){StlMode=a.StlMode},ct,p).ConfigureAwait(false);
            return Reply(r.Result,r);
        });
        Add<NewArguments>("builder_new","Start an empty scene through the shared document transition. Dirty work requires explicit Save/Discard/Cancel; default RequireDecision preserves it. No implicit file deletion.",true,async(a,ct,p)=>
        {
            var r=await editor.TransitionAsync(new NewDocumentTransition(a.ExpectedRevision,a.Decision,a.SavePath is null?null:Absolute(a.SavePath)),ct,p).ConfigureAwait(false);
            return Reply(r.Result,r);
        });
        Add<InsertArguments>("builder_insert","Insert a validated model as one Undo step, preserving the current native save path and metadata. Explicit non3MF source units; selects the complete inserted set. Member scope must be exited first.",true,async(a,ct,p)=>
        {
            var path=Absolute(a.Path);
            return Reply(await editor.InsertAsync(path,SourceUnit(path,a.Unit),ct,p,a.ExpectedRevision,a.StlMode).ConfigureAwait(false));
        });
        Add<SaveArguments>("builder_save","Save the whole accepted scene as native3MF, retaining editable supported groups/colors/metadata. Optional absolute path means SaveAs; omitted path uses the current native path. Naming a destination authorizes replacement; atomic writer preserves last-good bytes on failure/cancellation before commit.",true,async(a,ct,p)=>
        {
            var path=a.Path??editor.Snapshot.FilePath??throw new ModelException("Choose an absolute .3mf path for the first save.");
            return Reply(await editor.SaveAsync(Absolute(path),ct,p,a.ExpectedRevision).ConfigureAwait(false));
        });
        Add<ExportArguments>("builder_export","Export whole scene as STL/OBJ/PLY/glTF/GLB, or explicit ObjectIds as those formats or3MF. Absolute path and explicit coordinate units; STL/3MF require Millimeters. Whole-scene exports preserve native path/history. PLY flattens structure. glTF/GLB always reopen as standard meters regardless of vertex-coordinate choice. Naming a destination authorizes replacement.",true,async(a,ct,p)=>
        {
            Context(a.ExpectedRevision,a.MemberGroupId);var path=Absolute(a.Path);var ext=Path.GetExtension(path).ToLowerInvariant();
            if(ext is ".stl" or ".3mf"&&a.Unit!=ModelUnit.Millimeters)throw new ModelException("STL and native3MF exports use Millimeters.");
            OperationResult r;
            if(a.ObjectIds is {} ids)r=await editor.ExportSelectionAsync(path,ids,ct,p,a.ExpectedRevision,a.MemberGroupId,a.Unit).ConfigureAwait(false);
            else r=ext switch
            {
                ".stl"=>await editor.ExportStlAsync(path,ct,p,a.ExpectedRevision).ConfigureAwait(false),
                ".obj"=>await editor.ExportObjAsync(path,a.Unit,ct,p,a.ExpectedRevision).ConfigureAwait(false),
                ".ply"=>await editor.ExportPlyAsync(path,a.Unit,ct,p,a.ExpectedRevision).ConfigureAwait(false),
                ".gltf" or ".glb"=>await editor.ExportGltfAsync(path,a.Unit,ct,p,a.ExpectedRevision).ConfigureAwait(false),
                _=>new(Outcome.Invalid,"Export whole scenes as .stl/.obj/.ply/.gltf/.glb; use builder_save for native3MF or supply ObjectIds for selection3MF.")
            };
            return Reply(r);
        });
        Add<NewArguments>("builder_close_readiness","Check shared close readiness with explicit dirty decision. Reports SaveCommitted/ReadyToClose; does not close the desktop window or terminate the server. Window/process lifecycle belongs to the owning adapter. Any later edit invalidates approval.",true,async(a,ct,p)=>
        {
            var r=await editor.TransitionAsync(new CloseDocumentTransition(a.ExpectedRevision,a.Decision,a.SavePath is null?null:Absolute(a.SavePath)),ct,p).ConfigureAwait(false);
            return Reply(r.Result,r);
        });
        Add<RecoveryConfigureArguments>("builder_configure_recovery","Opt into recovery storage once for a headless session using an absolute directory; desktop already configures its own store. This does not start an autosave timer.",true,(a,ct,_)=>
        {Context(a.ExpectedRevision,null,false);ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.ConfigureRecovery(Absolute(a.Directory))));});
        Add<NoArguments>("builder_recovery_list","List abandoned recovery candidates from the configured store. Active sessions stay hidden; retain exact candidate fingerprints for restore/discard.",false,async(_,ct,_)=>Success(await editor.GetRecoveryCandidatesAsync(ct).ConfigureAwait(false)));
        Add<RevisionArguments>("builder_recovery_checkpoint","Checkpoint accepted work to separate recovery storage; never serializes an unapplied preview or changes native save path/history. Dirty work can survive process termination.",true,async(a,ct,p)=>Reply(await editor.CheckpointRecoveryAsync(ct,p,a.ExpectedRevision).ConfigureAwait(false)));
        Add<RecoveryArguments>("builder_recovery_restore","Restore an exact abandoned candidate, with explicit dirty decision. Retains saved native path, selection and member scope; opens dirty with fresh history. Failure/cancellation preserves both current work and checkpoint.",true,async(a,ct,p)=>
        {
            var r=await editor.RestoreRecoveryAsync(a.Candidate,ct,p,a.ExpectedRevision,a.Decision,a.SavePath is null?null:Absolute(a.SavePath)).ConfigureAwait(false);
            return Reply(r.Result,r);
        });
        Add<RecoveryArguments>("builder_recovery_discard","Discard only an exact listed recovery candidate/fingerprint. Current scene/history/native path are unchanged. This deletes that recovery copy.",true,async(a,ct,p)=>
        {Context(a.ExpectedRevision,null,false);return Reply(await editor.DiscardRecoveryAsync(a.Candidate,ct,p).ConfigureAwait(false));});
    }
}
