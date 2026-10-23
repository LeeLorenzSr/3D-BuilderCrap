using Builder.Core;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class SimplifyChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=Path.Combine(evidenceRoot??Path.Combine(root,".local/evidence/simplify-20261020"),"core");
        Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/simplify-20261020/inputs");
        var worker=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var fault=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/checks/Builder.WorkerFaultProbe.exe");
        var cases=new List<string>();var files=new List<object>();
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(string name="dense-box") {var s=New();Ok(await s.OpenAsync(Path.Combine(input,name+".3mf")));return s;}
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task SaveCase(EditorSession s,string name,double reduction,double deviation,object source,int boundaries,int cavities)
        {
            var before=s.Snapshot;var id=before.SelectedObjectId!.Value;var target=before.Scene.Objects.Single(o=>o.Id==id);
            var request=new SimplifyRequest(id,reduction,deviation){MemberGroupId=before.EditingGroupId};
            Ok(await s.PreviewAsync(request,expectedRevision:before.Revision));Accepted(s,before);
            var p=s.Snapshot.Preview!;var h=p.Simplify??throw new Exception("Missing simplification metadata.");var obj=p.Scene.Objects.Single(o=>o.Id==id);
            Check(h.OriginalTriangleCount==target.Mesh.Triangles.Length&&h.ResultTriangleCount==obj.Mesh.Triangles.Length&&h.ResultTriangleCount<h.OriginalTriangleCount,"Incorrect reduction metadata.");
            Near(h.AchievedReductionPercent,100d*(h.OriginalTriangleCount-h.ResultTriangleCount)/h.OriginalTriangleCount);
            Check(h.TargetReached==(h.ResultTriangleCount<=Math.Ceiling(h.OriginalTriangleCount*(1-reduction/100))),"Target goal reported incorrectly.");
            Check(h.BoundaryCount==boundaries&&h.CavityCount==cavities,"Incorrect boundary/cavity metadata.");
            Check(double.IsFinite(h.CertifiedDeviationUpperBoundMillimeters)&&h.CertifiedDeviationUpperBoundMillimeters>=0&&h.CertifiedDeviationUpperBoundMillimeters<=deviation&&h.WorkingToleranceMillimeters<=deviation,"Invalid numerical surface bound/working tolerance.");
            Check(obj.Id==target.Id&&obj.Name==target.Name&&obj.Transform==Affine.Identity,"Target identity/name/world baking lost.");
            Check(p.Scene.Groups.SequenceEqual(before.Scene.Groups)&&p.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&p.SelectedObjectId==before.SelectedObjectId&&p.EditingGroupId==before.EditingGroupId,"Groups/full selection/scope changed.");
            Check(before.Scene.Objects.Where(o=>o.Id!=id).All(o=>ReferenceEquals(o,p.Scene.Objects.Single(n=>n.Id==o.Id))),"Unrelated reference changed.");
            var sourceTopology=Topology(target);var resultTopology=Topology(obj);
            Check(sourceTopology.OrderBy(t=>t.Euler).ThenBy(t=>t.Positive).SequenceEqual(resultTopology.OrderBy(t=>t.Euler).ThenBy(t=>t.Positive)),"Boundary genus/sign changed.");
            Check(resultTopology.Count==boundaries&&resultTopology.Count(t=>!t.Positive)==cavities,"Saved result shell topology disagrees.");
            Near(h.OriginalVolumeCubicMillimeters,Volume(target),1e-5);Near(h.ResultVolumeCubicMillimeters,Volume(obj),1e-5);
            var world=target.Mesh.Vertices.Select(target.Transform.Apply).ToArray();var faces=target.Mesh.Triangles.Select(t=>Det(target.Transform)<0?new Triangle(t.A,t.C,t.B):t).ToArray();
            var sourceFile=Path.Combine(output,name+"-source.json");File.WriteAllText(sourceFile,JsonSerializer.Serialize(new {vertices=world,triangles=faces}));
            Ok(s.ApplyPreview(p.Id,before.Revision));var after=s.Snapshot;Check(after.Revision==before.Revision+1,"Apply not exactly one edit.");
            Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&s.Snapshot.EditingGroupId==before.EditingGroupId,"Undo did not restore exact scene/full scope selection.");
            Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,after.Scene),"Redo changed immutable candidate.");
            var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));
            foreach(var path in new[]{native,stl}){var opened=New();Ok(await opened.OpenAsync(path));Near(opened.Snapshot.Scene.Objects.Sum(Volume),p.Scene.Objects.Sum(Volume),path.EndsWith(".stl")?.05:1e-5);}
            files.Add(new {name,source,request=new {reductionPercent=reduction,maximumDeviationMillimeters=deviation,memberGroupId=before.EditingGroupId},comparison=h,
                sourceWorldMesh=new {path=Path.GetFileName(sourceFile),sha256=Hash(sourceFile)},resultTargetName=obj.Name,targetBounds=obj.Bounds,
                native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=new {path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        foreach(var name in new[]{"dense-box","dense-sphere","dense-torus","hollow-box","thin-hollow-box","hollow-sphere","thin-plate","separate-spheres","small-secondary","narrow-torus"})
        {
            var s=await Load(name);var multiple=name is "hollow-box" or "thin-hollow-box" or "hollow-sphere" or "separate-spheres" or "small-secondary";
            await SaveCase(s,"simplified-"+name,50,.1,new {file=Path.Combine(input,name+".3mf"),sha256=Hash(Path.Combine(input,name+".3mf"))},multiple?2:1,name.Contains("hollow")?1:0);
        }
        Pass("Ten dense/planar/curved/torus/thin-wall/cavity/tiny-secondary fixtures strictly reduce while preserving every oriented boundary/genus; metadata, one undo and STL/3MF roundtrip agree");

        var transformed=await Load("dense-sphere");var tid=transformed.Snapshot.SelectedObjectId!.Value;
        Ok(transformed.Execute(new MoveObject(tid,new(-12,7,40))));Ok(transformed.Execute(new RotateObject(tid,new(20,-30,15))));Ok(transformed.Execute(new ResizeObject(tid,new(22,28,24))));
        await SaveCase(transformed,"simplified-transformed",50,.5,new {file=Path.Combine(input,"dense-sphere.3mf"),sha256=Hash(Path.Combine(input,"dense-sphere.3mf")),moveCenter=new Vec3(-12,7,40),worldRotationDegrees=new Vec3(20,-30,15),worldResize=new Vec3(22,28,24)},1,0);
        // The affine has negative determinant; use a dense mesh so strict reduction remains possible.
        var reflected=await Load();var reflectedNative=Path.Combine(output,"reflected-input.3mf");Ok(await reflected.SaveAsync(reflectedNative));
        using(var zip=System.IO.Compression.ZipFile.Open(reflectedNative,System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry=zip.GetEntry("3D/3dmodel.model")!;System.Xml.Linq.XDocument xml;using(var stream=entry.Open())xml=System.Xml.Linq.XDocument.Load(stream);
            var item=xml.Descendants().First(e=>e.Name.LocalName=="item");item.SetAttributeValue("transform","-1 0.5 0 0 2 0 0.25 0 1 40 -10 6");entry.Delete();using var dest=zip.CreateEntry("3D/3dmodel.model").Open();xml.Save(dest);
        }
        reflected=New();Ok(await reflected.OpenAsync(reflectedNative));await SaveCase(reflected,"simplified-reflected",50,.1,new {file=reflectedNative,sha256=Hash(reflectedNative)},1,0);
        var grouped=await Load();var member=grouped.Snapshot.SelectedObjectId!.Value;Ok(grouped.Execute(new AddCube()));var sibling=grouped.Snapshot.SelectedObjectId!.Value;
        Ok(grouped.Execute(new MoveObject(sibling,new(60,10,10))));Ok(await grouped.PreviewArrangementAsync(new GroupRequest([member,sibling],"Simplify pair")));Ok(grouped.ApplyPreview(grouped.Snapshot.Preview!.Id));
        var groupBefore=grouped.Snapshot;Check((await grouped.PreviewSimplifyAsync(new(member))).Outcome==Outcome.Invalid,"Scene scope changed one member.");Same(grouped,groupBefore);
        Ok(grouped.EnterGroup(grouped.Snapshot.Scene.Groups.Single().Id,member));Ok(grouped.SelectMany([member,sibling],member));var scope=grouped.Snapshot.EditingGroupId;
        await SaveCase(grouped,"simplified-member",50,.1,new {file=Path.Combine(input,"dense-box.3mf"),sha256=Hash(Path.Combine(input,"dense-box.3mf")),neighborCubeCenter=new Vec3(60,10,10),group="Simplify pair"},1,0);
        Ok(grouped.ExitGroup());var exited=grouped.Snapshot;Check((await grouped.PreviewSimplifyAsync(new(member){MemberGroupId=scope})).Outcome==Outcome.Conflict,"Stale member authority accepted.");Same(grouped,exited);
        Pass("Rotated/nonuniform world geometry, negative-affine imported winding and isolated group members preserve identity, sibling references, group name, scope and full history");

        var cancel=await Load();Ok(await cancel.SaveAsync(Path.Combine(output,"accepted-baseline.3mf")));var cid=cancel.Snapshot.SelectedObjectId!.Value;
        Ok(cancel.Execute(new RenameObject(cid,"temporary")));Ok(cancel.Undo());var accepted=cancel.Snapshot;var req=new SimplifyRequest(cid);
        Ok(await cancel.PreviewSimplifyAsync(req));var first=cancel.Snapshot;Ok(await cancel.PreviewSimplifyAsync(req with {ReductionPercent=25},first.Preview!.Id));var held=cancel.Snapshot;
        Check(held.Preview!.Id!=first.Preview.Id&&held.Preview.Simplify!.OriginalTriangleCount==accepted.Scene.Objects.Single().Mesh.Triangles.Length,"Replacement accumulated or token not renewed.");
        foreach(var stage in new[]{"before","Preparing simplification","Preparing geometry","Calculating geometry","Reading geometry result","Validating simplification","Ready to preview simplification"})
        {using var cts=new CancellationTokenSource();if(stage=="before")cts.Cancel();var result=await cancel.PreviewSimplifyAsync(req,held.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage==stage)cts.Cancel();}));Check(result.Outcome==Outcome.Cancelled,"Cancellation failed at "+stage+": "+result);Same(cancel,held);}
        foreach(var bad in new[]{req with {ReductionPercent=0},req with {ReductionPercent=100},req with {ReductionPercent=double.NaN},req with {MaximumDeviationMillimeters=double.PositiveInfinity},req with {MaximumDeviationMillimeters=0},req with {MaximumDeviationMillimeters=1001},req with {MaximumDeviationMillimeters=1e-15}})
        {Check((await cancel.PreviewSimplifyAsync(bad,held.Preview.Id)).Outcome==Outcome.Invalid,"Bad/below-floor request accepted.");Same(cancel,held);}
        Check((await cancel.PreviewSimplifyAsync(req,first.Preview.Id)).Outcome==Outcome.Conflict,"Old preview token accepted.");
        Check((await cancel.PreviewSimplifyAsync(req,held.Preview.Id,expectedRevision:accepted.Revision-1)).Outcome==Outcome.Conflict,"Old revision accepted.");
        Check((await cancel.PreviewSimplifyAsync(req with {ObjectId=Guid.NewGuid()},held.Preview.Id)).Outcome==Outcome.Conflict,"Different replacement target accepted.");
        Check(cancel.Select(null).Outcome==Outcome.Conflict&&cancel.Undo().Outcome==Outcome.Conflict&&cancel.Execute(new AddCube()).Outcome==Outcome.Conflict&&(await cancel.SaveAsync(Path.Combine(output,"must-not-save.3mf"))).Outcome==Outcome.Conflict,"Private preview guards failed.");Same(cancel,held);
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cts=new CancellationTokenSource())
        {
            var job=cancel.PreviewSimplifyAsync(req,held.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage=="Calculating geometry"){entered.Set();Check(release.Wait(10000),"Barrier timed out.");}}));
            Check(entered.Wait(10000),"Kernel did not start.");Check(cancel.ApplyPreview(held.Preview.Id).Outcome==Outcome.Busy&&cancel.Select(null).Outcome==Outcome.Busy&&(await cancel.PreviewSimplifyAsync(req,held.Preview.Id)).Outcome==Outcome.Busy,"Busy lease failed.");cts.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Worker cancellation failed.");Same(cancel,held);
        }
        Check(cancel.DiscardPreview(held.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(cancel,accepted);
        Ok(await cancel.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,2,2))));var foreign=cancel.Snapshot;
        Check((await cancel.PreviewSimplifyAsync(req,foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Foreign preview replaced.");Same(cancel,foreign);cancel.DiscardPreview(foreign.Preview.Id);
        Pass("Accepted-source replacements, all cancellation publication stages, busy serialization, stale/foreign/scope/invalid settings retain saved path, clean/redo state, selection and prior candidate");

        var plain=New();Ok(plain.Execute(new AddCube()));var plainBefore=plain.Snapshot;Check((await plain.PreviewSimplifyAsync(new(plainBefore.SelectedObjectId!.Value))).Outcome==Outcome.Invalid,"Already minimal box no-op accepted.");Same(plain,plainBefore);
        var baseBox=(await Load()).Snapshot.Scene.Objects.Single();
        foreach(var kind in new[]{"nested-positive","isolated-negative","pinched-vertex"})
        {
            var other=kind=="nested-positive"?baseBox with {Transform=Affine.ScaleAbout(new(.5,.5,.5),new(10,10,10))}:baseBox with {Transform=Affine.Translation(kind=="pinched-vertex"?new(20,20,20):new(40,0,0))};
            var invalid=Path.Combine(output,"invalid-"+kind+".stl");WriteStl(invalid,[(baseBox,false),(other,kind=="isolated-negative")]);var s=New();Ok(await s.OpenAsync(invalid));var before=s.Snapshot;
            Check((await s.PreviewSimplifyAsync(new(before.SelectedObjectId!.Value))).Outcome==Outcome.Invalid,"Unsupported shell arrangement accepted: "+kind);Same(s,before);
        }
        Pass("No-op and invalid nested-positive, isolated-inward and pinched-vertex boundaries reject without normalizing authored material or losing the accepted document");

        var densePath=Path.Combine(root,".local/evidence/performance-20261019/inputs/dense-100000.stl");var denseHash=Hash(densePath);var dense=New();Ok(await dense.OpenAsync(densePath));
        var denseBefore=dense.Snapshot;var timer=Stopwatch.StartNew();var denseResult=await dense.PreviewSimplifyAsync(new(denseBefore.SelectedObjectId!.Value));timer.Stop();
        Check(Hash(densePath)==denseHash,"Read-only historical fixture changed.");
        Check(denseResult.Outcome==Outcome.Invalid&&denseResult.Message.Contains("surface-bound verification work",StringComparison.Ordinal),"Near-limit conservative budget refusal changed unexpectedly: "+denseResult);Same(dense,denseBefore);
        Pass("One public-session 100,000-triangle/300,000-raw-vertex planar import is admitted but conservatively refused at the surface-certificate work budget; exact accepted state and source hash preserved");

        using(var peer=Process.Start(new ProcessStartInfo(fault,"--simplify-safety-tests"){UseShellExecute=false,CreateNoWindow=true})!){peer.WaitForExit();Check(peer.ExitCode==0,"Native near-parallel contact regression failed.");}
        var adjacent=New();Ok(adjacent.Execute(new AddCube()));var a=adjacent.Snapshot.SelectedObjectId!.Value;Ok(adjacent.Execute(new AddCube()));var b=adjacent.Snapshot.SelectedObjectId!.Value;Ok(adjacent.Execute(new MoveObject(b,new(20,10,10))));
        Ok(await adjacent.PreviewAsync(new BooleanRequest(a,b,BooleanKind.Union)));Near(adjacent.Snapshot.Preview!.Scene.Objects.Sum(Volume),12000);adjacent.DiscardPreview(adjacent.Snapshot.Preview.Id);
        Ok(await adjacent.PreviewAsync(new SplitRequest(a,new(1,0,0),10)));Near(adjacent.Snapshot.Preview!.Scene.Objects.Where(o=>o.Id!=b).Sum(Volume),8000);adjacent.DiscardPreview(adjacent.Snapshot.Preview.Id);
        var box=New();Ok(box.Execute(new AddCube()));Ok(await box.PreviewHollowAsync(new(box.Snapshot.SelectedObjectId!.Value,2)));Check(box.Snapshot.Preview!.Hollow!.CavityCount==1,"Protocol5 hollow metadata lost.");
        var text=New();Ok(await text.PreviewTextAsync(new(new OutlineTextSource(new TextOutline([[new(0,0),new(4,0),new(4,3),new(0,3)]])),2)));Near(text.Snapshot.Preview!.Scene.Objects.Sum(Volume),24);
        var damaged=New();Ok(await damaged.OpenAsync(Path.Combine(root,"fixtures/generated/missing-top.stl")));Ok(await damaged.PreviewAsync(new RepairRequest(damaged.Snapshot.SelectedObjectId!.Value)));Near(damaged.Snapshot.Preview!.Scene.Objects.Sum(Volume),8000);
        var broken=new EditorSession(fault);Ok(await broken.OpenAsync(Path.Combine(input,"dense-box.3mf")));var brokenBefore=broken.Snapshot;Check((await broken.PreviewSimplifyAsync(new(brokenBefore.SelectedObjectId!.Value))).Outcome==Outcome.Failed,"Stale protocol accepted.");Same(broken,brokenBefore);
        Pass("Protocol5 existing union/split/hollow/text/repair validation paths remain functional; stale-worker response refuses simplification atomically");

        File.WriteAllText(Path.Combine(output,"simplify-results.json"),JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,kernel="Manifold 3.5.4",kernelCommit="ce50d78021d64507f89e8c9fc2c2e51018117857",protocolVersion=5,
            core=new {path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new {path=worker,sha256=Hash(worker)},faultPeer=new {path=fault,sha256=Hash(fault)},
            inputManifest=new {path=Path.Combine(input,"manifest.json"),sha256=Hash(Path.Combine(input,"manifest.json"))},outputs=files,
            denseLimitProbe=new {path=densePath,sha256=denseHash,milliseconds=timer.Elapsed.TotalMilliseconds,outcome=denseResult.Outcome.ToString(),message=denseResult.Message,acceptedTriangles=denseBefore.Scene.Objects.Single().Mesh.Triangles.Length},
            limits=new {sceneTriangles=ModelLimits.MaxTriangles,sceneVertices=ModelLimits.MaxVertices,maximumBoundaries=1024,fixedToleranceAttempts=6,patchBudget=2000000,pairBudget=4000000,nestingTriangleBudget=2000000,maximumSubdivisionDepth=12,timeoutSeconds=120},
            limitations="Numerical continuous-patch upper bound with explicit floating-point margin, not exact arithmetic. Both corresponding boundary directions checked. Work/depth budgets conservatively refuse hard geometry. Requested reduction is a goal; a safe smaller reduction may be returned. Shape volume can change within the surface limit."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} simplification scenario groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static List<(int Euler,bool Positive)> Topology(SceneObject obj)
    {
        var points=new Dictionary<Vec3,int>();var p=new List<Vec3>();var remap=obj.Mesh.Vertices.Select(obj.Transform.Apply).Select(v=>{if(!points.TryGetValue(v,out var i)){i=points.Count;points.Add(v,i);p.Add(v);}return i;}).ToArray();
        var faces=obj.Mesh.Triangles.Select(t=>new Triangle(remap[t.A],remap[t.B],remap[t.C])).ToArray();var edges=new Dictionary<(int,int),int>();var graph=Enumerable.Range(0,p.Count).Select(_=>new HashSet<int>()).ToArray();
        foreach(var t in faces){Check(Vec3.Cross(p[t.B]-p[t.A],p[t.C]-p[t.A])!=new Vec3(),"Degenerate face.");foreach(var e in new[]{(t.A,t.B),(t.B,t.C),(t.C,t.A)}){edges[e]=edges.GetValueOrDefault(e)+1;graph[e.Item1].Add(e.Item2);}}
        Check(edges.All(e=>e.Value==1&&edges.GetValueOrDefault((e.Key.Item2,e.Key.Item1))==1),"Boundary is not closed/oriented.");
        var seen=new HashSet<int>();var result=new List<(int,bool)>();foreach(var first in Enumerable.Range(0,p.Count))if(!seen.Contains(first)){var component=new HashSet<int>();var stack=new Stack<int>();stack.Push(first);while(stack.TryPop(out var v))if(component.Add(v)){seen.Add(v);foreach(var n in graph[v])stack.Push(n);}var ff=faces.Where(t=>component.Contains(t.A)).ToArray();var origin=p[first];var volume=ff.Sum(t=>Dot(p[t.A]-origin,Vec3.Cross(p[t.B]-origin,p[t.C]-origin))/6);result.Add((component.Count-ff.Length/2,volume*Det(obj.Transform)>0));}return result;
    }
    private static void WriteStl(string path,(SceneObject Object,bool Reverse)[] parts)
    {using var writer=new BinaryWriter(File.Create(path));writer.Write(new byte[80]);writer.Write(parts.Sum(p=>p.Object.Mesh.Triangles.Length));foreach(var (o,reverse) in parts)foreach(var t in o.Mesh.Triangles){for(var i=0;i<3;i++)writer.Write(0f);foreach(var index in reverse?new[]{t.A,t.C,t.B}:new[]{t.A,t.B,t.C}){var v=o.Transform.Apply(o.Mesh.Vertices[index]);writer.Write((float)v.X);writer.Write((float)v.Y);writer.Write((float)v.Z);}writer.Write((ushort)0);}}
    private static double Volume(SceneObject obj){var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);return obj.Mesh.Triangles.Sum(t=>Dot(obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin,Vec3.Cross(obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin,obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin))/6)*(Det(obj.Transform)<0?-1:1);}
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static double Det(Affine t)=>Dot(t.X,Vec3.Cross(t.Y,t.Z));
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Near(double actual,double expected,double tolerance=1e-7)=>Check(Math.Abs(actual-expected)<=tolerance,$"Expected {expected}, got {actual}.");
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Failure/cancel changed full accepted or prior preview state.");
    private static void Accepted(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot with {Preview=null}==before,"Preview changed accepted state.");
}
