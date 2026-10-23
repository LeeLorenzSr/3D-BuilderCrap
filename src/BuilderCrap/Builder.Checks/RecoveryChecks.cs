using Builder.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class RecoveryChecks
{
    public static async Task Child(string root,string? evidenceRoot)
    {
        var output=evidenceRoot??throw new ArgumentException("Child recovery evidence root is required.");Directory.CreateDirectory(output);
        using var session=new EditorSession();Ok(session.ConfigureRecovery(Path.Combine(output,"store")));Ok(session.Execute(new AddCube()));Ok(session.Execute(new RenameObject(session.Snapshot.SelectedObjectId!.Value,"Crash child unsaved")));Ok(await session.CheckpointRecoveryAsync());
        File.WriteAllText(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new{pid=Environment.ProcessId,objectId=session.Snapshot.SelectedObjectId,revision=session.Snapshot.Revision}));
        Console.WriteLine("RECOVERY_CHILD_READY");await Task.Delay(Timeout.Infinite);
    }
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/recovery-20261021/core");Directory.CreateDirectory(output);var cases=new List<string>();var records=new List<object>();
        var fixture=Path.Combine(root,".local/evidence/surface-paint-20261021/inputs/surface-group.3mf");
        EditorSession Configured(string directory){var e=new EditorSession();Ok(e.ConfigureRecovery(directory));return e;}
        void Pass(string message){cases.Add(message);Console.WriteLine("PASS "+message);}
        string Store(string name)=>Path.Combine(output,name,"store");
        static string OnlyFile(string directory)=>Directory.GetFiles(directory,"*.recovery.json").Single();
        async Task<RecoveryCandidate> One(EditorSession session){var values=await session.GetRecoveryCandidatesAsync();Check(values.Length==1,"Expected exactly one abandoned recovery.");return values[0];}

        {
            var directory=Store("accepted-native");using var owner=Configured(directory);Ok(await owner.OpenAsync(fixture));Ok(owner.UpdateModelMetadata(new("Recovered model","Designer","Line1\nLine2"),owner.Snapshot.Revision));
            var native=Path.Combine(output,"accepted-native","original.3mf");Ok(await owner.SaveAsync(native));var group=owner.Snapshot.Scene.Groups.Single();var member=group.ObjectIds[0];Ok(owner.EnterGroup(group.Id,member));Ok(owner.Execute(new RenameObject(member,"Unsaved member"){MemberGroupId=group.Id}));
            var accepted=owner.Snapshot;Ok(await owner.PreviewTransformAsync(new MoveTransform(member,new(9,0,0)){MemberGroupId=group.Id}));var held=owner.Snapshot;Ok(await owner.CheckpointRecoveryAsync());Same(owner,held);
            using var observer=Configured(directory);Check((await observer.GetRecoveryCandidatesAsync()).IsEmpty,"Active session was discoverable.");owner.Dispose();var candidate=await One(observer);Check(candidate.NativePath==native&&candidate.Title=="Recovered model"&&candidate.ObjectCount==2,"Recovery summary lost attribution/path.");
            Ok((await observer.RestoreRecoveryAsync(candidate)).Result);Restored(observer.Snapshot,accepted);Check(observer.Snapshot.IsDirty&&!observer.Snapshot.CanUndo&&!observer.Snapshot.CanRedo&&observer.Snapshot.Preview is null,"Restore did not start fresh dirty history without preview.");
            using var next=Configured(directory);Check((await next.GetRecoveryCandidatesAsync()).IsEmpty,"Restored adopted checkpoint was not actively owned.");observer.Dispose();var secondCrash=await One(next);Check(secondCrash.Fingerprint==candidate.Fingerprint,"Restore rewrote/deleted the only crash checkpoint before next timer.");Ok((await next.RestoreRecoveryAsync(secondCrash)).Result);Restored(next.Snapshot,accepted);
            var restoredScene=next.Snapshot.Scene;Ok(next.Execute(new RenameObject(member,"After restore"){MemberGroupId=group.Id}));Ok(next.Undo());Check(ReferenceEquals(next.Snapshot.Scene,restoredScene),"Normal recovered editing Undo failed.");
            var saved=Path.Combine(output,"restored-painted-group.3mf");Ok(await next.SaveAsync(saved));Check(!next.Snapshot.IsDirty&&next.Snapshot.FilePath==saved,"Recovered Save failed clean/native state.");records.Add(new{path=saved,sha256=Hash(saved)});next.Dispose();using var scan=Configured(directory);Check((await scan.GetRecoveryCandidatesAsync()).IsEmpty,"Successful Save left stale recoverable work.");
            Pass("Accepted native work checkpoints through preview without document mutation; stable IDs/groups/colors/metadata/selection/scope/path restore dirty, survive immediate second crash and support normal Undo/Save");
        }
        {
            var directory=Store("empty-untitled");using var owner=Configured(directory);Ok(owner.Execute(new AddCube()));Ok(await owner.CheckpointRecoveryAsync());Ok(owner.Execute(new DeleteObject(owner.Snapshot.SelectedObjectId!.Value)));var empty=owner.Snapshot;Ok(await owner.CheckpointRecoveryAsync());Same(owner,empty);owner.Dispose();
            using var receiver=Configured(directory);var candidate=await One(receiver);Check(candidate.ObjectCount==0&&candidate.NativePath is null,"Empty untitled checkpoint information lost.");Ok((await receiver.RestoreRecoveryAsync(candidate)).Result);Check(receiver.Snapshot.Scene.Objects.IsEmpty&&receiver.Snapshot.IsDirty&&receiver.Snapshot.FilePath is null,"Empty dirty scene was not restored.");
            var before=receiver.Snapshot;Check((await receiver.SaveAsync(Path.Combine(output,"empty-not-native.3mf"))).Outcome==Outcome.Invalid,"Invalid empty native3MF saved.");Same(receiver,before);Ok((await receiver.TransitionAsync(new NewDocumentTransition(before.Revision,DirtyDecision.Discard))).Result);receiver.Dispose();
            using var scan=Configured(directory);Check((await scan.GetRecoveryCandidatesAsync()).IsEmpty,"Discarded empty recovered work resurrected.");
            var corruptId=Guid.NewGuid();var corrupt=Path.Combine(directory,corruptId.ToString("N")+".recovery.json");File.WriteAllText(corrupt,"{broken");var damaged=await One(scan);Check(damaged.Problem is not null&&damaged.Fingerprint.Length==64,"Damaged recovery disappeared or lacks exact discard token.");before=scan.Snapshot;Check((await scan.RestoreRecoveryAsync(damaged)).Result.Outcome==Outcome.Invalid,"Damaged recovery restored.");Same(scan,before);Ok(await scan.DiscardRecoveryAsync(damaged));Check(!File.Exists(corrupt),"Damaged recovery was not explicitly retired.");Check((await scan.DiscardRecoveryAsync(damaged)).Outcome==Outcome.Conflict,"Stale discard did not conflict.");
            Pass("Untitled delete-all scene is recoverable despite native empty-save refusal; explicit New discard retires it and damaged bounded JSON remains visible/exactly discardable");
        }
        {
            var directory=Store("atomic-checkpoint");using var owner=Configured(directory);Ok(owner.Execute(new AddCube()));Ok(await owner.CheckpointRecoveryAsync());var file=OnlyFile(directory);var previous=Hash(file);Ok(owner.Execute(new RenameObject(owner.Snapshot.SelectedObjectId!.Value,"Newer unsaved")));var before=owner.Snapshot;
            using(var ct=new CancellationTokenSource()){Check((await owner.CheckpointRecoveryAsync(ct.Token,new Progress(p=>{if(p.Stage=="Ready to checkpoint recovery")ct.Cancel();}))).Outcome==Outcome.Cancelled,"Checkpoint cancellation failed.");Same(owner,before);Check(Hash(file)==previous,"Cancelled checkpoint replaced last good.");}
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Check((await owner.CheckpointRecoveryAsync()).Outcome==Outcome.Failed,"Locked checkpoint did not fail.");Same(owner,before);}Check(Hash(file)==previous,"Failed checkpoint changed last good.");
            Check((await owner.CheckpointRecoveryAsync(expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale checkpoint accepted.");Same(owner,before);
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
            {var job=owner.CheckpointRecoveryAsync(ct.Token,new Progress(p=>{if(p.Stage=="Ready to checkpoint recovery"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));Check(entered.Wait(10000),"Checkpoint did not reach boundary.");Check(owner.Execute(new AddCube()).Outcome==Outcome.Busy,"Edit bypassed checkpoint lease.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Checkpoint busy cancellation failed.");Same(owner,before);}
            Check(Hash(file)==previous&&!Directory.GetFiles(directory,"*.tmp").Any(),"Last good or temporary cleanup failed.");
            Pass("Atomic checkpoint cancellation, stale requests, locked writes and busy concurrency retain last good file and full accepted document");
        }
        {
            var directory=Store("restore-decisions");using var source=Configured(directory);Ok(source.Execute(new AddCube()));Ok(await source.CheckpointRecoveryAsync());source.Dispose();using var destination=Configured(directory);var candidate=await One(destination);
            Ok(destination.Execute(new AddCube()));Ok(destination.Execute(new RenameObject(destination.Snapshot.SelectedObjectId!.Value,"Destination dirty")));Ok(await destination.CheckpointRecoveryAsync());var before=destination.Snapshot;
            var result=await destination.RestoreRecoveryAsync(candidate);Check(result.RequiresDecision&&result.Result.Outcome==Outcome.Conflict,"Dirty Restore silently discarded.");Same(destination,before);
            Check((await destination.RestoreRecoveryAsync(candidate,decision:DirtyDecision.Cancel)).Result.Outcome==Outcome.Cancelled,"Restore Cancel failed.");Same(destination,before);
            Check((await destination.RestoreRecoveryAsync(candidate,expectedRevision:before.Revision-1,decision:DirtyDecision.Discard)).Result.Outcome==Outcome.Conflict,"Stale Restore accepted.");Same(destination,before);
            using(var ct=new CancellationTokenSource()){Check((await destination.RestoreRecoveryAsync(candidate,ct.Token,new Progress(p=>{if(p.Stage=="Recovery validated")ct.Cancel();}),decision:DirtyDecision.Save,savePath:Path.Combine(output,"must-not-save.3mf"))).Result.Outcome==Outcome.Cancelled,"Pre-save Restore cancellation failed.");Same(destination,before);Check(!File.Exists(Path.Combine(output,"must-not-save.3mf")),"Restore saved before validation/cancellation boundary.");}
            using var competitor=Configured(directory);
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
            {var job=destination.RestoreRecoveryAsync(candidate,ct.Token,new Progress(p=>{if(p.Stage=="Ready to restore recovery"){entered.Set();Check(release.Wait(10000),"Restore barrier timeout.");}}),decision:DirtyDecision.Discard);Check(entered.Wait(10000),"No Restore boundary.");Check((await competitor.DiscardRecoveryAsync(candidate)).Outcome==Outcome.Conflict,"Concurrent claim stole candidate.");Check(destination.Select(null).Outcome==Outcome.Busy,"Selection bypassed Restore lease.");ct.Cancel();release.Set();Check((await job).Result.Outcome==Outcome.Cancelled,"Restore cancellation failed.");Same(destination,before);}
            var checkpoint=Path.Combine(directory,candidate.SessionId.ToString("N")+".recovery.json");var originalJson=File.ReadAllText(checkpoint);var missing=JsonNode.Parse(originalJson)!;
            missing["Document"]!["Objects"]![0]!.AsObject().Remove("Transform");File.WriteAllText(checkpoint,missing.ToJsonString());var missingCandidate=(await competitor.GetRecoveryCandidatesAsync()).Single(c=>c.SessionId==candidate.SessionId);
            Check((await destination.RestoreRecoveryAsync(missingCandidate,decision:DirtyDecision.Discard)).Result.Outcome==Outcome.Invalid,"Missing transform silently restored default collapsed geometry.");Same(destination,before);
            var json=JsonNode.Parse(originalJson)!;json["Document"]!["Meshes"]![0]!["Triangles"]![0]!["A"]=-1;File.WriteAllText(checkpoint,json.ToJsonString());
            Check((await competitor.DiscardRecoveryAsync(candidate)).Outcome==Outcome.Conflict,"Changed checkpoint accepted stale fingerprint.");var updated=(await competitor.GetRecoveryCandidatesAsync()).Single(c=>c.SessionId==candidate.SessionId);var target=Path.Combine(output,"invalid-must-not-save.3mf");
            Check((await destination.RestoreRecoveryAsync(updated,decision:DirtyDecision.Save,savePath:target)).Result.Outcome==Outcome.Invalid,"Invalid recovered geometry accepted.");Same(destination,before);Check(!File.Exists(target),"Invalid recovery caused current-work Save before validation.");Ok(await competitor.DiscardRecoveryAsync(updated));
            Pass("Restore requires explicit dirty decision, validates before Save/Discard, rechecks exact fingerprint and exclusive claims, and preserves both current work and candidate on cancel/failure");
        }
        {
            var directory=Store("retirement");using var owner=Configured(directory);Ok(await owner.OpenAsync(fixture));Ok(owner.Execute(new RenameObject(owner.Snapshot.Scene.Objects[0].Id,"Dirty group")));Ok(await owner.CheckpointRecoveryAsync());var file=OnlyFile(directory);var before=owner.Snapshot;var previous=Hash(file);
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                Check(owner.Undo().Outcome==Outcome.Failed,"Clean Undo ignored retirement failure.");Same(owner,before);Check((await owner.TransitionAsync(new NewDocumentTransition(before.Revision,DirtyDecision.Discard))).Result.Outcome==Outcome.Failed,"Discard New ignored retirement failure.");Same(owner,before);
                Check((await owner.OpenAsync(fixture,decision:DirtyDecision.Discard)).Outcome==Outcome.Failed,"Discard Open ignored retirement failure.");Same(owner,before);
                var close=await owner.TransitionAsync(new CloseDocumentTransition(before.Revision,DirtyDecision.Discard));Ok(close.Result);Check(owner.CompleteClose(close.Revision).Outcome==Outcome.Failed,"Close ignored retirement failure.");Same(owner,before);
                var save=await owner.SaveAsync(Path.Combine(output,"saved-despite-cleanup-failure.3mf"));Ok(save);Check(save.Message.Contains("cleanup failed",StringComparison.Ordinal)&&!owner.Snapshot.IsDirty,"Committed Save was misreported or cleanup warning missing.");
            }
            Check(Hash(file)==previous,"Retirement failure destroyed checkpoint.");var saved=owner.Snapshot;Ok(await owner.CheckpointRecoveryAsync());Same(owner,saved);Check(!File.Exists(file),"Clean retry did not retire stale checkpoint.");
            Ok(owner.Execute(new RenameObject(owner.Snapshot.Scene.Objects[0].Id,"Dirty again")));Ok(await owner.CheckpointRecoveryAsync());Ok(owner.Undo());Check(!owner.Snapshot.IsDirty&&!Directory.GetFiles(directory,"*.recovery.json").Any(),"Clean Undo left stale recovery.");
            Ok(owner.Redo());Ok(await owner.CheckpointRecoveryAsync());var ready=await owner.TransitionAsync(new CloseDocumentTransition(owner.Snapshot.Revision,DirtyDecision.Discard));Ok(ready.Result);Check(Directory.GetFiles(directory,"*.recovery.json").Length==1,"Close readiness retired too early.");
            Check((await owner.TransitionAsync(new CloseDocumentTransition(owner.Snapshot.Revision,DirtyDecision.Cancel))).Result.Outcome==Outcome.Cancelled,"Close cancel failed.");Check(owner.CompleteClose(ready.Revision).Outcome==Outcome.Conflict,"Cancelled readiness still authorized close.");
            ready=await owner.TransitionAsync(new CloseDocumentTransition(owner.Snapshot.Revision,DirtyDecision.Discard));Ok(ready.Result);Ok(owner.Execute(new RenameObject(owner.Snapshot.Scene.Objects[0].Id,"Late edit")));Check(owner.CompleteClose(ready.Revision).Outcome==Outcome.Conflict,"Stale close retired later work.");
            ready=await owner.TransitionAsync(new CloseDocumentTransition(owner.Snapshot.Revision,DirtyDecision.Discard));Ok(ready.Result);Ok(owner.CompleteClose(ready.Revision));Check(owner.Execute(new AddCube()).Outcome==Outcome.Conflict,"Accepted edit after final close.");owner.Dispose();using var observer=Configured(directory);Check((await observer.GetRecoveryCandidatesAsync()).IsEmpty,"Intentionally discarded close resurrected recovery.");
            Pass("Retirement faults block clean Undo and discard New/Open/Close; Save remains successful with visible cleanup warning/retry; cancelled/stale close readiness cannot retire, final close seals edits");
        }
        {
            var childRoot=Path.Combine(output,"crashed-child");Directory.CreateDirectory(childRoot);var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};start.ArgumentList.Add(typeof(RecoveryChecks).Assembly.Location);start.ArgumentList.Add(root);start.ArgumentList.Add("--recovery-child");start.ArgumentList.Add("--evidence-root");start.ArgumentList.Add(childRoot);
            using var child=Process.Start(start)??throw new Exception("Could not start owned recovery child.");var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
            try
            {
                var ready=Path.Combine(childRoot,"ready.json");for(var i=0;i<200&&!File.Exists(ready)&&!child.HasExited;i++)await Task.Delay(50);Check(File.Exists(ready)&&!child.HasExited,"Owned child did not checkpoint and hold lock.");
                using var observer=Configured(Path.Combine(childRoot,"store"));Check((await observer.GetRecoveryCandidatesAsync()).IsEmpty,"Live subprocess work was stealable.");var pid=child.Id;child.Kill(entireProcessTree:true);await child.WaitForExitAsync();var candidate=await One(observer);Ok((await observer.RestoreRecoveryAsync(candidate)).Result);Check(observer.Snapshot.Scene.Objects.Single().Name=="Crash child unsaved"&&observer.Snapshot.IsDirty,"Crash-released checkpoint did not restore.");records.Add(new{ownedChildPid=pid,knownChildTerminated=true,restoredObject=observer.Snapshot.SelectedObjectId});
            }
            finally{if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}File.WriteAllText(Path.Combine(childRoot,"stdout.log"),await stdout);File.WriteAllText(Path.Combine(childRoot,"stderr.log"),await stderr);}
            Pass("Real owned subprocess checkpoint is hidden while alive; terminating only that child releases the OS lock and exposes restorable unsaved work");
        }
        File.WriteAllText(Path.Combine(output,"recovery-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,records,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=fixture,sha256=Hash(fixture)},limits=new{version=1,maxCheckpointBytes=64*1024*1024,maxRecords=1000},
            limitations="Recovery is opt-in and timer-free in Core; last completed accepted-state checkpoint, not an edit journal or power-loss guarantee. Native save remains3MF. Fresh history after restore; empty dirty recovery supported. Exclusive local owner locks and exact file fingerprints guard cooperating sessions. Damaged bounded JSON can be discarded; unreadable/oversize entry remains visible with retry/manual-move guidance. Small retired tombstone retained if deletion fails. No UI claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} recovery groups passed. Evidence: {output}");
    }
    private static void Restored(EditorSnapshot restored,EditorSnapshot source)
    {
        Check(restored.FilePath==source.FilePath&&restored.SelectedObjectId==source.SelectedObjectId&&restored.SelectedObjectIds.SequenceEqual(source.SelectedObjectIds)&&restored.EditingGroupId==source.EditingGroupId,"Recovery path/selection/scope changed.");Check(restored.Scene.Metadata==source.Scene.Metadata&&restored.Scene.ContentId==source.Scene.ContentId&&restored.Scene.Groups.Length==source.Scene.Groups.Length&&restored.Scene.Groups.Zip(source.Scene.Groups).All(p=>p.First.Id==p.Second.Id&&p.First.Name==p.Second.Name&&p.First.ObjectIds.SequenceEqual(p.Second.ObjectIds)),"Recovery metadata/group/content identity changed.");
        Check(restored.Scene.Objects.Length==source.Scene.Objects.Length,"Recovered object count changed.");foreach(var (a,b) in restored.Scene.Objects.Zip(source.Scene.Objects))Check(a.Id==b.Id&&a.Name==b.Name&&a.Transform==b.Transform&&a.Material==b.Material&&a.TriangleMaterials.OrderBy(p=>p.Key).SequenceEqual(b.TriangleMaterials.OrderBy(p=>p.Key))&&a.Mesh.Vertices.SequenceEqual(b.Mesh.Vertices)&&a.Mesh.Triangles.SequenceEqual(b.Mesh.Triangles),"Recovered geometry/material/identity changed.");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress value)=>action(value);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted editor state changed.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
