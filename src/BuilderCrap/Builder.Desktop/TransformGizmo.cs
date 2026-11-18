using Builder.Core;
using System.Windows;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

public enum HandleMode { Move, Rotate, Scale }

/// <summary>Axis is 0=X, 1=Y, 2=Z. Linear handles can be picked on either side of the selection.</summary>
public sealed record GizmoStroke(int Axis, IReadOnlyList<Point> Points, IReadOnlyList<Point>? Opposite = null);

/// <summary>Projection at the picked handle: pixels per world millimeter, or per degree for rotation.
/// Ring hits also expose a captured world-plane angle reader; callers unwrap successive readings for circular drags.</summary>
public sealed record GizmoHit(int Axis, Vector PixelsPerUnit, Func<Point,double?>? ReadAngleDegrees = null, bool Proportional = false);

/// <summary>An immutable presentation snapshot. It never reads or edits an EditorSession.</summary>
public sealed class GizmoLayout
{
    private readonly TransformGizmo.Projection? projection;
    private readonly IReadOnlyList<PickSegment> segments;
    internal sealed record PickSegment(int Axis, Point A, Point B, Func<double, Vector?> Mapping,
        Func<Point,double?>? ReadAngleDegrees = null, bool Proportional = false);

    internal GizmoLayout(TransformGizmo.Projection? projection, Point center,
        IReadOnlyList<GizmoStroke> strokes, IReadOnlyList<PickSegment> segments,
        Rect? scaleFrame = null, IReadOnlyList<Point>? proportionalHandles = null)
    {
        this.projection=projection; Center=center; Strokes=strokes; this.segments=segments;
        ScaleFrame=scaleFrame; ProportionalHandles=proportionalHandles ?? Array.Empty<Point>();
    }

    public bool IsVisible => Strokes.Count!=0;
    public Point Center { get; }
    public Rect? ScaleFrame { get; }
    public IReadOnlyList<Point> ProportionalHandles { get; }
    public Point? ProportionalHandle => ProportionalHandles.Count == 0 ? null : ProportionalHandles[0];
    public IReadOnlyList<GizmoStroke> Strokes { get; }

    /// <summary>Null means invalid camera/point, behind the near plane, beyond far, or outside the viewport.</summary>
    public Point? Project(Vec3 worldPoint) => projection?.Project(worldPoint);

    public GizmoHit? HitTest(Point point, double tolerance=11)
    {
        if(!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(tolerance) || tolerance<0)
            return null;
        GizmoHit? hit=null; var nearest=tolerance*tolerance;
        foreach(var segment in segments)
        {
            var delta=segment.B-segment.A; var lengthSquared=delta.LengthSquared;
            if(lengthSquared<=1e-12) continue;
            var parameter=Vector.Multiply(point-segment.A,delta)/lengthSquared;
            if(!double.IsFinite(parameter)) continue;
            var t=Math.Clamp(parameter,0,1);
            var distanceSquared=(point-(segment.A+delta*t)).LengthSquared;
            if(!double.IsFinite(distanceSquared) || distanceSquared>nearest) continue;
            if(segment.Mapping(t) is not {} mapping || !double.IsFinite(mapping.X) || !double.IsFinite(mapping.Y) ||
                !double.IsFinite(mapping.LengthSquared) || mapping.LengthSquared<=1e-24)
                continue;
            nearest=distanceSquared; hit=new(segment.Axis,mapping,segment.ReadAngleDegrees,segment.Proportional);
        }
        return hit;
    }
}

/// <summary>World-axis handles only. Drag mapping is fixed at pointer-down; document validation and snapping belong to Core.</summary>
public static class TransformGizmo
{
    private const double HandlePixels=90, MinimumAxisPixels=18, MinimumRingRadius=12;
    private const int RingSegments=64;
    private static readonly Vec3[] Axes=[new(1,0,0),new(0,1,0),new(0,0,1)];

    public static GizmoLayout Layout(ProjectionCamera camera, Size viewport, Vec3 center, HandleMode mode)
        => Layout(camera,viewport,center,mode,null,[]);

