using System.Collections.Immutable;
using System.Text;

namespace Builder.Core;

/// <summary>Read-only tree metadata. A group root contains leaf children; an independent leaf has no children.
/// ObjectIds always contains leaf identities to pass to the shared whole-group selection boundary.</summary>
public sealed record SceneTreeNode(Guid Id, string Name, ImmutableArray<Guid> ObjectIds, ImmutableArray<SceneTreeNode> Children);

public sealed partial record Scene
{
    /// <summary>Lists group roots and independent leaves in document order; group children also follow document order.
    /// This immutable metadata query computes no geometry and does not change selection or membership.</summary>
    public ImmutableArray<SceneTreeNode> GetObjectTree()
    {
        var membership=Groups.SelectMany(g=>g.ObjectIds.Select(id=>(id,group:g))).ToDictionary(p=>p.id,p=>p.group);
        var leaves=Objects.ToDictionary(o=>o.Id,o=>new SceneTreeNode(o.Id,o.Name,[o.Id],[]));
        var seen=new HashSet<Guid>(); var roots=ImmutableArray.CreateBuilder<SceneTreeNode>();
        foreach(var obj in Objects)
        {
            if(!membership.TryGetValue(obj.Id,out var group)) { roots.Add(leaves[obj.Id]); continue; }
            if(!seen.Add(group.Id)) continue;
            var children=Objects.Where(o=>group.ObjectIds.Contains(o.Id)).Select(o=>leaves[o.Id]).ToImmutableArray();
            roots.Add(new(group.Id,group.Name,children.Select(c=>c.Id).ToImmutableArray(),children));
        }
        return roots.ToImmutable();
    }
}

/// <summary>Renames a group with a trimmed 1–100 character name without controls, preserving group/member IDs and full selection.</summary>
public sealed record RenameGroup(Guid GroupId, string Name) : Edit;

public sealed partial class EditorSession
{
    // Called only by Execute after its guard, while the gate is held. Only validated group metadata changes.
    private OperationResult RenameGroupAccepted(RenameGroup edit)
    {
        var name=Scene.ValidateGroupName(edit.Name);
        var group=scene.Groups.FirstOrDefault(g=>g.Id==edit.GroupId) ?? throw new ModelException("Group not found.");
        if(group.Name==name) return new(Outcome.Success,"No change.");
        var candidate=scene with {ContentId=Guid.NewGuid(),Groups=scene.Groups.Replace(group,group with {Name=name})};
        CommitGroupEdit(candidate,selectedIds,selected);
        return new(Outcome.Success,"Group renamed. You can undo it.");
    }

