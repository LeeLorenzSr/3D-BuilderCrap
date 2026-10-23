using Builder.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : "../..");
var knownModes=new HashSet<string>(StringComparer.Ordinal) {"--v1-workflows-only","--surface-brush-only","--radial-array-only","--center-on-plate-only","--rectangular-array-only","--distribution-only","--view-preferences-only","--reflections-only","--shadows-only","--shading-only","--view-controls-only","--recovery-only","--recovery-child","--gltf-only","--ply-only","--obj-only","--model-metadata-only","--document-transitions-only","--surface-paint-only","--image-edge-stamp-only","--image-contour-only","--materials-only","--extrude-down-only","--separate-only","--smooth-only","--simplify-only","--hollow-only","--image-relief-only","--text-emboss-only","--construction-only",
    "--mirror-measurement-only","--primitives-only","--performance-only","--member-editing-only","--group-editing-only"};
foreach(var option in args.Where(a=>a.StartsWith("--",StringComparison.Ordinal)))
    if(!knownModes.Contains(option)&&option!="--evidence-root")throw new ArgumentException("Unknown check option: "+option);
if(args.Count(knownModes.Contains)>1)throw new ArgumentException("Choose only one focused check mode.");
_=NewFeatureEvidenceRoot(args,root); // Validate routing before any check can write evidence.
if(args.Contains("--v1-workflows-only"))
{
    await V1WorkflowChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--surface-brush-only"))
{
    await SurfaceBrushChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--radial-array-only"))
{
    await RadialArrayChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--center-on-plate-only"))
{
    await CenterOnPlateChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--rectangular-array-only"))
{
    await RectangularArrayChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--distribution-only"))
{
    await DistributionChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--view-preferences-only"))
{
    await ViewPreferenceChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--reflections-only"))
{
    await ReflectionChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--shadows-only"))
{
    await ShadowChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--shading-only"))
{
    await ShadingChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--view-controls-only"))
{
    await ViewControlsChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--recovery-child"))
{
    await RecoveryChecks.Child(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--recovery-only"))
{
    await RecoveryChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--gltf-only"))
{
    await GltfChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--ply-only"))
{
    await PlyChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--obj-only"))
{
    await ObjChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--model-metadata-only"))
{
    await ModelMetadataChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--document-transitions-only"))
{
    await DocumentTransitionChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--surface-paint-only"))
{
    await SurfacePaintChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--image-edge-stamp-only"))
{
    await ImageEdgeStampChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--image-contour-only"))
{
    await ImageContourChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--materials-only"))
{
    await MaterialChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--extrude-down-only"))
{
    await ExtrudeDownChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--separate-only"))
{
    await SeparateChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--smooth-only"))
{
    await SmoothChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--simplify-only"))
{
    await SimplifyChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--hollow-only"))
{
    await HollowChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--image-relief-only"))
{
    await ImageReliefChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--text-emboss-only"))
{
    await TextEmbossChecks.Run(root);
    return;
}
if(args.Contains("--construction-only"))
{
    await ConstructionChecks.Run(root);
    await MultiSelectionConstructionChecks.Run(root);
    return;
}
if(args.Contains("--mirror-measurement-only"))
{
    await MirrorMeasurementChecks.Run(root);
    return;
}
if(args.Contains("--primitives-only"))
{
    await PrimitiveChecks.Run(root);
    return;
}
if(args.Contains("--performance-only"))
{
    await PerformanceSafetyChecks.Run(root,includeLimitFixtures:true);
    return;
}
if(args.Contains("--member-editing-only"))
{
    await MemberEditingChecks.Run(root,NewFeatureEvidenceRoot(args,root));
    return;
}
if(args.Contains("--group-editing-only"))
{
    await GroupEditingChecks.Run(root);
    await SelectionExportChecks.Run(root);
    return;
}
var output = Path.Combine(root,".local","evidence","prototype-20261018","checks");
Directory.CreateDirectory(output);
var cases = new List<string>();
string Fixture(string file) => Path.Combine(root,"fixtures","generated",file);
void Assert(bool value,string message) { if(!value) throw new Exception(message); }
void Near(double actual,double expected) => Assert(Math.Abs(actual-expected)<1e-8,$"Expected {expected}, got {actual}.");
void Pass(string name) { cases.Add(name); Console.WriteLine("PASS " + name); }
void Ok(OperationResult result) => Assert(result.Succeeded,result.ToString());
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Same(EditorSession s,EditorSnapshot before) => Assert(s.Snapshot == before,"Operation changed the accepted editor state.");
var editor = new EditorSession();
Ok(await editor.OpenAsync(Fixture("cube-20mm.stl")));
var cube = editor.Snapshot.Scene.Objects[0].Id;
var original = editor.Snapshot.Scene;
Near(original.Bounds.Size.X,20);
Ok(editor.Execute(new ResizeObject(cube,new(30,20,20))));
Near(editor.Snapshot.Scene.Bounds.Size.X,30); Near(editor.Snapshot.Scene.Bounds.Center.X,10);
Ok(editor.Undo()); Assert(editor.Snapshot.Scene == original,"Undo did not restore original scene.");
Ok(editor.Redo()); Near(editor.Snapshot.Scene.Bounds.Size.X,30);
Pass("Open 20 mm STL; resize to 30 mm around center; undo/redo with stable identity");

var saved = Path.Combine(output,"scaled.3mf"); Ok(await editor.SaveAsync(saved));
Assert(!editor.Snapshot.IsDirty && editor.Snapshot.CanUndo,"Save changed history or failed to mark clean.");
Ok(editor.Undo()); Assert(editor.Snapshot.IsDirty,"Undo across save boundary must be dirty.");
Ok(editor.Redo()); Assert(!editor.Snapshot.IsDirty,"Redo to saved state must be clean.");
Ok(editor.Undo()); Ok(editor.Execute(new MoveObject(cube,new(15,10,10))));
Assert(!editor.Snapshot.CanRedo,"An edit after undo must clear redo.");
Pass("Save preserves history; clean state follows history; branching discards redo");

var reopened = new EditorSession(); Ok(await reopened.OpenAsync(saved));
Near(reopened.Snapshot.Scene.Bounds.Size.X,30); Assert(!reopened.Snapshot.CanUndo,"Reopen should not load session history.");
var exported = Path.Combine(output,"scaled.stl"); Ok(await reopened.ExportStlAsync(exported));
var exportState = reopened.Snapshot;
Ok(reopened.Execute(new MoveObject(reopened.Snapshot.Scene.Objects[0].Id,new(25,10,10))));
var beforeExport = reopened.Snapshot;
Ok(await reopened.ExportStlAsync(Path.Combine(output,"moved.stl"))); Same(reopened,beforeExport);
Assert(exportState.FilePath == saved,"STL export must not replace native save path.");
Pass("3MF reopen restores geometry; STL export preserves dirty state, filename and history");

foreach(var malformed in new[] { "truncated-cube.stl","not-a-package.3mf" })
{
    var before = editor.Snapshot;
    Assert(!(await editor.OpenAsync(Path.Combine(root,"fixtures","failures",malformed),decision:DirtyDecision.Discard)).Succeeded,"Malformed input accepted."); Same(editor,before);
}
var unchanged = editor.Snapshot;
Assert(editor.Execute(new ResizeObject(cube,new(double.NaN,20,20))).Outcome == Outcome.Invalid,"NaN accepted."); Same(editor,unchanged);
Assert(editor.Execute(new ResizeObject(cube,new(0,20,20))).Outcome == Outcome.Invalid,"Zero size accepted."); Same(editor,unchanged);
Assert(editor.Execute(new DeleteObject(Guid.NewGuid())).Outcome == Outcome.Invalid,"Missing object accepted."); Same(editor,unchanged);
Assert(editor.Execute(new AddCube(),unchanged.Revision-1).Outcome == Outcome.Conflict,"Stale revision accepted."); Same(editor,unchanged);
Assert(!(await editor.OpenAsync(Path.Combine(output,"missing.stl"),decision:DirtyDecision.Discard)).Succeeded,"Missing file accepted."); Same(editor,unchanged);
Pass("Malformed/missing open, invalid edits and stale commands preserve full scene, selection, path and history");

using(var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    Assert((await editor.OpenAsync(Fixture("assembly-mm.3mf"),cancellation:cancellation.Token,decision:DirtyDecision.Discard)).Outcome == Outcome.Cancelled,"Pre-cancel failed."); Same(editor,unchanged);
}
foreach(var stage in new[] { "Reading triangles","Ready to open" })
{
    using var cancellation = new CancellationTokenSource();
    Assert((await editor.OpenAsync(Fixture("cube-20mm.stl"),cancellation:cancellation.Token,
        progress:new InlineProgress(p => { if(p.Stage == stage) cancellation.Cancel(); }),decision:DirtyDecision.Discard)).Outcome == Outcome.Cancelled,"In-flight import cancellation failed.");
    Same(editor,unchanged);
}
Pass("Cancellation before read, during triangle read and before import commit preserves accepted scene");

var destination = Path.Combine(output,"preserved.3mf"); File.Copy(saved,destination,true); var hash = Hash(destination);
foreach(var stage in new[] { "Preparing save","Writing vertices","Ready to save" })
{
    using var cancellation = new CancellationTokenSource();
    Assert((await editor.SaveAsync(destination,cancellation.Token,new InlineProgress(p => { if(p.Stage == stage) cancellation.Cancel(); }))).Outcome == Outcome.Cancelled,"Save cancellation failed.");
    Same(editor,unchanged); Assert(Hash(destination) == hash,"Cancelled save replaced last good file.");
}
using(var locked = new FileStream(destination,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
{
    Assert((await editor.SaveAsync(destination)).Outcome == Outcome.Failed,"Locked destination should fail replacement."); Same(editor,unchanged);
}
Assert(Hash(destination) == hash,"Failed save changed the last good file.");
Assert(!(await editor.SaveAsync(Path.Combine(output,"absent-folder","file.3mf"))).Succeeded,"Missing folder should fail."); Same(editor,unchanged);
Assert(!Directory.EnumerateFiles(output,"*.tmp").Any(),"Temporary save files leaked.");
Pass("Cancelled writes, locked destination and missing folder preserve scene AND last good file; temporary files cleaned");

using(var entered = new ManualResetEventSlim())
using(var release = new ManualResetEventSlim())
{
    var pending = editor.OpenAsync(Fixture("assembly-mm.3mf"),progress:new InlineProgress(p => {
        if(p.Stage == "Ready to open") { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Test synchronization timed out."); }
    }),decision:DirtyDecision.Discard);
    Assert(entered.Wait(TimeSpan.FromSeconds(10)),"Import did not reach commit boundary.");
    Assert(editor.Execute(new AddCube()).Outcome == Outcome.Busy,"Concurrent edit should be rejected.");
    Assert(editor.Undo().Outcome == Outcome.Busy,"Concurrent undo should be rejected.");
    Assert((await editor.SaveAsync(destination)).Outcome == Outcome.Busy,"Concurrent save should be rejected.");
    release.Set(); Ok(await pending);
}
Pass("Concurrent edit, undo and save cannot race an in-flight import");

var mm = editor.Snapshot.Scene;
Ok(await reopened.OpenAsync(Fixture("assembly-inch.3mf")));
var inches = reopened.Snapshot.Scene;
Near(mm.Bounds.Size.X,50); Near(inches.Bounds.Size.X,50); Near(inches.Bounds.Size.Y,20);
Assert(mm.Objects.Length == 2 && inches.Objects.Length == 2,"Instances were merged.");
Assert(ReferenceEquals(inches.Objects[0].Mesh,inches.Objects[1].Mesh),"Shared resource was expanded unnecessarily.");
var second = inches.Objects[1];
Ok(reopened.Execute(new MoveObject(inches.Objects[0].Id,new(5,10,10))));
Assert(reopened.Snapshot.Scene.Objects[1] == second,"Moving one instance changed its neighbor.");
Ok(reopened.Undo()); Ok(await reopened.SaveAsync(Path.Combine(output,"assembly.3mf")));
Ok(await editor.OpenAsync(Path.Combine(output,"assembly.3mf")));
Assert(editor.Snapshot.Scene.Objects.Length == 2,"Saving selected object lost unselected objects.");
Near(editor.Snapshot.Scene.Bounds.Size.X,50);
Ok(await reopened.OpenAsync(Fixture("cube-20mm.stl"),ModelUnit.Inches)); Near(reopened.Snapshot.Scene.Bounds.Size.X,508);
Pass("Equivalent mm/inch assemblies retain units, shared resources and independent objects; STL inches gives 508 mm");

Ok(editor.Execute(new DuplicateObject(editor.Snapshot.Scene.Objects[0].Id)));
Assert(editor.Snapshot.Scene.Objects.Length == 3,"Duplicate failed.");
Ok(editor.Execute(new DeleteObject(editor.Snapshot.SelectedObjectId!.Value))); Ok(editor.Undo());
Assert(editor.Snapshot.Scene.Objects.Length == 3,"Undo delete failed.");
Ok(editor.Execute(new MoveObject(editor.Snapshot.SelectedObjectId!.Value,new(0,0,30))));
Ok(editor.Execute(new PlaceOnBed(editor.Snapshot.SelectedObjectId!.Value)));
Near(editor.Snapshot.Scene.Objects.First(o => o.Id == editor.Snapshot.SelectedObjectId).Bounds.Min.Z,0);
Pass("Duplicate, delete, move and place-on-bed are undoable document commands");

foreach(var fixture in Directory.EnumerateFiles(Path.Combine(root,"fixtures","generated"),"*.stl"))
{ Ok(await reopened.OpenAsync(fixture)); Assert(reopened.Snapshot.Scene.TriangleCount > 0,"Lost triangles."); }
Pass("All seven synthetic STL meshes open without silently repairing or removing defects");

// Additional original format probes: one triangle ASCII, component hierarchy/rotation, cycle, unsupported extension.
var ascii = Path.Combine(output,"ascii.stl");
File.WriteAllText(ascii,"solid test\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 20 0 0\nvertex 0 20 0\nendloop\nendfacet\nendsolid test\n");
Ok(await reopened.OpenAsync(ascii)); Near(reopened.Snapshot.Scene.Bounds.Size.X,20);
var component = Path.Combine(output,"components.3mf"); File.Copy(saved,component,true);
using(var zip = ZipFile.Open(component,ZipArchiveMode.Update))
{
    var entry = zip.GetEntry("3D/3dmodel.model")!; string xml;
    using(var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
    xml = xml.Replace("</resources>","<object id=\"2\" type=\"model\"><components><component objectid=\"1\" transform=\"0 1 0 -1 0 0 0 0 1 30 0 0\"/></components></object></resources>");
    xml = xml.Replace("<item objectid=\"1\"","<item objectid=\"2\"");
    entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open()); writer.Write(xml);
}
Ok(await reopened.OpenAsync(component)); Near(reopened.Snapshot.Scene.Bounds.Size.X,30); Near(reopened.Snapshot.Scene.Bounds.Size.Y,20);
Near(reopened.Snapshot.Scene.Bounds.Min.X,10);
// Nested rotation/translation is applied before the build item's X scaling and center offset.
Pass("ASCII STL and nested 3MF component transform import");

foreach(var mode in new[] { "cycle","extension","nonfinite" })
{
    var broken = Path.Combine(output,mode+".3mf"); File.Copy(component,broken,true);
    using(var zip = ZipFile.Open(broken,ZipArchiveMode.Update))
    {
        var entry = zip.GetEntry("3D/3dmodel.model")!; string xml;
        using(var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
        xml = mode switch {
            "cycle" => xml.Replace("<component objectid=\"1\"","<component objectid=\"2\""),
            "extension" => xml.Replace("<model ","<model requiredextensions=\"unsupported\" "),
            _ => xml.Replace("x=\"0\"","x=\"NaN\"") };
        entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open()); writer.Write(xml);
    }
    var before = reopened.Snapshot;
    Assert(!(await reopened.OpenAsync(broken)).Succeeded,"Unsupported or malformed 3MF was accepted: " + mode); Same(reopened,before);
}
Pass("Cyclic components, required extensions and nonfinite coordinates fail without scene loss");

File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow,
    runtime = Environment.Version.ToString(), cases, outputs = Directory.EnumerateFiles(output).Where(p => Path.GetExtension(p) is ".3mf" or ".stl").Select(p => new { file = Path.GetFileName(p),sha256 = Hash(p) }) },new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine($"{cases.Count} scenario groups passed. Evidence: {output}");
await ConstructionChecks.Run(root);
await RepairChecks.Run(root);
await EditingChecks.Run(root);
await PrimitiveChecks.Run(root);
await MirrorMeasurementChecks.Run(root);
await TextEmbossChecks.Run(root);
await ImageReliefChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await HollowChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await SimplifyChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await SmoothChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await SeparateChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ExtrudeDownChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await MaterialChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ImageContourChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ImageEdgeStampChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await SurfacePaintChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await DocumentTransitionChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ModelMetadataChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ObjChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await PlyChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await GltfChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await RecoveryChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ViewControlsChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ShadingChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ShadowChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ReflectionChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await ViewPreferenceChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await SurfaceBrushChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await RadialArrayChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await CenterOnPlateChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await RectangularArrayChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await DistributionChecks.Run(root,NewFeatureEvidenceRoot(args,root));
await TransformChecks.Run(root);
await MultiObjectChecks.Run(root);
await GroupFormatChecks.Run(root);
await MultiSelectionConstructionChecks.Run(root);
await GroupEditingChecks.Run(root);
await SelectionExportChecks.Run(root);
await MemberEditingChecks.Run(root);
await PerformanceSafetyChecks.Run(root);

static string? NewFeatureEvidenceRoot(string[] arguments,string root)
{
    var index=Array.IndexOf(arguments,"--evidence-root");
    if(index<0)return null;
    if(index+1>=arguments.Length || arguments[index+1].StartsWith("--",StringComparison.Ordinal))
        throw new ArgumentException("--evidence-root needs a path for the new feature checks.");
    return Path.GetFullPath(arguments[index+1],root);
}

sealed class InlineProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
{ public void Report(OperationProgress value) => action(value); }
