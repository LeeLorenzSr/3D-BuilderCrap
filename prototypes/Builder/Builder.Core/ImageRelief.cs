using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Top-to-bottom, unpremultiplied RGBA8 pixels in sRGB channel space. Encoded dimensions
/// determine aspect ratio; DPI, EXIF orientation and color profiles do not change geometry.</summary>
public sealed record RasterImage(int Width,int Height,ImmutableArray<byte> Rgba,string? Provenance=null);
/// <summary>Platform codec seam. Validate encoded size/dimensions before allocating pixels, return
/// one PNG/JPEG/BMP frame, and check cancellation around native decoding and through row copying.</summary>
public interface IRasterImageDecoder
{
    Task<RasterImage> DecodeAsync(string path,CancellationToken cancellation=default);
}
public abstract record ReliefSource;
public sealed record ImageFileSource(string Path):ReliefSource;
public sealed record PixelImageSource(RasterImage Image):ReliefSource;
/// <summary>Rectangular, closed height relief at minimum XYZ=(0,0,0). Width retains source aspect;
/// the longer grid dimension is Samples (2..128). White is high; inversion precedes alpha.
/// Transparent pixels retain backing. Source top maps to +Y. This is a piecewise-linear grid,
/// not a contour cutout, continuous image surface or attachment to an existing part.</summary>
public sealed record ImageReliefRequest(ReliefSource Source,double WidthMillimeters=80,
    double ReliefDepthMillimeters=3,double BaseThicknessMillimeters=1,int Samples=96,bool Invert=false):GeometryRequest;

public sealed partial class EditorSession
{
    /// <summary>Appends a private relief candidate. Exact owned-preview replacement retains its new
    /// object ID and issues a fresh preview token. Apply accepts one history step; all failure/cancel
    /// paths retain accepted state and the prior candidate. Creation is blocked in member scope.</summary>
    public Task<OperationResult> PreviewImageReliefAsync(ImageReliefRequest request,Guid? replacePreviewId=null,
        CancellationToken cancellation=default,IProgress<OperationProgress>? progress=null,long? expectedRevision=null)
        =>PreviewImageOperation(request,replacePreviewId,cancellation,progress,expectedRevision);

    private (SceneObject Object,string Summary) PrepareImageRelief(ImageReliefRequest request,EditorSnapshot captured,Guid objectId,
        CancellationToken cancellation,IProgress<OperationProgress>? progress)
    {
        ReliefMeshes.ValidateSettings(request);
        var image=ReadImageSource(request.Source,cancellation,progress);
        cancellation.ThrowIfCancellationRequested();
        var grid=ReliefMeshes.Describe(image,request);
        if(captured.Scene.Objects.Length+1>ModelLimits.MaxObjects || captured.Scene.TriangleCount+grid.Triangles>ModelLimits.MaxTriangles ||
            captured.Scene.Objects.Sum(o=>(long)o.Mesh.Vertices.Length)+grid.Vertices>ModelLimits.MaxVertices)
            throw new ModelException("This relief would exceed the scene object, triangle or vertex limit. Reduce Detail or remove other objects.");
        var mesh=ReliefMeshes.Create(image,request,grid,cancellation,progress);
        var summary=FormattableString.Invariant($"Image relief preview: {request.WidthMillimeters:G6} × {grid.HeightMillimeters:G6} mm; backing {request.BaseThicknessMillimeters:G6} mm, relief depth {request.ReliefDepthMillimeters:G6} mm; {grid.X} × {grid.Y} grid, {grid.Triangles:N0} triangles.");
        return(new(objectId,"Image relief",mesh,Affine.Identity),summary);
    }
}

