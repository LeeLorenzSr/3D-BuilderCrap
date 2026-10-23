using Builder.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Builder.Desktop;

/// <summary>One virtualizable display row. Core still owns selection units, member scope and history.</summary>
public sealed class ObjectTreeRow : INotifyPropertyChanged
{
    public SceneTreeNode Node {get;private set;}
    public SceneTreeNode SelectionUnit {get;private set;}
    public Guid? ParentGroupId {get;private set;}
    public Guid Id=>Node.Id;
    public string Name=>Node.Name;
    public System.Collections.Immutable.ImmutableArray<Guid> ObjectIds=>SelectionUnit.ObjectIds;
    public bool IsGroup=>!Node.Children.IsEmpty;
    public bool IsMember=>ParentGroupId.HasValue;
    public bool IsAlias=>Id!=SelectionUnit.Id;
    public Thickness Indent=>IsAlias ? new(20,0,0,0) : new(0);
    public int MemberCount=>Node.Children.Length;
    public bool IsExpanded {get;private set;}
    public bool IsUnitSelected {get;private set;}
    public string HelpText=>IsAlias ? $"Member of {SelectionUnit.Name}. Click selects the whole group; Enter or double-click edits this member." :
        IsGroup ? $"Group with {MemberCount} members. Left, Right or Space changes disclosure; Enter edits members." :
        IsMember ? "Independent member in the current editing group." : "Scene object.";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal ObjectTreeRow(SceneTreeNode node,SceneTreeNode owner,Guid? parentGroupId,bool expanded)
    {Node=node; SelectionUnit=owner; ParentGroupId=parentGroupId; IsExpanded=expanded;}

    internal void Update(SceneTreeNode node,SceneTreeNode owner,Guid? parentGroupId,bool expanded)
    {
        var name=Name; var group=IsGroup; var count=MemberCount; var alias=IsAlias; var member=IsMember; var ownerName=SelectionUnit.Name;
        Node=node; SelectionUnit=owner; ParentGroupId=parentGroupId;
        if(name!=Name) Changed(nameof(Name));
        if(group!=IsGroup) Changed(nameof(IsGroup));
        if(count!=MemberCount) Changed(nameof(MemberCount));
        if(alias!=IsAlias) {Changed(nameof(IsAlias)); Changed(nameof(Indent));}
        if(member!=IsMember) Changed(nameof(IsMember));
        // Compare metadata without interpolating help strings for every unrealized member row.
        if(alias!=IsAlias || member!=IsMember || group!=IsGroup || count!=MemberCount ||
            IsAlias && ownerName!=SelectionUnit.Name) Changed(nameof(HelpText));
        if(IsExpanded!=expanded) {IsExpanded=expanded; Changed(nameof(IsExpanded));}
    }
    internal void Select(bool selected)
    {if(IsUnitSelected!=selected) {IsUnitSelected=selected; Changed(nameof(IsUnitSelected));}}
    private void Changed([CallerMemberName]string? property=null)=>PropertyChanged?.Invoke(this,new(property));
}

// One-row deltas retain realized containers. Larger changes publish one reset, never 1,000 row events.
// Ordinary transforms and rename keep row instances and update only changed display properties.
internal sealed class ObjectTreeRows : ObservableCollection<ObjectTreeRow>
{
    public void ReplaceWith(IReadOnlyList<ObjectTreeRow> rows)
    {
        if(Math.Abs(rows.Count-Count)==1)
        {
            var prefix=0;
            while(prefix<Math.Min(Count,rows.Count) && ReferenceEquals(this[prefix],rows[prefix])) prefix++;
            var insert=rows.Count>Count;
            var matches=true;
            for(var i=prefix;i<Math.Min(Count,rows.Count);i++)
                if(!ReferenceEquals(this[insert ? i : i+1],rows[insert ? i+1 : i])) {matches=false; break;}
            if(matches)
            {
                if(insert) Insert(prefix,rows[prefix]); else RemoveAt(prefix);
                return;
            }
        }
        Items.Clear(); foreach(var row in rows) Items.Add(row);
        OnPropertyChanged(new(nameof(Count))); OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }
}
