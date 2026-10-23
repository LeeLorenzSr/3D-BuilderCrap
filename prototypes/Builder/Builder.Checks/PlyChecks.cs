using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class PlyChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/ply-20261021/core");Directory.CreateDirectory(output);
        var inputs=Path.Combine(root,".local/evidence/ply-20261021/inputs");var cases=new List<string>();var outputs=new List<object>();
        EditorSession New()=>new();
        async Task<EditorSession> Load(string name="assembly-mm.ply",ModelUnit unit=ModelUnit.Millimeters){var session=New();Ok(await session.OpenAsync(Path.Combine(inputs,name),unit));return session;}
        void Pass(string value){cases.Add(value);Console.WriteLine("PASS "+value);}
        void Record(string path,ModelUnit unit)=>outputs.Add(new{path=Path.GetFileName(path),unit=unit.ToString(),sha256=Hash(path)});
        var e=await Load();var accepted=e.Snapshot;var scene=accepted.Scene;
        Check(scene.Objects.Length==1&&scene.Groups.IsEmpty&&scene.TriangleCount==24&&accepted.FilePath is null&&!accepted.IsDirty&&!accepted.CanUndo,"PLY did not import one clean independent leaf.");
        Near(scene.Bounds.Size.X,50);Near(scene.Bounds.Size.Y,20);Near(scene.Bounds.Size.Z,20);Near(Volume(scene),16000);
        var colors=Colors(scene);Check(colors.Count(c=>c=="#F3F3F0")==8&&colors.Count(c=>c=="#D65248")==2&&colors.Count(c=>c=="#267CB5")==14&&!colors.Contains("#00FF00"),"Face RGB did not override green vertex RGB.");
        foreach(var name in new[]{"assembly-in-le.ply","assembly-in-be.ply"}){var read=await Load(name,ModelUnit.Inches);Near(read.Snapshot.Scene.Bounds.Size.X,50);Near(Volume(read.Snapshot.Scene),16000);Check(Colors(read.Snapshot.Scene).SequenceEqual(colors),"Endian/color import differed.");}
        var hollow=await Load("hollow-mm.ply");Check(hollow.Snapshot.Scene.Objects.Length==1&&hollow.Snapshot.Scene.TriangleCount==24,"Cavity shells separated.");Near(Volume(hollow.Snapshot.Scene),3904);
        Pass("ASCII and both binary endians honor explicit units and face RGB priority; multiple shells/cavities remain one clean editable leaf");

        var vertex=await Load("vertex-colors.ply");Check(vertex.Snapshot.Scene.Objects[0].GetTriangleMaterial(0)?.Color==new RgbColor(85,85,85),"Vertex color averaging failed.");Near(Area(vertex.Snapshot.Scene),4.5);
        var concave=await Load("concave-mm.ply");Check(concave.Snapshot.Scene.TriangleCount==4,"Concave polygon triangle count changed.");Near(Area(concave.Snapshot.Scene),5);Check(Colors(concave.Snapshot.Scene).All(c=>c=="#267CB5"),"Concave face color lost.");
        var lines=File.ReadAllLines(Path.Combine(inputs,"vertex-colors.ply")).ToList();var headerEnd=lines.IndexOf("end_header");var vertexCount=int.Parse(lines.Single(l=>l.StartsWith("element vertex ",StringComparison.Ordinal))[15..]);
        for(var i=0;i<vertexCount;i++)lines[headerEnd+1+i]+=" 0.5";
        lines.Insert(lines.FindIndex(l=>l.StartsWith("element face ",StringComparison.Ordinal)),"property float32 confidence");
        for(var i=0;i<lines.Count;i++)lines[i]=lines[i].Replace("property float ","property float32 ",StringComparison.Ordinal).Replace("property uchar ","property uint8 ",StringComparison.Ordinal).Replace("property list uchar int ","property list uint8 int32 ",StringComparison.Ordinal);
        lines.Insert(2,"comment exported without materials; author Materialise");
        var benign=Path.Combine(output,"aliases-and-extra-scalar.ply");File.WriteAllLines(benign,lines);var extra=New();Ok(await extra.OpenAsync(benign));Near(Area(extra.Snapshot.Scene),4.5);Check(Colors(extra.Snapshot.Scene).Single()=="#555555","Validated extra scalar altered RGB.");
        Pass("Concave planar polygon triangulation, per-result-triangle vertex averaging, common type aliases and ignored numeric scalar attributes work");

        var exported=Path.Combine(output,"assembly-mm.ply");var before=e.Snapshot;Ok(await e.ExportPlyAsync(exported,ModelUnit.Millimeters));Same(e,before);Record(exported,ModelUnit.Millimeters);
        var readback=New();Ok(await readback.OpenAsync(exported));Near(Volume(readback.Snapshot.Scene),16000);Check(Colors(readback.Snapshot.Scene).SequenceEqual(colors),"Binary writer changed face colors.");Check(readback.Snapshot.Scene.Objects[0].Mesh.Vertices.Length==72,"Writer did not emit per-triangle colored vertices.");
        var exportIn=Path.Combine(output,"assembly-in.ply");Ok(await e.ExportPlyAsync(exportIn,ModelUnit.Inches));readback=New();Ok(await readback.OpenAsync(exportIn,ModelUnit.Inches));Near(readback.Snapshot.Scene.Bounds.Size.X,50);Record(exportIn,ModelUnit.Inches);
        var hollowPath=Path.Combine(output,"hollow-mm.ply");Ok(await hollow.ExportPlyAsync(hollowPath,ModelUnit.Millimeters));readback=New();Ok(await readback.OpenAsync(hollowPath));Near(Volume(readback.Snapshot.Scene),3904);Record(hollowPath,ModelUnit.Millimeters);
        var reflected=New();Ok(await reflected.OpenAsync(Path.Combine(root,".local/evidence/mirror-measurement-20261020/inputs/reflected-affine-tetrahedron.3mf")));var reflectedPath=Path.Combine(output,"reflected-world.ply");Ok(await reflected.ExportPlyAsync(reflectedPath,ModelUnit.Millimeters));readback=New();Ok(await readback.OpenAsync(reflectedPath));Near(Volume(readback.Snapshot.Scene),5304);Record(reflectedPath,ModelUnit.Millimeters);
        var native=Path.Combine(output,"assembly-native.3mf");Ok(await e.SaveAsync(native));before=e.Snapshot;Ok(await e.ExportPlyAsync(exported,ModelUnit.Millimeters));Same(e,before);readback=New();Ok(await readback.OpenAsync(native));Check(Colors(readback.Snapshot.Scene).SequenceEqual(colors),"Native roundtrip lost imported color.");Record(native,ModelUnit.Millimeters);
        Pass("BinaryLE mm/in export preserves world geometry, effective RGB and reflected winding; hollow/native roundtrips retain material volume and export leaves native state intact");

        var destination=New();Ok(destination.Execute(new AddCube()));Ok(destination.UpdateModelMetadata(new("Destination"),destination.Snapshot.Revision));before=destination.Snapshot;
        Ok(await destination.InsertAsync(Path.Combine(inputs,"assembly-in-be.ply"),ModelUnit.Inches));Check(destination.Snapshot.Scene.Objects.Length==2&&destination.Snapshot.Scene.Metadata.Title=="Destination","Insert lost destination metadata or flattened existing objects.");Ok(destination.Undo());Check(ReferenceEquals(destination.Snapshot.Scene,before.Scene),"PLY Insert was not one Undo.");Ok(destination.Redo());
        var grouped=New();Ok(await grouped.OpenAsync(Path.Combine(root,".local/evidence/surface-paint-20261021/inputs/surface-group.3mf")));var group=grouped.Snapshot.Scene.Groups.Single();
        var groupPath=Path.Combine(output,"selected-group.ply");before=grouped.Snapshot;Ok(await grouped.ExportSelectionAsync(groupPath,[group.ObjectIds[0]],unit:ModelUnit.Centimeters));Same(grouped,before);readback=New();Ok(await readback.OpenAsync(groupPath,ModelUnit.Centimeters));Check(readback.Snapshot.Scene.Objects.Length==1&&readback.Snapshot.Scene.Groups.IsEmpty,"PLY selection did not flatten group.");Near(Volume(readback.Snapshot.Scene),16000);Check(Colors(readback.Snapshot.Scene).SequenceEqual(Colors(before.Scene)),"Flattened group lost per-face color ownership.");Record(groupPath,ModelUnit.Centimeters);
        Ok(grouped.EnterGroup(group.Id,group.ObjectIds[0]));var memberPath=Path.Combine(output,"selected-member.ply");before=grouped.Snapshot;Ok(await grouped.ExportSelectionAsync(memberPath,[group.ObjectIds[0]],memberGroupId:group.Id,unit:ModelUnit.Millimeters));Same(grouped,before);readback=New();Ok(await readback.OpenAsync(memberPath));Near(Volume(readback.Snapshot.Scene),8000);Record(memberPath,ModelUnit.Millimeters);
        Pass("PLY Insert is one Undo with destination attribution; group-expanded and exact member exports flatten deliberately while preserving effective surface colors");

        before=e.Snapshot;
        foreach(var name in new[]{"bad-index.ply","truncated.ply","transparent.ply","partial-rgb.ply"}){Check((await e.OpenAsync(Path.Combine(inputs,name))).Outcome==Outcome.Invalid,"Malformed/transparent PLY accepted.");Same(e,before);}
        var malformed=Path.Combine(output,"bad-list-type.ply");File.WriteAllText(malformed,File.ReadAllText(Path.Combine(inputs,"concave-mm.ply")).Replace("list uchar int","list float int",StringComparison.Ordinal));Check((await e.OpenAsync(malformed)).Outcome==Outcome.Invalid,"Floating list count accepted.");Same(e,before);
        Check((await e.ExportPlyAsync(exported,(ModelUnit)99)).Outcome==Outcome.Invalid,"Invalid unit accepted.");Same(e,before);Check((await e.ExportPlyAsync(exported,ModelUnit.Millimeters,expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale export accepted.");Same(e,before);
        Check((await e.ExportPlyAsync(native,ModelUnit.Millimeters)).Outcome==Outcome.Invalid,"Native path overwritten.");Same(e,before);
        Ok(await e.PreviewMaterialAsync(new([e.Snapshot.Scene.Objects[0].Id],new("Held",new(1,2,3)))));var held=e.Snapshot;Check((await e.ExportPlyAsync(exported,ModelUnit.Millimeters)).Outcome==Outcome.Conflict,"PLY export bypassed preview.");Same(e,held);e.DiscardPreview(held.Preview!.Id);before=e.Snapshot;
        using(var ct=new CancellationTokenSource()){Check((await e.InsertAsync(Path.Combine(inputs,"assembly-mm.ply"),cancellation:ct.Token,progress:new Progress(p=>{if(p.Stage=="Reading PLY vertex")ct.Cancel();}))).Outcome==Outcome.Cancelled,"PLY read cancellation failed.");Same(e,before);}
        Pass("Malformed/truncated/transparent/incomplete-color input and invalid units/stale/preview/native-path guards preserve accepted document; cancelled Insert publishes nothing");

        var atomic=Path.Combine(output,"atomic-existing.ply");Ok(await e.ExportPlyAsync(atomic,ModelUnit.Millimeters));var existingHash=Hash(atomic);before=e.Snapshot;
        foreach(var stage in new[]{"Writing PLY vertices","Ready to export PLY"})
        {using var ct=new CancellationTokenSource();Check((await e.ExportPlyAsync(atomic,ModelUnit.Millimeters,ct.Token,new Progress(p=>{if(p.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"PLY precommit cancel failed.");Same(e,before);Check(Hash(atomic)==existingHash,"Cancelled PLY replaced existing output.");}
        using(var locked=new FileStream(atomic,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Check((await e.ExportPlyAsync(atomic,ModelUnit.Millimeters)).Outcome==Outcome.Failed,"Locked PLY replacement succeeded.");Same(e,before);}Check(Hash(atomic)==existingHash,"Failed writer changed old PLY.");
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=e.ExportPlyAsync(atomic,ModelUnit.Millimeters,ct.Token,new Progress(p=>{if(p.Stage=="Ready to export PLY"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));Check(entered.Wait(10000),"No PLY commit barrier.");Check(e.Select(null).Outcome==Outcome.Busy,"Selection bypassed PLY lease.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"PLY busy cancellation failed.");Same(e,before);}
        Check(!Directory.GetFiles(output,"*.tmp").Any(),"Temporary PLY leaked.");Record(atomic,ModelUnit.Millimeters);
        Pass("Atomic single-file cancellation and write failures preserve existing output and accepted state; one busy lease blocks concurrent edits and temporary files are cleaned");
        File.WriteAllText(Path.Combine(output,"ply-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},
            inputs=Directory.GetFiles(inputs,"*.ply").Select(p=>new{path=p,sha256=Hash(p)}),
            limits=new{objects=1000,triangles=100000,vertices=300000,colors=1000,fileBytes=64*1024*1024,polygonVertices=256,headerBytes=65536,elements=32,propertiesPerElement=128,records=700000,numericValues=10000000,unknownListLength=4096},
            limitations="One imported leaf; exports flatten objects/groups/metadata. Opaque uchar RGB only; face colors override vertex colors, otherwise each resulting triangle averages vertex RGB nearest byte away from midpoint. Unknown benign numeric attributes validate but are not retained; texture/material extensions refuse. Normals/custom shading omitted. Writer emits binaryLE double coordinates and duplicated colored triangle vertices plus face RGB; unassigned becomes Neutral. No UI/original-app claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} PLY groups passed. Evidence: {output}");
    }
    private static double Volume(Scene scene)=>scene.Objects.Sum(o=>o.Mesh.Triangles.Sum(t=>{var a=o.Transform.Apply(o.Mesh.Vertices[t.A]);var b=o.Transform.Apply(o.Mesh.Vertices[t.B]);var c=o.Transform.Apply(o.Mesh.Vertices[t.C]);var n=Vec3.Cross(b,c);return(a.X*n.X+a.Y*n.Y+a.Z*n.Z)/6;}));
    private static double Area(Scene scene)=>scene.Objects.Sum(o=>o.Mesh.Triangles.Sum(t=>{var a=o.Transform.Apply(o.Mesh.Vertices[t.A]);var n=Vec3.Cross(o.Transform.Apply(o.Mesh.Vertices[t.B])-a,o.Transform.Apply(o.Mesh.Vertices[t.C])-a);return Math.Sqrt(n.X*n.X+n.Y*n.Y+n.Z*n.Z)/2;}));
    private static string[] Colors(Scene scene)=>scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)?.Color??SurfaceMaterial.Neutral.Color).Hex)).Order().ToArray();
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Near(double value,double expected)=>Check(Math.Abs(value-expected)<.001,$"Expected {expected}, got {value}.");
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted editor state changed.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
