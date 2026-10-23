using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>A flat, disjoint group of at least two existing leaf objects. IDs and member IDs are document identities.</summary>
public sealed record SceneGroup(Guid Id, string Name, ImmutableArray<Guid> ObjectIds);
/// <summary>An indivisible selection/alignment unit: one group or one ungrouped object, with world bounds in millimeters.</summary>
public sealed record SceneSelectionUnit(Guid Id, string Name, ImmutableArray<Guid> ObjectIds, Bounds3 Bounds);

public sealed partial record Scene
{
    /// <summary>Flat groups; every leaf belongs to at most one group. Group IDs never collide with object IDs.</summary>
    public ImmutableArray<SceneGroup> Groups { get; init; } = [];

    /// <summary>Replaces objects with a new content identity, preserving all groups. Validate rejects dangling members.</summary>
    public Scene WithObjects(ImmutableArray<SceneObject> objects)
    {
        if(objects.IsDefault) throw new ModelException("An object collection is required.");
        return objects.SequenceEqual(Objects) ? this : this with {ContentId=Guid.NewGuid(),Objects=objects};
    }

    /// <summary>
    /// Resolves leaf IDs into whole groups or ungrouped objects, in document order. Input is deduplicated,
    /// group membership expands automatically, unknown IDs and more than 1,000 input items are rejected.
    /// Pass all scene leaf IDs to list every selection unit in scene scope. A memberGroupId confines the
    /// query to raw leaves in that group and treats each member as an independent unit. This query is read-only.
    /// </summary>
    public ImmutableArray<SceneSelectionUnit> GetSelectionUnits(IEnumerable<Guid> objectIds,Guid? memberGroupId=null)
        => GetSelectionUnits(ReadSelectionIds(objectIds),default,null,memberGroupId);

    internal ImmutableArray<SceneSelectionUnit> GetSelectionUnits(ImmutableArray<Guid> objectIds,
        CancellationToken cancellation,IProgress<OperationProgress>? progress,Guid? memberGroupId=null)
    {
        var ids=ExpandObjectIds(objectIds,memberGroupId).ToHashSet();
        var membership=Groups.Where(g=>g.Id!=memberGroupId).SelectMany(g=>g.ObjectIds.Select(id=>(id,group:g))).ToDictionary(p=>p.id,p=>p.group);
        var seen=new HashSet<Guid>(); var result=ImmutableArray.CreateBuilder<SceneSelectionUnit>();
        var selectedObjects=Objects.Where(o=>ids.Contains(o.Id)).ToArray();
        var membersByGroup=selectedObjects.Where(o=>membership.ContainsKey(o.Id)).GroupBy(o=>membership[o.Id].Id).ToDictionary(g=>g.Key,g=>g.ToArray());
        var total=selectedObjects.Sum(o=>o.Mesh.Vertices.Length); var completed=0;
        Bounds3 Measure(IEnumerable<SceneObject> members)
        {
            IEnumerable<Bounds3> Bounds()
            {
                foreach(var member in members)
                {
                    yield return GeometryCache.Bounds(member,cancellation,progress,"Measuring selection",completed,total);
                    completed+=member.Mesh.Vertices.Length;
                }
            }
            return GeometryCache.Union(Bounds());
        }
        foreach(var obj in Objects)
        {
            if(!ids.Contains(obj.Id)) continue;
            if(membership.TryGetValue(obj.Id,out var group))
            {
                if(!seen.Add(group.Id)) continue;
                var members=membersByGroup[group.Id];
                result.Add(new(group.Id,group.Name,members.Select(o=>o.Id).ToImmutableArray(),
                    Measure(members)));
            }
            else
            {
                var bounds=GeometryCache.Bounds(obj,cancellation,progress,"Measuring selection",completed,total);
                completed+=obj.Mesh.Vertices.Length;
                result.Add(new(obj.Id,obj.Name,[obj.Id],bounds));
            }
        }
        return result.ToImmutable();
    }

