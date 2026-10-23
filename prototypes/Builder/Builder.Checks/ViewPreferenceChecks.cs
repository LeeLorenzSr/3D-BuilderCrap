using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ViewPreferenceChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/view-preferences-20261022/core");Directory.CreateDirectory(output);var cases=new List<string>();var input=Path.Combine(root,".local/evidence/surface-paint-20261021/inputs/surface-group.3mf");
        void Pass(string message){cases.Add(message);Console.WriteLine("PASS "+message);}
        var choices=new ViewSettings(false,false,true,true,ShadingMode.Smooth,false,true);
        var path=Path.Combine(output,"roundtrip","view.json");var store=new JsonViewPreferenceStore(path);
        using(var editor=new EditorSession())
        {
            var doc=editor.Snapshot;Ok(editor.Presentation.Update(choices));Check(editor.Presentation.PreferenceStatus==new ViewPreferenceStatus(ViewPreferenceState.MemoryOnly,false),"Default session used preference storage.");
            Ok(editor.Presentation.ConfigurePreferences(store));Check(editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Missing&&editor.Presentation.PreferenceStatus.PendingSave&&editor.Presentation.PreferenceStatus.Error is null,"Missing status is not quiet/pending.");Check(!File.Exists(path)&&!Directory.Exists(Path.GetDirectoryName(path)),"Configure missing preferences created a file/directory.");Check(editor.Presentation.Snapshot.Settings==choices,"Missing load reset existing display.");Same(editor,doc);
            var before=editor.Presentation.Snapshot;Ok(editor.Presentation.Update(choices));Check(editor.Presentation.Snapshot==before&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Saved,"Same-value explicit save changed revision or failed to persist.");Same(editor,doc);
        }
        var hash=Hash(path);using(var next=new EditorSession())
        {
            var doc=next.Snapshot;Ok(next.Presentation.ConfigurePreferences(new JsonViewPreferenceStore(path)));Check(next.Presentation.Snapshot.Settings==choices&&next.Presentation.PreferenceStatus.State==ViewPreferenceState.Loaded,"Seven choices did not load.");Same(next,doc);Check(Hash(path)==hash,"Startup rewrote preferences.");Ok(next.Presentation.Reset());Check(next.Presentation.Snapshot.Settings==ViewSettings.Default&&store.Load()==ViewSettings.Default,"Reset did not durably save defaults.");
        }
        var counted=new CountingStore(ViewSettings.Default);using(var idle=new EditorSession()){Ok(idle.Presentation.ConfigurePreferences(counted));Check(counted.Saves==0,"Configure wrote preferences.");Ok(idle.Presentation.Reset());Check(counted.Saves==1,"Equal default Reset did not save.");}Check(counted.Saves==1,"Dispose wrote preferences.");
        using(var final=new EditorSession()){Ok(final.Presentation.ConfigurePreferences(store));Check(final.Presentation.Snapshot.Settings==ViewSettings.Default,"Defaults did not survive another launch.");}
        Pass("Opt-in memory-only/default behavior, quiet missing-file load, seven-choice launch roundtrip and durable Reset work without startup/close writes or document changes");

        var validPath=Path.Combine(output,"valid-template.json");new JsonViewPreferenceStore(validPath).Save(choices);var valid=File.ReadAllText(validPath);var variants=new Dictionary<string,string>{{"broken","{broken"},{"version",Mutate(valid,n=>n["Version"]=2)},{"missing",Mutate(valid,n=>n["Settings"]!.AsObject().Remove("Reflections"))},{"type",Mutate(valid,n=>n["Settings"]!["Grid"]="true")},{"shading",Mutate(valid,n=>n["Settings"]!["Shading"]="Unknown")},{"oversize",new string(' ',JsonViewPreferenceStore.MaximumFileBytes+1)}};
        foreach(var (name,content) in variants)
        {
            var file=Path.Combine(output,"load-"+name+".json");File.WriteAllText(file,content);var original=Hash(file);using var editor=new EditorSession();var view=editor.Presentation.Snapshot;var doc=editor.Snapshot;Ok(editor.Presentation.ConfigurePreferences(new JsonViewPreferenceStore(file)));
            Check(editor.Presentation.Snapshot==view&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.LoadFailed&&editor.Presentation.PreferenceStatus.PendingSave&&!string.IsNullOrEmpty(editor.Presentation.PreferenceStatus.Error),"Invalid preferences altered display or hid the load error.");Check(Hash(file)==original,"Failed load rewrote preference file.");Same(editor,doc);
            Check(editor.Presentation.Update(ViewSettings.Default with{Shading=(ShadingMode)99}).Outcome==Outcome.Invalid,"Invalid display update accepted.");Check(Hash(file)==original,"Invalid update repaired/changed original file.");Ok(editor.Presentation.Reset());Check(editor.Presentation.Snapshot==view&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Saved&&new JsonViewPreferenceStore(file).Load()==ViewSettings.Default,"No-op Reset did not explicitly repair failed-load defaults.");Same(editor,doc);
        }
        var unreadable=Path.Combine(output,"load-locked.json");new JsonViewPreferenceStore(unreadable).Save(choices);hash=Hash(unreadable);using(var editor=new EditorSession())
        {
            using(var locked=new FileStream(unreadable,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Ok(editor.Presentation.ConfigurePreferences(new JsonViewPreferenceStore(unreadable)));Check(editor.Presentation.PreferenceStatus.State==ViewPreferenceState.LoadFailed,"Unreadable load did not report failure.");}Check(Hash(unreadable)==hash,"Unreadable load changed file.");
        }
        Pass("Corrupt/version/missing/wrong-type/shading/oversize/unreadable loads preserve current display and original file; explicit equal Reset repairs defaults, invalid updates do not");

        var atomic=Path.Combine(output,"atomic","view.json");var atomicStore=new JsonViewPreferenceStore(atomic);atomicStore.Save(ViewSettings.Default);hash=Hash(atomic);var sentinel=Path.Combine(Path.GetDirectoryName(atomic)!,"unrelated.tmp");File.WriteAllText(sentinel,"Keep unrelated file");
        using(var editor=new EditorSession())
        {
            Ok(editor.Presentation.ConfigurePreferences(atomicStore));var doc=editor.Snapshot;var before=editor.Presentation.Snapshot;
            using(var locked=new FileStream(atomic,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {var result=editor.Presentation.Update(choices);Ok(result);Check(result.Message.Contains("could not be remembered",StringComparison.Ordinal)&&editor.Presentation.Snapshot.Settings==choices&&editor.Presentation.Snapshot.Revision==before.Revision+1,"Write failure rejected accepted display or hid warning.");Check(editor.Presentation.PreferenceStatus.State==ViewPreferenceState.SaveFailed&&editor.Presentation.PreferenceStatus.PendingSave&&!string.IsNullOrEmpty(editor.Presentation.PreferenceStatus.Error),"Separate pending persistence error missing.");Same(editor,doc);}
            Check(Hash(atomic)==hash&&File.ReadAllText(sentinel)=="Keep unrelated file"&&Directory.GetFiles(Path.GetDirectoryName(atomic)!,"*.tmp").Length==1,"Failed atomic save changed last good or removed/leaked an unrelated temp.");
            before=editor.Presentation.Snapshot;Ok(editor.Presentation.Update(choices));Check(editor.Presentation.Snapshot==before&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Saved&&!editor.Presentation.PreferenceStatus.PendingSave&&editor.Presentation.PreferenceStatus.Error is null&&atomicStore.Load()==choices,"Equal Update did not retry failed save without revision change.");
            Ok(editor.Presentation.Reset());before=editor.Presentation.Snapshot;using(var locked=new FileStream(atomic,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Ok(editor.Presentation.Reset());Check(editor.Presentation.PreferenceStatus.State==ViewPreferenceState.SaveFailed&&editor.Presentation.Snapshot==before,"Equal Reset did not attempt/save failure safely.");}Ok(editor.Presentation.Reset());Check(editor.Presentation.Snapshot==before&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Saved&&atomicStore.Load()==ViewSettings.Default,"Repeated equal Reset did not repair failed persistence.");Same(editor,doc);
        }
        Pass("Atomic write faults keep last-good data and accept in-memory display with separate warning; equal Update/Reset retry pending saves without revision changes or temp-file interference");

        using(var editor=new EditorSession())
        {
            var preferencePath=Path.Combine(output,"document-isolation","view.json");Ok(editor.Presentation.ConfigurePreferences(new JsonViewPreferenceStore(preferencePath)));Ok(await editor.OpenAsync(input));var accepted=editor.Snapshot;Ok(editor.Presentation.Update(choices));Same(editor,accepted);
            Ok(await editor.PreviewTransformAsync(new MoveTransform(accepted.Scene.Objects[0].Id,new(2,0,0))));var held=editor.Snapshot;Ok(editor.Presentation.Update(choices with{Colors=true}));Same(editor,held);var view=editor.Presentation.Snapshot;Ok(editor.ApplyPreview(held.Preview!.Id));Ok(editor.Undo());Check(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&editor.Presentation.Snapshot==view,"Undo changed persistent presentation/source.");Ok(editor.Redo());
            var saved=Path.Combine(output,"preference-independent-scene.3mf");Ok(await editor.SaveAsync(saved));using(var reader=new EditorSession()){Ok(await reader.OpenAsync(saved));Check(reader.Presentation.Snapshot.Settings==ViewSettings.Default&&Colors(reader.Snapshot.Scene).SequenceEqual(Colors(editor.Snapshot.Scene)),"Native file gained view preferences or lost authored colors.");}
            var beforeBusy=editor.Snapshot;using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
            {var job=editor.ExportPlyAsync(Path.Combine(output,"cancelled-busy.ply"),ModelUnit.Millimeters,ct.Token,new Progress(p=>{if(p.Stage=="Ready to export PLY"){entered.Set();Check(release.Wait(10000),"Busy barrier timeout.");}}));Check(entered.Wait(10000),"No busy export boundary.");var busy=editor.Snapshot;using(var locked=new FileStream(preferencePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Ok(editor.Presentation.Update(editor.Presentation.Snapshot.Settings with{Grid=true}));Check(editor.Presentation.PreferenceStatus.State==ViewPreferenceState.SaveFailed,"Busy view persistence failure missing.");Same(editor,busy);}var updated=editor.Presentation.Snapshot;Ok(editor.Presentation.Update(updated.Settings));Check(editor.Presentation.Snapshot==updated&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Saved,"Busy retry changed own revision or did not save.");Same(editor,busy);ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy geometry cancellation failed.");}Same(editor,beforeBusy);
            Check(new JsonViewPreferenceStore(preferencePath).Load()==editor.Presentation.Snapshot.Settings,"Concurrent view preference save was lost on geometry completion.");
        }
        Pass("Persistent display settings remain independent of held previews, Apply/Undo/Redo, authored/native files and busy geometry cancellation, including failed-write retry during a lease");
        File.WriteAllText(Path.Combine(output,"view-preferences-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=input,sha256=Hash(input)},format=new{version=1,maximumBytes=JsonViewPreferenceStore.MaximumFileBytes,shading="Flat or Smooth string",flags=7},
            limitations="Opt-in full-snapshot view preference file; default sessions memory-only. Last completed write wins across windows; no cross-process settings merge. Only usual storage/format failures are translated to status; display stays accepted. No startup/close writes or document/recovery integration, no UI/original-app claim."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"{cases.Count} view preference groups passed. Evidence: {output}");
    }
    private static string Mutate(string text,Action<JsonNode> action){var node=JsonNode.Parse(text)!;action(node);return node.ToJsonString();}
    private sealed class CountingStore(ViewSettings? loaded):IViewPreferenceStore{public int Saves;public ViewSettings? Load()=>loaded;public void Save(ViewSettings value)=>Saves++;}
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession editor,EditorSnapshot before)=>Check(editor.Snapshot==before,"View preferences changed document snapshot.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
