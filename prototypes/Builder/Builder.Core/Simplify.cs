using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Reduction is a goal, not an exact count. Maximum deviation is physical world mm.
/// Requires finite 0&lt;ReductionPercent&lt;100 and 0&lt;MaximumDeviationMillimeters&lt;=1000.
/// A successful preview strictly reduces faces and verifies each corresponding closed boundary,
/// cavity nesting and a bidirectional surface upper bound within explicit floating-point margins.
/// Difficult geometry may exceed bounded verification work and reject without changing the document.</summary>
public sealed record SimplifyRequest(Guid ObjectId,double ReductionPercent=50,double MaximumDeviationMillimeters=.1):GeometryRequest;
public sealed record SimplifyComparison(int OriginalTriangleCount,int ResultTriangleCount,double RequestedReductionPercent,
    double AchievedReductionPercent,bool TargetReached,double MaximumDeviationMillimeters,
    double CertifiedDeviationUpperBoundMillimeters,int BoundaryCount,int CavityCount,
    double WorkingToleranceMillimeters,double OriginalVolumeCubicMillimeters,double ResultVolumeCubicMillimeters);
internal sealed record SimplifyKernelResult(Mesh Mesh,double CertifiedDeviationUpperBoundMillimeters,int BoundaryCount,
    int CavityCount,double WorkingToleranceMillimeters,double OriginalVolumeCubicMillimeters,double ResultVolumeCubicMillimeters);

public sealed partial class EditorSession
{
    /// <summary>Computes from accepted geometry; exact-token replacement must retain target/scope.
    /// Keeps ID/name/group/full selection and unrelated object references. The result is world-baked.
    /// Apply creates one undoable edit. Failure, no safe reduction, cancellation, stale requests and
    /// verification-budget exhaustion preserve accepted state and the previous private candidate.</summary>
    public Task<OperationResult> PreviewSimplifyAsync(SimplifyRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(request is null)return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose an object and simplification settings."));
            if(GuardMemberScope(request.MemberGroupId) is {} scopeConflict)return Task.FromResult(scopeConflict);
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId||current.Request is not SimplifyRequest old||old.ObjectId!=request.ObjectId||current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This simplification preview is no longer current or targets another object."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no simplification preview to replace."));
            var captured=Snapshot;
            return RunIo(()=>{
                progress?.Report(new("Preparing simplification"));cancellation.ThrowIfCancellationRequested();
                if(!double.IsFinite(request.ReductionPercent)||request.ReductionPercent<=0||request.ReductionPercent>=100)
                    throw new ModelException("Target reduction must be greater than 0% and less than 100%.");
                if(!double.IsFinite(request.MaximumDeviationMillimeters)||request.MaximumDeviationMillimeters<=0||request.MaximumDeviationMillimeters>1000)
                    throw new ModelException("Maximum shape deviation must be positive and at most 1,000 mm.");
                var target=captured.Scene.Objects.FirstOrDefault(o=>o.Id==request.ObjectId)??throw new ModelException("The simplification target no longer exists.");
                SurfacePainting.RequireUnpainted(target,"Simplify");
                _=captured.Scene.ExpandObjectIds([target.Id],captured.EditingGroupId);
                if(captured.EditingGroupId is null&&captured.Scene.IsGrouped(target.Id))throw new ModelException("Choose Edit members before simplifying one group member.");
                var result=geometry.Simplify(request,target,cancellation,progress);
                if(result.Mesh.Triangles.Length>=target.Mesh.Triangles.Length)throw new ModelException("No safe triangle reduction was found within the shape-deviation limit.");
                var obj=target with {Mesh=result.Mesh,Transform=Affine.Identity};
                var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Select(o=>o.Id==obj.Id?obj:o).ToImmutableArray());
                progress?.Report(new("Validating simplification"));cancellation.ThrowIfCancellationRequested();candidate.Validate(cancellation);
                var before=target.Mesh.Triangles.Length;var after=result.Mesh.Triangles.Length;var achieved=100d*(before-after)/before;
                var targetCount=(int)Math.Ceiling(before*(1-request.ReductionPercent/100));var reached=after<=targetCount;
                var comparison=new SimplifyComparison(before,after,request.ReductionPercent,achieved,reached,request.MaximumDeviationMillimeters,
                    result.CertifiedDeviationUpperBoundMillimeters,result.BoundaryCount,result.CavityCount,result.WorkingToleranceMillimeters,
                    result.OriginalVolumeCubicMillimeters,result.ResultVolumeCubicMillimeters);
                var summary=FormattableString.Invariant($"Simplify preview: {before:N0} → {after:N0} triangles ({achieved:G4}% reduction; target {request.ReductionPercent:G4}%). ")+
                    (reached?"Target reached. ":"Best safe reduction found; target not reached. ")+
                    FormattableString.Invariant($"Bidirectional surface bound {result.CertifiedDeviationUpperBoundMillimeters:G6} mm, including a numerical margin; {result.BoundaryCount} boundaries and {result.CavityCount} cavities preserved.");
                progress?.Report(new("Ready to preview simplification"));cancellation.ThrowIfCancellationRequested();
                lock(gate)
                {
                    cancellation.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,captured.SelectedObjectId,[obj.Id],summary)
                        {SelectedObjectIds=captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId,Simplify=comparison};
                }
                return summary;
            },expectedRevision,allowPreview:true);
        }
    }
}
