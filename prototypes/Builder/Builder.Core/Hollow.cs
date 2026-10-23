using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Conservative inward hollowing of one connected closed oriented surface (arbitrary genus).
/// Thickness is world-space mm, finite 0.05..1000. The fixed circumscribed 288-face kernel contains
/// the requested-radius ball. Narrow regions may stay solid; this is not exact uniform thickness.
/// Already-nested shells, disconnected outer surfaces and operands above 4,096 triangles reject.</summary>
public sealed record HollowRequest(Guid ObjectId,double WallThicknessMillimeters=2):GeometryRequest;
/// <summary>Outward world-space removed cavity and physical approximation metadata. The maximum
/// kernel radius bounds its support approximation, not the maximum local thickness of surviving features.</summary>
public sealed record HollowComparison(Mesh Cavity,double RequestedWallThicknessMillimeters,
    double MaximumKernelRadiusMillimeters,double RemovedVolumeCubicMillimeters,int CavityCount);
internal sealed record HollowKernelResult(Mesh Cavity,double MaximumKernelRadiusMillimeters,int CavityCount);

public sealed partial class EditorSession
{
    /// <summary>Builds a private shell, retaining the exact accepted world exterior and appending a
    /// reversed cavity. Exact-token replacements derive from accepted geometry and keep the same
    /// target ID/name/groups/full selection. Empty erosion, invalid geometry, cancellation or stale
    /// requests retain both accepted state and the previous preview. Apply is one undoable edit.</summary>
    public Task<OperationResult> PreviewHollowAsync(HollowRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(request is null)return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose an object and wall thickness."));
            if(GuardMemberScope(request.MemberGroupId) is {} scopeConflict)return Task.FromResult(scopeConflict);
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId||current.Request is not HollowRequest old||old.ObjectId!=request.ObjectId||current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This hollow preview is no longer current or targets another object."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no hollow preview to replace."));
            var captured=Snapshot;
            return RunIo(()=>{
                progress?.Report(new("Preparing hollow"));cancellation.ThrowIfCancellationRequested();
                if(!double.IsFinite(request.WallThicknessMillimeters)||request.WallThicknessMillimeters<.05||request.WallThicknessMillimeters>1000)
                    throw new ModelException("Wall thickness must be from 0.05 to 1,000 mm.");
                var target=captured.Scene.Objects.FirstOrDefault(o=>o.Id==request.ObjectId)??throw new ModelException("The hollow target no longer exists.");
                SurfacePainting.RequireUnpainted(target,"Hollow");
                _=captured.Scene.ExpandObjectIds([target.Id],captured.EditingGroupId);
                if(captured.EditingGroupId is null&&captured.Scene.IsGrouped(target.Id))throw new ModelException("Choose Edit members before hollowing one group member.");
                if(target.Mesh.Triangles.Length>4096)throw new ModelException("Hollow supports at most 4,096 operand triangles in this prototype. The accepted object is unchanged.");
                var result=geometry.Hollow(request,target,cancellation,progress);
                var cavity=result.Cavity;
                if(captured.Scene.TriangleCount+cavity.Triangles.Length>ModelLimits.MaxTriangles||
                    captured.Scene.Objects.Sum(o=>(long)o.Mesh.Vertices.Length)+cavity.Vertices.Length>ModelLimits.MaxVertices)
                    throw new ModelException("The hollow cavity would exceed the scene triangle or vertex limit.");
                progress?.Report(new("Building hollow shell"));cancellation.ThrowIfCancellationRequested();
                var vertices=new Vec3[target.Mesh.Vertices.Length+cavity.Vertices.Length];
                for(var i=0;i<target.Mesh.Vertices.Length;i++){if(i%1024==0)cancellation.ThrowIfCancellationRequested();vertices[i]=target.Transform.Apply(target.Mesh.Vertices[i]);}
                cavity.Vertices.CopyTo(vertices,target.Mesh.Vertices.Length);
                var triangles=new Triangle[target.Mesh.Triangles.Length+cavity.Triangles.Length];
                var reflected=ModelFiles.Determinant(target.Transform)<0;
                for(var i=0;i<target.Mesh.Triangles.Length;i++){cancellation.ThrowIfCancellationRequested();var t=target.Mesh.Triangles[i];triangles[i]=reflected?new(t.A,t.C,t.B):t;}
                var offset=target.Mesh.Vertices.Length;
                for(var i=0;i<cavity.Triangles.Length;i++){cancellation.ThrowIfCancellationRequested();var t=cavity.Triangles[i];triangles[target.Mesh.Triangles.Length+i]=new(offset+t.A,offset+t.C,offset+t.B);}
                var obj=target with {Mesh=new(vertices,triangles),Transform=Affine.Identity};
                // Native containment is checked during erosion. Validate this complete copied-exterior
                // candidate as a solid too; ignore its normalized result so source exterior stays exact.
                progress?.Report(new("Validating hollow shell"));cancellation.ThrowIfCancellationRequested();
                _=geometry.Calculate(new ValidateSolidRequest(),[obj],cancellation,progress);
                var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>o.Id==obj.Id?obj:o).ToImmutableArray());candidate.Validate(cancellation);
                var removed=HollowVolume(cavity,cancellation);
                var comparison=new HollowComparison(cavity,request.WallThicknessMillimeters,result.MaximumKernelRadiusMillimeters,removed,result.CavityCount);
                var summary=FormattableString.Invariant($"Hollow preview: requested wall {request.WallThicknessMillimeters:G6} mm; removed {removed:G6} mm³ in {result.CavityCount} cavit{(result.CavityCount==1?"y":"ies")}. Conservative kernel radius up to {result.MaximumKernelRadiusMillimeters:G6} mm; narrow regions may remain solid. The exterior is unchanged.");
                progress?.Report(new("Ready to preview hollow"));cancellation.ThrowIfCancellationRequested();
                lock(gate)
                {
                    cancellation.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,captured.SelectedObjectId,[obj.Id],summary)
                        {SelectedObjectIds=captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId,Hollow=comparison};
                }
                return summary;
            },expectedRevision,allowPreview:true);
        }
    }
    private static double HollowVolume(Mesh mesh,CancellationToken ct)
    {
        var origin=mesh.Vertices[0];double six=0;
        foreach(var t in mesh.Triangles){ct.ThrowIfCancellationRequested();var a=mesh.Vertices[t.A]-origin;var b=mesh.Vertices[t.B]-origin;var c=mesh.Vertices[t.C]-origin;var cross=Vec3.Cross(b,c);six+=a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z;}
        if(!double.IsFinite(six)||six<=0)throw new ModelException("Hollow produced no usable positive cavity volume.");return six/6;
    }
}
