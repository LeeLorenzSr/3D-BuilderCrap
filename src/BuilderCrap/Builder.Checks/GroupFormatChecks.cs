using Builder.Core;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

/// <summary>Public-session probes for standard 3MF component group interchange.</summary>
internal static class GroupFormatChecks
{
    private static readonly XNamespace Ns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private static readonly XNamespace Rel="http://schemas.openxmlformats.org/package/2006/relationships";
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/groups-20261019/group-format"); Directory.CreateDirectory(output);
        var cases=new List<string>(); var measurements=new List<object>();
        void Pass(string name) {cases.Add(name); Console.WriteLine("PASS "+name);}
        var source=Nested(); var input=Write("nested-inch-components",source);
        var session=new EditorSession(); Ok(await session.OpenAsync(input)); var scene=session.Snapshot.Scene;
        Check(scene.Objects.Length==5 && scene.Groups.Length==2,"Nested component instances did not expand to five leaves and two flat groups.");
        Check(scene.Groups.All(g=>g.ObjectIds.Length==2 && g.Name=="Assembly & <coupled>"),"Group names or membership changed.");
        Check(scene.Objects.All(o=>ReferenceEquals(o.Mesh,scene.Objects[0].Mesh)),"Component/build instances stopped sharing the source mesh.");
        Check(scene.Groups.SelectMany(g=>g.ObjectIds).Distinct().Count()==4,"Independent build groups share member identity.");
        Bounds(scene.Objects[0].Bounds,new(25,10,7),new(65,30,27));
        Bounds(scene.Objects[1].Bounds,new(125,10,7),new(165,30,27));
        Bounds(scene.Objects[2].Bounds,new(25,110,7),new(65,130,27));
        Bounds(scene.Objects[3].Bounds,new(125,110,7),new(165,130,27));
        Bounds(scene.Objects[4].Bounds,new(-70,0,0),new(-50,20,20));
        var saved=Path.Combine(output,"groups-roundtrip.3mf"); Ok(await session.SaveAsync(saved));
        using(var zip=ZipFile.OpenRead(saved))
        {
            using var stream=zip.GetEntry("3D/3dmodel.model")!.Open(); var model=XDocument.Load(stream);
            var resources=model.Root!.Element(Ns+"resources")!.Elements(Ns+"object").ToArray();
            Check(resources.Length==3 && resources.Count(e=>e.Element(Ns+"mesh")!=null)==1 && resources.Count(e=>e.Element(Ns+"components")!=null)==2,
                "Saved groups are not shared mesh resources plus standard component resources.");
            Check(model.Root.Element(Ns+"build")!.Elements(Ns+"item").Count()==3,"A group was flattened into separate build items.");
            Check(model.Descendants().All(e=>e.Name.Namespace==Ns),"Private extension metadata was introduced.");
            Check(resources.Where(e=>e.Element(Ns+"components")!=null).All(e=>(string?)e.Attribute("name")=="Assembly & <coupled>"),"Escaped group names did not serialize.");
        }
        var reopened=new EditorSession(); Ok(await reopened.OpenAsync(saved)); var restored=reopened.Snapshot.Scene;
        Check(restored.Groups.Length==2 && restored.Objects.Length==5,"Save/reopen lost groups or leaves.");
        Check(restored.Objects.All(o=>ReferenceEquals(o.Mesh,restored.Objects[0].Mesh)),"Save/reopen lost mesh sharing.");
        Check(!restored.Objects.Any(o=>scene.Objects.Any(p=>p.Id==o.Id)) && !restored.Groups.Any(g=>scene.Groups.Any(p=>p.Id==g.Id)),"Interchange import reused session IDs.");
        for(var i=0;i<5;i++)
        {
            Check(restored.Objects[i].Name==scene.Objects[i].Name,"Leaf name changed on save/reopen.");
            Bounds(restored.Objects[i].Bounds,scene.Objects[i].Bounds.Min,scene.Objects[i].Bounds.Max);
            for(var v=0;v<scene.Objects[i].Mesh.Vertices.Length;v++)
                Vec(restored.Objects[i].Transform.Apply(restored.Objects[i].Mesh.Vertices[v]),scene.Objects[i].Transform.Apply(scene.Objects[i].Mesh.Vertices[v]));
        }
        Ok(await reopened.ExportStlAsync(Path.Combine(output,"groups-roundtrip.stl")));
        measurements.Add(new {name="nested inch assembly with two independent group instances and a mirrored leaf",bounds=restored.Bounds,
            objects=restored.Objects.Length,groups=restored.Groups.Length,triangles=restored.TriangleCount,expectedVolumeMm3=72000});
        Pass("Nested inch component transforms, repeated group instances, mirrored leaf, names, shared meshes and standard-component save/reopen");

