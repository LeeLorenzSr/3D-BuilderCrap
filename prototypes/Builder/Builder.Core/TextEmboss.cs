using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>New Z-extruded solid from normalized-minXY ink. Position is its minimum XYZ in mm.</summary>
public sealed record TextRequest(TextSource Source,double DepthMillimeters=2,Vec3 PositionMillimeters=default):GeometryRequest;
public enum EmbossFace { PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ }
public enum EmbossMode { Raised, Engraved }
/// <summary>Ink center anchors to accepted AABB face center, then signed UV offsets and in-plane CCW rotation.
/// +X frame U=+Y,V=+Z; −X U=−Y,V=+Z; +Y U=−X,V=+Z; −Y U=+X,V=+Z;
/// +Z U=+X,V=+Y; −Z U=+X,V=−Y. In every case U×V is the outward normal.</summary>
public sealed record PlanarTextPlacement(EmbossFace Face,Vec2 OffsetMillimeters=default,double RotationDegrees=0);
/// <summary>Planar label on one ungrouped leaf or scoped member. Raised height is above the face;
/// engraving depth is inward. Full ink support is required, with positive backing under blind engraving.</summary>
public sealed record EmbossRequest(Guid ObjectId,TextSource Source,PlanarTextPlacement Placement,double DepthMillimeters,EmbossMode Mode=EmbossMode.Raised):GeometryRequest;
internal sealed record TextSolidRequest(TextOutline Outline,double DepthMillimeters,double OverlapMillimeters,Affine Placement,EmbossMode? Mode):GeometryRequest;

public sealed partial class EditorSession
{
    public ImmutableArray<string> GetFontFamilies()
    {
        if(textOutlineProvider is not ITextFontCatalog catalog)throw new ModelException("This session has no installed-font catalog. Supply explicit text outlines or configure a font provider.");
        try{return catalog.GetFontFamilies().ToImmutableArray();}
        catch(ModelException){throw;}catch(Exception e){throw new ModelException("Could not read installed fonts: "+e.Message);}
    }
    public Task<OperationResult> PreviewTextAsync(TextRequest request,Guid? replacePreviewId=null,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null,long? expectedRevision=null)=>PreviewTextOperation(request,replacePreviewId,cancellation,progress,expectedRevision);
    public Task<OperationResult> PreviewEmbossAsync(EmbossRequest request,Guid? replacePreviewId=null,CancellationToken cancellation=default,
        IProgress<OperationProgress>? progress=null,long? expectedRevision=null)=>PreviewTextOperation(request,replacePreviewId,cancellation,progress,expectedRevision);

