using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class SeparateChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=Path.Combine(evidenceRoot??Path.Combine(root,".local/evidence/separate-20261020"),"core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/separate-20261020/inputs");
        var worker=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var fault=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/checks/Builder.WorkerFaultProbe.exe");
        using var geometry=JsonDocument.Parse(File.ReadAllText(Path.Combine(input,"geometry.json")));
        var cases=new List<string>();var outputs=new List<object>();
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(string name="two-hollow-boxes"){var s=New();Ok(await s.OpenAsync(Path.Combine(input,name+".3mf")));return s;}
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task Save(EditorSession s,string name,string fixture,object? operations=null,bool stl=true)
        {
            var before=s.Snapshot;var source=before.Scene.Objects.Single(o=>o.Id==before.SelectedObjectId);var request=new SeparateRequest(source.Id){MemberGroupId=before.EditingGroupId};
            Ok(await s.PreviewAsync(request,expectedRevision:before.Revision));Check(s.Snapshot with {Preview=null}==before,"Preview changed accepted scene.");
            var p=s.Snapshot.Preview!;var c=p.Separate??throw new Exception("Missing piece metadata.");
            var expected=geometry.RootElement.GetProperty(fixture).GetProperty("expectedPieceTriangleIndices").EnumerateArray().Select(a=>a.EnumerateArray().Select(v=>v.GetInt32()).ToArray()).ToArray();
            Check(c.SourceObjectId==source.Id&&c.OriginalTriangleCount==source.Mesh.Triangles.Length&&c.Pieces.Length==expected.Length,"Incorrect partition source/count.");
            Check(c.Pieces[0].ObjectId==source.Id&&p.Scene.Objects.Single(o=>o.Id==source.Id).Name==source.Name,"First piece lost source identity/name.");
            Check(p.ResultObjectIds.SequenceEqual(c.Pieces.Select(x=>x.ObjectId))&&p.SelectedObjectId==source.Id&&p.ResultObjectIds.All(p.SelectedObjectIds.Contains),"Piece selection/order mismatch.");
            Check(before.SelectedObjectIds.Where(id=>id!=source.Id).All(p.SelectedObjectIds.Contains)&&p.EditingGroupId==before.EditingGroupId,"Surviving selection or scope lost.");
            Check(before.Scene.Objects.Where(o=>o.Id!=source.Id).All(o=>ReferenceEquals(o,p.Scene.Objects.Single(n=>n.Id==o.Id))),"Unrelated object reference changed.");
            for(var i=0;i<c.Pieces.Length;i++)
            {
                var part=c.Pieces[i];Check(part.SourceTriangleIndices.SequenceEqual(expected[i]),"Cavity/island ownership differs from fixture.");
                var obj=p.Scene.Objects.Single(o=>o.Id==part.ObjectId);Check(obj.Transform==source.Transform&&part.TriangleCount==obj.Mesh.Triangles.Length,"Placement/count changed.");
                for(var j=0;j<part.TriangleCount;j++){var a=source.Mesh.Triangles[part.SourceTriangleIndices[j]];var b=obj.Mesh.Triangles[j];Check(source.Mesh.Vertices[a.A]==obj.Mesh.Vertices[b.A]&&source.Mesh.Vertices[a.B]==obj.Mesh.Vertices[b.B]&&source.Mesh.Vertices[a.C]==obj.Mesh.Vertices[b.C],"Source triangle moved/flipped or was omitted.");}
                Near(Volume(obj),part.VolumeCubicMillimeters);
            }
            Near(c.Pieces.Sum(x=>x.VolumeCubicMillimeters),Volume(source));
            var ids=p.ResultObjectIds;Ok(await s.PreviewSeparateAsync(request,p.Id));var replacement=s.Snapshot.Preview!;Check(replacement.Id!=p.Id&&replacement.ResultObjectIds.SequenceEqual(ids),"Replacement IDs are not stable.");
            Ok(s.ApplyPreview(replacement.Id));var applied=s.Snapshot;Check(applied.Revision==before.Revision+1,"Apply not one edit.");Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&s.Snapshot.EditingGroupId==before.EditingGroupId,"Undo lost exact source/selection/scope.");Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,applied.Scene),"Redo lost candidate.");
            var native=Path.Combine(output,name+".3mf");Ok(await s.SaveAsync(native));var reopened=New();Ok(await reopened.OpenAsync(native));Check(reopened.Snapshot.Scene.Objects.Length==applied.Scene.Objects.Length,"Reopen lost piece objects.");Near(reopened.Snapshot.Scene.Objects.Sum(Volume),applied.Scene.Objects.Sum(Volume));
            string? export=null;if(stl){export=Path.Combine(output,name+".stl");Ok(await s.ExportStlAsync(export));var exported=New();Ok(await exported.OpenAsync(export));Near(exported.Snapshot.Scene.Objects.Sum(Volume),applied.Scene.Objects.Sum(Volume),.001);}
            outputs.Add(new {name,input=new {path=Path.Combine(input,fixture+".3mf"),sha256=Hash(Path.Combine(input,fixture+".3mf"))},operations,comparison=c,
                sourceTransform=source.Transform,sourceName=source.Name,group=before.EditingGroupId is {} g?before.Scene.Groups.Single(x=>x.Id==g).Name:null,
                native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=export is null?null:new {path=Path.GetFileName(export),sha256=Hash(export)}});
        }
        foreach(var name in new[]{"two-boxes","two-hollow-boxes","nested-island","torus-and-center"})await Save(await Load(name),"separated-"+name,name);
        Pass("Disjoint/hollow/nested-island/torus-hole pieces match exact source-face ownership; local geometry/placement, selection, one Undo/Redo and native/STL saves remain intact");

        var transformed=await Load();var id=transformed.Snapshot.SelectedObjectId!.Value;Ok(transformed.Execute(new MoveObject(id,new(-12,7,40))));Ok(transformed.Execute(new RotateObject(id,new(20,-30,15))));Ok(transformed.Execute(new ResizeObject(id,new(60,32,24))));
        await Save(transformed,"separated-transformed","two-hollow-boxes",new {moveCenter=new Vec3(-12,7,40),worldRotation=new Vec3(20,-30,15),worldResize=new Vec3(60,32,24)},stl:false);
        var member=await Load();var target=member.Snapshot.SelectedObjectId!.Value;Ok(member.Execute(new AddCube()));var sibling=member.Snapshot.SelectedObjectId!.Value;Ok(member.Execute(new MoveObject(sibling,new(90,10,10))));Ok(await member.PreviewArrangementAsync(new GroupRequest([target,sibling],"Separate pair")));Ok(member.ApplyPreview(member.Snapshot.Preview!.Id));var sceneScope=member.Snapshot;Check((await member.PreviewSeparateAsync(new(target))).Outcome==Outcome.Invalid,"Scene scope separated one group member.");Same(member,sceneScope);
        Ok(member.EnterGroup(member.Snapshot.Scene.Groups.Single().Id,target));Ok(member.SelectMany([target,sibling],target));var scope=member.Snapshot.EditingGroupId;var group=member.Snapshot.Scene.Groups.Single();
        await Save(member,"separated-member","two-hollow-boxes",new {siblingCubeCenter=new Vec3(90,10,10),group="Separate pair"},stl:false);
        Check(member.Snapshot.Scene.Groups.Single().Id==group.Id&&member.Snapshot.Scene.Groups.Single().ObjectIds.Length==3,"Group identity/membership was not extended.");Ok(member.ExitGroup());var exited=member.Snapshot;Check((await member.PreviewSeparateAsync(new(target){MemberGroupId=scope})).Outcome==Outcome.Conflict,"Stale member scope accepted.");Same(member,exited);
        Pass("Transformed local meshes stay exact; member separation extends the same group in document order and retains selected sibling/history; stale member authority rejects");

        var cancel=await Load();Ok(await cancel.SaveAsync(Path.Combine(output,"accepted-baseline.3mf")));var sourceId=cancel.Snapshot.SelectedObjectId!.Value;Ok(cancel.Execute(new RenameObject(sourceId,"temporary")));Ok(cancel.Undo());var accepted=cancel.Snapshot;var request=new SeparateRequest(sourceId);Ok(await cancel.PreviewSeparateAsync(request));var held=cancel.Snapshot;
        foreach(var stage in new[]{"before","Calculating geometry","Ready to preview separation"}){using var cts=new CancellationTokenSource();if(stage=="before")cts.Cancel();Check((await cancel.PreviewSeparateAsync(request,held.Preview!.Id,cts.Token,new Progress(p=>{if(p.Stage==stage)cts.Cancel();}))).Outcome==Outcome.Cancelled,"Cancel failed: "+stage);Same(cancel,held);}
        Check((await cancel.PreviewSeparateAsync(request,Guid.NewGuid())).Outcome==Outcome.Conflict&&(await cancel.PreviewSeparateAsync(request,held.Preview!.Id,expectedRevision:accepted.Revision-1)).Outcome==Outcome.Conflict,"Stale token/revision accepted.");
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cts=new CancellationTokenSource()){var job=cancel.PreviewSeparateAsync(request,held.Preview!.Id,cts.Token,new Progress(p=>{if(p.Stage=="Calculating geometry"){entered.Set();Check(release.Wait(10000),"Worker barrier timeout.");}}));Check(entered.Wait(10000),"Worker did not start.");Check(cancel.ApplyPreview(held.Preview.Id).Outcome==Outcome.Busy&&cancel.Select(null).Outcome==Outcome.Busy,"Busy guard failed.");cts.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy cancellation failed.");Same(cancel,held);}
        Check(cancel.DiscardPreview(held.Preview!.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(cancel,accepted);
        Pass("Exact replacement, cancelled/busy/stale requests preserve accepted scene/path/full selection/redo state and held candidate");

        foreach(var name in new[]{"one-hollow-box","touching-boxes","nested-positive"}){var s=await Load(name);var before=s.Snapshot;var result=await s.PreviewSeparateAsync(new(before.SelectedObjectId!.Value));Check(result.Outcome==Outcome.Invalid,"Unsupported/no-op source accepted: "+name);Check(!result.Message.Contains("simplif",StringComparison.OrdinalIgnoreCase),"Separation error named another tool.");Same(s,before);}
        // Use a known creation preview for an unrelated tool-token guard.
        var other=await Load();Ok(await other.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,2,2))));var otherBefore=other.Snapshot;Check((await other.PreviewSeparateAsync(new(otherBefore.SelectedObjectId!.Value),otherBefore.Preview!.Id)).Outcome==Outcome.Conflict,"Foreign preview replaced.");Same(other,otherBefore);
        Pass("One hollow material piece is a no-op; touching and invalid nested orientation refuse without mutation; another tool's preview cannot be replaced");

        var old=new EditorSession(fault);Ok(await old.OpenAsync(Path.Combine(input,"two-boxes.3mf")));var oldBefore=old.Snapshot;Check((await old.PreviewSeparateAsync(new(oldBefore.SelectedObjectId!.Value))).Outcome==Outcome.Failed,"Stale worker response accepted.");Same(old,oldBefore);
        var smooth=New();Ok(await smooth.OpenAsync(Path.Combine(root,".local/evidence/smooth-20261020/inputs/noisy-sphere.3mf")));Ok(await smooth.PreviewSmoothAsync(new(smooth.Snapshot.SelectedObjectId!.Value)));
        var split=New();Ok(split.Execute(new AddCube()));Ok(await split.PreviewAsync(new SplitRequest(split.Snapshot.SelectedObjectId!.Value,new(1,0,0),10)));Near(split.Snapshot.Preview!.Scene.Objects.Sum(Volume),8000);
        Pass("Protocol7 retains existing smoothing/split geometry paths and rejects a stale peer atomically");

        File.WriteAllText(Path.Combine(output,"separate-results.json"),JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,protocolVersion=7,kernel="Manifold3.5.4 source validation; original partition logic",
            core=new {path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new {path=worker,sha256=Hash(worker)},
            inputManifest=new {path=Path.Combine(input,"manifest.json"),sha256=Hash(Path.Combine(input,"manifest.json"))},toleranceMillimeters=.001,
            limitations="Closed oriented non-touching material boundaries only. Exact source-face partition and original local placement retained. Bounded contact/containment work can conservatively refuse; no repair, packing or new performance claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} separation scenario groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress value)=>action(value);}
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static double Volume(SceneObject o){var p=o.Transform.Apply(o.Mesh.Vertices[0]);return o.Mesh.Triangles.Sum(t=>Dot(o.Transform.Apply(o.Mesh.Vertices[t.A])-p,Vec3.Cross(o.Transform.Apply(o.Mesh.Vertices[t.B])-p,o.Transform.Apply(o.Mesh.Vertices[t.C])-p))/6)*(Dot(o.Transform.X,Vec3.Cross(o.Transform.Y,o.Transform.Z))<0?-1:1);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Near(double actual,double expected,double tolerance=.001)=>Check(Math.Abs(actual-expected)<=tolerance,$"Expected {expected}, got {actual}.");
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Operation changed accepted or prior-preview state.");
}
