using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class CenterOnPlateChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/center-on-plate-20261022/core");Directory.CreateDirectory(output);var input=Path.Combine(root,".local/fixtures/distribution/distribution-assembly.3mf");var cases=new List<string>();var outputs=new List<object>();
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task Save(EditorSession session,string file,Vec3 expectedDelta,string expected)
        {var path=Path.Combine(output,file);Ok(await session.SaveAsync(path));outputs.Add(new{path=file,sha256=Hash(path),expectedDelta,expected});using var reader=new EditorSession();Ok(await reader.OpenAsync(path));Check(reader.Snapshot.Scene.Objects.Length==session.Snapshot.Scene.Objects.Length&&reader.Snapshot.Scene.Groups.Length==session.Snapshot.Scene.Groups.Length,"Native count/group loss.");Near(reader.Snapshot.Scene.Bounds.Min,session.Snapshot.Scene.Bounds.Min);Near(reader.Snapshot.Scene.Bounds.Max,session.Snapshot.Scene.Bounds.Max);Check(Colors(reader.Snapshot.Scene).SequenceEqual(Colors(session.Snapshot.Scene)),"Native color loss.");}
        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(input));var ids=Ids(session);Ok(await session.PreviewTransformAsync(new RotateTransform(ids[0],new(17,23,31)){ObjectIds=ids}));Apply(session);Ok(await session.PreviewTransformAsync(new MoveTransform(ids[0],new(45,-22,18)){ObjectIds=ids}));Apply(session);Ok(session.SelectMany(ids,ids[3]));var before=session.Snapshot;var bounds=before.Scene.Bounds;
            var request=new CenterOnPlateRequest(ids);Ok(await session.PreviewAsync(request));var candidate=session.Snapshot.Preview!;var delta=new Vec3(-bounds.Center.X,-bounds.Center.Y,0);Translated(candidate.Scene,before.Scene,ids,delta);Near(candidate.Scene.Bounds.Center.X,0);Near(candidate.Scene.Bounds.Center.Y,0);Near(candidate.Scene.Bounds.Min.Z,bounds.Min.Z);Check(candidate.SelectedObjectIds.SequenceEqual(ids)&&candidate.SelectedObjectId==ids[3],"Centering lost primary/full selection.");
            Ok(await session.PreviewArrangementAsync(request with{PlaceOnBed=true},candidate.Id));candidate=session.Snapshot.Preview!;delta=new(-bounds.Center.X,-bounds.Center.Y,-bounds.Min.Z);Translated(candidate.Scene,before.Scene,ids,delta);Near(candidate.Scene.Bounds.Min.Z,0);var scene=candidate.Scene;Apply(session);Ok(session.Undo());Check(ReferenceEquals(session.Snapshot.Scene,before.Scene),"Center was not one exact Undo.");Ok(session.Redo());Check(ReferenceEquals(session.Snapshot.Scene,scene),"Redo changed identities.");await Save(session,"rotated-set-centered-bed.3mf",delta,"Entire rotated and translated5-leaf set receives one shared translation; XY bounds center0, lowestZ0; group/material ownership preserved");
        }
        foreach(var height in new[]{-4d,30d})
        {
            using var session=new EditorSession();Ok(session.Execute(new AddCube()));var id=session.Snapshot.SelectedObjectId!.Value;Ok(session.Execute(new MoveObject(id,new(50,35,height))));var before=session.Snapshot;Ok(await session.PreviewArrangementAsync(new CenterOnPlateRequest([id],true)));var result=session.Snapshot.Preview!.Scene;Near(result.Bounds.Min.Z,0);Translated(result,before.Scene,[id],new(-50,-35,-before.Scene.Bounds.Min.Z));
        }
        Pass("Aggregate rotated/translated sets center rigidly on XY; optional bed placement lowers raised work or lifts below-bed work, preserving mesh/colors, offsets, native save and exact Undo/Redo");
        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(input));var before=session.Snapshot;var group=before.Scene.Groups.Single();var targetIds=group.ObjectIds;var targetBounds=Bounds(before.Scene,targetIds);Ok(await session.PreviewArrangementAsync(new CenterOnPlateRequest([targetIds[0]])));var candidate=session.Snapshot.Preview!;Translated(candidate.Scene,before.Scene,targetIds,new(-targetBounds.Center.X,-targetBounds.Center.Y,0));Check(candidate.ResultObjectIds.SequenceEqual(targetIds),"Root target did not expand whole group.");Check(ReferenceEquals(candidate.Scene.Groups[0],group),"Root group changed.");
        }
        using(var member=new EditorSession())
        {
            Ok(await member.OpenAsync(input));var group=member.Snapshot.Scene.Groups.Single();var id=group.ObjectIds[1];Ok(member.EnterGroup(group.Id,id));Ok(await member.PreviewSurfaceMaterialAsync(new(id,2,new("Face highlight",new(255,210,40)),SurfacePaintMode.Triangle){MemberGroupId=group.Id}));Apply(member);var before=member.Snapshot;var source=before.Scene.Objects.Single(o=>o.Id==id);Check((await member.PreviewArrangementAsync(new CenterOnPlateRequest([id]))).Outcome==Outcome.Conflict,"Center borrowed active member authority.");Same(member,before);
            Ok(await member.PreviewArrangementAsync(new CenterOnPlateRequest([id]){MemberGroupId=group.Id}));var delta=new Vec3(-source.Bounds.Center.X,-source.Bounds.Center.Y,0);Translated(member.Snapshot.Preview!.Scene,before.Scene,[id],delta);Apply(member);Check(member.Snapshot.EditingGroupId==group.Id&&member.Snapshot.SelectedObjectId==id,"Member scope lost.");await Save(member,"member-centered.3mf",delta,"Only Green member centeredXY; group remains intact, face highlight retained and anchors/siblings unchanged");Ok(member.Undo());Check(ReferenceEquals(member.Snapshot.Scene,before.Scene),"Member Undo lost exact original.");
        }
        Pass("Root group targets expand rigidly; explicit members alone center while scope, outsider references, authored face colors and group metadata remain intact");
        using(var session=new EditorSession())
        {
            Ok(session.Execute(new AddCube()));var id=session.Snapshot.SelectedObjectId!.Value;Ok(session.Execute(new MoveObject(id,new(0,0,10))));Ok(session.Execute(new RenameObject(id,"Temporary")));Ok(session.Undo());Ok(session.Select(null));var before=session.Snapshot;var request=new CenterOnPlateRequest([id],true);Ok(await session.PreviewArrangementAsync(request));var preview=session.Snapshot.Preview!;Check(ReferenceEquals(preview.Scene,before.Scene)&&preview.SelectedObjectId is null&&preview.SelectedObjectIds.IsEmpty,"No-op altered scene/selection.");Apply(session);Same(session,before);
            Ok(session.Execute(new MoveObject(id,new(.0005,-.0005,10.0005))));Ok(session.Execute(new RenameObject(id,"Near-zero temporary")));Ok(session.Undo());var near=session.Snapshot;
            Ok(await session.PreviewArrangementAsync(request));Check(ReferenceEquals(session.Snapshot.Preview!.Scene,near.Scene)&&session.Snapshot.Preview.SelectedObjectIds.SequenceEqual(near.SelectedObjectIds)&&session.Snapshot.Preview.SelectedObjectId==near.SelectedObjectId,"Sub-resolution centering changed scene/selection.");Apply(session);Same(session,near);before=near;
            foreach(var bad in new[]{new CenterOnPlateRequest([]),new CenterOnPlateRequest(default),new CenterOnPlateRequest([Guid.NewGuid()])}){Check((await session.PreviewArrangementAsync(bad)).Outcome==Outcome.Invalid,"Invalid target accepted.");Same(session,before);}
        }
        Pass("Centered-within0.001mm no-op preserves clean/dirty identity, empty selection, primary and Redo; empty/default/unknown target collections refuse without mutation");
        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(input));var ids=Ids(session);var before=session.Snapshot;var request=new CenterOnPlateRequest(ids);Ok(await session.PreviewArrangementAsync(request));var first=session.Snapshot.Preview!;Ok(await session.PreviewArrangementAsync(request with{PlaceOnBed=true},first.Id));var held=session.Snapshot;Check(held.Preview!.Id!=first.Id,"Replacement token not refreshed.");Translated(held.Preview.Scene,before.Scene,ids,new(-before.Scene.Bounds.Center.X,-before.Scene.Bounds.Center.Y,0));
            Check((await session.PreviewArrangementAsync(request,first.Id)).Outcome==Outcome.Conflict,"Stale token accepted.");Same(session,held);Check((await session.PreviewArrangementAsync(request,held.Preview.Id,expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale revision accepted.");Same(session,held);Check((await session.PreviewArrangementAsync(request with{ObjectIds=[ids[0]]},held.Preview.Id)).Outcome==Outcome.Conflict,"Changed targets accepted.");Same(session,held);
            foreach(var stage in new[]{"Measuring center bounds","Centering objects","Ready to preview"}){using var ct=new CancellationTokenSource();Check((await session.PreviewArrangementAsync(request,held.Preview.Id,ct.Token,new Progress(p=>{if(p.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"Cancellation failed at "+stage);Same(session,held);}
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
            {var task=session.PreviewArrangementAsync(request,held.Preview.Id,ct.Token,new Progress(p=>{if(p.Stage=="Ready to preview"){entered.Set();Check(release.Wait(10000),"Center barrier timeout.");}}));Check(entered.Wait(10000),"Center barrier not reached.");Check(session.Select(null).Outcome==Outcome.Busy,"Busy selection allowed.");ct.Cancel();release.Set();Check((await task).Outcome==Outcome.Cancelled,"Busy cancel failed.");Same(session,held);}
            Check(session.DiscardPreview(held.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(session,before);Ok(await session.PreviewArrangementAsync(request));Apply(session);Check(session.Snapshot.Revision==before.Revision+1&&session.Snapshot.FilePath==before.FilePath,"Apply failed history/path policy.");Ok(session.Undo());Check(ReferenceEquals(session.Snapshot.Scene,before.Scene)&&!session.Snapshot.IsDirty,"Undo failed clean document restore.");
        }
        Pass("Accepted-source replacement, discard, stale guards and preparation/final cancellation retain accepted scene and held preview; busy edits refuse and Apply preserves native path with one Undo");
        File.WriteAllText(Path.Combine(output,"center-on-plate-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=input,sha256=Hash(input)},limitations="WorldXY bounds centering at origin, optional aggregate lowestZ0; not collision packing or individual per-object centering. No mesh, cavity, color or geometry topology changes. No UI/native claim."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"{cases.Count} center on plate groups passed. Evidence: {output}");
    }
    private static Bounds3 Bounds(Scene scene,ImmutableArray<Guid> ids)=>Bounds3.From(scene.Objects.Where(o=>ids.Contains(o.Id)).SelectMany(o=>o.Mesh.Vertices.Select(o.Transform.Apply)));
    private static ImmutableArray<Guid> Ids(EditorSession s)=>s.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
    private static void Translated(Scene after,Scene before,ImmutableArray<Guid> ids,Vec3 delta)
    {Check(after.Groups==before.Groups&&after.Metadata==before.Metadata&&after.Objects.Length==before.Objects.Length,"Center changed document ownership.");foreach(var source in before.Objects){var result=after.Objects.Single(o=>o.Id==source.Id);if(!ids.Contains(source.Id)){Check(ReferenceEquals(source,result),"Center changed outsider.");continue;}Check(source.Name==result.Name&&ReferenceEquals(source.Mesh,result.Mesh)&&ReferenceEquals(source.Material,result.Material)&&ReferenceEquals(source.TriangleMaterials,result.TriangleMaterials),"Center changed mesh/material ownership.");for(var i=0;i<source.Mesh.Vertices.Length;i++)Near(result.Transform.Apply(result.Mesh.Vertices[i]),source.Transform.Apply(source.Mesh.Vertices[i])+delta);}}
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private static void Apply(EditorSession s)=>Ok(s.ApplyPreview(s.Snapshot.Preview!.Id));
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<.001,$"Expected {b}, got {a}.");
    private static void Near(Vec3 a,Vec3 b){Near(a.X,b.X);Near(a.Y,b.Y);Near(a.Z,b.Z);}
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Center changed accepted/prior preview state.");
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
