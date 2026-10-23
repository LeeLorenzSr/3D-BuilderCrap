using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ShadingChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/shading-20261021/core");Directory.CreateDirectory(output);var cases=new List<string>();var input=Path.Combine(root,".local/evidence/surface-paint-20261021/inputs/surface-group.3mf");
        void Pass(string message){cases.Add(message);Console.WriteLine("PASS "+message);}
        using var editor=new EditorSession();var presentation=editor.Presentation;var document=editor.Snapshot;
        Check((int)ShadingMode.Flat==0&&(int)ShadingMode.Smooth==1&&ViewSettings.Default.Shading==ShadingMode.Flat&&new ViewSettings(false,false,true,true).Shading==ShadingMode.Flat,"Default/legacy positional shading contract changed.");
        var before=presentation.Snapshot;var smooth=before.Settings with{Shading=ShadingMode.Smooth};Ok(presentation.Update(smooth,before.Revision));Check(presentation.Snapshot.Settings==smooth&&presentation.Snapshot.Revision==before.Revision+1,"Smooth update did not use presentation revision.");Same(editor,document);before=presentation.Snapshot;Ok(presentation.Update(smooth));Check(presentation.Snapshot==before,"Equal shading update advanced revision.");
        Check(presentation.Update(smooth with{Shading=(ShadingMode)2}).Outcome==Outcome.Invalid,"Undefined shading accepted.");Check(presentation.Snapshot==before,"Invalid shading changed presentation.");Same(editor,document);
        Check(presentation.Reset(before.Revision-1).Outcome==Outcome.Conflict,"Stale presentation Reset accepted.");Check(presentation.Snapshot==before,"Stale Reset changed presentation.");Ok(presentation.Reset(before.Revision));Check(presentation.Snapshot.Settings==ViewSettings.Default,"Reset did not return Flat/default flags.");before=presentation.Snapshot;Ok(presentation.Reset());Check(presentation.Snapshot==before,"Default reset advanced revision.");
        Pass("Appended Flat/Smooth enum preserves four-boolean callers; invalid values/CAS/no-op/reset affect only the independent presentation boundary");

        Ok(await editor.OpenAsync(input));var accepted=editor.Snapshot;var target=accepted.Scene.Objects[0].Id;Ok(await editor.PreviewTransformAsync(new MoveTransform(target,new(4,0,0))));var held=editor.Snapshot;Ok(presentation.Update(presentation.Snapshot.Settings with{Shading=ShadingMode.Smooth}));Same(editor,held);Check(ReferenceEquals(editor.Snapshot.Preview,held.Preview),"Shading replaced held geometry preview.");
        var heldView=presentation.Snapshot;Check(presentation.Update(heldView.Settings with{Shading=(ShadingMode)99}).Outcome==Outcome.Invalid,"Undefined mode accepted while preview held.");Same(editor,held);Check(presentation.Snapshot==heldView,"Invalid preview shading changed settings.");
        Ok(editor.ApplyPreview(held.Preview!.Id));Check(presentation.Snapshot==heldView,"Apply reset shading.");Ok(editor.Undo());Check(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&presentation.Snapshot==heldView,"Undo changed source/shading.");Ok(editor.Redo());Check(presentation.Snapshot==heldView,"Redo reset shading.");
        var saved=Path.Combine(output,"shading-independent-scene.3mf");Ok(await editor.SaveAsync(saved));Check(presentation.Snapshot==heldView,"Save reset shading.");var clean=editor.Snapshot;Ok(presentation.Update(heldView.Settings with{Shading=ShadingMode.Flat}));Same(editor,clean);Check(!editor.Snapshot.IsDirty,"Shading marked clean document dirty.");
        Ok(presentation.Update(presentation.Snapshot.Settings with{Shading=ShadingMode.Smooth}));using(var reader=new EditorSession()){Ok(await reader.OpenAsync(saved));Check(reader.Presentation.Snapshot.Settings.Shading==ShadingMode.Flat,"Native file retained session shading.");Check(Colors(reader.Snapshot.Scene).SequenceEqual(Colors(editor.Snapshot.Scene)),"Shading changed authored face/base colors.");}
        var sessionView=presentation.Snapshot;Ok((await editor.TransitionAsync(new NewDocumentTransition(editor.Snapshot.Revision))).Result);Check(presentation.Snapshot==sessionView,"New reset session shading.");Ok(await editor.OpenAsync(saved));Check(presentation.Snapshot==sessionView,"Open reset session shading.");
        Pass("Held preview and Apply/Undo/Redo/New/Open/Save isolate shading from exact document state, authored geometry/materials, dirty state and native persistence");

        var beforeBusy=editor.Snapshot;Ok(presentation.Update(presentation.Snapshot.Settings with{Shading=ShadingMode.Flat}));
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=editor.ExportPlyAsync(Path.Combine(output,"cancelled-busy.ply"),ModelUnit.Millimeters,ct.Token,new Progress(p=>{if(p.Stage=="Ready to export PLY"){entered.Set();Check(release.Wait(10000),"Busy barrier timeout.");}}));Check(entered.Wait(10000),"No busy export boundary.");var busy=editor.Snapshot;Check(busy.IsBusy,"Document busy lease not held.");Ok(presentation.Update(presentation.Snapshot.Settings with{Shading=ShadingMode.Smooth}));Same(editor,busy);var current=presentation.Snapshot;Check(presentation.Update(current.Settings with{Shading=(ShadingMode)(-1)}).Outcome==Outcome.Invalid,"Invalid mode bypassed busy validation.");Check(presentation.Snapshot==current,"Invalid busy shading changed presentation.");Same(editor,busy);ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Export cancellation failed.");}
        Same(editor,beforeBusy);Check(presentation.Snapshot.Settings.Shading==ShadingMode.Smooth,"Geometry completion overwrote concurrent shading.");
        Pass("Valid shading updates remain available during busy work; invalid values and cancelled geometry completion preserve both independent boundaries");
        File.WriteAllText(Path.Combine(output,"shading-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=input,sha256=Hash(input)},output=new{path=saved,sha256=Hash(saved)},
            defaults=ViewSettings.Default,limitations="Core stores/validates shading choice only. Renderer-owned normals and visual appearance are checked separately. No document mesh/normal edits, file/recovery serialization or picking changes. No UI/original-app rendering claim."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"{cases.Count} shading groups passed. Evidence: {output}");
    }
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress value)=>action(value);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession editor,EditorSnapshot before)=>Check(editor.Snapshot==before,"Shading changed document snapshot.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