        foreach(var name in new[]{"assembly-mm.3mf","assembly-inch.3mf"})
        {
            Ok(await session.OpenAsync(Path.Combine(root,"fixtures/generated",name)));
            Check(session.Snapshot.Scene.Objects.Length==2 && session.Snapshot.Scene.Groups.IsEmpty,"Mesh-only 3MF acquired a group.");
            Check(ReferenceEquals(session.Snapshot.Scene.Objects[0].Mesh,session.Snapshot.Scene.Objects[1].Mesh),"Mesh-only resource sharing changed.");
        }
        var singleton=Model([MeshObject(1,Mesh.Cube),Components(2,"Wrapper",Component(1,"1 0 0 0 1 0 0 0 1 3 4 5"))],[Item(2)]);
        Ok(await session.OpenAsync(Write("single-component",singleton)));
        Check(session.Snapshot.Scene.Objects.Length==1 && session.Snapshot.Scene.Groups.IsEmpty,"One-leaf wrapper created an invalid singleton group.");
        Bounds(session.Snapshot.Scene.Bounds,new(3,4,5),new(23,24,25));
        Pass("Mesh-only instances remain ungrouped/shared; one-leaf component wrapper remains an ordinary object");

        var unnamed=Model([MeshObject(1,Mesh.Cube),Components(2,"   ",Component(1),Component(1,"1 0 0 0 1 0 0 0 1 30 0 0"))],[Item(2)]);
        Ok(await session.OpenAsync(Write("unnamed-group",unnamed))); Check(session.Snapshot.Scene.Groups.Single().Name=="Group 2","Blank component group name was not normalized.");
        var stable=session.Snapshot;
        foreach(var fault in new[]{"cycle","missing","singular","empty","both","long-name"})
        {
            var broken=new XDocument(source); var resources=broken.Root!.Element(Ns+"resources")!; var target=resources.Elements(Ns+"object").Single(e=>(string?)e.Attribute("id")=="3");
            var component=target.Descendants(Ns+"component").First();
            switch(fault)
            {
                case "cycle": component.SetAttributeValue("objectid",3); break;
                case "missing": component.SetAttributeValue("objectid",999); break;
                case "singular": component.SetAttributeValue("transform","0 0 0 0 0 0 0 0 0 0 0 0"); break;
                case "empty": target.Element(Ns+"components")!.RemoveNodes(); break;
                case "both": target.Add(new XElement(resources.Elements(Ns+"object").First().Element(Ns+"mesh")!)); break;
                default: target.SetAttributeValue("name",new string('x',101)); break;
            }
            Check(!(await session.OpenAsync(Write("invalid-"+fault,broken))).Succeeded,"Malformed group input accepted: "+fault);
            Check(session.Snapshot==stable,"Malformed group import changed accepted state: "+fault);
        }
        Pass("Blank group name normalization; cycles, missing references, singular transforms, empty/ambiguous resources and overlong group names fail atomically");

        var tooMany=Model([MeshObject(1,Mesh.Cube),Components(2,"Too many",Enumerable.Range(0,1001).Select(_=>Component(1)).ToArray())],[Item(2)]);
        await Refused("expanded-objects",tooMany);
        var triangleHeavy=new Mesh(Mesh.Cube.Vertices,Enumerable.Repeat(Mesh.Cube.Triangles[0],1001));
        await Refused("expanded-triangles",Model([MeshObject(1,triangleHeavy),Components(2,"Triangles",Enumerable.Range(0,100).Select(_=>Component(1)).ToArray())],[Item(2)]));
        var vertexHeavy=new Mesh(Mesh.Cube.Vertices.Concat(Enumerable.Repeat(new Vec3(10,10,10),2993)),Mesh.Cube.Triangles);
        await Refused("expanded-vertices",Model([MeshObject(1,vertexHeavy),Components(2,"Vertices",Enumerable.Range(0,100).Select(_=>Component(1)).ToArray())],[Item(2)]));
        await Refused("resource-count",Model(Enumerable.Range(1,1001).Select(i=>MeshObject(i,Mesh.Cube)),[Item(1)]));
        for(var depth=32;depth<=33;depth++)
        {
            var resources=new List<XElement>{MeshObject(1,Mesh.Cube)};
            for(var id=2;id<=depth;id++) resources.Add(Components(id,"Depth "+id,Component(id-1)));
            var file=Write("resource-depth-"+depth,Model(resources,[Item(depth)]));
            if(depth==32) {Ok(await session.OpenAsync(file)); Check(session.Snapshot.Scene.Groups.IsEmpty,"One-leaf nested chain acquired a group."); stable=session.Snapshot;}
            else {Check(!(await session.OpenAsync(file)).Succeeded,"Over-depth resource chain accepted."); Check(session.Snapshot==stable,"Over-depth input changed state.");}
        }
        Pass("Expanded object/triangle/vertex, resource-count and 32-resource-path nesting limits reject safely");

