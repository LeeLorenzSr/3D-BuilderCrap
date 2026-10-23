using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Moves an exactly welded world-space mesh through ten synchronous uniform Taubin
/// pairs (lambda .5, mu -.53), blends from the accepted source, then limits each vertex's mm
/// displacement. Sharp protection fixes endpoints of original dihedral edges at least 60 degrees.
/// Strength must be finite in (0,100]; maximum deviation finite in (0,1000] mm. Faces are not
/// decimated or added. Size/volume may change. Difficult contacts can conservatively reject.</summary>
public sealed record SmoothRequest(Guid ObjectId,double StrengthPercent=50,double MaximumDeviationMillimeters=.2,
    bool PreserveSharpEdges=true):GeometryRequest;
public sealed record SmoothComparison(int TriangleCount,int VertexCount,double StrengthPercent,double MaximumDeviationMillimeters,
    double MaximumVertexDisplacementMillimeters,double CertifiedDeviationUpperBoundMillimeters,bool PreserveSharpEdges,
    int FixedSharpVertexCount,int ClippedVertexCount,int BoundaryCount,int CavityCount,
    double OriginalVolumeCubicMillimeters,double ResultVolumeCubicMillimeters);
internal sealed record SmoothKernelResult(Mesh Mesh,double MaximumVertexDisplacementMillimeters,
    double CertifiedDeviationUpperBoundMillimeters,int FixedSharpVertexCount,int ClippedVertexCount,
    int BoundaryCount,int CavityCount,double OriginalVolumeCubicMillimeters,double ResultVolumeCubicMillimeters);

public sealed partial class EditorSession
{
    /// <summary>Builds a private smoothing candidate from the accepted source. Exact-token replacement
    /// retains target/scope; failure/cancel/no measurable surface change retains accepted work and the
    /// prior candidate. Apply is one undo step. Exact coordinate welding canonicalizes repeated raw
    /// vertices but preserves corresponding indexed face order/connectivity. Bound is numerical with
    /// an explicit margin, not exact arithmetic; source/candidate contacts and cavities are checked.</summary>
    public Task<OperationResult> PreviewSmoothAsync(SmoothRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(request is null)return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose an object and smoothing settings."));
            if(GuardMemberScope(request.MemberGroupId) is {} conflict)return Task.FromResult(conflict);
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId||current.Request is not SmoothRequest old||old.ObjectId!=request.ObjectId||current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This smoothing preview is no longer current or targets another object."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no smoothing preview to replace."));
            var captured=Snapshot;
            return RunIo(()=>{
                progress?.Report(new("Preparing smoothing"));cancellation.ThrowIfCancellationRequested();
                if(!double.IsFinite(request.StrengthPercent)||request.StrengthPercent<=0||request.StrengthPercent>100)
                    throw new ModelException("Smoothing strength must be greater than 0% and at most 100%.");
                if(!double.IsFinite(request.MaximumDeviationMillimeters)||request.MaximumDeviationMillimeters<=0||request.MaximumDeviationMillimeters>1000)
                    throw new ModelException("Maximum shape deviation must be positive and at most 1,000 mm.");
                var target=captured.Scene.Objects.FirstOrDefault(o=>o.Id==request.ObjectId)??throw new ModelException("The smoothing target no longer exists.");
                SurfacePainting.RequireUnpainted(target,"Smooth");
                _=captured.Scene.ExpandObjectIds([target.Id],captured.EditingGroupId);
                if(captured.EditingGroupId is null&&captured.Scene.IsGrouped(target.Id))throw new ModelException("Choose Edit members before smoothing one group member.");
                var result=geometry.Smooth(request,target,cancellation,progress);
                if(result.Mesh.Triangles.Length!=target.Mesh.Triangles.Length)throw new ModelException("Smoothing must preserve the triangle count.");
                var obj=target with {Mesh=result.Mesh,Transform=Affine.Identity};
                var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>o.Id==obj.Id?obj:o).ToImmutableArray());
                progress?.Report(new("Validating smoothing"));cancellation.ThrowIfCancellationRequested();candidate.Validate(cancellation);
                var comparison=new SmoothComparison(result.Mesh.Triangles.Length,result.Mesh.Vertices.Length,request.StrengthPercent,request.MaximumDeviationMillimeters,
                    result.MaximumVertexDisplacementMillimeters,result.CertifiedDeviationUpperBoundMillimeters,request.PreserveSharpEdges,
                    result.FixedSharpVertexCount,result.ClippedVertexCount,result.BoundaryCount,result.CavityCount,result.OriginalVolumeCubicMillimeters,result.ResultVolumeCubicMillimeters);
                var summary=FormattableString.Invariant($"Smooth preview: {request.StrengthPercent:G4}% strength; {comparison.TriangleCount:N0} triangles retained. Maximum vertex movement {comparison.MaximumVertexDisplacementMillimeters:G6} mm; surface bound {comparison.CertifiedDeviationUpperBoundMillimeters:G6} mm including a numerical margin. {comparison.FixedSharpVertexCount:N0} sharp vertices fixed; {comparison.ClippedVertexCount:N0} movements limited. {comparison.CavityCount} cavities preserved. Size and volume may change.");
                progress?.Report(new("Ready to preview smoothing"));cancellation.ThrowIfCancellationRequested();
                lock(gate){cancellation.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,captured.SelectedObjectId,[obj.Id],summary)
                    {SelectedObjectIds=captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId,Smooth=comparison};}
                return summary;
            },expectedRevision,allowPreview:true);
        }
    }
}
