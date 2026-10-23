using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Bright sampled cells become a flat solid, retaining the physical image frame and margins.
/// Luminance is inverted before alpha, then bilinearly sampled at cell centers. Foreground is strictly
/// greater than Threshold. Source top maps to +Y; exact cell boundaries form stair-step outlines.
/// Multiple islands and enclosed holes share one new unassigned object. Point contacts are refused.</summary>
public sealed record ImageContourRequest(ReliefSource Source,double WidthMillimeters=80,
    double ThicknessMillimeters=3,double Threshold=0.5,int Samples=96,bool Invert=false):GeometryRequest;

public sealed partial class EditorSession
{
    /// <summary>Stages a new silhouette from the accepted scene. Exact-owned relief/contour replacement
    /// keeps the candidate object ID and publishes a fresh token. Failure/cancel retains the previous
    /// candidate and all accepted state. Creation requires leaving member editing first.</summary>
    public Task<OperationResult> PreviewImageContourAsync(ImageContourRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
        =>PreviewImageOperation(request,replacePreviewId,cancellation,progress,expectedRevision);

    private (SceneObject Object,string Summary) PrepareImageContour(ImageContourRequest request,EditorSnapshot captured,Guid objectId,
        CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        ContourImages.ValidateSettings(request);
        var image=ReadImageSource(request.Source,cancellation,progress);
        var contours=ContourImages.Trace(image,request,cancellation,progress);
        var mesh=ExtrudeImageOutline(contours,request.ThicknessMillimeters,null,cancellation,progress,"Image contour");
        var summary=FormattableString.Invariant($"Image contour preview: frame {request.WidthMillimeters:G6} × {contours.HeightMillimeters:G6} mm; thickness {request.ThicknessMillimeters:G6} mm; {contours.X} × {contours.Y} cells, {contours.Islands} island(s), {contours.Holes} hole(s), {mesh.Triangles.Length:N0} triangles. Cell-boundary silhouette.");
        return(new(objectId,"Image contour",mesh,Affine.Identity),summary);
    }

}

internal static class ImageSampling
{
    internal static double Scalar(RasterImage image,int x,int y,bool invert)
    {
        var i=4*(y*image.Width+x);var rgba=image.Rgba;
        var luminance=(2126d*rgba[i]+7152d*rgba[i+1]+722d*rgba[i+2])/2_550_000;
        return(invert?1-luminance:luminance)*(rgba[i+3]/255d);
    }
    internal static double Bilinear(RasterImage image,double u,double v,bool invert)
    {
        var sx=u*(image.Width-1);var sy=v*(image.Height-1);
        var x0=(int)Math.Floor(sx);var y0=(int)Math.Floor(sy);var x1=Math.Min(x0+1,image.Width-1);var y1=Math.Min(y0+1,image.Height-1);
        var fx=sx-x0;var fy=sy-y0;
        var a=Scalar(image,x0,y0,invert)*(1-fx)+Scalar(image,x1,y0,invert)*fx;
        var b=Scalar(image,x0,y1,invert)*(1-fx)+Scalar(image,x1,y1,invert)*fx;
        return a*(1-fy)+b*fy;
    }
}

internal static class ContourImages
{
    internal static Mask InnerBorder(Mask mask,int rx,int ry,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        // A window reaching outside either axis cannot erode any foreground; avoid large-area arithmetic.
        if(rx>=mask.X || ry>=mask.Y){ct.ThrowIfCancellationRequested();return mask;}
        var stride=mask.X+1;var sums=new int[stride*(mask.Y+1)];
        for(var y=0;y<mask.Y;y++)
        {
            ct.ThrowIfCancellationRequested();var row=0;
            for(var x=0;x<mask.X;x++){if(mask.Cells[y*mask.X+x])row++;sums[(y+1)*stride+x+1]=sums[y*stride+x+1]+row;}
        }
        var border=new bool[mask.Cells.Length];
        for(var y=0;y<mask.Y;y++)
        {
            progress?.Report(new("Building image edge",y,mask.Y));ct.ThrowIfCancellationRequested();
            for(var x=0;x<mask.X;x++)
            {
                if(!mask.Cells[y*mask.X+x])continue;
                var eroded=false;
                if(x>=rx&&y>=ry&&x+rx<mask.X&&y+ry<mask.Y)
                {
                    var a=x-rx;var b=y-ry;var c=x+rx+1;var d=y+ry+1;
                    var count=sums[d*stride+c]-sums[b*stride+c]-sums[d*stride+a]+sums[b*stride+a];
                    eroded=count==(c-a)*(d-b);
                }
                border[y*mask.X+x]=!eroded;
            }
        }
        return mask with{Cells=border};
    }
    internal sealed record Mask(int X,int Y,double WidthMillimeters,double HeightMillimeters,bool[] Cells);
    internal sealed record Result(TextOutline Outline,Vec2 Minimum,int X,int Y,double HeightMillimeters,int Islands,int Holes);
    internal static void ValidateSettings(ImageContourRequest request)
    {
        if(!double.IsFinite(request.WidthMillimeters)||request.WidthMillimeters<.1||request.WidthMillimeters>1000)
            throw new ModelException("Contour width must be from 0.1 to 1,000 mm.");
        if(!double.IsFinite(request.ThicknessMillimeters)||request.ThicknessMillimeters<.1||request.ThicknessMillimeters>100)
            throw new ModelException("Contour thickness must be from 0.1 to 100 mm.");
        if(!double.IsFinite(request.Threshold)||request.Threshold<0||request.Threshold>1)
            throw new ModelException("Contour threshold must be from 0 to 1.");
        if(request.Samples<2||request.Samples>128)throw new ModelException("Detail must be an integer from 2 to 128 samples.");
    }
    internal static Result Trace(RasterImage image,ImageContourRequest request,CancellationToken ct,IProgress<OperationProgress>? progress)
        =>Trace(Sample(image,request,ct,progress),ct,progress);
    internal static Mask Sample(RasterImage image,ImageContourRequest request,CancellationToken ct,IProgress<OperationProgress>? progress,string mode="image contour")
    {
        ReliefMeshes.Grid grid;
        try{grid=ReliefMeshes.Describe(image,new(request.Source,request.WidthMillimeters,Samples:request.Samples));}
        catch(ModelException e){throw new ModelException(e.Message.Replace("relief","contour",StringComparison.Ordinal));}
        var mask=new bool[grid.X*grid.Y];var filled=0;
        for(var y=0;y<grid.Y;y++)
        {
            progress?.Report(new("Sampling "+mode,y,grid.Y));ct.ThrowIfCancellationRequested();
            for(var x=0;x<grid.X;x++)
                if(mask[y*grid.X+x]=ImageSampling.Bilinear(image,(x+.5)/grid.X,1-(y+.5)/grid.Y,request.Invert)>request.Threshold)filled++;
        }
        if(filled==0)throw new ModelException("No foreground remains at this threshold. Change Threshold, Invert or Detail.");
        return new(grid.X,grid.Y,request.WidthMillimeters,grid.HeightMillimeters,mask);
    }
    internal static Result Trace(Mask grid,CancellationToken ct,IProgress<OperationProgress>? progress,string mode="image contour")
    {
        bool Filled(int x,int y)=>x>=0&&x<grid.X&&y>=0&&y<grid.Y&&grid.Cells[y*grid.X+x];
        var edges=new Dictionary<(int X,int Y),(int X,int Y)>();
        void Edge((int X,int Y) a,(int X,int Y) b)
        {
            if(!edges.TryAdd(a,b))throw new ModelException("The contour has a point contact or pinch. Change Threshold or Detail to separate touching regions.");
        }
        for(var y=0;y<grid.Y;y++)
        {
            progress?.Report(new("Tracing "+mode,y,grid.Y));ct.ThrowIfCancellationRequested();
            for(var x=0;x<grid.X;x++)
            {
                if(!Filled(x,y))continue;
                // Filled material stays on the left: outer CCW, enclosed-hole CW.
                if(!Filled(x,y-1))Edge((x,y),(x+1,y));
                if(!Filled(x+1,y))Edge((x+1,y),(x+1,y+1));
                if(!Filled(x,y+1))Edge((x+1,y+1),(x,y+1));
                if(!Filled(x-1,y))Edge((x,y+1),(x,y));
            }
        }
        var loops=ImmutableArray.CreateBuilder<ImmutableArray<Vec2>>();var points=0;var islands=0;var holes=0;
        while(edges.Count>0)
        {
            ct.ThrowIfCancellationRequested();var start=edges.First().Key;var current=start;var raw=new List<(int X,int Y)>();
            do
            {
                ct.ThrowIfCancellationRequested();raw.Add(current);
                if(!edges.Remove(current,out var next))throw new ModelException("The image contour has an unsupported open boundary. Change Threshold or Detail.");
                current=next;
            }while(current!=start);
            var loop=ImmutableArray.CreateBuilder<Vec2>();
            for(var i=0;i<raw.Count;i++)
            {
                var a=raw[(i+raw.Count-1)%raw.Count];var b=raw[i];var c=raw[(i+1)%raw.Count];
                if((b.X-a.X)*(c.Y-b.Y)==(b.Y-a.Y)*(c.X-b.X))continue;
                loop.Add(new(b.X==grid.X?grid.WidthMillimeters:grid.WidthMillimeters*b.X/grid.X,
                    b.Y==grid.Y?grid.HeightMillimeters:grid.HeightMillimeters*b.Y/grid.Y));
            }
            points+=loop.Count;
            if(points>4096||loops.Count>=256)throw new ModelException("This contour exceeds 4,096 outline points or 256 boundaries. Reduce Detail or simplify the image.");
            var contour=loop.ToImmutable();if(TextOutlines.Area(contour)>0)islands++;else holes++;loops.Add(contour);
        }
        var minimum=new Vec2(loops.Min(l=>l.Min(p=>p.X)),loops.Min(l=>l.Min(p=>p.Y)));
        progress?.Report(new("Tracing "+mode,grid.Y,grid.Y));ct.ThrowIfCancellationRequested();
        return new(new(loops.ToImmutable(),TextFillRule.NonZero,"Sampled image cell silhouette."),minimum,grid.X,grid.Y,grid.HeightMillimeters,islands,holes);
    }
}