    private Task<OperationResult> PreviewTextOperation(GeometryRequest request,Guid? replacePreviewId,CancellationToken ct,
        IProgress<OperationProgress>? progress,long? expectedRevision)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(request is not (TextRequest or EmbossRequest))return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose text or emboss settings."));
            if(GuardMemberScope(request.MemberGroupId) is {} scopeConflict)return Task.FromResult(scopeConflict);
            if(request is TextRequest && editingGroupId.HasValue)return Task.FromResult(DoneWithGroup());
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId || current.BaseRevision!=revision || current.Request.GetType()!=request.GetType() ||
                    request is EmbossRequest e && current.Request is EmbossRequest old && e.ObjectId!=old.ObjectId)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This text preview is no longer current or targets a different object."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no text preview to replace."));
            var captured=Snapshot;var resultId=request is EmbossRequest emboss ? emboss.ObjectId : preview?.ResultObjectIds.Single()??Guid.NewGuid();
            return RunIo(()=>PrepareText(request,captured,resultId,ct,progress),expectedRevision,allowPreview:true);
        }
    }
    private string PrepareText(GeometryRequest request,EditorSnapshot captured,Guid resultId,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        progress?.Report(new("Preparing text"));ct.ThrowIfCancellationRequested();
        var source=request is TextRequest t?t.Source:((EmbossRequest)request).Source;
        var depth=request is TextRequest text?text.DepthMillimeters:((EmbossRequest)request).DepthMillimeters;
        if(!double.IsFinite(depth)||depth<0.05||depth>1000)throw new ModelException("Enter text depth from 0.05 to 1,000 mm.");
        TextOutline raw;
        if(source is OutlineTextSource direct)raw=direct.Outline;
        else if(source is FontTextSource font)
        {
            TextOutlines.ValidateSpec(font.Specification);
            if(textOutlineProvider is null)throw new ModelException("No font outline provider is configured. Supply explicit text outlines.");
            progress?.Report(new("Building font outlines"));ct.ThrowIfCancellationRequested();
            try{raw=textOutlineProvider.BuildAsync(font.Specification,ct).GetAwaiter().GetResult();}
            catch(OperationCanceledException){throw;}catch(ModelException){throw;}catch(Exception e){throw new IOException("Could not build font outlines: "+e.Message,e);}
        }
        else throw new ModelException("Choose font text or explicit outlines.");
        ct.ThrowIfCancellationRequested();var outline=TextOutlines.Normalize(raw,ct,progress);
        var width=outline.Contours.Max(c=>c.Max(p=>p.X));var height=outline.Contours.Max(c=>c.Max(p=>p.Y));
        SceneObject? target=null;Affine placement;double overlap=0;EmbossMode? mode=null;
        if(request is TextRequest create)
        {
            if(captured.Scene.Objects.Length>=ModelLimits.MaxObjects)throw new ModelException("Adding text exceeds the scene object limit.");
            placement=Affine.Translation(create.PositionMillimeters);
        }
        else
        {
            var emboss=(EmbossRequest)request;
            if(emboss.Placement is null || !Enum.IsDefined(emboss.Placement.Face)||!Enum.IsDefined(emboss.Mode) ||
                !double.IsFinite(emboss.Placement.RotationDegrees)||!double.IsFinite(emboss.Placement.OffsetMillimeters.X)||!double.IsFinite(emboss.Placement.OffsetMillimeters.Y))
                throw new ModelException("Choose a valid face, finite placement and raised/engraved mode.");
            target=captured.Scene.Objects.FirstOrDefault(o=>o.Id==emboss.ObjectId)??throw new ModelException("The emboss target no longer exists.");
            SurfacePainting.RequireUnpainted(target,"Emboss");
            _=captured.Scene.ExpandObjectIds([target.Id],captured.EditingGroupId);
            if(captured.EditingGroupId is null && captured.Scene.IsGrouped(target.Id))throw new ModelException("Choose Edit members before embossing one group member.");
            placement=FacePlacement(target.Bounds,emboss.Placement.Face);mode=emboss.Mode;overlap=Math.Min(0.1,depth/10);
            var angle=emboss.Placement.RotationDegrees%360;var(sin,cos)=Math.SinCos(angle*Math.PI/180);var offset=emboss.Placement.OffsetMillimeters;
            outline=outline with {Contours=outline.Contours.Select(c=>c.Select(p=>{
                var x=p.X-width/2;var y=p.Y-height/2;return new Vec2(x*cos-y*sin+offset.X,x*sin+y*cos+offset.Y);
            }).ToImmutableArray()).ToImmutableArray()};
        }
        // Validate both the actual tool and engraving backing probe before any worker launch.
        var bottom=mode is null?0:mode==EmbossMode.Raised?-overlap:-(depth+overlap);var top=mode is null?depth:mode==EmbossMode.Raised?depth:overlap;
        _=Bounds3.From(outline.Contours.SelectMany(c=>c).SelectMany(p=>new[]{placement.Apply(new(p.X,p.Y,bottom)),placement.Apply(new(p.X,p.Y,top))}));ct.ThrowIfCancellationRequested();
        var result=geometry.Calculate(new TextSolidRequest(outline,depth,overlap,placement,mode),target is null?[]:[target],ct,progress).Single()
            ??throw new ModelException("Text or emboss produced no usable solid.");
        var name=source is FontTextSource f?"Text · "+f.Specification.Text:"Text";
        if(name.Length>100)name=name[..(char.IsHighSurrogate(name[99])?99:100)];
        var obj=target is null?new SceneObject(resultId,name,result,Affine.Identity):target with {Mesh=result,Transform=Affine.Identity};
        var objects=target is null?captured.Scene.Objects.Add(obj):captured.Scene.Objects.Select(o=>o.Id==obj.Id?obj:o).ToImmutableArray();
        var candidate=captured.Scene.WithObjects(objects);candidate.Validate(ct);
        var summary=FormattableString.Invariant($"{(mode is null?"Text":mode==EmbossMode.Raised?"Raised emboss":"Blind engraving")} preview: ink {width:G6} × {height:G6} mm, depth {depth:G6} mm. ")+ (outline.Provenance??"Explicit outlines.");
        progress?.Report(new("Ready to preview text"));ct.ThrowIfCancellationRequested();
        lock(gate)
        {
            ct.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,target is null?obj.Id:captured.SelectedObjectId,[obj.Id],summary)
                {SelectedObjectIds=target is null?[obj.Id]:captured.SelectedObjectIds,EditingGroupId=captured.EditingGroupId};
        }
        return summary;
    }
    private static Affine FacePlacement(Bounds3 b,EmbossFace face)=>face switch {
        EmbossFace.PositiveX=>new(new(0,1,0),new(0,0,1),new(1,0,0),b.Center with {X=b.Max.X}),
        EmbossFace.NegativeX=>new(new(0,-1,0),new(0,0,1),new(-1,0,0),b.Center with {X=b.Min.X}),
        EmbossFace.PositiveY=>new(new(-1,0,0),new(0,0,1),new(0,1,0),b.Center with {Y=b.Max.Y}),
        EmbossFace.NegativeY=>new(new(1,0,0),new(0,0,1),new(0,-1,0),b.Center with {Y=b.Min.Y}),
        EmbossFace.PositiveZ=>new(new(1,0,0),new(0,1,0),new(0,0,1),b.Center with {Z=b.Max.Z}),
        _=>new(new(1,0,0),new(0,-1,0),new(0,0,-1),b.Center with {Z=b.Min.Z})};
}
