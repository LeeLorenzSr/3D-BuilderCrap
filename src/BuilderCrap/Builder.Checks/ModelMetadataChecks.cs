using Builder.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

internal static class ModelMetadataChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/model-metadata-20261021/core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/model-metadata-20261021/inputs/painted-group.3mf");
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");var cases=new List<string>();var outputs=new List<object>();
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(){var e=New();Ok(await e.OpenAsync(input));return e;}
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        void Record(string path)=>outputs.Add(new{path=Path.GetFileName(path),sha256=Hash(path)});
        var request=new ModelMetadata("Modèle <A> & \"B\" 😀","  ","First line\r\n第二行\rThird\tcolumn", "© 2026 Designer & friends","Use & share.\r\nKeep attribution."){Language="en-US"};
        var expected=request with{Description="First line\n第二行\nThird\tcolumn",LicenseTerms="Use & share.\nKeep attribution."};

        var s=await Load();var group=s.Snapshot.Scene.Groups.Single();var id=s.Snapshot.Scene.Objects[0].Id;Ok(s.EnterGroup(group.Id,id));var original=s.Snapshot;
        Ok(s.UpdateModelMetadata(request,original.Revision));var edited=s.Snapshot;
        Check(edited.Scene.Metadata==expected&&edited.IsDirty&&edited.Revision==original.Revision+1&&edited.SelectedObjectIds.SequenceEqual(original.SelectedObjectIds)&&edited.EditingGroupId==original.EditingGroupId,"Metadata edit lost text/selection/scope or history semantics.");
        Check(edited.Scene.Objects==original.Scene.Objects&&edited.Scene.Groups==original.Scene.Groups,"Metadata edit changed objects/groups/materials.");
        var saved=Path.Combine(output,"model-info-group.3mf");Ok(await s.SaveAsync(saved));var clean=s.Snapshot;var reader=New();Ok(await reader.OpenAsync(saved));Check(reader.Snapshot.Scene.Metadata==expected&&reader.Snapshot.Scene.Objects[0].TriangleMaterials.Count==4&&reader.Snapshot.Scene.Groups.Length==1,"Metadata, language or painted geometry failed native roundtrip.");Record(saved);
        using(var zip=ZipFile.OpenRead(saved)){var doc=XDocument.Load(zip.GetEntry("3D/3dmodel.model")!.Open());var names=doc.Root!.Elements().Select(e=>e.Name.LocalName).ToArray();Check(names.Take(5).All(n=>n=="metadata")&&names[5]=="resources","Metadata not written before resources.");}
        Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,original.Scene)&&s.Snapshot.IsDirty,"Undo across save did not restore exact source.");Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,clean.Scene)&&!s.Snapshot.IsDirty,"Redo lost saved information/clean state.");
        var noChange=s.Snapshot;Ok(s.UpdateModelMetadata(request,noChange.Revision));Same(s,noChange);
        Ok(s.UpdateModelMetadata(ModelMetadata.Empty,s.Snapshot.Revision));Check(s.Snapshot.Scene.Metadata==ModelMetadata.Empty,"Clear model info failed.");Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,clean.Scene),"Clear Undo lost saved metadata.");
        Pass("Unicode/XML escaping, retained language, whitespace-only fields and multiline canonicalization roundtrip with painted groups; no-op/clear/Undo/Redo follow saved clean state");

        Ok(s.Execute(new RenameObject(id,"Renamed member"){MemberGroupId=group.Id}));Check(s.Snapshot.Scene.Metadata==expected,"Ordinary edit lost metadata.");
        Ok(await s.PreviewTransformAsync(new MoveTransform(id,new(1,2,3)){MemberGroupId=group.Id}));Check(s.Snapshot.Preview!.Scene.Metadata==expected,"Transform preview lost metadata.");Check(s.DiscardPreview(s.Snapshot.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");
        Ok(s.ExitGroup());Ok(s.Execute(new AddCube()));var cube=s.Snapshot.SelectedObjectId!.Value;Ok(s.Execute(new DuplicateObject(cube)));var copy=s.Snapshot.SelectedObjectId!.Value;
        Ok(await s.PreviewArrangementAsync(new GroupRequest([cube,copy],"Added pair")));Check(s.Snapshot.Preview!.Scene.Metadata==expected,"Grouping lost metadata.");Ok(s.ApplyPreview(s.Snapshot.Preview.Id));
        Ok(await s.PreviewMaterialAsync(new([cube],new("Amber",new(230,162,60)))));Check(s.Snapshot.Preview!.Scene.Metadata==expected,"Material preview lost metadata.");Check(s.DiscardPreview(s.Snapshot.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");
        var subset=Path.Combine(output,"attributed-subset.3mf");var before=s.Snapshot;Ok(await s.ExportSelectionAsync(subset,[id]));Same(s,before);reader=New();Ok(await reader.OpenAsync(subset));Check(reader.Snapshot.Scene.Metadata==expected&&reader.Snapshot.Scene.Objects.Length==2&&reader.Snapshot.Scene.Objects[0].TriangleMaterials.Count==4,"Subset attribution or paint lost.");Record(subset);
        var stl=Path.Combine(output,"geometry-only.stl");Ok(await s.ExportStlAsync(stl));reader=New();Ok(await reader.OpenAsync(stl));Check(reader.Snapshot.Scene.Metadata==ModelMetadata.Empty,"STL import retained unrelated metadata.");Record(stl);
        Pass("Ordinary edits, geometry/group/material previews and duplicate preserve metadata; selection3MF retains attribution and STL remains geometry-only");

        var destination=New();Ok(destination.Execute(new AddCube()));var destinationInfo=new ModelMetadata("Destination","Current designer"){Language="fr-FR"};Ok(destination.UpdateModelMetadata(destinationInfo,destination.Snapshot.Revision));var destinationBefore=destination.Snapshot;
        var insert=await destination.InsertAsync(saved);Ok(insert);Check(insert.Message.Contains("not merged",StringComparison.Ordinal)&&destination.Snapshot.Scene.Metadata==destinationInfo,"Insert merged or silently ignored source model information.");Ok(destination.Undo());Check(ReferenceEquals(destination.Snapshot.Scene,destinationBefore.Scene),"Insert Undo lost original attribution.");Ok(destination.Redo());
        var insertedFile=Path.Combine(output,"insert-keeps-destination.3mf");Ok(await destination.SaveAsync(insertedFile));Record(insertedFile);
        Ok((await destination.TransitionAsync(new OpenDocumentTransition(saved,destination.Snapshot.Revision,Decision:DirtyDecision.Discard))).Result);Check(destination.Snapshot.Scene.Metadata==expected&&!destination.Snapshot.IsDirty,"3MF Open did not load model information.");
        Ok((await destination.TransitionAsync(new NewDocumentTransition(destination.Snapshot.Revision))).Result);Check(destination.Snapshot.Scene.Metadata==ModelMetadata.Empty&&destination.Snapshot.Scene.Objects.IsEmpty&&!destination.Snapshot.IsDirty,"New retained metadata.");
        Pass("Insert keeps destination information with explicit nonmerge feedback and Undo; 3MF Open loads metadata and New resets it cleanly");

        var guarded=await Load();before=guarded.Snapshot;
        foreach(var bad in new[]{new ModelMetadata(Title:new string('x',257)),new ModelMetadata(Designer:"two\nlines"),new ModelMetadata(Copyright:new string('x',1025)),new ModelMetadata(Description:new string('x',16385)),new ModelMetadata(LicenseTerms:"bad\u0001"),new ModelMetadata(Title:"bad\uD800"),new ModelMetadata(Title:null!),new ModelMetadata(){Language="not a tag"}})
        {Check(guarded.UpdateModelMetadata(bad,before.Revision).Outcome==Outcome.Invalid,"Invalid model information accepted.");Same(guarded,before);}
        Check(guarded.UpdateModelMetadata(request,before.Revision-1).Outcome==Outcome.Conflict,"Stale metadata accepted.");Same(guarded,before);using(var ct=new CancellationTokenSource()){ct.Cancel();Check(guarded.UpdateModelMetadata(request,before.Revision,ct.Token).Outcome==Outcome.Cancelled,"Cancelled metadata edit accepted.");Same(guarded,before);}
        var target=before.Scene.Objects[0].Id;Ok(await guarded.PreviewMaterialAsync(new([target],new("Held",new(1,2,3)))));var held=guarded.Snapshot;Check(guarded.UpdateModelMetadata(request,held.Revision).Outcome==Outcome.Conflict,"Metadata bypassed preview guard.");Same(guarded,held);guarded.DiscardPreview(held.Preview!.Id);
        before=guarded.Snapshot;using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=guarded.InsertAsync(saved,cancellation:ct.Token,progress:new Progress(p=>{if(p.Stage=="Ready to insert"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));Check(entered.Wait(10000),"Insert did not reach boundary.");Check(guarded.UpdateModelMetadata(request,before.Revision).Outcome==Outcome.Busy,"Metadata bypassed busy guard.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Cancel insert failed.");Same(guarded,before);}
        var atomic=Path.Combine(output,"atomic-save.3mf");File.Copy(saved,atomic,true);var hash=Hash(atomic);using(var ct=new CancellationTokenSource()){Check((await guarded.SaveAsync(atomic,ct.Token,new Progress(p=>{if(p.Stage=="Ready to save")ct.Cancel();}))).Outcome==Outcome.Cancelled,"Save cancellation failed.");Same(guarded,before);Check(Hash(atomic)==hash,"Cancelled save replaced file.");}
        using(var locked=new FileStream(atomic,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Check((await guarded.SaveAsync(atomic)).Outcome==Outcome.Failed,"Locked writer did not fail.");Same(guarded,before);}Check(Hash(atomic)==hash,"Failed writer replaced file.");
        Pass("Bounded text/XML validation and stale/cancel/preview/busy guards preserve state; cancelled or faulted writer preserves accepted document and existing file");

        XNamespace core="http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
        string Mutate(string name,Action<XElement> change)
        {var file=Path.Combine(output,name+".3mf");File.Copy(saved,file,true);using var zip=ZipFile.Open(file,ZipArchiveMode.Update);var part=zip.GetEntry("3D/3dmodel.model")!;XDocument doc;using(var stream=part.Open())doc=XDocument.Load(stream,LoadOptions.PreserveWhitespace);change(doc.Root!);part.Delete();using(var stream=zip.CreateEntry("3D/3dmodel.model").Open())doc.Save(stream);return file;}
        var compatible=Mutate("supported-preserve-string",model=>{model.SetAttributeValue(XNamespace.Xmlns+"xs","http://www.w3.org/2001/XMLSchema");var field=model.Elements(core+"metadata").First();field.SetAttributeValue("preserve","true");field.SetAttributeValue("type","xs:string");field.SetAttributeValue(XNamespace.Xml+"lang","en-US");});reader=New();Ok(await reader.OpenAsync(compatible));Check(reader.Snapshot.Scene.Metadata==expected,"Supported preserve/string/model-language semantics lost.");Record(compatible);
        var invalids=new[]{Mutate("duplicate-invalid",m=>m.AddFirst(new XElement(m.Elements(core+"metadata").First()))),Mutate("unknown-preserved-invalid",m=>m.AddFirst(new XElement(core+"metadata",new XAttribute("name","Unknown"),new XAttribute("preserve",1),"Keep"))),
            Mutate("object-metadata-invalid",m=>m.Descendants(core+"object").First().AddFirst(new XElement(core+"metadatagroup",new XElement(core+"metadata",new XAttribute("name","Title"),"Object title")))),
            Mutate("typed-invalid",m=>{m.SetAttributeValue(XNamespace.Xmlns+"xs","http://www.w3.org/2001/XMLSchema");m.Elements(core+"metadata").First().SetAttributeValue("type","xs:integer");}),Mutate("mixed-language-invalid",m=>m.Elements(core+"metadata").First().SetAttributeValue(XNamespace.Xml+"lang","de-DE"))};
        before=reader.Snapshot;foreach(var file in invalids){Check((await reader.OpenAsync(file)).Outcome==Outcome.Invalid,"Unsupported metadata import accepted.");Same(reader,before);}
        Pass("Supported root preserve1/true and verified xs:string retain text/language; duplicate, unknown-preserved, object-scoped, exotic typed and mixed-language metadata refuse atomically");
        File.WriteAllText(Path.Combine(output,"model-metadata-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,request,expected,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},input=new{path=input,sha256=Hash(input)},
            limits=new{title=256,designer=256,copyright=1024,description=16384,licenseTerms=16384,language=128},limitations="Five root model plain-text fields plus retained model XML language; matching per-field language and verified XML-schema string supported. Mixed languages, object/build-scoped metadata, exotic typed fields and unknown preserved fields refuse. Unknown unpreserved fields remain ignored. STL does not carry metadata; Insert does not merge attribution. No worker changes or UI claims."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} model metadata groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted or preview state changed.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
