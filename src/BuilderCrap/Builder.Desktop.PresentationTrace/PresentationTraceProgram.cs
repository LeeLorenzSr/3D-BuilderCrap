using Builder.Core;
using Builder.Desktop;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

// Separate shown diagnostic host. The referenced product and Core binaries are never patched.
internal static class PresentationTraceProgram
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private const string ExpectedDesktop="6CFF91ECC217B371DEAFAACFE397E0110FAF144C791E9DBE0E2336D92F74E157";
    private const string ExpectedCore="941FBC0759107880BAD2E0CD95BF21A871315917A86C590BBB3DFF0B84461EF5";
    private const int Capacity=8192;
    private static readonly object gate=new();
    private static readonly List<object> inputEvents=[],stateEvents=[],trialResults=[];
    private static readonly List<FrameRecord> frames=[];
    private static readonly Dictionary<string,PixelFrame> references=[];
    private static readonly Dictionary<string,PixelFrame> retained=[];
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,IncludeFields=true};
    private static MainWindow window=null!;
    private static EditorSession editor=null!;
    private static Config config=null!;
    private static string output="";
    private static IntPtr hwnd;
    private static uint uiNativeThreadId;
    private static SceneGroup group=null!;
    private static Guid member;
    private static Grid marker=null!;
    private static Native.Callback nativeCallback=OnFrame;
    private static PixelFrame? latest;
    private static Trial? activeTrial;
    private static bool armed,closing,overflow;
    private static int sequence,markerSequence,referenceMarkerSequence=1000;
    private static bool doneMouseDown;
    private static Point projectedMember;
    private static int anchorX,anchorY;
    private static (int Message,long WParam,long LParam,int Time)? lastFilteredMessage;
    private static long lastRevision=-1,lastSourceTime;
    private static string lastScope="";
    private static RoiSpec[] specs=[];
    private static readonly Stopwatch lifetime=Stopwatch.StartNew();

    private sealed record Config(string Input,string Output,int Width=1280,int Height=780,bool Calibrate=true,int TimeoutSeconds=900);
    private sealed record RoiSpec(string Name,Native.Roi Bounds);
    private sealed record PixelRoi(string Name,int Width,int Height,byte[] Pixels);
    private sealed record PixelFrame(long Ordinal,long SourceTime100ns,long CallbackQpc,int Width,int Height,PixelRoi[] Rois);
    private sealed record RoiScore(string Name,int InformativePixels,double TargetAgreement,double OldAgreement,bool Passed);
    private sealed record FrameRecord(long Ordinal,long SourceTime100ns,long CallbackQpc,long ObserverEndQpc,int Width,int Height,long Gap100ns,bool Monotonic,int MarkerSequence,bool MarkerPixelsMatch,int TrialSequence,string[] Hashes,RoiScore[] Scores,bool Match);
    private sealed class Trial(int id,string target,long receiptQpc,long receiptEndQpc,int message,int messageTime,ulong tickBefore,ulong tickAfter)
    {
        public int Sequence=id;public string Target=target;public long ReceiptQpc=receiptQpc,ReceiptEndQpc=receiptEndQpc;
        public int Message=message,MessageTime=messageTime;public ulong TickBefore=tickBefore,TickAfter=tickAfter;
        public long? FirstMatchOrdinal,FirstMatchTime100ns,LastNonmatchOrdinal;
        public long? PreInputOrdinal,PreInputSourceTime100ns,PreInputCallbackQpc;
        public bool StateConfirmed;public long? StateQpc;
        public object? ReceiptState,AcceptedState;public string ReceiptStage="";
    }

    [STAThread] private static int Main(string[] args)
    {
        var configPath=args.Length==2&&args[0]=="--config"?args[1]:System.IO.Path.Combine(AppContext.BaseDirectory,"presentation-config.json");
        try
        {
            config=JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidOperationException("Empty config.");
            output=System.IO.Path.GetFullPath(config.Output);Directory.CreateDirectory(output);
            if(Hash(typeof(MainWindow).Assembly.Location)!=ExpectedDesktop||Hash(typeof(EditorSession).Assembly.Location)!=ExpectedCore)
                throw new InvalidOperationException("Frozen binary mismatch; expected Desktop6CFF/Core941F.");
            var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/Builder;component/Theme.xaml",UriKind.Relative)});
            app.Startup+=async(_,_)=>
            {
                try
                {
                    window=new MainWindow{Width=config.Width,Height=config.Height};app.MainWindow=window;
                    editor=(EditorSession)typeof(MainWindow).GetField("editor",Private)!.GetValue(window)!;
                    window.SourceInitialized+=(_,_)=>
                    {
                        hwnd=new WindowInteropHelper(window).Handle;uiNativeThreadId=Native.GetWindowThreadProcessId(hwnd,out var ownerPid);
                        if(ownerPid!=Environment.ProcessId)throw new InvalidOperationException("Diagnostic HWND is not owned by this process.");
                        HwndSource.FromHwnd(hwnd)!.AddHook(Hook);ComponentDispatcher.ThreadFilterMessage+=ThreadMessage;
                    };
                    window.Closed+=(_,_)=>{closing=true;ComponentDispatcher.ThreadFilterMessage-=ThreadMessage;Native.ObserverStop();Flush();};
                    window.Show();await Setup();
                }
                catch(Exception e){File.WriteAllText(System.IO.Path.Combine(output,"host-error.txt"),e.ToString());app.Shutdown(1);}
            };
            return app.Run();
        }
        catch(Exception e){File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"presentation-startup-error.txt"),e.ToString());return 1;}
    }

    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static object? Invoke(string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,Private)!.Invoke(window,args);
    private static T Named<T>(string name)where T:class=>(T)window.FindName(name);
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Visuals(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static SceneTreeNode? Node(object? row)=>row?.GetType().GetProperty("Node")?.GetValue(row)as SceneTreeNode;
    private static object MemberRow()=>Named<ListBox>("ObjectList").Items.Cast<object>().Single(r=>Node(r)?.Id==member);
    private static ScrollViewer Scroller()=>Visuals(Named<ListBox>("ObjectList")).OfType<ScrollViewer>().First();
    private static async Task Settle(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.Background);await Task.Delay(250);}
    private static void FocusLast()
    {var row=MemberRow();Invoke("SelectObjectTreeRow",row,ModifierKeys.None);Named<ListBox>("ObjectList").ScrollIntoView(row);Scroller().ScrollToEnd();Invoke("FocusObjectTreeRow",row,true);}
    private static async Task Setup()
    {
        var opened=await editor.OpenAsync(System.IO.Path.GetFullPath(config.Input));if(!opened.Succeeded)throw new InvalidOperationException(opened.ToString());
        var snapshot=editor.Snapshot;group=snapshot.Scene.Groups.Single();member=group.ObjectIds[^1];
        if(snapshot.Scene.Objects.Length!=1000||snapshot.Scene.TriangleCount!=100000||group.ObjectIds.Length!=1000)throw new InvalidOperationException("Expected exact1000objects/100kfaces grouped fixture.");
        editor.SelectMany(group.ObjectIds,member,editor.Snapshot.Revision);Invoke("Refresh",true);Invoke("FitScene");await Settle();FocusLast();await Settle();
        // Marker identifies an already-armed trial, not the response. It is excluded from semantic ROIs.
        marker=new Grid{Width=16,Height=16,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(3,3,0,0),IsHitTestVisible=false};
        for(var i=0;i<4;i++){marker.RowDefinitions.Add(new RowDefinition());marker.ColumnDefinitions.Add(new ColumnDefinition());}
        for(var i=0;i<16;i++){var cell=new Rectangle();Grid.SetRow(cell,i/4);Grid.SetColumn(cell,i%4);marker.Children.Add(cell);}
        SetMarker(0);
        ((Grid)window.Content).Children.Add(marker);Panel.SetZIndex(marker,999);await Settle();
        var view=Named<Viewport3D>("Viewport");var selected=editor.Snapshot.Scene.Objects.Single(o=>o.Id==member);
        projectedMember=TransformGizmo.Layout((ProjectionCamera)view.Camera,new Size(view.ActualWidth,view.ActualHeight),selected.Bounds.Center,HandleMode.Move).Project(selected.Bounds.Center)
            ?? throw new InvalidOperationException("Selected last member projects outside viewport.");
        var picked=await (Task<Guid?>)Invoke("PickViewportObject",projectedMember)!;
        if(picked!=member)throw new InvalidOperationException("Projected last member is occluded; cannot establish visible member ROI.");
        lock(gate)stateEvents.Add(new{stage="selected-model-roi-proof",qpc=Stopwatch.GetTimestamp(),member,projectedMember,picked});
        specs=ComputeRois();
        Native.DwmGetWindowAttribute(hwnd,9,out var captureBounds,Marshal.SizeOf<Native.Rect>());
        var anchorScreen=view.PointToScreen(projectedMember);var viewportBounds=specs.Single(r=>r.Name=="viewport").Bounds;
        anchorX=(int)Math.Round(anchorScreen.X-captureBounds.Left)-viewportBounds.X;anchorY=(int)Math.Round(anchorScreen.Y-captureBounds.Top)-viewportBounds.Y;
        var status=Native.ObserverStart(hwnd,specs.Select(s=>s.Bounds).ToArray(),specs.Length,nativeCallback);
        if(status<0)throw new COMException("Own-HWND observer failed.",status);
        await WaitForFrames(1);
        if(config.Calibrate)await Calibrate();
        InputManager.Current.PreProcessInput+=(_,e)=>InputStage("pre",e.StagingItem.Input);
        InputManager.Current.PostProcessInput+=(_,e)=>{InputStage("post",e.StagingItem.Input);Snapshot("post-input");};
        window.Dispatcher.Hooks.OperationCompleted+=(_,_)=>{if(armed)Snapshot("dispatcher-complete");};
        Snapshot("ready");Flush();
        // A bounded automatic close only flushes; it never generates input or product actions.
        var timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(config.TimeoutSeconds)};
        timeout.Tick+=(_,_)=>{timeout.Stop();typeof(MainWindow).GetField("closeApproved",Private)!.SetValue(window,true);window.Close();};timeout.Start();
    }
    private static RoiSpec[] ComputeRois()
    {
        Native.DwmGetWindowAttribute(hwnd,9,out var bounds,Marshal.SizeOf<Native.Rect>());
        Native.Roi ElementRect(FrameworkElement element,int inset=0)
        {
            var point=element.PointToScreen(new Point(inset,inset));var scale=VisualTreeHelper.GetDpi(element);
            return new((int)Math.Round(point.X-bounds.Left),(int)Math.Round(point.Y-bounds.Top),
                Math.Max(1,(int)Math.Round((element.ActualWidth-2*inset)*scale.DpiScaleX)),Math.Max(1,(int)Math.Round((element.ActualHeight-2*inset)*scale.DpiScaleY)));
        }
        var inspector=Named<Grid>("Inspector");var anchor=ElementRect(inspector);
        var viewport=ElementRect(Named<FrameworkElement>("ViewportSurface"));var dpi=VisualTreeHelper.GetDpi(inspector);
        // Fixed physical rectangles cover BOTH scope layouts. Header includes real scope/title text;
        // tree captures the scrolled member rows; viewport avoids camera toolbar and bottom hints.
        return [new("header",new(anchor.X+8,anchor.Y+5,anchor.Width-16,(int)(100*dpi.DpiScaleY))),
            new("tree",new(anchor.X+18,anchor.Y+(int)(100*dpi.DpiScaleY),anchor.Width-36,(int)(185*dpi.DpiScaleY))),
            new("viewport",new(viewport.X+Math.Clamp((int)(projectedMember.X*dpi.DpiScaleX)-128,0,Math.Max(0,viewport.Width-256)),viewport.Y+Math.Clamp((int)(projectedMember.Y*dpi.DpiScaleY)-128,0,Math.Max(0,viewport.Height-256)),Math.Min(256,viewport.Width),Math.Min(256,viewport.Height))),
            new("marker",ElementRect(marker))];
    }
    private static async Task WaitForFrames(int count)
    {
        long start;lock(gate)start=latest?.Ordinal??0;
        var watch=Stopwatch.StartNew();while(true){lock(gate)if((latest?.Ordinal??0)>=start+count)return;if(watch.Elapsed.TotalSeconds>10)throw new TimeoutException("Capture produced no frames: "+ObserverError());await Task.Delay(30);}
    }
    private static async Task<PixelFrame> StableFrame()
    {
        await Settle();
        PixelFrame? previous=null;
        for(var i=0;i<3;i++)
        {
            // Only the excluded diagnostic marker requests a fresh capture of otherwise static
            // reference content. No measured trial uses these programmatic changes as its endpoint.
            var requestQpc=Stopwatch.GetTimestamp();SetMarker(++referenceMarkerSequence);
            var watch=Stopwatch.StartNew();PixelFrame current;
            while(true)
            {
                lock(gate)if(latest is {} frame&&(decimal)frame.SourceTime100ns*Stopwatch.Frequency>=(decimal)requestQpc*10_000_000){current=frame;break;}
                if(watch.Elapsed.TotalSeconds>10)throw new TimeoutException("Reference frame not received.");await Task.Delay(25);
            }
            if(previous!=null)
                foreach(var name in new[]{"header","tree","viewport"})
                    if(!previous.Rois.Single(r=>r.Name==name).Pixels.SequenceEqual(current.Rois.Single(r=>r.Name==name).Pixels))
                        throw new InvalidOperationException($"Unstable reference ROI {name}; pointer/focus/animation must settle before calibration.");
            previous=current;
        }
        return previous!;
    }
    private static async Task Calibrate()
    {
        var groupFrame=await StableFrame();lock(gate)references["group"]=groupFrame;
        // Unmeasured setup invokes the real row navigation branch; measured trials use only Sky.
        Invoke("NavigateObjectTree",Key.Enter,ModifierKeys.None);await Settle();
        if(editor.Snapshot.EditingGroupId!=group.Id||editor.Snapshot.SelectedObjectId!=member)throw new InvalidOperationException("Calibration entry missed last member.");
        var memberFrame=await StableFrame();lock(gate)references["member"]=memberFrame;
        Invoke("ExitGroupClick",window,new RoutedEventArgs());await Settle();FocusLast();await Settle();
        var old=references["group"];var target=references["member"];
        var contrast=Score(old,target,target);lock(gate)stateEvents.Add(new{stage="reference-contrast",qpc=Stopwatch.GetTimestamp(),scores=contrast});
        // Insufficient independent ROI contrast is a reported unsupported endpoint, never a weakened matcher.
        foreach(var pair in references)retained["reference-"+pair.Key]=pair.Value;
    }
    private static void SetMarker(int value)
    {for(var bit=0;bit<16;bit++)((Rectangle)marker.Children[bit]).Fill=(value&(1<<bit))==0?Brushes.Magenta:Brushes.Lime;}
    private static bool HitDone(IntPtr clientPoint)
    {
        var packed=clientPoint.ToInt64();var point=new Native.Point{X=(short)(packed&0xffff),Y=(short)((packed>>16)&0xffff)};Native.ClientToScreen(hwnd,ref point);
        var button=Named<Button>("DoneEditingButton");var local=button.PointFromScreen(new Point(point.X,point.Y));
        return button.IsVisible&&button.IsEnabled&&local.X>=0&&local.Y>=0&&local.X<button.ActualWidth&&local.Y<button.ActualHeight;
    }
    private static IntPtr Hook(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(closing)return IntPtr.Zero;
        if(message==0x100&&wParam.ToInt32()is 0x74 or 0x75)
        {
            handled=true;
            if(wParam.ToInt32()==0x74)Arm();else Flush();
            return IntPtr.Zero;
        }
        ObserveMessage("hwnd",handle,message,wParam,lParam,null);
        return IntPtr.Zero;
    }
    private static void ThreadMessage(ref MSG message,ref bool handled)
    {
        // Observe only our HWND, before WPF may consume keyboard messages. Never handle/inject.
        if(!closing&&message.hwnd==hwnd)ObserveMessage("thread-filter",message.hwnd,message.message,message.wParam,message.lParam,message.time);
    }
    private static void ObserveMessage(string stage,IntPtr handle,int message,IntPtr wParam,IntPtr lParam,int? rawMessageTime)
    {
        if(message is 0x100 or 0x101 or 0x104 or 0x105 or 0x201 or 0x202 or 0x203 or 0x204 or 0x205 or 0x207 or 0x208 or 0x20A)
        {
            var before=Stopwatch.GetTimestamp();var tickBefore=Native.GetTickCount64();var messageTime=rawMessageTime??Native.GetMessageTime();var tickAfter=Native.GetTickCount64();var after=Stopwatch.GetTimestamp();
            var doneHit=message is 0x201 or 0x202&&HitDone(lParam);
            lock(gate)
            {
                var fingerprint=(message,wParam.ToInt64(),lParam.ToInt64(),messageTime);
                var duplicate=stage=="hwnd"&&lastFilteredMessage==fingerprint;
                if(stage=="thread-filter")lastFilteredMessage=fingerprint;
                Add(inputEvents,new{stage,qpcBefore=before,qpcAfter=after,message,wParam=wParam.ToInt64(),lParam=lParam.ToInt64(),messageTime,tickBefore,tickAfter,hwnd=handle.ToInt64(),armed,markerSequence,duplicate});
                if(duplicate)return;
                // Enter responds on key down; Done button responds on mouse up. Other inputs retain
                // timing evidence but cannot accidentally start a claimed Enter/Done response.
                if(message==0x201)doneMouseDown=doneHit;
                var enterKey=message==0x100&&wParam.ToInt32()==13&&editor.Snapshot.EditingGroupId==null&&
                    Named<ListBox>("ObjectList").IsKeyboardFocusWithin&&Node((Keyboard.FocusedElement as FrameworkElement)?.DataContext)?.Id==member;
                var doneKey=message==0x100&&wParam.ToInt32()==13&&editor.Snapshot.EditingGroupId!=null&&Named<Button>("DoneEditingButton").IsKeyboardFocusWithin;
                if(armed&&activeTrial==null&&(enterKey||doneKey||(message==0x202&&doneHit&&doneMouseDown&&editor.Snapshot.EditingGroupId!=null)))
                {
                    var target=editor.Snapshot.EditingGroupId==null?"member":"group";
                    activeTrial=new(++sequence,target,before,after,message,messageTime,tickBefore,tickAfter){ReceiptState=State(),ReceiptStage=stage};
                    if(latest is {} preInput)
                    {
                        retained[$"trial-{sequence:D3}-pre-input"]=preInput;activeTrial.PreInputOrdinal=preInput.Ordinal;
                        activeTrial.PreInputSourceTime100ns=preInput.SourceTime100ns;activeTrial.PreInputCallbackQpc=preInput.CallbackQpc;
                    }
                }
                if(message==0x202)doneMouseDown=false;
            }
            window.Dispatcher.BeginInvoke(()=>Snapshot("after-message"),DispatcherPriority.Background);
        }
    }
    private static void Arm()
    {
        var s=editor.Snapshot;
        if(s.IsBusy||s.Preview!=null||s.SelectedObjectId!=member||!(s.EditingGroupId==group.Id&&s.SelectedObjectIds.SequenceEqual(new[]{member})||s.EditingGroupId==null&&s.SelectedObjectIds.ToHashSet().SetEquals(group.ObjectIds)))
        {lock(gate)Add(stateEvents,new{stage="arm-rejected-invalid-state",qpc=Stopwatch.GetTimestamp(),snapshot=State()});return;}
        lock(gate)
        {
            if(sequence>=64){overflow=true;Add(stateEvents,new{stage="arm-rejected-trial-cap",qpc=Stopwatch.GetTimestamp()});return;}
            if(latest is {} before&&references.Count==2)
            {
                var target=s.EditingGroupId==null?"member":"group";
                if(Score(references[target=="member"?"group":"member"],references[target],before).All(score=>score.Passed))
                {Add(stateEvents,new{stage="arm-rejected-target-already-visible",qpc=Stopwatch.GetTimestamp()});return;}
            }
            if(activeTrial!=null){trialResults.Add(new{status="incomplete-rearmed",trial=activeTrial});activeTrial=null;}
            armed=true;markerSequence++;SetMarker(markerSequence);
            Add(stateEvents,new{stage="armed",qpc=Stopwatch.GetTimestamp(),markerSequence,snapshot=State()});
        }
    }
    private static void InputStage(string stage,InputEventArgs input)
    {
        if(!armed)return;
        lock(gate)Add(inputEvents,new{stage,qpc=Stopwatch.GetTimestamp(),type=input.GetType().Name,routedEvent=input.RoutedEvent?.Name,input.Timestamp,source=(input.OriginalSource as FrameworkElement)?.Name,handled=input.Handled});
    }
    private static object State()
    {
        var s=editor.Snapshot;var focused=Keyboard.FocusedElement as FrameworkElement;var scroller=Scroller();
        return new{s.Revision,s.Scene.ContentId,s.EditingGroupId,s.SelectedObjectId,selectedObjectIds=s.SelectedObjectIds.ToArray(),previewId=s.Preview?.Id,s.CanUndo,s.CanRedo,s.IsBusy,s.IsDirty,s.FilePath,
            focused=focused?.Name,focusedType=focused?.GetType().Name,focusedRowId=Node(focused?.DataContext)?.Id,
            treeSelectedRows=Named<ListBox>("ObjectList").SelectedItems.Cast<object>().Select(row=>Node(row)?.Id).ToArray(),
            treeScroll=new{scroller.VerticalOffset,scroller.ExtentHeight,scroller.ViewportHeight}};
    }
    private static void Snapshot(string stage)
    {
        if(closing)return;var s=editor.Snapshot;var scope=s.EditingGroupId?.ToString()??"scene";
        lock(gate)
        {
            if(s.Revision!=lastRevision||scope!=lastScope){Add(stateEvents,new{stage,qpc=Stopwatch.GetTimestamp(),snapshot=State()});lastRevision=s.Revision;lastScope=scope;}
            if(activeTrial is {} t&&!t.StateConfirmed&&s.SelectedObjectId==member&&((t.Target=="member"&&s.EditingGroupId==group.Id&&s.SelectedObjectIds.SequenceEqual(new[]{member}))||(t.Target=="group"&&s.EditingGroupId==null&&s.SelectedObjectIds.ToHashSet().SetEquals(group.ObjectIds))))
            {t.StateConfirmed=true;t.StateQpc=Stopwatch.GetTimestamp();t.AcceptedState=State();}
        }
    }
    private static void Add(List<object> list,object item){if(list.Count<Capacity)list.Add(item);else overflow=true;}
    private static RoiScore[] Score(PixelFrame old,PixelFrame target,PixelFrame candidate)
    {
        var result=new List<RoiScore>();
        if(old.Width!=target.Width||old.Height!=target.Height||candidate.Width!=target.Width||candidate.Height!=target.Height)
            return new[]{"header","tree","viewport"}.Select(name=>new RoiScore(name,0,0,0,false)).ToArray();
        foreach(var name in new[]{"header","tree","viewport"})
        {
            var a=old.Rois.Single(r=>r.Name==name);var b=target.Rois.Single(r=>r.Name==name);var c=candidate.Rois.Single(r=>r.Name==name);
            if(a.Width!=b.Width||a.Height!=b.Height||c.Width!=b.Width||c.Height!=b.Height){result.Add(new(name,0,0,0,false));continue;}
            int informative=0,targetAgreement=0,oldAgreement=0;
            for(var i=0;i<a.Pixels.Length;i+=4)
            {
                if(Math.Max(Math.Abs(a.Pixels[i]-b.Pixels[i]),Math.Max(Math.Abs(a.Pixels[i+1]-b.Pixels[i+1]),Math.Abs(a.Pixels[i+2]-b.Pixels[i+2])))<24)continue;
                informative++;
                bool Near(byte[] left,byte[] right)=>Math.Abs(left[i]-right[i])<=12&&Math.Abs(left[i+1]-right[i+1])<=12&&Math.Abs(left[i+2]-right[i+2])<=12;
                if(Near(c.Pixels,b.Pixels))targetAgreement++;if(Near(c.Pixels,a.Pixels))oldAgreement++;
            }
            var targetFraction=informative==0?0:(double)targetAgreement/informative;var oldFraction=informative==0?0:(double)oldAgreement/informative;
            result.Add(new(name,informative,targetFraction,oldFraction,informative>=64&&targetFraction>=.95&&oldFraction<=.20));
        }
        // The last selected model is blue in BOTH references. It is deliberately outside the
        // old/target difference mask, so require its pick-proven surface patch independently.
        var reference=target.Rois.Single(r=>r.Name=="viewport");var sample=candidate.Rois.Single(r=>r.Name=="viewport");
        int anchorPixels=0,anchorAgreement=0;
        for(var y=anchorY-4;y<=anchorY+4;y++)for(var x=anchorX-4;x<=anchorX+4;x++)
        {
            if(x<0||y<0||x>=reference.Width||y>=reference.Height)continue;anchorPixels++;var i=(y*reference.Width+x)*4;
            if(Math.Abs(reference.Pixels[i]-sample.Pixels[i])<=12&&Math.Abs(reference.Pixels[i+1]-sample.Pixels[i+1])<=12&&Math.Abs(reference.Pixels[i+2]-sample.Pixels[i+2])<=12)anchorAgreement++;
        }
        var anchorFraction=anchorPixels==0?0:(double)anchorAgreement/anchorPixels;
        result.Add(new("selected-model-anchor",anchorPixels,anchorFraction,0,anchorPixels==81&&anchorFraction>=.95));
        return result.ToArray();
    }
    private static void OnFrame(IntPtr headerPointer,IntPtr roiPointer,IntPtr pixelsPointer)
    {
        try
        {
            var header=Marshal.PtrToStructure<Native.Header>(headerPointer);var rois=new PixelRoi[header.RoiCount];int offset=0;
            for(var i=0;i<header.RoiCount;i++)
            {var r=Marshal.PtrToStructure<Native.Roi>(roiPointer+i*Marshal.SizeOf<Native.Roi>());var bytes=new byte[checked(r.Width*r.Height*4)];Marshal.Copy(pixelsPointer+offset,bytes,0,bytes.Length);offset+=bytes.Length;rois[i]=new(specs[i].Name,r.Width,r.Height,bytes);}
            var frame=new PixelFrame(header.Ordinal,header.SourceTime100ns,header.CallbackQpc,header.Width,header.Height,rois);
            Trial? capturedTrial;PixelFrame? oldReference=null,targetReference=null;int capturedMarker;
            lock(gate)
            {
                capturedTrial=activeTrial;capturedMarker=markerSequence;
                if(capturedTrial!=null&&references.Count==2){oldReference=references[capturedTrial.Target=="member"?"group":"member"];targetReference=references[capturedTrial.Target];}
            }
            // Pixel scans/hashes stay off the UI's small metadata lock. The observer can cost CPU,
            // but it must not make the HWND wait for a full image comparison merely to log input.
            var hashes=frame.Rois.Select(r=>Convert.ToHexString(SHA256.HashData(r.Pixels))).ToArray();
            var scores=oldReference!=null&&targetReference!=null?Score(oldReference,targetReference,frame):Array.Empty<RoiScore>();
            var markerRoi=frame.Rois.Single(r=>r.Name=="marker");var markerPixelsMatch=true;
            for(var bit=0;bit<16;bit++)
            {
                var x=(int)((bit%4+.5)*markerRoi.Width/4);var y=(int)((bit/4+.5)*markerRoi.Height/4);var markerPixel=(y*markerRoi.Width+x)*4;
                markerPixelsMatch&=(capturedMarker&(1<<bit))==0
                    ? markerRoi.Pixels[markerPixel]>=240&&markerRoi.Pixels[markerPixel+1]<=15&&markerRoi.Pixels[markerPixel+2]>=240
                    : markerRoi.Pixels[markerPixel]<=15&&markerRoi.Pixels[markerPixel+1]>=240&&markerRoi.Pixels[markerPixel+2]<=15;
            }
            lock(gate)
            {
                var gap=lastSourceTime==0?0:frame.SourceTime100ns-lastSourceTime;var monotonic=lastSourceTime==0||gap>0;lastSourceTime=frame.SourceTime100ns;latest=frame;
                var match=false;
                if(capturedTrial is {} trial&&ReferenceEquals(activeTrial,trial)&&capturedMarker==markerSequence&&scores.Length==4)
                {
                    var afterInput=(decimal)frame.SourceTime100ns*Stopwatch.Frequency>=(decimal)trial.ReceiptQpc*10_000_000;
                    match=monotonic&&afterInput&&markerPixelsMatch&&trial.StateConfirmed&&scores.All(s=>s.Passed);
                    if(!match){trial.LastNonmatchOrdinal=frame.Ordinal;retained[$"trial-{trial.Sequence:D3}-last-nonmatch"]=frame;}
                    else
                    {
                        trial.FirstMatchOrdinal=frame.Ordinal;trial.FirstMatchTime100ns=frame.SourceTime100ns;retained[$"trial-{trial.Sequence:D3}-first-match"]=frame;
                        var latency=(double)((decimal)frame.SourceTime100ns/10_000_000-(decimal)trial.ReceiptQpc/Stopwatch.Frequency)*1000;
                        trialResults.Add(new{status="matched-captured-composed-response",trial,receiptToCapturedFrameMilliseconds=latency,allRequiredSemanticRois=true,endpoint="WGC compositor-rendered response; not scanout or photons"});activeTrial=null;armed=false;
                    }
                }
                if(frames.Count<Capacity)frames.Add(new(frame.Ordinal,frame.SourceTime100ns,frame.CallbackQpc,Stopwatch.GetTimestamp(),frame.Width,frame.Height,gap,monotonic,capturedMarker,markerPixelsMatch,capturedTrial?.Sequence??0,hashes,scores,match));else overflow=true;
            }
        }
        catch(Exception e){lock(gate)Add(stateEvents,new{stage="observer-managed-error",qpc=Stopwatch.GetTimestamp(),error=e.ToString()});}
    }
    private static string ObserverError(){var buffer=new StringBuilder(2048);Native.ObserverError(buffer,buffer.Capacity);return buffer.ToString();}
    private static void Flush()
    {
        if(string.IsNullOrEmpty(output))return;
        var observerError=ObserverError();
        var finalState=State();var finalStateQpc=Stopwatch.GetTimestamp();
        lock(gate)
        {
            var rawRoot=System.IO.Path.Combine(output,"roi-frames");Directory.CreateDirectory(rawRoot);var saved=new Dictionary<string,object>();
            foreach(var pair in retained)
            {
                var roiFiles=new List<object>();foreach(var roi in pair.Value.Rois)
                {
                    var relative=$"roi-frames/{pair.Key}-{roi.Name}.bgra";File.WriteAllBytes(System.IO.Path.Combine(output,relative),roi.Pixels);
                    roiFiles.Add(new{name=roi.Name,roi.Width,roi.Height,strideBytes=roi.Width*4,pixelFormat="BGRA8",file=relative,sha256=Convert.ToHexString(SHA256.HashData(roi.Pixels))});
                    var image=BitmapSource.Create(roi.Width,roi.Height,96,96,PixelFormats.Bgra32,null,roi.Pixels,roi.Width*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(System.IO.Path.Combine(rawRoot,$"{pair.Key}-{roi.Name}.png"));encoder.Save(stream);
                }
                saved[pair.Key]=new{pair.Value.Ordinal,pair.Value.SourceTime100ns,pair.Value.CallbackQpc,rois=roiFiles};
            }
            File.WriteAllText(System.IO.Path.Combine(output,"presentation-trace.json"),JsonSerializer.Serialize(new{
                schema="builder-own-hwnd-presentation-trace-v1",utc=DateTimeOffset.UtcNow,processId=Environment.ProcessId,uiNativeThreadId,hwnd=hwnd.ToInt64(),qpcFrequency=Stopwatch.Frequency,finalStateQpc,finalState,
                binaries=new{desktop=Hash(typeof(MainWindow).Assembly.Location),core=Hash(typeof(EditorSession).Assembly.Location),host=Hash(typeof(PresentationTraceProgram).Assembly.Location),observer=Hash(System.IO.Path.Combine(AppContext.BaseDirectory,"CaptureObserver.dll"))},
                config,inputSha256=Hash(System.IO.Path.GetFullPath(config.Input)),os=RuntimeInformation.OSDescription,runtime=Environment.Version.ToString(),renderTier=RenderCapability.Tier>>16,
                method="Only own HWND WGC; default WPF rendering; real input exclusively external Sky. Earliest observed owned-HWND thread-filter message receipt is preferred because WPF can consume keyboard messages before HWND hook. Pre-input marker is not semantic endpoint. Header/tree/viewport informative masks AND selected-model9x9surface patch must match. Capture timestamp is compositor render, not callback, monitor scanout or photons.",
                thresholds=new{maximumChannelDifferenceForInformativePixel=24,minimumInformativePixels=64,maximumPerChannelTargetError=12,minimumTargetAgreement=.95,maximumOldAgreement=.20,ignoreAlpha=true},requiredRois=new[]{"header","tree","viewport"},selectedModelAnchor=new{roi="viewport",x=anchorX,y=anchorY,radius=4,minimumTargetAgreement=.95,maximumPerChannelTargetError=12,pickProvenObjectId=member},viewportTargetAnchor=new{x=anchorX-4,y=anchorY-4,width=9,height=9},
                rois=specs,overflow,armed,activeTrial,observerError,elapsedSeconds=lifetime.Elapsed.TotalSeconds,inputEvents,stateEvents,trialResults,frames,retainedFrames=saved,
                limitations=new[]{"No physical input or photons. Sky does not expose injection QPC; input starts at HWND receipt.","WGC can capture occluded windows; parent must establish foreground/unobscured state with Sky.","Missing/coalesced capture frames make first observed matching response an upper bound; callback arrival time is separately logged.","All frame summaries retained within bounded capacity; only references/last-nonmatch/first-match full ROI bytes retained. First-ever matching completeness is not independently proven from omitted earlier ROI pixels.","Hover, focus, scroll, camera or DPI mismatch can reject valid responses. Resize invalidates calibration; do not loosen thresholds after trials.","Capture observer has overhead and may affect presentation cadence; this is a diagnostic host, not unobserved normal-product latency."}},Json));
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal readonly struct Roi(int x,int y,int width,int height){public readonly int X=x,Y=y,Width=width,Height=height;}
        [StructLayout(LayoutKind.Sequential)] internal struct Header{public long Ordinal,SourceTime100ns,CallbackQpc;public int Width,Height,RoiCount,ByteCount;}
        [StructLayout(LayoutKind.Sequential)] internal struct Rect{public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] internal struct Point{public int X,Y;}
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Callback(IntPtr header,IntPtr rois,IntPtr bytes);
        [DllImport("CaptureObserver.dll",CallingConvention=CallingConvention.StdCall)] internal static extern int ObserverStart(IntPtr hwnd,[In]Roi[] rois,int count,Callback callback);
        [DllImport("CaptureObserver.dll",CallingConvention=CallingConvention.StdCall)] internal static extern void ObserverStop();
        [DllImport("CaptureObserver.dll",CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Unicode)] internal static extern int ObserverError(StringBuilder output,int capacity);
        [DllImport("user32.dll")] internal static extern int GetMessageTime();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr handle,out uint processId);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr handle,ref Point point);
        [DllImport("kernel32.dll")] internal static extern ulong GetTickCount64();
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr handle,int attribute,out Rect rect,int size);
    }
}
