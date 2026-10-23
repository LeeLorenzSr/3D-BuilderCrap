using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ShadowChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/shadows-20261021/core");Directory.CreateDirectory(output);var cases=new List<string>();var input=Path.Combine(root,".local/evidence/surface-paint-20261021/inputs/surface-group.3mf");
        void Pass(string message){cases.Add(message);Console.WriteLine("PASS "+message);}
        using var editor=new EditorSession();var presentation=editor.Presentation;var document=editor.Snapshot;
        Check(ViewSettings.Default.Shadows&&new ViewSettings(false,false,true,true).Shadows&&new ViewSettings(false,false,true,true,ShadingMode.Smooth).Shadows,"Default/legacy constructors do not enable shadows.");
        var previous=presentation.Snapshot;var disabled=previous.Settings with{Shadows=false,Shading=ShadingMode.Smooth};Ok(presentation.Update(disabled,previous.Revision));Check(presentation.Snapshot.Settings==disabled&&presentation.Snapshot.Revision==previous.Revision+1,"Shadow update did not use independent presentation revision.");Same(editor,document);
        previous=presentation.Snapshot;Ok(presentation.Update(disabled));Check(presentation.Snapshot==previous,"Equal shadow update advanced revision.");Check(presentation.Reset(previous.Revision-1).Outcome==Outcome.Conflict,"Stale presentation reset accepted.");Check(presentation.Snapshot==previous,"Stale reset changed settings.");Ok(presentation.Reset(previous.Revision));Check(presentation.Snapshot.Settings==ViewSettings.Default&&presentation.Snapshot.Settings.Shadows,"Reset did not enable shadows/defaults.");previous=presentation.Snapshot;Ok(presentation.Reset());Check(presentation.Snapshot==previous,"Default reset advanced revision.");Same(editor,document);
        Pass("Shadows default on for appended/legacy constructors; CAS, no-op and reset use only independent presentation state");

        Ok(await editor.OpenAsync(input));var accepted=editor.Snapshot;Ok(await editor.PreviewTransformAsync(new MoveTransform(accepted.Scene.Objects[0].Id,new(3,0,0))));var held=editor.Snapshot;
        Ok(presentation.Update(presentation.Snapshot.Settings with{Shadows=false}));Same(editor,held);Check(ReferenceEquals(editor.Snapshot.Preview,held.Preview),"Shadow toggle replaced held preview.");var view=presentation.Snapshot;Ok(editor.ApplyPreview(held.Preview!.Id));Check(presentation.Snapshot==view,"Apply reset shadows.");Ok(editor.Undo());Check(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&presentation.Snapshot==view,"Undo changed presentation/source.");Ok(editor.Redo());Check(presentation.Snapshot==view,"Redo reset shadows.");
        var saved=Path.Combine(output,"shadow-independent-scene.3mf");Ok(await editor.SaveAsync(saved));Check(presentation.Snapshot==view,"Save changed shadows.");var clean=editor.Snapshot;Ok(presentation.Update(view.Settings with{Shadows=true}));Same(editor,clean);Ok(presentation.Update(presentation.Snapshot.Settings with{Shadows=false}));Same(editor,clean);
        using(var reader=new EditorSession()){Ok(await reader.OpenAsync(saved));Check(reader.Presentation.Snapshot.Settings.Shadows,"Native save persisted disabled session shadows.");Check(Colors(reader.Snapshot.Scene).SequenceEqual(Colors(editor.Snapshot.Scene)),"Display shadows changed material assignments.");}
        Pass("Shadow toggles preserve exact held preview and document state; Apply/Undo/Redo/Save retain session choice while native files and authored materials exclude it");

        var beforeBusy=editor.Snapshot;
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=editor.ExportPlyAsync(Path.Combine(output,"cancelled-busy.ply"),ModelUnit.Millimeters,ct.Token,new Progress(p=>{if(p.Stage=="Ready to export PLY"){entered.Set();Check(release.Wait(10000),"Busy boundary timeout.");}}));Check(entered.Wait(10000),"Export did not reach boundary.");var busy=editor.Snapshot;Check(busy.IsBusy,"Document busy lease not held.");Ok(presentation.Update(presentation.Snapshot.Settings with{Shadows=true}));Same(editor,busy);Check(editor.Select(null).Outcome==Outcome.Busy,"Document guard bypassed during presentation update.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy export cancellation failed.");}
        Same(editor,beforeBusy);Check(presentation.Snapshot.Settings.Shadows,"Geometry completion overwrote concurrent shadow choice.");
        Pass("Shadows can change during a document busy lease; cancelled work does not overwrite the view choice or accepted document");
        File.WriteAllText(Path.Combine(output,"shadows-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=input,sha256=Hash(input)},output=new{path=saved,sha256=Hash(saved)},defaults=ViewSettings.Default,
            limitations="Core stores Shadows only. Build-plate shadow rendering is verified separately; no document mesh/material/picking/save/recovery changes and no UI/original-app claim."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"{cases.Count} shadow groups passed. Evidence: {output}");
    }
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Shadow action changed document snapshot.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
