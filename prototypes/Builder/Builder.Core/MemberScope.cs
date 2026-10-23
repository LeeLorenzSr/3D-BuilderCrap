using System.Collections.Immutable;

namespace Builder.Core;

public sealed partial class EditorSession
{
    private Guid? editingGroupId;

    /// <summary>Enters a flat group's member-editing scope and selects one member. No content/history change.
    /// A different active group must be exited first. Busy operations and owned previews block scope changes.</summary>
    public OperationResult EnterGroup(Guid groupId,Guid? memberId=null,long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision) is {} blocked) return blocked;
            if(editingGroupId.HasValue && editingGroupId!=groupId) return DoneWithGroup();
            var group=scene.Groups.FirstOrDefault(g=>g.Id==groupId);
            if(group is null) return new(Outcome.Invalid,"Group not found.");
            if(editingGroupId==groupId && !memberId.HasValue) return new(Outcome.Success,"No change.");
            var members=scene.ExpandObjectIds(group.ObjectIds,groupId);
            var primary=memberId ?? members[0];
            if(!members.Contains(primary)) return new(Outcome.Invalid,"Choose a member of this group.");
            if(editingGroupId!=groupId || selected!=primary || selectedIds.Length!=1 || selectedIds[0]!=primary)
            { editingGroupId=groupId; selected=primary; selectedIds=[primary]; revision++; }
            return new(Outcome.Success,"Editing group members. Choose Done to select the whole group again.");
        }
    }

    /// <summary>Returns to scene scope, selecting the whole edited group while retaining its primary member.
    /// This changes only selection/scope revision, not document content or history.</summary>
    public OperationResult ExitGroup(long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision) is {} blocked) return blocked;
            if(editingGroupId is not {} groupId) return new(Outcome.Success,"No change.");
            var group=scene.Groups.Single(g=>g.Id==groupId);
            selectedIds=scene.ExpandObjectIds(group.ObjectIds);
            selected=selected is {} old && selectedIds.Contains(old) ? old : selectedIds[0];
            editingGroupId=null; revision++;
            return new(Outcome.Success,"Group member editing finished.");
        }
    }

    private OperationResult? GuardMemberScope(Guid? requestedGroupId)
        => requestedGroupId==editingGroupId ? null : new(Outcome.Conflict,
            "The editing scope changed. Read the current group scope before applying this operation.");

    private static OperationResult DoneWithGroup()=>new(Outcome.Conflict,"Choose Done to leave group member editing before this operation.");
}

public sealed partial record Scene
{
    /// <summary>Rebinds only an active group's consumed/result leaves after validated member geometry work.
    /// Group identity/name and all other groups are retained; fewer than two members is rejected.</summary>
    internal Scene ReplaceMemberIds(Guid groupId,IEnumerable<Guid> consumed,IEnumerable<Guid> results)
    {
        var group=Groups.FirstOrDefault(g=>g.Id==groupId) ?? throw new ModelException("The edited group no longer exists.");
        var removed=consumed.ToHashSet(); var members=group.ObjectIds.Where(id=>!removed.Contains(id)).Concat(results).ToHashSet();
        var canonical=Objects.Where(o=>members.Contains(o.Id)).Select(o=>o.Id).ToImmutableArray();
        if(canonical.Length < 2) throw new ModelException("Keep at least two members in this group. Choose Done and Ungroup to remove the group instead.");
        if(canonical.SequenceEqual(group.ObjectIds)) return this;
        return this with {Groups=Groups.Replace(group,group with {ObjectIds=canonical})};
    }
}
