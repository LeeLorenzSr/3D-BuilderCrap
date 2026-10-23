using Builder.Core;

namespace Builder.Mcp;

public sealed partial class EditorMcpConnection
{
    private void EditTool<T>(string name,string description)where T:Edit
        =>Add<EditArguments<T>>(name,description+" Requires expectedRevision and exact edit.memberGroupId for member scope; one Undo step.",true,(a,ct,_)=>
        {ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.Execute(a.Edit,a.ExpectedRevision)));});
    private void RegisterEdits()
    {
        Add<CancelOperationArguments>("builder_cancel_operation","Request cancellation of this connection's exact active operation ID from builder_scene.state.operation. Available while busy; no scene revision required. Cancellation is best effort: an edit/file already committed remains accepted. Re-read state after it settles. Cancels pending work, not a published preview; use builder_cancel_preview for that.",false,(a,ct,_)=>
        {
            ct.ThrowIfCancellationRequested();
            lock(ownership)
            {
                if(pendingOperation is not {} job||job.Id!=a.OperationId)return Task.FromResult(Reply(new(Outcome.Conflict,"This operation is no longer active for this connection.")));
                job.Cancellation.Cancel();
            }
            return Task.FromResult(Reply(new(Outcome.Success,"Cancellation requested. Query state until isBusy is false; already committed changes remain accepted.")));
        },readOnly:false);
        Add<SelectionArguments>("builder_select","Replace selection using explicit leaf IDs; scene scope expands whole groups. Selection changes revision but not document content/history. Supply exact memberGroupId in member scope.",true,(a,ct,_)=>
        {Context(a.ExpectedRevision,a.MemberGroupId);ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.SelectMany(a.ObjectIds,a.PrimaryObjectId,a.ExpectedRevision)));});
        Add<EnterGroupArguments>("builder_enter_group","Enter independent member editing without ungrouping or creating a history edit. Optional memberId chooses the initial member.",true,(a,ct,_)=>
        {ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.EnterGroup(a.GroupId,a.MemberId,a.ExpectedRevision)));});
        Add<RevisionArguments>("builder_exit_group","Done editing members; select the whole group without creating a history edit.",true,(a,ct,_)=>
        {ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.ExitGroup(a.ExpectedRevision)));});
        EditTool<AddCube>("builder_add_cube","Add a20mm cube. For custom shapes and tessellation use primitive preview tools.");
        EditTool<AddPrimitive>("builder_add_primitive","Add a primitive with explicit physical size; advanced primitive previews expose radius/tessellation controls.");
        EditTool<RenameObject>("builder_rename","Rename an object/member; use builder_rename_group for a group.");
        EditTool<RenameGroup>("builder_rename_group","Rename an entire flat group.");
        EditTool<ResizeObject>("builder_resize","Set positive world bounds size in millimeters about its center. For a staged relative size edit use builder_preview_scale.");
        EditTool<MoveObject>("builder_move_to","Set the object's world bounds center in millimeters; groups use builder_preview_move.");
        EditTool<RotateObject>("builder_rotate","Rotate one object about its bounds center, world X thenY thenZ degrees; groups use builder_preview_rotate.");
        EditTool<PlaceOnBed>("builder_place_on_bed","Translate one object until its minimum world Z is zero; whole sets use builder_preview_center_on_plate.");
        Add<BulkArguments>("builder_duplicate","Duplicate explicit objects, groups or scoped members as one cancellable accepted edit, retaining groups/materials and selecting copies.",true,async(a,ct,p)=>
            Reply(await editor.DuplicateSelectionAsync(a.ObjectIds,ct,p,a.ExpectedRevision,a.MemberGroupId).ConfigureAwait(false)));
        Add<BulkArguments>("builder_delete","Delete explicit objects/groups or scoped members as one cancellable accepted edit. Group membership limits still apply.",true,async(a,ct,p)=>
            Reply(await editor.DeleteSelectionAsync(a.ObjectIds,ct,p,a.ExpectedRevision,a.MemberGroupId).ConfigureAwait(false)));
        Add<RevisionArguments>("builder_undo","Undo one accepted edit through shared history; restores scope/selection. Apply or cancel any preview first.",true,(a,ct,_)=>
        {ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.Undo(a.ExpectedRevision)));});
        Add<RevisionArguments>("builder_redo","Redo one accepted edit through shared history; restores the same copied identities. Apply or cancel any preview first.",true,(a,ct,_)=>
        {ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.Redo(a.ExpectedRevision)));});
        Add<MetadataArguments>("builder_model_metadata","Replace title/designer/description/copyright/license/language metadata as one undoable edit, persisted in native3MF.",true,(a,ct,_)=>
            Task.FromResult(Reply(editor.UpdateModelMetadata(a.Metadata,a.ExpectedRevision,ct))));
        Add<ViewArguments>("builder_view","Update display settings using the separate presentation revision; never edits meshes, document history or model files. Desktop persists its configured preferences.",false,(a,ct,_)=>
        {ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.Presentation.Update(a.Settings,a.ExpectedPresentationRevision)));},readOnly:false);
        Add<ViewResetArguments>("builder_reset_view","Reset display flags to remembered defaults using the separate presentation revision; never edits the document.",false,(a,ct,_)=>
        {ct.ThrowIfCancellationRequested();return Task.FromResult(Reply(editor.Presentation.Reset(a.ExpectedPresentationRevision)));},readOnly:false);
        Add<PreviewTokenArguments>("builder_apply_preview","Accept only this connection's exact current preview token as one Undo step. expectedRevision must still match its base revision.",true,(a,ct,_)=>Task.FromResult(CompletePreview(a,true,ct)));
        Add<PreviewTokenArguments>("builder_cancel_preview","Discard only this connection's exact current preview token. Accepted scene/history/native path stay unchanged.",true,(a,ct,_)=>Task.FromResult(CompletePreview(a,false,ct)));
    }
}
