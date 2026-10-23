using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// Caller-only diagnostics. No product hooks and no Process.Refresh, forced GC or working-set queries.
internal sealed class TracePhases
{
    private readonly FrameworkElement surface;
    private readonly Dispatcher dispatcher;
    private readonly ScrollViewer scroll;
    private readonly Func<bool> pendingRestore;
    private int layoutCount,scrollCount,sequence;
    private readonly int[] posted=new int[12],started=new int[12];
    private string? stage;
    private readonly List<object> phases=[];
    public readonly List<object> Samples=[];
    public int ProcessId=>Environment.ProcessId;
    public uint UiNativeThreadId {get;}=GetCurrentThreadId();
    public long Frequency=>Stopwatch.Frequency;
    public TracePhases(FrameworkElement surface,Dispatcher dispatcher,ScrollViewer scroll,Func<bool> pendingRestore)
    {
        this.surface=surface;this.dispatcher=dispatcher;this.scroll=scroll;this.pendingRestore=pendingRestore;
        surface.LayoutUpdated+=(_,_)=>layoutCount++;
        scroll.ScrollChanged+=(_,_)=>scrollCount++;
        dispatcher.Hooks.OperationPosted+=(_,e)=>{if(stage!=null){var p=(int)e.Operation.Priority;if(p>=0&&p<posted.Length)posted[p]++;}};
        dispatcher.Hooks.OperationStarted+=(_,e)=>{if(stage!=null){var p=(int)e.Operation.Priority;if(p>=0&&p<started.Length)started[p]++;}};
        _=PhaseEvents.Log;
    }
    private readonly record struct Stamp(long Ticks,long Allocated,long ThreadAllocated,int G0,int G1,int G2,long PauseTicks,long CpuTicks,int Layouts,int Scrolls,bool Pending,double Offset,int[] Posted,int[] Started);
    private Stamp Capture()
    {
        GetThreadTimes(GetCurrentThread(),out _,out _,out var kernel,out var user);
        return new(Stopwatch.GetTimestamp(),GC.GetTotalAllocatedBytes(false),GC.GetAllocatedBytesForCurrentThread(),GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2),GC.GetTotalPauseDuration().Ticks,kernel+user,layoutCount,scrollCount,pendingRestore(),scroll.VerticalOffset,(int[])posted.Clone(),(int[])started.Clone());
    }
    private object Delta(Stamp a,Stamp b)=>new{startTicks=a.Ticks,endTicks=b.Ticks,wallMilliseconds=(b.Ticks-a.Ticks)*1000d/Stopwatch.Frequency,
        processAllocatedBytes=b.Allocated-a.Allocated,uiAllocatedBytes=b.ThreadAllocated-a.ThreadAllocated,
        gen0=b.G0-a.G0,gen1=b.G1-a.G1,gen2=b.G2-a.G2,gcPauseMilliseconds=(b.PauseTicks-a.PauseTicks)/10000d,
        uiCpuMilliseconds=(b.CpuTicks-a.CpuTicks)/10000d,layoutNotifications=b.Layouts-a.Layouts,scrollNotifications=b.Scrolls-a.Scrolls,
        pendingBefore=a.Pending,pendingAfter=b.Pending,offsetBefore=a.Offset,offsetAfter=b.Offset,
        dispatcherPosted=b.Posted.Zip(a.Posted,(x,y)=>x-y).ToArray(),dispatcherStarted=b.Started.Zip(a.Started,(x,y)=>x-y).ToArray()};
    public async Task MeasureAsync(string name,Func<Task> action)
    {
        if(stage!=null)throw new InvalidOperationException("Nested traced sample.");
        stage=name;sequence++;phases.Clear();PhaseEvents.Log.Boundary(0,sequence,name,"sample",Stopwatch.GetTimestamp());
        var before=Capture();
        try{await action();}
        finally{var after=Capture();PhaseEvents.Log.Boundary(1,sequence,name,"sample",after.Ticks);Samples.Add(new{sequence,stage=name,metrics=Delta(before,after),phases=phases.ToArray()});stage=null;}
    }
    public void Phase(string name,Action action)
    {
        if(stage==null){action();return;}
        PhaseEvents.Log.Boundary(0,sequence,stage,name,Stopwatch.GetTimestamp());var before=Capture();
        try{action();}
        finally{var after=Capture();PhaseEvents.Log.Boundary(1,sequence,stage,name,after.Ticks);phases.Add(new{phase=name,metrics=Delta(before,after)});}
    }
    public async Task SettleAsync(Action layout)
    {
        if(stage==null){layout();await dispatcher.InvokeAsync(layout,DispatcherPriority.Background);layout();return;}
        Phase("synchronous-layout",layout);
        var name=stage;var seq=sequence;
        PhaseEvents.Log.Boundary(0,seq,name,"background-queue",Stopwatch.GetTimestamp());var queued=Capture();
        await dispatcher.InvokeAsync(()=>{
            var entered=Capture();PhaseEvents.Log.Boundary(1,seq,name,"background-queue",entered.Ticks);phases.Add(new{phase="background-queue",metrics=Delta(queued,entered)});
            Phase("background-layout",layout);
        },DispatcherPriority.Background);
        Phase("final-layout",layout);
    }
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetThreadTimes(nint thread,out long creation,out long exit,out long kernel,out long user);
}

[EventSource(Name="Builder-Latency-Phases")]
internal sealed class PhaseEvents:EventSource
{
    public static readonly PhaseEvents Log=new();
    [Event(1,Level=EventLevel.Informational)]public void Boundary(int kind,int sequence,string stage,string phase,long ticks)
    {if(IsEnabled())WriteEvent(1,kind,sequence,stage,phase,ticks);}
}
