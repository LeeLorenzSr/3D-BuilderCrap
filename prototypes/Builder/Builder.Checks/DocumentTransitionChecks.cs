using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class DocumentTransitionChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/document-navigation-20261021/core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/surface-paint-20261021/inputs/surface-group.3mf");var cube=Path.Combine(root,"fixtures/generated/cube-20mm.stl");
        var cases=new List<string>();var files=new List<object>();
        async Task<EditorSession> Dirty()
        {
            var s=new EditorSession();Ok(await s.OpenAsync(input));var group=s.Snapshot.Scene.Groups.Single().Id;var id=s.Snapshot.Scene.Objects[0].Id;Ok(s.EnterGroup(group,id));
            Ok(s.Execute(new RenameObject(id,"Unsaved painted member"){MemberGroupId=group}));Ok(s.Execute(new RenameObject(id,"temporary"){MemberGroupId=group}));Ok(s.Undo());return s;
        }
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        void Record(string path)=>files.Add(new{path=Path.GetFileName(path),sha256=Hash(path)});

        var s=await Dirty();var before=s.Snapshot;
        DocumentTransition[] defaults=[new NewDocumentTransition(before.Revision),new OpenDocumentTransition(cube,before.Revision),new CloseDocumentTransition(before.Revision)];
        foreach(var request in defaults){var r=await s.TransitionAsync(request);Check(r.Result.Outcome==Outcome.Conflict&&r.RequiresDecision&&!r.SaveCommitted&&!r.ReadyToClose,"Dirty default silently continued.");Same(s,before);}
        Check((await s.OpenAsync(cube)).Outcome==Outcome.Conflict,"Legacy Open silently discarded dirty content.");Same(s,before);
        foreach(var request in new DocumentTransition[]{new NewDocumentTransition(before.Revision,DirtyDecision.Cancel),new OpenDocumentTransition(cube,before.Revision,Decision:DirtyDecision.Cancel),new CloseDocumentTransition(before.Revision,DirtyDecision.Cancel)}){var r=await s.TransitionAsync(request);Check(r.Result.Outcome==Outcome.Cancelled&&!r.SaveCommitted&&!r.ReadyToClose,"Cancel decision continued.");Same(s,before);}
        Check((await s.TransitionAsync(new NewDocumentTransition(before.Revision-1,DirtyDecision.Discard))).Result.Outcome==Outcome.Conflict,"Stale prompt decision accepted.");Same(s,before);
        using(var ct=new CancellationTokenSource()){ct.Cancel();Check((await s.TransitionAsync(new OpenDocumentTransition(cube,before.Revision,Decision:DirtyDecision.Discard),ct.Token)).Result.Outcome==Outcome.Cancelled,"Precancelled replacement accepted.");Same(s,before);}
        Pass("Dirty New/Open/Close and source-compatible Open require explicit decisions; Cancel, precancel and stale prompt decisions preserve full painted scene, scope, selection and history");

        var close=await s.TransitionAsync(new CloseDocumentTransition(before.Revision,DirtyDecision.Discard));Check(close.Result.Succeeded&&close.ReadyToClose&&close.Revision==before.Revision&&!close.SaveCommitted,"Close readiness failed.");Same(s,before);
        Ok(s.ExitGroup());Check(s.Snapshot.Revision!=close.Revision,"A later state change did not invalidate close stamp.");
        var newResult=await s.TransitionAsync(new NewDocumentTransition(s.Snapshot.Revision,DirtyDecision.Discard));Ok(newResult.Result);var empty=s.Snapshot;
        Check(empty.Scene.Objects.IsEmpty&&empty.Scene.Groups.IsEmpty&&empty.SelectedObjectId is null&&empty.SelectedObjectIds.IsEmpty&&empty.EditingGroupId is null&&empty.FilePath is null&&!empty.IsDirty&&!empty.CanUndo&&!empty.CanRedo&&newResult.Revision==empty.Revision,"New did not fully reset clean document state.");
        Ok(s.Execute(new AddCube()));var id=s.Snapshot.SelectedObjectId!.Value;Ok(s.Execute(new DeleteObject(id)));var deleted=s.Snapshot;Check(deleted.Scene.Objects.IsEmpty&&deleted.IsDirty&&deleted.CanUndo,"Delete-all fixture not dirty/undoable.");
        var emptyPath=Path.Combine(output,"must-not-save-empty.3mf");var rejected=await s.TransitionAsync(new NewDocumentTransition(deleted.Revision,DirtyDecision.Save,emptyPath));Check(rejected.Result.Outcome==Outcome.Invalid&&!rejected.SaveCommitted&&rejected.Result.Message.Contains("Undo",StringComparison.Ordinal)&&!File.Exists(emptyPath),"Dirty empty save did not offer usable alternative.");Same(s,deleted);
        Check((await s.TransitionAsync(new NewDocumentTransition(deleted.Revision,DirtyDecision.Cancel))).Result.Outcome==Outcome.Cancelled,"Dirty empty Cancel failed.");Same(s,deleted);Ok(s.Undo());Check(s.Snapshot.Scene.Objects.Length==1,"Dirty empty Save destroyed Undo.");
        Ok((await s.TransitionAsync(new NewDocumentTransition(s.Snapshot.Revision,DirtyDecision.Discard))).Result);
        Pass("Close is revision-stamped readiness without mutation; New resets clean untitled state, and dirty delete-all Save refuses while Undo/Discard/Cancel remain usable");

        s=await Dirty();before=s.Snapshot;var saved=Path.Combine(output,"saved-before-open.3mf");var opened=await s.TransitionAsync(new OpenDocumentTransition(cube,before.Revision,Decision:DirtyDecision.Save,SavePath:saved));Ok(opened.Result);
        Check(opened.SaveCommitted&&opened.SavedPath==Path.GetFullPath(saved)&&opened.Revision==s.Snapshot.Revision&&!opened.ReadyToClose&&s.Snapshot.FilePath is null&&!s.Snapshot.IsDirty&&!s.Snapshot.CanUndo&&!s.Snapshot.CanRedo&&s.Snapshot.EditingGroupId is null&&s.Snapshot.Scene.Objects.Length==1,"Save+Open success state incorrect.");
        var read=new EditorSession();Ok(await read.OpenAsync(saved));Check(read.Snapshot.Scene.Groups.Length==1&&read.Snapshot.Scene.Objects[0].Name=="Unsaved painted member"&&read.Snapshot.Scene.Objects[0].TriangleMaterials.Count==4,"Pre-transition save lost content/appearance.");Record(saved);
        s=await Dirty();var newSave=Path.Combine(output,"saved-before-new.3mf");var fresh=await s.TransitionAsync(new NewDocumentTransition(s.Snapshot.Revision,DirtyDecision.Save,newSave));Ok(fresh.Result);Check(fresh.SaveCommitted&&s.Snapshot.Scene.Objects.IsEmpty&&s.Snapshot.FilePath is null&&!s.Snapshot.IsDirty,"Save+New did not create clean untitled document.");Record(newSave);
        s=await Dirty();var closeSave=Path.Combine(output,"saved-before-close.3mf");var closeSaved=await s.TransitionAsync(new CloseDocumentTransition(s.Snapshot.Revision,DirtyDecision.Save,closeSave));Ok(closeSaved.Result);Check(closeSaved.ReadyToClose&&closeSaved.SaveCommitted&&closeSaved.Revision==s.Snapshot.Revision&&!s.Snapshot.IsDirty&&s.Snapshot.CanUndo&&s.Snapshot.CanRedo&&s.Snapshot.EditingGroupId is not null,"Save+Close lost content/history/scope or stamp.");Record(closeSave);
        Pass("Save+Open/New/Close serialize successful stages and report committed native paths; saved groups/surface colors survive and replacement alone resets history/scope");

        s=await Dirty();before=s.Snapshot;var preserved=Path.Combine(output,"preserved.3mf");File.Copy(input,preserved,true);var originalHash=Hash(preserved);
        using(var ct=new CancellationTokenSource()){var r=await s.TransitionAsync(new OpenDocumentTransition(cube,before.Revision,Decision:DirtyDecision.Save,SavePath:preserved),ct.Token,new Progress(p=>{if(p.Stage=="Ready to save")ct.Cancel();}));Check(r.Result.Outcome==Outcome.Cancelled&&!r.SaveCommitted&&r.SavedPath is null,"Cancelled save reported committed transition.");Same(s,before);Check(Hash(preserved)==originalHash,"Cancelled save replaced destination.");}
        var failed=await s.TransitionAsync(new NewDocumentTransition(before.Revision,DirtyDecision.Save,Path.Combine(output,"missing-directory","failed.3mf")));Check(failed.Result.Outcome==Outcome.Failed&&!failed.SaveCommitted,"Failed save continued.");Same(s,before);
        var missing=Path.Combine(output,"not-found.3mf");var discarded=await s.TransitionAsync(new OpenDocumentTransition(missing,before.Revision,Decision:DirtyDecision.Discard));Check(discarded.Result.Outcome==Outcome.Failed&&!discarded.SaveCommitted,"Failed discard/open changed stage semantics.");Same(s,before);
        using(var ct=new CancellationTokenSource()){var r=await s.TransitionAsync(new OpenDocumentTransition(cube,before.Revision,Decision:DirtyDecision.Discard),ct.Token,new Progress(p=>{if(p.Stage=="Ready to open")ct.Cancel();}));Check(r.Result.Outcome==Outcome.Cancelled&&!r.SaveCommitted,"Cancelled import committed.");Same(s,before);}
        var committedPath=Path.Combine(output,"saved-then-open-failed.3mf");var partial=await s.TransitionAsync(new OpenDocumentTransition(missing,before.Revision,Decision:DirtyDecision.Save,SavePath:committedPath));
        Check(partial.Result.Outcome==Outcome.Failed&&partial.SaveCommitted&&partial.SavedPath==Path.GetFullPath(committedPath)&&partial.Result.Message.Contains("saved",StringComparison.OrdinalIgnoreCase),"Post-save failure inaccurately described rollback.");
        Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&s.Snapshot.EditingGroupId==before.EditingGroupId&&s.Snapshot.CanUndo==before.CanUndo&&s.Snapshot.CanRedo==before.CanRedo&&!s.Snapshot.IsDirty&&s.Snapshot.FilePath==Path.GetFullPath(committedPath)&&s.Snapshot.Revision==before.Revision+1,"Post-save failure changed geometry/history or hid successful save.");Record(committedPath);
        s=await Dirty();before=s.Snapshot;var cancelledPath=Path.Combine(output,"saved-then-transition-cancelled.3mf");using(var ct=new CancellationTokenSource()){var r=await s.TransitionAsync(new NewDocumentTransition(before.Revision,DirtyDecision.Save,cancelledPath),ct.Token,new Progress(p=>{if(p.Stage=="Current document saved")ct.Cancel();}));Check(r.Result.Outcome==Outcome.Cancelled&&r.SaveCommitted&&r.SavedPath==Path.GetFullPath(cancelledPath)&&r.Result.Message.Contains("saved",StringComparison.OrdinalIgnoreCase)&&ReferenceEquals(s.Snapshot.Scene,before.Scene)&&!s.Snapshot.IsDirty&&s.Snapshot.CanRedo,"Post-save cancellation hid successful save or discarded scene.");}Record(cancelledPath);
        Pass("Failed/cancelled save or unsaved Open retains accepted state/file; failure/cancel after atomic save honestly retains current scene as saved with committed path, history and selection");

        s=await Dirty();before=s.Snapshot;using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {
            var job=s.TransitionAsync(new OpenDocumentTransition(cube,before.Revision,Decision:DirtyDecision.Discard),ct.Token,new Progress(p=>{if(p.Stage=="Ready to open"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));Check(entered.Wait(10000),"Open did not reach ready stage.");
            Check(s.Select(null).Outcome==Outcome.Busy&&s.Undo().Outcome==Outcome.Busy&&(await s.SaveAsync(Path.Combine(output,"busy-must-not-save.3mf"))).Outcome==Outcome.Busy&&(await s.TransitionAsync(new NewDocumentTransition(before.Revision,DirtyDecision.Discard))).Result.Outcome==Outcome.Busy,"One-lease transition allowed a racing mutation.");ct.Cancel();release.Set();Check((await job).Result.Outcome==Outcome.Cancelled,"Busy cancelled import committed.");Same(s,before);
        }
        var target=before.Scene.Objects[0].Id;Ok(await s.PreviewSurfaceMaterialAsync(new(target,2,new("Preview",new(1,2,3))){MemberGroupId=before.EditingGroupId}));var held=s.Snapshot;
        var blocked=await s.TransitionAsync(new NewDocumentTransition(held.Revision,DirtyDecision.Save,Path.Combine(output,"preview-must-not-save.3mf")));Check(blocked.Result.Outcome==Outcome.Conflict&&!blocked.SaveCommitted&&!File.Exists(Path.Combine(output,"preview-must-not-save.3mf")),"Transition bypassed pending preview.");Same(s,held);
        Check(s.DiscardPreview(held.Preview!.Id).Outcome==Outcome.Cancelled,"Discard failed.");before=s.Snapshot;Ok(await s.OpenAsync(cube,expectedRevision:before.Revision,decision:DirtyDecision.Discard));Check(!s.Snapshot.IsDirty&&s.Snapshot.Scene.Objects.Length==1&&!s.Snapshot.CanUndo,"Explicit compatible Open did not replace.");
        Pass("Busy/preview guards reject before saving; one lease prevents interleaved edits and explicit revision-bound legacy Discard remains available");
        File.WriteAllText(Path.Combine(output,"document-transitions-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,files,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=input,sha256=Hash(input)},limitations="Close readiness is point-in-time revision state; Desktop must recheck final Closing callback. Empty native save remains unsupported, with Undo/Discard/Cancel alternatives. Recent-file storage/dialogs are adapter concerns; only committed successful paths should be recorded. No UI claims or worker changes."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} document transition groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted scene/path/history/selection/scope changed.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
