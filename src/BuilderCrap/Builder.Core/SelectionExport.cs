using System.Collections.Immutable;

namespace Builder.Core;

public sealed partial class EditorSession
{
    /// <summary>
    /// Exports explicit leaf targets as STL (geometry only), OBJ/MTL or flattened PLY (display colors), glTF/GLB, or 3MF (native scene properties).
    /// Scene-scope targets expand to complete groups.
    /// In exact memberGroupId scope, partial member selections export independent leaves; a full group may retain its record.
    /// This does not change accepted content, selection, history, revision, dirty identity or native path.
    /// Apply/discard any pending preview first. A selection export cannot replace this session's native document.
    /// </summary>
    public Task<OperationResult> ExportSelectionAsync(string filePath, ImmutableArray<Guid> objectIds,
        CancellationToken cancellation = default, IProgress<OperationProgress>? progress = null, long? expectedRevision = null,
        Guid? memberGroupId=null,ModelUnit unit=ModelUnit.Millimeters)
        => RunIo(() => {
            void Check(string stage,int completed=0,int total=0)
            { progress?.Report(new(stage,completed,total)); cancellation.ThrowIfCancellationRequested(); }
            Check("Preparing selection export");
            var fullPath=Path.GetFullPath(filePath);
            var extension=Path.GetExtension(fullPath);
            if(!extension.Equals(".stl",StringComparison.OrdinalIgnoreCase) && !extension.Equals(".3mf",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".obj",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".ply",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".gltf",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".glb",StringComparison.OrdinalIgnoreCase))
                throw new ModelException("Export the selected objects as .stl, .obj, .ply, .gltf, .glb or .3mf.");
            var captured=Snapshot;
            if(captured.FilePath is {} nativePath && string.Equals(fullPath,Path.GetFullPath(nativePath),StringComparison.OrdinalIgnoreCase))
                throw new ModelException("Choose a different destination for selection export. Use Save to replace the whole native scene.");
            var targets=captured.Scene.ExpandObjectIds(objectIds,captured.EditingGroupId);
            if(targets.IsEmpty) throw new ModelException("Choose at least one object to export.");
            var included=targets.ToHashSet();
            var objects=ImmutableArray.CreateBuilder<SceneObject>(targets.Length);
            foreach(var obj in captured.Scene.Objects)
            {
                cancellation.ThrowIfCancellationRequested();
                if(!included.Contains(obj.Id)) continue;
                objects.Add(obj); Check("Preparing selected objects",objects.Count,targets.Length);
            }
            var groups=ImmutableArray.CreateBuilder<SceneGroup>();
            foreach(var group in captured.Scene.Groups)
            {
                Check("Preparing selected groups",groups.Count,captured.Scene.Groups.Length);
                // Scene scope expands whole groups. Member scope may intentionally export only
                // some leaves; preserve a group record only when every member is included.
                if(group.ObjectIds.All(included.Contains)) groups.Add(group);
            }
            var subset=new Scene(Guid.NewGuid(),objects.ToImmutable()) {Groups=groups.ToImmutable(),Metadata=captured.Scene.Metadata};
            cancellation.ThrowIfCancellationRequested();
            if(extension.Equals(".obj",StringComparison.OrdinalIgnoreCase))ObjFiles.WriteAtomic(fullPath,subset,unit,cancellation,progress);
            else if(extension.Equals(".ply",StringComparison.OrdinalIgnoreCase))PlyFiles.WriteAtomic(fullPath,subset,unit,cancellation,progress);
            else if(extension.Equals(".gltf",StringComparison.OrdinalIgnoreCase)||extension.Equals(".glb",StringComparison.OrdinalIgnoreCase))GltfFiles.WriteAtomic(fullPath,subset,unit,cancellation,progress);
            else ModelFiles.WriteAtomic(fullPath,subset,extension.Equals(".stl",StringComparison.OrdinalIgnoreCase),cancellation,progress);
            // Like whole-scene export, cancellation after the atomic replacement is success.
            return $"Exported {objects.Count} selected object{(objects.Count==1 ? "" : "s")} to {Path.GetFileName(fullPath)}.";
        },expectedRevision,preflight:()=>GuardMemberScope(memberGroupId));
}
