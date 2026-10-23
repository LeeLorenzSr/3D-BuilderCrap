using Builder.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GltfChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/gltf-20261021/core");Directory.CreateDirectory(output);var inputs=Path.Combine(root,".local/evidence/gltf-20261021/inputs");var cases=new List<string>();var outputs=new List<object>();
        EditorSession New()=>new();
        async Task<EditorSession> Load(string name="assembly.gltf",ModelUnit unit=ModelUnit.Meters){var session=New();Ok(await session.OpenAsync(Path.Combine(inputs,name),unit));return session;}
        void Pass(string message){cases.Add(message);Console.WriteLine("PASS "+message);}
        void Record(string path,ModelUnit unit)=>outputs.Add(new{path=Path.GetFileName(path),coordinateUnit=unit.ToString(),reopenUnit="Meters",sha256=Hash(path)});
        var implicitUnits=New();Ok(await implicitUnits.OpenAsync(Path.Combine(inputs,"assembly.glb")));Near(implicitUnits.Snapshot.Scene.Bounds.Size.X,50);
        var implicitInsert=New();Ok(await implicitInsert.InsertAsync(Path.Combine(inputs,"assembly.gltf")));Near(implicitInsert.Snapshot.Scene.Bounds.Size.X,50);
        var implicitTransition=New();Ok((await implicitTransition.TransitionAsync(new OpenDocumentTransition(Path.Combine(inputs,"assembly.glb"),implicitTransition.Snapshot.Revision))).Result);Near(implicitTransition.Snapshot.Scene.Bounds.Size.X,50);
        var e=await Load();var scene=e.Snapshot.Scene;var colors=Colors(scene);
        Check(scene.Objects.Length==2&&scene.Groups.Single().Name=="Assembly"&&scene.TriangleCount==24&&e.Snapshot.FilePath is null&&!e.Snapshot.IsDirty&&!e.Snapshot.CanUndo,"Static scene ownership/history failed.");
        Near(scene.Bounds.Min,new());Near(scene.Bounds.Max,new(50,20,20));NearVolume(Volume(scene),16000);Check(colors.Count(c=>c=="#F3F3F0")==8&&colors.Count(c=>c=="#D65248")==2&&colors.Count(c=>c=="#267CB5")==14,"Opaque base color conversion failed.");
        foreach(var name in new[]{"assembly.glb","assembly-external.gltf"}){var reader=await Load(name);Near(reader.Snapshot.Scene.Bounds.Max,new(50,20,20));NearVolume(Volume(reader.Snapshot.Scene),16000);Check(Colors(reader.Snapshot.Scene).SequenceEqual(colors),"Embedded/external/GLB colors differ.");}
        var raw=await Load("raw-millimeters.gltf",ModelUnit.Millimeters);Near(raw.Snapshot.Scene.Bounds.Size,new(20,20,20));NearVolume(Volume(raw.Snapshot.Scene),8000);
        var hollow=await Load("hollow.glb");Check(hollow.Snapshot.Scene.Objects.Length==1,"Hollow shells split into objects.");NearVolume(Volume(hollow.Snapshot.Scene),3904);
        Pass("Embedded/external-buffer glTF and GLB import meters/Y-up, groups, opaque base colors and one-leaf cavities; explicit raw-mm override works");

        var instances=await Load("instances.glb");var instanceScene=instances.Snapshot.Scene;Check(instanceScene.Objects.Length==2&&ReferenceEquals(instanceScene.Objects[0].Mesh,instanceScene.Objects[1].Mesh),"Mesh-node instances lost shared geometry or identity.");Near(instanceScene.Bounds.Min,new(-12,3,2));Near(instanceScene.Bounds.Max,new(55,20,28));NearVolume(Volume(instanceScene),1768);
        var vertex=await Load("vertex-colors.gltf");Near(Area(vertex.Snapshot.Scene),9);var expected=new[]{LinearColor(1d/12,1d/6,1d/3),LinearColor(.25,.125,.0625)}.Order().ToArray();Check(Colors(vertex.Snapshot.Scene).SequenceEqual(expected),"Linear COLOR_0 averaging/base multiplication to sRGB failed.");
        var strips=await Load("strip-fan.gltf");Check(strips.Snapshot.Scene.Objects.Length==2&&strips.Snapshot.Scene.TriangleCount==4,"Strip/fan topology failed.");Near(Area(strips.Snapshot.Scene),18);
        var parentMesh=JsonNode.Parse(File.ReadAllText(Path.Combine(inputs,"assembly.gltf")))!;parentMesh["nodes"]![0]!["mesh"]=0;var parentPath=Path.Combine(output,"mesh-and-children.gltf");File.WriteAllText(parentPath,parentMesh.ToJsonString());var readerParent=New();Ok(await readerParent.OpenAsync(parentPath,ModelUnit.Meters));Check(readerParent.Snapshot.Scene.Objects.Length==3&&readerParent.Snapshot.Scene.Groups.Single().ObjectIds.Length==3,"Parent node mesh was omitted beside children.");
        Pass("Nested TRS/matrix and reflected instances retain geometry; normalized/float vertex colors average in linear space; strips/fans and mesh-bearing parents work");

        var before=e.Snapshot;
        foreach(var (name,unit) in new[]{("assembly-coordinate-mm.gltf",ModelUnit.Millimeters),("assembly-coordinate-in.glb",ModelUnit.Inches),("assembly-meter.glb",ModelUnit.Meters)})
        {
            var path=Path.Combine(output,name);Ok(await e.ExportGltfAsync(path,unit));Same(e,before);var reopened=New();Ok(await reopened.OpenAsync(path,ModelUnit.Meters));Near(reopened.Snapshot.Scene.Bounds.Max,new(50,20,20));NearVolume(Volume(reopened.Snapshot.Scene),16000);Check(Colors(reopened.Snapshot.Scene).SequenceEqual(colors)&&reopened.Snapshot.Scene.Groups.Single().Name=="Assembly","Colors/groups failed standards-meter roundtrip.");
            Check(reopened.Snapshot.Scene.Objects.Sum(o=>o.Mesh.Vertices.Length)==scene.Objects.Sum(o=>o.Mesh.Vertices.Length),"Shared POSITION duplicated per material primitive.");Record(path,unit);
        }
        var hollowPath=Path.Combine(output,"hollow.glb");Ok(await hollow.ExportGltfAsync(hollowPath,ModelUnit.Millimeters));var read=New();Ok(await read.OpenAsync(hollowPath,ModelUnit.Meters));NearVolume(Volume(read.Snapshot.Scene),3904);Check(read.Snapshot.Scene.Objects.Length==1,"Cavity ownership changed.");Record(hollowPath,ModelUnit.Millimeters);
        var reflectedPath=Path.Combine(output,"reflected-instances.glb");Ok(await instances.ExportGltfAsync(reflectedPath,ModelUnit.Centimeters));read=New();Ok(await read.OpenAsync(reflectedPath,ModelUnit.Meters));NearVolume(Volume(read.Snapshot.Scene),1768);Near(read.Snapshot.Scene.Bounds.Min,new(-12,3,2));Record(reflectedPath,ModelUnit.Centimeters);
        var vertexPath=Path.Combine(output,"flat-vertex-colors.gltf");Ok(await vertex.ExportGltfAsync(vertexPath,ModelUnit.Meters));read=New();Ok(await read.OpenAsync(vertexPath,ModelUnit.Meters));Check(Colors(read.Snapshot.Scene).SequenceEqual(expected),"Flattened color export changed visible sRGB.");Record(vertexPath,ModelUnit.Meters);
        var native=Path.Combine(output,"native-scene.3mf");Ok(await e.SaveAsync(native));before=e.Snapshot;Ok(await e.ExportGltfAsync(Path.Combine(output,"saved-export.glb"),ModelUnit.Meters));Same(e,before);Record(native,ModelUnit.Meters);
        Pass("Self-contained glTF/GLB exports with mm/in/m coordinate choices reopen as standard meters with colors/groups, reflected winding, cavities and shared POSITION retained");

        var destination=New();Ok(destination.Execute(new AddCube()));Ok(destination.UpdateModelMetadata(new("Destination"),destination.Snapshot.Revision));before=destination.Snapshot;Ok(await destination.InsertAsync(Path.Combine(inputs,"assembly.glb"),ModelUnit.Meters));Check(destination.Snapshot.Scene.Objects.Length==3&&destination.Snapshot.Scene.Metadata.Title=="Destination","Insert lost destination attribution or objects.");Ok(destination.Undo());Check(ReferenceEquals(destination.Snapshot.Scene,before.Scene),"Insert did not restore source with one Undo.");Ok(destination.Redo());
        var group=destination.Snapshot.Scene.Groups.Single();var subset=Path.Combine(output,"selected-group.glb");before=destination.Snapshot;Ok(await destination.ExportSelectionAsync(subset,[group.ObjectIds[0]],unit:ModelUnit.Millimeters));Same(destination,before);read=New();Ok(await read.OpenAsync(subset,ModelUnit.Meters));Check(read.Snapshot.Scene.Objects.Length==2&&read.Snapshot.Scene.Groups.Length==1,"Scene-scope export did not retain group.");NearVolume(Volume(read.Snapshot.Scene),16000);Record(subset,ModelUnit.Millimeters);
        Ok(destination.EnterGroup(group.Id,group.ObjectIds[0]));var memberPath=Path.Combine(output,"selected-member.gltf");before=destination.Snapshot;Ok(await destination.ExportSelectionAsync(memberPath,[group.ObjectIds[0]],memberGroupId:group.Id,unit:ModelUnit.Inches));Same(destination,before);read=New();Ok(await read.OpenAsync(memberPath,ModelUnit.Meters));Check(read.Snapshot.Scene.Objects.Length==1&&read.Snapshot.Scene.Groups.IsEmpty,"Member subset included siblings.");NearVolume(Volume(read.Snapshot.Scene),8000);Record(memberPath,ModelUnit.Inches);
        Pass("One-step Insert Undo retains destination attribution; scene-group and exact member selection exports keep ownership and accepted state");

        before=e.Snapshot;foreach(var name in new[]{"bad-accessor.gltf","unsafe-companion.gltf","transparent.gltf","texture.gltf","animated.gltf","truncated.glb"}){Check((await e.OpenAsync(Path.Combine(inputs,name),ModelUnit.Meters)).Outcome==Outcome.Invalid,"Unsupported/malformed glTF accepted.");Same(e,before);}
        var original=File.ReadAllBytes(Path.Combine(inputs,"assembly.glb"));using(var input=new BinaryReader(new MemoryStream(original)))
        {
            input.ReadBytes(12);var jsonLength=input.ReadInt32();input.ReadUInt32();var json=JsonNode.Parse(input.ReadBytes(jsonLength))!;var binLength=input.ReadInt32();input.ReadUInt32();var bin=input.ReadBytes(binLength);json["buffers"]![0]!["byteLength"]=json["buffers"]![0]!["byteLength"]!.GetValue<int>()-1;var bytes=Encoding.UTF8.GetBytes(json.ToJsonString());var padded=(bytes.Length+3)&~3;var invalidPath=Path.Combine(output,"accessor-into-padding.glb");
            using(var writer=new BinaryWriter(File.Create(invalidPath))){writer.Write(0x46546c67u);writer.Write(2u);writer.Write(12+8+padded+8+bin.Length);writer.Write(padded);writer.Write(0x4e4f534au);writer.Write(bytes);for(var i=bytes.Length;i<padded;i++)writer.Write((byte)32);writer.Write(bin.Length);writer.Write(0x004e4942u);writer.Write(bin);}
            Check((await e.OpenAsync(invalidPath,ModelUnit.Meters)).Outcome==Outcome.Invalid,"Accessor reached GLB buffer padding.");Same(e,before);
        }
        Check((await e.ExportGltfAsync(Path.Combine(output,"invalid-unit.glb"),(ModelUnit)99)).Outcome==Outcome.Invalid,"Invalid export unit accepted.");Same(e,before);Check((await e.ExportGltfAsync(native,ModelUnit.Meters)).Outcome==Outcome.Invalid,"Native destination replaced.");Same(e,before);Check((await e.ExportGltfAsync(Path.Combine(output,"stale.glb"),ModelUnit.Meters,expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale export accepted.");Same(e,before);
        Ok(await e.PreviewMaterialAsync(new([e.Snapshot.Scene.Objects[0].Id],new("Held",new(1,2,3)))));var held=e.Snapshot;Check((await e.ExportGltfAsync(Path.Combine(output,"preview.glb"),ModelUnit.Meters)).Outcome==Outcome.Conflict,"Export bypassed preview guard.");Same(e,held);e.DiscardPreview(held.Preview!.Id);before=e.Snapshot;
        using(var ct=new CancellationTokenSource()){Check((await e.InsertAsync(Path.Combine(inputs,"assembly.glb"),ModelUnit.Meters,ct.Token,new Progress(p=>{if(p.Stage=="Packing glTF instances")ct.Cancel();}))).Outcome==Outcome.Cancelled,"Insert cancellation failed.");Same(e,before);}
        Pass("Bad accessors/padding/companions, textures/transparency/animation, invalid units/stale/preview/native-path and cancelled Insert preserve accepted work");

        var atomic=Path.Combine(output,"atomic-existing.glb");Ok(await e.ExportGltfAsync(atomic,ModelUnit.Meters));var previousHash=Hash(atomic);before=e.Snapshot;
        foreach(var stage in new[]{"Packing glTF geometry","Ready to export glTF"}){using var ct=new CancellationTokenSource();Check((await e.ExportGltfAsync(atomic,ModelUnit.Meters,ct.Token,new Progress(p=>{if(p.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"glTF cancel failed.");Same(e,before);Check(Hash(atomic)==previousHash,"Cancelled export changed existing output.");}
        using(var locked=new FileStream(atomic,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Check((await e.ExportGltfAsync(atomic,ModelUnit.Meters)).Outcome==Outcome.Failed,"Locked output did not fail.");Same(e,before);}Check(Hash(atomic)==previousHash,"Failed output changed file.");
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=e.ExportGltfAsync(atomic,ModelUnit.Meters,ct.Token,new Progress(p=>{if(p.Stage=="Ready to export glTF"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));Check(entered.Wait(10000),"No glTF export boundary.");Check(e.Select(null).Outcome==Outcome.Busy,"Selection bypassed busy export.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Export cancellation failed.");Same(e,before);}
        Check(!Directory.GetFiles(output,"*.tmp").Any(),"Temporary glTF file leaked.");Record(atomic,ModelUnit.Meters);
        Pass("Atomic glTF/GLB writer cancellation/failure preserves existing file and accepted state; one busy lease blocks concurrent edits and removes temporary files");
        File.WriteAllText(Path.Combine(output,"gltf-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},inputs=Directory.GetFiles(inputs).Where(p=>Path.GetExtension(p) is ".gltf" or ".glb" or ".bin").Select(p=>new{path=p,sha256=Hash(p)}),
            limits=new{objects=1000,triangles=100000,vertices=300000,materials=1000,fileAndDecodedBufferBytes=64*1024*1024,buffers=32,views=10000,accessors=10000,nodes=4000,treeDepth=64},
            limitations="Static triangle/strip/fan meshes only; selected/default-first scene; one leaf per mesh-bearing node and flat group per multi-leaf root. Float POSITION; unsigned/float COLOR_0 flattened after linear multiplication/averaging then sRGB conversion. OPAQUE ignores alpha. Normals/UV/shading/extras omitted; textures, transparency modes, animation, skins, morphs, sparse/compressed/required-extension data refuse. Export coordinates use selected units but nodeScale preserves standard meters; reopen export with Meters. Native3MF retains document metadata. No UI/original-app claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} glTF groups passed. Evidence: {output}");
    }
    private static string LinearColor(double r,double g,double b){static byte Channel(double n)=>(byte)Math.Round((n<=.0031308?12.92*n:1.055*Math.Pow(n,1/2.4)-.055)*255,MidpointRounding.AwayFromZero);return new RgbColor(Channel(r),Channel(g),Channel(b)).Hex;}
    private static double Volume(Scene scene)=>scene.Objects.Sum(o=>Math.Abs(o.Mesh.Triangles.Sum(t=>{var a=o.Transform.Apply(o.Mesh.Vertices[t.A]);var b=o.Transform.Apply(o.Mesh.Vertices[t.B]);var c=o.Transform.Apply(o.Mesh.Vertices[t.C]);var n=Vec3.Cross(b,c);return(a.X*n.X+a.Y*n.Y+a.Z*n.Z)/6;})));
    private static double Area(Scene scene)=>scene.Objects.Sum(o=>o.Mesh.Triangles.Sum(t=>{var a=o.Transform.Apply(o.Mesh.Vertices[t.A]);var n=Vec3.Cross(o.Transform.Apply(o.Mesh.Vertices[t.B])-a,o.Transform.Apply(o.Mesh.Vertices[t.C])-a);return Math.Sqrt(n.X*n.X+n.Y*n.Y+n.Z*n.Z)/2;}));
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void NearVolume(double value,double expected)=>Check(Math.Abs(value-expected)<Math.Max(.001,expected*1e-6),$"Expected volume {expected}, got {value}.");
    private static void Near(double value,double expected)=>Check(Math.Abs(value-expected)<.001,$"Expected {expected}, got {value}.");
    private static void Near(Vec3 value,Vec3 expected){Near(value.X,expected.X);Near(value.Y,expected.Y);Near(value.Z,expected.Z);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted editor state changed.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