    internal static GizmoLayout Layout(ProjectionCamera camera, Size viewport, Vec3 center, HandleMode mode,
        Bounds3? bounds, IEnumerable<Point3D> worldVertices)
    {
        var projection=Projection.Capture(camera,viewport);
        if(projection==null || projection.Project(center) is not {} origin)
            return new(projection,new(),Array.Empty<GizmoStroke>(),Array.Empty<GizmoLayout.PickSegment>());
        var strokes=new List<GizmoStroke>(); var segments=new List<GizmoLayout.PickSegment>();
        var worldPerPixel = projection.WorldPerPixel(center);
        var mappings = new Vector?[3];
        var directions = new Vector[3];
        var limits = new (double Min,double Max)[3];
        if (mode is HandleMode.Move or HandleMode.Scale)
            for (var axis=0; axis<3; axis++)
            {
                if (projection.ProjectDirection(center,Axes[axis]) is not {} mapping ||
                    mapping.Length*worldPerPixel*HandlePixels < MinimumAxisPixels) continue;
                mappings[axis] = mapping;
                mapping.Normalize();
                directions[axis] = mapping;
            }
        // Use the same unique world vertices as the selection outline. Only the extrema survive
        // this pass; neither triangle corners nor a second set of projected points are retained.
        var footprint = Rect.Empty;
        double radiusSquared = 0;
        foreach (var vertex in worldVertices)
        {
            if (projection.Project(new(vertex.X,vertex.Y,vertex.Z)) is not {} point)
            {
                footprint = Rect.Empty;
                break;
            }
            footprint.Union(point);
            var offset = point-origin;
            radiusSquared = Math.Max(radiusSquared,offset.LengthSquared);
            for (var axis=0; axis<3 && mode!=HandleMode.Rotate; axis++)
            {
                var extent = Vector.Multiply(offset,directions[axis]);
                limits[axis] = (Math.Min(limits[axis].Min,extent),Math.Max(limits[axis].Max,extent));
            }
        }
        var maxReach = Math.Max(48, Math.Min(viewport.Width, viewport.Height)*.42);
        var radius = footprint.IsEmpty ? HandlePixels : Math.Clamp(Math.Sqrt(radiusSquared)+12, 46, maxReach);
        var length = worldPerPixel*radius;
        Rect? scaleFrame = null;
        var proportionalHandles = new List<Point>(4);
        var screen = new Rect(new Point(), viewport);
        if(double.IsFinite(length) && length>0 && mode is HandleMode.Move or HandleMode.Scale)
        {
            for (var axis=0; axis<3; axis++)
            {
                if (mappings[axis] is not {} mapping) continue;
                var unit = directions[axis];
                Point[]? Handle(int side)
                {
                    var direction = unit*side;
                    var extent = side>0 ? limits[axis].Max : -limits[axis].Min;
                    var reach = footprint.IsEmpty ? HandlePixels : Math.Clamp(extent+24, 43, maxReach);
                    var end = origin+direction*reach;
                    var start = end-direction*21;
                    if (!screen.Contains(start) || !screen.Contains(end)) return null;
                    var dragMapping = mode == HandleMode.Scale ? mapping*side : mapping;
                    segments.Add(new(axis,start,end,_=>dragMapping));
                    return [start,end];
                }
                var positive = Handle(1);
                var negative = Handle(-1);
                if (positive != null || negative != null)
                    strokes.Add(new(axis,positive ?? negative!,positive != null ? negative : null));
            }
            if (mode == HandleMode.Scale && bounds is {} selected && !footprint.IsEmpty)
            {
                var frame = footprint;
                frame.Inflate(3,3);
                var sizes = new[] {selected.Size.X, selected.Size.Y, selected.Size.Z};
                var axis = Array.IndexOf(sizes,sizes.Max());
                if (sizes[axis] > 0 && frame.Width > 12 && frame.Height > 12)
                {
                    scaleFrame = frame;
                    foreach (var corner in new[] {frame.TopRight,frame.TopLeft,frame.BottomRight,frame.BottomLeft})
                    {
                        if (!screen.Contains(corner)) continue;
                        var radial = corner-origin;
                        // A corner moved its full screen radius when the selected size doubles.
                        var mapping = radial*(2/sizes[axis]);
                        proportionalHandles.Add(corner);
                        segments.Add(new(axis,corner-new Vector(1,0),corner+new Vector(1,0),_=>mapping, Proportional:true));
                    }
                }
            }
        }
        else if(double.IsFinite(length) && length>0 && mode==HandleMode.Rotate)
        {
            for(var axis=0;axis<3;axis++)
            {
                // u x v points along the positive world axis, so positive angular drag
                // follows the same right-handed convention as the Core rotation command.
                var u=Axes[(axis+1)%3]; var v=Axes[(axis+2)%3];
                var points=new Point[RingSegments+1]; var complete=true;
                for(var i=0;i<=RingSegments;i++)
                {
                    var angle=2*Math.PI*i/RingSegments;
                    if(projection.Project(center+(u*Math.Cos(angle)+v*Math.Sin(angle))*length) is not {} p)
                    { complete=false; break; }
                    points[i]=p;
                }
                // A clipped ring is hidden as a whole. A line-like edge-on ring has
                // two ambiguous angles at one pixel and cannot provide a reliable drag.
                if(!complete || MinorRadius(points)<MinimumRingRadius) continue;
                strokes.Add(new(axis,Array.AsReadOnly(points)));
                double? ReadAngle(Point point)=>projection.ReadPlaneAngle(point,center,u,v);
                for(var i=0;i<RingSegments;i++)
                {
                    var startAngle=2*Math.PI*i/RingSegments;
                    Vector? Mapping(double fraction)
                    {
                        var angle=startAngle+fraction*2*Math.PI/RingSegments;
                        var p=center+(u*Math.Cos(angle)+v*Math.Sin(angle))*length;
                        var tangent=(v*Math.Cos(angle)-u*Math.Sin(angle))*(length*Math.PI/180);
                        var pixels=projection.ProjectDirection(p,tangent);
                        // Below 0.15 px/degree a small pointer movement would imply an
                        // excessive angle. Keep the visible ring, but do not pick that part.
                        return pixels is {} vector && vector.Length>=0.15 ? vector : null;
                    }
                    segments.Add(new(axis,points[i],points[i+1],Mapping,ReadAngle));
                }
            }
        }
        return new(projection,origin,strokes.AsReadOnly(),segments.AsReadOnly(),scaleFrame,proportionalHandles.AsReadOnly());
    }