        using(var ct=new CancellationTokenSource())
        {
            var reached=false;
            var result=await session.OpenAsync(input,cancellation:ct.Token,progress:new ProgressInline(p=>{if(p.Stage=="Reading objects" && p.Completed==1){reached=true;ct.Cancel();}}));
            Check(reached && result.Outcome==Outcome.Cancelled && session.Snapshot==stable,"Cancellation after a privately expanded leaf changed accepted state.");
        }
        Ok(await session.OpenAsync(input)); Ok(await session.SaveAsync(saved)); stable=session.Snapshot; var good=File.ReadAllBytes(saved);
        foreach(var stage in new[]{"Writing groups","Ready to save"})
        {
            using var ct=new CancellationTokenSource(); var reached=false;
            var result=await session.SaveAsync(saved,ct.Token,new ProgressInline(p=>{if(p.Stage==stage){reached=true;ct.Cancel();}}));
            Check(reached && result.Outcome==Outcome.Cancelled && session.Snapshot==stable,"Group save cancellation changed accepted state: "+stage);
            Check(File.ReadAllBytes(saved).SequenceEqual(good),"Group save cancellation replaced last-good destination.");
            Check(!Directory.EnumerateFiles(output,"*.tmp").Any(),"Group save cancellation left a temporary file.");
        }
        Pass("Cancellation after private leaf expansion, during group serialization and before atomic replacement preserves scene/groups and last-good file");

