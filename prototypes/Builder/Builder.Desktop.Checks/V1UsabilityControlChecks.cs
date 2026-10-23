using Builder.Core;
using Builder.Desktop;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static async Task RunV1UsabilityControls()
    {
        var failures=new List<Exception>();
        foreach(var test in new Func<Task>[]{V1TransformKeys,V1ConstructionTextKeys,V1OperationDrafts,V1MinimumAndPrompts,V1NativePromptGuard,V1CreatedSelectionVisible,V1AccessibleRecoveryAndViewport,V1ScaledControls,V1AccessibleChrome,V1AccessibleDialogs})
        {try{await test();}catch(Exception error){failures.Add(error);observations.Add(new{test=test.Method.Name,failed=error.ToString()});Console.WriteLine("FAIL "+test.Method.Name+": "+error.Message);}}
        if(failures.Count>0)throw new AggregateException("Focused usability regressions failed.",failures);
    }
    private static KeyEventArgs V1Enter(System.Windows.Interop.HwndSource source,UIElement target,RoutedEvent routed)
    {
        Keyboard.ClearFocus();var key=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Enter){RoutedEvent=routed,Source=target};target.RaiseEvent(key);return key;
    }
    private static async Task V1MovePreview()
    {await Open("cube-20mm.stl");await Workspace(1);Click(Named<Button>("MoveHandleButton"));Named<CheckBox>("SnapTransforms").IsChecked=false;ArrayNumber("PositionX",42);Click(Named<Button>("PreviewTransformButton"));await Idle();Assert(editor.Snapshot.Preview is {Request:MoveTransform},"Move preview missing.");}
    private static async Task V1TransformKeys()
    {
        await V1MovePreview();var accepted=editor.Snapshot;var preview=accepted.Preview!;
        using var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-v1-transform-keys"){Width=1,Height=1,WindowStyle=0});
        var cancel=Named<Button>("CancelTransformButton");var key=V1Enter(source,cancel,Keyboard.PreviewKeyDownEvent);
        Assert(!key.Handled&&ReferenceEquals(editor.Snapshot.Preview,preview)&&ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Enter on CancelTransformButton was intercepted and accepted the transform.");
        foreach(var name in new[]{"ApplyTransformButton","PreviewTransformButton","PositionX","SnapTransforms"})
        {var target=Named<UIElement>(name);key=V1Enter(source,target,Keyboard.PreviewKeyDownEvent);Assert(!key.Handled&&ReferenceEquals(editor.Snapshot.Preview,preview),"Global Enter intercepted transform control: "+name);}
        V1Enter(source,cancel,Keyboard.KeyDownEvent);await Idle();Assert(editor.Snapshot.Preview==null&&ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Normal button Enter did not cancel the transform.");
        await V1MovePreview();accepted=editor.Snapshot;preview=accepted.Preview!;var apply=Named<Button>("ApplyTransformButton");key=V1Enter(source,apply,Keyboard.PreviewKeyDownEvent);Assert(!key.Handled,"Apply Enter was globally intercepted.");V1Enter(source,apply,Keyboard.KeyDownEvent);await Idle();Assert(ReferenceEquals(editor.Snapshot.Scene,preview.Scene),"Normal Apply button Enter did not accept reviewed scene.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Keyboard Apply did not remain one Undo step.");
        await V1MovePreview();accepted=editor.Snapshot;ArrayNumber("PositionX",48);var button=Named<Button>("PreviewTransformButton");key=V1Enter(source,button,Keyboard.PreviewKeyDownEvent);Assert(!key.Handled,"Preview Enter was intercepted.");V1Enter(source,button,Keyboard.KeyDownEvent);await Idle();Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&editor.Snapshot.Preview is {} p&&Math.Abs(p.Scene.Bounds.Center.X-48)<.001,"Preview button Enter committed or failed to review changed draft.");
        var field=Named<TextBox>("PositionX");CheckLocalTextUndo(field,"48","-12.375");key=V1Enter(source,field,Keyboard.PreviewKeyDownEvent);Assert(!key.Handled&&ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"TextBox Enter committed a transform.");Click(Named<Button>("CancelTransformButton"));
        await V1MovePreview();preview=editor.Snapshot.Preview!;key=V1Enter(source,Named<UIElement>("ViewportSurface"),Keyboard.PreviewKeyDownEvent);Assert(key.Handled&&ReferenceEquals(editor.Snapshot.Scene,preview.Scene),"Neutral viewport Enter no longer accepts the reviewed transform.");
        Pass("Transform footer Enter honors Cancel, Apply and Preview button behavior; TextBox/local Undo stays local and neutral viewport Enter still accepts the reviewed edit");
    }
    private static async Task V1ConstructionTextKeys()
    {
        await Open("cube-20mm.stl");await Workspace(2);Click(Named<Button>("SplitButton"));await Idle();var accepted=editor.Snapshot;var field=Named<TextBox>("PlaneOffset");
        try
        {
            var policy=typeof(MainWindow).GetMethod("BlockConstructionShortcut",BindingFlags.NonPublic|BindingFlags.Static)!;
            foreach(var key in new[]{Key.Z,Key.Y})
            {Assert(!(bool)policy.Invoke(null,[key,ModifierKeys.Control,null,field])!,"Construction consumed local text Ctrl+"+key);Assert((bool)policy.Invoke(null,[key,ModifierKeys.Control,null,Named<UIElement>("ViewportSurface")])!,"Construction no longer blocks scene history during its draft.");}
            CheckLocalTextUndo(field,"10","12.375");Assert(ApplicationCommands.Redo.CanExecute(null,field),"Local PlaneOffset Redo unavailable.");ApplicationCommands.Redo.Execute(null,field);Assert(field.Text=="12.375","Local PlaneOffset Redo lost its draft.");SameAccepted(accepted);
        }
        finally{Click(ConstructionCancel());}
        Pass("Construction keyboard policy preserves PlaneOffset Ctrl+Z/Y and actual local routed Undo/Redo without exposing scene history during a construction draft");
    }
    private static async Task V1OperationDrafts()
    {
        await Open("cube-20mm.stl");await Workspace(1);var accepted=editor.Snapshot;const string draft="Unapplied rename Δ";Named<TextBox>("ObjectName").Text=draft;Named<TextBox>("ObjectName").Select(3,4);
        var path=Path.Combine(output,"draft-preserved.stl");await ExportViaDialog(false,path);SameAccepted(accepted);Assert(File.Exists(path)&&Named<TextBox>("ObjectName").Text==draft,"Successful export erased an unchanged-context rename draft.");
        var blocked=Path.Combine(output,"blocked.stl");Directory.CreateDirectory(blocked);await ExportViaDialog(false,blocked);SameAccepted(accepted);Assert(Named<TextBox>("ObjectName").Text==draft,"Failed export erased a rename draft.");
        await ExportViaDialog(false,null);SameAccepted(accepted);Assert(Named<TextBox>("ObjectName").Text==draft,"Cancelled export erased a rename draft.");
        navigationDialogs.OpenPath=NavigationInput("invalid.3mf");navigationDialogs.Decision=DirtyDecision.Discard;await ChooseDocument();SameAccepted(accepted);Assert(Named<TextBox>("ObjectName").Text==draft,"Failed Open erased an unchanged-context rename draft.");
        navigationDialogs.SavePath=Path.Combine(output,"draft-preserved-save.3mf");Assert(await (Task<bool>)Invoke("Save",true)!,"Native Save failed.");Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&Named<TextBox>("ObjectName").Text==draft&&Named<TextBlock>("SceneTitle").Text.Contains("draft-preserved-save"),"Save failed to preserve draft or refresh chrome.");
        Ok(editor.Execute(new AddCube()));Refresh();Assert(Named<TextBox>("ObjectName").Text!=draft,"A changed accepted scene retained an unrelated rename draft.");
        Pass("Export success/failure/cancel and failed Open preserve an unchanged-context rename draft; Save refreshes path/title while keeping it, and changed accepted context resets it");
    }
    private static async Task V1NativePromptGuard()
    {
        await Open("cube-20mm.stl");await Workspace(1);var accepted=editor.Snapshot;const string draft="Keep while choosing a file";Named<TextBox>("ObjectName").Text=draft;
        var prompt=typeof(MainWindow).GetField("documentPromptActive",PrivateInstance)!;
        using var source=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-v1-prompt-keys"){Width=1,Height=1,WindowStyle=0});
        try
        {
            prompt.SetValue(window,true);Invoke("RefreshWorkspaceNavigation");Keyboard.ClearFocus();
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Delete){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=Named<UIElement>("ViewportSurface")};Named<UIElement>("ViewportSurface").RaiseEvent(key);await Idle();
            Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(accepted.SelectedObjectIds)&&editor.Snapshot.Revision==accepted.Revision&&editor.Snapshot.CanUndo==accepted.CanUndo&&editor.Snapshot.CanRedo==accepted.CanRedo,"A main-window Delete shortcut edited the document while a native document prompt was active.");
            Assert(Named<TextBox>("ObjectName").Text==draft&&key.Handled,"Prompt guard failed to retain the idle draft or consume the owner-window shortcut.");
        }
        finally{prompt.SetValue(window,false);Invoke("RefreshWorkspaceNavigation");}
        var after=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Delete){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=Named<UIElement>("ViewportSurface")};Named<UIElement>("ViewportSurface").RaiseEvent(after);await Idle();Assert(editor.Snapshot.Scene.Objects.IsEmpty,"Main-window Delete remained blocked after the prompt ended.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,accepted.Scene),"Post-prompt Delete did not retain ordinary Undo.");
        Pass("Main-window keyboard shortcuts cannot change accepted scene, selection, history or rename drafts while a native document prompt is active; ordinary Delete/Undo resumes afterward");
    }
    private static void V1DialogCapture(FrameworkElement content,double width,string name)
    {
        content.Measure(new(width,double.PositiveInfinity));content.Arrange(new Rect(new Point(),content.DesiredSize));content.UpdateLayout();
        foreach(var button in Tree(content).OfType<Button>()){var b=button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));Assert(b.Width>0&&b.Height>0&&b.Left>=0&&b.Right<=width+.1&&b.Bottom<=content.ActualHeight+.1,"Dialog action clipped: "+button.Content);}
        var height=content.ActualHeight+content.Margin.Top+content.Margin.Bottom;var image=new RenderTargetBitmap((int)width,(int)Math.Ceiling(height),96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var draw=background.RenderOpen())draw.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));image.Render(background);image.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);
    }
    private static async Task V1MinimumAndPrompts()
    {
        await Open("cube-20mm.stl");var names=new Dictionary<int,string[]>{
            [0]=["InsertShapePalette","PaletteTetrahedronButton","NewTextButton","ImageReliefButton","InsertButton"],
            [1]=["MoveHandleButton","BedButton","CenterOnPlateButton","RadialArrayButton","DuplicateButton","ArrayButton","DistributeButton","MirrorButton","MeasureButton","EditMembersButton"],
            [2]=["SplitButton","CombineButton","RepairButton","HollowButton","SimplifyButton","SmoothButton","SeparateButton","EmbossButton","ExtrudeDownButton"],
            [3]=["MaterialButton","SurfaceMaterialButton","BrushMaterialButton"],
            [4]=["ViewIsometricButton","ViewTopButton","FitSceneButton","ViewGrid","ViewReflections","ViewShadingSmooth","ResetDisplayButton","AboutModelButton","RecoverButton"]};
        foreach(var entry in names){await Workspace(entry.Key);Layout(1040,720);foreach(var name in new[]{"FileStrip","NewButton","RecentFilesMenu","SaveButton","SaveAsButton","ExportButton","UndoButton","RedoButton"}.Concat(entry.Value))Reachable(name);Assert(Named<FrameworkElement>("ViewportSurface").ActualHeight>=250,"Ribbon consumed the usable viewport.");Capture("v1-"+WorkspaceNames[entry.Key].ToLowerInvariant()+"-minimum",1040,720);}
        var prompt=new SaveChangesDialog(editor.Snapshot,"opening another model");var content=(FrameworkElement)prompt.Content;var cancel=Tree(content).OfType<Button>().Single(b=>b.Content?.ToString()=="Cancel");Assert(cancel.IsDefault&&cancel.IsCancel&&prompt.Decision==DirtyDecision.Cancel,"Dirty prompt does not default to safe Cancel.");V1DialogCapture(content,442,"v1-save-discard-cancel");prompt.Close();
        foreach(var format in new[]{"model.stl","model.obj","model.ply","model.gltf","model.glb"})
        {var dialog=new UnitsDialog(format);Assert(dialog.Unit==UnitsDialog.DefaultUnit(format),"Format source-unit default changed.");V1DialogCapture((FrameworkElement)dialog.Content,392,"v1-"+Path.GetExtension(format)[1..]+"-units");dialog.Close();}
        Assert(DocumentDialogs.ModelOpenFilter.Contains("*.glb")&&DocumentDialogs.SceneExportFilter.Contains("STL geometry only")&&DocumentDialogs.SelectionExportFilter.Contains("3MF selection"),"File choices hide interchange/native distinction.");
        Pass("Current five workspaces and persistent file/history commands fit1040×720 with usable viewport; safe Save/Discard/Cancel and explicit-unit dialogs retain readable actions and format guidance");
    }
}
