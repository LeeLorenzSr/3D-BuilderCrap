using Builder.Core;
using System.Globalization;
using System.Text.Json;

internal static class RepairChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/repair-20261018/checks"); Directory.CreateDirectory(output);
        var cases=new List<string>(); var measurements=new List<object>();
        void Pass(string name) { cases.Add(name); Console.WriteLine("PASS "+name); }
        string Fixture(string name)=>Path.Combine(root,"fixtures/generated",name+".stl");
        async Task<EditorSession> Open(string file) { var s=new EditorSession(); Ok(await s.OpenAsync(file)); return s; }
        foreach(var (name,degenerate,duplicate,boundary,inward,removed,flipped,holes) in new[] {
            ("missing-top",0,0,4,0,0,0,1),("inverted-cube",0,0,0,1,0,12,0),
            ("duplicate-face",0,1,0,0,1,0,0),("degenerate-face",1,0,0,0,1,0,0) })
        {
            var s=await Open(Fixture(name)); var before=s.Snapshot; var id=before.SelectedObjectId!.Value;
            var diagnosis=await s.DiagnoseAsync(id,expectedRevision:before.Revision); Ok(diagnosis.Result);
            Check(s.Snapshot==before,"Diagnosis mutated accepted state."); var a=diagnosis.Diagnosis!.Assessment;
            Check(a.ZeroAreaFaces==degenerate && a.DuplicateFaces==duplicate && a.BoundaryEdges==boundary && a.InwardShells==inward,"Unexpected diagnosis: "+name);
            Ok(await s.PreviewAsync(new RepairRequest(id))); var p=s.Snapshot.Preview!; var comparison=p.Repair!;
            Same(s,before); Check(p.Scene.Objects.Single().Id==id,"Repair changed object identity.");
            Check(comparison.RemovedFaces==removed && comparison.ReorientedFaces==flipped && comparison.FilledHoles==holes,"Wrong repair counts.");
            Check(comparison.After.BoundaryEdges==0 && comparison.After.WindingConflicts==0 && comparison.After.Triangles==12,"Incomplete repair.");
            Near(comparison.After.SignedVolumeMm3!.Value,8000); Check(comparison.Before.Bounds==comparison.After.Bounds,"Dimensions changed.");
            var originalVertices=before.Scene.Objects[0].Mesh.Vertices.ToHashSet();
            Check(p.Scene.Objects[0].Mesh.Vertices.All(originalVertices.Contains),"A retained vertex moved.");
            Check(s.Undo().Outcome==Outcome.Conflict,"Undo escaped preview guard.");
            Check((await s.SaveAsync(Path.Combine(output,"unaccepted.3mf"))).Outcome==Outcome.Conflict,"Saved unaccepted repair.");
            Check(s.ApplyPreview(p.Id,before.Revision-1).Outcome==Outcome.Conflict,"Applied stale repair.");
            Check(s.DiscardPreview(p.Id).Outcome==Outcome.Cancelled,"Discard failed."); Check(s.Snapshot==before,"Discard changed state.");
            Ok(await s.PreviewAsync(new RepairRequest(id))); p=s.Snapshot.Preview!; Ok(s.ApplyPreview(p.Id));
            Check(s.Snapshot.Revision==before.Revision+1,"More than one commit.");
            var file=Path.Combine(output,name+"-repaired.3mf"); Ok(await s.SaveAsync(file)); Ok(await s.ExportStlAsync(Path.Combine(output,name+"-repaired.stl")));
            var accepted=s.Snapshot; Ok(s.Undo()); Check(s.Snapshot.Scene==before.Scene,"Undo did not restore original mesh.");
            Ok(s.Redo()); Check(s.Snapshot.Scene==accepted.Scene && !s.Snapshot.IsDirty,"Redo lost accepted scene or save boundary.");
            var noOp=s.Snapshot; Ok(await s.PreviewAsync(new RepairRequest(id))); Check(s.Snapshot==noOp,"Second repair was not a no-op.");
            var reopened=await Open(file); Ok(await reopened.PreviewAsync(new SplitRequest(reopened.Snapshot.SelectedObjectId!.Value,new(0,0,1),10)));
            Check(reopened.Snapshot.Preview!.Scene.Objects.Length==2,"Repaired mesh cannot split.");
            measurements.Add(new {name,diagnosis=a,repair=comparison});
        }
        Pass("Four damaged fixtures: diagnosis counts, exact dimensions/vertices, volume, preview/discard/apply, undo/redo, save/reopen, idempotence and subsequent split");

        foreach(var name in new[]{"cube-20mm","separate-cubes"})
        {
            var s=await Open(Fixture(name)); var before=s.Snapshot;
            Ok(await s.PreviewAsync(new RepairRequest(before.SelectedObjectId!.Value))); Check(s.Snapshot==before,"Valid mesh created a repair/history entry.");
        }
        var overlap=await Open(Fixture("overlapping-cubes")); var overlapBefore=overlap.Snapshot;
        var od=await overlap.DiagnoseAsync(overlapBefore.SelectedObjectId!.Value); Ok(od.Result);
        Check(od.Diagnosis!.Assessment.OverlappingShellBounds==1,"Overlap bounds not flagged.");
        Check((await overlap.PreviewAsync(new RepairRequest(overlapBefore.SelectedObjectId!.Value))).Outcome==Outcome.Invalid,"Overlapping shells were silently merged.");
        Check(overlap.Snapshot==overlapBefore,"Overlap failure changed document.");
        Pass("Valid meshes are no-ops; overlapping shell bounds are reported and repair refuses to guess");

        foreach(var stage in new[]{"Checking mesh","Orienting faces","Filling planar holes","Preparing geometry","Calculating geometry","Reading geometry result","Ready to preview"})
        {
            var s=await Open(Fixture("missing-top")); Ok(s.Execute(new AddCube())); Ok(s.Undo()); var before=s.Snapshot;
            using var ct=new CancellationTokenSource(); var reached=false;
            var result=await s.PreviewAsync(new RepairRequest(before.SelectedObjectId!.Value),ct.Token,new InlineProgress(p=>{if(p.Stage==stage){reached=true;ct.Cancel();}}));
            Check(reached && result.Outcome==Outcome.Cancelled,"Cancellation stage failed: "+stage); Check(s.Snapshot==before,"Cancellation changed scene/history.");
            Ok(s.Redo()); Check(s.Snapshot.Scene.Objects.Length==2,"Cancellation lost redo branch.");
        }
        var cancelled=await Open(Fixture("missing-top")); var cb=cancelled.Snapshot;
        using(var ct=new CancellationTokenSource())
        {
            ct.Cancel(); Check((await cancelled.PreviewAsync(new RepairRequest(cb.SelectedObjectId!.Value),ct.Token)).Outcome==Outcome.Cancelled,"Pre-cancellation failed.");
            Check(cancelled.Snapshot==cb,"Pre-cancellation changed state.");
        }
        using(var ct=new CancellationTokenSource())
        {
            var result=await cancelled.DiagnoseAsync(cb.SelectedObjectId!.Value,ct.Token,new InlineProgress(p=>{if(p.Stage=="Diagnosis complete") ct.Cancel();}));
            Check(result.Result.Outcome==Outcome.Cancelled && result.Diagnosis==null && cancelled.Snapshot==cb,"Cancelled diagnosis leaked a report or mutation.");
        }
        using(var entered=new ManualResetEventSlim()) using(var release=new ManualResetEventSlim()) using(var ct=new CancellationTokenSource())
        {
            var task=cancelled.PreviewAsync(new RepairRequest(cb.SelectedObjectId!.Value),ct.Token,new InlineProgress(p=>{if(p.Stage=="Checking mesh"){entered.Set();release.Wait();}}));
            Check(entered.Wait(TimeSpan.FromSeconds(10)),"Job failed to start.");
            try { Check(cancelled.Execute(new AddCube()).Outcome==Outcome.Busy,"Edit escaped busy guard."); Check((await cancelled.DiagnoseAsync(cb.SelectedObjectId!.Value)).Result.Outcome==Outcome.Busy,"Concurrent diagnosis escaped lease."); ct.Cancel(); }
            finally { release.Set(); }
            Check((await task).Outcome==Outcome.Cancelled && cancelled.Snapshot==cb,"Busy cancellation mutated state.");
        }
        Pass("Cancellation before/during cleanup, capping, kernel validation and publication; diagnosis cancellation; busy lease and redo preservation");

        var cube=Mesh.Cube; var mixedFaces=cube.Triangles.ToArray(); mixedFaces[3]=new(mixedFaces[3].A,mixedFaces[3].C,mixedFaces[3].B);
        var mixed=await Open(Write("mixed-winding",cube.Vertices,mixedFaces));
        Ok(await mixed.PreviewAsync(new RepairRequest(mixed.Snapshot.SelectedObjectId!.Value)));
        Check(mixed.Snapshot.Preview!.Repair!.ReorientedFaces==1,"Mixed winding fix flipped the wrong faces.");
        Ok(mixed.ApplyPreview(mixed.Snapshot.Preview.Id)); Ok(await mixed.SaveAsync(Path.Combine(output,"mixed-winding-repaired.3mf")));
        var nonplanarVertices=cube.Vertices.ToArray(); nonplanarVertices[6]+=new Vec3(0,0,2);
        var openFaces=cube.Triangles.Where(t=>!(cube.Vertices[t.A].Z==20 && cube.Vertices[t.B].Z==20 && cube.Vertices[t.C].Z==20)).ToArray();
        var nonplanar=await Open(Write("nonplanar-hole",nonplanarVertices,openFaces)); await Refused(nonplanar,"Nonplanar hole");
        var nm=await Open(Write("nonmanifold-edge",cube.Vertices.Append(new Vec3(10,-10,0)),cube.Triangles.Append(new Triangle(0,1,8)))); await Refused(nm,"Nonmanifold edge");
        var bow=await Open(Write("bowtie-vertex",cube.Vertices.Concat(cube.Vertices.Select(v=>v+new Vec3(20,20,20))),cube.Triangles.Concat(cube.Triangles.Select(t=>new Triangle(t.A+8,t.B+8,t.C+8))))); await Refused(bow,"Bowtie vertex");
        var nested=await Open(Write("nested-shells",cube.Vertices.Concat(cube.Vertices.Select(v=>v*.5+new Vec3(5,5,5))),cube.Triangles.Concat(cube.Triangles.Select(t=>new Triangle(t.A+8,t.C+8,t.B+8))))); await Refused(nested,"Nested shells");
        var empty=await Open(Write("all-degenerate",[new(0,0,0),new(1,0,0),new(2,0,0)],[new(0,1,2)])); await Refused(empty,"All degenerate");
        Pass("Mixed winding repaired; nonplanar holes, nonmanifold edge, bowtie vertex, nested shells and empty cleanup fail without mutation");

        Vec3[] outline=[new(0,0,0),new(20,0,0),new(20,10,0),new(10,10,0),new(10,20,0),new(0,20,0)];
        var prismVertices=outline.Concat(outline.Select(v=>v+new Vec3(0,0,20))).ToArray();
        var prismFaces=new List<Triangle>{new(0,2,1),new(0,3,2),new(0,5,3),new(3,5,4)};
        for(var i=0;i<6;i++) { var j=(i+1)%6; prismFaces.Add(new(i,j,j+6)); prismFaces.Add(new(i,j+6,i+6)); }
        var concave=await Open(Write("concave-planar-hole",prismVertices,prismFaces));
        Ok(await concave.PreviewAsync(new RepairRequest(concave.Snapshot.SelectedObjectId!.Value)));
        var cp=concave.Snapshot.Preview!; Check(cp.Repair!.FilledHoles==1 && cp.Repair.AddedFaces==4,"Concave cap did not triangulate correctly.");
        Near(cp.Repair.After.SignedVolumeMm3!.Value,6000); Ok(concave.ApplyPreview(cp.Id)); Ok(await concave.SaveAsync(Path.Combine(output,"concave-repaired.3mf")));
        Vec3[] ring=[new(0,0,0),new(20,0,0),new(20,20,0),new(0,20,0),new(5,5,0),new(15,5,0),new(15,15,0),new(5,15,0)];
        var ringFaces=new List<Triangle>(); for(var i=0;i<4;i++){var j=(i+1)%4;ringFaces.Add(new(i,j,j+4));ringFaces.Add(new(i,j+4,i+4));}
        var nestedHoles=await Open(Write("nested-hole-boundaries",ring,ringFaces)); await Refused(nestedHoles,"Nested hole boundaries");
        var manyPoints=new List<Vec3>(); var manyFaces=new List<Triangle>();
        for(var i=0;i<1025;i++) {var n=manyPoints.Count; manyPoints.AddRange([new(i*3,0,0),new(i*3+1,0,0),new(i*3,1,0)]);manyFaces.Add(new(n,n+1,n+2));}
        var many=await Open(Write("too-many-components",manyPoints,manyFaces)); var mb=many.Snapshot;
        Check((await many.DiagnoseAsync(mb.SelectedObjectId!.Value)).Result.Outcome==Outcome.Invalid && many.Snapshot==mb,"Component budget failed.");
        var partialClean=await Open(Write("open-with-duplicate",cube.Vertices,openFaces.Append(openFaces[0])));
        Ok(await partialClean.PreviewAsync(new RepairRequest(partialClean.Snapshot.SelectedObjectId!.Value,false)));
        Check(partialClean.Snapshot.Preview!.Repair!.After.BoundaryEdges==4 && partialClean.Snapshot.Preview.Repair.RemovedFaces==1,"Partial cleanup concealed remaining hole.");
        Pass("Concave planar cap has analytic 6000 mm³ volume; nested boundaries and component budget rejected; partial cleanup reports remaining open edges");

        var partial=await Open(Fixture("missing-top")); var partialBefore=partial.Snapshot;
        Ok(await partial.PreviewAsync(new RepairRequest(partialBefore.SelectedObjectId!.Value,false))); Check(partial.Snapshot==partialBefore,"Disabled capping changed a clean open mesh.");
        // Two shared-resource instances: only the requested instance is repaired, in world coordinates.
        var assemblyPath=Path.Combine(output,"damaged-assembly.3mf"); File.Copy(Path.Combine(root,"fixtures/generated/assembly-mm.3mf"),assemblyPath,true);
        using(var zip=System.IO.Compression.ZipFile.Open(assemblyPath,System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry=zip.GetEntry("3D/3dmodel.model")!; System.Xml.Linq.XDocument xml;
            using(var stream=entry.Open()) xml=System.Xml.Linq.XDocument.Load(stream);
            var ns=xml.Root!.Name.Namespace; var triangle=xml.Descendants(ns+"triangle").First(); triangle.Parent!.Add(new System.Xml.Linq.XElement(triangle));
            entry.Delete(); using var write=zip.CreateEntry("3D/3dmodel.model").Open(); xml.Save(write);
        }
        var assembly=await Open(assemblyPath); var target=assembly.Snapshot.Scene.Objects[1]; var untouched=assembly.Snapshot.Scene.Objects[0];
        Ok(assembly.Execute(new ResizeObject(target.Id,new(30,20,20)))); var ab=assembly.Snapshot;
        Ok(await assembly.PreviewAsync(new RepairRequest(target.Id))); var ap=assembly.Snapshot.Preview!;
        Check(ap.Scene.Objects[0]==untouched && ap.Scene.Objects[1].Id==target.Id && ap.Scene.Objects[1].Bounds==ab.Scene.Objects[1].Bounds,"Repair affected another instance, identity or transform.");
        Near(ap.Repair!.After.SignedVolumeMm3!.Value,12000); Ok(assembly.ApplyPreview(ap.Id)); Ok(assembly.Undo()); Check(assembly.Snapshot.Scene==ab.Scene,"Undo lost instance transform.");
        foreach(var worker in new[]{Path.Combine(output,"absent-worker.exe"),Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/checks/Builder.WorkerFaultProbe.exe")})
        {
            var s=new EditorSession(worker); Ok(await s.OpenAsync(Fixture("missing-top"))); var before=s.Snapshot;
            Check(!(await s.PreviewAsync(new RepairRequest(before.SelectedObjectId!.Value))).Succeeded,"Broken worker accepted repair."); Check(s.Snapshot==before,"Worker failure mutated scene.");
        }
        Pass("Disabled hole filling; transformed shared-resource instance isolation and undo; missing worker and missing response safety");
        await RepairReviewChecks.Run(root);
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,runtime=Environment.Version.ToString(),cases,measurements},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} repair scenario groups passed. Evidence: {output}");

        string Write(string name,IEnumerable<Vec3> points,IEnumerable<Triangle> faces)
        {
            var vertices=points.ToArray(); var path=Path.Combine(output,name+"-input.stl");
            using var writer=new StreamWriter(path); writer.WriteLine("solid test");
            foreach(var t in faces)
            {
                writer.WriteLine("facet normal 0 0 0\nouter loop");
                foreach(var i in new[]{t.A,t.B,t.C}) { var v=vertices[i]; writer.WriteLine(FormattableString.Invariant($"vertex {v.X:R} {v.Y:R} {v.Z:R}")); }
                writer.WriteLine("endloop\nendfacet");
            }
            writer.WriteLine("endsolid test"); return path;
        }
        async Task Refused(EditorSession s,string label)
        { var before=s.Snapshot; Check((await s.PreviewAsync(new RepairRequest(before.SelectedObjectId!.Value))).Outcome==Outcome.Invalid,label+" was accepted."); Check(s.Snapshot==before,label+" changed state."); }
    }
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.Message);
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-7,$"Expected {b}, got {a}.");
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot with {Preview=null,IsBusy=false}==before,"Preview changed accepted state.");
}