    internal bool IsGrouped(Guid objectId)=>Groups.Any(g=>g.ObjectIds.Contains(objectId));

    internal ImmutableArray<Guid> ResolveObjectIds(IEnumerable<Guid> requested,Guid? memberGroupId=null)
        =>ExpandObjectIds(ReadSelectionIds(requested),memberGroupId);

    internal ImmutableArray<Guid> ExpandObjectIds(ImmutableArray<Guid> requested,Guid? memberGroupId=null)
    {
        if(requested.IsDefault) throw new ModelException("An object selection is required.");
        if(requested.Length > ModelLimits.MaxObjects) throw new ModelException("Select at most 1,000 objects.");
        if(Groups.IsDefault) throw new ModelException("A group collection is required.");
        var ids=requested.ToHashSet(); var existing=Objects.Select(o=>o.Id).ToHashSet();
        if(ids.Any(id=>!existing.Contains(id))) throw new ModelException("One of the selected objects no longer exists.");
        if(memberGroupId is {} scope)
        {
            var group=Groups.FirstOrDefault(g=>g.Id==scope) ?? throw new ModelException("The edited group no longer exists.");
            if(ids.Any(id=>!group.ObjectIds.Contains(id))) throw new ModelException("Choose only members of the group being edited.");
        }
        else foreach(var group in Groups) if(group.ObjectIds.Any(ids.Contains)) ids.UnionWith(group.ObjectIds);
        return Objects.Where(o=>ids.Contains(o.Id)).Select(o=>o.Id).ToImmutableArray();
    }

    internal static ImmutableArray<Guid> ReadSelectionIds(IEnumerable<Guid> requested)
    {
        if(requested is null) throw new ModelException("An object selection is required.");
        var result=ImmutableArray.CreateBuilder<Guid>();
        try
        {
            foreach(var id in requested)
            {
                if(result.Count == ModelLimits.MaxObjects) throw new ModelException("Select at most 1,000 objects.");
                result.Add(id);
            }
        }
        catch(ModelException) { throw; }
        catch(Exception e) when(e is not OutOfMemoryException)
        { throw new ModelException("Could not enumerate the object selection: "+e.Message); }
        return result.ToImmutable();
    }

    internal static string ValidateGroupName(string name)
    {
        if(name is null || name.Any(char.IsControl)) throw new ModelException("Enter a group name without control characters.");
        name=name.Trim();
        if(name.Length is < 1 or > 100) throw new ModelException("Enter a group name from 1 to 100 characters.");
        return name;
    }

    private void ValidateGroups()
    {
        var objects=new HashSet<Guid>();
        foreach(var obj in Objects)
            if(obj.Id==Guid.Empty || !objects.Add(obj.Id)) throw new ModelException("Object IDs must be unique and nonempty.");
        if(Groups.IsDefault || Groups.Length > ModelLimits.MaxObjects/2) throw new ModelException("This scene has an invalid group collection.");
        var groupIds=new HashSet<Guid>(); var members=new HashSet<Guid>();
        foreach(var group in Groups)
        {
            if(group is null) throw new ModelException("A group cannot be null.");
            if(group.Id==Guid.Empty || objects.Contains(group.Id) || !groupIds.Add(group.Id))
                throw new ModelException("Group IDs must be unique, nonempty and different from object IDs.");
            if(ValidateGroupName(group.Name)!=group.Name) throw new ModelException("Group names must be trimmed.");
            if(group.ObjectIds.IsDefault || group.ObjectIds.Length < 2 || group.ObjectIds.Length > ModelLimits.MaxObjects)
                throw new ModelException("A group must contain at least two objects.");
            foreach(var id in group.ObjectIds)
                if(!objects.Contains(id) || !members.Add(id))
                    throw new ModelException("Group members must exist and cannot repeat or belong to another group.");
        }
    }
}
