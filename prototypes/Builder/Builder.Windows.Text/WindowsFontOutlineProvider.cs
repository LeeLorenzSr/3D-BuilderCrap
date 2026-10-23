using Builder.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Builder.Windows.Text;

/// <summary>Installed Windows typography to portable physical contours. No window, viewport,
/// application resources, font binaries, or pre-generated glyph library is required or packaged.</summary>
public sealed class WindowsFontOutlineProvider : ITextOutlineProvider,ITextFontCatalog
{
    private static readonly Lazy<ReadOnlyCollection<string>> Catalog=new(()=>Array.AsReadOnly(Fonts.SystemFontFamilies
        .Select(f=>f.Source).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()));
    public IReadOnlyList<string> GetFontFamilies()=>Catalog.Value;

    public Task<TextOutline> BuildAsync(TextOutlineSpec specification,CancellationToken cancellation=default)
        =>Task.Run(()=>Build(specification,cancellation),cancellation);

    private static TextOutline Build(TextOutlineSpec spec,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(spec.Text)||spec.Text.Length>128||spec.Text.Any(char.IsControl))
            throw new ModelException("Enter one line of text, 1–128 characters, with at least one visible character.");
        var remaining=spec.Text.AsSpan();
        while(!remaining.IsEmpty)
        {
            if(Rune.DecodeFromUtf16(remaining,out _,out var consumed)!=OperationStatus.Done)
                throw new ModelException("Text contains an invalid Unicode sequence.");
            remaining=remaining[consumed..];
        }
        if(!double.IsFinite(spec.HeightMillimeters)||spec.HeightMillimeters<0.1||spec.HeightMillimeters>1000||
            !double.IsFinite(spec.FlatteningToleranceMillimeters)||spec.FlatteningToleranceMillimeters<0.001||
            spec.FlatteningToleranceMillimeters>Math.Min(1,spec.HeightMillimeters/4))
            throw new ModelException("Text height must be 0.1–1,000 mm, with a valid physical outline tolerance.");
        var family=Catalog.Value.FirstOrDefault(f=>string.Equals(f,spec.FontFamily,StringComparison.OrdinalIgnoreCase))
            ??throw new ModelException("The chosen font is not installed. Choose an installed font family.");
        var formatted=new FormattedText(spec.Text,CultureInfo.GetCultureInfo("en-US"),FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),100,Brushes.Black,1);
        var drawing=new DrawingGroup();using(var context=drawing.Open())context.DrawText(formatted,new Point());
        var actualFonts=new SortedDictionary<string,object>(StringComparer.Ordinal);
        foreach(var run in GlyphRuns(drawing))
        {
            cancellation.ThrowIfCancellationRequested();
            if(run.GlyphIndices.Contains((ushort)0))throw new ModelException("A character has no supported glyph. Change the text or font; missing-glyph boxes are not accepted.");
            var face=run.GlyphTypeface;var uri=face.FontUri;
            if(!actualFonts.ContainsKey(uri.AbsoluteUri))
            {
                string? hash=null;
                if(uri.IsFile&&File.Exists(uri.LocalPath))
                {
                    try { using var file=File.OpenRead(uri.LocalPath);hash=Convert.ToHexString(SHA256.HashData(file)); }
                    catch(IOException) { /* Optional provenance must not prevent otherwise usable installed typography. */ }
                    catch(UnauthorizedAccessException) { }
                }
                actualFonts.Add(uri.AbsoluteUri,new{families=face.FamilyNames.Values.Distinct().Order().ToArray(),fontUri=uri.AbsoluteUri,sha256=hash});
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var geometry=formatted.BuildGeometry(new Point());var bounds=geometry.Bounds;
        if(bounds.IsEmpty||bounds.Height<=0||bounds.Width<=0)throw new ModelException("This text has no usable filled outline. Choose visible text and an outline font.");
        var scale=spec.HeightMillimeters/bounds.Height;
        // Scale into physical mm BEFORE outlining/flattening; WPF Y-down becomes Core Y-up.
        geometry.Transform=new MatrixTransform(scale,0,0,-scale,-bounds.Left*scale,bounds.Bottom*scale);
        var halfTolerance=spec.FlatteningToleranceMillimeters/2;
        var outlined=geometry.GetOutlinedPathGeometry(halfTolerance,ToleranceType.Absolute);
        cancellation.ThrowIfCancellationRequested();
        var flat=outlined.GetFlattenedPathGeometry(halfTolerance,ToleranceType.Absolute);
        cancellation.ThrowIfCancellationRequested();
        var loops=new List<List<Vec2>>();var vertexCount=0;
        foreach(var figure in flat.Figures)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!figure.IsFilled)continue;
            if(!figure.IsClosed)throw new ModelException("The font returned an open outline; choose another font.");
            var points=new List<Vec2>();
            void Add(Point point)
            {
                cancellation.ThrowIfCancellationRequested();
                var p=flat.Transform?.Transform(point)??point;
                if(!double.IsFinite(p.X)||!double.IsFinite(p.Y))throw new ModelException("The font returned non-finite outline coordinates.");
                var value=new Vec2(p.X,p.Y);if(points.Count==0||points[^1]!=value)points.Add(value);
                if(vertexCount+points.Count>4097)throw new ModelException("Text is too detailed. Use shorter text or a simpler font.");
            }
            Add(figure.StartPoint);
            foreach(var segment in figure.Segments)
            {
                cancellation.ThrowIfCancellationRequested();
                if(segment is LineSegment line)Add(line.Point);
                else if(segment is PolyLineSegment poly)foreach(var point in poly.Points)Add(point);
                else throw new ModelException("The font outline did not flatten to straight segments.");
            }
            if(points.Count>1&&points[^1]==points[0])points.RemoveAt(points.Count-1);
            if(points.Count<3)throw new ModelException("The font returned a collapsed contour; choose another font or larger text.");
            vertexCount+=points.Count;loops.Add(points);
            if(vertexCount>4096||loops.Count>256)throw new ModelException("Text is too detailed. Use shorter text, a simpler font or a larger outline tolerance.");
        }
        if(loops.Count==0)throw new ModelException("This text has no usable filled outline.");
        var minX=loops.Min(l=>l.Min(p=>p.X));var minY=loops.Min(l=>l.Min(p=>p.Y));var maxY=loops.Max(l=>l.Max(p=>p.Y));
        if(maxY<=minY)throw new ModelException("The font returned a collapsed outline.");
        // Native WPF path operations can introduce tiny float-sized bounds offsets. Return exact
        // minXY zero and physical INK height, not font em size; preserve every contour/fill rule.
        var finalScale=spec.HeightMillimeters/(maxY-minY);
        var contours=loops.Select(l=>l.Select(p=>new Vec2((p.X-minX)*finalScale,(p.Y-minY)*finalScale)).ToImmutableArray()).ToImmutableArray();
        cancellation.ThrowIfCancellationRequested();
        var provenance=JsonSerializer.Serialize(new{provider="Windows WPF installed typography",runtime=Environment.Version.ToString(),requestedFamily=family,
            actualFonts=actualFonts.Values,layoutCulture="en-US",flowDirection="LeftToRight with WPF script shaping",style="Normal",weight="Normal",
            physicalInkHeightMillimeters=spec.HeightMillimeters,flatteningToleranceMillimeters=spec.FlatteningToleranceMillimeters,
            normalization="Actual filled ink bounds; minimum XY zero; Y up; outlined filled region before flattening",characterFallback="Installed WPF fallback allowed and recorded; unknown family or glyph index zero rejected"});
        return new(contours,flat.FillRule==FillRule.Nonzero?TextFillRule.NonZero:TextFillRule.EvenOdd,provenance);
    }
    private static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing)
    {
        if(drawing is GlyphRunDrawing glyph)yield return glyph.GlyphRun;
        else if(drawing is DrawingGroup group)foreach(var child in group.Children)foreach(var run in GlyphRuns(child))yield return run;
    }
}
