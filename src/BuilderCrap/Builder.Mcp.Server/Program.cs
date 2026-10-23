using Builder.Core;
using Builder.Mcp;
using Builder.Windows.Text;
using Builder.Windows.Images;
using System.IO.Pipes;
using Builder.Hosting;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        using var shutdown=new CancellationTokenSource();
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;shutdown.Cancel();};
        try
        {
            using var installation=InstallationLease.Acquire(AppContext.BaseDirectory);
            if(args is ["--version"])
            {
                Console.WriteLine(JsonSerializer.Serialize(new {product="3D BuilderCrap",version=Assembly.GetEntryAssembly()!.GetName().Version!.ToString(),runtime=Environment.Version.ToString(),architecture=RuntimeInformation.ProcessArchitecture.ToString(),runtimeDirectory=RuntimeEnvironment.GetRuntimeDirectory()}));
                return 0;
            }
            if(args is ["--attach",var pipe])
            {
                if(!pipe.StartsWith("buildercrap-",StringComparison.Ordinal)||pipe.Length!=44||!Guid.TryParseExact(pipe[12..],"N",out _))
                    throw new ArgumentException("Use the exact pipe name shown in desktop View → MCP access.");
                await using var stream=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                using var connecting=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);connecting.CancelAfter(TimeSpan.FromSeconds(5));
                await stream.ConnectAsync(connecting.Token).ConfigureAwait(false);
                using var relay=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                var incoming=Console.OpenStandardInput().CopyToAsync(stream,relay.Token);
                var outgoing=stream.CopyToAsync(Console.OpenStandardOutput(),relay.Token);
                await Task.WhenAny(incoming,outgoing).ConfigureAwait(false);
                relay.Cancel();await stream.DisposeAsync().ConfigureAwait(false);
                try{await Task.WhenAll(incoming,outgoing).ConfigureAwait(false);}catch(Exception e)when(e is OperationCanceledException or IOException or ObjectDisposedException){ }
                return 0;
            }
            if(args is not ["--headless"]&&args is not ["--headless","--recovery",_])
                throw new ArgumentException("Usage: Builder.Mcp.Server --attach <desktop-pipe> | --headless [--recovery <absolute-directory>]. No network listener. Trusted local clients have current-user model/file access; no file-root sandbox.");
            using var editor=new EditorSession(textOutlineProvider:new WindowsFontOutlineProvider(),imageDecoder:new WindowsRasterImageDecoder());
            if(args.Length==3)
            {
                if(!Path.IsPathFullyQualified(args[2]))throw new ArgumentException("Recovery directory must be absolute.");
                var r=editor.ConfigureRecovery(args[2]);if(!r.Succeeded)throw new IOException(r.Message);
            }
            var connection=new EditorMcpConnection(editor);
            try{await LocalMcpServer.RunAsync(Console.OpenStandardInput(),Console.OpenStandardOutput(),connection,shutdown.Token).ConfigureAwait(false);}
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                if(editor.RecoveryDirectory is not null)
                {var recovery=await editor.CheckpointRecoveryAsync().ConfigureAwait(false);if(!recovery.Succeeded)Console.Error.WriteLine("Recovery checkpoint failed during shutdown: "+recovery.Message);}
            }
            return 0;
        }
        catch(OperationCanceledException){Console.Error.WriteLine("MCP connection cancelled or attachment timed out. No fallback session was created.");return 2;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or ModelContextProtocol.McpException)
        {Console.Error.WriteLine(e.Message);return 1;}
    }
}