    private static double MinorRadius(Point[] points)
    {
        double x=0,y=0;
        for(var i=0;i<RingSegments;i++) { x+=points[i].X; y+=points[i].Y; }
        x/=RingSegments; y/=RingSegments;
        double xx=0,xy=0,yy=0;
        for(var i=0;i<RingSegments;i++)
        { var dx=points[i].X-x; var dy=points[i].Y-y; xx+=dx*dx; xy+=dx*dy; yy+=dy*dy; }
        xx/=RingSegments; xy/=RingSegments; yy/=RingSegments;
        var discriminant=Math.Sqrt((xx-yy)*(xx-yy)+4*xy*xy);
        return Math.Sqrt(Math.Max(0,xx+yy-discriminant));
    }

    // Captures values, including Camera.Transform, so an in-progress drag cannot be
    // silently remapped if the caller later changes a WPF camera object.
    internal sealed class Projection
    {
        private readonly Matrix3D worldToCameraSpace, cameraToWorldSpace;
        private readonly Point3D position;
        private readonly Vector3D right,up,forward;
        private readonly double width,height,near,far,pixelsPerCameraUnit;
        private readonly bool perspective;

        private Projection(Matrix3D inverse, Matrix3D transform, Point3D position,
            Vector3D right, Vector3D up, Vector3D forward, Size viewport,
            double near, double far, double pixelsPerCameraUnit, bool perspective)
        {
            worldToCameraSpace=inverse; cameraToWorldSpace=transform; this.position=position;
            this.right=right; this.up=up; this.forward=forward;
            width=viewport.Width; height=viewport.Height; this.near=near; this.far=far;
            this.pixelsPerCameraUnit=pixelsPerCameraUnit; this.perspective=perspective;
        }

