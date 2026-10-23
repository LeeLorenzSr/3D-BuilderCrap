using Builder.Core;
using Builder.Desktop;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static partial class Program
{
    private static T RecoveryControl<T>(RecoveryDialog dialog,string name) where T:class=>(T)dialog.FindName(name);
    private static string[] RecoveryFiles(string directory)=>Directory.GetFiles(directory,"*.recovery.json");
    private static void StopRecoveryTimer()=>((DispatcherTimer?)typeof(MainWindow).GetField("recoveryTimer",PrivateInstance)!.GetValue(window))?.Stop();
    private static async Task RecoveryWindow(string directory)
    {
        StopRecoveryTimer();if(editor.RecoveryDirectory!=null)editor.Dispose();
        window=new MainWindow(new JsonRecentFileStore(Path.Combine(output,Guid.NewGuid()+"-recents.json")),navigationDialogs,directory);
        editor=(EditorSession)typeof(MainWindow).GetField("editor",PrivateInstance)!.GetValue(window)!;surface=(FrameworkElement)window.Content;Refresh();Layout(1040,680);await Workspace(4);
    }
    private static async Task WaitRecovery(Func<bool> ready)
    {for(var i=0;i<300&&!ready();i++)await Task.Delay(10);Assert(ready(),"Recovery asynchronous UI did not settle.");}
    private static async Task ShowRecovery()=>await (Task)Invoke("ShowRecovery",false)!;
    private static async Task<EditorSnapshot> MakeRecovery(string directory,string title,bool native=false,bool assembly=false)
    {
        using var producer=new EditorSession();Ok(producer.ConfigureRecovery(directory));
        if(assembly){Ok(await producer.OpenAsync(Path.Combine(root,".local/evidence/model-metadata-20261021/inputs/painted-group.3mf")));var group=producer.Snapshot.Scene.Groups.Single();Ok(producer.EnterGroup(group.Id,group.ObjectIds[^1]));}
        else Ok(producer.Execute(new AddCube()));
        Ok(producer.UpdateModelMetadata(producer.Snapshot.Scene.Metadata with{Title=title},producer.Snapshot.Revision));
        if(native){Ok(await producer.SaveAsync(Path.Combine(output,title+".3mf")));Ok(producer.UpdateModelMetadata(producer.Snapshot.Scene.Metadata with{Designer="Unsaved recovery author"},producer.Snapshot.Revision));}
        Ok(await producer.CheckpointRecoveryAsync());return producer.Snapshot;
    }
    private static void CaptureRecoveryDialog(RecoveryDialog dialog,string name,double width=560,double height=440)
    {
        var content=(FrameworkElement)dialog.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        foreach(var control in new[]{"RestoreRecoveryButton","DiscardRecoveryButton","LaterRecoveryButton"}){var button=RecoveryControl<Button>(dialog,control);var bounds=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));Assert(bounds.Width>=90&&bounds.Left>=0&&bounds.Right<=width+.1&&bounds.Top>=0&&bounds.Bottom<=height+.1,"Recovery footer clipped: "+control);}
        var list=RecoveryControl<ListBox>(dialog,"RecoveryCopies");Assert(list.ActualHeight>40,"Recovery list has no usable viewport.");if(list.Items.Count>0)list.ScrollIntoView(list.Items[^1]);content.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);
    }
    private sealed class HoldRecovery : IProgress<OperationProgress>,IDisposable
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release=new();
        public void Report(OperationProgress progress){Entered.TrySetResult();if(!Release.Wait(TimeSpan.FromSeconds(10)))throw new IOException("Held recovery test timed out.");}
        public void Dispose(){Release.Set();Release.Dispose();}
    }
    private static async Task RunRecoveryControls()
    {
        Assert(editor.RecoveryDirectory==null&&!Named<Button>("RecoverButton").IsEnabled,"Injected check window touched current-user recovery storage.");
        var directory=Path.Combine(output,"startup-store");await MakeRecovery(directory,"First copy",true);await MakeRecovery(directory,"Second copy");
        using var live=new EditorSession();Ok(live.ConfigureRecovery(directory));Ok(live.Execute(new AddCube()));Ok(await live.CheckpointRecoveryAsync());
        await RecoveryWindow(directory);var before=editor.Snapshot;Named<TextBox>("ObjectName").Text="Idle name draft";var initialPrompts=navigationDialogs.RecoveryPrompts;
        navigationDialogs.RecoveryAction=dialog=>{
            Assert(RecoveryControl<ListBox>(dialog,"RecoveryCopies").Items.Count==2,"Startup included a live other-instance copy or omitted abandoned work.");Assert(RecoveryControl<Button>(dialog,"LaterRecoveryButton").IsDefault&&RecoveryControl<Button>(dialog,"LaterRecoveryButton").IsCancel,"Not now is not conservative Enter/Escape default.");
            CaptureRecoveryDialog(dialog,"recovery-dialog");CaptureRecoveryDialog(dialog,"recovery-dialog-minimum",460,380);
            using var keySource=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-recovery-keys"){Width=1,Height=1,WindowStyle=0});var escape=new KeyEventArgs(Keyboard.PrimaryDevice,keySource,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};dialog.RaiseEvent(escape);Assert(escape.Handled&&!dialog.Restored,"Escape did not choose Not now.");return Task.CompletedTask;
        };
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await WaitRecovery(()=>navigationDialogs.RecoveryPrompts==initialPrompts+1&&!(bool)typeof(MainWindow).GetField("documentPromptActive",PrivateInstance)!.GetValue(window)!);StopRecoveryTimer();
        // Loaded initializes accepted presentation first; dismissal itself must not reset an existing draft.
        Named<TextBox>("ObjectName").Text="Deferred recovery draft";before=editor.Snapshot;await ShowRecovery();SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Deferred recovery draft","Not now erased an idle draft.");var promptCount=navigationDialogs.RecoveryPrompts;window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Idle();StopRecoveryTimer();Assert(navigationDialogs.RecoveryPrompts==promptCount,"Startup recovery prompted more than once.");
        Pass("Recovery is opt-in for injected windows; startup lists abandoned copies only, has a scrollable minimum-size list/fixed actions, and Not now/Escape preserve copies and drafts with manual access later");

        directory=Path.Combine(output,"cadence-store");await RecoveryWindow(directory);navigationDialogs.RecoveryAction=null;window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));await Idle();var timer=(DispatcherTimer)typeof(MainWindow).GetField("recoveryTimer",PrivateInstance)!.GetValue(window)!;Assert(timer.Interval==TimeSpan.FromSeconds(10),"Production recovery cadence is not ten seconds.");timer.Interval=TimeSpan.FromMilliseconds(40);
        Ok(editor.Execute(new AddCube()));Refresh();Named<TextBox>("ObjectName").Text="Unaccepted typed name";before=editor.Snapshot;await WaitRecovery(()=>RecoveryFiles(directory).Length==1);StopRecoveryTimer();await (Task)typeof(MainWindow).GetField("recoveryCheckpointTask",PrivateInstance)!.GetValue(window)!;SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Unaccepted typed name"&&!File.ReadAllText(RecoveryFiles(directory).Single()).Contains("Unaccepted typed name"),"Timer serialized a local draft or reset fields.");
        using(var hold=new HoldRecovery())
        {
            var checkpoint=editor.CheckpointRecoveryAsync(progress:hold);await hold.Entered.Task;var draft=Named<TextBox>("ObjectName").Text;
            Invoke("DuplicateClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());Assert(Named<TextBox>("ObjectName").Text==draft,"Busy duplicate/Undo erased an idle draft.");hold.Release.Set();Ok(await checkpoint);Invoke("RefreshWorkspaceNavigation");
        }
        SameAccepted(before);var checkpointFile=RecoveryFiles(directory).Single();
        using(var locked=new FileStream(checkpointFile,FileMode.Open,FileAccess.Read,FileShare.Read)){await (Task)Invoke("CheckpointRecovery")!;SameAccepted(before);Assert(Named<TextBlock>("RecoveryWarning").Visibility==Visibility.Visible,"Failed checkpoint did not show persistent nonfatal warning.");Capture("recovery-warning-minimum",1040,680);}
        await (Task)Invoke("CheckpointRecovery")!;Assert(Named<TextBlock>("RecoveryWarning").Visibility==Visibility.Collapsed&&Named<TextBox>("ObjectName").Text=="Unaccepted typed name","Successful retry did not clear warning or preserved draft.");
        await Workspace(1);Click(Named<Button>("MoveHandleButton"));var prior=Hash(checkpointFile);await (Task)Invoke("CheckpointRecovery")!;Assert(Hash(checkpointFile)==prior,"Cadence ran during an active tool.");Click(Named<Button>("CancelTransformButton"));
        Pass("The actual DispatcherTimer checkpoints accepted work without history/path/draft changes; busy actions preserve drafts, write failures warn nonmodally, retries clear warning and active tools defer cadence");

        directory=Path.Combine(output,"restore-store");var recovered=await MakeRecovery(directory,"Assembly recovery",true,true);await RecoveryWindow(directory);
        navigationDialogs.RecoveryAction=async dialog=>{Click(RecoveryControl<Button>(dialog,"RestoreRecoveryButton"));await dialog.CurrentAction;Assert(dialog.Restored,"Valid recovery failed.");};await ShowRecovery();
        Assert(editor.Snapshot.IsDirty&&editor.Snapshot.FilePath==recovered.FilePath&&!editor.Snapshot.CanUndo&&!editor.Snapshot.CanRedo&&editor.Snapshot.Scene.ContentId==recovered.Scene.ContentId&&editor.Snapshot.Scene.Metadata==recovered.Scene.Metadata&&editor.Snapshot.SelectedObjectIds.SequenceEqual(recovered.SelectedObjectIds)&&editor.Snapshot.EditingGroupId==recovered.EditingGroupId&&PlyColors(editor.Snapshot.Scene).SequenceEqual(PlyColors(recovered.Scene)),"Restore lost native path, dirty state, scope/selection, model information or colors.");
        Assert(RecoveryFiles(directory).Length==1,"Restore removed its crash-recoverable checkpoint before save.");Assert(await (Task<bool>)Invoke("Save",false)!,"Restored normal native Save failed.");Assert(!editor.Snapshot.IsDirty&&RecoveryFiles(directory).Length==0,"Successful Save did not retire recovery.");
        Ok(editor.UpdateModelMetadata(editor.Snapshot.Scene.Metadata with{Designer="New unsaved author"},editor.Snapshot.Revision));Refresh();await (Task)Invoke("CheckpointRecovery")!;before=editor.Snapshot;checkpointFile=RecoveryFiles(directory).Single();
        var blocked=Path.Combine(output,"blocked-save.3mf");Directory.CreateDirectory(blocked);navigationDialogs.SavePath=blocked;Assert(!await (Task<bool>)Invoke("Save",true)!,"Blocked Save unexpectedly succeeded.");SameAccepted(before);Assert(File.Exists(checkpointFile),"Failed Save retired recovery.");
        navigationDialogs.Decision=DirtyDecision.Cancel;Named<TextBox>("ObjectName").Text="Still local";await NewDocument();SameAccepted(before);Assert(File.Exists(checkpointFile)&&Named<TextBox>("ObjectName").Text=="Still local","Cancelled New retired recovery or erased draft.");navigationDialogs.Decision=DirtyDecision.Discard;await NewDocument();Assert(editor.Snapshot.Scene.Objects.IsEmpty&&RecoveryFiles(directory).Length==0,"Successful explicit New/Discard did not retire prior recovery.");
        Pass("Restore retains native path, group-member scope, identity, metadata/colors and dirty work with fresh history; Save retires recovery, while failed Save/cancelled New preserve it and explicit New/Discard retires it");

        directory=Path.Combine(output,"damaged-store");await MakeRecovery(directory,"Damaged copy");var damaged=RecoveryFiles(directory).Single();File.WriteAllText(damaged,"{ broken recovery",Encoding.UTF8);await RecoveryWindow(directory);before=editor.Snapshot;
        navigationDialogs.RecoveryAction=async dialog=>{
            var item=(ListBoxItem)RecoveryControl<ListBox>(dialog,"RecoveryCopies").Items[0];Assert(((RecoveryCandidate)item.Tag).Problem!=null,"Damaged copy was silently pruned.");Click(RecoveryControl<Button>(dialog,"RestoreRecoveryButton"));await dialog.CurrentAction;SameAccepted(before);Assert(!dialog.Restored&&RecoveryControl<TextBlock>(dialog,"RecoveryError").Visibility==Visibility.Visible&&File.Exists(damaged),"Failed Restore lost candidate or accepted document.");CaptureRecoveryDialog(dialog,"recovery-damaged-minimum",460,380);Click(RecoveryControl<Button>(dialog,"DiscardRecoveryButton"));await dialog.CurrentAction;
        };await ShowRecovery();SameAccepted(before);Assert(!File.Exists(damaged),"Exact damaged-copy Discard failed.");
        var unverifiable=new RecoveryCandidate(Guid.NewGuid(),Guid.Empty,"",DateTimeOffset.UtcNow,null,"Unreadable copy",0,"This copy cannot be verified. Choose Not now and try again later.");var unavailable=new RecoveryDialog([unverifiable],(_,_,_)=>throw new InvalidOperationException("Unverifiable action must stay disabled."));Assert(!RecoveryControl<Button>(unavailable,"RestoreRecoveryButton").IsEnabled&&!RecoveryControl<Button>(unavailable,"DiscardRecoveryButton").IsEnabled,"Unverifiable copy offered destructive action.");unavailable.Close();
        Pass("Corrupt copies remain visible; failed Restore retains document/candidate with retry/discard/later, exact Discard affects only that copy and unverifiable entries cannot be destructively discarded");

        directory=Path.Combine(output,"close-store");await RecoveryWindow(directory);Ok(editor.Execute(new AddCube()));Refresh();await (Task)Invoke("CheckpointRecovery")!;checkpointFile=RecoveryFiles(directory).Single();before=editor.Snapshot;
        navigationDialogs.Decision=DirtyDecision.Cancel;Assert(!await (Task<bool>)Invoke("RequestCloseDocument")!,"Close Cancel approved disposal.");SameAccepted(before);Assert(File.Exists(checkpointFile),"Cancelled Close retired recovery.");navigationDialogs.Decision=DirtyDecision.Discard;Assert(await (Task<bool>)Invoke("RequestCloseDocument")!,"Close readiness failed.");Assert(File.Exists(checkpointFile),"Point-in-time readiness retired recovery too soon.");
        using(var locked=new FileStream(checkpointFile,FileMode.Open,FileAccess.Read,FileShare.Read))
        {var closing=new CancelEventArgs();typeof(MainWindow).GetMethod("OnClosing",PrivateInstance,null,[typeof(object),typeof(CancelEventArgs)],null)!.Invoke(window,[window,closing]);Assert(closing.Cancel&&File.Exists(checkpointFile),"Failed final retirement did not keep window/recovery open.");Ok(editor.Execute(new RenameObject(editor.Snapshot.SelectedObjectId!.Value,"Editing remains available")));}
        Assert(await (Task<bool>)Invoke("RequestCloseDocument")!,"Repeated close readiness failed.");var finalClosing=new CancelEventArgs();typeof(MainWindow).GetMethod("OnClosing",PrivateInstance,null,[typeof(object),typeof(CancelEventArgs)],null)!.Invoke(window,[window,finalClosing]);Assert(!finalClosing.Cancel&&RecoveryFiles(directory).Length==0,"Confirmed final close did not retire recovery.");StopRecoveryTimer();editor.Dispose();navigationDialogs.RecoveryAction=null;
        Pass("Close Cancel and readiness leave recovery intact; final retirement failure keeps editing available, and only a confirmed revision-bound final close retires/seals the session");
    }
}
