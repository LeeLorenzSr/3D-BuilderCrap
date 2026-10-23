using Builder.Core;
using Builder.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static partial class Program
{
    private static string root="",output="",server="";
    private static readonly List<string> passed=[];
    private static readonly List<object> observations=[];
    private static readonly CancellationTokenSource timeout=new(TimeSpan.FromMinutes(3));
    private static CancellationToken Ct=>timeout.Token;
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if(args.Length!=2){Console.Error.WriteLine("Usage: Builder.Mcp.Checks <repo-root> <fresh-evidence-directory>");return 2;}
        root=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);
        if(File.Exists(Path.Combine(output,"results.json")))throw new IOException("Choose a fresh evidence directory.");
        Directory.CreateDirectory(output);server=Path.Combine(root,"src/BuilderCrap/Builder.Mcp.Server/bin/Debug/net10.0-windows/Builder.Mcp.Server.exe");
        try
        {
            await Headless();await PreviewFamilies();await SharedPipe();
            var result=new{utc=DateTimeOffset.UtcNow,method="Real official SDK client over subprocess stdio and same-user pipe proxy; Core state assertions and test-only progress barriers for deterministic in-flight cancellation",passed,observations,
                binaries=new[]{server,Path.Combine(Path.GetDirectoryName(server)!,"Builder.Mcp.dll"),Path.Combine(Path.GetDirectoryName(server)!,"Builder.Core.dll")}.Select(p=>new{path=p,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}),
                limitations="Focused protocol/session checks; native desktop input and isolated WPF checks are recorded separately. No remote/network or adversarial different-user Windows account claim."};
            await File.WriteAllTextAsync(Path.Combine(output,"results.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}),Ct);return 0;
        }
        catch(Exception e){await File.WriteAllTextAsync(Path.Combine(output,"failure.txt"),e.ToString());Console.Error.WriteLine(e);return 1;}
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static void Pass(string message){passed.Add(message);Console.WriteLine("PASS "+message);}
    private static async Task<McpClient> Client(params string[] args)
    {
        var wiretap=Environment.GetEnvironmentVariable("MCP_CHECK_WIRETAP");
        var command=wiretap is null?server:Path.Combine(root,".local/verification-venv/Scripts/python.exe");
        var arguments=wiretap is null?args:new[]{wiretap,Path.Combine(output,"wire-"+Guid.NewGuid().ToString("N")+".log"),server}.Concat(args).ToArray();
        return await McpClient.CreateAsync(new StdioClientTransport(new(){Command=command,Arguments=arguments,ShutdownTimeout=TimeSpan.FromSeconds(3)}),cancellationToken:Ct);
    }
    private static Dictionary<string,object?> Arguments(object value)
        =>JsonSerializer.SerializeToElement(value,value.GetType(),McpJson.Options).EnumerateObject().ToDictionary(p=>p.Name,p=>(object?)p.Value);
    private static async Task<JsonObject> Call(McpClient client,string name,object? arguments=null,string outcome="Success",IProgress<ProgressNotificationValue>? progress=null,CancellationToken? cancellation=null)
    {
        var r=await client.CallToolAsync(name,arguments is null?null:Arguments(arguments),progress,cancellationToken:cancellation??Ct);
        var node=JsonNode.Parse(r.StructuredContent!.Value.GetRawText())!.AsObject();
        Check(node["result"]!["outcome"]!.GetValue<string>()==outcome,name+": "+node.ToJsonString());
        Check(r.IsError==(outcome is "Invalid" or "Failed" or "Conflict" or "Busy"),"MCP error hint disagrees with outcome");
        return node;
    }
    private static long Revision(JsonObject reply)=>reply["state"]!["revision"]!.GetValue<long>();
    private static Guid Primary(JsonObject reply)=>Guid.Parse(reply["state"]!["primaryObjectId"]!.GetValue<string>());
    private static Guid PreviewId(JsonObject reply)=>Guid.Parse(reply["state"]!["preview"]!["id"]!.GetValue<string>());
    private static async Task<JsonObject> Scene(McpClient client)=>await Call(client,"builder_scene");
    private static async Task<JsonObject> Open(McpClient client,string path)=>await Call(client,"builder_open",new{path=Path.GetFullPath(path,root),expectedRevision=Revision(await Scene(client)),unit=ModelUnit.Millimeters,decision=DirtyDecision.Discard});
    private static async Task<JsonObject> Preview(McpClient client,string kind,GeometryRequest request,Guid? replace=null)
        =>await Call(client,"builder_preview_"+kind,new{request=JsonSerializer.SerializeToElement(request,request.GetType(),McpJson.Options),expectedRevision=Revision(await Scene(client)),replacePreviewId=replace});
    private static Task<JsonObject> Finish(McpClient client,JsonObject preview,bool apply=false)=>Call(client,apply?"builder_apply_preview":"builder_cancel_preview",new{previewId=PreviewId(preview),expectedRevision=Revision(preview)},apply?"Success":"Cancelled");
    private static async Task Headless()
    {
        await using var client=await Client("--headless");var tools=await client.ListToolsAsync(cancellationToken:Ct);
        Check(tools.Count==71,"Unexpected tool count");
        var names=tools.Select(t=>t.Name).ToArray();Check(names.Distinct().Count()==71,"Duplicate tool name");
        var poly=tools.Single(t=>t.Name=="builder_preview_polyhedron").JsonSchema.GetRawText();Check(poly.Contains("\"kind\"")&&poly.Contains("Cube"),"Constructor-only Kind omitted from schema");
        Check(!tools.Single(t=>t.Name=="builder_view").ProtocolTool.Annotations!.ReadOnlyHint!.Value,"View preference writes marked read-only");
        await File.WriteAllTextAsync(Path.Combine(output,"catalog.json"),JsonSerializer.Serialize(tools.Select(t=>t.ProtocolTool),new JsonSerializerOptions{WriteIndented=true}),Ct);
        var empty=await Scene(client);Check(empty["state"]!["objectCount"]!.GetValue<int>()==0,"Headless starts nonempty");
        await Call(client,"builder_add_cube",new{edit=new{},expectedRevision=0,unknown=true},"Invalid");
        await Call(client,"builder_add_cube",new{edit=new{}},"Invalid");
        await Call(client,"builder_open",new{path="relative.stl",expectedRevision=0,unit=ModelUnit.Millimeters},"Invalid");
        Pass("Official SDK discovers71 typed tools; required/unknown fields, constructor-only primitive kinds, access instructions and relative-path refusal are explicit");
        var scene=await Open(client,".local/fixtures/distribution/distribution-assembly.3mf");
        scene=await Scene(client);var group=scene["data"]!["groups"]![0]!;
        var groupId=Guid.Parse(group["id"]!.GetValue<string>());var member=Guid.Parse(group["objectIds"]![0]!.GetValue<string>());
        scene=await Call(client,"builder_enter_group",new{groupId,memberId=member,expectedRevision=Revision(scene)});
        var accepted=scene["state"]!["contentId"]!.GetValue<string>();
        await Call(client,"builder_select",new{objectIds=new[]{member},expectedRevision=Revision(scene)},"Conflict");
        scene=await Call(client,"builder_select",new{objectIds=new[]{member},expectedRevision=Revision(scene),memberGroupId=groupId});
        var preview=await Preview(client,"move",new MoveTransform(member,new(7,0,0)){MemberGroupId=groupId});
        Check(preview["state"]!["contentId"]!.GetValue<string>()==accepted,"Preview mutated accepted content");
        var second=await Preview(client,"move",new MoveTransform(member,new(12,0,0)){MemberGroupId=groupId},PreviewId(preview));
        await Call(client,"builder_apply_preview",new{previewId=PreviewId(preview),expectedRevision=Revision(preview)},"Conflict");
        await Finish(client,second);Check((await Scene(client))["state"]!["contentId"]!.GetValue<string>()==accepted,"Cancel changed content");
        preview=await Preview(client,"scale",new ScaleTransform(member,new(2,1,1)){MemberGroupId=groupId});scene=await Finish(client,preview,true);
        var changed=scene["state"]!["contentId"]!.GetValue<string>();Check(changed!=accepted,"Apply did not edit");
        scene=await Call(client,"builder_undo",new{expectedRevision=Revision(scene)});Check(scene["state"]!["contentId"]!.GetValue<string>()==accepted,"Undo mismatch");
        scene=await Call(client,"builder_redo",new{expectedRevision=Revision(scene)});Check(scene["state"]!["contentId"]!.GetValue<string>()==changed,"Redo mismatch");
        Check(scene["state"]!["memberGroupId"]!.GetValue<string>()==groupId.ToString(),"History lost member scope");
        Pass("Real stdio member selection/preview replacement/stale token/Cancel/Apply/Undo/Redo share Core identities, sibling ownership and exact scope");
        var native=Path.Combine(output,"sdk-assembly.3mf");scene=await Call(client,"builder_save",new{path=native,expectedRevision=Revision(scene)});
        File.Copy(native,Path.Combine(output,"sdk-assembly-before-duplicate.3mf"));
        foreach(var (ext,unit)in new[]{(".obj",ModelUnit.Centimeters),(".ply",ModelUnit.Millimeters),(".gltf",ModelUnit.Meters),(".glb",ModelUnit.Inches),(".stl",ModelUnit.Millimeters)})
        {
            var path=Path.Combine(output,"sdk-assembly"+ext);
            var exported=await Call(client,"builder_export",new{path,unit,expectedRevision=Revision(scene),memberGroupId=groupId});
            Check(Revision(exported)==Revision(scene)&&exported["state"]!["filePath"]!.GetValue<string>()==native,"Export changed native state");
        }
        await Call(client,"builder_export",new{path=Path.Combine(output,"sdk-member.3mf"),unit=ModelUnit.Millimeters,objectIds=new[]{member},expectedRevision=Revision(scene),memberGroupId=groupId});
        await Call(client,"builder_resize",new{edit=new ResizeObject(member,new(9,10,14)){MemberGroupId=groupId},expectedRevision=Revision(scene)-1},"Conflict");
        scene=await Call(client,"builder_duplicate",new{objectIds=new[]{member},memberGroupId=groupId,expectedRevision=Revision(scene)});
        var dirty=scene["state"]!["contentId"]!.GetValue<string>();
        var refused=await Call(client,"builder_new",new{expectedRevision=Revision(scene)},"Conflict");Check(refused["data"]!["requiresDecision"]!.GetValue<bool>(),"Dirty New omitted decision");
        await Call(client,"builder_open",new{path=Path.Combine(output,"missing.3mf"),expectedRevision=Revision(scene),decision=DirtyDecision.Cancel},"Cancelled");
        Check((await Scene(client))["state"]!["contentId"]!.GetValue<string>()==dirty,"Cancelled Open lost scene");
        scene=await Call(client,"builder_open",new{path=Path.Combine(output,"missing.3mf"),expectedRevision=Revision(scene),decision=DirtyDecision.Save},"Failed");
        Check(scene["data"]!["saveCommitted"]!.GetValue<bool>()&&!scene["state"]!["isDirty"]!.GetValue<bool>()&&scene["state"]!["contentId"]!.GetValue<string>()==dirty,"Post-save Open failure reported rollback");
        scene=await Call(client,"builder_new",new{expectedRevision=Revision(scene)});Check(scene["state"]!["objectCount"]!.GetValue<int>()==0,"New failed");
        scene=await Open(client,native);Check(scene["state"]!["objectCount"]!.GetValue<int>()==6,"Native reopen count");
        Pass("Native Save/selection export plus STL/OBJ/PLY/glTF/GLB preserve path/history; dirty decisions, cancelled Open, stale edits and SaveCommitted-on-later-failure retain accepted work");
        var id=Primary(scene);var pick=await Call(client,"builder_pick",new{origin=new Vec3(5,5,100),direction=new Vec3(0,0,-1)});
        Check(pick["data"] is not null,"Core pick missing");
        var measured=await Call(client,"builder_measure",new{firstMillimeters=new Vec3(0,0,0),secondMillimeters=new Vec3(3,4,0),expectedRevision=Revision(scene)});
        Check(measured["data"]!["distanceMillimeters"]!.GetValue<double>()==5,"Measurement mismatch");
        await Call(client,"builder_mesh",new{objectId=id,expectedRevision=Revision(scene),limit=12});await Call(client,"builder_diagnose",new{objectId=id,expectedRevision=Revision(scene)});
        var viewRevision=scene["state"]!["presentation"]!["revision"]!.GetValue<long>();
        var view=await Call(client,"builder_view",new{settings=new ViewSettings(Wireframe:true),expectedPresentationRevision=viewRevision});Check(Revision(view)==Revision(scene),"View edited document revision");
        await Call(client,"builder_view",new{settings=new ViewSettings(),expectedPresentationRevision=viewRevision},"Conflict");
        Pass("Paged meshes, Core ray picking/measurement/diagnosis and separate View revisions are accessible without alternate geometry/edit paths");
    }
}