        public static Projection? Capture(ProjectionCamera? camera, Size viewport)
        {
            // Keep all emitted canvas coordinates finite and within a bounded viewport.
            if(camera==null || !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
                viewport.Width<=0 || viewport.Height<=0 || viewport.Width>1_000_000 || viewport.Height>1_000_000 ||
                !Finite(camera.Position) || !double.IsFinite(camera.NearPlaneDistance) ||
                double.IsNaN(camera.FarPlaneDistance) || camera.FarPlaneDistance<=Math.Max(0,camera.NearPlaneDistance)) return null;
            var forward=camera.LookDirection; if(!Normalize(ref forward)) return null;
            var cameraUp=camera.UpDirection; if(!Normalize(ref cameraUp)) return null;
            var right=Vector3D.CrossProduct(forward,cameraUp); if(!Normalize(ref right)) return null;
            var up=Vector3D.CrossProduct(right,forward); if(!Normalize(ref up)) return null;
            var transform=camera.Transform?.Value ?? Matrix3D.Identity;
            // WPF Transform3D is normally affine. Reject a projective/non-invertible
            // matrix instead of inventing a misleading tangent mapping for it.
            if(!Finite(transform) || transform.M14!=0 || transform.M24!=0 || transform.M34!=0 || transform.M44!=1 || !transform.HasInverse) return null;
            var inverse=transform; inverse.Invert(); if(!Finite(inverse)) return null;
            double pixels;
            if(camera is PerspectiveCamera p)
            {
                if(!double.IsFinite(p.FieldOfView) || p.FieldOfView<=0 || p.FieldOfView>=179) return null;
                pixels=viewport.Width/(2*Math.Tan(p.FieldOfView*Math.PI/360));
            }
            else if(camera is OrthographicCamera o)
            {
                if(!double.IsFinite(o.Width) || o.Width<=0) return null;
                pixels=viewport.Width/o.Width;
            }
            else return null;
            if(!double.IsFinite(pixels) || pixels<=0) return null;
            return new(inverse,transform,camera.Position,right,up,forward,viewport,
                Math.Max(0,camera.NearPlaneDistance),camera.FarPlaneDistance,pixels,camera is PerspectiveCamera);
        }

        public Point? Project(Vec3 world)
        {
            if(!Coordinates(world,out var x,out var y,out var depth)) return null;
            var scale=perspective ? pixelsPerCameraUnit/depth : pixelsPerCameraUnit;
            var screen=new Point(width/2+x*scale,height/2-y*scale);
            return double.IsFinite(screen.X) && double.IsFinite(screen.Y) &&
                screen.X>=0 && screen.X<=width && screen.Y>=0 && screen.Y<=height ? screen : null;
        }

        public Vector? ProjectDirection(Vec3 world, Vec3 direction)
        {
            if(!direction.IsFinite || !Coordinates(world,out var x,out var y,out var depth)) return null;
            var d=worldToCameraSpace.Transform(new Vector3D(direction.X,direction.Y,direction.Z));
            var dx=Vector3D.DotProduct(d,right); var dy=Vector3D.DotProduct(d,up); var dz=Vector3D.DotProduct(d,forward);
            var result=perspective ? new Vector(pixelsPerCameraUnit*(dx-x*dz/depth)/depth,-pixelsPerCameraUnit*(dy-y*dz/depth)/depth)
                : new Vector(pixelsPerCameraUnit*dx,-pixelsPerCameraUnit*dy);
            return double.IsFinite(result.X) && double.IsFinite(result.Y) ? result : null;
        }

        public bool TryRay(Point screen,out Vec3 origin,out Vec3 direction,out double minimum,out double maximum)
        {
            origin=direction=new(); minimum=maximum=0;
            if(!double.IsFinite(screen.X) || !double.IsFinite(screen.Y) || screen.X<0 || screen.Y<0 || screen.X>width || screen.Y>height) return false;
            var x=(screen.X-width/2)/pixelsPerCameraUnit; var y=(height/2-screen.Y)/pixelsPerCameraUnit;
            var localOrigin=perspective ? position : position+right*x+up*y;
            // Local direction has unit forward depth. Its transformed length converts camera-plane
            // clip depths into normalized world-ray distances, including affine Camera.Transform.
            var localDirection=perspective ? forward+right*x+up*y : forward;
            var worldOrigin=cameraToWorldSpace.Transform(localOrigin); var worldDirection=cameraToWorldSpace.Transform(localDirection);
            var worldPerDepth=worldDirection.Length;
            if(!Finite(worldOrigin) || !double.IsFinite(worldPerDepth) || worldPerDepth<=0 || !Normalize(ref worldDirection)) return false;
            minimum=near*worldPerDepth; maximum=far*worldPerDepth;
            origin=new(worldOrigin.X,worldOrigin.Y,worldOrigin.Z); direction=new(worldDirection.X,worldDirection.Y,worldDirection.Z);
            return double.IsFinite(minimum) && maximum>=minimum;
        }

