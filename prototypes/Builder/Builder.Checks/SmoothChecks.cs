using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class SmoothChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=Path.Combine(evidenceRoot??Path.Combine(root,".local/evidence/smooth-20261020"),"core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/smooth-20261020/inputs");
        var worker=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var fault=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/checks/Builder.WorkerFaultProbe.exe");
        var cases=new List<string>();var outputs=new List<object>();
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(string name="noisy-sphere",string extension=".3mf"){var s=New();Ok(await s.OpenAsync(Path.Combine(input,name+extension)));return s;}
        object Input(string name,string extension=".3mf")=>new {path=Path.Combine(input,name+extension),sha256=Hash(Path.Combine(input,name+extension))};
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task Save(EditorSession s,string name,object source,double strength=50,double maximum=.2,bool sharp=true,int boundaries=1,int cavities=0)
        {
            var before=s.Snapshot;var id=before.SelectedObjectId!.Value;var original=before.Scene.Objects.Single(o=>o.Id==id);var canonical=Canonical(original);
            var request=new SmoothRequest(id,strength,maximum,sharp){MemberGroupId=before.EditingGroupId};Ok(await s.PreviewAsync(request,expectedRevision:before.Revision));Accepted(s,before);
            var preview=s.Snapshot.Preview!;var comparison=preview.Smooth??throw new Exception("No smoothing metadata.");var target=preview.Scene.Objects.Single(o=>o.Id==id);
            Check(target.Id==original.Id&&target.Name==original.Name&&target.Transform==Affine.Identity,"Identity/name/world bake changed.");
            Check(target.Mesh.Triangles.SequenceEqual(canonical.Triangles)&&target.Mesh.Vertices.Length==canonical.Vertices.Length,"Authored canonical indexed connectivity changed.");
            Check(comparison.VertexCount==canonical.Vertices.Length&&comparison.TriangleCount==original.Mesh.Triangles.Length&&comparison.BoundaryCount==boundaries&&comparison.CavityCount==cavities,"Wrong count metadata.");
            var movement=target.Mesh.Vertices.Zip(canonical.Vertices,(a,b)=>Length(a-b)).Max();Near(movement,comparison.MaximumVertexDisplacementMillimeters,1e-11);
            Check(movement>0&&comparison.CertifiedDeviationUpperBoundMillimeters>=movement&&comparison.CertifiedDeviationUpperBoundMillimeters<=maximum,"Movement or numerical bound invalid.");
            Near(comparison.OriginalVolumeCubicMillimeters,Volume(original),1e-5);Near(comparison.ResultVolumeCubicMillimeters,Volume(target),1e-5);
            Check(preview.Scene.Groups.SequenceEqual(before.Scene.Groups)&&preview.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&preview.SelectedObjectId==before.SelectedObjectId&&preview.EditingGroupId==before.EditingGroupId,"Full selection/group/scope changed.");
            Check(before.Scene.Objects.Where(o=>o.Id!=id).All(o=>ReferenceEquals(o,preview.Scene.Objects.Single(n=>n.Id==o.Id))),"Unrelated immutable object changed.");
            var sourceFile=Path.Combine(output,name+"-source.json");File.WriteAllText(sourceFile,JsonSerializer.Serialize(new {vertices=canonical.Vertices,triangles=canonical.Triangles,originalVertexRecords=original.Mesh.Vertices.Length}));
            Ok(s.ApplyPreview(preview.Id,before.Revision));var applied=s.Snapshot;Check(applied.Revision==before.Revision+1,"Apply was not one edit.");
            Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&s.Snapshot.EditingGroupId==before.EditingGroupId,"Undo lost exact source/full selection/scope.");
            Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,applied.Scene),"Redo changed immutable result.");
            var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));
            foreach(var path in new[]{native,stl}){var reopened=New();Ok(await reopened.OpenAsync(path));Near(reopened.Snapshot.Scene.Objects.Sum(Volume),preview.Scene.Objects.Sum(Volume),path.EndsWith(".stl")?.02:1e-5);Check(reopened.Snapshot.Scene.Objects.Sum(o=>o.Mesh.Triangles.Length)==preview.Scene.Objects.Sum(o=>o.Mesh.Triangles.Length),"Roundtrip lost triangles.");}
            outputs.Add(new {name,source,request=new {strengthPercent=strength,maximumDeviationMillimeters=maximum,preserveSharpEdges=sharp,memberGroupId=before.EditingGroupId},comparison,
                sourceWorldMesh=new {path=Path.GetFileName(sourceFile),sha256=Hash(sourceFile)},originalBounds=original.Bounds,resultBounds=target.Bounds,targetName=target.Name,
                native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=new {path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        foreach(var name in new[]{"noisy-sphere","noisy-hollow-sphere","thin-hollow-sphere","noisy-torus","narrow-torus","noisy-box","noisy-thin-plate","separate-noisy-solids"})
            await Save(await Load(name),"smoothed-"+name,Input(name),boundaries:name.Contains("hollow")||name=="separate-noisy-solids"?2:1,cavities:name.Contains("hollow")?1:0);
        await Save(await Load("sharp-box"),"smoothed-sharp-box-unprotected",Input("sharp-box"),sharp:false);
        Pass("Nine noisy/curved/torus/thin-wall/cavity/separate-solid fixtures preserve authored canonical triangles, signed volume metadata, identity, one-step history and STL/3MF roundtrip");

        var linear=await Load();var lid=linear.Snapshot.SelectedObjectId!.Value;var sourceMesh=Canonical(linear.Snapshot.Scene.Objects.Single());
        Ok(await linear.PreviewSmoothAsync(new(lid,25,1)));var p25=linear.Snapshot.Preview!;Ok(await linear.PreviewSmoothAsync(new(lid,75,1),p25.Id));var p75=linear.Snapshot.Preview!;
        Check(p25.Smooth!.ClippedVertexCount==0&&p75.Smooth!.ClippedVertexCount==0&&p75.Id!=p25.Id,"Unclamped strength samples unexpectedly limited or token reused.");
        for(var i=0;i<sourceMesh.Vertices.Length;i++)VecNear(p75.Scene.Objects.Single().Mesh.Vertices[i]-sourceMesh.Vertices[i],(p25.Scene.Objects.Single().Mesh.Vertices[i]-sourceMesh.Vertices[i])*3,1e-11);
        linear.DiscardPreview(p75.Id);
        await Save(await Load(),"smoothed-strength-25",Input("noisy-sphere"),strength:25,maximum:1);
        await Save(await Load(),"smoothed-strength-75",Input("noisy-sphere"),strength:75,maximum:1);
        var clipped=await Load();await Save(clipped,"smoothed-clipped",Input("noisy-sphere"),maximum:.01);Check(clipped.Snapshot.Scene.Objects.Single().Mesh.Triangles.Length==1280,"Clamp decimated faces.");
        var protectedBox=await Load("sharp-box");var protectedBefore=protectedBox.Snapshot;Check((await protectedBox.PreviewSmoothAsync(new(protectedBefore.SelectedObjectId!.Value))).Outcome==Outcome.Invalid,"Protected box no-op reported success.");Same(protectedBox,protectedBefore);
        Pass("Strength blends the same full filter linearly before limits; 0.01mm clamp stays within the actual bound; protected sharp box refuses no geometric change");

        var transformed=await Load();var tid=transformed.Snapshot.SelectedObjectId!.Value;Ok(transformed.Execute(new MoveObject(tid,new(-12,7,40))));Ok(transformed.Execute(new RotateObject(tid,new(20,-30,15))));Ok(transformed.Execute(new ResizeObject(tid,new(22,28,24))));
        await Save(transformed,"smoothed-transformed",new {input=Input("noisy-sphere"),moveCenter=new Vec3(-12,7,40),rotationWorldDegrees=new Vec3(20,-30,15),resizeWorldBounds=new Vec3(22,28,24)});
        var negative=await Load();var negativePath=Path.Combine(output,"negative-affine-input.3mf");Ok(await negative.SaveAsync(negativePath));
        using(var zip=System.IO.Compression.ZipFile.Open(negativePath,System.IO.Compression.ZipArchiveMode.Update)){var entry=zip.GetEntry("3D/3dmodel.model")!;System.Xml.Linq.XDocument xml;using(var stream=entry.Open())xml=System.Xml.Linq.XDocument.Load(stream);xml.Descendants().First(e=>e.Name.LocalName=="item").SetAttributeValue("transform","-1 0.5 0 0 2 0 0.25 0 1 40 -10 6");entry.Delete();using var dest=zip.CreateEntry("3D/3dmodel.model").Open();xml.Save(dest);}
        negative=New();Ok(await negative.OpenAsync(negativePath));await Save(negative,"smoothed-reflected",new {path=negativePath,sha256=Hash(negativePath)});
        await Save(await Load(extension:".stl"),"smoothed-stl-source",Input("noisy-sphere",".stl"));
        var grouped=await Load();var member=grouped.Snapshot.SelectedObjectId!.Value;Ok(grouped.Execute(new AddCube()));var sibling=grouped.Snapshot.SelectedObjectId!.Value;Ok(grouped.Execute(new MoveObject(sibling,new(60,10,10))));Ok(await grouped.PreviewArrangementAsync(new GroupRequest([member,sibling],"Smooth pair")));Ok(grouped.ApplyPreview(grouped.Snapshot.Preview!.Id));
        var sceneScope=grouped.Snapshot;Check((await grouped.PreviewSmoothAsync(new(member))).Outcome==Outcome.Invalid,"Scene scope targeted one group member.");Same(grouped,sceneScope);
        Ok(grouped.EnterGroup(grouped.Snapshot.Scene.Groups.Single().Id,member));Ok(grouped.SelectMany([member,sibling],member));var scope=grouped.Snapshot.EditingGroupId;
        await Save(grouped,"smoothed-member",new {input=Input("noisy-sphere"),group="Smooth pair",siblingCubeCenter=new Vec3(60,10,10)});
        Ok(grouped.ExitGroup());var exited=grouped.Snapshot;Check((await grouped.PreviewSmoothAsync(new(member){MemberGroupId=scope})).Outcome==Outcome.Conflict,"Stale member authority accepted.");Same(grouped,exited);
        Pass("Rotated/nonuniform and negative-affine world meshes, exact-welded raw STL and isolated member edits preserve physical mm bounds, siblings, groups, full selection and history");

        var cancel=await Load();var cid=cancel.Snapshot.SelectedObjectId!.Value;Ok(await cancel.SaveAsync(Path.Combine(output,"accepted-baseline.3mf")));Ok(cancel.Execute(new RenameObject(cid,"temporary")));Ok(cancel.Undo());var accepted=cancel.Snapshot;var req=new SmoothRequest(cid);
        Ok(await cancel.PreviewSmoothAsync(req));var first=cancel.Snapshot;Ok(await cancel.PreviewSmoothAsync(req with {StrengthPercent=25},first.Preview!.Id));var held=cancel.Snapshot;
        foreach(var stage in new[]{"before","Preparing smoothing","Preparing geometry","Calculating geometry","Reading geometry result","Validating smoothing","Ready to preview smoothing"}){using var cts=new CancellationTokenSource();if(stage=="before")cts.Cancel();var result=await cancel.PreviewSmoothAsync(req,held.Preview!.Id,cts.Token,new Progress(p=>{if(p.Stage==stage)cts.Cancel();}));Check(result.Outcome==Outcome.Cancelled,"Cancel failed at "+stage+": "+result);Same(cancel,held);}
        foreach(var bad in new[]{req with {StrengthPercent=0},req with {StrengthPercent=101},req with {StrengthPercent=double.NaN},req with {MaximumDeviationMillimeters=0},req with {MaximumDeviationMillimeters=1001},req with {MaximumDeviationMillimeters=double.PositiveInfinity},req with {MaximumDeviationMillimeters=1e-15}}){Check((await cancel.PreviewSmoothAsync(bad,held.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid/below-margin request accepted.");Same(cancel,held);}
        Check((await cancel.PreviewSmoothAsync(req,first.Preview!.Id)).Outcome==Outcome.Conflict&&(await cancel.PreviewSmoothAsync(req,held.Preview!.Id,expectedRevision:accepted.Revision-1)).Outcome==Outcome.Conflict&&(await cancel.PreviewSmoothAsync(req with {ObjectId=Guid.NewGuid()},held.Preview.Id)).Outcome==Outcome.Conflict,"Stale/token/target conflict failed.");
        Check(cancel.Select(null).Outcome==Outcome.Conflict&&cancel.Undo().Outcome==Outcome.Conflict&&cancel.Execute(new AddCube()).Outcome==Outcome.Conflict&&(await cancel.SaveAsync(Path.Combine(output,"must-not-save.3mf"))).Outcome==Outcome.Conflict,"Preview guards failed.");Same(cancel,held);
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cts=new CancellationTokenSource()){var job=cancel.PreviewSmoothAsync(req,held.Preview!.Id,cts.Token,new Progress(p=>{if(p.Stage=="Calculating geometry"){entered.Set();Check(release.Wait(10000),"Worker barrier timeout.");}}));Check(entered.Wait(10000),"Worker did not start.");Check(cancel.ApplyPreview(held.Preview.Id).Outcome==Outcome.Busy&&cancel.Select(null).Outcome==Outcome.Busy&&(await cancel.PreviewSmoothAsync(req,held.Preview.Id)).Outcome==Outcome.Busy,"Busy lease failed.");cts.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy cancel failed.");Same(cancel,held);}
        Check(cancel.DiscardPreview(held.Preview!.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(cancel,accepted);
        Ok(await cancel.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,2,2))));var foreign=cancel.Snapshot;Check((await cancel.PreviewSmoothAsync(req,foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Foreign preview replaced.");Same(cancel,foreign);cancel.DiscardPreview(foreign.Preview.Id);
        Pass("Accepted-source exact-token replacements, cancellation at every session/process boundary, busy/preview/history/file guards and stale/foreign/invalid requests preserve saved/redo state and prior candidate");

        // Fresh synthetic invalid shells use the same exact fixture arrays; no historical writes.
        var cube=(await Load("sharp-box")).Snapshot.Scene.Objects.Single();
        foreach(var kind in new[]{"nested-positive","isolated-inward","pinched-vertex"}){var other=cube with {Transform=kind=="nested-positive"?Affine.ScaleAbout(new(.5,.5,.5),cube.Bounds.Center):Affine.Translation(kind=="pinched-vertex"?cube.Bounds.Size:new(40,0,0))};var path=Path.Combine(output,"invalid-"+kind+".stl");WriteStl(path,[(cube,false),(other,kind=="isolated-inward")]);var s=New();Ok(await s.OpenAsync(path));var before=s.Snapshot;Check((await s.PreviewSmoothAsync(new(before.SelectedObjectId!.Value,50,.2,false))).Outcome==Outcome.Invalid,"Unsafe source accepted: "+kind);Same(s,before);}
        var dangerous=await Load("thin-hollow-sphere");var dangerBefore=dangerous.Snapshot;var unsafeResult=await dangerous.PreviewSmoothAsync(new(dangerBefore.SelectedObjectId!.Value,100,1000,false));
        if(unsafeResult.Succeeded){Check(dangerous.Snapshot.Preview!.Smooth!.CavityCount==1,"High strength lost cavity.");dangerous.DiscardPreview(dangerous.Snapshot.Preview.Id);}else{Check(unsafeResult.Outcome==Outcome.Invalid,"Unsafe case failed outside validation boundary.");Same(dangerous,dangerBefore);}
        Pass("Nested-positive, isolated-inward and pinched sources reject conservatively; a thin-shell high-limit request either preserves its cavity or refuses without mutation");

        var simplify=await Load();Ok(await simplify.PreviewSimplifyAsync(new(simplify.Snapshot.SelectedObjectId!.Value,50,.1)));Check(simplify.Snapshot.Preview!.Simplify!.ResultTriangleCount<1280,"Protocol6 simplification adjacency failed.");
        var hollow=New();Ok(hollow.Execute(new AddCube()));Ok(await hollow.PreviewHollowAsync(new(hollow.Snapshot.SelectedObjectId!.Value,2)));Check(hollow.Snapshot.Preview!.Hollow!.CavityCount==1,"Protocol6 hollow failed.");
        var text=New();Ok(await text.PreviewTextAsync(new(new OutlineTextSource(new TextOutline([[new(0,0),new(4,0),new(4,3),new(0,3)]])),2)));Near(text.Snapshot.Preview!.Scene.Objects.Sum(Volume),24);
        var damaged=New();Ok(await damaged.OpenAsync(Path.Combine(root,"fixtures/generated/missing-top.stl")));Ok(await damaged.PreviewAsync(new RepairRequest(damaged.Snapshot.SelectedObjectId!.Value)));Near(damaged.Snapshot.Preview!.Scene.Objects.Sum(Volume),8000);
        var split=New();Ok(split.Execute(new AddCube()));Ok(await split.PreviewAsync(new SplitRequest(split.Snapshot.SelectedObjectId!.Value,new(1,0,0),10)));Near(split.Snapshot.Preview!.Scene.Objects.Sum(Volume),8000);
        var broken=new EditorSession(fault);Ok(await broken.OpenAsync(Path.Combine(input,"noisy-sphere.3mf")));var brokenBefore=broken.Snapshot;Check((await broken.PreviewSmoothAsync(new(brokenBefore.SelectedObjectId!.Value))).Outcome==Outcome.Failed,"Stale worker protocol accepted.");Same(broken,brokenBefore);
        Pass("Protocol6 simplify/hollow/text/split/repair-validation calls remain functional; stale-version response fails atomically");

        File.WriteAllText(Path.Combine(output,"smooth-results.json"),JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,kernel="Manifold3.5.4 validation only",kernelCommit="ce50d78021d64507f89e8c9fc2c2e51018117857",protocolVersion=6,
            core=new {path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new {path=worker,sha256=Hash(worker)},faultPeer=new {path=fault,sha256=Hash(fault)},inputManifest=new {path=Path.Combine(input,"manifest.json"),sha256=Hash(Path.Combine(input,"manifest.json"))},
            algorithm=new {pairs=10,lambda=.5,mu=-.53,sharpDihedralDegrees=60,order="full filter then strength blend then displacement clamp with two numeric margins reserved",coordinates="world millimeters; canonical exact weld; unchanged corresponding indexed triangle order"},
            limits=new {sceneTriangles=ModelLimits.MaxTriangles,sceneVertices=ModelLimits.MaxVertices,maximumBoundaries=1024,pairBudget=4000000,nestingTriangleBudget=2000000,separationDepth=12,timeoutSeconds=120},
            limitations="Uniform filter depends on tessellation; size and volume may change. Continuous corresponding-triangle bound uses matched vertex motion plus coordinate-scaled floating-point margin, not exact arithmetic. Bounded contact/separation checks can conservatively refuse. No decimation or smoothing-normals-only operation."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} smoothing scenario groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress value)=>action(value);}
    private static Mesh Canonical(SceneObject o){var points=new List<Vec3>();var map=new Dictionary<Vec3,int>();int Index(int index){var p=o.Transform.Apply(o.Mesh.Vertices[index]);if(!map.TryGetValue(p,out var n)){n=map.Count;map.Add(p,n);points.Add(p);}return n;}var negative=Det(o.Transform)<0;var faces=o.Mesh.Triangles.Select(t=>negative?new Triangle(Index(t.A),Index(t.C),Index(t.B)):new Triangle(Index(t.A),Index(t.B),Index(t.C))).ToArray();return new(points,faces);}
    private static void WriteStl(string path,(SceneObject Object,bool Reverse)[] parts){using var w=new BinaryWriter(File.Create(path));w.Write(new byte[80]);w.Write(parts.Sum(p=>p.Object.Mesh.Triangles.Length));foreach(var(o,reverse)in parts)foreach(var t in o.Mesh.Triangles){for(var i=0;i<3;i++)w.Write(0f);foreach(var index in reverse?new[]{t.A,t.C,t.B}:new[]{t.A,t.B,t.C}){var p=o.Transform.Apply(o.Mesh.Vertices[index]);w.Write((float)p.X);w.Write((float)p.Y);w.Write((float)p.Z);}w.Write((ushort)0);}}
    private static double Volume(SceneObject o){var p=o.Transform.Apply(o.Mesh.Vertices[0]);return o.Mesh.Triangles.Sum(t=>Dot(o.Transform.Apply(o.Mesh.Vertices[t.A])-p,Vec3.Cross(o.Transform.Apply(o.Mesh.Vertices[t.B])-p,o.Transform.Apply(o.Mesh.Vertices[t.C])-p))/6)*(Det(o.Transform)<0?-1:1);}
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static double Det(Affine a)=>Dot(a.X,Vec3.Cross(a.Y,a.Z));
    private static double Length(Vec3 v)=>Math.Sqrt(Dot(v,v));
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Near(double actual,double expected,double tolerance=1e-7)=>Check(Math.Abs(actual-expected)<=tolerance,$"Expected {expected}, got {actual}.");
    private static void VecNear(Vec3 a,Vec3 b,double tolerance){Near(a.X,b.X,tolerance);Near(a.Y,b.Y,tolerance);Near(a.Z,b.Z,tolerance);}
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Failure/cancel changed full accepted or previous preview state.");
    private static void Accepted(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot with {Preview=null}==before,"Preview changed accepted state.");
}
