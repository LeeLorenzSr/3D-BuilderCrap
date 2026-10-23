using Builder.Core;
using Builder.Desktop;
using Builder.Mcp;
using ModelContextProtocol.Client;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private static Task McpPrivate(string method)=>(Task)typeof(MainWindow).GetMethod(method,PrivateInstance)!.Invoke(window,[])!;
    private static void McpVoid(string method)=>typeof(MainWindow).GetMethod(method,PrivateInstance)!.Invoke(window,[]);
    private static DesktopMcpHost? McpHost=>(DesktopMcpHost?)typeof(MainWindow).GetField("mcpHost",PrivateInstance)!.GetValue(window);
    private static async Task<JsonObject> McpCall(McpClient client,string name,object? value=null,string outcome="Success")
    {
        var args=value is null?null:JsonSerializer.SerializeToElement(value,value.GetType(),McpJson.Options).EnumerateObject().ToDictionary(p=>p.Name,p=>(object?)p.Value);
        var result=await client.CallToolAsync(name,args,cancellationToken:new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var data=JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
        Assert(data["result"]!["outcome"]!.GetValue<string>()==outcome,"MCP control "+name+": "+data.ToJsonString());return data;
    }
    private static async Task RunMcpControls()
    {
        await OpenDistribution();await Workspace(4);Layout(1040,680);await Idle();
        Assert(McpHost is null,"Desktop MCP enabled by default");Reachable("McpAccessButton");
        var dialog=new McpAccessDialog(()=>McpHost?.PipeName,()=>McpHost?.LastError,()=>McpVoid("EnableMcp"),()=>McpPrivate("StopMcp"));
        McpClient? client=null;
        try
        {
            var content=(FrameworkElement)dialog.Content;content.Measure(new(620,500));content.Arrange(new Rect(0,0,620,500));content.UpdateLayout();
            var config=Tree(content).OfType<TextBox>().Single();Assert(!config.Text.Contains("buildercrap-"),"Disabled dialog exposes endpoint");
            var enable=Tree(content).OfType<Button>().Single(b=>b.Content?.ToString()=="Enable access");Click(enable);Assert(McpHost is not null,"Enable failed");
            var name=McpHost!.PipeName;Assert(config.Text.Contains(name)&&config.Text.Contains("--attach")&&config.IsReadOnly,"Missing usable attachment config");
            client=await McpClient.CreateAsync(new StdioClientTransport(new(){Command=Path.Combine(root,"prototypes/Builder/Builder.Mcp.Server/bin/Debug/net10.0-windows/Builder.Mcp.Server.exe"),Arguments=["--attach",name]}));
            var queried=await McpCall(client,"builder_scene");Assert(queried["state"]!["contentId"]!.GetValue<string>()==editor.Snapshot.Scene.ContentId.ToString(),"Desktop and MCP have different document");
            Capture("mcp-view-access",1040,680);
            Pass("View offers reachable opt-in local access at minimum client size; disabled startup, explicit same-user/file-permission rules and copyable per-window stdio configuration precede shared-session attachment");
            await Workspace(1);var before=editor.Snapshot;var group=before.Scene.Groups.Single();var id=group.ObjectIds[0];
            var prepared=await McpCall(client,"builder_preview_move",new{request=new MoveTransform(id,new(8,0,0)),expectedRevision=before.Revision});await Idle();
            Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.Preview is not null,"Remote preview committed");
            Assert(Named<FrameworkElement>("McpPreviewActions").Visibility==Visibility.Visible,"Remote preview footer hidden");
            foreach(var control in new[]{"ApplyMcpPreviewButton","CancelMcpPreviewButton"})Reachable(control);
            Capture("mcp-preview-controls",1040,680);Click(Named<Button>("CancelMcpPreviewButton"));await Idle();
            Assert(editor.Snapshot.Preview is null&&ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Desktop Cancel accepted remote preview");
            await McpCall(client,"builder_apply_preview",new{previewId=prepared["state"]!["preview"]!["id"]!.GetValue<string>(),expectedRevision=before.Revision},"Conflict");
            prepared=await McpCall(client,"builder_preview_move",new{request=new MoveTransform(id,new(8,0,0)),expectedRevision=editor.Snapshot.Revision});
            Click(Named<Button>("ApplyMcpPreviewButton"));await Idle();var moved=editor.Snapshot;
            Assert(moved.Revision==before.Revision+1&&moved.Preview is null&&moved.CanUndo,"Desktop Apply did not use shared history");
            await McpCall(client,"builder_undo",new{expectedRevision=moved.Revision});await Idle();Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Remote Undo mismatch");
            Click(Named<Button>("RedoButton"));await Idle();Assert(ReferenceEquals(editor.Snapshot.Scene,moved.Scene),"Desktop Redo mismatch");
            Pass("Remote group previews render with fixed Apply/Cancel controls; desktop acceptance/cancellation and MCP Undo/desktop Redo use the same scene, ownership tokens and history");
            Ok(editor.Select(id));Refresh();Click(Named<Button>("ScaleHandleButton"));await Idle();
            var local=editor.Snapshot;Named<TextBox>("SizeX").Text="33";
            await McpCall(client,"builder_undo",new{expectedRevision=local.Revision},"Conflict");
            Assert(Named<TextBox>("SizeX").Text=="33"&&ReferenceEquals(local.Scene,editor.Snapshot.Scene),"Local resize draft lost");
            var view=editor.Presentation.Snapshot;
            await McpCall(client,"builder_view",new{settings=view.Settings with{Wireframe=true},expectedPresentationRevision=view.Revision});
            Assert(Named<CheckBox>("ViewWireframe").IsChecked==true&&Named<TextBox>("SizeX").Text=="33","Remote View did not sync controls or retain local draft");
            Click(Named<Button>("CancelTransformButton"));await Idle();
            Named<TextBox>("ObjectName").Text="Unaccepted group draft";
            await McpCall(client,"builder_undo",new{expectedRevision=editor.Snapshot.Revision},"Conflict");Assert(Named<TextBox>("ObjectName").Text=="Unaccepted group draft","Rename draft lost");Refresh();
            typeof(MainWindow).GetField("documentPromptActive",PrivateInstance)!.SetValue(window,true);
            try{await McpCall(client,"builder_undo",new{expectedRevision=editor.Snapshot.Revision},"Conflict");}
            finally{typeof(MainWindow).GetField("documentPromptActive",PrivateInstance)!.SetValue(window,false);}
            Pass("Desktop tools, numeric/name drafts and document prompts refuse remote document mutations; read queries remain available and remote View updates preserve draft/document state");
            var accepted=editor.Snapshot;
            await McpCall(client,"builder_preview_rotate",new{request=new RotateTransform(id,new(0,0,45)),expectedRevision=accepted.Revision});await Idle();
            await McpPrivate("StopMcp");await Idle();
            Assert(McpHost is null&&editor.Snapshot.Preview is null&&ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&editor.Snapshot.CanUndo==accepted.CanUndo,"Stop changed accepted document/history");
            Assert(Named<FrameworkElement>("McpPreviewActions").Visibility==Visibility.Collapsed,"Remote footer leaked after Stop");
            Click(Named<Button>("MoveHandleButton"));await Idle();Assert(Named<FrameworkElement>("TransformActions").Visibility==Visibility.Visible,"Local tools did not resume after Stop");Click(Named<Button>("CancelTransformButton"));
            Pass("Stop cancels attachment and its private preview while retaining accepted scene/history; remote footer clears and ordinary desktop editing resumes");
        }
        finally{if(McpHost is not null)await McpPrivate("StopMcp");if(client is not null)await client.DisposeAsync();dialog.Close();}
    }
}
