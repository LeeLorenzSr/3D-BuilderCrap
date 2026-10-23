using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace Builder.Desktop;

/// <summary>Presentation metadata; command content, access keys and automation names stay intact.</summary>
public static class Icon
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Icon), new FrameworkPropertyMetadata(null));
    public static string? GetKey(DependencyObject target) => (string?)target.GetValue(KeyProperty);
    public static void SetKey(DependencyObject target, string? value) => target.SetValue(KeyProperty, value);

    public static readonly DependencyProperty StackedProperty = DependencyProperty.RegisterAttached(
        "Stacked", typeof(bool), typeof(Icon), new FrameworkPropertyMetadata(false));
    public static bool GetStacked(DependencyObject target) => (bool)target.GetValue(StackedProperty);
    public static void SetStacked(DependencyObject target, bool value) => target.SetValue(StackedProperty, value);
}

/// <summary>
/// Renders the application's embedded, path-only 24-unit SVG artwork. This is deliberately
/// not an importer for arbitrary SVG documents. No browser, external files or font glyphs.
/// </summary>
public sealed class SvgIcon : FrameworkElement
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(SvgIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public string? Key { get => (string?)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(SvgIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private sealed record Shape(Geometry Geometry, string Fill, string Stroke, double Width);
    private static readonly ConcurrentDictionary<string, Shape[]> Artwork = new(StringComparer.Ordinal);

    public SvgIcon() { IsHitTestVisible = false; Focusable = false; }

    private static Shape[] Load(string key)
    {
        using var stream = typeof(SvgIcon).Assembly.GetManifestResourceStream($"Builder.Icons.{key}.svg")
            ?? throw new InvalidOperationException($"Unknown application icon: {key}");
        var root = XDocument.Load(stream).Root!;
        if ((string?)root.Attribute("viewBox") != "0 0 24 24" || root.Elements().Any(e => e.Name.LocalName is not ("path" or "title")))
            throw new InvalidDataException($"Icon {key} must use the application's path-only SVG contract.");
        string Attribute(XElement path, string name, string fallback) => (string?)path.Attribute(name) ?? (string?)root.Attribute(name) ?? fallback;
        return root.Elements().Where(e => e.Name.LocalName == "path").Select(path => {
            var geometry = Geometry.Parse((string?)path.Attribute("d") ?? throw new InvalidDataException($"Missing path in {key}"));
            geometry.Freeze();
            return new Shape(geometry, Attribute(path, "fill", "none"), Attribute(path, "stroke", "currentColor"),
                double.Parse(Attribute(path, "stroke-width", "1.6"), CultureInfo.InvariantCulture));
        }).ToArray();
    }

    private Brush? Paint(string value) => value switch {
        "none" => null,
        "currentColor" => Foreground,
        _ => (Brush)new BrushConverter().ConvertFromInvariantString(value)!
    };

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (string.IsNullOrEmpty(Key)) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        context.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        foreach (var shape in Artwork.GetOrAdd(Key, Load))
        {
            var stroke = Paint(shape.Stroke);
            var pen = stroke is null ? null : new Pen(stroke, shape.Width) {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
            };
            context.DrawGeometry(Paint(shape.Fill), pen, shape.Geometry);
        }
        context.Pop();
        context.Pop();
    }
}
