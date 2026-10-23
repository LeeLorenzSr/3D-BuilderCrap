using Builder.Core;
using Builder.Desktop;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

// Trace-only evolution of frozen A15D caller; original caller and product remain unchanged. Fixed 120 warm Enter/Done pairs.
internal static class LatencyTraceProgram
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static MainWindow window=null!;
    private static EditorSession editor=null!;
    private static FrameworkElement surface=null!;
    private static readonly List<object> stages=[];
    private static readonly List<string> assertions=[],failures=[];
    private static readonly List<object> behavior=[];
    private static string input="",output="",lastStage="startup";
    private static string initialInputHash="";
    private static double expectedDoneOffset;
    private static TracePhases? trace;
    private sealed record Sample(double Milliseconds,long ProcessAllocatedBytes,long UiThreadAllocatedBytes);

    [STAThread] private static int Main(string[] args)
    {
        input=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);initialInputHash=Hash(input);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        _=Task.Run(async()=>{await Task.Delay(TimeSpan.FromMinutes(3));File.WriteAllText(output+".timeout.json",JsonSerializer.Serialize(new{lastStage,input}));Environment.Exit(3);});
        RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/Builder;component/Theme.xaml",UriKind.Relative)});
        app.Startup+=async(_,_)=>{
            var exit=0;
            try{await Run();Check(Hash(input)==initialInputHash,"Input hash changed.");}
            catch(Exception e){failures.Add(e.ToString());Console.Error.WriteLine(e);exit=1;}
            finally{
                File.WriteAllText(output,JsonSerializer.Serialize(new{
                    utc=DateTimeOffset.UtcNow,input=new{path=input,sha256=initialInputHash},
                    harness=new{sourceSha256=Hash(Path.Combine(AppContext.BaseDirectory,"LatencyTraceProgram.cs")),projectSha256=Hash(Path.Combine(AppContext.BaseDirectory,"Builder.Desktop.LatencyTrace.csproj")),assemblySha256=Hash(typeof(LatencyTraceProgram).Assembly.Location)},
                    desktop=new{path=typeof(MainWindow).Assembly.Location,sha256=Hash(typeof(MainWindow).Assembly.Location)},
                    core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},
                    tools=new{runtime=Environment.Version.ToString(),os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,Environment.ProcessorCount,serverGc=System.Runtime.GCSettings.IsServerGC},
                    configuration="Release referenced binaries; detached actual WPF content; forced software rendering; fixed1280x780 DIP layout; no raster timing",
                    method="Three fresh processes per version. Cold structural interactions separated from repeated warm calls. Each timed Refresh/Enter/Done/selection/Apply/Undo explicitly measures and arranges actual WPF content and UpdateLayout. Preview waits for operation idle then forces layout. Process-wide and UI-thread allocation deltas; no native input, physical presentation or peak/native memory claim.",
                    gates=new{initialExpandedCpuLayoutMilliseconds=50,doneCpuLayoutMilliseconds=50,memberApplyUndoCpuLayoutMilliseconds=50},
                    trace=new{trace?.ProcessId,trace?.UiNativeThreadId,trace?.Frequency,phaseSourceSha256=Hash(Path.Combine(AppContext.BaseDirectory,"TracePhases.cs")),samples=trace?.Samples},stages,assertions,behavior,failures},new JsonSerializerOptions{WriteIndented=true}));
                if(window!=null)typeof(MainWindow).GetField("closeApproved",Private)!.SetValue(window,true);
                app.Shutdown(exit);
            }
        };
        return app.Run();
    }

    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static object? Invoke(string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,Private)!.Invoke(window,args);
    private static T Named<T>(string name)where T:class=>(T)window.FindName(name);
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static Sample Time(Action action){var bytes=GC.GetTotalAllocatedBytes(true);var thread=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();action();watch.Stop();return new(watch.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(true)-bytes,GC.GetAllocatedBytesForCurrentThread()-thread);}
    private static async Task<Sample> TimeAsync(Func<Task> action){var bytes=GC.GetTotalAllocatedBytes(true);var thread=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();await action();watch.Stop();return new(watch.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(true)-bytes,GC.GetAllocatedBytesForCurrentThread()-thread);}
    private static void Record(string stage,IEnumerable<Sample> samples){lastStage=stage;var values=samples.ToArray();stages.Add(new{stage,samples=values});Console.WriteLine($"{stage}: n={values.Length}, median {values.Select(s=>s.Milliseconds).Order().ElementAt(values.Length/2):F3} ms");File.WriteAllText(output+".partial.json",JsonSerializer.Serialize(new{lastStage,stages}));}
    private static void Layout(){surface.Measure(new(1280,780));surface.Arrange(new Rect(0,0,1280,780));surface.UpdateLayout();}
    private static void Refresh(){if(trace==null){Invoke("Refresh",true);Layout();}else{trace.Phase("handler-refresh",()=>Invoke("Refresh",true));trace.Phase("refresh-inline-layout",Layout);}}
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent){yield return parent;for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Visuals(VisualTreeHelper.GetChild(parent,i)))yield return child;}
    // Old templates bind SceneTreeNode; flat virtual rows expose the same node as Node.
    private static SceneTreeNode? Node(object? value)=>value as SceneTreeNode ?? value?.GetType().GetProperty("Node")?.GetValue(value)as SceneTreeNode;
    private static object Row(Guid id)=>Named<ListBox>("ObjectList").Items.Cast<object>().FirstOrDefault(item=>Node(item)?.Id==id)
        ?? Named<ListBox>("ObjectList").Items.Cast<object>().First(item=>Node(item)?.ObjectIds.Contains(id)==true);
    private static ScrollViewer Scroller()=>Visuals(Named<ListBox>("ObjectList")).OfType<ScrollViewer>().First();
    private static bool MemberVisible(Guid id)
    {
        var list=Named<ListBox>("ObjectList");var viewport=new Rect(0,0,list.ActualWidth,list.ActualHeight);
        return Visuals(list).OfType<FrameworkElement>().Where(e=>Node(e.DataContext)?.Id==id&&e.ActualHeight>0&&e.ActualWidth>0)
            .Any(e=>e.TransformToAncestor(list).TransformBounds(new Rect(0,0,e.ActualWidth,e.ActualHeight)).IntersectsWith(viewport));
    }
    private static async Task SettleLayout()
    {if(trace==null){Layout();await window.Dispatcher.InvokeAsync(Layout,DispatcherPriority.Background);Layout();}else await trace.SettleAsync(Layout);}
    private static async Task ScrollLast(Guid id)
    {
        var list=Named<ListBox>("ObjectList");var item=Row(id);
        if(Node(item)?.Id==id)list.ScrollIntoView(item);
        Scroller().ScrollToEnd();await SettleLayout();
        if(!MemberVisible(id))
        {
            var matches=Visuals(list).OfType<FrameworkElement>().Where(e=>Node(e.DataContext)?.Id==id).Select(e=>new{type=e.GetType().Name,e.ActualWidth,e.ActualHeight,bounds=e.TransformToAncestor(list).TransformBounds(new Rect(e.RenderSize)).ToString()}).ToArray();
            throw new InvalidOperationException("The last member is not visibly realized after scrolling: "+JsonSerializer.Serialize(new{list.ActualHeight,list.ActualWidth,Scroller().VerticalOffset,Scroller().ExtentHeight,Scroller().ViewportHeight,id,matches}));
        }
    }
    private static void CheckExpanded(Guid groupId)
    {
        var list=Named<ListBox>("ObjectList");var expanded=typeof(MainWindow).GetField("objectTreeExpansion",Private)?.GetValue(window)as IDictionary<Guid,bool>;
        var toggle=Visuals(list).OfType<ToggleButton>().FirstOrDefault(t=>Node(t.DataContext)?.Id==groupId&&t.Name=="TreeDisclosure");
        var storedExpanded=expanded!=null&&(!expanded.TryGetValue(groupId,out var stored)||stored);
        Check(toggle?.IsChecked==true || (toggle==null&&storedExpanded),"Benchmark requires the group expanded; collapse is not a performance workaround.");
    }
    private static void Rows(string stage)
    {
        var list=Named<ListBox>("ObjectList");var visuals=Visuals(list).ToArray();
        var controls=visuals.OfType<ItemsControl>().Select(control=>new{type=control.GetType().Name,control.Name,items=control.Items.Count,
            realized=Enumerable.Range(0,control.Items.Count).Count(i=>control.ItemContainerGenerator.ContainerFromIndex(i)!=null)}).ToArray();
        var scene=editor.Snapshot.Scene;int Count(SceneTreeNode node)=>1+node.Children.Sum(Count);
        stages.Add(new{stage,logicalSceneNodes=scene.GetObjectTree().Sum(Count),topLevelListItems=list.Items.Count,
            realizedRowContainers=controls.Sum(c=>c.realized),visualNodes=visuals.Length,itemControls=controls,
            actualListWidth=list.ActualWidth,actualListHeight=list.ActualHeight,
            scope=editor.Snapshot.EditingGroupId,selectedLeaves=editor.Snapshot.SelectedObjectIds.Length,
            scrollOffset=Scroller().VerticalOffset,scrollExtent=Scroller().ExtentHeight,scrollViewport=Scroller().ViewportHeight});
    }
    private static async Task Idle(){var watch=Stopwatch.StartNew();while(editor.Snapshot.IsBusy||typeof(MainWindow).GetField("operation",Private)!.GetValue(window)!=null){if(watch.Elapsed.TotalSeconds>30)throw new TimeoutException("Member preview did not finish.");await Task.Delay(1);}}
    private static async Task Enter(Guid group,Guid member)=>await trace!.MeasureAsync("Enter",async()=>{
        expectedDoneOffset=Scroller().VerticalOffset;
        trace.Phase("handler-enter",()=>Invoke("EnterMemberScope",group,member));await SettleLayout();
        trace.Phase("assertions",()=>{Check(editor.Snapshot.EditingGroupId==group&&editor.Snapshot.SelectedObjectIds.SequenceEqual(new[]{member}),"Enter did not select the requested member.");behavior.Add(new{transition="Enter",selectedMemberVisible=MemberVisible(member),passed=MemberVisible(member),scrollOffset=Scroller().VerticalOffset});});});
    private static async Task Done(SceneGroup group,Guid primary)=>await trace!.MeasureAsync("Done",async()=>{
        trace.Phase("handler-done",()=>Invoke("ExitGroupClick",window,new RoutedEventArgs()));await SettleLayout();
        trace.Phase("assertions",()=>{var s=editor.Snapshot;Check(s.EditingGroupId==null&&s.SelectedObjectIds.ToHashSet().SetEquals(group.ObjectIds)&&s.SelectedObjectId==primary,"Done did not restore exactly the whole group and primary member.");CheckExpanded(group.Id);var restored=Math.Abs(Scroller().VerticalOffset-expectedDoneOffset)<=1;var visible=MemberVisible(primary);behavior.Add(new{transition="Done",scrollRestored=restored,selectedMemberVisible=visible,passed=restored&&visible,expectedOffset=expectedDoneOffset,scrollOffset=Scroller().VerticalOffset});});});

    private static async Task Run()
    {
        Record("cold.window-construct",[Time(()=>window=new MainWindow())]);editor=(EditorSession)typeof(MainWindow).GetField("editor",Private)!.GetValue(window)!;surface=(FrameworkElement)window.Content;
        // A hidden Window can later arrange attached content to zero. The harness owns this root's
        // explicit layout size; detach it so dispatcher work cannot silently erase the measured viewport.
        window.Content=null;
        // Match the application workflow: the empty window initializes tabs/templates before Open.
        Record("cold.empty-layout-setup",[await TimeAsync(async()=>{Refresh();await SettleLayout();})]);
        trace=new TracePhases(surface,window.Dispatcher,Scroller(),()=>typeof(MainWindow).GetField("pendingTreeRestore",Private)!.GetValue(window)!=null);
        Record("cold.core-open",[await TimeAsync(async()=>Ok(await editor.OpenAsync(input)))]);
        var original=editor.Snapshot;var group=original.Scene.Groups.Single();var last=group.ObjectIds[^1];
        Check(original.Scene.Objects.Length==1000&&original.Scene.TriangleCount==100000&&group.ObjectIds.Length==1000,"Expected combined-limit authoritative fixture.");
        Record("cold.expanded-initial-refresh-layout",[await TimeAsync(()=>trace.MeasureAsync("Initial",async()=>{Refresh();await SettleLayout();}))]);CheckExpanded(group.Id);Rows("initial.expanded-rows");
        Check(Named<ListBox>("ObjectList").ActualWidth>0&&Named<ListBox>("ObjectList").ActualHeight>0,"Actual measured list bounds must stay nonzero.");
        for(var i=0;i<3;i++){Refresh();await SettleLayout();}await Task.Delay(200);
        var refreshes=new List<Sample>();for(var i=0;i<12;i++)refreshes.Add(await TimeAsync(async()=>{Refresh();await SettleLayout();}));Record("warm.expanded-refresh-layout",refreshes);
        Record("cold.expanded-scroll-last-layout",[await TimeAsync(()=>ScrollLast(last))]);Rows("expanded.last-member-rows");
        var scrolls=new List<Sample>();
        for(var i=0;i<12;i++){Scroller().ScrollToTop();await SettleLayout();scrolls.Add(await TimeAsync(()=>ScrollLast(last)));}
        Record("warm.expanded-scroll-last-layout",scrolls);
        Ok(editor.SelectMany([]));Refresh();await ScrollLast(last);
        Record("expanded.last-member-selection-layout",[await TimeAsync(async()=>{Named<ListBox>("ObjectList").SelectedItem=Row(last);await SettleLayout();})]);
        Check(editor.Snapshot.EditingGroupId==null&&editor.Snapshot.SelectedObjectIds.ToHashSet().SetEquals(group.ObjectIds),"A scene-scope member row failed to select the whole group.");
        Check(MemberVisible(last),"Whole-group selection scrolled the last member out of view.");
        assertions.Add("Expanded group remains expanded; final member is visibly realized, reachable by scroll and selects its entire group.");
        Record("cold.member-enter-layout",[await TimeAsync(()=>Enter(group.Id,last))]);Rows("member.initial-rows");
        var previews=new List<Sample>();var applies=new List<Sample>();var undoes=new List<Sample>();
        for(var i=0;i<10;i++)
        {
            var before=editor.Snapshot;
            Invoke("MoveHandleClick",window,new RoutedEventArgs());Named<CheckBox>("SnapTransforms").IsChecked=false;
            previews.Add(await TimeAsync(async()=>{
                var field=Named<TextBox>("PositionX");field.Text=(double.Parse(field.Text,CultureInfo.CurrentCulture)+1).ToString(CultureInfo.CurrentCulture);
                Invoke("PreviewTransformClick",window,new RoutedEventArgs());await Idle();await SettleLayout();
                Check(editor.Snapshot.Preview!=null,"Member preview failed.");Check(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Preview changed accepted scene.");
            }));
            applies.Add(await TimeAsync(async()=>{Invoke("ApplyTransformClick",window,new RoutedEventArgs());await SettleLayout();}));
            var applied=editor.Snapshot;Check(applied.IsDirty&&applied.EditingGroupId==group.Id&&applied.SelectedObjectIds.SequenceEqual(new[]{last}),"Apply lost member scope/selection or did not become dirty.");
            Check(applied.Scene.Groups.Single()==group,"Member Apply changed group metadata.");
            var appliedObjects=applied.Scene.Objects.ToDictionary(o=>o.Id);
            foreach(var obj in before.Scene.Objects.Where(o=>o.Id!=last))Check(ReferenceEquals(appliedObjects[obj.Id],obj),"Member Apply changed a sibling reference.");
            undoes.Add(await TimeAsync(async()=>{Invoke("UndoClick",window,new RoutedEventArgs());await SettleLayout();}));
            var undone=editor.Snapshot;Check(ReferenceEquals(undone.Scene,before.Scene)&&undone.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&undone.EditingGroupId==before.EditingGroupId&&undone.IsDirty==before.IsDirty&&undone.FilePath==before.FilePath,"Undo failed to restore exact accepted content/scope/selection/path/dirty state.");
        }
        Record("member.preview-layout-sequence",previews);Record("member.apply-layout-sequence",applies);Record("member.undo-layout-sequence",undoes);Rows("member.after-undo-rows");
        assertions.Add("Ten member Preview/Apply/Undo sequences preserve accepted preview state, exact Undo content, native path, clean state, group identity, scope, selection and all999 sibling references.");
        Record("cold.done-layout",[await TimeAsync(()=>Done(group,last))]);Rows("done.expanded-rows");
        var enters=new List<Sample>();var exits=new List<Sample>();
        for(var i=0;i<120;i++){enters.Add(await TimeAsync(()=>Enter(group.Id,last)));exits.Add(await TimeAsync(()=>Done(group,last)));}
        Record("warm.member-enter-layout",enters);Record("warm.done-layout",exits);Rows("done.final-expanded-rows");
        Check(ReferenceEquals(editor.Snapshot.Scene,original.Scene)&&!editor.Snapshot.IsDirty&&editor.Snapshot.FilePath==original.FilePath,"Repeated Enter/Done changed accepted document/path/dirty state.");
        await ScrollLast(last);Rows("done.last-member-rows");
        assertions.Add("121 Done transitions preserve whole-group selection and primary, expanded disclosure and exact original accepted document; final member remains reachable.");
    }
}
