using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class HollowChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=Path.Combine(evidenceRoot??Path.Combine(root,".local/evidence/hollow-20261020"),"core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/hollow-20261020/inputs");
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var fault=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/checks/Builder.WorkerFaultProbe.exe");
        var cases=new List<string>();var files=new List<object>();
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(string name="box-20.3mf"){var s=New();Ok(await s.OpenAsync(Path.Combine(input,name)));return s;}
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task SaveCase(EditorSession s,string name,double wall,int cavities,object source)
        {
            var before=s.Snapshot;var id=before.SelectedObjectId!.Value;var target=before.Scene.Objects.Single(o=>o.Id==id);
            Ok(await s.PreviewAsync(new HollowRequest(id,wall){MemberGroupId=before.EditingGroupId},expectedRevision:before.Revision));Accepted(s,before);
            var p=s.Snapshot.Preview!;var h=p.Hollow??throw new Exception("Missing hollow comparison.");var obj=p.Scene.Objects.Single(o=>o.Id==id);
            Check(h.CavityCount==cavities,"Incorrect hollow comparison/cavity count.");
            Near(h.RequestedWallThicknessMillimeters,wall);Check(h.MaximumKernelRadiusMillimeters>=wall&&h.MaximumKernelRadiusMillimeters<wall*1.03,"Kernel does not conservatively contain requested sphere.");
            Near(h.MaximumKernelRadiusMillimeters,wall*1.0228471903736216,1e-9);
            Check(obj.Id==target.Id&&obj.Name==target.Name&&obj.Transform==Affine.Identity,"Hollow lost target identity/name/world baking.");
            Check(p.Scene.Groups.SequenceEqual(before.Scene.Groups)&&p.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&p.SelectedObjectId==before.SelectedObjectId&&p.EditingGroupId==before.EditingGroupId,"Hollow changed groups/full selection/scope.");
            Check(before.Scene.Objects.Where(o=>o.Id!=id).All(o=>ReferenceEquals(o,p.Scene.Objects.Single(n=>n.Id==o.Id))),"Hollow changed unrelated object references.");
            Exterior(target,obj);Check(BoundaryVolumes(obj).Count(v=>v>0)==1&&BoundaryVolumes(obj).Count(v=>v<0)==cavities,"Shell outer/inner signs are wrong.");
            Check(BoundaryVolumes(new(Guid.NewGuid(),"cavity",h.Cavity,Affine.Identity)).All(v=>v>0),"Comparison cavity is not outward.");
            Near(Volume(obj),Volume(target)-h.RemovedVolumeCubicMillimeters,1e-6);Near(Volume(new(Guid.NewGuid(),"cavity",h.Cavity,Affine.Identity)),h.RemovedVolumeCubicMillimeters,1e-6);
            Check(obj.Bounds==target.Bounds,"Hollow changed exterior world bounds.");
            var cavityJson=Path.Combine(output,name+"-cavity.json");File.WriteAllText(cavityJson,JsonSerializer.Serialize(new {vertices=h.Cavity.Vertices,triangles=h.Cavity.Triangles}));
            Ok(s.ApplyPreview(p.Id,before.Revision));var after=s.Snapshot;Check(after.Revision==before.Revision+1,"Hollow not one history revision.");
            Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&s.Snapshot.EditingGroupId==before.EditingGroupId,"Undo did not restore original immutable scene/full scope/selection.");
            Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,after.Scene),"Redo did not retain candidate.");
            var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));
            foreach(var path in new[]{native,stl})
            {
                var reopened=New();Ok(await reopened.OpenAsync(path));Near(reopened.Snapshot.Scene.Objects.Sum(Volume),p.Scene.Objects.Sum(Volume),path.EndsWith(".stl")?.02:1e-6);
                if(path.EndsWith(".3mf"))
                {
                    var roundtrip=reopened.Snapshot.Scene.Objects.Single(o=>o.Name==obj.Name);var volumes=BoundaryVolumes(roundtrip);
                    Check(volumes.Count(v=>v>0)==1&&volumes.Count(v=>v<0)==cavities,"Native roundtrip lost oriented cavity boundaries.");Exterior(target,roundtrip);
                }
                else foreach(var part in reopened.Snapshot.Scene.Objects)Check(BoundaryVolumes(part).Count(v=>v<0)>=cavities,"STL lost cavity orientation.");
            }
            files.Add(new {name,source,wallMillimeters=wall,maximumKernelRadiusMillimeters=h.MaximumKernelRadiusMillimeters,cavityCount=cavities,removedVolumeMm3=h.RemovedVolumeCubicMillimeters,
                expectedMaterialVolumeMm3=p.Scene.Objects.Sum(Volume),expectedTargetMaterialVolumeMm3=Volume(obj),targetBounds=obj.Bounds,originalTargetVertices=target.Mesh.Vertices.Length,originalTargetTriangles=target.Mesh.Triangles.Length,
                cavity=new {path=Path.GetFileName(cavityJson),sha256=Hash(cavityJson)},native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=new {path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        foreach(var name in new[]{"box-20","sphere-20","concave-l","narrow-bridge"})
        {var s=await Load(name+".3mf");await SaveCase(s,"hollow-"+name,2,name=="narrow-bridge"?2:1,new {file=name+".3mf",sha256=Hash(Path.Combine(input,name+".3mf"))});}
        var box=await Load();Ok(await box.PreviewHollowAsync(new(box.Snapshot.SelectedObjectId!.Value,2)));var boxH=box.Snapshot.Preview!.Hollow!;var cb=Bounds3.From(boxH.Cavity.Vertices);var r=boxH.MaximumKernelRadiusMillimeters;
        VecNear(cb.Min,new(r,r,r));VecNear(cb.Max,new(20-r,20-r,20-r));Near(boxH.RemovedVolumeCubicMillimeters,Math.Pow(20-2*r,3),1e-6);
        Pass("Box/faceted sphere/concave L/disconnected bridge cavities preserve exact exterior, identity, groups/selection/history; outward comparison and negative inner boundaries match volumes and STL/3MF roundtrip");

        var torus=New();Ok(await torus.PreviewPrimitiveAsync(new TorusPrimitiveRequest(10,4,24,16)));Ok(torus.ApplyPreview(torus.Snapshot.Preview!.Id));
        await SaveCase(torus,"hollow-torus",1,1,new {primitive="Torus",majorRadius=10,tubeRadius=4,radialSegments=24,tubeSegments=16});
        var transformed=await Load();var tid=transformed.Snapshot.SelectedObjectId!.Value;Ok(transformed.Execute(new MoveObject(tid,new(-7,33,14))));
        Ok(transformed.Execute(new RotateObject(tid,new(20,-30,15))));Ok(transformed.Execute(new ResizeObject(tid,new(28,32,24))));
        await SaveCase(transformed,"hollow-transformed",1,1,new {file="box-20.3mf",moveCenter=new Vec3(-7,33,14),rotationWorldDegrees=new Vec3(20,-30,15),resizeWorldBounds=new Vec3(28,32,24)});
        var reflectedPath=Path.Combine(root,".local/evidence/mirror-measurement-20261020/inputs/reflected-affine-tetrahedron.3mf");var reflected=New();Ok(await reflected.OpenAsync(reflectedPath));
        await SaveCase(reflected,"hollow-reflected-affine",.5,1,new {file=reflectedPath,sha256=Hash(reflectedPath)});
        Pass("Arbitrary-genus torus, rotated/nonuniform world geometry and negative-affine import use physical world thickness with determinant normalization once and exact exterior copying");

        var cancel=await Load();Ok(await cancel.SaveAsync(Path.Combine(output,"hollow-accepted-baseline.3mf")));Ok(cancel.Execute(new RenameObject(cancel.Snapshot.SelectedObjectId!.Value,"temporary")));Ok(cancel.Undo());
        var accepted=cancel.Snapshot;var request=new HollowRequest(accepted.SelectedObjectId!.Value,2);Ok(await cancel.PreviewHollowAsync(request));var first=cancel.Snapshot;
        Ok(await cancel.PreviewHollowAsync(request with {WallThicknessMillimeters=1},first.Preview!.Id));var held=cancel.Snapshot;
        Check(held.Preview!.Id!=first.Preview.Id&&held.Preview.ResultObjectIds.SequenceEqual(first.Preview.ResultObjectIds),"Replacement did not renew token/preserve target ID.");Exterior(accepted.Scene.Objects.Single(),held.Preview.Scene.Objects.Single());
        foreach(var stage in new[]{"before","Preparing hollow","Preparing geometry","Calculating geometry","Reading geometry result","Building hollow shell","Validating hollow shell","validation-worker","Ready to preview hollow"})
        {
            using var cts=new CancellationTokenSource();if(stage=="before")cts.Cancel();var calculations=0;
            var result=await cancel.PreviewHollowAsync(request,held.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage=="Calculating geometry")calculations++;if(p.Stage==stage||stage=="validation-worker"&&p.Stage=="Calculating geometry"&&calculations==2)cts.Cancel();}));
            Check(result.Outcome==Outcome.Cancelled,"Cancel failed at "+stage+": "+result);Same(cancel,held);
        }
        foreach(var bad in new[]{request with {WallThicknessMillimeters=0},request with {WallThicknessMillimeters=1001},request with {WallThicknessMillimeters=double.NaN},request with {WallThicknessMillimeters=double.PositiveInfinity},request with {WallThicknessMillimeters=20}})
        {Check((await cancel.PreviewHollowAsync(bad,held.Preview.Id)).Outcome==Outcome.Invalid,"Invalid/collapsed wall accepted.");Same(cancel,held);}
        Check((await cancel.PreviewHollowAsync(request,first.Preview.Id)).Outcome==Outcome.Conflict,"Stale replacement token accepted.");
        Check((await cancel.PreviewHollowAsync(request,held.Preview.Id,expectedRevision:accepted.Revision-1)).Outcome==Outcome.Conflict,"Stale revision accepted.");
        Check((await cancel.PreviewHollowAsync(request with {ObjectId=Guid.NewGuid()},held.Preview.Id)).Outcome==Outcome.Conflict,"Different replacement target accepted.");
        Check(cancel.Select(null).Outcome==Outcome.Conflict&&cancel.Execute(new AddCube()).Outcome==Outcome.Conflict&&cancel.Undo().Outcome==Outcome.Conflict&&
            (await cancel.SaveAsync(Path.Combine(output,"must-not-save.3mf"))).Outcome==Outcome.Conflict,"Preview mutation/history/file guard failed.");Same(cancel,held);
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cts=new CancellationTokenSource())
        {
            var job=cancel.PreviewHollowAsync(request,held.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage=="Calculating geometry"){entered.Set();Check(release.Wait(10000),"Worker barrier timeout.");}}));
            Check(entered.Wait(10000),"Worker did not start.");Check(cancel.Execute(new AddCube()).Outcome==Outcome.Busy&&cancel.ApplyPreview(held.Preview.Id).Outcome==Outcome.Busy&&
                (await cancel.PreviewHollowAsync(request,held.Preview.Id)).Outcome==Outcome.Busy,"Busy lease failed.");cts.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy cancellation failed.");Same(cancel,held);
        }
        Check(cancel.DiscardPreview(held.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(cancel,accepted);
        Ok(await cancel.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,2,2))));var foreign=cancel.Snapshot;
        Check((await cancel.PreviewHollowAsync(request,foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Foreign preview replaced.");Same(cancel,foreign);cancel.DiscardPreview(foreign.Preview.Id);
        Pass("Exact owned replacement derives from accepted geometry; pre/transfer/kernel/read/shell-validation/prepublish cancellation, busy serialization, stale/foreign/invalid/collapsed requests retain saved/redo state and prior candidate");

        foreach(var name in new[]{"thin-plate.3mf","thin-plate.stl"}){var s=await Load(name);var before=s.Snapshot;Check((await s.PreviewHollowAsync(new(before.SelectedObjectId!.Value,2))).Outcome==Outcome.Invalid,"Empty thin-plate erosion accepted.");Same(s,before);}
        foreach(var name in new[]{"missing-top.stl","degenerate-face.stl","duplicate-face.stl","inverted-cube.stl","overlapping-cubes.stl"})
        {
            var s=New();Ok(await s.OpenAsync(Path.Combine(root,"fixtures/generated",name)));var before=s.Snapshot;
            Check((await s.PreviewHollowAsync(new(before.SelectedObjectId!.Value,1))).Outcome==Outcome.Invalid,"Unsupported damaged/disconnected input accepted: "+name);Same(s,before);
        }
        var already=await Load();Ok(await already.PreviewHollowAsync(new(already.Snapshot.SelectedObjectId!.Value,2)));Ok(already.ApplyPreview(already.Snapshot.Preview!.Id));var alreadyBefore=already.Snapshot;
        Check((await already.PreviewHollowAsync(new(alreadyBefore.SelectedObjectId!.Value,1))).Outcome==Outcome.Invalid,"Already nested hollow shell accepted.");Same(already,alreadyBefore);
        var diagnosis=await already.DiagnoseAsync(alreadyBefore.SelectedObjectId.Value);Ok(diagnosis.Result);Check(diagnosis.Diagnosis!.Assessment.InwardShells==1,"Diagnosis lost intentional inward cavity boundary.");
        Check((await already.PreviewAsync(new RepairRequest(alreadyBefore.SelectedObjectId.Value))).Outcome==Outcome.Invalid,"Repair flipped/repaired intentional unsupported cavity.");Same(already,alreadyBefore);
        var cap=New();Ok(await cap.PreviewPrimitiveAsync(new SpherePrimitiveRequest(new(20,20,20),96,48)));Ok(cap.ApplyPreview(cap.Snapshot.Preview!.Id));var capBefore=cap.Snapshot;var launched=false;
        Check((await cap.PreviewHollowAsync(new(capBefore.SelectedObjectId!.Value),progress:new Progress(p=>{if(p.Stage=="Calculating geometry")launched=true;}))).Outcome==Outcome.Invalid&&!launched,"Operand cap failed before native work.");Same(cap,capBefore);
        Pass("Thin/collapsed, open/degenerate/duplicate/inward/disconnected and already-hollow surfaces reject without repair; diagnosis retains intentional inward boundary and repair rejects cavities; operand cap rejects before native launch");

        var grouped=await Load();var member=grouped.Snapshot.SelectedObjectId!.Value;Ok(grouped.Execute(new AddCube()));var neighbor=grouped.Snapshot.SelectedObjectId!.Value;Ok(grouped.Execute(new MoveObject(neighbor,new(40,10,10))));
        Ok(await grouped.PreviewArrangementAsync(new GroupRequest([member,neighbor],"Hollow pair")));Ok(grouped.ApplyPreview(grouped.Snapshot.Preview!.Id));var groupedBefore=grouped.Snapshot;
        Check((await grouped.PreviewHollowAsync(new(member,2))).Outcome==Outcome.Invalid,"Scene-scope member-only hollow accepted.");Same(grouped,groupedBefore);
        Ok(grouped.EnterGroup(groupedBefore.Scene.Groups.Single().Id,member));var scope=grouped.Snapshot.EditingGroupId;
        await SaveCase(grouped,"hollow-member",2,1,new {file="box-20.3mf",neighborCubeCenter=new Vec3(40,10,10),group="Hollow pair",memberScope=true});
        Ok(grouped.ExitGroup());var exited=grouped.Snapshot;Check((await grouped.PreviewHollowAsync(new(member,2){MemberGroupId=scope})).Outcome==Outcome.Conflict,"Stale member authority accepted.");Same(grouped,exited);
        Pass("Single member targeting requires explicit active scope; sibling refs/group identity/name/full scope/selection/history survive isolated hollow and stale-scope replay rejects");

        // Protocol4 affects all worker calls; use fresh local adjacency artifacts rather than historical suites.
        var adjacent=New();Ok(adjacent.Execute(new AddCube()));var a=adjacent.Snapshot.SelectedObjectId!.Value;Ok(adjacent.Execute(new AddCube()));var b=adjacent.Snapshot.SelectedObjectId!.Value;
        Ok(adjacent.Execute(new MoveObject(b,new(20,10,10))));Ok(await adjacent.PreviewAsync(new BooleanRequest(a,b,BooleanKind.Union)));Near(adjacent.Snapshot.Preview!.Scene.Objects.Sum(Volume),12000);adjacent.DiscardPreview(adjacent.Snapshot.Preview.Id);
        Ok(await already.PreviewAsync(new SplitRequest(alreadyBefore.SelectedObjectId.Value,new(1,0,0),10)));Near(already.Snapshot.Preview!.Scene.Objects.Sum(Volume),alreadyBefore.Scene.Objects.Sum(Volume),1e-6);already.DiscardPreview(already.Snapshot.Preview.Id);
        var textSource=new OutlineTextSource(new TextOutline([[new(0,0),new(4,0),new(4,3),new(0,3)]]));var text=New();Ok(await text.PreviewTextAsync(new(textSource,2)));Near(text.Snapshot.Preview!.Scene.Objects.Sum(Volume),24);text.DiscardPreview(text.Snapshot.Preview.Id);
        foreach(var mode in Enum.GetValues<EmbossMode>()){var s=await Load();Ok(await s.PreviewEmbossAsync(new(s.Snapshot.SelectedObjectId!.Value,textSource,new(EmbossFace.PositiveZ),1,mode)));Near(s.Snapshot.Preview!.Scene.Objects.Sum(Volume),mode==EmbossMode.Raised?8012:7988);}
        var damaged=New();Ok(await damaged.OpenAsync(Path.Combine(root,"fixtures/generated/missing-top.stl")));Ok(await damaged.PreviewAsync(new RepairRequest(damaged.Snapshot.SelectedObjectId!.Value)));Near(damaged.Snapshot.Preview!.Scene.Objects.Sum(Volume),8000);
        var broken=new EditorSession(fault);Ok(await broken.OpenAsync(Path.Combine(input,"box-20.3mf")));var brokenBefore=broken.Snapshot;
        Check((await broken.PreviewHollowAsync(new(brokenBefore.SelectedObjectId!.Value,2))).Outcome==Outcome.Failed,"Stale-version hollow response accepted.");Same(broken,brokenBefore);
        var missing=new EditorSession(Path.Combine(output,"missing-worker.exe"));Ok(missing.Execute(new AddCube()));var missingBefore=missing.Snapshot;
        Check((await missing.PreviewHollowAsync(new(missingBefore.SelectedObjectId!.Value,2))).Outcome==Outcome.Invalid,"Missing hollow worker accepted.");Same(missing,missingBefore);
        Pass("Protocol4 union/split of hollow material, explicit text/raised/engraved and repair validation remain functional; missing/stale-version workers fail without accepted-state mutation");

        File.WriteAllText(Path.Combine(output,"hollow-results.json"),JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,kernel="Manifold 3.5.4",kernelCommit="ce50d78021d64507f89e8c9fc2c2e51018117857",protocolVersion=4,
            core=new {path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new {path=worker,sha256=Hash(worker)},faultPeer=new {path=fault,sha256=Hash(fault)},
            inputManifest=new {path=Path.Combine(input,"manifest.json"),sha256=Hash(Path.Combine(input,"manifest.json"))},outputs=files,
            limits=new {operandTriangles=4096,sceneTriangles=ModelLimits.MaxTriangles,sceneVertices=ModelLimits.MaxVertices,wallMm="0.05..1000",kernelSegments=24,kernelTriangles=288,timeoutSeconds=120},
            limitations="Sealed shell, no drain holes. Conservative kernel-support approximation is not a uniform maximum local wall bound. Narrow features may remain solid. One connected closed oriented surface only; no self-intersection certificate or100k-face hollow operand claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} hollow scenario groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Exterior(SceneObject before,SceneObject after)
    {
        Check(after.Mesh.Vertices.Length>=before.Mesh.Vertices.Length&&after.Mesh.Triangles.Length>before.Mesh.Triangles.Length,"No cavity added.");
        for(var i=0;i<before.Mesh.Vertices.Length;i++)Check(after.Mesh.Vertices[i]==before.Transform.Apply(before.Mesh.Vertices[i]),"Exterior vertex changed.");
        var determinant=Dot(before.Transform.X,Vec3.Cross(before.Transform.Y,before.Transform.Z));
        for(var i=0;i<before.Mesh.Triangles.Length;i++){var t=before.Mesh.Triangles[i];Check(after.Mesh.Triangles[i]==(determinant<0?new Triangle(t.A,t.C,t.B):t),"Exterior face changed.");}
    }
    private static List<double> BoundaryVolumes(SceneObject obj)
    {
        var points=new Dictionary<Vec3,int>();var remap=obj.Mesh.Vertices.Select(obj.Transform.Apply).Select(v=>{if(!points.TryGetValue(v,out var i)){i=points.Count;points.Add(v,i);}return i;}).ToArray();
        var edges=new Dictionary<(int,int),int>();var adjacency=new Dictionary<int,HashSet<int>>();
        foreach(var t in obj.Mesh.Triangles)
        {
            Check(Vec3.Cross(obj.Mesh.Vertices[t.B]-obj.Mesh.Vertices[t.A],obj.Mesh.Vertices[t.C]-obj.Mesh.Vertices[t.A])!=new Vec3(),"Degenerate shell face.");
            foreach(var e in new[]{(remap[t.A],remap[t.B]),(remap[t.B],remap[t.C]),(remap[t.C],remap[t.A])}){edges[e]=edges.GetValueOrDefault(e)+1;if(!adjacency.TryGetValue(e.Item1,out var n))adjacency[e.Item1]=n=[];n.Add(e.Item2);}
        }
        Check(edges.All(e=>e.Value==1&&edges.GetValueOrDefault((e.Key.Item2,e.Key.Item1))==1),"Shell boundary not consistently closed.");
        var components=new Dictionary<int,int>();var count=0;
        foreach(var start in adjacency.Keys)if(!components.ContainsKey(start)){var stack=new Stack<int>();stack.Push(start);while(stack.TryPop(out var v))if(components.TryAdd(v,count))foreach(var n in adjacency[v])stack.Push(n);count++;}
        var volumes=Enumerable.Repeat(0d,count).ToArray();var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);
        foreach(var t in obj.Mesh.Triangles){var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin;var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin;var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin;volumes[components[remap[t.A]]]+=Dot(a,Vec3.Cross(b,c))/6;}
        Check(volumes.All(v=>v!=0),"Zero-volume shell component.");return volumes.ToList();
    }
    private static double Volume(SceneObject obj)
    {
        var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);var signed=obj.Mesh.Triangles.Sum(t=>{var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin;var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin;var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin;return Dot(a,Vec3.Cross(b,c))/6;});
        return Dot(obj.Transform.X,Vec3.Cross(obj.Transform.Y,obj.Transform.Z))<0?-signed:signed;
    }
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Near(double actual,double expected,double tolerance=1e-7)=>Check(Math.Abs(actual-expected)<=tolerance,$"Expected {expected}, got {actual}.");
    private static void VecNear(Vec3 a,Vec3 b){Near(a.X,b.X);Near(a.Y,b.Y);Near(a.Z,b.Z);}
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Failure/cancel changed accepted scene/path/selection/scope/history/preview.");
    private static void Accepted(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot with {Preview=null}==before,"Preview changed accepted state.");
}
