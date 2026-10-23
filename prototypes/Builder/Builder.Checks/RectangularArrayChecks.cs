using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class RectangularArrayChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/rectangular-array-20261022/core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/fixtures/distribution/distribution-assembly.3mf");var cases=new List<string>();var outputs=new List<object>();
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        void Record(string path,string expected)=>outputs.Add(new{path=Path.GetFileName(path),sha256=Hash(path),expected});
        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(input));var accepted=session.Snapshot;var source=accepted.Scene.Objects[0];
            Ok(await session.PreviewAsync(new RectangularArrayRequest([source.Id],3,2,2,new(25,-30,40))));var preview=session.Snapshot.Preview!;
            Check(((ArrangementRequest)preview.Request).ObjectIds.SequenceEqual([source.Id]),"Array request replaced its source IDs.");Check(preview.ResultObjectIds.Length==11&&preview.SelectedObjectIds.SequenceEqual(preview.ResultObjectIds),"Copies were not the result selection.");
            Check(preview.Scene.Objects.Take(accepted.Scene.Objects.Length).SequenceEqual(accepted.Scene.Objects),"Originals changed.");
            var index=accepted.Scene.Objects.Length;
            for(var z=0;z<2;z++)for(var y=0;y<2;y++)for(var x=0;x<3;x++)
            {if(x==0&&y==0&&z==0)continue;var copy=preview.Scene.Objects[index++];Near(copy.Transform.Offset-source.Transform.Offset,new(25*x,-30*y,40*z));Ownership(copy,source);}
            Check(preview.Scene.Objects.Select(o=>o.Id).Distinct().Count()==preview.Scene.Objects.Length,"Array IDs collided.");
            var candidate=preview.Scene;Apply(session);Check(session.Snapshot.SelectedObjectIds.SequenceEqual(preview.ResultObjectIds),"Apply selected originals.");Ok(session.Undo());Check(ReferenceEquals(session.Snapshot.Scene,accepted.Scene),"Undo lost exact scene.");Ok(session.Redo());Check(ReferenceEquals(session.Snapshot.Scene,candidate),"Redo regenerated candidate IDs.");
        }
        Pass("Signed X/Y/Z grid steps repeat accepted objects with deterministic cells, shared geometry/materials, copy-only selection and exact Apply/Undo/Redo identity");
        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(input));var group=session.Snapshot.Scene.Groups.Single();var source=session.Snapshot.Scene;var request=new RectangularArrayRequest([source.Objects[0].Id,group.ObjectIds[0]],2,2,1,new(45,55,0));
            Ok(await session.PreviewArrangementAsync(request));var preview=session.Snapshot.Preview!;Check(preview.ResultObjectIds.Length==12&&preview.Scene.Groups.Length==4,"Whole group was not copied per cell.");
            for(var cell=0;cell<3;cell++)
            {var clone=preview.Scene.Groups[cell+1];Check(clone.Id!=group.Id&&clone.ObjectIds.Length==3,"Copied group identity invalid.");foreach(var id in clone.ObjectIds){var copy=preview.Scene.Objects.Single(o=>o.Id==id);var original=source.Objects.Single(o=>copy.Name==o.Name+" copy");Ownership(copy,original);}}
            Check(ReferenceEquals(preview.Scene.Groups[0],group),"Original group changed.");Apply(session);var path=Path.Combine(output,"group-grid.3mf");Ok(await session.SaveAsync(path));Record(path,"Original Left anchor and Color trio repeat as a set across3 new cells; each fresh group retains3 colored leaves at steps(45,55,0)");using var reopened=new EditorSession();Ok(await reopened.OpenAsync(path));Check(reopened.Snapshot.Scene.Groups.Length==4&&reopened.Snapshot.Scene.Objects.Length==17,"Group grid failed native roundtrip.");
        }
        using(var member=new EditorSession())
        {
            Ok(await member.OpenAsync(input));var group=member.Snapshot.Scene.Groups.Single();var id=group.ObjectIds[1];Ok(member.EnterGroup(group.Id,id));Ok(await member.PreviewSurfaceMaterialAsync(new(id,2,new("Face highlight",new(255,210,40)),SurfacePaintMode.Triangle){MemberGroupId=group.Id}));Apply(member);var before=member.Snapshot;var source=before.Scene.Objects.Single(o=>o.Id==id);
            Check((await member.PreviewArrangementAsync(new RectangularArrayRequest([id],2,1,1,new(20,0,0)))).Outcome==Outcome.Conflict,"Array borrowed later member scope.");Same(member,before);
            Ok(await member.PreviewArrangementAsync(new RectangularArrayRequest([id],2,1,1,new(20,0,0)){MemberGroupId=group.Id}));var preview=member.Snapshot.Preview!;var copy=preview.Scene.Objects.Last();Ownership(copy,source);Check(preview.Scene.Groups.Single().ObjectIds.SequenceEqual(group.ObjectIds.Add(copy.Id)),"Member copy did not append existing group.");foreach(var obj in before.Scene.Objects)Check(ReferenceEquals(preview.Scene.Objects.Single(o=>o.Id==obj.Id),obj),"Member array changed source/context.");Apply(member);Check(member.Snapshot.EditingGroupId==group.Id&&member.Snapshot.SelectedObjectId==copy.Id,"Member scope/copy selection lost.");var path=Path.Combine(output,"member-grid.3mf");Ok(await member.SaveAsync(path));Record(path,"One Green member copy at+20mmX joins original group, including triangle2 Highlight; outside anchors unchanged");Ok(member.Undo());Check(ReferenceEquals(member.Snapshot.Scene,before.Scene)&&member.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Member Undo lost scope/source selection.");
        }
        Pass("Whole source groups gain fresh groups per cell; explicit member copies append existing group with authored face materials, context and native save ownership");
        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(input));var id=session.Snapshot.Scene.Objects[0].Id;Ok(session.Execute(new RenameObject(id,"Temporary")));Ok(session.Undo());Ok(session.Select(null));var before=session.Snapshot;
            var noop=new RectangularArrayRequest([id],1,1,1,new());Ok(await session.PreviewArrangementAsync(noop));Check(ReferenceEquals(session.Snapshot.Preview!.Scene,before.Scene)&&session.Snapshot.Preview.SelectedObjectIds.IsEmpty&&session.Snapshot.Preview.SelectedObjectId is null,"No-op changed scene/empty selection.");Apply(session);Same(session,before);
            foreach(var bad in new[]{noop with{CountX=0},noop with{CountY=-1},noop with{CountX=2},noop with{SpacingMillimeters=new(double.NaN,0,0)},noop with{CountX=int.MaxValue,CountY=int.MaxValue,CountZ=int.MaxValue,SpacingMillimeters=new(1,1,1)},noop with{CountX=1000,SpacingMillimeters=new(20,0,0)},noop with{CountX=2,SpacingMillimeters=new(10_000_001,0,0)}})
            {Check((await session.PreviewArrangementAsync(bad)).Outcome==Outcome.Invalid,"Invalid count/step/limit accepted.");Same(session,before);}
            Check((await session.PreviewArrangementAsync(noop with{ObjectIds=[Guid.NewGuid()]})).Outcome==Outcome.Invalid,"Unknown array target accepted.");Same(session,before);
            Ok(session.Execute(new AddPrimitive(PrimitiveKind.Sphere,new(20,20,20))));var sphere=session.Snapshot.SelectedObjectId!.Value;var dense=session.Snapshot;Check((await session.PreviewArrangementAsync(new RectangularArrayRequest([sphere],900,1,1,new(30,0,0)))).Outcome==Outcome.Invalid,"Expanded geometry budget not enforced.");Same(session,dense);
        }
        Pass("Invalid counts, nonfinite/zero active steps, overflow, expanded budgets and out-of-range bounds refuse; original-cell-only preview preserves exact empty selection, clean state and Redo");
        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(input));var ids=ImmutableArray.Create(session.Snapshot.Scene.Objects[0].Id);Ok(session.SelectMany(ids,ids[0]));var accepted=session.Snapshot;var request=new RectangularArrayRequest(ids,2,1,1,new(25,0,0));
            Ok(await session.PreviewArrangementAsync(request));var old=session.Snapshot.Preview!;Ok(await session.PreviewArrangementAsync(request with{CountX=3},old.Id));var held=session.Snapshot;Check(held.Preview!.Id!=old.Id&&held.Preview.Scene.Objects.Length==accepted.Scene.Objects.Length+2,"Replacement accumulated old copies or reused token.");
            foreach(var attempt in new[]{await session.PreviewArrangementAsync(request,old.Id),await session.PreviewArrangementAsync(request,held.Preview.Id,expectedRevision:accepted.Revision-1),await session.PreviewArrangementAsync(request with{ObjectIds=[accepted.Scene.Objects[1].Id]},held.Preview.Id)}){Check(attempt.Outcome==Outcome.Conflict,"Stale/foreign targets accepted.");Same(session,held);}
            Check((await session.PreviewArrangementAsync(request with{CountX=0},held.Preview.Id)).Outcome==Outcome.Invalid,"Failed replacement succeeded.");Same(session,held);
            foreach(var stage in new[]{"Creating array copies","Ready to preview"})
            {using var ct=new CancellationTokenSource();Check((await session.PreviewArrangementAsync(request,held.Preview.Id,ct.Token,new Progress(p=>{if(p.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"Cancellation failed at "+stage);Same(session,held);}
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
            {var job=session.PreviewArrangementAsync(request,held.Preview.Id,ct.Token,new Progress(p=>{if(p.Stage=="Ready to preview"){entered.Set();Check(release.Wait(10000),"Array barrier timeout.");}}));Check(entered.Wait(10000),"Array preparation did not enter barrier.");Check(session.Select(null).Outcome==Outcome.Busy,"Busy array selection allowed.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy array cancellation failed.");Same(session,held);}
            var candidate=held.Preview.Scene;Apply(session);Check(session.Snapshot.Revision==accepted.Revision+1&&session.Snapshot.IsDirty&&session.Snapshot.FilePath==accepted.FilePath,"Array Apply history/path incorrect.");Ok(session.Undo());Check(ReferenceEquals(session.Snapshot.Scene,accepted.Scene)&&session.Snapshot.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds),"Array Undo lost exact selection/source.");Ok(session.Redo());Check(ReferenceEquals(session.Snapshot.Scene,candidate),"Array Redo changed IDs.");
        }
        Pass("Exact-token replacement regenerates from accepted source; stale targets/revision, failures, busy edits and cancellation preserve held preview; acceptance creates one path-preserving Undo step");
        File.WriteAllText(Path.Combine(output,"rectangular-array-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=input,sha256=Hash(input)},limitations="World-axis signed translation steps, not physical bounds gaps or collision packing. Overlapping copies allowed. Scene/native resource limits apply. No UI/native behavior claim."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"{cases.Count} rectangular array groups passed. Evidence: {output}");
    }
    private static void Ownership(SceneObject copy,SceneObject original)=>Check(ReferenceEquals(copy.Mesh,original.Mesh)&&ReferenceEquals(copy.Material,original.Material)&&ReferenceEquals(copy.TriangleMaterials,original.TriangleMaterials),"Array lost mesh/material/face map sharing.");
    private static void Apply(EditorSession session)=>Ok(session.ApplyPreview(session.Snapshot.Preview!.Id));
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Array mutated accepted/prior preview state.");
    private static void Near(Vec3 actual,Vec3 expected)=>Check(Math.Abs(actual.X-expected.X)<.001&&Math.Abs(actual.Y-expected.Y)<.001&&Math.Abs(actual.Z-expected.Z)<.001,$"Expected{expected},got{actual}");
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