    /// <summary>
    /// Duplicates explicit leaf targets after expanding whole groups. The complete set moves by its world X
    /// extent plus 5 mm. New object/group IDs are created; source meshes are shared. Copies are selected,
    /// mapping the existing primary when included. One undo step commits after private validation/cancellation.
    /// In exact memberGroupId scope only requested members are copied and appended to the same group.
    /// </summary>
    public Task<OperationResult> DuplicateSelectionAsync(ImmutableArray<Guid> objectIds, CancellationToken cancellation = default,
        IProgress<OperationProgress>? progress = null, long? expectedRevision = null,Guid? memberGroupId=null)
        => RunIo(()=> {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new("Preparing duplicate")); cancellation.ThrowIfCancellationRequested();
            var captured=Snapshot; var ids=captured.Scene.ExpandObjectIds(objectIds,captured.EditingGroupId);
            if(ids.IsEmpty) throw new ModelException("Choose objects to duplicate.");
            var targets=ids.ToHashSet(); var originals=captured.Scene.Objects.Where(o=>targets.Contains(o.Id)).ToImmutableArray();
            if(captured.Scene.Objects.Length+originals.Length > ModelLimits.MaxObjects ||
                captured.Scene.TriangleCount+originals.Sum(o=>o.Mesh.Triangles.Length) > ModelLimits.MaxTriangles ||
                captured.Scene.Objects.Sum(o=>(long)o.Mesh.Vertices.Length)+originals.Sum(o=>(long)o.Mesh.Vertices.Length) > ModelLimits.MaxVertices)
                throw new ModelException("Duplicating this selection would exceed the scene object, triangle or vertex limit.");
            var bounds=GroupEditBounds(originals,cancellation,progress,"Measuring duplicate bounds");
            var translation=Affine.Translation(new(bounds.Size.X+5,0,0));
            var mapping=new Dictionary<Guid,Guid>(); var copies=ImmutableArray.CreateBuilder<SceneObject>();
            foreach(var original in originals)
            {
                cancellation.ThrowIfCancellationRequested();
                var id=Guid.NewGuid(); mapping.Add(original.Id,id);
                copies.Add(original with {Id=id,Name=CopyName(original.Name),Transform=original.Transform.Then(translation)});
            }
            var copiedGroups=ImmutableArray.CreateBuilder<SceneGroup>();
            foreach(var group in captured.Scene.Groups)
            {
                cancellation.ThrowIfCancellationRequested();
                if(captured.EditingGroupId.HasValue || !group.ObjectIds.Any(targets.Contains)) continue;
                copiedGroups.Add(new(Guid.NewGuid(),CopyName(group.Name),group.ObjectIds.Select(id=>mapping[id]).ToImmutableArray()));
            }
            var copyObjects=copies.ToImmutable();
            _=GroupEditBounds(copyObjects,cancellation,progress,"Duplicating objects");
            var candidate=captured.Scene.WithObjects(captured.Scene.Objects.AddRange(copyObjects))
                with {Groups=captured.Scene.Groups.AddRange(copiedGroups)};
            if(captured.EditingGroupId is {} scope) candidate=candidate.ReplaceMemberIds(scope,[],copyObjects.Select(o=>o.Id));
            candidate.Validate(cancellation);
            var nextIds=copyObjects.Select(o=>o.Id).ToImmutableArray();
            var primary=captured.SelectedObjectId is {} old && mapping.TryGetValue(old,out var mapped) ? mapped : nextIds[0];
            progress?.Report(new("Ready to duplicate")); cancellation.ThrowIfCancellationRequested();
            lock(gate) { cancellation.ThrowIfCancellationRequested(); CommitGroupEdit(candidate,nextIds,primary); }
            return $"Duplicated {copyObjects.Length} objects. You can undo it.";
        },expectedRevision,preflight:()=>GuardMemberScope(memberGroupId));

    /// <summary>
    /// Deletes explicit leaf targets after expanding whole groups, also removing their group records.
    /// Surviving selected leaves and primary are retained. With no selected survivors selection becomes empty,
    /// even if other objects remain. One undo step restores the complete original scene and selection.
    /// In exact memberGroupId scope only requested members are removed; at least two group members must remain.
    /// </summary>
    public Task<OperationResult> DeleteSelectionAsync(ImmutableArray<Guid> objectIds, CancellationToken cancellation = default,
        IProgress<OperationProgress>? progress = null, long? expectedRevision = null,Guid? memberGroupId=null)
        => RunIo(()=> {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new("Preparing deletion")); cancellation.ThrowIfCancellationRequested();
            var captured=Snapshot; var ids=captured.Scene.ExpandObjectIds(objectIds,captured.EditingGroupId);
            if(ids.IsEmpty) throw new ModelException("Choose objects to delete.");
            var targets=ids.ToHashSet(); var objects=ImmutableArray.CreateBuilder<SceneObject>();
            for(var i=0;i<captured.Scene.Objects.Length;i++)
            {
                if(i%64==0) { progress?.Report(new("Deleting objects",i,captured.Scene.Objects.Length)); cancellation.ThrowIfCancellationRequested(); }
                var obj=captured.Scene.Objects[i]; if(!targets.Contains(obj.Id)) objects.Add(obj);
            }
            var groups=captured.EditingGroupId.HasValue ? captured.Scene.Groups
                : captured.Scene.Groups.Where(g=>!g.ObjectIds.Any(targets.Contains)).ToImmutableArray();
            var candidate=captured.Scene.WithObjects(objects.ToImmutable()) with {Groups=groups};
            if(captured.EditingGroupId is {} scope) candidate=candidate.ReplaceMemberIds(scope,ids,[]);
            candidate.Validate(cancellation);
            var nextIds=captured.SelectedObjectIds.Where(id=>!targets.Contains(id)).ToImmutableArray();
            if(nextIds.SequenceEqual(captured.SelectedObjectIds)) nextIds=captured.SelectedObjectIds;
            Guid? primary=captured.SelectedObjectId is {} old && nextIds.Contains(old) ? old : nextIds.IsEmpty ? null : nextIds[0];
            progress?.Report(new("Ready to delete")); cancellation.ThrowIfCancellationRequested();
            lock(gate) { cancellation.ThrowIfCancellationRequested(); CommitGroupEdit(candidate,nextIds,primary); }
            return $"Deleted {ids.Length} objects. You can undo it.";
        },expectedRevision,preflight:()=>GuardMemberScope(memberGroupId));

    private void CommitGroupEdit(Scene candidate,ImmutableArray<Guid> selection,Guid? primary)
    {
        undo.Add((scene,selected,selectedIds,editingGroupId)); if(undo.Count>100) undo.RemoveAt(0);
        redo.Clear(); scene=candidate; selectedIds=selection; selected=primary; revision++;
    }

    private static Bounds3 GroupEditBounds(ImmutableArray<SceneObject> objects,CancellationToken cancellation,
        IProgress<OperationProgress>? progress,string stage)
    {
        var total=objects.Sum(o=>o.Mesh.Vertices.Length); var completed=0;
        IEnumerable<Bounds3> Bounds()
        {
            foreach(var obj in objects)
            {
                yield return GeometryCache.Bounds(obj,cancellation,progress,stage,completed,total);
                completed+=obj.Mesh.Vertices.Length;
            }
            cancellation.ThrowIfCancellationRequested();
        }
        return GeometryCache.Union(Bounds());
    }

    private static string CopyName(string name)
    {
        const string suffix=" copy"; var builder=new StringBuilder();
        foreach(var rune in (name ?? "Object").Trim().EnumerateRunes())
        {
            if(Rune.IsControl(rune)) continue;
            var text=rune.ToString();
            if(builder.Length+text.Length > 100-suffix.Length) break;
            builder.Append(text);
        }
        var stem=builder.ToString().Trim();
        return (stem.Length==0 ? "Object" : stem)+suffix;
    }
}
