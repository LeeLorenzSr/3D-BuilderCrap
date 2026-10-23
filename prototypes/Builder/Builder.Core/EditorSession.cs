using System.Collections.Immutable;

namespace Builder.Core;

public enum Outcome { Success, Cancelled, Invalid, Failed, Busy, Conflict }
public sealed record OperationResult(Outcome Outcome, string Message)
{
    public bool Succeeded => Outcome == Outcome.Success;
}
public sealed record OperationProgress(string Stage, int Completed = 0, int Total = 0);
public enum ModelUnit { Millimeters, Centimeters, Inches, Meters }
public abstract record Edit
{
    /// <summary>Exact member-editing group scope; null means scene scope. Stale scope requests return Conflict.</summary>
    public Guid? MemberGroupId { get; init; }
}
public sealed record ResizeObject(Guid ObjectId, Vec3 SizeMillimeters) : Edit;
public sealed record MoveObject(Guid ObjectId, Vec3 CenterMillimeters) : Edit;
/// <summary>Relative right-handed WORLD X, then Y, then Z rotation, in degrees, about the current world bounds center.</summary>
public sealed record RotateObject(Guid ObjectId, Vec3 Degrees) : Edit;
/// <summary>Trimmed 1–100 character name without control characters; persisted in native 3MF.</summary>
public sealed record RenameObject(Guid ObjectId, string Name) : Edit;
public sealed record PlaceOnBed(Guid ObjectId) : Edit;
public sealed record DuplicateObject(Guid ObjectId) : Edit;
public sealed record DeleteObject(Guid ObjectId) : Edit;
public sealed record AddCube : Edit;
/// <summary>Adds a solid with local bounds (0,0,0)..SizeMillimeters, on the bed. See PrimitiveKind for tessellation.</summary>
public sealed record AddPrimitive(PrimitiveKind Kind, Vec3 SizeMillimeters) : Edit;

public sealed record EditorSnapshot(Scene Scene, Guid? SelectedObjectId, string? FilePath,
    bool IsDirty, bool CanUndo, bool CanRedo, long Revision, bool IsBusy, GeometryPreview? Preview = null)
{
    /// <summary>Authoritative selected leaf IDs in document order, including every member of each selected group.</summary>
    public ImmutableArray<Guid> SelectedObjectIds { get; init; } = SelectedObjectId is {} id ? [id] : [];
    /// <summary>Active member-editing group, or null for ordinary whole-group scene selection.</summary>
    public Guid? EditingGroupId { get; init; }
    /// <summary>Selection units in the current scope: independent members inside a group, whole groups outside.</summary>
    public ImmutableArray<SceneSelectionUnit> GetSelectionUnits()=>Scene.GetSelectionUnits(SelectedObjectIds,EditingGroupId);
}

/// <summary>
/// The single application boundary for desktop and future MCP callers. Thread-safe; all mutations
/// are serialized. Snapshots and meshes are immutable. I/O owns a busy lease until commit or rollback.
/// </summary>
public sealed partial class EditorSession
{
    private readonly object gate = new();
    private Scene scene = Scene.Empty;
    private Guid savedContent;
    private Guid? selected;
    private ImmutableArray<Guid> selectedIds = [];
    private string? path;
    private long revision;
    private bool busy;
    private readonly List<(Scene Scene, Guid? Selected, ImmutableArray<Guid> SelectedIds,Guid? EditingGroupId)> undo = [], redo = [];
    private readonly GeometryWorker geometry;
    private readonly ITextOutlineProvider? textOutlineProvider;
    private readonly IRasterImageDecoder? imageDecoder;
    public EditorSession(string? geometryWorkerPath = null,ITextOutlineProvider? textOutlineProvider=null,IRasterImageDecoder? imageDecoder=null)
    {
        savedContent = scene.ContentId;
        geometry = new(geometryWorkerPath ?? Path.Combine(AppContext.BaseDirectory,"Builder.GeometryWorker.exe"));
        this.textOutlineProvider=textOutlineProvider;
        this.imageDecoder=imageDecoder;
    }
    public EditorSnapshot Snapshot
    {
        get { lock(gate) return new(scene, selected, path, scene.ContentId != savedContent,
            undo.Count > 0, redo.Count > 0, revision, busy, preview) {SelectedObjectIds=selectedIds,EditingGroupId=editingGroupId}; }
    }
    private OperationResult? Guard(long? expectedRevision, bool allowPreview = false)
    {
        if (disposeRequested) return new(Outcome.Conflict,"This editor session is closed.");
        if (busy) return new(Outcome.Busy,"Wait for the current operation or cancel it first.");
        if (!allowPreview && preview != null) return new(Outcome.Conflict,"Apply or cancel the preview first.");
        if (expectedRevision.HasValue && revision != expectedRevision.Value)
            return new(Outcome.Conflict,"The scene changed. Read it again before applying this operation.");
        return null;
    }
    /// <summary>Replaces selection with one leaf's whole group, or clears it. Does not create a dirty edit/history step.</summary>
    public OperationResult Select(Guid? objectId)=>SelectMany(objectId is {} id ? [id] : [],objectId);

