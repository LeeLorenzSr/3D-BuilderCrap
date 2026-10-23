using System.Collections.Immutable;

namespace Builder.Core;

/// <summary>Conservative repair in world millimeters. Existing vertex positions never move.</summary>
public sealed record RepairRequest(Guid ObjectId, bool FillPlanarHoles = true) : GeometryRequest;
internal sealed record ValidateSolidRequest : GeometryRequest;
/// <summary>Topology counts use exact position welding and ignore duplicate/zero-area faces.
/// Shell-bound overlap is only a warning, not an intersection test. No self-intersection test is performed.</summary>
public sealed record MeshAssessment(int Triangles, int UniqueVertices, int ZeroAreaFaces, int DuplicateFaces,
    int BoundaryEdges, int NonManifoldEdges, int NonManifoldVertices, int WindingConflicts,
    int Components, int InwardShells, int OverlappingShellBounds, bool Orientable,
    Bounds3 Bounds, double? SignedVolumeMm3);
public sealed record ObjectDiagnosis(Guid ObjectId, long Revision, MeshAssessment Assessment);
public sealed record DiagnosisResult(OperationResult Result, ObjectDiagnosis? Diagnosis);
public sealed record RepairComparison(MeshAssessment Before, MeshAssessment After,
    int RemovedFaces, int ReorientedFaces, int FilledHoles, int AddedFaces);

public sealed partial class EditorSession
{
    /// <summary>A cancellable read under the session job lease; does not change scene, selection or history.
    /// Read-only diagnosis may inspect any explicit leaf regardless of the active member-editing scope.</summary>
    public async Task<DiagnosisResult> DiagnoseAsync(Guid objectId, CancellationToken cancellation = default,
        IProgress<OperationProgress>? progress = null, long? expectedRevision = null)
    {
        ObjectDiagnosis? diagnosis = null;
        var result = await RunIo(() => {
            var captured = Snapshot;
            var obj = captured.Scene.Objects.FirstOrDefault(o => o.Id == objectId)
                ?? throw new ModelException("Choose an object to check.");
            var assessment = MeshRepair.Diagnose(obj,cancellation,progress);
            progress?.Report(new("Diagnosis complete")); cancellation.ThrowIfCancellationRequested();
            diagnosis = new(objectId,captured.Revision,assessment);
            return "Mesh checks complete. Self-intersections have not been checked.";
        },expectedRevision).ConfigureAwait(false);
        return new(result,result.Succeeded ? diagnosis : null);
    }

    private string PrepareRepair(RepairRequest request, EditorSnapshot captured, CancellationToken cancellation,
        IProgress<OperationProgress>? progress)
    {
        var obj = captured.Scene.Objects.FirstOrDefault(o => o.Id == request.ObjectId)
            ?? throw new ModelException("Choose an object to repair.");
        SurfacePainting.RequireUnpainted(obj,"Repair");
        _=captured.Scene.ExpandObjectIds([obj.Id],captured.EditingGroupId);
        if(captured.EditingGroupId is null && captured.Scene.IsGrouped(obj.Id)) throw new ModelException("Ungroup this object before repairing it. Diagnosis remains available.");
        var (mesh,comparison) = MeshRepair.Prepare(obj,request.FillPlanarHoles,cancellation,progress);
        if(mesh == null) return "No changes needed for these repair options. Self-intersections have not been checked.";
        var repaired = obj with {Mesh=mesh,Transform=Affine.Identity};
        // Validate the closed candidate with the same kernel used by split/boolean tools. Keep our
        // original candidate, not the kernel's retessellation, to preserve existing coordinates.
        if(comparison.After.BoundaryEdges == 0)
            geometry.Calculate(new ValidateSolidRequest(),[repaired],cancellation,progress);
        var candidate = captured.Scene.WithObjects(captured.Scene.Objects.Select(o => o.Id == obj.Id ? repaired : o).ToImmutableArray());
        candidate.Validate(cancellation);
        var summary = $"Removed {comparison.RemovedFaces} faces, reoriented {comparison.ReorientedFaces}, filled {comparison.FilledHoles} holes. " +
            (comparison.After.BoundaryEdges == 0 ? "Existing vertices did not move. Review before applying." : "Open edges remain. Review before applying.");
        progress?.Report(new("Ready to preview")); cancellation.ThrowIfCancellationRequested();
        lock(gate) preview = new(Guid.NewGuid(),captured.Revision,request,candidate,obj.Id,[obj.Id],summary,comparison)
            {SelectedObjectIds=candidate.ExpandObjectIds(captured.SelectedObjectIds.Contains(obj.Id)
                ? captured.SelectedObjectIds : captured.SelectedObjectIds.Add(obj.Id),captured.EditingGroupId),EditingGroupId=captured.EditingGroupId};
        return summary;
    }
}

