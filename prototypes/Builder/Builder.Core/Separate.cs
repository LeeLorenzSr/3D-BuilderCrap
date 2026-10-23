using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Partitions one independent leaf or scoped member into disconnected material pieces.
/// An outer material boundary keeps its immediate inward cavity boundaries; nested positive islands
/// become separate pieces. Touching/intersecting/ambiguous shells reject. A single piece is a no-op.</summary>
public sealed record SeparateRequest(Guid ObjectId):GeometryRequest;
/// <summary>SourceTriangleIndices refer to the accepted source mesh's original triangle order.
/// Every source triangle belongs to exactly one piece. Local coordinates and source affine remain exact.</summary>
public sealed record SeparatePieceComparison(Guid ObjectId,int TriangleCount,int BoundaryCount,int CavityCount,
    double VolumeCubicMillimeters,ImmutableArray<int> SourceTriangleIndices);
public sealed record SeparateComparison(Guid SourceObjectId,int OriginalTriangleCount,ImmutableArray<SeparatePieceComparison> Pieces);
internal sealed record SeparateKernelPiece(ImmutableArray<int> SourceTriangleIndices,int BoundaryCount,int CavityCount,double VolumeCubicMillimeters);

public sealed partial class EditorSession
{
    /// <summary>Computes a private partition from accepted geometry. First source-face-order piece
    /// retains source ID/name; later pieces have fresh stable preview IDs and bounded names. Replacements
    /// reuse IDs by exact provenance. Apply replaces the source in document/group order, selects all pieces
    /// plus surviving selection, keeps member scope and creates one Undo step. Failure/cancel preserves
    /// accepted work and a prior preview. Geometry is not translated, welded or repaired in the outputs.</summary>
    public Task<OperationResult> PreviewSeparateAsync(SeparateRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(request is null)return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose an object to separate."));
            if(GuardMemberScope(request.MemberGroupId) is {} conflict)return Task.FromResult(conflict);
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId||current.Request is not SeparateRequest old||old.ObjectId!=request.ObjectId||current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This separation preview is no longer current or targets another object."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no separation preview to replace."));
            var captured=Snapshot;var previous=preview?.Separate;
            return RunIo(()=>{
                progress?.Report(new("Preparing separation"));cancellation.ThrowIfCancellationRequested();
                var source=captured.Scene.Objects.FirstOrDefault(o=>o.Id==request.ObjectId)??throw new ModelException("The separation target no longer exists.");
                _=captured.Scene.ExpandObjectIds([source.Id],captured.EditingGroupId);
                if(captured.EditingGroupId is null&&captured.Scene.IsGrouped(source.Id))throw new ModelException("Choose Edit members before separating one group member.");
                var partition=geometry.Separate(request,source,cancellation,progress);
                if(captured.Scene.Objects.Length-1+partition.Length>ModelLimits.MaxObjects)throw new ModelException("Separating these pieces would exceed the 1,000-object scene limit.");
                if(previous is not null&&(previous.Pieces.Length!=partition.Length||previous.Pieces.Where((p,i)=>!p.SourceTriangleIndices.SequenceEqual(partition[i].SourceTriangleIndices)).Any()))
                    throw new ModelException("The replacement partition does not match the accepted source provenance.");
                var pieces=ImmutableArray.CreateBuilder<SceneObject>();var comparisons=ImmutableArray.CreateBuilder<SeparatePieceComparison>();
                for(var i=0;i<partition.Length;i++)
                {
                    var part=partition[i];var vertices=new List<Vec3>();var triangles=new List<Triangle>();var remap=new Dictionary<int,int>();
                    int Index(int original){if(!remap.TryGetValue(original,out var n)){n=vertices.Count;remap.Add(original,n);vertices.Add(source.Mesh.Vertices[original]);}return n;}
                    for(var j=0;j<part.SourceTriangleIndices.Length;j++)
                    {
                        if(j%1024==0){progress?.Report(new("Packing separated pieces",j,part.SourceTriangleIndices.Length));cancellation.ThrowIfCancellationRequested();}
                        var t=source.Mesh.Triangles[part.SourceTriangleIndices[j]];triangles.Add(new(Index(t.A),Index(t.B),Index(t.C)));
                    }
                    var id=i==0?source.Id:previous is not null?previous.Pieces[i].ObjectId:Guid.NewGuid();
                    pieces.Add(source with {Id=id,Name=i==0?source.Name:SeparatePieceName(source.Name,i+1),Mesh=new(vertices,triangles),TriangleMaterials=part.SourceTriangleIndices.Select((old,index)=>(old,index))
                        .Where(p=>source.TriangleMaterials.ContainsKey(p.old)).ToImmutableDictionary(p=>p.index,p=>source.TriangleMaterials[p.old])});
                    comparisons.Add(new(id,triangles.Count,part.BoundaryCount,part.CavityCount,part.VolumeCubicMillimeters,part.SourceTriangleIndices));
                }
                var objects=ImmutableArray.CreateBuilder<SceneObject>();foreach(var obj in captured.Scene.Objects){if(obj.Id==source.Id)objects.AddRange(pieces);else objects.Add(obj);}
                var candidate=captured.Scene.WithObjects(objects.ToImmutable());var resultIds=pieces.Select(o=>o.Id).ToImmutableArray();
                if(captured.EditingGroupId is {} scope)candidate=candidate.ReplaceMemberIds(scope,[source.Id],resultIds);
                progress?.Report(new("Validating separation"));cancellation.ThrowIfCancellationRequested();candidate.Validate(cancellation);
                var selection=candidate.ExpandObjectIds(captured.SelectedObjectIds.Where(id=>id!=source.Id).Concat(resultIds).ToImmutableArray(),captured.EditingGroupId);
                var comparison=new SeparateComparison(source.Id,source.Mesh.Triangles.Length,comparisons.ToImmutable());
                var summary=$"Separate preview: {pieces.Count} editable pieces; {comparisons.Sum(p=>p.CavityCount)} cavities remain with their owning solids. Triangle coordinates and placement are unchanged. Apply creates all pieces in one Undo step.";
                progress?.Report(new("Ready to preview separation"));cancellation.ThrowIfCancellationRequested();
                lock(gate){cancellation.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,resultIds[0],resultIds,summary)
                    {SelectedObjectIds=selection,EditingGroupId=captured.EditingGroupId,Separate=comparison};}
                return summary;
            },expectedRevision,allowPreview:true);
        }
    }
    private static string SeparatePieceName(string name,int number)
    {
        var suffix=$" · piece {number}";var length=Math.Min(name.Length,100-suffix.Length);
        if(length>0&&char.IsHighSurrogate(name[length-1]))length--;
        return name[..length].TrimEnd()+suffix;
    }
}
