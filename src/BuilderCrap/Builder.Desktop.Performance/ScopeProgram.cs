using Builder.Core;
using Builder.Desktop;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// Separate whole-group/member profile keeps the canonical desktop benchmark source frozen.
internal static class ScopeProgram
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static MainWindow window=null!;
    private static EditorSession editor=null!;
    private static FrameworkElement surface=null!;
    private static readonly List<object> stages=[];
    private static readonly List<string> failures=[];
    private static string input="",output="";
    private sealed record Sample(double Milliseconds,long AllocatedBytes);
    [STAThread] private static int Main(string[] args)
    {
        input=Path.GetFullPath(args[0]); output=Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/Builder;component/Theme.xaml",UriKind.Relative)});
        app.Startup+=async (_,_)=>{
            var exit=0;
            try {await Run();} catch(Exception e) {failures.Add(e.ToString()); Console.Error.WriteLine(e); exit=1;}
            finally
            {
                File.WriteAllText(output,JsonSerializer.Serialize(new {
                    utc=DateTime.UtcNow,input=new {path=input,sha256=Hash(input)},
                    harness=new {sourceSha256=Hash(Path.Combine(AppContext.BaseDirectory,"ScopeProgram.cs")),assemblySha256=Hash(typeof(ScopeProgram).Assembly.Location)},
                    desktop=new {sha256=Hash(typeof(MainWindow).Assembly.Location)},core=new {sha256=Hash(typeof(EditorSession).Assembly.Location)},
                    method="Fresh process, actual detached WPF content/controls, CPU/dispatcher work, software renderer. Twenty refresh samples; ten preview/Apply/Undo samples. First interaction separate from later samples. No native input or physical-presentation claim.",
                    stages,failures},new JsonSerializerOptions {WriteIndented=true}));
                if(window!=null) typeof(MainWindow).GetField("closeApproved",Private)!.SetValue(window,true);
                app.Shutdown(exit);
            }
        };
        return app.Run();
    }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static object? Invoke(string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,Private)!.Invoke(window,args);
    private static T Named<T>(string name) where T:class=>(T)window.FindName(name);
    private static void Ok(OperationResult result) {if(!result.Succeeded) throw new InvalidOperationException(result.ToString());}
    private static Sample Time(Action action)
    {var allocated=GC.GetTotalAllocatedBytes(true); var sw=Stopwatch.StartNew(); action(); sw.Stop(); return new(sw.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(true)-allocated);}
    private static async Task<Sample> TimeAsync(Func<Task> action)
    {var allocated=GC.GetTotalAllocatedBytes(true); var sw=Stopwatch.StartNew(); await action(); sw.Stop(); return new(sw.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(true)-allocated);}
    private static void Record(string stage,IEnumerable<Sample> values)
    {var samples=values.ToArray(); stages.Add(new {stage,samples}); Console.WriteLine($"{stage}: {samples.Length} samples, median {samples.Select(s=>s.Milliseconds).Order().ElementAt(samples.Length/2):F3} ms");}
    private static void Layout() {surface.Measure(new(1280,780)); surface.Arrange(new Rect(0,0,1280,780)); surface.UpdateLayout();}
    private static void Refresh() {Invoke("Refresh",true); Layout();}
    private static void Rows(string stage)
    {
        var list=Named<ListBox>("ObjectList");
        stages.Add(new {stage,totalRoots=list.Items.Count,realizedRoots=Enumerable.Range(0,list.Items.Count).Count(i=>list.ItemContainerGenerator.ContainerFromIndex(i)!=null),
            visualNodes=Visuals(list),scope=editor.Snapshot.EditingGroupId});
    }
    private static int Visuals(DependencyObject parent)
    {var count=1; for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) count+=Visuals(VisualTreeHelper.GetChild(parent,i)); return count;}
    private static async Task Idle()
    {
        var sw=Stopwatch.StartNew();
        while(editor.Snapshot.IsBusy || typeof(MainWindow).GetField("operation",Private)!.GetValue(window)!=null)
        {if(sw.Elapsed.TotalSeconds>30) throw new TimeoutException("Member preview did not finish."); await Task.Delay(1);}
    }
    private static async Task Run()
    {
        window=new MainWindow(); editor=(EditorSession)typeof(MainWindow).GetField("editor",Private)!.GetValue(window)!; surface=(FrameworkElement)window.Content;
        Ok(await editor.OpenAsync(input));
        var group=editor.Snapshot.Scene.Groups.Single();
        Record("whole.initial-refresh-layout",[Time(Refresh)]); Rows("whole.rows");
        for(var i=0;i<5;i++) Refresh(); await Task.Delay(500);
        Record("whole.refresh-layout",Enumerable.Range(0,20).Select(_=>Time(Refresh)).ToArray());
        Record("member.enter-refresh-layout",[Time(()=>{Ok(editor.EnterGroup(group.Id)); Refresh();})]); Rows("member.rows");
        for(var i=0;i<5;i++) Refresh();
        Record("member.refresh-layout",Enumerable.Range(0,20).Select(_=>Time(Refresh)).ToArray());
        var previews=new List<Sample>(); var applies=new List<Sample>(); var undoes=new List<Sample>();
        for(var i=0;i<10;i++)
        {
            Invoke("MoveHandleClick",window,new RoutedEventArgs()); Named<CheckBox>("SnapTransforms").IsChecked=false;
            previews.Add(await TimeAsync(async ()=>{
                var field=Named<TextBox>("PositionX"); field.Text=(double.Parse(field.Text,System.Globalization.CultureInfo.CurrentCulture)+1).ToString(System.Globalization.CultureInfo.CurrentCulture);
                Invoke("PreviewTransformClick",window,new RoutedEventArgs()); await Idle();
                if(editor.Snapshot.Preview==null) throw new InvalidOperationException("Member preview failed.");
            }));
            applies.Add(Time(()=>{Invoke("ApplyTransformClick",window,new RoutedEventArgs()); Layout();}));
            undoes.Add(Time(()=>{Invoke("UndoClick",window,new RoutedEventArgs()); Layout();}));
        }
        Record("member.preview-sequence",previews); Record("member.apply-sequence",applies); Record("member.undo-sequence",undoes);
        Record("scope.exit-refresh-layout",[Time(()=>{Ok(editor.ExitGroup()); Refresh();})]); Rows("whole.restored-rows");
    }
}
