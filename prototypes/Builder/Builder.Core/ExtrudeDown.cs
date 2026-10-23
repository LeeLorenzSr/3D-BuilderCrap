using System.Collections.Immutable;
namespace Builder.Core;

/// <summary>Extends existing world-space material vertically down to a horizontal Z plane in mm.
/// Original material is retained, including below the plane. Undercuts and enclosed cavities beneath
/// material can fill; unobstructed vertical through-holes remain. This is not sketch extrusion.</summary>
public sealed record ExtrudeDownRequest(Guid ObjectId,double PlaneHeightMillimeters=0):GeometryRequest;
public sealed record ExtrudeDownComparison(double PlaneHeightMillimeters,int OriginalTriangleCount,int ResultTriangleCount,
    double OriginalVolumeCubicMillimeters,double ResultVolumeCubicMillimeters,double AddedVolumeCubicMillimeters);
internal sealed record ExtrudeDownKernelResult(Mesh Mesh,double OriginalVolumeCubicMillimeters,double ResultVolumeCubicMillimeters,double AddedVolumeCubicMillimeters);

public sealed partial class EditorSession
{
    /// <summary>Previews one independent leaf or active group member from accepted geometry. Exact-token
    /// replacements retain the target and scope. ID/name/groups/full selection survive; output is world-baked.
    /// Apply is one history step. No added material, failure, cancellation or stale authority preserves accepted
    /// work and any prior candidate. Plane must be finite and within +/-10,000,000 world millimeters.</summary>
    public Task<OperationResult> PreviewExtrudeDownAsync(ExtrudeDownRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(request is null)return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose an object and plane height."));
            if(GuardMemberScope(request.MemberGroupId) is {} conflict)return Task.FromResult(conflict);
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId||current.Request is not ExtrudeDownRequest old||old.ObjectId!=request.ObjectId||current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This Extrude down preview is no longer current or targets another object."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no Extrude down preview to replace."));
            var captured=Snapshot;
            return RunIo(()=>{
                progress?.Report(new("Preparing Extrude down"));cancellation.ThrowIfCancellationRequested();
                if(!double.IsFinite(request.PlaneHeightMillimeters)||Math.Abs(request.PlaneHeightMillimeters)>10_000_000)
                    throw new ModelException("Enter a finite world Z plane height within +/-10,000,000 mm.");
                var target=captured.Scene.Objects.FirstOrDefault(o=>o.Id==request.ObjectId)??throw new ModelException("The extrusion target no longer exists.");
                SurfacePainting.RequireUnpainted(target,"Extrude down");
                _=captured.Scene.ExpandObjectIds([target.Id],captured.EditingGroupId);
                if(captured.EditingGroupId is null&&captured.Scene.IsGrouped(target.Id))throw new ModelException("Choose Edit members before extending one group member.");
                var result=geometry.ExtrudeDown(request,target,cancellation,progress);
                var obj=target with {Mesh=result.Mesh,Transform=Affine.Identity};
                var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>o.Id==obj.Id?obj:o).ToImmutableArray());
                progress?.Report(new("Validating Extrude down"));cancellation.ThrowIfCancellationRequested();candidate.Validate(cancellation);
                var comparison=new ExtrudeDownComparison(request.PlaneHeightMillimeters,target.Mesh.Triangles.Length,result.Mesh.Triangles.Length,
                    result.OriginalVolumeCubicMillimeters,result.ResultVolumeCubicMillimeters,result.AddedVolumeCubicMillimeters);
                var summary=FormattableString.Invariant($"Extrude down preview: world Z plane {request.PlaneHeightMillimeters:G6} mm; added material {result.AddedVolumeCubicMillimeters:G6} mm³. Original material is retained. Covered cavities and undercuts may fill; unobstructed vertical holes remain.");
                progress?.Report(new("Ready to preview Extrude down"));cancellation.ThrowIfCancellationRequested();
                lock(gate){cancellation.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,captured.SelectedObjectId,[obj.Id],summary)
                    {SelectedObjectIds=captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId,ExtrudeDown=comparison};}
                return summary;
            },expectedRevision,allowPreview:true);
        }
    }
}
