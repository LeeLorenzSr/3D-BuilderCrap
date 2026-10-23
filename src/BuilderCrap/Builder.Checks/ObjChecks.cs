using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ObjChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/obj-20261021/core");Directory.CreateDirectory(output);
        var inputs=Path.Combine(root,".local/evidence/obj-20261021/inputs");var cases=new List<string>();var outputs=new List<object>();
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(string name="assembly-mm.obj",ModelUnit unit=ModelUnit.Millimeters){var e=New();Ok(await e.OpenAsync(Path.Combine(inputs,name),unit));return e;}
        void Pass(string value){cases.Add(value);Console.WriteLine("PASS "+value);}
        void Record(string path,ModelUnit unit)=>outputs.Add(new{path=Path.GetFileName(path),unit=unit.ToString(),sha256=Hash(path),companion=Path.GetExtension(path)==".obj"?Companion(path):null});
        var e=await Load();var original=e.Snapshot;var scene=original.Scene;
        Check(scene.Objects.Length==2&&scene.Groups.Single().Name=="Assembly"&&scene.TriangleCount==24,"OBJ object/group/quad ownership failed.");
        Near(scene.Bounds.Size.X,50);Near(scene.Bounds.Size.Y,20);Near(scene.Bounds.Size.Z,20);Near(Volume(scene),16000);
        var first=scene.Objects[0];Check(first.Material?.Color==new RgbColor(243,243,240),"White fallback lost.");
        Check(Enumerable.Range(0,12).Count(i=>first.GetTriangleMaterial(i)?.Color==new RgbColor(214,82,72))==2&&Enumerable.Range(0,12).Count(i=>first.GetTriangleMaterial(i)?.Color==new RgbColor(38,124,181))==2,"Face diffuse colors lost.");
        Check(scene.Objects[1].Material?.Color==new RgbColor(38,124,181)&&!original.IsDirty&&!original.CanUndo&&original.FilePath is null,"OBJ clean/path or negative-index material semantics failed.");
        var inches=await Load("assembly-in.obj",ModelUnit.Inches);Near(inches.Snapshot.Scene.Bounds.Size.X,50);Near(Volume(inches.Snapshot.Scene),16000);
        var hollow=await Load("hollow-mm.obj");Check(hollow.Snapshot.Scene.Objects.Length==1,"Cavity split into separate objects.");Near(Volume(hollow.Snapshot.Scene),3904);
        var concave=await Load("concave-mm.obj");Check(concave.Snapshot.Scene.TriangleCount==4,"Concave polygon not triangulated into four faces.");Near(Area(concave.Snapshot.Scene),5);
        Check(concave.Snapshot.Scene.Objects[0].Mesh.Triangles.All(t=>Normal(concave.Snapshot.Scene.Objects[0],t).Z>0),"Concave winding lost.");
        Pass("Explicit mm/in units; objects/groups, positive/negative references, colored quads, one hollow leaf and concave vt/vn/s polygon import");

        var exported=Path.Combine(output,"assembly-mm.obj");var before=e.Snapshot;Ok(await e.ExportObjAsync(exported,ModelUnit.Millimeters));Same(e,before);Record(exported,ModelUnit.Millimeters);
        var read=New();Ok(await read.OpenAsync(exported));Check(read.Snapshot.Scene.Groups.Single().Name=="Assembly"&&read.Snapshot.Scene.Objects[0].Material?.Name=="White","Ordinary readable names changed.");Near(Volume(read.Snapshot.Scene),16000);Check(Colors(read.Snapshot.Scene).SequenceEqual(Colors(scene)),"OBJ diffuse face colors changed.");
        var exportedIn=Path.Combine(output,"assembly-in.obj");Ok(await e.ExportObjAsync(exportedIn,ModelUnit.Inches));read=New();Ok(await read.OpenAsync(exportedIn,ModelUnit.Inches));Near(read.Snapshot.Scene.Bounds.Size.X,50);Record(exportedIn,ModelUnit.Inches);
        var native=Path.Combine(output,"assembly-native.3mf");Ok(await e.SaveAsync(native));read=New();Ok(await read.OpenAsync(native));Check(Colors(read.Snapshot.Scene).SequenceEqual(Colors(scene)),"Native save after OBJ lost face colors.");Record(native,ModelUnit.Millimeters);
        var hollowExport=Path.Combine(output,"hollow-mm.obj");Ok(await hollow.ExportObjAsync(hollowExport,ModelUnit.Millimeters));read=New();Ok(await read.OpenAsync(hollowExport));Near(Volume(read.Snapshot.Scene),3904);Check(read.Snapshot.Scene.Objects.Length==1,"Hollow roundtrip split ownership.");Record(hollowExport,ModelUnit.Millimeters);
        var reflected=New();var reflectedInput=Path.Combine(root,".local/evidence/mirror-measurement-20261020/inputs/reflected-affine-tetrahedron.3mf");Ok(await reflected.OpenAsync(reflectedInput));var reflectedPath=Path.Combine(output,"reflected-world.obj");Ok(await reflected.ExportObjAsync(reflectedPath,ModelUnit.Millimeters));read=New();Ok(await read.OpenAsync(reflectedPath));Near(Volume(read.Snapshot.Scene),5304);Record(reflectedPath,ModelUnit.Millimeters);
        Pass("World-space OBJ/MTL mm/in export, readable names, per-face/native roundtrip and reflected winding retain material volume and cavity ownership");

        var mixed=New();Ok(mixed.Execute(new AddCube()));var unassigned=mixed.Snapshot.SelectedObjectId!.Value;Ok(mixed.Execute(new AddCube()));var painted=mixed.Snapshot.SelectedObjectId!.Value;
        var longName=new string('N',99)+"\\";Ok(mixed.Execute(new RenameObject(unassigned,longName)));Ok(await mixed.PreviewArrangementAsync(new GroupRequest([unassigned,painted],longName)));Ok(mixed.ApplyPreview(mixed.Snapshot.Preview!.Id));
        Ok(mixed.EnterGroup(mixed.Snapshot.Scene.Groups.Single().Id,painted));var scope=mixed.Snapshot.EditingGroupId;Ok(await mixed.PreviewMaterialAsync(new([painted],new(longName,new(11,22,33))){MemberGroupId=scope}));Ok(mixed.ApplyPreview(mixed.Snapshot.Preview!.Id));Ok(mixed.ExitGroup());
        var mixedPath=Path.Combine(output,"mixed-default-and-color.obj");before=mixed.Snapshot;Ok(await mixed.ExportObjAsync(mixedPath,ModelUnit.Millimeters));Same(mixed,before);read=New();Ok(await read.OpenAsync(mixedPath));
        Check(read.Snapshot.Scene.Objects[0].Material?.Color==SurfaceMaterial.Neutral.Color&&read.Snapshot.Scene.Objects[1].Material?.Color==new RgbColor(11,22,33),"Defined neutral or assigned color changed.");
        Check(read.Snapshot.Scene.Objects[0].Name.Length<=100&&read.Snapshot.Scene.Groups.Single().Name.Length<=100&&read.Snapshot.Scene.Objects[1].Material!.Name.Length<=100,"Exported bounded names cannot roundtrip.");
        Check(!File.ReadAllText(mixedPath).Contains("usemtl off",StringComparison.Ordinal),"Unresolved material reset emitted.");Record(mixedPath,ModelUnit.Millimeters);
        Pass("Mixed assigned/unassigned objects export a defined neutral color; bounded names and trailing backslashes normalize safely without mutating native semantics");

        var destination=New();Ok(destination.Execute(new AddCube()));Ok(destination.UpdateModelMetadata(new("Destination"),destination.Snapshot.Revision));before=destination.Snapshot;
        Ok(await destination.InsertAsync(Path.Combine(inputs,"assembly-in.obj"),ModelUnit.Inches));Check(destination.Snapshot.Scene.Objects.Length==3&&destination.Snapshot.Scene.Metadata.Title=="Destination","Insert did not retain destination metadata or add all objects.");Ok(destination.Undo());Check(ReferenceEquals(destination.Snapshot.Scene,before.Scene),"Insert was not one exact Undo.");Ok(destination.Redo());
        var pair=destination.Snapshot.Scene.Groups.Single();var groupPath=Path.Combine(output,"selected-group.obj");before=destination.Snapshot;Ok(await destination.ExportSelectionAsync(groupPath,[pair.ObjectIds[0]],unit:ModelUnit.Centimeters));Same(destination,before);read=New();Ok(await read.OpenAsync(groupPath,ModelUnit.Centimeters));Check(read.Snapshot.Scene.Objects.Length==2&&read.Snapshot.Scene.Groups.Length==1,"Scene selection did not expand group.");Record(groupPath,ModelUnit.Centimeters);
        Ok(destination.EnterGroup(pair.Id,pair.ObjectIds[0]));var memberPath=Path.Combine(output,"selected-member.obj");before=destination.Snapshot;Ok(await destination.ExportSelectionAsync(memberPath,[pair.ObjectIds[0]],memberGroupId:pair.Id,unit:ModelUnit.Millimeters));Same(destination,before);read=New();Ok(await read.OpenAsync(memberPath));Check(read.Snapshot.Scene.Objects.Length==1&&read.Snapshot.Scene.Groups.IsEmpty,"Member export retained unrelated group members.");Record(memberPath,ModelUnit.Millimeters);
        Pass("Insert is one undoable edit retaining destination attribution; scene-scope subset expands groups and member-scope subset exports one independent leaf");

        before=e.Snapshot;
        foreach(var name in new[]{"bad-index.obj","unsupported-texture.obj"}){Check(!(await e.OpenAsync(Path.Combine(inputs,name))).Succeeded,"Invalid/unsupported OBJ accepted.");Same(e,before);}
        string Variant(string name,string text){var path=Path.Combine(output,name+".obj");File.WriteAllText(path,text);return path;}
        foreach(var path in new[]{Variant("missing-library","mtllib missing.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3"),Variant("external-library","mtllib ../palette.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3"),Variant("bad-normal-index","v 0 0 0\nv 1 0 0\nv 0 1 0\nvn 0 0 1\nf 1//2 2//1 3//1")})
        {Check(!(await e.OpenAsync(path)).Succeeded,"Invalid companion/reference accepted.");Same(e,before);}
        Check((await e.ExportObjAsync(Path.Combine(output,"invalid-unit.obj"),(ModelUnit)99)).Outcome==Outcome.Invalid,"Invalid unit accepted.");Same(e,before);
        Check((await e.ExportObjAsync(exported,ModelUnit.Millimeters,expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale export accepted.");Same(e,before);
        Check((await e.ExportObjAsync(native,ModelUnit.Millimeters)).Outcome==Outcome.Invalid,"OBJ replaced native path.");Same(e,before);
        Ok(await e.PreviewMaterialAsync(new([e.Snapshot.Scene.Objects[0].Id],new("Held",new(1,2,3)))));var held=e.Snapshot;Check((await e.ExportObjAsync(exported,ModelUnit.Millimeters)).Outcome==Outcome.Conflict,"Export bypassed preview.");Same(e,held);e.DiscardPreview(held.Preview!.Id);
        before=e.Snapshot;using(var ct=new CancellationTokenSource()){ct.Cancel();Check((await e.InsertAsync(Path.Combine(inputs,"assembly-mm.obj"),cancellation:ct.Token)).Outcome==Outcome.Cancelled,"Cancelled insert accepted.");Same(e,before);}
        Pass("Invalid companions/textures/indices/units, stale/native-path/preview and cancelled insert guards preserve accepted state");

        var atomic=Path.Combine(output,"atomic-pair.obj");Ok(await e.ExportObjAsync(atomic,ModelUnit.Millimeters));var initialSidecar=Companion(atomic);var initialObjHash=Hash(atomic);var initialMtlHash=Hash(Path.Combine(output,initialSidecar));before=e.Snapshot;
        string[] Sidecars()=>Directory.GetFiles(output,"materials-*.mtl").Select(Path.GetFileName).Order().ToArray()!;
        foreach(var stage in new[]{"OBJ material companion staged","Ready to export OBJ"})
        {var files=Sidecars();using var ct=new CancellationTokenSource();Check((await e.ExportObjAsync(atomic,ModelUnit.Millimeters,ct.Token,new Progress(p=>{if(p.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"Pair cancellation failed.");Same(e,before);Check(Hash(atomic)==initialObjHash&&Hash(Path.Combine(output,initialSidecar))==initialMtlHash&&Sidecars().SequenceEqual(files),"Precommit cancellation changed old pair or leaked a sidecar.");}
        var lockedFiles=Sidecars();using(var locked=new FileStream(atomic,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Check((await e.ExportObjAsync(atomic,ModelUnit.Millimeters)).Outcome==Outcome.Failed,"Locked OBJ replacement did not fail.");Same(e,before);}Check(Hash(atomic)==initialObjHash&&Sidecars().SequenceEqual(lockedFiles),"Failed commit changed old OBJ or leaked companion.");
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=e.ExportObjAsync(atomic,ModelUnit.Millimeters,ct.Token,new Progress(p=>{if(p.Stage=="Ready to export OBJ"){entered.Set();Check(release.Wait(10000),"Export barrier timeout.");}}));Check(entered.Wait(10000),"No export barrier.");Check(e.Select(null).Outcome==Outcome.Busy,"Selection bypassed export lease.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy export cancellation failed.");Same(e,before);}
        Ok(await e.ExportObjAsync(atomic,ModelUnit.Millimeters));Check(Companion(atomic)!=initialSidecar&&File.Exists(Path.Combine(output,initialSidecar))&&Hash(Path.Combine(output,initialSidecar))==initialMtlHash,"Successful replacement reused/deleted older immutable companion.");Same(e,before);Check(!Directory.GetFiles(output,"*.tmp").Any(),"Temporary export file leaked.");Record(atomic,ModelUnit.Millimeters);
        Pass("Unique immutable companions and last-step OBJ commit preserve old pair on cancellation/failure, retain old sidecars on success, and hold one busy lease");
        File.WriteAllText(Path.Combine(output,"obj-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},
            inputs=Directory.GetFiles(inputs).Where(p=>Path.GetExtension(p) is ".obj" or ".mtl").Select(p=>new{path=p,sha256=Hash(p)}),
            limits=new{objects=1000,triangles=100000,vertices=300000,materials=1000,totalSourceBytes=64*1024*1024,materialLibraries=32,polygonVertices=256},
            limitations="Opaque Kd display colors only; UVs/normals/smoothing/render properties omitted. One flat g membership; names normalize when needed. OBJ export promotes unassigned appearance to defined Neutral. No model metadata; native3MF retains it. Unsupported textures/transparency/freeform, external companions and ambiguous polygons refuse. Immutable older sidecars remain after successful replacement. No UI or original-app claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} OBJ groups passed. Evidence: {output}");
    }
    private static string Companion(string path)=>File.ReadLines(path).Single(s=>s.StartsWith("mtllib ",StringComparison.Ordinal))[7..];
    private static Vec3 Normal(SceneObject obj,Triangle t)=>Vec3.Cross(obj.Transform.Apply(obj.Mesh.Vertices[t.B])-obj.Transform.Apply(obj.Mesh.Vertices[t.A]),obj.Transform.Apply(obj.Mesh.Vertices[t.C])-obj.Transform.Apply(obj.Mesh.Vertices[t.A]));
    private static double Area(Scene scene)=>scene.Objects.Sum(o=>o.Mesh.Triangles.Sum(t=>{var n=Normal(o,t);return Math.Sqrt(n.X*n.X+n.Y*n.Y+n.Z*n.Z)/2;}));
    private static double Volume(Scene scene)=>scene.Objects.Sum(o=>o.Mesh.Triangles.Sum(t=>{var a=o.Transform.Apply(o.Mesh.Vertices[t.A]);var b=o.Transform.Apply(o.Mesh.Vertices[t.B]);var c=o.Transform.Apply(o.Mesh.Vertices[t.C]);var n=Vec3.Cross(b,c);return(a.X*n.X+a.Y*n.Y+a.Z*n.Z)/6;}));
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Near(double value,double expected)=>Check(Math.Abs(value-expected)<.001,$"Expected {expected}, got {value}.");
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted editor state changed.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
