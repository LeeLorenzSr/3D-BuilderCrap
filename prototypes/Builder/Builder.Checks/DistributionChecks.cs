using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class DistributionChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/distribution-20261022/core");Directory.CreateDirectory(output);var cases=new List<string>();var outputs=new List<object>();var fixture=Path.Combine(root,".local/fixtures/distribution/distribution-assembly.3mf");
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        void Record(string path,string expected)=>outputs.Add(new{path=Path.GetFileName(path),sha256=Hash(path),expected});
        async Task<EditorSession> Make(DistributionAxis axis,double[] widths,double[] centers)
        {
            var session=new EditorSession();for(var i=0;i<widths.Length;i++)
            {
                var width=widths[i];Ok(session.Execute(new AddPrimitive(PrimitiveKind.Cube,new(width,width,width))));var id=session.Snapshot.SelectedObjectId!.Value;
                var center=axis switch{DistributionAxis.X=>new Vec3(centers[i],width/2,width/2),DistributionAxis.Y=>new Vec3(width/2,centers[i],width/2),_=>new Vec3(width/2,width/2,centers[i])};Ok(session.Execute(new MoveObject(id,center)));Ok(session.Execute(new RenameObject(id,"Part "+(i+1))));
                Ok(await session.PreviewMaterialAsync(new([id],new("Part color "+(i+1),new((byte)(70+i*30),(byte)(150-i*20),(byte)(100+i*20))))));Apply(session);
            }
            return session;
        }
        foreach(var axis in Enum.GetValues<DistributionAxis>())
        {
            using var session=await Make(axis,[10,20,30],[5,35,95]);var ids=Ids(session);Ok(session.SelectMany(ids,ids[1]));var accepted=session.Snapshot;
            foreach(var spacing in Enum.GetValues<DistributionSpacing>())
            {
                Ok(await session.PreviewAsync(new DistributionRequest(ids.Reverse().ToImmutableArray(),axis,spacing)));var candidate=session.Snapshot.Preview!.Scene;var expected=spacing==DistributionSpacing.Centers?50d:45d;
                Near(Coordinate(candidate.Objects[1].Bounds.Center,axis),expected);Check(ReferenceEquals(candidate.Objects[0],accepted.Scene.Objects[0])&&ReferenceEquals(candidate.Objects[2],accepted.Scene.Objects[2]),"Endpoint object references changed.");
                var delta=candidate.Objects[1].Transform.Offset-accepted.Scene.Objects[1].Transform.Offset;Near(delta,axis switch{DistributionAxis.X=>new(expected-35,0,0),DistributionAxis.Y=>new(0,expected-35,0),_=>new(0,0,expected-35)});Ownership(candidate,accepted.Scene);
                Apply(session);Check(session.Snapshot.SelectedObjectIds.SequenceEqual(ids)&&session.Snapshot.SelectedObjectId==ids[1],"Distribution lost full/primary selection.");Ok(session.Undo());Check(ReferenceEquals(session.Snapshot.Scene,accepted.Scene),"Distribution was not one exact Scene Undo.");
            }
        }
        Pass("Centers and unequal-width gaps on all world axes translate only interior units; endpoints, other axes, meshes/materials, primary selection and one-step Undo are preserved");

        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(fixture));var group=session.Snapshot.Scene.Groups.Single();var all=Ids(session);var endpoints=session.Snapshot.Scene.Objects.Where(o=>!group.ObjectIds.Contains(o.Id)).OrderBy(o=>o.Bounds.Center.X).ToArray();Ok(session.SelectMany(all,group.ObjectIds[0]));var accepted=session.Snapshot;
            foreach(var spacing in Enum.GetValues<DistributionSpacing>())
            {
                Ok(await session.PreviewArrangementAsync(new DistributionRequest([endpoints[0].Id,group.ObjectIds[0],endpoints[1].Id],DistributionAxis.X,spacing)));var candidate=session.Snapshot.Preview!.Scene;var movement=spacing==DistributionSpacing.Centers?29d:24d;
                foreach(var id in group.ObjectIds){var before=accepted.Scene.Objects.Single(o=>o.Id==id);var after=candidate.Objects.Single(o=>o.Id==id);Near(after.Transform.Offset-before.Transform.Offset,new(movement,0,0));}
                foreach(var endpoint in endpoints)Check(ReferenceEquals(candidate.Objects.Single(o=>o.Id==endpoint.Id),endpoint),"Fixed root anchor changed.");Check(candidate.Groups==accepted.Scene.Groups,"Distribution recreated group metadata.");Ownership(candidate,accepted.Scene);
                Apply(session);var path=Path.Combine(output,spacing==DistributionSpacing.Centers?"root-centers-x.3mf":"root-gaps-x.3mf");Ok(await session.SaveAsync(path));Record(path,spacing==DistributionSpacing.Centers?"Color trio moved +29mm X; root centers5,55,105":"Color trio moved +24mm X; root gaps32mm");Ok(session.Undo());Check(ReferenceEquals(session.Snapshot.Scene,accepted.Scene),"Grouped Undo changed original identity.");
            }
        }
        using(var member=new EditorSession())
        {
            Ok(await member.OpenAsync(fixture));var group=member.Snapshot.Scene.Groups.Single();var green=member.Snapshot.Scene.Objects.Single(o=>o.Name=="Green member").Id;Ok(member.EnterGroup(group.Id,green));
            Ok(await member.PreviewSurfaceMaterialAsync(new(green,2,new("Face highlight",new(255,210,40)),SurfacePaintMode.Triangle){MemberGroupId=group.Id}));Apply(member);Ok(member.SelectMany(group.ObjectIds,green));var accepted=member.Snapshot;
            Check((await member.PreviewArrangementAsync(new DistributionRequest(group.ObjectIds))).Outcome==Outcome.Conflict,"Stale scene-scope distribution borrowed active member authority.");Same(member,accepted);
            Ok(await member.PreviewArrangementAsync(new DistributionRequest(group.ObjectIds,DistributionAxis.X,DistributionSpacing.Centers){MemberGroupId=group.Id}));var candidate=member.Snapshot.Preview!.Scene;
            foreach(var obj in accepted.Scene.Objects){var after=candidate.Objects.Single(o=>o.Id==obj.Id);if(obj.Id==green)Near(after.Transform.Offset-obj.Transform.Offset,new(-2.5,0,0));else Check(ReferenceEquals(after,obj),"Member distribution moved sibling/outsider anchor.");}Ownership(candidate,accepted.Scene);Check(candidate.Groups==accepted.Scene.Groups,"Member distribution changed group identity/name.");
            Apply(member);Check(member.Snapshot.EditingGroupId==group.Id&&member.Snapshot.SelectedObjectIds.SequenceEqual(group.ObjectIds)&&member.Snapshot.SelectedObjectId==green,"Member apply changed full scope/selection.");var path=Path.Combine(output,"member-centers-x.3mf");Ok(await member.SaveAsync(path));Record(path,"Green member moved -2.5mm X; blue/violet and both outside anchors unchanged; one highlighted face retained");
            using var reader=new EditorSession();Ok(await reader.OpenAsync(path));Check(reader.Snapshot.Scene.Groups.Single().Name==group.Name&&Colors(reader.Snapshot.Scene).SequenceEqual(Colors(member.Snapshot.Scene)),"Native roundtrip lost groups/face materials.");Ok(member.Undo());Check(ReferenceEquals(member.Snapshot.Scene,accepted.Scene)&&member.Snapshot.EditingGroupId==accepted.EditingGroupId&&member.Snapshot.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds),"Member Undo lost exact source/scope/full selection.");
        }
        Pass("Whole flat groups distribute rigidly; explicit members distribute independently with outsider isolation, authored face materials, scope/selection and native group/color persistence");

        using(var noop=await Make(DistributionAxis.X,[10,20,30],[5,50,95]))
        {
            var ids=Ids(noop);Ok(noop.Execute(new RenameObject(ids[0],"Temporary rename")));Ok(noop.Undo());var before=noop.Snapshot;Check(before.CanRedo,"No-op test needs redo branch.");Ok(await noop.PreviewArrangementAsync(new DistributionRequest(ids)));Check(ReferenceEquals(noop.Snapshot.Preview!.Scene,before.Scene),"Already distributed units did not reuse exact Scene.");Apply(noop);Same(noop,before);
            foreach(var bad in new[]{new DistributionRequest(ids,(DistributionAxis)99),new DistributionRequest(ids,DistributionAxis.X,(DistributionSpacing)99),new DistributionRequest([ids[0],ids[1]]),new DistributionRequest([ids[0],ids[1],Guid.NewGuid()])}){Check((await noop.PreviewArrangementAsync(bad)).Outcome==Outcome.Invalid,"Invalid distribution accepted.");Same(noop,before);}
        }
        using(var ties=await Make(DistributionAxis.X,[10,20,30],[5,5,95]))
        {var before=ties.Snapshot;Ok(await ties.PreviewArrangementAsync(new DistributionRequest(Ids(ties).Reverse().ToImmutableArray())));Check(ReferenceEquals(ties.Snapshot.Preview!.Scene.Objects[0],before.Scene.Objects[0]),"Tie ordering depended on request order.");Near(ties.Snapshot.Preview.Scene.Objects[1].Bounds.Center.X,50);}
        using(var overlap=await Make(DistributionAxis.X,[10,100,10],[5,35,65]))
        {var before=overlap.Snapshot;var refusal=await overlap.PreviewArrangementAsync(new DistributionRequest(Ids(overlap),DistributionAxis.X,DistributionSpacing.Gaps));Check(refusal.Outcome==Outcome.Invalid&&refusal.Message.Contains("outer",StringComparison.OrdinalIgnoreCase)&&refusal.Message.Contains("Centers",StringComparison.Ordinal),"Negative gap did not refuse with actionable guidance.");Same(overlap,before);Ok(await overlap.PreviewArrangementAsync(new DistributionRequest(Ids(overlap))));Check(ReferenceEquals(overlap.Snapshot.Preview!.Scene,before.Scene),"Center spacing incorrectly collision-packed or rejected overlapping units.");Apply(overlap);Same(overlap,before);}
        Pass("Undefined/insufficient/unknown targets refuse; stable scene-order ties, exact no-op/redo preservation and overlap-permitted Centers versus actionable negative-gap refusal hold");

        using(var session=new EditorSession())
        {
            Ok(await session.OpenAsync(fixture));var ids=Ids(session);Ok(session.SelectMany(ids,ids[0]));Ok(session.Execute(new RenameObject(ids[0],"Temporary rename")));Ok(session.Undo());var accepted=session.Snapshot;var group=accepted.Scene.Groups.Single();
            var centers=new DistributionRequest(ids,DistributionAxis.X,DistributionSpacing.Centers);var gaps=centers with{Spacing=DistributionSpacing.Gaps};Ok(await session.PreviewArrangementAsync(centers));var firstToken=session.Snapshot.Preview!.Id;Ok(await session.PreviewArrangementAsync(gaps,firstToken));var held=session.Snapshot;Check(held.Preview!.Id!=firstToken,"Replacement reused stale preview token.");Near(held.Preview.Scene.GetSelectionUnits(group.ObjectIds).Single().Bounds.Center.X,50);
            Check((await session.PreviewArrangementAsync(centers,firstToken)).Outcome==Outcome.Conflict,"Stale replacement token accepted.");Same(session,held);Check((await session.PreviewArrangementAsync(centers,held.Preview.Id,expectedRevision:accepted.Revision-1)).Outcome==Outcome.Conflict,"Stale document revision accepted.");Same(session,held);Check((await session.PreviewArrangementAsync(centers with{Axis=(DistributionAxis)99},held.Preview.Id)).Outcome==Outcome.Invalid,"Failed replacement accepted.");Same(session,held);
            foreach(var stage in new[]{"Preparing distribution","Distributing objects","Ready to preview"})
            {using var ct=new CancellationTokenSource();Check((await session.PreviewArrangementAsync(centers,held.Preview.Id,ct.Token,new Progress(p=>{if(p.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"Distribution cancellation failed at "+stage);Same(session,held);}
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
            {var job=session.PreviewArrangementAsync(centers,held.Preview.Id,ct.Token,new Progress(p=>{if(p.Stage=="Ready to preview"){entered.Set();Check(release.Wait(10000),"Distribution barrier timeout.");}}));Check(entered.Wait(10000),"No final preview boundary.");Check(session.Select(null).Outcome==Outcome.Busy,"Selection bypassed distribution busy lease.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy replacement cancellation failed.");Same(session,held);}
            Apply(session);Check(session.Snapshot.Revision==accepted.Revision+1&&session.Snapshot.FilePath==accepted.FilePath&&session.Snapshot.IsDirty&&!session.Snapshot.CanRedo,"Distribution commit/history/path semantics failed.");Ok(session.Undo());Check(ReferenceEquals(session.Snapshot.Scene,accepted.Scene)&&!session.Snapshot.IsDirty,"One Undo did not restore clean source.");Ok(session.Redo());var final=Path.Combine(output,"accepted-gaps.3mf");Ok(await session.SaveAsync(final));Record(final,"Accepted root X Gaps;32mm equal gaps, clean saved state after Undo/Redo");
            Ok(await session.PreviewMaterialAsync(new([ids[0]],new("Foreign preview",new(10,20,30)))));var foreign=session.Snapshot;Check((await session.PreviewArrangementAsync(new DistributionRequest(ids),foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Foreign preview was replaced.");Same(session,foreign);
        }
        Pass("Accepted-source replacements issue fresh tokens; stale/foreign/failed/cancelled/busy requests retain prior preview and accepted state; Apply is one branchable Undo with native path/save persistence");
        File.WriteAllText(Path.Combine(output,"distribution-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=fixture,sha256=Hash(fixture)},
            analyticScenes=new{widths=new[]{10,20,30},sourceCenters=new[]{5,35,95},expectedCenters=new[]{5,50,95},expectedGapCenters=new[]{5,45,95},equalGap=25},
            limitations="World-axis bounds distribution only; not collision packing. Whole-group units at scene scope, members inside explicit scope. Stable center/document order, fixed outer units. Centers may overlap; negative gaps refuse. No UI/original-app claim."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"{cases.Count} distribution groups passed. Evidence: {output}");
    }
    private static ImmutableArray<Guid> Ids(EditorSession session)=>session.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
    private static double Coordinate(Vec3 value,DistributionAxis axis)=>axis switch{DistributionAxis.X=>value.X,DistributionAxis.Y=>value.Y,_=>value.Z};
    private static void Ownership(Scene after,Scene before)
    {Check(after.Metadata==before.Metadata&&after.Groups==before.Groups,"Distribution changed metadata/groups.");foreach(var obj in before.Objects){var next=after.Objects.Single(o=>o.Id==obj.Id);Check(next.Name==obj.Name&&ReferenceEquals(next.Mesh,obj.Mesh)&&ReferenceEquals(next.Material,obj.Material)&&ReferenceEquals(next.TriangleMaterials,obj.TriangleMaterials),"Distribution changed object geometry/material ownership.");}}
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private static void Apply(EditorSession session)=>Ok(session.ApplyPreview(session.Snapshot.Preview!.Id));
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Near(double value,double expected)=>Check(Math.Abs(value-expected)<.001,$"Expected {expected}, got {value}.");
    private static void Near(Vec3 value,Vec3 expected){Near(value.X,expected.X);Near(value.Y,expected.Y);Near(value.Z,expected.Z);}
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Distribution changed accepted/held preview state.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
