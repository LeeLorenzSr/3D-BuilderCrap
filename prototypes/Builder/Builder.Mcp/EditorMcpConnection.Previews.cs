using Builder.Core;

namespace Builder.Mcp;

public sealed partial class EditorMcpConnection
{
    private void PreviewTool<T>(string name,string description)where T:GeometryRequest
        =>Add<PreviewArguments<T>>("builder_preview_"+name,description+" Computes an unaccepted candidate. Requires expectedRevision and exact request.memberGroupId in member scope. Inspect state.preview, then explicitly Apply or Cancel with its token. Replacement is supported only for the same tool/targets and this connection's current token.",true,async(a,ct,p)=>
        {
            if(a.Request is null)throw new ModelException("Supply a non-null preview request.");
            var source=a.Request switch{ImageReliefRequest r=>r.Source,ImageContourRequest r=>r.Source,ImageEdgeRequest r=>r.Source,ImageStampRequest r=>r.Source,_=>null};
            if(source is ImageFileSource file)_=Absolute(file.Path);
            Context(a.ExpectedRevision,a.Request.MemberGroupId);
            if(a.ReplacePreviewId is {} replace&&OwnedPreviewId!=replace)
                return Reply(new(Outcome.Conflict,"Only this connection's current preview can be replaced."));
            var result=await Preview(a.Request,a.ReplacePreviewId,a.ExpectedRevision,ct,p).ConfigureAwait(false);
            if(result.Succeeded)lock(ownership)ownedPreview=editor.Snapshot.Preview?.Id;
            return Reply(result);
        });
    private Task<OperationResult> Preview(GeometryRequest request,Guid? replacement,long revision,CancellationToken ct,IProgress<OperationProgress>? p)
        =>request switch
        {
            TransformRequest r=>editor.PreviewTransformAsync(r,replacement,ct,p,revision),
            ArrangementRequest r=>editor.PreviewArrangementAsync(r,replacement,ct,p,revision),
            PrimitiveRequest r=>editor.PreviewPrimitiveAsync(r,replacement,ct,p,revision),
            TextRequest r=>editor.PreviewTextAsync(r,replacement,ct,p,revision),
            EmbossRequest r=>editor.PreviewEmbossAsync(r,replacement,ct,p,revision),
            ImageReliefRequest r=>editor.PreviewImageReliefAsync(r,replacement,ct,p,revision),
            ImageContourRequest r=>editor.PreviewImageContourAsync(r,replacement,ct,p,revision),
            ImageEdgeRequest r=>editor.PreviewImageEdgeAsync(r,replacement,ct,p,revision),
            ImageStampRequest r=>editor.PreviewImageStampAsync(r,replacement,ct,p,revision),
            HollowRequest r=>editor.PreviewHollowAsync(r,replacement,ct,p,revision),
            SimplifyRequest r=>editor.PreviewSimplifyAsync(r,replacement,ct,p,revision),
            SmoothRequest r=>editor.PreviewSmoothAsync(r,replacement,ct,p,revision),
            SeparateRequest r=>editor.PreviewSeparateAsync(r,replacement,ct,p,revision),
            ExtrudeDownRequest r=>editor.PreviewExtrudeDownAsync(r,replacement,ct,p,revision),
            MaterialRequest r=>editor.PreviewMaterialAsync(r,replacement,ct,p,revision),
            SurfaceMaterialRequest r=>editor.PreviewSurfaceMaterialAsync(r,replacement,ct,p,revision),
            SurfaceBrushRequest r=>editor.PreviewSurfaceBrushAsync(r,replacement,ct,p,revision),
            _ when replacement.HasValue=>Task.FromResult(new OperationResult(Outcome.Conflict,"Cancel this construction preview before preparing another; it does not support token replacement.")),
            _=>editor.PreviewAsync(request,ct,p,revision)
        };
    private void RegisterPreviews()
    {
        PreviewTool<MoveTransform>("move","Move objects/groups or scoped members by world millimeter deltas, with optional snapping.");
        PreviewTool<RotateTransform>("rotate","Rotate selection by world X/Y/Z degrees about its combined bounds center, with optional angle snapping.");
        PreviewTool<ScaleTransform>("scale","Scale selection by positive axis factors about its combined bounds center, with optional percent snapping.");
        PreviewTool<MirrorTransform>("mirror","Mirror selection about a world axis plane; omitted position uses selection center.");
        PreviewTool<GroupRequest>("group","Group selected units without merging their editable geometry.");
        PreviewTool<UngroupRequest>("ungroup","Ungroup selected units, preserving independent leaves.");
        PreviewTool<AlignmentRequest>("align","Align selected units along world bounds min/center/max, optionally relative to a selected reference.");
        PreviewTool<DistributionRequest>("distribute","Distribute at least three units by centers or equal gaps along one axis.");
        PreviewTool<CenterOnPlateRequest>("center_on_plate","Center the selected aggregate on worldXY zero; optionally place its minimumZ on the bed.");
        PreviewTool<RectangularArrayRequest>("rectangular_array","Create a rectangular grid of independent copies with explicit counts and millimeter steps; counts include originals.");
        PreviewTool<RadialArrayRequest>("radial_array","Create radial copies about an explicit world axis/pivot with count, angle step and optional rotation; count includes originals.");
        PreviewTool<PolyhedronPrimitiveRequest>("polyhedron","Create a cube, pyramid, hexagon, wedge or tetrahedron with custom physical dimensions.");
        PreviewTool<RadialPrimitiveRequest>("radial_primitive","Create a cylinder or cone with custom dimensions and bounded radial segment count.");
        PreviewTool<SpherePrimitiveRequest>("sphere","Create a custom-sized sphere with radial/latitude segments.");
        PreviewTool<TorusPrimitiveRequest>("torus","Create a torus with major/tube radii and bounded major/tube segments.");
        PreviewTool<SplitRequest>("split","Split one editable object with a world-space plane; keep both, positive or negative side.");
        PreviewTool<BooleanRequest>("boolean","Union, subtractA-minusB or intersect two editable objects; Apply consumes the operands.");
        PreviewTool<RepairRequest>("repair","Preview conservative mesh cleanup and optional supported planar-hole caps. Diagnosis alone is not repair parity.");
        PreviewTool<TextRequest>("text","Create solid text from an installed font (sourceKind=font) or supplied physical contours (sourceKind=outline).");
        PreviewTool<EmbossRequest>("emboss","Add or engrave text on a supported planar bounds face, with physical placement/depth.");
        PreviewTool<ImageReliefRequest>("image_relief","Convert PNG/JPEG/BMP (sourceKind=file, absolute path) or pixels to a backed rectangular relief.");
        PreviewTool<ImageContourRequest>("image_contour","Convert an image to a threshold/invert solid contour with holes/islands retained.");
        PreviewTool<ImageEdgeRequest>("image_edge","Convert thresholded artwork into inner-edge solids with physical stroke width.");
        PreviewTool<ImageStampRequest>("image_stamp","Create raised artwork and backing with optional image mirroring.");
        PreviewTool<HollowRequest>("hollow","Create a bounded sealed cavity with minimum wall thickness; current Core operand limits apply.");
        PreviewTool<SimplifyRequest>("simplify","Reduce triangles within the supported shape/cavity and deviation limits; a safe partial result may be reported.");
        PreviewTool<SmoothRequest>("smooth","Smooth supported geometry with strength, deviation and sharp-edge controls while preserving cavity ownership.");
        PreviewTool<SeparateRequest>("separate","Separate disconnected solid pieces into objects, retaining cavities with their owning solid.");
        PreviewTool<ExtrudeDownRequest>("extrude_down","Extend geometry vertically down to an explicit world plane; supported holes remain open.");
        PreviewTool<MaterialRequest>("material","Set or clear opaque whole-object/group/member materials.");
        PreviewTool<SurfaceMaterialRequest>("surface_material","Paint/erase one triangle or connected surface using a canonical triangle index.");
        PreviewTool<SurfaceBrushRequest>("surface_brush","Paint/erase ordered physical-diameter brush strokes using world-ray samples; hidden surfaces remain protected.");
    }
}