/// <summary>Original bounded cleanup and planar capping. This is not a general solid-reconstruction engine.</summary>
internal static class MeshRepair
{
    private readonly record struct EdgeUse(int Face, int From, int To);
    private sealed class Topology
    {
        public required Dictionary<(int,int),List<EdgeUse>> Edges;
        public required List<(int Face,bool Flip)>[] Neighbors;
        public required List<int[]> Components;
        public required bool[] Flips;
        public required int[] ComponentOf;
        public bool[] ClosedComponents = [];
        public bool Orientable = true;
        public int NonManifoldVertices;
    }
    private sealed record Input(Vec3[] Vertices, Triangle[] Faces, int OriginalFaces, int Degenerate, int Duplicates);
    public static MeshAssessment Diagnose(SceneObject obj,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var input = Read(obj,ct,progress);
        return Assess(input,Build(input.Vertices,input.Faces,ct),obj.Bounds,ct);
    }
    public static (Mesh? Mesh,RepairComparison Comparison) Prepare(SceneObject obj,bool fillHoles,CancellationToken ct,
        IProgress<OperationProgress>? progress)
    {
        var input = Read(obj,ct,progress); var topology = Build(input.Vertices,input.Faces,ct);
        var before = Assess(input,topology,obj.Bounds,ct);
        if(input.Faces.Length == 0) throw new ModelException("No nonzero-area faces remain. This object cannot be repaired.");
        if(before.NonManifoldEdges != 0 || before.NonManifoldVertices != 0 || !before.Orientable)
            throw new ModelException("This mesh has branching or non-manifold topology. This repair pass cannot safely choose a solid.");
        if(before.OverlappingShellBounds != 0)
            throw new ModelException("Shell bounds overlap or nest. Review the shells separately; this repair pass does not resolve intersections or cavities.");
        progress?.Report(new("Orienting faces")); ct.ThrowIfCancellationRequested();
        var faces = input.Faces.Select((t,i) => topology.Flips[i] ? Reverse(t) : t).ToList();
        var originalCount = faces.Count; var holes = 0;
        if(fillHoles && before.BoundaryEdges != 0)
        {
            progress?.Report(new("Filling planar holes")); ct.ThrowIfCancellationRequested();
            var oriented = Build(input.Vertices,faces.ToArray(),ct);
            var loops=BoundaryLoops(oriented,ct);
            var loopBounds=loops.Select(loop=>Bounds3.From(loop.Select(i=>input.Vertices[i]))).ToArray();
            for(var i=0;i<loops.Count;i++) for(var j=0;j<i;j++)
                if(Overlaps(loopBounds[i],loopBounds[j])) throw new ModelException("Hole boundaries overlap or nest. This repair pass cannot safely cap them separately.");
            foreach(var loop in loops)
            {
                if(++holes > 64) throw new ModelException("Repair is limited to 64 simple planar holes per object.");
                // The cap follows the reverse of the existing boundary.
                loop.Reverse(); faces.AddRange(Cap(input.Vertices,loop,ct));
                if(faces.Count > ModelLimits.MaxTriangles) throw new ModelException("Repair exceeds the triangle limit.");
            }
        }
        var finalTopology = Build(input.Vertices,faces.ToArray(),ct);
        foreach(var component in finalTopology.Components)
        {
            ct.ThrowIfCancellationRequested();
            if(!IsClosed(component,finalTopology)) continue;
            var volume = Volume(input.Vertices,faces,component);
            if(volume == 0 || !double.IsFinite(volume)) throw new ModelException("A closed shell has no usable volume.");
            if(volume < 0) foreach(var index in component) faces[index] = Reverse(faces[index]);
        }
        var flipped = Enumerable.Range(0,originalCount).Count(i => faces[i] != input.Faces[i]);
        var changed = input.Degenerate+input.Duplicates+flipped+holes != 0;
        if(!changed)
        {
            ct.ThrowIfCancellationRequested();
            return (null,new(before,before,0,0,0,0));
        }
        // Drop vertices unused after cleanup, without moving retained vertices.
        var used = new Dictionary<int,int>(); var vertices = new List<Vec3>();
        int Remap(int i) { if(!used.TryGetValue(i,out var index)) { index=vertices.Count; used.Add(i,index); vertices.Add(input.Vertices[i]); } return index; }
        var remapped = new List<Triangle>();
        foreach(var t in faces) { ct.ThrowIfCancellationRequested(); remapped.Add(new(Remap(t.A),Remap(t.B),Remap(t.C))); }
        var candidate = new Mesh(vertices,remapped);
        var afterInput = new Input(vertices.ToArray(),remapped.ToArray(),faces.Count,0,0);
        var after = Assess(afterInput,Build(afterInput.Vertices,afterInput.Faces,ct),Bounds3.From(candidate.Vertices),ct);
        if(after.NonManifoldEdges != 0 || after.NonManifoldVertices != 0 || after.WindingConflicts != 0 ||
            after.InwardShells != 0 || !after.Orientable || (fillHoles && after.BoundaryEdges != 0))
            throw new ModelException("The repair did not produce the expected topology. Your scene is unchanged.");
        return (candidate,new(before,after,input.Degenerate+input.Duplicates,flipped,holes,faces.Count-originalCount));
    }
    private static Input Read(SceneObject obj,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        progress?.Report(new("Checking mesh")); ct.ThrowIfCancellationRequested();
        var vertices = new List<Vec3>(); var map = new Dictionary<Vec3,int>(); var remap = new int[obj.Mesh.Vertices.Length];
        for(var i=0;i<remap.Length;i++)
        {
            ct.ThrowIfCancellationRequested(); var v = obj.Transform.Apply(obj.Mesh.Vertices[i]);
            if(!map.TryGetValue(v,out var index)) { index=vertices.Count; map.Add(v,index); vertices.Add(v); }
            remap[i] = index;
        }
        var faces = new List<Triangle>(); var seen = new HashSet<(int,int,int)>(); int degenerate=0,duplicates=0;
        var reflected = ModelFiles.Determinant(obj.Transform) < 0;
        foreach(var raw in obj.Mesh.Triangles)
        {
            ct.ThrowIfCancellationRequested(); var t = new Triangle(remap[raw.A],remap[raw.B],remap[raw.C]);
            if(reflected) t=Reverse(t);
            if(Vec3.Cross(vertices[t.B]-vertices[t.A],vertices[t.C]-vertices[t.A]) == new Vec3()) { degenerate++; continue; }
            int a=t.A,b=t.B,c=t.C; if(a>b) (a,b)=(b,a); if(b>c) (b,c)=(c,b); if(a>b) (a,b)=(b,a);
            if(!seen.Add((a,b,c))) { duplicates++; continue; }
            faces.Add(t);
        }
        return new(vertices.ToArray(),faces.ToArray(),obj.Mesh.Triangles.Length,degenerate,duplicates);
    }
    private static Topology Build(Vec3[] vertices,Triangle[] faces,CancellationToken ct)
    {
        var edges = new Dictionary<(int,int),List<EdgeUse>>();
        var incident = new List<int>[vertices.Length]; for(var i=0;i<incident.Length;i++) incident[i]=[];
        var neighbors = new List<(int Face,bool Flip)>[faces.Length];
        for(var i=0;i<faces.Length;i++)
        {
            ct.ThrowIfCancellationRequested(); neighbors[i]=[]; var t=faces[i];
            foreach(var v in new[]{t.A,t.B,t.C}) incident[v].Add(i);
            foreach(var (a,b) in new[]{(t.A,t.B),(t.B,t.C),(t.C,t.A)})
            { var key=(Math.Min(a,b),Math.Max(a,b)); if(!edges.TryGetValue(key,out var uses)) edges[key]=uses=[]; uses.Add(new(i,a,b)); }
        }
        foreach(var uses in edges.Values)
        {
            ct.ThrowIfCancellationRequested();
            for(var j=1;j<uses.Count;j++)
            { var a=uses[0]; var b=uses[j]; var same=a.From==b.From; neighbors[a.Face].Add((b.Face,same)); neighbors[b.Face].Add((a.Face,same)); }
        }
        var topology = new Topology {Edges=edges,Neighbors=neighbors,Components=[],Flips=new bool[faces.Length],ComponentOf=Enumerable.Repeat(-1,faces.Length).ToArray()};
        for(var start=0;start<faces.Length;start++)
        {
            ct.ThrowIfCancellationRequested(); if(topology.ComponentOf[start]>=0) continue;
            var group=new List<int>(); var queue=new Queue<int>(); queue.Enqueue(start); topology.ComponentOf[start]=topology.Components.Count;
            while(queue.TryDequeue(out var f))
            {
                ct.ThrowIfCancellationRequested(); group.Add(f);
                foreach(var (other,flip) in neighbors[f])
                {
                    var needed=topology.Flips[f]^flip;
                    if(topology.ComponentOf[other]<0) { topology.ComponentOf[other]=topology.Components.Count; topology.Flips[other]=needed; queue.Enqueue(other); }
                    else if(topology.Flips[other]!=needed) topology.Orientable=false;
                }
            }
            topology.Components.Add(group.ToArray());
            if(topology.Components.Count>1024) throw new ModelException("Diagnosis is limited to 1,024 connected components per object.");
        }
        topology.ClosedComponents=Enumerable.Repeat(true,topology.Components.Count).ToArray();
        foreach(var uses in edges.Values) if(uses.Count!=2) topology.ClosedComponents[topology.ComponentOf[uses[0].Face]]=false;
        // A manifold vertex has a single connected face fan. Edge checks alone miss bow ties.
        foreach(var fan in incident)
        {
            ct.ThrowIfCancellationRequested(); if(fan.Count==0) continue;
            var remaining=fan.ToHashSet(); var queue=new Queue<int>(); queue.Enqueue(fan[0]); remaining.Remove(fan[0]);
            while(queue.TryDequeue(out var f)) foreach(var (other,_) in neighbors[f]) if(remaining.Remove(other)) queue.Enqueue(other);
            if(remaining.Count!=0) topology.NonManifoldVertices++;
        }
        return topology;
    }
    private static MeshAssessment Assess(Input input,Topology t,Bounds3 bounds,CancellationToken ct)
    {
        var boundary=t.Edges.Values.Count(e=>e.Count==1); var nonmanifold=t.Edges.Values.Count(e=>e.Count>2);
        var winding=t.Edges.Values.Count(e=>e.Count==2 && e[0].From==e[1].From);
        // An inconsistent component cannot be classified as inward, but it must not
        // conceal an independently consistent inward shell elsewhere in the object.
        var inconsistentComponents=new bool[t.Components.Count];
        foreach(var edge in t.Edges.Values)
        {
            ct.ThrowIfCancellationRequested();
            if(edge.Count==2 && edge[0].From==edge[1].From) inconsistentComponents[t.ComponentOf[edge[0].Face]]=true;
        }
        var componentBounds=new List<Bounds3>(); int inward=0,overlap=0; double volume=0;
        foreach(var component in t.Components)
        {
            ct.ThrowIfCancellationRequested();
            componentBounds.Add(Bounds3.From(component.SelectMany(i=>new[]{input.Faces[i].A,input.Faces[i].B,input.Faces[i].C}).Select(i=>input.Vertices[i])));
            if(IsClosed(component,t))
            {
                var v=Volume(input.Vertices,input.Faces,component); volume+=v;
                if(v<0 && !inconsistentComponents[t.ComponentOf[component[0]]]) inward++;
            }
        }
        // Bound the quadratic shell comparison independently of the triangle budget.
        if(componentBounds.Count>1024) throw new ModelException("Diagnosis is limited to 1,024 connected components per object.");
        for(var i=0;i<componentBounds.Count;i++) for(var j=0;j<i;j++)
        {
            ct.ThrowIfCancellationRequested(); var a=componentBounds[i]; var b=componentBounds[j];
            if(Overlaps(a,b)) overlap++;
        }
        return new(input.OriginalFaces,input.Vertices.Length,input.Degenerate,input.Duplicates,boundary,nonmanifold,
            t.NonManifoldVertices,winding,t.Components.Count,inward,overlap,t.Orientable,bounds,
            boundary==0 && nonmanifold==0 && t.NonManifoldVertices==0 && winding==0 && overlap==0 && input.Faces.Length>0 ? volume : null);
    }
    private static bool Overlaps(Bounds3 a,Bounds3 b)=>a.Min.X<=b.Max.X && b.Min.X<=a.Max.X && a.Min.Y<=b.Max.Y && b.Min.Y<=a.Max.Y && a.Min.Z<=b.Max.Z && b.Min.Z<=a.Max.Z;
    private static bool IsClosed(int[] component,Topology t)=>t.ClosedComponents[t.ComponentOf[component[0]]];
    private static double Volume(Vec3[] vertices,IReadOnlyList<Triangle> faces,int[] component)
    {
        var origin=vertices[faces[component[0]].A]; double sum=0;
        foreach(var i in component) { var f=faces[i]; sum+=Dot(vertices[f.A]-origin,Vec3.Cross(vertices[f.B]-origin,vertices[f.C]-origin)); }
        return sum/6;
    }
    private static Triangle Reverse(Triangle t)=>new(t.A,t.C,t.B);
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static List<List<int>> BoundaryLoops(Topology t,CancellationToken ct)
    {
        var next=new Dictionary<int,int>(); var incoming=new HashSet<int>();
        foreach(var edge in t.Edges.Values.Where(e=>e.Count==1).Select(e=>e[0]))
        { ct.ThrowIfCancellationRequested(); if(!next.TryAdd(edge.From,edge.To)||!incoming.Add(edge.To)) throw new ModelException("A hole boundary branches. Repair cannot safely cap it."); }
        var loops=new List<List<int>>();
        while(next.Count>0)
        {
            ct.ThrowIfCancellationRequested(); var start=next.First().Key; var current=start; var loop=new List<int>();
            do
            {
                if(loop.Count>=256 || !next.Remove(current,out var to)) throw new ModelException("Repair supports simple closed hole boundaries of at most 256 vertices.");
                loop.Add(current); current=to;
            } while(current!=start);
            loops.Add(loop);
            if(loops.Count>64) throw new ModelException("Repair is limited to 64 simple planar holes per object.");
        }
        return loops;
    }
    private readonly record struct Point(double X,double Y);
    private static double Cross(Point a,Point b,Point c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
    private static List<Triangle> Cap(Vec3[] vertices,List<int> loop,CancellationToken ct)
    {
        if(loop.Count<3) throw new ModelException("A hole has too few boundary vertices.");
        var origin=vertices[loop[0]]; var normal=new Vec3();
        for(var i=0;i<loop.Count;i++) normal+=Vec3.Cross(vertices[loop[i]]-origin,vertices[loop[(i+1)%loop.Count]]-origin);
        var length=Math.Sqrt(Dot(normal,normal));
        if(length==0) throw new ModelException("A hole boundary has no stable plane.");
        normal*=1/length;
        var size=Bounds3.From(loop.Select(i=>vertices[i])).Size;
        var scale=Math.Max(size.X,Math.Max(size.Y,size.Z)); var distanceTolerance=Math.Max(scale*1e-9,1e-10);
        if(loop.Any(i=>Math.Abs(Dot(vertices[i]-origin,normal))>distanceTolerance))
            throw new ModelException("A hole is not planar. This repair pass only fills simple planar holes.");
        var axis=Math.Abs(normal.X)>Math.Abs(normal.Y) ? (Math.Abs(normal.X)>Math.Abs(normal.Z)?0:2) : (Math.Abs(normal.Y)>Math.Abs(normal.Z)?1:2);
        Point Project(int i) { var v=vertices[i]-origin; return axis==0 ? new(v.Y,v.Z) : axis==1 ? new(v.X,v.Z) : new(v.X,v.Y); }
        var points=loop.Select(Project).ToArray(); var tolerance=scale*scale*1e-12;
        for(var i=0;i<points.Length;i++) for(var j=i+1;j<points.Length;j++)
        {
            ct.ThrowIfCancellationRequested(); var ni=(i+1)%points.Length; var nj=(j+1)%points.Length;
            if(ni==j || nj==i) continue;
            var a=Cross(points[i],points[ni],points[j]); var b=Cross(points[i],points[ni],points[nj]);
            var c=Cross(points[j],points[nj],points[i]); var d=Cross(points[j],points[nj],points[ni]);
            // Conservatively reject collinear non-adjacent edges as well as crossings.
            if(a*b<=0 && c*d<=0) throw new ModelException("A hole boundary crosses or has ambiguous collinear edges.");
        }
        double area=0; for(var i=0;i<points.Length;i++) area+=points[i].X*points[(i+1)%points.Length].Y-points[(i+1)%points.Length].X*points[i].Y;
        var sign=Math.Sign(area); if(sign==0) throw new ModelException("A hole boundary has no area.");
        var remaining=Enumerable.Range(0,loop.Count).ToList(); var result=new List<Triangle>();
        while(remaining.Count>3)
        {
            ct.ThrowIfCancellationRequested(); var found=false;
            for(var i=0;i<remaining.Count;i++)
            {
                var a=remaining[(i+remaining.Count-1)%remaining.Count]; var b=remaining[i]; var c=remaining[(i+1)%remaining.Count];
                if(sign*Cross(points[a],points[b],points[c])<=tolerance) continue;
                if(remaining.Any(p=>p!=a && p!=b && p!=c && sign*Cross(points[a],points[b],points[p])>=-tolerance &&
                    sign*Cross(points[b],points[c],points[p])>=-tolerance && sign*Cross(points[c],points[a],points[p])>=-tolerance)) continue;
                result.Add(new(loop[a],loop[b],loop[c])); remaining.RemoveAt(i); found=true; break;
            }
            if(!found) throw new ModelException("The hole cannot be capped without ambiguous or zero-area triangles.");
        }
        if(sign*Cross(points[remaining[0]],points[remaining[1]],points[remaining[2]])<=tolerance)
            throw new ModelException("The hole would create a zero-area triangle.");
        result.Add(new(loop[remaining[0]],loop[remaining[1]],loop[remaining[2]])); return result;
    }
}
