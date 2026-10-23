using Builder.Core;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

internal static class MaterialChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/paint-materials-20261021/core-first"); Directory.CreateDirectory(output);
        var inputs=Path.Combine(root,".local/evidence/paint-materials-20261021/inputs");
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var cases=new List<string>(); var outputs=new List<object>();
        var red=new SurfaceMaterial("Warm red",new(220,50,40)); var blue=new SurfaceMaterial("Ocean",new(30,90,200));
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(string file="colored-assembly.3mf") {var s=New();Ok(await s.OpenAsync(Path.Combine(inputs,file)));return s;}
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task Paint(EditorSession s,ImmutableArray<Guid> ids,SurfaceMaterial? material,Guid? scope=null)
        {Ok(await s.PreviewAsync(new MaterialRequest(ids,material){MemberGroupId=scope}));Ok(s.ApplyPreview(s.Snapshot.Preview!.Id));}
        async Task Save(EditorSession s,string name)
        {
            var file=Path.Combine(output,name+".3mf");Ok(await s.SaveAsync(file));var reopened=New();Ok(await reopened.OpenAsync(file));
            Check(reopened.Snapshot.Scene.Objects.Select(o=>(o.Name,o.Material,o.Transform,o.Bounds)).SequenceEqual(s.Snapshot.Scene.Objects.Select(o=>(o.Name,o.Material,o.Transform,o.Bounds))),"Native material/name/placement roundtrip changed.");
            Check(reopened.Snapshot.Scene.Groups.Select(g=>g.Name).SequenceEqual(s.Snapshot.Scene.Groups.Select(g=>g.Name)),"Groups lost on material roundtrip.");
            outputs.Add(new {name,path=Path.GetFileName(file),sha256=Hash(file),objects=s.Snapshot.Scene.Objects.Select(o=>new{o.Name,material=o.Material,bounds=o.Bounds,triangles=o.Mesh.Triangles.Length})});
        }

        Check(RgbColor.TryParse("#aB012f",out var color)&&color.Hex=="#AB012F","RGB parser failed.");
        foreach(var invalid in new[]{"AB012F","#AB012FFF","#GG012F"," #AB012F",null}) Check(!RgbColor.TryParse(invalid,out _),"Invalid color accepted.");
        var grouped=await Load();var initial=grouped.Snapshot;var ids=initial.Scene.Objects.Select(o=>o.Id).ToImmutableArray();var group=initial.Scene.Groups.Single();
        Ok(grouped.SelectMany([ids[2]],ids[2]));var selected=grouped.Snapshot;
        Ok(await grouped.PreviewMaterialAsync(new([ids[0]],red with{Name="  Warm red  "})));var p=grouped.Snapshot.Preview!;
        Check(((MaterialRequest)p.Request).ObjectIds.SequenceEqual(group.ObjectIds)&&p.Scene.Objects.Take(2).All(o=>o.Material==red),"Group target expansion/canonical material failed.");
        Check(p.SelectedObjectIds.SequenceEqual(selected.SelectedObjectIds)&&p.SelectedObjectId==selected.SelectedObjectId,"Paint changed selection.");
        for(var i=0;i<3;i++) Check(ReferenceEquals(p.Scene.Objects[i].Mesh,selected.Scene.Objects[i].Mesh)&&p.Scene.Objects[i].Transform==selected.Scene.Objects[i].Transform,"Paint changed geometry.");
        Check(ReferenceEquals(p.Scene.Objects[2],selected.Scene.Objects[2]),"Unrelated object cloned.");Ok(grouped.ApplyPreview(p.Id));Ok(grouped.Undo());Check(ReferenceEquals(grouped.Snapshot.Scene,selected.Scene),"Undo lost exact source.");Ok(grouped.Redo());
        Ok(grouped.EnterGroup(group.Id,ids[0]));var scoped=grouped.Snapshot;Ok(await grouped.PreviewMaterialAsync(new([ids[0]],blue){MemberGroupId=group.Id}));p=grouped.Snapshot.Preview!;
        Check(p.Scene.Objects[0].Material==blue&&ReferenceEquals(p.Scene.Objects[1],scoped.Scene.Objects[1])&&p.Scene.Groups.Single()==group,"Member paint leaked to siblings/group.");Ok(grouped.ApplyPreview(p.Id));
        await Paint(grouped,[ids[0]],null,group.Id);Check(grouped.Snapshot.Scene.Objects[0].Material is null,"Clear failed.");Ok(grouped.Undo());Check(grouped.Snapshot.Scene.Objects[0].Material==blue,"Clear undo failed.");
        Ok(grouped.ExitGroup());var exited=grouped.Snapshot;Check((await grouped.PreviewMaterialAsync(new([ids[0]],red){MemberGroupId=group.Id})).Outcome==Outcome.Conflict,"Stale scope accepted.");Same(grouped,exited);
        await Save(grouped,"painted-members");
        Pass("Opaque RGB/name validation; group expansion and exact member paint/clear preserve geometry, siblings, full selection, groups and history");

        var retained=await Load();var id=retained.Snapshot.Scene.Objects[2].Id;Ok(retained.Execute(new RenameObject(id,"temporary")));Ok(retained.Undo());var baseline=retained.Snapshot;
        var request=new MaterialRequest([id],red);Ok(await retained.PreviewMaterialAsync(request));var first=retained.Snapshot.Preview!;Ok(await retained.PreviewMaterialAsync(request with {Material=blue},first.Id));var held=retained.Snapshot;
        Check(first.Id!=held.Preview!.Id&&ReferenceEquals(held.Scene,baseline.Scene),"Replacement identity/accepted source wrong.");
        Check((await retained.PreviewMaterialAsync(request,first.Id)).Outcome==Outcome.Conflict,"Stale preview accepted.");Same(retained,held);
        Check((await retained.PreviewMaterialAsync(request,held.Preview.Id,expectedRevision:baseline.Revision-1)).Outcome==Outcome.Conflict,"Stale revision accepted.");Same(retained,held);
        foreach(var bad in new[]{new MaterialRequest(default,red),new MaterialRequest([],red),new MaterialRequest([Guid.NewGuid()],red),request with{Material=red with{Name=" "}},request with{Material=red with{Name="bad\nname"}},request with{Material=red with{Name=new string('a',101)}},request with{Material=red with{Name="bad\uD800"}}})
        {Check((await retained.PreviewMaterialAsync(bad,held.Preview.Id)).Outcome==Outcome.Invalid,"Invalid paint accepted.");Same(retained,held);}
        foreach(var stage in new[]{"before","Assigning material","Ready to preview"})
        {using var ct=new CancellationTokenSource();if(stage=="before")ct.Cancel();Check((await retained.PreviewMaterialAsync(request,held.Preview.Id,ct.Token,new Progress(x=>{if(x.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"Cancel failed: "+stage);Same(retained,held);}
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {
            var job=retained.PreviewMaterialAsync(request,held.Preview.Id,ct.Token,new Progress(x=>{if(x.Stage=="Assigning material"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));
            Check(entered.Wait(10000),"Paint did not start.");Check(retained.Select(null).Outcome==Outcome.Busy&&retained.ApplyPreview(held.Preview.Id).Outcome==Outcome.Busy,"Busy guards failed.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy cancellation failed.");Same(retained,held);
        }
        Check(retained.DiscardPreview(held.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(retained,baseline);
        Ok(await retained.PreviewMaterialAsync(new([id],null)));Ok(retained.ApplyPreview(retained.Snapshot.Preview!.Id));Same(retained,baseline);
        Ok(await retained.PreviewTransformAsync(new MoveTransform(id,new(1,0,0))));held=retained.Snapshot;Check((await retained.PreviewMaterialAsync(request,held.Preview!.Id)).Outcome==Outcome.Conflict,"Foreign preview replaced.");Same(retained,held);
        Pass("Failed/cancelled/stale/busy/foreign replacements retain prior candidate and clean redo state; unchanged Apply is fully history-neutral");

        var mixed=New();Ok(mixed.Execute(new AddCube()));var a=mixed.Snapshot.SelectedObjectId!.Value;Ok(mixed.Execute(new DuplicateObject(a)));var b=mixed.Snapshot.SelectedObjectId!.Value;Ok(mixed.Execute(new RenameObject(b,"Cube")));
        Check(ReferenceEquals(mixed.Snapshot.Scene.Objects[0].Mesh,mixed.Snapshot.Scene.Objects[1].Mesh),"Fixture did not share mesh.");await Paint(mixed,[a],red);await Paint(mixed,[b],blue);await Save(mixed,"shared-mesh-distinct-materials");
        var native=Path.Combine(output,"shared-mesh-distinct-materials.3mf");using(var zip=ZipFile.OpenRead(native)){var xml=XDocument.Load(zip.GetEntry("3D/3dmodel.model")!.Open());XNamespace ns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02";var resources=xml.Root!.Element(ns+"resources")!;Check(resources.Elements(ns+"object").Count()==2&&resources.Elements(ns+"basematerials").Single().Elements(ns+"base").Count()==2,"Colored resource identity merged.");}
        var imported=await Load("six-digit-color.3mf");Check(imported.Snapshot.Scene.Objects[0].Material!.Color.Hex=="#E6A23C","Six-digit import failed.");
        var mixedBefore=mixed.Snapshot;Ok(await mixed.InsertAsync(Path.Combine(inputs,"colored-assembly.3mf")));Check(mixed.Snapshot.Scene.Objects.Skip(2).Select(o=>o.Material).SequenceEqual(initial.Scene.Objects.Select(o=>o.Material)),"Insertion dropped materials.");Ok(mixed.Undo());Check(ReferenceEquals(mixed.Snapshot.Scene,mixedBefore.Scene),"Insert undo failed.");Ok(mixed.Redo());await Save(mixed,"inserted-materials");
        var subset=Path.Combine(output,"group-subset.3mf");var importedBefore=imported.Snapshot;Ok(await imported.ExportSelectionAsync(subset,[imported.Snapshot.Scene.Objects[0].Id]));Same(imported,importedBefore);var subsetReader=New();Ok(await subsetReader.OpenAsync(subset));Check(subsetReader.Snapshot.Scene.Objects.Length==2&&subsetReader.Snapshot.Scene.Groups.Length==1&&subsetReader.Snapshot.Scene.Objects.Select(o=>o.Material).SequenceEqual(initial.Scene.Objects.Take(2).Select(o=>o.Material)),"Subset materials/group lost.");
        outputs.Add(new{name="group-subset",path=Path.GetFileName(subset),sha256=Hash(subset)});
        Pass("Shared mesh/name with different materials stays distinct; 6/8-digit imports, native group roundtrip, insertion and selection export retain material names/RGB");

        var badReader=await Load();var badBefore=badReader.Snapshot;
        foreach(var file in new[]{"transparent-material.3mf","missing-material.3mf","duplicate-resource.3mf"}){Check((await badReader.OpenAsync(Path.Combine(inputs,file))).Outcome==Outcome.Invalid,"Unsupported material import accepted: "+file);Same(badReader,badBefore);}
        XNamespace core="http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
        string Mutate(string name,Action<XElement> edit)
        {var file=Path.Combine(output,name+".3mf");File.Copy(Path.Combine(inputs,"colored-assembly.3mf"),file,true);using var zip=ZipFile.Open(file,ZipArchiveMode.Update);var entry=zip.GetEntry("3D/3dmodel.model")!;XDocument doc;using(var stream=entry.Open())doc=XDocument.Load(stream);edit(doc.Root!);entry.Delete();using(var stream=zip.CreateEntry("3D/3dmodel.model").Open())doc.Save(stream);return file;}
        var badFiles=new[]{Mutate("component-material-invalid",m=>m.Descendants(core+"object").First(o=>o.Element(core+"components")!=null).SetAttributeValue("pid",1)),Mutate("bad-index-invalid",m=>m.Descendants(core+"object").First(o=>o.Attribute("pindex")!=null).SetAttributeValue("pindex",999)),Mutate("texture-invalid",m=>m.Element(core+"resources")!.Add(new XElement(core+"texture2d",new XAttribute("id",999)))),
            Mutate("material-entry-limit-invalid",m=>{var list=m.Descendants(core+"basematerials").First();var entry=new XElement(list.Elements(core+"base").First());for(var i=0;i<1000;i++)list.Add(new XElement(entry));})};
        foreach(var file in badFiles){Check((await badReader.OpenAsync(file)).Outcome==Outcome.Invalid,"Bad material reference accepted.");Same(badReader,badBefore);}
        var atomic=Path.Combine(output,"atomic-save.3mf");File.Copy(Path.Combine(inputs,"colored-assembly.3mf"),atomic,true);var originalHash=Hash(atomic);
        using(var ct=new CancellationTokenSource()){Check((await badReader.SaveAsync(atomic,ct.Token,new Progress(x=>{if(x.Stage=="Ready to save")ct.Cancel();}))).Outcome==Outcome.Cancelled,"Save cancellation failed.");}Check(Hash(atomic)==originalHash,"Cancelled save replaced destination.");Same(badReader,badBefore);
        var failPath=Path.Combine(output,"missing-directory","failed.3mf");Check((await badReader.SaveAsync(failPath)).Outcome==Outcome.Failed,"Missing-directory save did not fail.");Same(badReader,badBefore);
        Pass("Transparent/texture/component/invalid-reference/global-ID imports refuse atomically; failed and cancelled saves preserve document and existing destination");

        var geometry=New();Ok(geometry.Execute(new AddCube()));a=geometry.Snapshot.SelectedObjectId!.Value;await Paint(geometry,[a],red);Ok(geometry.Execute(new DuplicateObject(a)));b=geometry.Snapshot.SelectedObjectId!.Value;Check(geometry.Snapshot.Scene.Objects.All(o=>o.Material==red),"Duplicate dropped material.");
        Ok(await geometry.PreviewTransformAsync(new RotateTransform(a,new(0,0,20))));Check(geometry.Snapshot.Preview!.Scene.Objects[0].Material==red,"Transform dropped material.");Check(geometry.DiscardPreview(geometry.Snapshot.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");
        Ok(await geometry.PreviewAsync(new SplitRequest(a,new(1,0,0),10)));Check(geometry.Snapshot.Preview!.Scene.Objects.Where(o=>o.Id!=b).All(o=>o.Material==red),"Split dropped source material.");Check(geometry.DiscardPreview(geometry.Snapshot.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");
        await Paint(geometry,[b],blue);Ok(geometry.Execute(new MoveObject(b,new(20,10,10))));Ok(await geometry.PreviewAsync(new BooleanRequest(a,b,BooleanKind.Union)));Check(geometry.Snapshot.Preview!.Scene.Objects.Single().Material==red,"Boolean did not inherit A material.");Ok(geometry.ApplyPreview(geometry.Snapshot.Preview.Id));await Save(geometry,"boolean-material-a");
        var pieces=New();Ok(await pieces.OpenAsync(Path.Combine(root,".local/evidence/separate-20261020/inputs/two-hollow-boxes.3mf")));a=pieces.Snapshot.SelectedObjectId!.Value;await Paint(pieces,[a],blue);Ok(await pieces.PreviewSeparateAsync(new(a)));Check(pieces.Snapshot.Preview!.Scene.Objects.All(o=>o.Material==blue),"Separate dropped material.");Ok(pieces.ApplyPreview(pieces.Snapshot.Preview.Id));await Save(pieces,"separated-materials");
        Check(new SceneObject(Guid.NewGuid(),"new",Mesh.Cube,Affine.Identity).Material is null,"Creation default assigned material.");
        try{new Scene(Guid.NewGuid(),[new(Guid.NewGuid(),"bad",Mesh.Cube,Affine.Identity){Material=red with{Name=" "}}]).Validate();throw new Exception("Scene validation accepted bad material.");}catch(ModelException){}
        Pass("Duplicate/transform/split/Separate inherit source material; Boolean inherits A; creations are unassigned and direct Scene validation rejects invalid materials");

        File.WriteAllText(Path.Combine(output,"materials-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new{path=worker,sha256=Hash(worker)},
            inputs=Directory.GetFiles(inputs,"*.3mf").Select(path=>new{path,sha256=Hash(path)}),limitations="Uniform opaque display materials only; no triangle paint, texture, transparency or filament assignment. STL is geometry-only. Up to1000 base entries. No worker changes."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} material groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted or prior preview state changed.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
