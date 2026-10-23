using System.Collections.Immutable;

namespace Builder.Core;

public readonly record struct Vec2(double X,double Y);
public enum TextFillRule { EvenOdd, NonZero }
/// <summary>Closed contours in physical millimeters, without a repeated closing point. Core validates
/// simple, non-touching contours and resolves fill into CCW outer/CW hole boundaries. Provenance is descriptive.</summary>
public sealed record TextOutline(ImmutableArray<ImmutableArray<Vec2>> Contours,TextFillRule FillRule=TextFillRule.EvenOdd,string? Provenance=null);
/// <summary>Visible ink height, not typographic em size. Single-line printable text; flattening uses absolute mm tolerance.</summary>
public sealed record TextOutlineSpec(string Text,string FontFamily,double HeightMillimeters,double FlatteningToleranceMillimeters=0.02);
public interface ITextOutlineProvider
{
    Task<TextOutline> BuildAsync(TextOutlineSpec specification,CancellationToken cancellation=default);
}
public interface ITextFontCatalog { IReadOnlyList<string> GetFontFamilies(); }
public abstract record TextSource;
public sealed record FontTextSource(TextOutlineSpec Specification):TextSource;
public sealed record OutlineTextSource(TextOutline Outline):TextSource;

internal static class TextOutlines
{
    internal static void ValidateSpec(TextOutlineSpec spec)
    {
        if(spec is null || string.IsNullOrWhiteSpace(spec.Text) || spec.Text.Length>128 || spec.Text.Any(char.IsControl) || !ValidUnicode(spec.Text))
            throw new ModelException("Enter 1–128 printable text characters on one line.");
        if(string.IsNullOrWhiteSpace(spec.FontFamily) || spec.FontFamily.Length>100 || spec.FontFamily.Any(char.IsControl) || !ValidUnicode(spec.FontFamily))
            throw new ModelException("Choose an installed font family.");
        if(!double.IsFinite(spec.HeightMillimeters) || spec.HeightMillimeters<0.1 || spec.HeightMillimeters>1000)
            throw new ModelException("Enter a visible text height from 0.1 to 1,000 mm.");
        if(!double.IsFinite(spec.FlatteningToleranceMillimeters) || spec.FlatteningToleranceMillimeters<0.001 ||
            spec.FlatteningToleranceMillimeters>Math.Min(1,spec.HeightMillimeters/4))
            throw new ModelException("Text tolerance must be at least 0.001 mm and at most 1 mm or one quarter of the height.");
    }
    private static bool ValidUnicode(string value)
    {
        for(var i=0;i<value.Length;i++)if(char.IsSurrogate(value[i]))
        {if(!char.IsHighSurrogate(value[i]) || i+1==value.Length || !char.IsLowSurrogate(value[++i]))return false;}
        return true;
    }
    internal static TextOutline Normalize(TextOutline outline,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        if(outline is null || outline.Contours.IsDefaultOrEmpty || outline.Contours.Length>256 || !Enum.IsDefined(outline.FillRule))
            throw new ModelException("Text needs 1–256 valid closed contours.");
        var total=0;var loops=new List<Vec2[]>();
        foreach(var contour in outline.Contours)
        {
            ct.ThrowIfCancellationRequested();
            if(contour.IsDefault || contour.Length<3 || contour.Length>4097)throw new ModelException("A text contour needs at least three points.");
            var points=contour.ToArray();if(points.Length>3 && points[0]==points[^1])points=points[..^1];
            total+=points.Length;if(total>4096)throw new ModelException("Text is limited to 4,096 contour points.");
            if(points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||Math.Max(Math.Abs(p.X),Math.Abs(p.Y))>10_000_000) || points.Distinct().Count()!=points.Length)
                throw new ModelException("Text contour coordinates must be finite, distinct and within physical limits.");
            loops.Add(points);
        }
        var min=new Vec2(loops.Min(l=>l.Min(p=>p.X)),loops.Min(l=>l.Min(p=>p.Y)));
        var width=loops.Max(l=>l.Max(p=>p.X))-min.X;var height=loops.Max(l=>l.Max(p=>p.Y))-min.Y;
        if(width<0.1 || height<0.1 || width>1000 || height>1000)throw new ModelException("Text ink bounds must be 0.1–1,000 mm in both dimensions.");
        for(var i=0;i<loops.Count;i++)loops[i]=loops[i].Select(p=>new Vec2(p.X-min.X,p.Y-min.Y)).ToArray();
        var areas=loops.Select(Area).ToArray();
        if(areas.Any(a=>a==0 || !double.IsFinite(a)))throw new ModelException("A text contour has zero or unusable area.");
        var edges=loops.SelectMany((l,li)=>l.Select((a,i)=>(Loop:li,Index:i,A:a,B:l[(i+1)%l.Length]))).ToArray();
        for(var i=0;i<edges.Length;i++)
        {
            if(i%32==0){progress?.Report(new("Validating text outlines",i,edges.Length));ct.ThrowIfCancellationRequested();}
            var a=edges[i];
            for(var j=i+1;j<edges.Length;j++)
            {
                if(j%256==0)ct.ThrowIfCancellationRequested();var b=edges[j];
                if(a.Loop==b.Loop && ((a.Index+1)%loops[a.Loop].Length==b.Index || (b.Index+1)%loops[a.Loop].Length==a.Index))
                {
                    var shared=a.B==b.A?a.B:a.A;var first=a.A==shared?a.B:a.A;var second=b.A==shared?b.B:b.A;
                    if(Cross(first,shared,second)==0 && (first.X-shared.X)*(second.X-shared.X)+(first.Y-shared.Y)*(second.Y-shared.Y)>0)
                        throw new ModelException("Text contour edges overlap or double back.");
                    continue;
                }
                if(Intersects(a.A,a.B,b.A,b.B))throw new ModelException("Text contours must not cross, overlap or touch.");
            }
        }
        var result=ImmutableArray.CreateBuilder<ImmutableArray<Vec2>>();
        for(var i=0;i<loops.Count;i++)
        {
            ct.ThrowIfCancellationRequested();var parents=Enumerable.Range(0,loops.Count).Where(j=>j!=i && Contains(loops[j],loops[i][0])).ToArray();
            var outside=outline.FillRule==TextFillRule.EvenOdd ? parents.Length%2!=0 : parents.Sum(j=>Math.Sign(areas[j]))!=0;
            var inside=outline.FillRule==TextFillRule.EvenOdd ? !outside : parents.Sum(j=>Math.Sign(areas[j]))+Math.Sign(areas[i])!=0;
            if(outside==inside)continue; // Nonzero winding can contain a redundant internal contour.
            var points=loops[i];if((areas[i]>0)!=inside)Array.Reverse(points);
            result.Add(points.ToImmutableArray());
        }
        if(result.Count==0 || result.Sum(c=>Area(c))<=0)throw new ModelException("Text has no filled ink area.");
        progress?.Report(new("Validating text outlines",edges.Length,edges.Length));ct.ThrowIfCancellationRequested();
        return new(result.ToImmutable(),TextFillRule.NonZero,outline.Provenance);
    }
    internal static double Area(IEnumerable<Vec2> points)
    {
        var p=points.ToArray();double area=0;for(var i=0;i<p.Length;i++){var q=p[(i+1)%p.Length];area+=p[i].X*q.Y-p[i].Y*q.X;}return area/2;
    }
    private static double Cross(Vec2 a,Vec2 b,Vec2 c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
    private static bool On(Vec2 a,Vec2 b,Vec2 p)=>Cross(a,b,p)==0 && p.X>=Math.Min(a.X,b.X) && p.X<=Math.Max(a.X,b.X) && p.Y>=Math.Min(a.Y,b.Y) && p.Y<=Math.Max(a.Y,b.Y);
    private static bool Intersects(Vec2 a,Vec2 b,Vec2 c,Vec2 d)
    {
        if(Math.Max(a.X,b.X)<Math.Min(c.X,d.X)||Math.Max(c.X,d.X)<Math.Min(a.X,b.X)||Math.Max(a.Y,b.Y)<Math.Min(c.Y,d.Y)||Math.Max(c.Y,d.Y)<Math.Min(a.Y,b.Y))return false;
        return On(a,b,c)||On(a,b,d)||On(c,d,a)||On(c,d,b)||Math.Sign(Cross(a,b,c))!=Math.Sign(Cross(a,b,d))&&Math.Sign(Cross(c,d,a))!=Math.Sign(Cross(c,d,b));
    }
    private static bool Contains(Vec2[] p,Vec2 q)
    {
        var inside=false;for(var i=0;i<p.Length;i++){var a=p[i];var b=p[(i+1)%p.Length];if((a.Y>q.Y)!=(b.Y>q.Y) && q.X<(b.X-a.X)*(q.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;}return inside;
    }
}
