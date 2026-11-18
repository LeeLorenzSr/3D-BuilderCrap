using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

public partial class MainWindow
{
    private void RefreshFloor()
    {
        if (FloorLayer.Visibility != Visibility.Visible) return;
        var center = new Point(Math.Round(focus.X / BuildPlateFloor.Block) * BuildPlateFloor.Block,
            Math.Round(focus.Y / BuildPlateFloor.Block) * BuildPlateFloor.Block);
        if (floorCenter == center) return;
        floorCenter = center;
        FloorLayer.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : BuildPlateFloor.Horizon;
        FloorVisual.Content = BuildPlateFloor.Build(center);
    }
}

// The floor is rendered with the scene camera in a separate background viewport. This keeps
// it outside picking and leaves reflected geometry and below-bed edits visible from either side.
internal static class BuildPlateFloor
{
    internal const int Block = 100;
    private const int Tile = 10, Mark = 50, BlocksPerSide = 16;
    private static readonly Color Dark = Color.FromRgb(85,85,85), Light = Color.FromRgb(106,106,106);
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    internal static readonly Brush Horizon = FrozenBrush(Color.FromRgb(96,96,96));
    private static readonly Lazy<Material> Checker = new(() => CheckerMaterial(Dark,Light));

    internal static Model3DGroup Build(Point center)
    {
        var floor = new MeshGeometry3D();
        var marks = new List<(double X, double Y, double Value)>();
        var left = center.X - BlocksPerSide * Block / 2;
        var bottom = center.Y - BlocksPerSide * Block / 2;
        var highContrast = SystemParameters.HighContrast;
        var dark = highContrast ? SystemColors.WindowColor : Dark;
        var light = highContrast ? Color.Multiply(dark,.78f) + Color.Multiply(SystemColors.WindowTextColor,.22f) : Light;
        var checker = highContrast ? CheckerMaterial(dark,light) : Checker.Value;

        // Reuse the same 100 mm checker texture away from the axes. On the two
        // positive axes replace a 50 x 10 mm strip with its numbered checker tiles.
        for (var row = 0; row < BlocksPerSide; row++)
        for (var column = 0; column < BlocksPerSide; column++)
        {
            var x = left + column * Block;
            var y = bottom + row * Block;
            if (!(y == 0 && x >= 0 || x == 0 && y >= 0))
            {
                Quad(floor,x,y,Block,Block,new Rect(0,0,1,1));
                continue;
            }
            for (var dy = 0; dy < Block; dy += Tile)
            for (var dx = 0; dx < Block; dx += Mark)
            {
                var mx = x+dx;
                var my = y+dy;
                if (my == 0 || mx == 0 && dy % Mark == 0)
                    marks.Add((mx,my,my == 0 ? mx : my));
                else
                    Quad(floor,mx,my,Mark,Tile,new Rect((double)dx/Block,1-(double)(dy+Tile)/Block,
                        (double)Mark/Block,(double)Tile/Block));
            }
        }
        // WPF clips an isolated viewport whose complete scene has zero depth.
        // An unreferenced point gives it a depth interval without adding a rendered face.
        floor.Positions.Add(new(center.X,center.Y,-1));
        floor.TextureCoordinates.Add(new(0,0));
        var group = new Model3DGroup();
        group.Children.Add(new GeometryModel3D(floor,checker) { BackMaterial = checker });
        if (marks.Count > 0)
        {
            var ink = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(Color.FromRgb(38,38,38));
            group.Children.Add(NumberedTiles(marks,FrozenBrush(dark),FrozenBrush(light),ink));
        }
        group.Freeze();
        return group;
    }

    private static GeometryModel3D NumberedTiles(List<(double X, double Y, double Value)> marks,
        Brush dark, Brush light, Brush ink)
    {
        const int pixelsPerMm = 8;
        const int stripWidth = Mark * pixelsPerMm, stripHeight = Tile * pixelsPerMm;
        var values = marks.Select(m=>m.Value).Distinct().ToArray();
        var columns = Math.Min(values.Length,4);
        var rows = (values.Length + columns - 1) / columns;
        var image = new DrawingVisual();
        var uv = new Dictionary<double,Rect>();
        using (var drawing = image.RenderOpen())
        {
            for (var i = 0; i < values.Length; i++)
            {
                var column = i % columns;
                var row = i / columns;
                var x = column * stripWidth;
                var y = row * stripHeight;
                for (var cell = 0; cell < Mark / Tile; cell++)
                {
                    var isLight = (values[i] / Tile + cell) % 2 == 0;
                    drawing.DrawRectangle(isLight ? light : dark,null,new Rect(x+cell*stripHeight,y,stripHeight,stripHeight));
                }
                var label = new FormattedText(values[i].ToString("0",CultureInfo.InvariantCulture)+" mm",
                    CultureInfo.InvariantCulture,FlowDirection.LeftToRight,LabelTypeface,5.5*pixelsPerMm,ink,1);
                drawing.DrawText(label,new Point(x + .25*pixelsPerMm,y + (stripHeight-label.Height)/2));
                uv.Add(values[i],new Rect((double)column/columns,(double)row/rows,1d/columns,1d/rows));
            }
        }
        var bitmap = new RenderTargetBitmap(columns*stripWidth,rows*stripHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(image);
        var material = Texture(bitmap);
        var mesh = new MeshGeometry3D();
        foreach (var mark in marks) Quad(mesh,mark.X,mark.Y,Mark,Tile,uv[mark.Value]);
        return new GeometryModel3D(mesh,material) { BackMaterial = material };
    }
    private static void Quad(MeshGeometry3D mesh,double x,double y,double width,double height,Rect uv)
    {
        var first = mesh.Positions.Count;
        mesh.Positions.Add(new(x,y,0)); mesh.Positions.Add(new(x+width,y,0));
        mesh.Positions.Add(new(x+width,y+height,0)); mesh.Positions.Add(new(x,y+height,0));
        mesh.TextureCoordinates.Add(uv.BottomLeft); mesh.TextureCoordinates.Add(uv.BottomRight);
        mesh.TextureCoordinates.Add(uv.TopRight); mesh.TextureCoordinates.Add(uv.TopLeft);
        mesh.TriangleIndices.Add(first); mesh.TriangleIndices.Add(first+1); mesh.TriangleIndices.Add(first+2);
        mesh.TriangleIndices.Add(first); mesh.TriangleIndices.Add(first+2); mesh.TriangleIndices.Add(first+3);
    }

    private static Material CheckerMaterial(Color dark, Color light)
    {
        const int samplesPerTile = 32;
        const int side = Block / Tile * samplesPerTile;
        var pixels = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var color = ((x / samplesPerTile + y / samplesPerTile) & 1) == 0 ? dark : light;
            var offset = (y * side + x) * 4;
            pixels[offset] = color.B; pixels[offset+1] = color.G; pixels[offset+2] = color.R; pixels[offset+3] = 255;
        }
        return Texture(BitmapSource.Create(side,side,96,96,PixelFormats.Bgra32,null,pixels,side*4));
    }

    private static Material Texture(BitmapSource bitmap)
    {
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0,0,1,1) };
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }
    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