        var budget=new EditorSession();
        for(var i=0;i<1000;i++) Ok(budget.Execute(new AddCube()));
        var ids=budget.Snapshot.Scene.Objects.Select(o=>o.Id).ToArray();
        for(var i=0;i<ids.Length;i+=2)
        {
            Ok(await budget.PreviewArrangementAsync(new GroupRequest(ImmutableArray.Create(ids[i],ids[i+1]),"Pair "+i)));
            Ok(budget.ApplyPreview(budget.Snapshot.Preview!.Id));
        }
        var budgetBefore=budget.Snapshot; var destination=Path.Combine(output,"resource-budget-last-good.3mf"); File.Copy(saved,destination,true);
        var oldDestination=File.ReadAllBytes(destination);
        Check((await budget.SaveAsync(destination)).Outcome==Outcome.Invalid,"Writer emitted more than 1,000 resource definitions.");
        Check(budget.Snapshot==budgetBefore && File.ReadAllBytes(destination).SequenceEqual(oldDestination),"Resource-budget save failure changed scene or last-good file.");
        Check(!Directory.EnumerateFiles(output,"*.tmp").Any(),"Resource-budget failure left a temporary file.");
        Pass("A valid 1,000-object / 500-group scene exceeding the 3MF resource budget fails save without replacing accepted scene or destination");
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,runtime=Environment.Version.ToString(),cases,measurements,
            coreSha256=Hash(typeof(EditorSession).Assembly.Location),files=Directory.GetFiles(output).Where(p=>Path.GetExtension(p) is ".3mf" or ".stl").Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)})},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} group-format scenario groups passed. Evidence: {output}");

        async Task Refused(string name,XDocument model)
        {Check(!(await session.OpenAsync(Write(name,model))).Succeeded,"Limit input accepted: "+name); Check(session.Snapshot==stable,"Limit failure changed state: "+name);}
        string Write(string name,XDocument model)
        {
            var path=Path.Combine(output,name+".3mf"); using var stream=File.Create(path); using var zip=new ZipArchive(stream,ZipArchiveMode.Create);
            return SavePackage(zip,path,model);
        }
    }

    private static string SavePackage(ZipArchive zip,string path,XDocument model)
    {
        void Part(string name,XDocument document) {using var stream=zip.CreateEntry(name).Open(); document.Save(stream);}
        XNamespace content="http://schemas.openxmlformats.org/package/2006/content-types";
        Part("[Content_Types].xml",new(new XElement(content+"Types",new XElement(content+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(content+"Default",new XAttribute("Extension","model"),new XAttribute("ContentType","application/vnd.ms-package.3dmanufacturing-3dmodel+xml")))));
        Part("_rels/.rels",new(new XElement(Rel+"Relationships",new XElement(Rel+"Relationship",new XAttribute("Id","r0"),new XAttribute("Type","http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"),new XAttribute("Target","/3D/3dmodel.model")))));
        Part("3D/3dmodel.model",model); return path;
    }
    private static XDocument Nested()
    {
        var scale=1/25.4; var cube=Mesh.Cube;
        var inchMesh=new Mesh(cube.Vertices.Select(v=>v*scale),cube.Triangles);
        string Transform(Affine a)=>string.Join(" ",new[]{a.X.X,a.X.Y,a.X.Z,a.Y.X,a.Y.Y,a.Y.Z,a.Z.X,a.Z.Y,a.Z.Z,a.Offset.X*scale,a.Offset.Y*scale,a.Offset.Z*scale}.Select(n=>n.ToString("R",CultureInfo.InvariantCulture)));
        var rotate=Affine.RotateAbout(new(0,0,90),new()).Then(Affine.Translation(new(30,0,0)));
        return Model([MeshObject(1,inchMesh,"Leaf & \"quoted\""),
            Components(2,"Inner wrapper",Component(1,Transform(rotate)),Component(1,Transform(Affine.Translation(new(60,0,0))))),
            Components(3," Assembly & <coupled> ",Component(2,Transform(Affine.ScaleAbout(new(2,1,1),new()).Then(Affine.Translation(new(0,10,0))))))],
            [Item(3,Transform(Affine.Translation(new(5,0,7)))),Item(3,Transform(Affine.Translation(new(5,100,7)))),Item(1,Transform(Affine.ScaleAbout(new(-1,1,1),new()).Then(Affine.Translation(new(-50,0,0)))))],"inch");
    }
    private static XDocument Model(IEnumerable<XElement> resources,IEnumerable<XElement> items,string unit="millimeter")=>
        new(new XElement(Ns+"model",new XAttribute("unit",unit),new XElement(Ns+"resources",resources),new XElement(Ns+"build",items)));
    private static XElement MeshObject(int id,Mesh mesh,string name="Leaf")=>new(Ns+"object",new XAttribute("id",id),new XAttribute("type","model"),new XAttribute("name",name),
        new XElement(Ns+"mesh",new XElement(Ns+"vertices",mesh.Vertices.Select(v=>new XElement(Ns+"vertex",new XAttribute("x",v.X.ToString("R",CultureInfo.InvariantCulture)),new XAttribute("y",v.Y.ToString("R",CultureInfo.InvariantCulture)),new XAttribute("z",v.Z.ToString("R",CultureInfo.InvariantCulture))))),
            new XElement(Ns+"triangles",mesh.Triangles.Select(t=>new XElement(Ns+"triangle",new XAttribute("v1",t.A),new XAttribute("v2",t.B),new XAttribute("v3",t.C))))));
    private static XElement Components(int id,string name,params XElement[] children)=>new(Ns+"object",new XAttribute("id",id),new XAttribute("name",name),new XElement(Ns+"components",children));
    private static XElement Component(int id,string? transform=null)=>Reference("component",id,transform);
    private static XElement Item(int id,string? transform=null)=>Reference("item",id,transform);
    private static XElement Reference(string element,int id,string? transform)=>new(Ns+element,new XAttribute("objectid",id),transform==null?null:new XAttribute("transform",transform));
    private static void Check(bool condition,string message) {if(!condition) throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-8,$"Expected {b}, got {a}.");
    private static void Vec(Vec3 a,Vec3 b) {Near(a.X,b.X); Near(a.Y,b.Y); Near(a.Z,b.Z);}
    private static void Bounds(Bounds3 bounds,Vec3 min,Vec3 max) {Vec(bounds.Min,min); Vec(bounds.Max,max);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class ProgressInline(Action<OperationProgress> report):IProgress<OperationProgress> {public void Report(OperationProgress p)=>report(p);}
}
