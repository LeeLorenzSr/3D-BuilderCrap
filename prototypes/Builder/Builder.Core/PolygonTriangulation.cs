namespace Builder.Core;

// Shared bounded simple-planar-polygon ear clipping; OBJ behavior and winding are unchanged.
internal static class PolygonTriangulation
{
    internal static IEnumerable<Triangle> Triangulate(List<int> indices,List<Vec3> vertices,string format="OBJ")
    {
        if(indices.Distinct().Count()!=indices.Count||indices.Select(i=>vertices[i]).Distinct().Count()!=indices.Count)throw new ModelException(format+" polygon vertices must be distinct.");
        var points=indices.Select(i=>vertices[i]).ToArray();var origin=points[0];var normal=new Vec3();
        for(var i=1;i+1<points.Length;i++)normal+=Vec3.Cross(points[i]-origin,points[i+1]-origin);
        var length=Math.Sqrt(normal.X*normal.X+normal.Y*normal.Y+normal.Z*normal.Z);if(length==0)throw new ModelException(format+" polygon has no usable area.");normal*=1/length;
        if(points.Any(p=>Math.Abs((p.X-origin.X)*normal.X+(p.Y-origin.Y)*normal.Y+(p.Z-origin.Z)*normal.Z)>.001))throw new ModelException(format+" polygon must be planar within 0.001 mm.");
        var axis=Math.Abs(normal.X)>=Math.Abs(normal.Y)&&Math.Abs(normal.X)>=Math.Abs(normal.Z)?0:Math.Abs(normal.Y)>=Math.Abs(normal.Z)?1:2;
        var p=points.Select(v=>axis==0?new Vec2(v.Y,v.Z):axis==1?new Vec2(v.Z,v.X):new Vec2(v.X,v.Y)).ToArray();
        static double Cross(Vec2 a,Vec2 b,Vec2 c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        static bool On(Vec2 a,Vec2 b,Vec2 q)=>Cross(a,b,q)==0&&q.X>=Math.Min(a.X,b.X)&&q.X<=Math.Max(a.X,b.X)&&q.Y>=Math.Min(a.Y,b.Y)&&q.Y<=Math.Max(a.Y,b.Y);
        for(var i=0;i<p.Length;i++)for(var j=i+1;j<p.Length;j++)
        {
            if(j==i+1||i==0&&j==p.Length-1)continue;var a=p[i];var b=p[(i+1)%p.Length];var c=p[j];var d=p[(j+1)%p.Length];
            if(On(a,b,c)||On(a,b,d)||On(c,d,a)||On(c,d,b)||Math.Sign(Cross(a,b,c))!=Math.Sign(Cross(a,b,d))&&Math.Sign(Cross(c,d,a))!=Math.Sign(Cross(c,d,b)))throw new ModelException(format+" polygons must be simple and non-touching.");
        }
        var area=Enumerable.Range(0,p.Length).Sum(i=>p[i].X*p[(i+1)%p.Length].Y-p[i].Y*p[(i+1)%p.Length].X);if(area==0)throw new ModelException(format+" polygon has zero area.");var sign=Math.Sign(area);var remaining=Enumerable.Range(0,p.Length).ToList();
        while(remaining.Count>3)
        {
            var clipped=false;
            for(var i=0;i<remaining.Count;i++)
            {
                var a=remaining[(i+remaining.Count-1)%remaining.Count];var b=remaining[i];var c=remaining[(i+1)%remaining.Count];if(sign*Cross(p[a],p[b],p[c])<=0)continue;
                if(remaining.Any(q=>q!=a&&q!=b&&q!=c&&sign*Cross(p[a],p[b],p[q])>=0&&sign*Cross(p[b],p[c],p[q])>=0&&sign*Cross(p[c],p[a],p[q])>=0))continue;
                yield return new(indices[a],indices[b],indices[c]);remaining.RemoveAt(i);clipped=true;break;
            }
            if(!clipped)throw new ModelException(format+" polygon cannot be triangulated safely. Remove collinear or touching vertices.");
        }
        if(sign*Cross(p[remaining[0]],p[remaining[1]],p[remaining[2]])<=0)throw new ModelException(format+" polygon contains a degenerate final triangle.");yield return new(indices[remaining[0]],indices[remaining[1]],indices[remaining[2]]);
    }
}