        public double? ReadPlaneAngle(Point screen, Vec3 pivot, Vec3 u, Vec3 v)
        {
            // Mouse capture can carry the pointer beyond the viewport. Permit that,
            // while bounding unprojection arithmetic and rejecting the unstable center.
            if(!double.IsFinite(screen.X) || !double.IsFinite(screen.Y) ||
                Math.Abs(screen.X)>1_000_000 || Math.Abs(screen.Y)>1_000_000 ||
                Project(pivot) is not {} projectedPivot || (screen-projectedPivot).LengthSquared<4) return null;
            var x=(screen.X-width/2)/pixelsPerCameraUnit;
            var y=(height/2-screen.Y)/pixelsPerCameraUnit;
            var localOrigin=perspective ? position : position+right*x+up*y;
            var localDirection=perspective ? forward+right*x+up*y : forward;
            var origin=cameraToWorldSpace.Transform(localOrigin);
            var direction=cameraToWorldSpace.Transform(localDirection);
            if(!Finite(origin) || !Normalize(ref direction)) return null;
            var planeU=new Vector3D(u.X,u.Y,u.Z); var planeV=new Vector3D(v.X,v.Y,v.Z);
            var normal=Vector3D.CrossProduct(planeU,planeV);
            if(!Normalize(ref normal)) return null;
            var denominator=Vector3D.DotProduct(direction,normal);
            if(!double.IsFinite(denominator) || Math.Abs(denominator)<1e-8) return null;
            var distance=Vector3D.DotProduct(new Point3D(pivot.X,pivot.Y,pivot.Z)-origin,normal)/denominator;
            if(!double.IsFinite(distance) || distance<=0) return null;
            var intersection=origin+direction*distance;
            if(!Finite(intersection)) return null;
            var relative=intersection-new Point3D(pivot.X,pivot.Y,pivot.Z);
            var planeX=Vector3D.DotProduct(relative,planeU); var planeY=Vector3D.DotProduct(relative,planeV);
            if(!double.IsFinite(planeX) || !double.IsFinite(planeY) || (planeX==0 && planeY==0)) return null;
            var angle=Math.Atan2(planeY,planeX)*180/Math.PI;
            return double.IsFinite(angle) ? angle : null;
        }

        public double WorldPerPixel(Vec3 world)
        {
            if(!Coordinates(world,out _,out _,out var depth)) return double.NaN;
            var cameraUnits=(perspective ? depth : 1)/pixelsPerCameraUnit;
            var worldVector=cameraToWorldSpace.Transform(right*cameraUnits);
            return worldVector.Length;
        }

        private bool Coordinates(Vec3 world,out double x,out double y,out double depth)
        {
            x=y=depth=0; if(!world.IsFinite) return false;
            var local=worldToCameraSpace.Transform(new Point3D(world.X,world.Y,world.Z)); if(!Finite(local)) return false;
            var delta=local-position; x=Vector3D.DotProduct(delta,right); y=Vector3D.DotProduct(delta,up); depth=Vector3D.DotProduct(delta,forward);
            return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(depth) && depth>near && depth<far;
        }

        private static bool Normalize(ref Vector3D v)
        {
            var scale=Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)));
            if(!double.IsFinite(scale) || scale==0) return false;
            v/=scale; var length=v.Length; if(!double.IsFinite(length) || length<=1e-12) return false;
            v/=length; return true;
        }
        private static bool Finite(Point3D p)=>double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
        private static bool Finite(Matrix3D m)=>
            double.IsFinite(m.M11) && double.IsFinite(m.M12) && double.IsFinite(m.M13) && double.IsFinite(m.M14) &&
            double.IsFinite(m.M21) && double.IsFinite(m.M22) && double.IsFinite(m.M23) && double.IsFinite(m.M24) &&
            double.IsFinite(m.M31) && double.IsFinite(m.M32) && double.IsFinite(m.M33) && double.IsFinite(m.M34) &&
            double.IsFinite(m.OffsetX) && double.IsFinite(m.OffsetY) && double.IsFinite(m.OffsetZ) && double.IsFinite(m.M44);
    }
}
