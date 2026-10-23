using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ViewControlsChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/view-controls-20261021/core");Directory.CreateDirectory(output);var cases=new List<string>();
        var input=Path.Combine(root,".local/evidence/surface-paint-20261021/inputs/surface-group.3mf");
        void Pass(string value){cases.Add(value);Console.WriteLine("PASS "+value);}
        using var e=new EditorSession();var presentation=e.Presentation;var document=e.Snapshot;
        Check(presentation.Snapshot==new PresentationSnapshot(new(true,true,false,false),0),"View defaults differ.");
        for(var bits=0;bits<16;bits++)
        {var value=new ViewSettings((bits&1)!=0,(bits&2)!=0,(bits&4)!=0,(bits&8)!=0);Ok(presentation.Update(value));Check(presentation.Snapshot.Settings==value,"A view flag was not retained.");Same(e,document);var before=presentation.Snapshot;Ok(presentation.Update(value));Check(presentation.Snapshot==before,"Equal view update changed presentation revision.");}
        var captured=presentation.Snapshot;Check(presentation.Update(null!).Outcome==Outcome.Invalid,"Null view settings accepted.");Check(presentation.Snapshot==captured,"Invalid update changed presentation.");Check(presentation.Reset(captured.Revision-1).Outcome==Outcome.Conflict,"Stale presentation revision accepted.");Check(presentation.Snapshot==captured,"Stale Reset changed presentation.");
        var results=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>Task.Run(()=>presentation.Update(new(false,false,false,false),captured.Revision))));Check(results.Count(r=>r.Succeeded)==1&&results.Count(r=>r.Outcome==Outcome.Conflict)==15,"Atomic presentation revision did not resolve concurrent callers.");Same(e,document);Ok(presentation.Reset());var reset=presentation.Snapshot;Ok(presentation.Reset());Check(presentation.Snapshot==reset&&reset.Settings==ViewSettings.Default,"Reset is not a default/no-op.");using(var other=new EditorSession()){Ok(presentation.Update(new(false,false,true,true)));Check(other.Presentation.Snapshot.Settings==ViewSettings.Default,"View settings leaked between sessions.");}
        Pass("All flags/defaults/reset, null/stale no-op safety and concurrent presentation revisions work independently of document and other sessions");

        Ok(await e.OpenAsync(input));var original=e.Snapshot;var group=original.Scene.Groups.Single();var target=group.ObjectIds[0];Ok(presentation.Update(new(false,false,true,true)));var accepted=e.Snapshot;Same(e,original);
        Ok(await e.PreviewTransformAsync(new MoveTransform(target,new(3,0,0))));var preview=e.Snapshot;var token=preview.Preview!.Id;Ok(presentation.Update(new(true,false,false,true)));Same(e,preview);Check(ReferenceEquals(e.Snapshot.Preview,preview.Preview),"View update replaced geometry preview.");var settings=presentation.Snapshot;
        Ok(e.ApplyPreview(token));Check(presentation.Snapshot==settings,"Apply overwrote presentation.");Ok(e.Undo());Check(ReferenceEquals(e.Snapshot.Scene,accepted.Scene)&&presentation.Snapshot==settings,"Undo overwrote presentation/source.");Ok(e.Redo());Check(presentation.Snapshot==settings,"Redo overwrote presentation.");
        var saved=Path.Combine(output,"view-independent-scene.3mf");Ok(await e.SaveAsync(saved));Check(presentation.Snapshot==settings,"Save reset presentation.");using(var read=new EditorSession()){Ok(await read.OpenAsync(saved));Check(read.Presentation.Snapshot.Settings==ViewSettings.Default,"Native scene persisted view flags.");Check(Colors(read.Snapshot.Scene).SequenceEqual(Colors(e.Snapshot.Scene)),"Colors-off mutated authored materials.");}
        Ok((await e.TransitionAsync(new NewDocumentTransition(e.Snapshot.Revision))).Result);Check(presentation.Snapshot==settings,"New reset session view flags.");Ok(await e.OpenAsync(saved));Check(presentation.Snapshot==settings,"Open reset session view flags.");
        Pass("Display updates preserve exact accepted/preview state; Apply/Undo/Redo/New/Open/Save retain session settings and native files keep authored colors without view flags");

        var beforeBusy=e.Snapshot;using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=e.ExportPlyAsync(Path.Combine(output,"cancelled-busy.ply"),ModelUnit.Millimeters,ct.Token,new Progress(p=>{if(p.Stage=="Ready to export PLY"){entered.Set();Check(release.Wait(10000),"Busy boundary timeout.");}}));Check(entered.Wait(10000),"Export did not reach boundary.");var busy=e.Snapshot;Check(busy.IsBusy,"Document lease not active.");Ok(presentation.Update(new(false,true,true,false)));Same(e,busy);Check(e.Select(null).Outcome==Outcome.Busy,"Document edit should remain guarded while presentation updates.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy export cancellation failed.");}
        Same(e,beforeBusy);Check(presentation.Snapshot.Settings==new ViewSettings(false,true,true,false),"Job completion overwrote concurrent presentation.");
        Pass("Presentation remains available during a document busy lease and cancellation; geometry completion does not overwrite independently changed settings");

        var directory=Path.Combine(output,"recovery-store");Ok(e.ConfigureRecovery(directory));Ok(e.Execute(new RenameObject(e.Snapshot.Scene.Objects[0].Id,"Recover accepted view-independent work")));var beforeCheckpoint=e.Snapshot;var viewBeforeCheckpoint=presentation.Snapshot;Ok(await e.CheckpointRecoveryAsync());Same(e,beforeCheckpoint);Check(presentation.Snapshot==viewBeforeCheckpoint,"Checkpoint changed presentation.");
        var checkpoint=Directory.GetFiles(directory,"*.recovery.json").Single();using(var json=JsonDocument.Parse(File.ReadAllBytes(checkpoint))){var payload=json.RootElement.GetProperty("Document");Check(!payload.TryGetProperty("Presentation",out _)&&!payload.TryGetProperty("ViewSettings",out _),"Recovery persisted view settings.");}
        e.Dispose();using var receiver=new EditorSession();Ok(receiver.ConfigureRecovery(directory));var candidate=(await receiver.GetRecoveryCandidatesAsync()).Single();var receiverView=receiver.Presentation.Snapshot;Ok((await receiver.RestoreRecoveryAsync(candidate)).Result);Check(receiver.Presentation.Snapshot==receiverView&&receiverView.Settings==ViewSettings.Default,"Restore adopted another session's display flags.");Check(receiver.Snapshot.Scene.Objects[0].Id==beforeCheckpoint.Scene.Objects[0].Id,"Recovery failed underlying document identity.");
        Pass("Accepted work recovery excludes presentation; restored document retains stable identity while the receiving session keeps its own default view settings");
        File.WriteAllText(Path.Combine(output,"view-controls-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=input,sha256=Hash(input)},output=new{path=saved,sha256=Hash(saved)},
            defaults=ViewSettings.Default,limitations="Core stores flags only; viewport rendering and UI behavior are verified separately. Presentation has its own lock/revision, survives document transitions/history within a session, and is excluded from files/recovery. Picking/geometry are unchanged. No rendering/original-app claim."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"{cases.Count} view control groups passed. Evidence: {output}");
    }
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"View action changed document snapshot.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
