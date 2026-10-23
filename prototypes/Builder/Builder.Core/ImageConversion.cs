namespace Builder.Core;

/// <summary>The inner cell-boundary border of thresholded foreground. Stroke is rounded upward
/// separately to whole X/Y cells; rectangular erosion treats outside the frame as background.
/// Thin features can remain completely filled. Other sampling rules match ImageContourRequest.</summary>
public sealed record ImageEdgeRequest(ReliefSource Source,double WidthMillimeters=80,double ThicknessMillimeters=3,
    double StrokeWidthMillimeters=2,double Threshold=0.5,int Samples=96,bool Invert=false):GeometryRequest;
/// <summary>A binary raised silhouette on a full rectangular backing. Mirror reflects sampled cells
/// in frame X before tracing; it defaults on for a stamp impression. Holes stop at the backing.</summary>
public sealed record ImageStampRequest(ReliefSource Source,double WidthMillimeters=80,double RaisedHeightMillimeters=2,
    double BaseThicknessMillimeters=2,double Threshold=0.5,int Samples=96,bool Invert=false,bool Mirror=true):GeometryRequest;

public sealed partial class EditorSession
{
    public Task<OperationResult> PreviewImageEdgeAsync(ImageEdgeRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
        =>PreviewImageOperation(request,replacePreviewId,cancellation,progress,expectedRevision);
    public Task<OperationResult> PreviewImageStampAsync(ImageStampRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
        =>PreviewImageOperation(request,replacePreviewId,cancellation,progress,expectedRevision);

    // All four image APIs share preview ownership, scope, publication and cancellation semantics.
    private Task<OperationResult> PreviewImageOperation(GeometryRequest request,Guid? replacePreviewId,
        CancellationToken cancellation,IProgress<OperationProgress>? progress,long? expectedRevision)
    {
        lock(gate)
        {
            if(Guard(expectedRevision,true) is {} blocked)return Task.FromResult(blocked);
            if(!IsImageRequest(request))return Task.FromResult(new OperationResult(Outcome.Invalid,"Choose image conversion settings."));
            if(GuardMemberScope(request.MemberGroupId) is {} scope)return Task.FromResult(scope);
            if(editingGroupId.HasValue)return Task.FromResult(DoneWithGroup());
            if(preview is {} current)
            {
                if(current.Id!=replacePreviewId || !IsImageRequest(current.Request) || current.BaseRevision!=revision)
                    return Task.FromResult(new OperationResult(Outcome.Conflict,"This image preview is no longer current."));
            }
            else if(replacePreviewId.HasValue)return Task.FromResult(new OperationResult(Outcome.Conflict,"There is no image preview to replace."));
            var captured=Snapshot;var objectId=preview?.ResultObjectIds.Single()??Guid.NewGuid();
            return RunIo(()=>
            {
                var mode=request switch{ImageReliefRequest=>"image relief",ImageContourRequest=>"image contour",ImageEdgeRequest=>"image edge",_=>"image stamp"};
                progress?.Report(new("Preparing "+mode));cancellation.ThrowIfCancellationRequested();
                if(captured.Scene.Objects.Length>=ModelLimits.MaxObjects)throw new ModelException("Adding this image would exceed the scene object limit.");
                var result=request switch {
                    ImageReliefRequest r=>PrepareImageRelief(r,captured,objectId,cancellation,progress),
                    ImageContourRequest r=>PrepareImageContour(r,captured,objectId,cancellation,progress),
                    ImageEdgeRequest r=>PrepareImageEdge(r,objectId,cancellation,progress),
                    ImageStampRequest r=>PrepareImageStamp(r,objectId,cancellation,progress),
                    _=>throw new ModelException("Choose a supported image conversion.")};
                var candidate=captured.Scene.WithObjects(captured.Scene.Objects.Add(result.Object));
                progress?.Report(new("Validating "+mode));cancellation.ThrowIfCancellationRequested();candidate.Validate(cancellation);
                progress?.Report(new("Ready to preview "+mode));cancellation.ThrowIfCancellationRequested();
                lock(gate)
                {
                    cancellation.ThrowIfCancellationRequested();preview=new(Guid.NewGuid(),captured.Revision,request,candidate,objectId,[objectId],result.Summary)
                        {SelectedObjectIds=[objectId],EditingGroupId=null};
                }
                return result.Summary;
            },expectedRevision,allowPreview:true);
        }
    }
    private static bool IsImageRequest(GeometryRequest? request)=>request is ImageReliefRequest or ImageContourRequest or ImageEdgeRequest or ImageStampRequest;

    private (SceneObject Object,string Summary) PrepareImageEdge(ImageEdgeRequest request,Guid objectId,
        CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var contour=new ImageContourRequest(request.Source,request.WidthMillimeters,request.ThicknessMillimeters,request.Threshold,request.Samples,request.Invert);
        ContourImages.ValidateSettings(contour);ValidateImageDimension(request.StrokeWidthMillimeters,"Stroke width");
        var image=ReadImageSource(request.Source,ct,progress);
        var mask=ContourImages.Sample(image,contour,ct,progress,"image edge");
        var rx=(int)Math.Ceiling(request.StrokeWidthMillimeters/(mask.WidthMillimeters/mask.X));
        var ry=(int)Math.Ceiling(request.StrokeWidthMillimeters/(mask.HeightMillimeters/mask.Y));
        var edge=ContourImages.InnerBorder(mask,rx,ry,ct,progress);
        var contours=ContourImages.Trace(edge,ct,progress,"image edge");
        var mesh=ExtrudeImageOutline(contours,request.ThicknessMillimeters,null,ct,progress,"Image edge");
        var summary=FormattableString.Invariant($"Image edge preview: frame {mask.WidthMillimeters:G6} × {mask.HeightMillimeters:G6} mm; thickness {request.ThicknessMillimeters:G6} mm; {mask.X} × {mask.Y} cells; actual grid stroke X {rx*mask.WidthMillimeters/mask.X:G6}, Y {ry*mask.HeightMillimeters/mask.Y:G6} mm; {mesh.Triangles.Length:N0} triangles. Inner cell-boundary border; thin features can remain filled.");
        return(new(objectId,"Image edge",mesh,Affine.Identity),summary);
    }
    private (SceneObject Object,string Summary) PrepareImageStamp(ImageStampRequest request,Guid objectId,
        CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var contour=new ImageContourRequest(request.Source,request.WidthMillimeters,request.RaisedHeightMillimeters,request.Threshold,request.Samples,request.Invert);
        ContourImages.ValidateSettings(contour);ValidateImageDimension(request.BaseThicknessMillimeters,"Stamp backing thickness");
        var image=ReadImageSource(request.Source,ct,progress);
        var mask=ContourImages.Sample(image,contour,ct,progress,"image stamp");
        if(request.Mirror)
        {
            var reflected=new bool[mask.Cells.Length];
            for(var y=0;y<mask.Y;y++){ct.ThrowIfCancellationRequested();for(var x=0;x<mask.X;x++)reflected[y*mask.X+x]=mask.Cells[y*mask.X+mask.X-1-x];}
            mask=mask with{Cells=reflected};
        }
        var contours=ContourImages.Trace(mask,ct,progress,"image stamp");
        var backing=new SceneObject(objectId,"Image stamp",Primitives.Create(PrimitiveKind.Cube,new(mask.WidthMillimeters,mask.HeightMillimeters,request.BaseThicknessMillimeters)),Affine.Identity);
        var mesh=ExtrudeImageOutline(contours,request.RaisedHeightMillimeters,backing,ct,progress,"Image stamp");
        var summary=FormattableString.Invariant($"Image stamp preview: frame {mask.WidthMillimeters:G6} × {mask.HeightMillimeters:G6} mm; backing {request.BaseThicknessMillimeters:G6} mm, raised height {request.RaisedHeightMillimeters:G6} mm; {mask.X} × {mask.Y} cells, mirror X {(request.Mirror?"on":"off")}; {mesh.Triangles.Length:N0} triangles. Full backing joins islands; holes stop at the backing.");
        return(new(objectId,"Image stamp",mesh,Affine.Identity),summary);
    }
    private static void ValidateImageDimension(double value,string field)
    {
        if(!double.IsFinite(value)||value<.1||value>100)throw new ModelException(field+" must be from 0.1 to 100 mm.");
    }
    private Mesh ExtrudeImageOutline(ContourImages.Result contours,double depth,SceneObject? backing,
        CancellationToken ct,IProgress<OperationProgress>? progress,string label)
    {
        try
        {
            var outline=TextOutlines.Normalize(contours.Outline,ct,progress);
            var overlap=backing is null?0:Math.Min(.1,depth/10);
            return geometry.Calculate(new TextSolidRequest(outline,depth,overlap,
                Affine.Translation(new(contours.Minimum.X,contours.Minimum.Y,backing?.Bounds.Max.Z??0)),backing is null?null:EmbossMode.Raised),
                backing is null?[]:[backing],ct,progress).Single()??throw new ModelException("The image produced no usable solid.");
        }
        catch(ModelException e){throw new ModelException(e.Message.Replace("Text",label,StringComparison.Ordinal).Replace("text",label.ToLowerInvariant(),StringComparison.Ordinal));}
    }
    private RasterImage ReadImageSource(ReliefSource source,CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        cancellation.ThrowIfCancellationRequested();
        if(source is PixelImageSource pixels)return pixels.Image;
        if(source is not ImageFileSource file)throw new ModelException("Choose an image file or explicit RGBA pixels.");
        if(string.IsNullOrWhiteSpace(file.Path))throw new ModelException("Choose an image file.");
        if(imageDecoder is null)throw new ModelException("No image decoder is configured. Supply explicit RGBA pixels or configure a decoder.");
        var fullPath=Path.GetFullPath(file.Path);
        if(new FileInfo(fullPath).Length>ModelLimits.MaxFileBytes)throw new ModelException("Image files are limited to 64 MiB.");
        progress?.Report(new("Decoding image"));cancellation.ThrowIfCancellationRequested();
        try{return imageDecoder.DecodeAsync(fullPath,cancellation).GetAwaiter().GetResult();}
        catch(OperationCanceledException){throw;}catch(ModelException){throw;}catch(Exception e){throw new IOException("Could not decode the image: "+e.Message,e);}
    }
}