    /// <summary>
    /// Replaces selection with deduplicated, document-ordered leaf IDs, expanding whole groups in scene scope.
    /// In member scope only raw leaves of the active group are allowed, without group expansion. The optional
    /// primary must be a selected leaf; otherwise the first selected leaf becomes primary. Enumeration is
    /// bounded to 1,000 items outside the session lock. Selection changes revision but not content/history.
    /// </summary>
    public OperationResult SelectMany(IEnumerable<Guid> objectIds, Guid? primaryObjectId = null, long? expectedRevision = null)
    {
        Scene captured; long capturedRevision; Guid? capturedScope;
        lock(gate)
        {
            if(Guard(expectedRevision) is {} blocked) return blocked;
            captured=scene; capturedRevision=revision; capturedScope=editingGroupId;
        }
        ImmutableArray<Guid> ids;
        try { ids=captured.ResolveObjectIds(objectIds,capturedScope); }
        catch(ModelException e) { return new(Outcome.Invalid,e.Message); }
        if(primaryObjectId.HasValue && !ids.Contains(primaryObjectId.Value)) return new(Outcome.Invalid,"The primary object must belong to the selection.");
        var primary=primaryObjectId ?? (ids.Length > 0 ? ids[0] : (Guid?)null);
        lock(gate)
        {
            if(Guard(expectedRevision) is {} blocked) return blocked;
            if(revision!=capturedRevision) return new(Outcome.Conflict,"The scene changed while reading the selection.");
            if(selected!=primary || !selectedIds.SequenceEqual(ids)) { selected=primary; selectedIds=ids; revision++; }
            return new(Outcome.Success,"Selection updated.");
        }
    }
    public OperationResult Execute(Edit edit, long? expectedRevision = null)
    {
        lock(gate)
        {
            if (Guard(expectedRevision) is { } blocked) return blocked;
            try
            {
                if(edit is null) throw new ModelException("Choose an edit.");
                if(GuardMemberScope(edit.MemberGroupId) is {} scopeConflict) return scopeConflict;
                if(editingGroupId.HasValue && edit is AddCube or AddPrimitive or RenameGroup) return DoneWithGroup();
                if(editingGroupId.HasValue && edit is DuplicateObject or DeleteObject)
                    throw new ModelException("Use the scoped DuplicateSelectionAsync or DeleteSelectionAsync member operation.");
                if(edit is RenameGroup renameGroup) return RenameGroupAccepted(renameGroup);
                var objects = scene.Objects;
                Guid? nextSelection = selected;
                var nextSelectedIds = selectedIds;
                var objectId = edit switch {
                    ResizeObject e => e.ObjectId, MoveObject e => e.ObjectId, RotateObject e => e.ObjectId,
                    RenameObject e => e.ObjectId, PlaceOnBed e => e.ObjectId,
                    DuplicateObject e => e.ObjectId, DeleteObject e => e.ObjectId, AddCube or AddPrimitive => Guid.Empty,
                    _ => throw new ModelException("Unknown edit.") };
                var index = -1;
                for (var i = 0; i < objects.Length; i++) if (objects[i].Id == objectId) index = i;
                if (edit is AddCube)
                {
                    var cube = new SceneObject(Guid.NewGuid(),"Cube",Mesh.Cube,Affine.Identity);
                    objects = objects.Add(cube); nextSelection = cube.Id; nextSelectedIds=[cube.Id];
                }
                else if (edit is AddPrimitive primitive)
                {
                    var obj = new SceneObject(Guid.NewGuid(),primitive.Kind.ToString(),
                        Primitives.Create(primitive.Kind,primitive.SizeMillimeters),Affine.Identity);
                    objects = objects.Add(obj); nextSelection = obj.Id; nextSelectedIds=[obj.Id];
                }
                else
                {
                    if (index < 0) throw new ModelException("Object not found.");
                    var obj = objects[index];
                    _=scene.ExpandObjectIds([obj.Id],editingGroupId);
                    if(editingGroupId is null && edit is not RenameObject && scene.IsGrouped(obj.Id))
                        throw new ModelException("This object belongs to a group. Use a group transform or Ungroup before this single-object edit.");
                    var bounds = obj.Bounds;
                    switch(edit)
                    {
                        case RotateObject e:
                            var rotation = Affine.RotateAbout(e.Degrees,bounds.Center);
                            objects = objects.SetItem(index,obj with { Transform = obj.Transform.Then(rotation) }); break;
                        case RenameObject e:
                            if (e.Name is null || e.Name.Any(char.IsControl))
                                throw new ModelException("Enter a name without control characters.");
                            var name = e.Name.Trim();
                            if (name.Length is < 1 or > 100) throw new ModelException("Enter a name from 1 to 100 characters.");
                            objects = objects.SetItem(index,obj with { Name = name }); break;
                        case ResizeObject e:
                            var size = e.SizeMillimeters;
                            if (!size.IsFinite || size.X <= 0 || size.Y <= 0 || size.Z <= 0)
                                throw new ModelException("Enter a positive, finite size for each axis.");
                            if (bounds.Size.X <= 0 || bounds.Size.Y <= 0 || bounds.Size.Z <= 0)
                                throw new ModelException("A flat object cannot be resized along a zero-width axis yet.");
                            obj = obj with { Transform = obj.Transform.Then(Affine.ScaleAbout(
                                new(size.X/bounds.Size.X,size.Y/bounds.Size.Y,size.Z/bounds.Size.Z),bounds.Center)) };
                            objects = objects.SetItem(index,obj); break;
                        case MoveObject e:
                            if (!e.CenterMillimeters.IsFinite) throw new ModelException("Enter a finite position for each axis.");
                            objects = objects.SetItem(index,obj with { Transform = obj.Transform.Then(Affine.Translation(e.CenterMillimeters-bounds.Center)) }); break;
                        case PlaceOnBed:
                            objects = objects.SetItem(index,obj with { Transform = obj.Transform.Then(Affine.Translation(new(0,0,-bounds.Min.Z))) }); break;
                        case DuplicateObject:
                            var copy = obj with { Id = Guid.NewGuid(), Name = obj.Name + " copy",
                                Transform = obj.Transform.Then(Affine.Translation(new(bounds.Size.X+5,0,0))) };
                            objects = objects.Add(copy); nextSelection = copy.Id; nextSelectedIds=[copy.Id]; break;
                        case DeleteObject:
                            objects = objects.RemoveAt(index);
                            nextSelectedIds=selectedIds.Where(id=>id!=obj.Id).ToImmutableArray();
                            if(nextSelectedIds.Length==0)
                            {
                                nextSelection=objects.Length > 0 ? objects[Math.Min(index,objects.Length-1)].Id : null;
                                nextSelectedIds=nextSelection is {} nextId ? scene.WithObjects(objects).ExpandObjectIds([nextId]) : [];
                            }
                            else if(nextSelection==obj.Id) nextSelection=nextSelectedIds[0];
                            break;
                    }
                }
                if (objects.SequenceEqual(scene.Objects)) return new(Outcome.Success,"No change.");
                var candidate = scene.WithObjects(objects);
                candidate.Validate();
                undo.Add((scene,selected,selectedIds,editingGroupId));
                // Bound retained history; meshes are shared, transforms are small value records.
                if (undo.Count > 100) undo.RemoveAt(0);
                redo.Clear(); scene = candidate; selected = nextSelection; selectedIds=nextSelectedIds; revision++;
                return new(Outcome.Success,"Edit applied. You can undo it.");
            }
            catch (ModelException e) { return new(Outcome.Invalid,e.Message); }
        }
    }
    public OperationResult Undo(long? expectedRevision = null) => Travel(undo,redo,expectedRevision,"Undone.");
    public OperationResult Redo(long? expectedRevision = null) => Travel(redo,undo,expectedRevision,"Redone.");
    private OperationResult Travel(List<(Scene Scene, Guid? Selected, ImmutableArray<Guid> SelectedIds,Guid? EditingGroupId)> from,
        List<(Scene Scene, Guid? Selected, ImmutableArray<Guid> SelectedIds,Guid? EditingGroupId)> to,
        long? expectedRevision, string message)
    {
        lock(gate)
        {
            if (Guard(expectedRevision) is { } blocked) return blocked;
            if (from.Count == 0) return new(Outcome.Invalid,"There are no more history steps.");
            if(from[^1].Scene.ContentId==savedContent)
            {
                try{recovery?.RetireOwn();}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException or ModelException)
                {return new(Outcome.Failed,"Recovery could not be retired; history is unchanged. "+e.Message);}
            }
            to.Add((scene,selected,selectedIds,editingGroupId)); (scene,selected,selectedIds,editingGroupId) = from[^1]; from.RemoveAt(from.Count-1); revision++;
            return new(Outcome.Success,message);
        }
    }
    /// <summary>Safely opens a replacement. Dirty content requires an explicit decision; existing callers
    /// retain source compatibility but no longer silently discard unsaved work. Prompts should pass their
    /// captured expectedRevision, or use TransitionAsync for SaveCommitted and close-readiness details.
    /// Omitted import units mean meters for glTF/GLB and millimeters for STL/OBJ/PLY.</summary>
    public async Task<OperationResult> OpenAsync(string filePath, ModelUnit? unit = null,
        CancellationToken cancellation = default, IProgress<OperationProgress>? progress = null, long? expectedRevision = null,
        DirtyDecision decision=DirtyDecision.RequireDecision,string? savePath=null,StlImportMode stlMode=StlImportMode.Strict)
        => (await TransitionAsync(new OpenDocumentTransition(filePath,expectedRevision??Snapshot.Revision,unit,decision,savePath){StlMode=stlMode},cancellation,progress).ConfigureAwait(false)).Result;

    /// <summary>Appends a validated import as one undoable edit and selects every imported leaf/group as a set.
    /// Existing objects and the native save path are preserved; the first imported leaf is primary.</summary>
    public Task<OperationResult> InsertAsync(string filePath, ModelUnit? unit = null,
        CancellationToken cancellation = default, IProgress<OperationProgress>? progress = null, long? expectedRevision = null,
        StlImportMode stlMode=StlImportMode.Strict)
        => RunIo(() => {
            var imported = ModelFiles.Read(Path.GetFullPath(filePath),unit,cancellation,progress,stlMode);
            var captured = Snapshot;
            var candidate = captured.Scene.WithObjects(captured.Scene.Objects.AddRange(imported.Objects))
                with {Groups=captured.Scene.Groups.AddRange(imported.Groups)};
            candidate.Validate(cancellation);
            if (imported.Objects.IsEmpty) throw new ModelException("The file contains no objects to insert.");
            progress?.Report(new("Ready to insert")); cancellation.ThrowIfCancellationRequested();
            lock(gate)
            {
                cancellation.ThrowIfCancellationRequested();
                undo.Add((scene,selected,selectedIds,editingGroupId)); if (undo.Count > 100) undo.RemoveAt(0);
                redo.Clear(); scene = candidate; selected = imported.Objects[0].Id;
                selectedIds=candidate.ExpandObjectIds(imported.Objects.Select(o=>o.Id).ToImmutableArray()); revision++;
            }
            return "Inserted " + Path.GetFileName(filePath) + ". You can undo it."+
                (imported.Metadata.IsEmpty?"":" Imported model information was not merged; the current document's information was retained.")+
                (stlMode==StlImportMode.GeometryOnly&&Path.GetExtension(filePath).Equals(".stl",StringComparison.OrdinalIgnoreCase)?" Geometry only: STL attribute/color data was not imported.":"");
        },expectedRevision,preflight:()=>!Enum.IsDefined(stlMode)?new OperationResult(Outcome.Invalid,"Choose Strict or Geometry only for STL import."):editingGroupId.HasValue ? DoneWithGroup() : null);

    public Task<OperationResult> SaveAsync(string filePath, CancellationToken cancellation = default,
        IProgress<OperationProgress>? progress = null, long? expectedRevision = null)
        => WriteAsync(filePath,false,cancellation,progress,expectedRevision);
    public Task<OperationResult> ExportStlAsync(string filePath, CancellationToken cancellation = default,
        IProgress<OperationProgress>? progress = null, long? expectedRevision = null)
        => WriteAsync(filePath,true,cancellation,progress,expectedRevision);
    /// <summary>Exports world triangles and opaque diffuse materials in explicit unitless OBJ units.
    /// Native path, content/dirty identity, history and selection are unchanged. Native 3MF retains metadata.</summary>
    public Task<OperationResult> ExportObjAsync(string filePath,ModelUnit unit,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
        =>RunIo(()=>
        {
            var captured=Snapshot;var fullPath=Path.GetFullPath(filePath);
            if(captured.FilePath is {} native&&string.Equals(Path.GetFullPath(native),fullPath,StringComparison.OrdinalIgnoreCase))
                throw new ModelException("Choose another destination for OBJ export. Use Save for the native document.");
            ObjFiles.WriteAtomic(fullPath,captured.Scene,unit,cancellation,progress);
            return "Exported OBJ and diffuse MTL materials in "+unit+". Native 3MF retains model information and editing structure.";
        },expectedRevision);
    /// <summary>Exports one flattened binary-little-endian PLY mesh with opaque effective face RGB in
    /// explicit units. Objects/groups and metadata remain native3MF properties; accepted state is unchanged.</summary>
    public Task<OperationResult> ExportPlyAsync(string filePath,ModelUnit unit,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
        =>RunIo(()=>
        {
            var captured=Snapshot;var fullPath=Path.GetFullPath(filePath);
            if(captured.FilePath is {} native&&string.Equals(Path.GetFullPath(native),fullPath,StringComparison.OrdinalIgnoreCase))
                throw new ModelException("Choose another destination for PLY export. Use Save for the native document.");
            PlyFiles.WriteAtomic(fullPath,captured.Scene,unit,cancellation,progress);
            return "Exported one flattened PLY mesh in "+unit+" with opaque display colors. Native3MF retains objects, groups and model information.";
        },expectedRevision);
    /// <summary>Exports static Y-up glTF2/GLB with opaque base colors. Vertices use chosen units;
    /// node scale expresses physical meters. Native path, selection, content and history are unchanged.</summary>
    public Task<OperationResult> ExportGltfAsync(string filePath,ModelUnit unit,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
        =>RunIo(()=>
        {
            var captured=Snapshot;var fullPath=Path.GetFullPath(filePath);
            if(captured.FilePath is {} native&&string.Equals(Path.GetFullPath(native),fullPath,StringComparison.OrdinalIgnoreCase))
                throw new ModelException("Choose another destination for glTF export. Use Save for the native document.");
            GltfFiles.WriteAtomic(fullPath,captured.Scene,unit,cancellation,progress);
            return "Exported static glTF with opaque display colors and standard meter node scaling. Native3MF retains model information.";
        },expectedRevision);
    private Task<OperationResult> WriteAsync(string filePath, bool stl, CancellationToken cancellation,
        IProgress<OperationProgress>? progress, long? expectedRevision)
        => RunIo(() => {
            var fullPath = Path.GetFullPath(filePath);
            if (!Path.GetExtension(fullPath).Equals(stl ? ".stl" : ".3mf",StringComparison.OrdinalIgnoreCase))
                throw new ModelException(stl ? "Use .stl for mesh export." : "Use .3mf to save the scene.");
            var captured = Snapshot.Scene;
            if (captured.Objects.IsEmpty) throw new ModelException("There are no objects to save or export. Undo the deletion or add an object; choose Discard or Cancel if leaving this scene.");
            ModelFiles.WriteAtomic(fullPath,captured,stl,cancellation,progress);
            // Cancellation after the atomic replacement is success: the file is already committed.
            if (!stl) lock(gate) { path = fullPath; savedContent = captured.ContentId; revision++; }
            return (stl ? "Exported " : "Saved ") + Path.GetFileName(fullPath) + "."+(!stl?RetireRecoveryAfterSave():"");
        },expectedRevision);

    private Task<OperationResult> RunIo(Func<string> work, long? expectedRevision, bool allowPreview = false,
        Func<OperationResult?>? preflight=null)
    {
        lock(gate)
        {
            if (Guard(expectedRevision,allowPreview) is { } blocked) return Task.FromResult(blocked);
            if(preflight?.Invoke() is {} rejected) return Task.FromResult(rejected);
            busy = true;
        }
        return Task.Run(() => {
            try { return new OperationResult(Outcome.Success,work()); }
            catch (OperationCanceledException) { return new(Outcome.Cancelled,"Cancelled. Your scene is unchanged."); }
            catch (ModelException e) { return new(Outcome.Invalid,e.Message); }
            catch (RecoveryConflictException e) { return new(Outcome.Conflict,e.Message); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or
                FormatException or OverflowException or ArgumentException or NotSupportedException)
            { return new(Outcome.Failed,"Could not complete the operation: " + e.Message); }
            finally { lock(gate) {busy = false;if(disposeRequested)recovery?.Dispose();} }
        });
    }
}
