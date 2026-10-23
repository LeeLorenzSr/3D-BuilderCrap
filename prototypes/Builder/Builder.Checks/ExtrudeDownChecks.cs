using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ExtrudeDownChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=Path.Combine(evidenceRoot??Path.Combine(root,".local/evidence/extrude-down-20261020"),"core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/extrude-down-20261020/inputs");
        var worker=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var fault=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/checks/Builder.WorkerFaultProbe.exe");
        var cases=new List<string>();var outputs=new List<object>();
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(string name="raised-box"){var s=New();Ok(await s.OpenAsync(Path.Combine(input,name+".3mf")));return s;}
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task Save(string fixture,string name,double plane,double? expectedVolume)
        {
            var s=await Load(fixture);var before=s.Snapshot;var source=before.Scene.Objects.Single();
            Ok(await s.PreviewAsync(new ExtrudeDownRequest(source.Id,plane)));SameAccepted(s,before);var p=s.Snapshot.Preview!;var c=p.ExtrudeDown!;var target=p.Scene.Objects.Single();
            Check(target.Id==source.Id&&target.Name==source.Name&&target.Transform==Affine.Identity&&p.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Identity/selection/world bake changed.");
            Near(c.PlaneHeightMillimeters,plane);Near(c.OriginalVolumeCubicMillimeters,Volume(source));Near(c.ResultVolumeCubicMillimeters,Volume(target));Near(c.AddedVolumeCubicMillimeters,c.ResultVolumeCubicMillimeters-c.OriginalVolumeCubicMillimeters);
            if(expectedVolume is {} expected)Near(Volume(target),expected);
            Near(target.Bounds.Min.X,source.Bounds.Min.X);Near(target.Bounds.Max.X,source.Bounds.Max.X);Near(target.Bounds.Min.Y,source.Bounds.Min.Y);Near(target.Bounds.Max.Y,source.Bounds.Max.Y);Near(target.Bounds.Max.Z,source.Bounds.Max.Z);Near(target.Bounds.Min.Z,Math.Min(source.Bounds.Min.Z,plane));
            if(fixture=="raised-torus")Check(p.Scene.PickRay(new(source.Bounds.Center.X,source.Bounds.Center.Y,50),new(0,0,-1)) is null,"Extrude down plugged an unobstructed vertical hole.");
            Ok(s.ApplyPreview(p.Id));var applied=s.Snapshot;Check(applied.Revision==before.Revision+1,"Apply not one edit.");Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Undo did not restore exact source.");Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,applied.Scene),"Redo changed reviewed candidate.");
            var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));
            foreach(var path in new[]{native,stl}){var reopened=New();Ok(await reopened.OpenAsync(path));Near(reopened.Snapshot.Scene.Objects.Sum(Volume),Volume(target));}
            outputs.Add(new {name,input=new {path=Path.Combine(input,fixture+".3mf"),sha256=Hash(Path.Combine(input,fixture+".3mf"))},planeHeightMillimeters=plane,expectedVolumeCubicMillimeters=expectedVolume,comparison=c,sourceBounds=source.Bounds,resultBounds=target.Bounds,
                native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=new {path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        await Save("raised-box","box-to-bed",0,12000);await Save("raised-hollow-box","hollow-to-bed",0,12000);
        await Save("raised-hollow-box","hollow-intersecting-plane",20,5952);await Save("raised-torus","torus-to-bed",0,null);
        Pass("Raised box extends to bed, covered hollow fills explicitly, intersecting plane retains original lower material, and torus vertical hole stays open; identity/history/native/STL saves agree");

        var retained=await Load();var id=retained.Snapshot.SelectedObjectId!.Value;Ok(retained.Execute(new RenameObject(id,"temporary")));Ok(retained.Undo());var before=retained.Snapshot;var request=new ExtrudeDownRequest(id);
        Ok(await retained.PreviewExtrudeDownAsync(request));var first=retained.Snapshot.Preview!;Ok(await retained.PreviewExtrudeDownAsync(request with {PlaneHeightMillimeters=5},first.Id));var held=retained.Snapshot;Near(held.Preview!.ExtrudeDown!.ResultVolumeCubicMillimeters,10000);
        Check(first.Id!=held.Preview.Id&&held.Preview.ResultObjectIds.SequenceEqual(first.ResultObjectIds),"Replacement accumulated geometry or lost stable target ID.");
        foreach(var bad in new[]{request with {PlaneHeightMillimeters=double.NaN},request with {PlaneHeightMillimeters=10000001},request with {PlaneHeightMillimeters=20},request with {PlaneHeightMillimeters=30}}){Check((await retained.PreviewExtrudeDownAsync(bad,held.Preview.Id)).Outcome==Outcome.Invalid,"Invalid/no-op plane accepted.");Same(retained,held);}
        foreach(var stage in new[]{"before","Calculating geometry","Ready to preview Extrude down"}){using var cts=new CancellationTokenSource();if(stage=="before")cts.Cancel();Check((await retained.PreviewExtrudeDownAsync(request,held.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage==stage)cts.Cancel();}))).Outcome==Outcome.Cancelled,"Cancellation failed: "+stage);Same(retained,held);}
        Check((await retained.PreviewExtrudeDownAsync(request,first.Id)).Outcome==Outcome.Conflict&&(await retained.PreviewExtrudeDownAsync(request,held.Preview.Id,expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale preview/revision accepted.");
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cts=new CancellationTokenSource()){var job=retained.PreviewExtrudeDownAsync(request,held.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage=="Calculating geometry"){entered.Set();Check(release.Wait(10000),"Worker barrier timeout.");}}));Check(entered.Wait(10000),"Worker did not start.");Check(retained.ApplyPreview(held.Preview.Id).Outcome==Outcome.Busy&&retained.Select(null).Outcome==Outcome.Busy,"Busy guard failed.");cts.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy cancel failed.");Same(retained,held);}
        Check(retained.DiscardPreview(held.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(retained,before);
        Pass("Replacement derives from accepted source; actual no-op/invalid/stale/busy/cancelled requests preserve saved path, clean/redo state and held candidate");

        var member=await Load();var sourceId=member.Snapshot.SelectedObjectId!.Value;Ok(member.Execute(new AddCube()));var siblingId=member.Snapshot.SelectedObjectId!.Value;Ok(member.Execute(new MoveObject(siblingId,new(70,10,10))));Ok(await member.PreviewArrangementAsync(new GroupRequest([sourceId,siblingId],"Extrude pair")));Ok(member.ApplyPreview(member.Snapshot.Preview!.Id));var grouped=member.Snapshot;
        Check((await member.PreviewExtrudeDownAsync(new(sourceId))).Outcome==Outcome.Invalid,"Scene scope changed one group member.");Same(member,grouped);Ok(member.EnterGroup(grouped.Scene.Groups.Single().Id,sourceId));Ok(member.SelectMany([sourceId,siblingId],sourceId));var scoped=member.Snapshot;var group=scoped.Scene.Groups.Single();var sibling=scoped.Scene.Objects.Single(o=>o.Id==siblingId);
        Ok(await member.PreviewExtrudeDownAsync(new(sourceId){MemberGroupId=group.Id}));var p=member.Snapshot.Preview!;Check(ReferenceEquals(p.Scene.Objects.Single(o=>o.Id==siblingId),sibling)&&p.Scene.Groups.Single()==group&&p.SelectedObjectIds.SequenceEqual(scoped.SelectedObjectIds)&&p.EditingGroupId==group.Id,"Member extrusion changed siblings/group/full selection/scope.");Ok(member.ApplyPreview(p.Id));Ok(member.Undo());Check(ReferenceEquals(member.Snapshot.Scene,scoped.Scene),"Member Undo lost exact source.");Ok(member.ExitGroup());var exited=member.Snapshot;Check((await member.PreviewExtrudeDownAsync(new(sourceId){MemberGroupId=group.Id})).Outcome==Outcome.Conflict,"Stale member scope accepted.");Same(member,exited);
        Pass("Member extrusion retains sibling/group/full selection and exact Undo; scene-scope and stale member authority reject");

        var damaged=New();Ok(await damaged.OpenAsync(Path.Combine(root,"fixtures/generated/missing-top.stl")));var damagedBefore=damaged.Snapshot;Check((await damaged.PreviewExtrudeDownAsync(new(damagedBefore.SelectedObjectId!.Value))).Outcome==Outcome.Invalid,"Open source accepted.");Same(damaged,damagedBefore);
        var foreign=await Load();Ok(await foreign.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,2,2))));var foreignBefore=foreign.Snapshot;Check((await foreign.PreviewExtrudeDownAsync(new(foreignBefore.SelectedObjectId!.Value),foreignBefore.Preview!.Id)).Outcome==Outcome.Conflict,"Foreign preview replaced.");Same(foreign,foreignBefore);
        var old=new EditorSession(fault);Ok(await old.OpenAsync(Path.Combine(input,"raised-box.3mf")));var oldBefore=old.Snapshot;Check((await old.PreviewExtrudeDownAsync(new(oldBefore.SelectedObjectId!.Value))).Outcome==Outcome.Failed,"Stale worker accepted.");Same(old,oldBefore);
        var split=New();Ok(split.Execute(new AddCube()));Ok(await split.PreviewAsync(new SplitRequest(split.Snapshot.SelectedObjectId!.Value,new(1,0,0),10)));Near(split.Snapshot.Preview!.Scene.Objects.Sum(Volume),8000);
        var separate=New();Ok(await separate.OpenAsync(Path.Combine(root,".local/evidence/separate-20261020/inputs/two-hollow-boxes.3mf")));Ok(await separate.PreviewSeparateAsync(new(separate.Snapshot.SelectedObjectId!.Value)));Check(separate.Snapshot.Preview!.Separate!.Pieces.Length==2,"Protocol8 separation adjacency failed.");
        Pass("Open/foreign/stale-worker failures are atomic; protocol8 split and cavity-aware separation remain functional");
        File.WriteAllText(Path.Combine(output,"extrude-down-results.json"),JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,protocolVersion=8,kernel="Manifold3.5.4 pinned Hull/BatchBoolean",
            core=new {path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new {path=worker,sha256=Hash(worker)},
            limitations="Vertical world Z material closure only, not sketch extrusion. Covered cavities/undercuts may fill; unobstructed projected holes remain. Existing scene/result/worker budgets apply; no precision or performance campaign."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} Extrude down groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static double Volume(SceneObject o){var p=o.Transform.Apply(o.Mesh.Vertices[0]);return o.Mesh.Triangles.Sum(t=>Dot(o.Transform.Apply(o.Mesh.Vertices[t.A])-p,Vec3.Cross(o.Transform.Apply(o.Mesh.Vertices[t.B])-p,o.Transform.Apply(o.Mesh.Vertices[t.C])-p))/6)*(Dot(o.Transform.X,Vec3.Cross(o.Transform.Y,o.Transform.Z))<0?-1:1);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<=.001,$"Expected {b}, got {a}.");
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Operation changed accepted or held preview state.");
    private static void SameAccepted(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot with {Preview=null}==before,"Preview changed accepted state.");
}
