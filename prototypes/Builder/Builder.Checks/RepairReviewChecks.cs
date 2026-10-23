using Builder.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Independent repair review probes, including the advertised geometry and cap limits.</summary>
internal static class RepairReviewChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/overnight-20261018/repair-review"); Directory.CreateDirectory(output);
        var worker=Path.Combine(root,"prototypes/Builder/Builder.Desktop/bin/Debug/net10.0-windows/Builder.GeometryWorker.exe");
        var cases=new List<object>();
        async Task<EditorSession> Open(string path)
        {
            var session=new EditorSession(worker); Ok(await session.OpenAsync(path)); return session;
        }
        var cube=Mesh.Cube; var inconsistent=cube.Triangles.ToArray(); inconsistent[3]=Reverse(inconsistent[3]);
        var mixed=await Open(Write("inward-plus-inconsistent",cube.Vertices.Concat(cube.Vertices.Select(v=>v+new Vec3(40,0,0))),
            inconsistent.Concat(cube.Triangles.Select(t=>Offset(Reverse(t),8)))));
        var before=mixed.Snapshot; var diagnosis=await mixed.DiagnoseAsync(before.SelectedObjectId!.Value); Ok(diagnosis.Result);
        var assessment=diagnosis.Diagnosis!.Assessment;
        Check(assessment.Components==2 && assessment.WindingConflicts==3 && assessment.InwardShells==1 && assessment.SignedVolumeMm3==null,
            "One inconsistent shell concealed the separate, consistently inward shell.");
        Check(mixed.Snapshot==before,"Diagnosis changed accepted state.");
        Ok(await mixed.PreviewAsync(new RepairRequest(before.SelectedObjectId!.Value)));
        var preview=mixed.Snapshot.Preview!;
        Check(preview.Repair!.ReorientedFaces==13 && preview.Repair.After.InwardShells==0,"Independent shells were not oriented correctly.");
        Near(preview.Repair.After.SignedVolumeMm3!.Value,16000,1e-7);
        Ok(mixed.ApplyPreview(preview.Id)); Ok(mixed.Undo()); Check(mixed.Snapshot.Scene==before.Scene,"Repair undo lost original independent shells.");
        cases.Add(new {name="mixed-winding shell does not conceal independent inward shell",assessment,repair=preview.Repair});

        Vec3[] tetra=[new(0,0,0),new(30,0,0),new(0,20,0),new(0,0,10)];
        Triangle[] tetraFaces=[new(0,2,1),new(0,1,3),new(0,3,2),new(1,2,3)];
        var wedge=await Open(Write("inverted-tetrahedron",tetra,tetraFaces.Select(Reverse)));
        var wedgeBefore=wedge.Snapshot; Ok(await wedge.PreviewAsync(new RepairRequest(wedgeBefore.SelectedObjectId!.Value)));
        var wp=wedge.Snapshot.Preview!; Check(wp.Repair!.ReorientedFaces==4,"Tetrahedron winding repair was incomplete.");
        Near(wp.Repair.After.SignedVolumeMm3!.Value,1000,1e-8);
        Check(wp.Scene.Objects.Single().Mesh.Vertices.ToHashSet().SetEquals(tetra),"Tetrahedron repair moved or removed a retained vertex.");
        Check(wp.Repair.After.Bounds==wedgeBefore.Scene.Bounds,"Tetrahedron dimensions changed.");
        Ok(wedge.ApplyPreview(wp.Id)); Ok(await wedge.ExportStlAsync(Path.Combine(output,"tetrahedron-repaired.stl")));
        var stable=wedge.Snapshot; Ok(await wedge.PreviewAsync(new RepairRequest(stable.SelectedObjectId!.Value))); Check(wedge.Snapshot==stable,"Tetrahedron second repair was not a no-op.");
        cases.Add(new {name="non-box sharp tetrahedron: volume, vertices, bounds and idempotence",repair=wp.Repair});

        foreach(var sides in new[]{256,257})
        {
            var points=new List<Vec3>{new(0,0,0)};
            points.AddRange(Enumerable.Range(0,sides).Select(i=>new Vec3(10*Math.Cos(2*Math.PI*i/sides),10*Math.Sin(2*Math.PI*i/sides),0)));
            points.AddRange(points.Skip(1).Select(v=>v+new Vec3(0,0,20)).ToArray());
            var faces=new List<Triangle>();
            for(var i=0;i<sides;i++) { var a=1+i; var b=1+(i+1)%sides; faces.Add(new(0,b,a)); faces.Add(new(a,b,b+sides)); faces.Add(new(a,b+sides,a+sides)); }
            var session=await Open(Write($"cylinder-open-{sides}",points,faces)); var snapshot=session.Snapshot;
            var timer=Stopwatch.StartNew(); var result=await session.PreviewAsync(new RepairRequest(snapshot.SelectedObjectId!.Value)); timer.Stop();
            if(sides==257) Check(result.Outcome==Outcome.Invalid && session.Snapshot==snapshot,"An over-limit boundary was accepted or changed the scene.");
            else
            {
                Ok(result); var cap=session.Snapshot.Preview!;
                Check(cap.Repair!.FilledHoles==1 && cap.Repair.AddedFaces==254 && cap.Repair.After.BoundaryEdges==0,"256-vertex cap was incomplete.");
                Near(cap.Repair.After.SignedVolumeMm3!.Value,sides*100*Math.Sin(2*Math.PI/sides)*10,1e-7);
                Check(cap.Scene.Objects.Single().Mesh.Vertices.ToHashSet().SetEquals(points),"Cylinder cap moved or introduced a vertex.");
                Ok(session.ApplyPreview(cap.Id)); Ok(await session.ExportStlAsync(Path.Combine(output,"cylinder-256-repaired.stl")));
                cases.Add(new {name="256-vertex curved-outline planar cap",elapsedMs=timer.Elapsed.TotalMilliseconds,repair=cap.Repair});
            }
            if(sides==257) cases.Add(new {name="257-vertex boundary refuses safely",elapsedMs=timer.Elapsed.TotalMilliseconds,result});
        }

        // All 100,000 faces meet one of two high-valence tips. This stresses both topology
        // walks and cancellation without exceeding the 300,000 expanded STL vertex limit.
        const int ring=50000;
        var largePoints=new List<Vec3>{new(0,0,-10),new(0,0,10)};
        largePoints.AddRange(Enumerable.Range(0,ring).Select(i=>new Vec3(10*Math.Cos(2*Math.PI*i/ring),10*Math.Sin(2*Math.PI*i/ring),0)));
        var largeFaces=new List<Triangle>();
        for(var i=0;i<ring;i++) { var a=i+2; var b=(i+1)%ring+2; largeFaces.Add(new(0,b,a)); largeFaces.Add(new(1,a,b)); }
        var largePath=Write("bipyramid-100000",largePoints,largeFaces);
        var large=await Open(largePath); var largeBefore=large.Snapshot; var id=largeBefore.SelectedObjectId!.Value;
        var watch=Stopwatch.StartNew(); var checkedLarge=await large.DiagnoseAsync(id); watch.Stop(); Ok(checkedLarge.Result);
        var la=checkedLarge.Diagnosis!.Assessment;
        Check(la.Triangles==100000 && la.Components==1 && la.BoundaryEdges==0 && la.NonManifoldVertices==0,"Large high-valence mesh diagnosis was incorrect.");
        Near(la.SignedVolumeMm3!.Value,ring*100*Math.Sin(2*Math.PI/ring)*20/6,1e-6);
        Check(large.Snapshot==largeBefore,"Large diagnosis changed document.");
        cases.Add(new {name="100000-face high-valence diagnosis",elapsedMs=watch.Elapsed.TotalMilliseconds,assessment=la});
        using(var ct=new CancellationTokenSource())
        {
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job=large.DiagnoseAsync(id,ct.Token,new ProgressInline(p=> {if(p.Stage=="Checking mesh") started.TrySetResult();}));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(10); watch.Restart(); ct.Cancel(); var cancelled=await job; watch.Stop();
            Check(cancelled.Result.Outcome==Outcome.Cancelled && cancelled.Diagnosis==null && large.Snapshot==largeBefore,"Cancellation of an actual running large diagnosis failed.");
            cases.Add(new {name="large diagnosis asynchronous cancellation",elapsedAfterCancelMs=watch.Elapsed.TotalMilliseconds,result=cancelled.Result});
        }
        watch.Restart(); Ok(await large.PreviewAsync(new RepairRequest(id))); watch.Stop();
        Check(large.Snapshot==largeBefore,"A valid large solid created a repair or history entry.");
        cases.Add(new {name="100000-face repair no-op",elapsedMs=watch.Elapsed.TotalMilliseconds});

        largeFaces[1]=Reverse(largeFaces[1]);
        var damagedLarge=await Open(Write("bipyramid-100000-mixed",largePoints,largeFaces)); var damagedBefore=damagedLarge.Snapshot;
        watch.Restart(); Ok(await damagedLarge.PreviewAsync(new RepairRequest(damagedBefore.SelectedObjectId!.Value))); watch.Stop();
        var repairedLarge=damagedLarge.Snapshot.Preview!;
        Check(repairedLarge.Repair!.ReorientedFaces==1 && repairedLarge.Repair.After.Triangles==100000 && repairedLarge.Repair.After.BoundaryEdges==0,
            "A large winding repair did not retain the full triangle set.");
        Check(repairedLarge.Scene.Objects.Single().Mesh.Vertices.ToHashSet().SetEquals(largePoints),"Large winding repair moved or removed a retained vertex.");
        Check(repairedLarge.Repair.After.Bounds==damagedBefore.Scene.Bounds,"Large winding repair changed dimensions.");
        Ok(damagedLarge.ApplyPreview(repairedLarge.Id)); Ok(damagedLarge.Undo()); Check(damagedLarge.Snapshot.Scene==damagedBefore.Scene,"Large repair undo failed.");
        cases.Add(new {name="100000-face winding repair with native validation and exact retained vertices",elapsedMs=watch.Elapsed.TotalMilliseconds,repair=repairedLarge.Repair});

        foreach(var count in new[]{64,65,1024})
        {
            var points=new List<Vec3>(); var faces=new List<Triangle>();
            var openFaces=cube.Triangles.Where(t=>cube.Vertices[t.A].Z!=20 || cube.Vertices[t.B].Z!=20 || cube.Vertices[t.C].Z!=20).ToArray();
            IEnumerable<Triangle> sourceFaces=count==1024 ? cube.Triangles : openFaces;
            for(var i=0;i<count;i++)
            {
                points.AddRange(cube.Vertices.Select(v=>v+new Vec3(i*30,0,0)));
                faces.AddRange(sourceFaces.Select(t=>Offset(t,i*8)));
            }
            var shells=await Open(Write($"separated-shells-{count}",points,faces)); var snapshot=shells.Snapshot;
            watch.Restart(); var result=await shells.PreviewAsync(new RepairRequest(snapshot.SelectedObjectId!.Value)); watch.Stop();
            if(count==65) Check(result.Outcome==Outcome.Invalid && shells.Snapshot==snapshot,"65-hole budget did not refuse safely.");
            else
            {
                Ok(result);
                if(count==1024) Check(shells.Snapshot==snapshot,"1,024 valid shells were not a no-op.");
                else
                {
                    var caps=shells.Snapshot.Preview!; Check(caps.Repair!.FilledHoles==64 && caps.Repair.After.Components==64,"64 separate holes were not capped independently.");
                    Near(caps.Repair.After.SignedVolumeMm3!.Value,64*8000,1e-7);
                    Check(shells.DiscardPreview(caps.Id).Outcome==Outcome.Cancelled,"Discard of 64 caps failed.");
                    Check(shells.Snapshot==snapshot,"Discard of 64 caps changed accepted state.");
                }
            }
            cases.Add(new {name=$"{count} separated shells / hole-budget boundary",elapsedMs=watch.Elapsed.TotalMilliseconds,result});
        }

        var files=Directory.GetFiles(output,"*.stl").Select(p=>new {path=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,runtime=Environment.Version.ToString(),
            coreSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EditorSession).Assembly.Location))),cases,files},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS Independent repair review: {cases.Count} probes; evidence: {output}");

        string Write(string name,IEnumerable<Vec3> points,IEnumerable<Triangle> triangles)
        {
            var vertices=points.ToArray(); var path=Path.Combine(output,name+".stl");
            using var writer=new StreamWriter(path); writer.WriteLine("solid review");
            foreach(var t in triangles)
            {
                writer.WriteLine("facet normal 0 0 0\nouter loop");
                foreach(var i in new[]{t.A,t.B,t.C}) { var v=vertices[i]; writer.WriteLine(FormattableString.Invariant($"vertex {v.X:R} {v.Y:R} {v.Z:R}")); }
                writer.WriteLine("endloop\nendfacet");
            }
            writer.WriteLine("endsolid review"); return path;
        }
    }
    private static Triangle Reverse(Triangle t)=>new(t.A,t.C,t.B);
    private static Triangle Offset(Triangle t,int n)=>new(t.A+n,t.B+n,t.C+n);
    private static void Check(bool condition,string message) {if(!condition) throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Near(double a,double b,double tolerance)=>Check(Math.Abs(a-b)<tolerance,$"Expected {b}, got {a}.");
    private sealed class ProgressInline(Action<OperationProgress> report):IProgress<OperationProgress> {public void Report(OperationProgress p)=>report(p);}
}
