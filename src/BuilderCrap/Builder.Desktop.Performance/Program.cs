using Builder.Core;
using Builder.Desktop;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

// A fresh process measures one immutable fixture against explicitly supplied application binaries.
// No production hooks, synthetic document injection, native input or existing app access.
internal static class Program
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static MainWindow window=null!;
    private static EditorSession editor=null!;
    private static FrameworkElement surface=null!;
    private static Viewport3D viewport=null!;
    private static readonly List<object> rows=[];
    private static readonly List<object> assertions=[];
    private static readonly List<string> failures=[];
    private static string fixture="", output="";
    private static int iterations;
    private static bool showWindow;
    private static string lastStage="startup";
    private sealed record Sample(double Milliseconds,long ProcessAllocatedBytes,long UiThreadAllocatedBytes);

    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length<2) { Console.Error.WriteLine("Usage: Builder.Desktop.Performance fixture.stl|3mf output.json [iterations=9]"); return 2; }
        fixture=Path.GetFullPath(args[0]); output=Path.GetFullPath(args[1]); iterations=args.Length>2 ? int.Parse(args[2]) : 9;
        showWindow=args.Length>3 && args[3]=="show";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        _=Task.Run(async ()=> {
            await Task.Delay(TimeSpan.FromMinutes(3));
            File.WriteAllText(output+".timeout.json",JsonSerializer.Serialize(new {failure="Harness watchdog exceeded three minutes",lastStage,fixture}));
            Environment.Exit(3);
        });
        RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var startup=Stopwatch.StartNew();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/Builder;component/Theme.xaml",UriKind.Relative)});
        app.Startup+=async (_,_)=> {
            var exit=0;
            try { await Run(startup); }
            catch(Exception e) { failures.Add(e.ToString()); Console.Error.WriteLine(e); exit=1; }
            finally
            {
                File.WriteAllText(output,JsonSerializer.Serialize(new {
                    utc=DateTime.UtcNow,framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    configuration=$"Release binary references; isolated {(showWindow ? "offscreen shown window" : "detached window content")}; forced software rendering",
                    desktop=new {path=typeof(MainWindow).Assembly.Location,sha256=Hash(typeof(MainWindow).Assembly.Location)},
                    core=new {path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},
                    harness=new {assemblySha256=Hash(typeof(Program).Assembly.Location),sourceSha256=Hash(Path.Combine(AppContext.BaseDirectory,"Program.cs"))},
                    input=new {path=fixture,sha256=Hash(fixture)},iterations,
                    method="Stopwatch elapsed CPU/dispatcher work, process-wide total allocations and UI-thread allocations. Cold means fresh process/first scene, not cold filesystem cache. GC outside timed stages; preview wait polls at 1 ms. Software bitmap render is not physical presentation latency.",
                    stages=rows,assertions,failures,
                    limitations=new[]{"Single workstation, software WPF renderer, no native input or OS dialogs.","Process allocations include runtime/dispatcher/background activity; UI thread count isolates synchronous adapter work.","Working set is process lifetime data, not a managed retained-heap-only metric.","Timing runs must be exclusive; all raw samples are retained."}
                },new JsonSerializerOptions {WriteIndented=true}));
                if(window!=null) { typeof(MainWindow).GetField("closeApproved",Private)!.SetValue(window,true); window.Close(); }
                app.Shutdown(exit);
            }
        };
        return app.Run();
    }

    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Assert(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static void Ok(OperationResult result)=>Assert(result.Succeeded,result.ToString());
    private static object? Invoke(string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,Private)!.Invoke(window,args);
    private static T Named<T>(string name) where T:class=>(T)window.FindName(name);
    private static Sample Time(Action action)
    {
        var allocated=GC.GetTotalAllocatedBytes(true); var thread=GC.GetAllocatedBytesForCurrentThread(); var watch=Stopwatch.StartNew();
        action(); watch.Stop();
        return new(watch.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(true)-allocated,GC.GetAllocatedBytesForCurrentThread()-thread);
    }
    private static async Task<Sample> TimeAsync(Func<Task> action)
    {
        var allocated=GC.GetTotalAllocatedBytes(true); var thread=GC.GetAllocatedBytesForCurrentThread(); var watch=Stopwatch.StartNew();
        await action(); watch.Stop();
        return new(watch.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(true)-allocated,GC.GetAllocatedBytesForCurrentThread()-thread);
    }
    private static void Record(string stage,IEnumerable<Sample> samples,int operationsPerSample=1)
    {
        var values=samples.ToArray(); var sorted=values.Select(v=>v.Milliseconds/operationsPerSample).Order().ToArray();
        lastStage=stage;
        rows.Add(new {stage,operationsPerSample,samples=values,medianMilliseconds=sorted[sorted.Length/2],
            p95Milliseconds=sorted[Math.Max(0,(int)Math.Ceiling(sorted.Length*.95)-1)],minMilliseconds=sorted[0],maxMilliseconds=sorted[^1],
            medianProcessAllocatedBytes=values.Select(v=>v.ProcessAllocatedBytes/operationsPerSample).Order().ElementAt(values.Length/2),
            medianUiThreadAllocatedBytes=values.Select(v=>v.UiThreadAllocatedBytes/operationsPerSample).Order().ElementAt(values.Length/2)});
        Console.WriteLine($"{stage}: median {sorted[sorted.Length/2]:F3} ms, p95 {sorted[Math.Max(0,(int)Math.Ceiling(sorted.Length*.95)-1)]:F3} ms");
        File.WriteAllText(output+".partial.json",JsonSerializer.Serialize(new {lastStage,stages=rows}));
    }
    private static void Bench(string stage,Action action,int operationsPerSample=1)
    {
        var samples=new List<Sample>();
        lastStage=stage;
        for(var i=0;i<3;i++) action();
        for(var i=0;i<iterations;i++) samples.Add(Time(action));
        Record(stage,samples,operationsPerSample);
    }
    private static void Layout()
    {
        if(showWindow) window.UpdateLayout();
        else { surface.Measure(new(1280,780)); surface.Arrange(new Rect(0,0,1280,780)); surface.UpdateLayout(); }
    }
    private static void Render()
    { var bitmap=new RenderTargetBitmap(1280,780,96,96,PixelFormats.Pbgra32); bitmap.Render(surface); }
    private static async Task<object> Memory()
    {
        // WPF finalizers may marshal release work to the dispatcher of a shown window.
        // Keep that dispatcher available while forcing collection outside timed stages.
        await Task.Run(()=>{GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();});
        using var process=Process.GetCurrentProcess(); process.Refresh();
        return new {managedRetainedBytes=GC.GetTotalMemory(false),workingSetBytes=process.WorkingSet64,
            privateBytes=process.PrivateMemorySize64,peakWorkingSetBytes=process.PeakWorkingSet64};
    }
    private static Guid? WpfPick(Point point)
    {
        var map=(Dictionary<GeometryModel3D,Guid>)typeof(MainWindow).GetField("hitObjects",Private)!.GetValue(window)!;
        Guid? selected=null; var nearest=double.PositiveInfinity;
        VisualTreeHelper.HitTest(viewport,null,result=> {
            if(result is RayMeshGeometry3DHitTestResult hit && hit.DistanceToRayOrigin<nearest &&
                hit.ModelHit is GeometryModel3D model && map.TryGetValue(model,out var id))
            { selected=id; nearest=hit.DistanceToRayOrigin; }
            return HitTestResultBehavior.Continue;
        },new PointHitTestParameters(point));
        return selected;
    }
    private static async Task<Guid?> AdapterPick(Point point)
    {
        var method=typeof(MainWindow).GetMethod("PickViewportObject",Private);
        if(method==null) return WpfPick(point);
        var value=method.Invoke(window,[point]);
        return value is Task<Guid?> pending ? await pending : (Guid?)value;
    }
    private static async Task Idle()
    {
        var watch=Stopwatch.StartNew();
        while(editor.Snapshot.IsBusy || typeof(MainWindow).GetField("operation",Private)!.GetValue(window)!=null)
        { Assert(watch.Elapsed<TimeSpan.FromMinutes(2),"Desktop operation timed out."); await Task.Delay(1); }
    }
    private static async Task Preview()
    {
        var before=editor.Snapshot;
        var x=Named<TextBox>("PositionX");
        x.Text=(double.Parse(x.Text,System.Globalization.CultureInfo.CurrentCulture)+1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        Invoke("PreviewTransformClick",window,new RoutedEventArgs()); await Idle();
        Assert(editor.Snapshot.Preview!=null && ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Preview changed accepted document or failed.");
    }

    private static async Task CheckPicking()
    {
        foreach(var view in new[]{"ViewIsometricClick","ViewTopClick","ViewFrontClick","ViewRightClick"})
        {
            Invoke(view,window,new RoutedEventArgs()); Layout();
            var compared=0; var hits=0;
            foreach(var x in new[]{.1,.3,.5,.7,.9}) foreach(var y in new[]{.1,.3,.5,.7,.9})
            {
                var point=new Point(viewport.ActualWidth*x,viewport.ActualHeight*y);
                var expected=WpfPick(point); var actual=await AdapterPick(point);
                Assert(expected==actual,$"WPF/Core nearest leaf mismatch in {view} at {point}: {expected} != {actual}");
                compared++; if(expected!=null) hits++;
            }
            assertions.Add(new {test="Actual WPF picking across camera views",view,compared,hits,passed=true});
        }
        Invoke("ViewIsometricClick",window,new RoutedEventArgs()); Layout();
        var camera=(ProjectionCamera)viewport.Camera;
        var originalNear=camera.NearPlaneDistance; var originalFar=camera.FarPlaneDistance;
        var center=new Point(viewport.ActualWidth/2,viewport.ActualHeight/2);
        camera.FarPlaneDistance=camera.NearPlaneDistance+1;
        Assert(WpfPick(center)==await AdapterPick(center),"WPF/Core far clipping mismatch.");
        assertions.Add(new {test="Actual WPF far-plane nearest leaf equivalence",hit=WpfPick(center),passed=true});
        camera.FarPlaneDistance=originalFar;
        camera.NearPlaneDistance=camera.LookDirection.Length*2;
        Assert(WpfPick(center)==await AdapterPick(center),"WPF/Core near clipping mismatch.");
        assertions.Add(new {test="Actual WPF near-plane nearest leaf equivalence",hit=WpfPick(center),passed=true});
        camera.NearPlaneDistance=originalNear;
        var position=camera.Position; var look=camera.LookDirection; var up=camera.UpDirection;
        var bounds=editor.Snapshot.Scene.Objects[0].Bounds;
        camera.Position=new(bounds.Center.X,bounds.Center.Y,bounds.Center.Z); camera.LookDirection=new(0,0,-1); camera.UpDirection=new(0,1,0);
        camera.NearPlaneDistance=.0001;
        var insideExpected=WpfPick(center);
        Assert(insideExpected!=null && insideExpected==await AdapterPick(center),"WPF/Core inside/back-face nearest leaf mismatch.");
        assertions.Add(new {test="Actual WPF inside-object back-face equivalence",passed=true});
        camera.Position=position; camera.LookDirection=look; camera.UpDirection=up; camera.NearPlaneDistance=originalNear;
    }

    private static async Task Run(Stopwatch startup)
    {
        Record("cold.window.construct",[Time(()=>window=new MainWindow {ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000})]);
        editor=(EditorSession)typeof(MainWindow).GetField("editor",Private)!.GetValue(window)!;
        surface=(FrameworkElement)window.Content; viewport=Named<Viewport3D>("Viewport");
        Record(showWindow ? "cold.window.show-layout" : "cold.detached-layout",[Time(()=>{if(showWindow) window.Show(); Layout();})]);
        await Task.Delay(1);
        rows.Add(new {stage="cold.theme-to-empty-idle",milliseconds=startup.Elapsed.TotalMilliseconds});
        lastStage="memory.empty"; rows.Add(new {stage="memory.empty",memory=await Memory()});
        Record("cold.core-open",[await TimeAsync(async ()=>Ok(await editor.OpenAsync(fixture)))]);
        var initialIds=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToArray();
        assertions.Add(new {objects=editor.Snapshot.Scene.Objects.Length,triangles=editor.Snapshot.Scene.Objects.Sum(o=>(long)o.Mesh.Triangles.Length),
            vertices=editor.Snapshot.Scene.Objects.Sum(o=>(long)o.Mesh.Vertices.Length),uniqueMeshes=editor.Snapshot.Scene.Objects.Select(o=>o.Mesh).Distinct(ReferenceEqualityComparer.Instance).Count()});
        Record("cold.refresh-first-mesh",[Time(()=>{Invoke("Refresh",true); Layout(); Invoke("FitScene");})]);
        Record("cold.software-render",[Time(Render)]);
        lastStage="memory.loaded"; rows.Add(new {stage="memory.loaded",memory=await Memory()});
        var points=(from x in new[]{.2,.35,.5,.65,.8} from y in new[]{.2,.35,.5,.65,.8}
                    select new Point(viewport.ActualWidth*x,viewport.ActualHeight*y)).ToArray();
        Record("cold.wpf-pick",[Time(()=>_ = WpfPick(points[12]))]);
        Record("cold.adapter-pick",[await TimeAsync(async ()=>_ = await AdapterPick(points[12]))]);
        var expected=points.Select(WpfPick).ToArray();
        var actual=new List<Guid?>(); foreach(var point in points) actual.Add(await AdapterPick(point));
        Assert(expected.SequenceEqual(actual),"Desktop picker differs from WPF nearest-hit results.");
        assertions.Add(new {test="25-point actual WPF nearest leaf equivalence",hits=expected.Count(id=>id!=null),passed=true});
        // Warm up every synchronous stage before measured samples.
        for(var i=0;i<5;i++) {Invoke("Refresh",false); Invoke("RenderScene"); Layout();}
        Render(); await Task.Delay(500);
        Bench("warm.selection-target",()=>_ = typeof(MainWindow).GetProperty("SelectedTarget",Private)!.GetValue(window));
        Bench("warm.scene-bounds",()=>_ = editor.Snapshot.Scene.Bounds);
        Bench("warm.refresh-fields",()=>Invoke("Refresh",false));
        Bench("warm.render-scene",()=>Invoke("RenderScene"));
        Bench("warm.camera-update",()=>Invoke("UpdateCamera"));
        Bench("warm.layout",Layout);
        Bench("warm.wpf-pick.batch-average",()=>{foreach(var point in points) _=WpfPick(point);},points.Length);
        var individualWpf=new List<Sample>();
        for(var i=0;i<iterations;i++) foreach(var point in points) individualWpf.Add(Time(()=>_ = WpfPick(point)));
        Record("warm.wpf-pick.individual",individualWpf);
        var adapterSamples=new List<Sample>();
        for(var i=0;i<iterations;i++) adapterSamples.Add(await TimeAsync(async ()=>{foreach(var point in points) _=await AdapterPick(point);}));
        Record("warm.adapter-pick.batch-average",adapterSamples,points.Length);
        var individualAdapter=new List<Sample>();
        for(var i=0;i<iterations;i++) foreach(var point in points) individualAdapter.Add(await TimeAsync(async ()=>_ = await AdapterPick(point)));
        Record("warm.adapter-pick.individual",individualAdapter);
        Bench("warm.software-render",Render);
        Console.WriteLine("Starting numeric transform control loop");
        lastStage="transform.begin";
        Invoke("MoveHandleClick",window,new RoutedEventArgs());
        Named<CheckBox>("SnapTransforms").IsChecked=false;
        var previews=new List<Sample>(); var applies=new List<Sample>(); var undoes=new List<Sample>(); var cancels=new List<Sample>();
        for(var i=0;i<iterations;i++)
        {
            lastStage=$"transform.preview-{i}";
            previews.Add(await TimeAsync(Preview));
            lastStage=$"transform.apply-{i}";
            applies.Add(Time(()=>Invoke("ApplyTransformClick",window,new RoutedEventArgs())));
            Assert(editor.Snapshot.Preview==null,"Apply did not accept preview.");
            lastStage=$"transform.undo-{i}"; undoes.Add(Time(()=>Invoke("UndoClick",window,new RoutedEventArgs())));
            Invoke("MoveHandleClick",window,new RoutedEventArgs());
            lastStage=$"transform.preview-cancel-{i}"; await Preview();
            cancels.Add(Time(()=>Invoke("CancelTransformClick",window,new RoutedEventArgs())));
            Invoke("MoveHandleClick",window,new RoutedEventArgs());
        }
        Invoke("CancelTransformClick",window,new RoutedEventArgs());
        Record("warm.desktop-preview-to-idle",previews); Record("warm.desktop-apply-refresh",applies);
        Record("warm.desktop-undo-refresh",undoes); Record("warm.desktop-cancel-refresh",cancels);
        Assert(editor.Snapshot.Scene.Objects.Select(o=>o.Id).SequenceEqual(initialIds),"Transform path lost object identities.");
        lastStage="memory.after-preview-history"; rows.Add(new {stage="memory.after-preview-history",memory=await Memory()});
        // Return to a fresh document, drain dispatcher, then measure whether obsolete WPF/preview meshes survive.
        Ok(await editor.OpenAsync(fixture)); Invoke("Refresh",true); await Task.Delay(1);
        lastStage="memory.after-reopen-gc"; rows.Add(new {stage="memory.after-reopen-gc",memory=await Memory()});
        assertions.Add(new {test="numeric preview, Apply, Undo, Cancel; accepted identity and fresh reopen",passed=true});
        lastStage="picking-equivalence"; await CheckPicking();
    }
}