internal static class ReliefMeshes
{
    internal readonly record struct Grid(int X,int Y,double HeightMillimeters)
    {
        public int Vertices=>2*X*Y;
        public int Triangles=>4*(X*Y-1);
    }
    internal static void ValidateSettings(ImageReliefRequest request)
    {
        if(!double.IsFinite(request.WidthMillimeters)||request.WidthMillimeters<.1||request.WidthMillimeters>1000)
            throw new ModelException("Relief width must be from 0.1 to 1,000 mm.");
        if(!double.IsFinite(request.ReliefDepthMillimeters)||request.ReliefDepthMillimeters<0||request.ReliefDepthMillimeters>100)
            throw new ModelException("Relief depth must be from 0 to 100 mm.");
        if(!double.IsFinite(request.BaseThicknessMillimeters)||request.BaseThicknessMillimeters<.1||request.BaseThicknessMillimeters>100)
            throw new ModelException("Backing thickness must be from 0.1 to 100 mm.");
        if(request.Samples<2||request.Samples>128)throw new ModelException("Detail must be an integer from 2 to 128 samples.");
    }
    internal static Grid Describe(RasterImage image,ImageReliefRequest request)
    {
        if(image is null||image.Width<1||image.Height<1||image.Width>2048||image.Height>2048||image.Rgba.IsDefault ||
            image.Rgba.Length!=4L*image.Width*image.Height)
            throw new ModelException("Images need 1–2,048 pixels per axis and exactly four RGBA bytes per pixel.");
        var height=request.WidthMillimeters*image.Height/image.Width;
        if(!double.IsFinite(height)||height<.1||height>1000)throw new ModelException("Aspect-preserving relief height must be from 0.1 to 1,000 mm. Change Width or choose another image.");
        var shorter=Math.Max(2,(int)Math.Round(request.Samples*(double)Math.Min(image.Width,image.Height)/Math.Max(image.Width,image.Height),MidpointRounding.AwayFromZero));
        return image.Width>=image.Height?new(request.Samples,shorter,height):new(shorter,request.Samples,height);
    }
    internal static Mesh Create(RasterImage image,ImageReliefRequest request,Grid grid,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var vertices=new Vec3[grid.Vertices];var triangles=new Triangle[grid.Triangles];var topCount=grid.X*grid.Y;
        double Scalar(int x,int y)=>ImageSampling.Scalar(image,x,y,request.Invert);
        for(var y=0;y<grid.Y;y++)
        {
            progress?.Report(new("Sampling image relief",y,grid.Y));ct.ThrowIfCancellationRequested();
            // Grid endpoints map to first/last pixel centers. One-pixel axes remain constant.
            var sy=(grid.Y-1-y)*(double)(image.Height-1)/(grid.Y-1);var y0=(int)Math.Floor(sy);var y1=Math.Min(y0+1,image.Height-1);var fy=sy-y0;
            for(var x=0;x<grid.X;x++)
            {
                var sx=x*(double)(image.Width-1)/(grid.X-1);var x0=(int)Math.Floor(sx);var x1=Math.Min(x0+1,image.Width-1);var fx=sx-x0;
                var low=Scalar(x0,y0)*(1-fx)+Scalar(x1,y0)*fx;var high=Scalar(x0,y1)*(1-fx)+Scalar(x1,y1)*fx;
                var z=request.BaseThicknessMillimeters+request.ReliefDepthMillimeters*(low*(1-fy)+high*fy);
                var px=x==grid.X-1?request.WidthMillimeters:request.WidthMillimeters*x/(grid.X-1);
                var py=y==grid.Y-1?grid.HeightMillimeters:grid.HeightMillimeters*y/(grid.Y-1);
                var i=y*grid.X+x;vertices[i]=new(px,py,z);vertices[topCount+i]=new(px,py,0);
            }
        }
        progress?.Report(new("Sampling image relief",grid.Y,grid.Y));ct.ThrowIfCancellationRequested();
        var ti=0;
        void Face(int a,int b,int c)=>triangles[ti++]=new(a,b,c);
        for(var y=0;y<grid.Y-1;y++)
        {
            progress?.Report(new("Building image relief",y,grid.Y-1));ct.ThrowIfCancellationRequested();
            for(var x=0;x<grid.X-1;x++)
            {
                var a=y*grid.X+x;var b=a+1;var c=a+grid.X;var d=c+1;
                Face(a,b,d);Face(a,d,c);Face(a+topCount,d+topCount,b+topCount);Face(a+topCount,c+topCount,d+topCount);
            }
        }
        // CCW perimeter from lower-left. The side's top edge reverses each top boundary edge.
        var perimeter=new List<int>(2*(grid.X+grid.Y-2));
        for(var x=0;x<grid.X;x++)perimeter.Add(x);
        for(var y=1;y<grid.Y;y++)perimeter.Add(y*grid.X+grid.X-1);
        for(var x=grid.X-2;x>=0;x--)perimeter.Add((grid.Y-1)*grid.X+x);
        for(var y=grid.Y-2;y>0;y--)perimeter.Add(y*grid.X);
        for(var i=0;i<perimeter.Count;i++)
        {
            ct.ThrowIfCancellationRequested();var a=perimeter[i];var b=perimeter[(i+1)%perimeter.Count];
            Face(a+topCount,b+topCount,b);Face(a+topCount,b,a);
        }
        progress?.Report(new("Building image relief",grid.Y-1,grid.Y-1));ct.ThrowIfCancellationRequested();
        return new(vertices,triangles);
    }
}
