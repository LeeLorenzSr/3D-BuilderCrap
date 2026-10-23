using Builder.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;
using System.Threading.Channels;

namespace Builder.Mcp;

internal sealed class SessionTool(Tool tool,Func<JsonElement,IProgress<OperationProgress>?,CancellationToken,Task<McpReply>> invoke):McpServerTool
{
    public override Tool ProtocolTool=>tool;
    public override IReadOnlyList<object> Metadata=>[];
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,CancellationToken cancellationToken=default)
    {
        var arguments=JsonSerializer.SerializeToElement(request.Params?.Arguments??new Dictionary<string,JsonElement>(),McpJson.Options);
        await using var progress=new ProgressBridge(request,cancellationToken);
        var reply=await invoke(arguments,progress,cancellationToken).ConfigureAwait(false);
        var structured=McpJson.Object(reply);
        return new(){IsError=reply.Result.Outcome is Outcome.Invalid or Outcome.Failed or Outcome.Busy or Outcome.Conflict,
            StructuredContent=JsonSerializer.SerializeToElement(structured),Content=[new TextContentBlock{Text=structured.ToJsonString()}]};
    }
    private sealed class ProgressBridge:IProgress<OperationProgress>,IAsyncDisposable
    {
        private readonly Channel<OperationProgress> pending=Channel.CreateBounded<OperationProgress>(new BoundedChannelOptions(128){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
        private readonly Task pump;
        public ProgressBridge(RequestContext<CallToolRequestParams> request,CancellationToken ct)
        {
            pump=Task.Run(async()=>
            {
                var step=0;
                try
                {
                    await foreach(var p in pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                        if(request.Params?.ProgressToken is {} token)
                            await request.Server.NotifyProgressAsync(token,new(){Progress=++step,Message=p.Stage+(p.Total>0?$" ({p.Completed}/{p.Total})":"")},cancellationToken:ct).ConfigureAwait(false);
                }
                catch(Exception e)when(e is OperationCanceledException or IOException or McpException or ObjectDisposedException){ }
            },CancellationToken.None);
        }
        public void Report(OperationProgress value)=>pending.Writer.TryWrite(value);
        public async ValueTask DisposeAsync(){pending.Writer.TryComplete();await pump.ConfigureAwait(false);}
    }
}
