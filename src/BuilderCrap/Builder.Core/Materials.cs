using System.Collections.Immutable;
using System.Globalization;

namespace Builder.Core;

/// <summary>An opaque display color; this does not assign printer filament.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static bool TryParse(string? value, out RgbColor color)
    {
        color=default;
        if(value is not {Length:7} || value[0]!='#' ||
            !uint.TryParse(value.AsSpan(1),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var rgb)) return false;
        color=new((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb); return true;
    }
}
public sealed record SurfaceMaterial(string Name, RgbColor Color)
{
    /// <summary>Explicit fallback assigned with the first surface paint on an unassigned object.</summary>
    public static SurfaceMaterial Neutral { get; }=new("Neutral",new(153,170,185));
    internal static SurfaceMaterial? Canonical(SurfaceMaterial? material)
    {
        if(material is null) return null;
        var name=material.Name?.Trim();
        if(string.IsNullOrEmpty(name) || name.Length>100 || name.Any(c=>char.IsControl(c) || c is '\uFFFE' or '\uFFFF') ||
            name.Any(c=>char.IsSurrogate(c)) && !ValidUnicode(name))
            throw new ModelException("Material names need 1–100 characters without controls or invalid Unicode.");
        return name==material.Name ? material : material with {Name=name};
    }
    private static bool ValidUnicode(string text)
    {
        for(var i=0;i<text.Length;i++)
            if(char.IsSurrogate(text[i]) && (!char.IsHighSurrogate(text[i]) || ++i>=text.Length || !char.IsLowSurrogate(text[i]))) return false;
        return true;
    }
}
/// <summary>Assigns one opaque material to expanded leaf targets; null clears their assignment.</summary>
public sealed record MaterialRequest(ImmutableArray<Guid> ObjectIds, SurfaceMaterial? Material) : GeometryRequest;

public sealed partial class EditorSession
{
    /// <summary>Stages material assignment from the accepted scene. Updates require the current material token,
    /// same expanded targets and exact member scope. Geometry and full selection remain unchanged.</summary>
    public Task<OperationResult> PreviewMaterialAsync(MaterialRequest request, Guid? replacePreviewId=null,
        CancellationToken cancellation=default, IProgress<OperationProgress>? progress=null, long? expectedRevision=null)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked) return Task.FromResult(blocked);
            if(request is null) return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose material targets."));
            if(GuardMemberScope(request.MemberGroupId) is {} scope) return Task.FromResult(scope);
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId || current.Request is not MaterialRequest)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This material preview is no longer current."));
            }
            else if(replacePreviewId.HasValue) return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no material preview to replace."));
            try
            {
                var canonical=scene.ExpandObjectIds(request.ObjectIds,editingGroupId);
                if(canonical.IsEmpty) throw new ModelException("Choose objects to paint.");
                if(preview?.Request is MaterialRequest previous && !previous.ObjectIds.SequenceEqual(canonical))
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"A material update must keep the same target objects."));
                request=request with {ObjectIds=request.ObjectIds.SequenceEqual(canonical)?request.ObjectIds:canonical,
                    Material=SurfaceMaterial.Canonical(request.Material)};
            }
            catch(ModelException e) { return Task.FromResult(new OperationResult(Outcome.Invalid,e.Message)); }
            var captured=Snapshot;
            return RunIo(()=>PrepareMaterial(request,captured,cancellation,progress),expectedRevision,allowPreview:true);
        }
    }
    private string PrepareMaterial(MaterialRequest request, EditorSnapshot captured, CancellationToken cancellation,
        IProgress<OperationProgress>? progress)
    {
        cancellation.ThrowIfCancellationRequested();
        var targets=request.ObjectIds.ToHashSet(); var objects=ImmutableArray.CreateBuilder<SceneObject>(captured.Scene.Objects.Length);
        foreach(var obj in captured.Scene.Objects)
        {
            progress?.Report(new("Assigning material",objects.Count,captured.Scene.Objects.Length)); cancellation.ThrowIfCancellationRequested();
            objects.Add(targets.Contains(obj.Id) && (obj.Material!=request.Material||obj.TriangleMaterials.Count>0)
                ? obj with {Material=request.Material,TriangleMaterials=ImmutableDictionary<int,SurfaceMaterial>.Empty}:obj);
        }
        var candidate=captured.Scene.WithObjects(objects.ToImmutable()); candidate.Validate(cancellation);
        var summary=request.Material is {} material ? $"Paint {targets.Count} object(s): {material.Name} {material.Color.Hex}." : $"Clear material on {targets.Count} object(s).";
        progress?.Report(new("Ready to preview")); cancellation.ThrowIfCancellationRequested();
        lock(gate)
        {
            cancellation.ThrowIfCancellationRequested();
            preview=new(Guid.NewGuid(),captured.Revision,request,candidate,captured.SelectedObjectId,request.ObjectIds,summary)
                {SelectedObjectIds=captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId};
        }
        return summary;
    }
}
