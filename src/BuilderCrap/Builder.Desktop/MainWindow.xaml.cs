using Builder.Core;
using Builder.Windows.Text;
using Builder.Windows.Images;
using Microsoft.Win32;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

public partial class MainWindow : Window
{
    private readonly EditorSession editor = new(textOutlineProvider:new WindowsFontOutlineProvider(),imageDecoder:new WindowsRasterImageDecoder());
    private readonly PerspectiveCamera camera = new() { FieldOfView = 45, UpDirection = new(0,0,1) };
    private readonly OrthographicCamera orthographicCamera = new();
    private readonly ConditionalWeakTable<Mesh,MeshGeometry3D> meshCache = new();
    private readonly Dictionary<GeometryModel3D,Guid> hitObjects = [];
    private CancellationTokenSource? operation;
    private bool refreshing, closeApproved, useOrthographic;
    private Point lastMouse;
    private MouseButton? drag;
    private Point3D focus = new(10,10,10);
    private double yaw = -0.85, pitch = 0.55, distance = 120;
    private long selectionPresentationRevision=-1;
    private SceneSelectionUnit? selectionTarget;
    private SceneGroup? selectionGroup;
    private bool singleEditableSelection;
    private int selectionUnitCount;
    private bool selectionHasGroups;

    public MainWindow():this(JsonRecentFileStore.ForCurrentUser(),new DocumentDialogs(),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"3D BuilderCrap","Recovery"),new JsonViewPreferenceStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"3D BuilderCrap","view-preferences.json"))){}
    public MainWindow(IRecentFileStore recentStore,IDocumentDialogs documentDialogs,string? recoveryDirectory=null,IViewPreferenceStore? viewPreferenceStore=null)
    {
        this.recentStore=recentStore;this.documentDialogs=documentDialogs;
        if(viewPreferenceStore!=null)editor.Presentation.ConfigurePreferences(viewPreferenceStore);
        InitializeComponent(); Viewport.Camera = camera;
        InitializeTransforms();
        InitializeArrangement();
        InitializePrimitives();
        InitializeMaterials();
        InitializeWorkspaceNavigation();
        InitializeViewControls();
        InitializeDocumentNavigation();
        InitializeRecovery(recoveryDirectory);
        InitializeMcp();
        SystemParameters.StaticPropertyChanged+=DesktopSystemChanged;
        Closed+=(_,_)=>SystemParameters.StaticPropertyChanged-=DesktopSystemChanged;
        Loaded += (_,_) => { Refresh(); FitScene(); };
    }
    private SceneObject? Selected => editor.Snapshot.Scene.Objects.FirstOrDefault(o => o.Id == editor.Snapshot.SelectedObjectId);
    private void DesktopSystemChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(!IsLoaded||Dispatcher.HasShutdownStarted)return;
        if(!Dispatcher.CheckAccess()){_=Dispatcher.BeginInvoke(()=>DesktopSystemChanged(sender,e));return;}
        gridBounds=null;RenderScene(); // Chrome/theme refresh retains accepted geometry, tool drafts and preview ownership.
    }
    private void UpdateSelectionPresentation()
    {
        var snapshot=editor.Snapshot;
        if(selectionPresentationRevision==snapshot.Revision) return;
        var units=snapshot.GetSelectionUnits();
        selectionUnitCount=units.Length;
        // Canonical scene groups contain at least two leaves; scoped units are single members.
        selectionHasGroups=snapshot.EditingGroupId==null && units.Any(unit=>unit.ObjectIds.Length>1);
        selectionTarget=units.IsEmpty ? null : new(snapshot.SelectedObjectId ?? snapshot.SelectedObjectIds[0],
            units.Length==1 ? units[0].Name : $"{units.Length} selected items",snapshot.SelectedObjectIds,
            Bounds3.From(units.SelectMany(u=>new[]{u.Bounds.Min,u.Bounds.Max})));
        var ids=snapshot.SelectedObjectIds.ToHashSet();
        selectionGroup=snapshot.EditingGroupId!=null ? null : snapshot.Scene.Groups.FirstOrDefault(g=>
            g.ObjectIds.Length==ids.Count && g.ObjectIds.All(ids.Contains));
        singleEditableSelection=ids.Count==1 && (snapshot.EditingGroupId!=null || !snapshot.Scene.Groups.Any(g=>g.ObjectIds.Contains(snapshot.SelectedObjectIds[0])));
        selectionPresentationRevision=snapshot.Revision;
    }
    private SceneSelectionUnit? SelectedTarget { get {UpdateSelectionPresentation(); return selectionTarget;} }
    private bool SingleUngroupedSelection { get {UpdateSelectionPresentation(); return singleEditableSelection;} }
    private SceneGroup? SelectedGroup { get {UpdateSelectionPresentation(); return selectionGroup;} }
    private bool CanRenameSelection => SingleUngroupedSelection || SelectedGroup!=null;
    private static Bounds3 SelectionBounds(Scene scene,IEnumerable<Guid> ids)
    {
        var selected=ids.ToHashSet();
        return Bounds3.From(scene.Objects.Where(o=>selected.Contains(o.Id)).SelectMany(o=>new[]{o.Bounds.Min,o.Bounds.Max}));
    }
    private static string Number(double value) => value.ToString("0.######",CultureInfo.CurrentCulture);
    private void Message(string text,bool error = false)
    { Status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,error ? "ErrorInk" : "StatusInk"); Status.Text = text; RefreshWorkspaceNavigation(); }
    private void Refresh(bool render = true)=>RefreshScenePresentation(render,false);
    private void RefreshScenePresentation(bool render,bool preserveObjectNameDraft)
    {
        var snapshot = editor.Snapshot;
        RefreshMcpPresentation();
        refreshing = true;
        RefreshObjectTree(snapshot.Scene,snapshot.SelectedObjectIds);
        var selectedIds=snapshot.SelectedObjectIds.ToHashSet();
        var obj = SelectedTarget;
        EditPanel.IsEnabled = obj != null;
        ObjectNameLabel.Text=SelectedGroup!=null ? "Group name" : snapshot.EditingGroupId!=null ? "Member name" : "Object name";
        System.Windows.Automation.AutomationProperties.SetName(ObjectName,ObjectNameLabel.Text);
        SelectionHint.Text = obj == null ? "Select objects to edit. Ctrl adds or removes; Shift selects a range." :
            $"{obj.Name} · {snapshot.SelectedObjectIds.Length} object{(snapshot.SelectedObjectIds.Length==1 ? "" : "s")}";
        RefreshTransformFieldPresentation();
        if(obj != null)
        {
            if(!preserveObjectNameDraft)ObjectName.Text = obj.Name;
            ObjectSummary.Text = $"{snapshot.Scene.Objects.Where(o=>selectedIds.Contains(o.Id)).Sum(o=>o.Mesh.Triangles.Length):N0} triangles · millimeters · Z up";
        }
        else { if(!preserveObjectNameDraft)ObjectName.Text = ""; ObjectSummary.Text = ""; }
        var title = snapshot.FilePath == null ? "Untitled scene" : Path.GetFileName(snapshot.FilePath);
        SceneTitle.Text = title + (snapshot.IsDirty ? " • Unsaved changes" : "");
        Title = title + (snapshot.IsDirty ? " *" : "") + " — 3D BuilderCrap";
        var bnd = snapshot.Scene.Bounds.Size;
        SceneSummary.Text = snapshot.Scene.Objects.Length == 0 ? "" : $"{snapshot.Scene.Objects.Length} object{(snapshot.Scene.Objects.Length == 1 ? "" : "s")}  ·  {bnd.X:0.###} × {bnd.Y:0.###} × {bnd.Z:0.###} mm";
        UndoButton.IsEnabled = snapshot.CanUndo; RedoButton.IsEnabled = snapshot.CanRedo;
        SaveButton.IsEnabled = SaveAsButton.IsEnabled = ExportButton.IsEnabled = snapshot.Scene.Objects.Length > 0;
        EmptyHint.Visibility = snapshot.Scene.Objects.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        RefreshConstruction();
        RefreshTransforms();
        RefreshArrangement();
        RefreshPrimitives();
        RefreshInspection();
        RefreshTextTool();
        RefreshImageRelief();
        RefreshHollow();
        RefreshSimplify();
        RefreshSmooth();
        RefreshSeparate();
        RefreshExtrudeDown();
        RefreshMaterials();
        refreshing = false;
        RefreshWorkspaceNavigation();
        if(render) RenderScene();
    }
    private void Apply(Edit edit)
    { var result = editor.Execute(edit with {MemberGroupId=editor.Snapshot.EditingGroupId},editor.Snapshot.Revision); Message(result.Message,!result.Succeeded); if(result.Succeeded) Refresh(); }
    private void UndoClick(object sender,RoutedEventArgs e) { if(ModalToolActive)return; var r = editor.Undo(); if(r.Succeeded)Refresh(); Message(r.Message,!r.Succeeded); }
    private void RedoClick(object sender,RoutedEventArgs e) { if(ModalToolActive)return; var r = editor.Redo(); if(r.Succeeded)Refresh(); Message(r.Message,!r.Succeeded); }
    private void AddCubeClick(object sender,RoutedEventArgs e) => BeginPrimitive(PrimitiveKind.Cube);
    private void AddCylinderClick(object sender,RoutedEventArgs e) => BeginPrimitive(PrimitiveKind.Cylinder);
    private void AddSphereClick(object sender,RoutedEventArgs e) => BeginPrimitive(PrimitiveKind.Sphere);
    private void RenameClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable || !CanRenameSelection) return;
        Edit edit=SelectedGroup is {} group ? new RenameGroup(group.Id,ObjectName.Text) : new RenameObject(Selected!.Id,ObjectName.Text);
        var result=editor.Execute(edit with {MemberGroupId=editor.Snapshot.EditingGroupId},editor.Snapshot.Revision);
        Message(result.Message,!result.Succeeded);
        if(result.Succeeded) { Refresh(); ViewportSurface.Focus(); }
    }
    private void RotateClick(object sender,RoutedEventArgs e) => PreviewNumericTransform(HandleMode.Rotate);
    private async void DuplicateClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable || editor.Snapshot.SelectedObjectIds.IsEmpty) return;
        var captured=editor.Snapshot;
        var result=await RunOperation((ct,p)=>editor.DuplicateSelectionAsync(captured.SelectedObjectIds,ct,p,captured.Revision,captured.EditingGroupId));
        if(result.Succeeded) { FitScene(); ViewportSurface.Focus(); }
    }
    private async void DeleteClick(object sender,RoutedEventArgs e)
    {
        if(!ArrangementSelectionAvailable || editor.Snapshot.SelectedObjectIds.IsEmpty) return;
        if(!CanDeleteSelection) { Message("A group must keep at least two members. Choose Done to delete the whole group.",true); return; }
        var captured=editor.Snapshot;
        var result=await RunOperation((ct,p)=>editor.DeleteSelectionAsync(captured.SelectedObjectIds,ct,p,captured.Revision,captured.EditingGroupId));
        if(result.Succeeded) ViewportSurface.Focus();
    }
    private void BedClick(object sender,RoutedEventArgs e) => PreviewOnBed();
    private bool ReadVector(TextBox x,TextBox y,TextBox z,out Vec3 value)
    {
        value = new();
        if(!double.TryParse(x.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var a) ||
            !double.TryParse(y.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var b) ||
            !double.TryParse(z.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var c) || !new Vec3(a,b,c).IsFinite)
        { Message("Enter a finite number in each field. Your scene is unchanged.",true); return false; }
        value = new(a,b,c); return true;
    }
    private void ResizeClick(object sender,RoutedEventArgs e) => PreviewNumericTransform(HandleMode.Scale);
    private void MoveClick(object sender,RoutedEventArgs e) => PreviewNumericTransform(HandleMode.Move);
    private void ResetClick(object sender,RoutedEventArgs e) { CancelTransform(); }
    private void SizeInputChanged(object sender,TextChangedEventArgs e)
    {
        if(refreshing || AspectLock?.IsChecked != true || SelectedTarget is not {} obj || sender is not TextBox field || !field.IsKeyboardFocused) return;
        if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var number) || !double.IsFinite(number) || number <= 0) return;
        var size = obj.Bounds.Size; var old = field == SizeX ? size.X : field == SizeY ? size.Y : size.Z;
        if(old <= 0) return;
        var factor = number/old; refreshing = true;
        if(field != SizeX) SizeX.Text = Number(size.X*factor);
        if(field != SizeY) SizeY.Text = Number(size.Y*factor);
        if(field != SizeZ) SizeZ.Text = Number(size.Z*factor);
        refreshing = false;
    }
    private void SelectionChanged(object sender,SelectionChangedEventArgs e)
        =>ObjectTreeSelectionChanged(e);
    private void SelectViewportObject(Guid? id,ModifierKeys modifiers)
        =>SelectViewportObjectAtRevision(id,modifiers,editor.Snapshot.Revision);
    private void SelectViewportObjectAtRevision(Guid? id,ModifierKeys modifiers,long expectedRevision)
    {
        var snapshot=editor.Snapshot;
        if(ModalToolActive || snapshot.Revision!=expectedRevision) return;
        if(id is {} outside && snapshot.EditingGroupId!=null && !ScopeObjects(snapshot).Any(o=>o.Id==outside))
        { Message("This object is outside the group being edited. Choose Done to return to the scene."); return; }
        if(id is not {} hit) { if((modifiers&(ModifierKeys.Control|ModifierKeys.Shift))==0) editor.SelectMany([],expectedRevision:expectedRevision); return; }
        if((modifiers&(ModifierKeys.Control|ModifierKeys.Shift))==0) { editor.SelectMany([hit],hit,expectedRevision); return; }
        var unit=snapshot.Scene.GetSelectionUnits([hit],snapshot.EditingGroupId).Single();
        var selected=snapshot.SelectedObjectIds.ToHashSet();
        if(modifiers.HasFlag(ModifierKeys.Control) && unit.ObjectIds.All(selected.Contains)) selected.ExceptWith(unit.ObjectIds);
        else selected.UnionWith(unit.ObjectIds);
        editor.SelectMany(selected,selected.Contains(hit) ? hit : null,expectedRevision);
    }

    private async Task<OperationResult> RunOperation(Func<CancellationToken,IProgress<OperationProgress>,Task<OperationResult>> action)
    {
        if(operation != null||editor.Snapshot.IsBusy){Message("An operation is already running. Try again when it finishes.");return new(Outcome.Busy,"An operation is already running.");}
        var context=editor.Snapshot;
        using var cancellation = new CancellationTokenSource(); operation = cancellation; RefreshWorkspaceNavigation();
        Toolbar.IsEnabled = Inspector.IsEnabled = ViewportSurface.IsEnabled = false;
        CancelOperation.Visibility = Progress.Visibility = Visibility.Visible;
        // Queued progress can arrive after completion; ignore it once this operation no longer owns the UI.
        var progress = new Progress<OperationProgress>(p => { if(ReferenceEquals(operation,cancellation)) Message(p.Stage + "…"); });
        try
        {
            var result = await action(cancellation.Token,progress);
            var current=editor.Snapshot;
            // File I/O can update path/dirty chrome without changing the selected model being named.
            var sameNameContext=ReferenceEquals(current.Scene,context.Scene)&&current.EditingGroupId==context.EditingGroupId&&
                current.SelectedObjectId==context.SelectedObjectId&&current.SelectedObjectIds.SequenceEqual(context.SelectedObjectIds);
            RefreshScenePresentation(true,sameNameContext);Message(result.Message,result.Outcome is not (Outcome.Success or Outcome.Cancelled));return result;
        }
        finally
        {
            operation = null; RefreshWorkspaceNavigation(); Inspector.IsEnabled = ViewportSurface.IsEnabled = true;
            RefreshConstruction(); RefreshTransforms(); RefreshArrangement();
            RefreshPrimitives();
            RefreshInspection();
        RefreshTextTool();
        RefreshImageRelief();
        RefreshHollow();
        RefreshSimplify();
        RefreshSmooth();
        RefreshSeparate();
        RefreshExtrudeDown();
        RefreshMaterials();
            CancelOperation.Visibility = Progress.Visibility = Visibility.Collapsed;
        }
    }
    private void CancelClick(object sender,RoutedEventArgs e)
    { if(mcpOperationActive){operation?.Cancel();return;}
        if(mcpPreviewActive){_ = CompleteRemotePreview(false);return;}
        if(materialsActive){CancelMaterials();return;}
        if(extrudeDownActive){CancelExtrudeDown();return;}
        if(separateActive){CancelSeparate();return;}
        if(smoothActive){CancelSmooth();return;}
        if(simplifyActive) CancelSimplify(); else if(hollowActive) CancelHollow(); else if(imageReliefActive) CancelImageRelief(); else if(textToolMode!=null) CancelTextTool(); else if(InspectionActive) EndInspection(); else if(primitiveActive) CancelPrimitive(); else if(arrangementMode!=null) CancelArrangement(); else if(handleMode!=null) CancelTransform(); else operation?.Cancel(); }
    private async void OpenClick(object sender,RoutedEventArgs e) => await ChooseModel(false);
    private async void InsertClick(object sender,RoutedEventArgs e) => await ChooseModel(true);
    private async void SaveClick(object sender,RoutedEventArgs e) => await Save(false);
    private async void SaveAsClick(object sender,RoutedEventArgs e) => await Save(true);
    private async void ExportClick(object sender,RoutedEventArgs e)=>await ExportDocument(false);
    private async void SelectionExport_Click(object sender,RoutedEventArgs e)=>await ExportDocument(true);
    // Kept as the millimeter adapter for existing callers; interchange UI uses explicit units.
    private Task<OperationResult> ExportSelectionTo(string filePath,EditorSnapshot captured)=>ExportSelectionInUnits(filePath,captured,ModelUnit.Millimeters);
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        CancelPendingPick();
        if(closeApproved&&closeApprovedRevision==editor.Snapshot.Revision&&!editor.Snapshot.IsBusy&&editor.Snapshot.Preview==null&&operation==null&&!WorkspaceToolActive)
        {
            if(mcpHost is not null)
            {
                e.Cancel=true;await StopMcp();Close();return;
            }
            if(recoveryDirectory!=null)
            {
                var completed=editor.CompleteClose(closeApprovedRevision.Value);
                if(!completed.Succeeded){e.Cancel=true;closeApproved=false;recoveryClosePending=false;Message(completed.Message,true);}
            }
            return;
        }
        closeApproved=false;
        if(documentPromptActive){e.Cancel=true;return;}
        if(operation != null) { e.Cancel = true; Message("Cancel the current operation before closing."); return; }
        if(materialsActive){e.Cancel=true;Message("Apply or cancel the material before closing.");return;}
        if(extrudeDownActive){e.Cancel=true;Message("Apply or cancel Extrude down before closing.");return;}
        if(separateActive){e.Cancel=true;Message("Apply or cancel Separate before closing.");return;}
        if(smoothActive){e.Cancel=true;Message("Apply or cancel Smooth before closing.");return;}
        if(simplifyActive){e.Cancel=true;Message("Apply or cancel Simplify before closing.");return;}
        if(hollowActive){e.Cancel=true;Message("Apply or cancel Hollow before closing.");return;}
        if(imageReliefActive){e.Cancel=true;Message("Add or cancel the image conversion before closing.");return;}
        if(textToolMode!=null){e.Cancel=true;Message("Apply or cancel text before closing.");return;}
        if(InspectionActive){e.Cancel=true;Message("Finish measurement, or apply or cancel the mirror, before closing.");return;}
        if(primitiveActive) {e.Cancel=true;Message("Add or cancel the shape before closing.");return;}
        if(arrangementMode != null) { e.Cancel=true; Message("Apply or cancel the arrangement before closing."); return; }
        if(handleMode != null) { e.Cancel = true; Message("Apply or cancel the transform before closing."); return; }
        if(constructionMode != null) { e.Cancel = true; Message("Apply or cancel the construction tool before closing."); return; }
        e.Cancel=true;
        if(await RequestCloseDocument())
        {
            // Close readiness belongs to one revision, including the queued final callback.
            _=Dispatcher.BeginInvoke(new Action(()=>
            {
                var current=editor.Snapshot;
                if(closeApprovedRevision==current.Revision&&!current.IsBusy&&current.Preview==null&&DocumentNavigationAvailable)Close();
                else{closeApproved=false;recoveryClosePending=false;Message("The scene changed. Close again when you are ready.");}
            }));
        }
    }
    private static bool BlockConstructionShortcut(Key key,ModifierKeys modifiers,IInputElement? focused,IInputElement? source)
        =>modifiers.HasFlag(ModifierKeys.Control)&&(key is Key.O or Key.I or Key.S or Key.D||
            focused is not TextBox&&source is not TextBox&&key is Key.Z or Key.Y);

    private async void OnKeyDown(object sender,KeyEventArgs e)
    {
        // Owned native dialogs handle their own keys; never act on shortcuts reaching the editor behind them.
        if(documentPromptActive){e.Handled=true;return;}
        if(e.Key==Key.F1){HelpClick(this,new RoutedEventArgs());e.Handled=true;return;}
        if(e.Key==Key.F6){CycleWorkspaceFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));e.Handled=true;return;}
        if(mcpOperationActive&&e.Key==Key.Escape){operation?.Cancel();e.Handled=true;return;}
        if(mcpPreviewActive&&e.Key==Key.Escape){e.Handled=true;await CompleteRemotePreview(false);return;}
        if(e.Key==Key.Escape) CancelPendingPick();
        if(materialsActive)
        {
            if(e.Key==Key.Escape){CancelMaterials();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(extrudeDownActive)
        {
            if(e.Key==Key.Escape){CancelExtrudeDown();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(separateActive)
        {
            if(e.Key==Key.Escape){CancelSeparate();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(smoothActive)
        {
            if(e.Key==Key.Escape){CancelSmooth();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(simplifyActive)
        {
            if(e.Key==Key.Escape){CancelSimplify();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(hollowActive)
        {
            if(e.Key==Key.Escape){CancelHollow();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(imageReliefActive)
        {
            if(e.Key==Key.Escape){CancelImageRelief();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(textToolMode!=null)
        {
            if(e.Key==Key.Escape){CancelTextTool();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(InspectionActive)
        {
            if(e.Key==Key.Escape){EndInspection();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(primitiveActive)
        {
            if(e.Key==Key.Escape){CancelPrimitive();e.Handled=true;}
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox){FitScene();e.Handled=true;}
            return;
        }
        if(arrangementMode != null)
        {
            if(e.Key==Key.Escape) { CancelArrangement(); e.Handled=true; }
            else if(e.Key==Key.Enter && Keyboard.FocusedElement is not Control && e.OriginalSource is not Control && ApplyArrangementButton.IsEnabled)
            { ApplyArrangementClick(this,e); e.Handled=true; }
            else if(e.Key==Key.F && Keyboard.FocusedElement is not TextBox)
            { if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FitSelection(); else FitScene(); e.Handled=true; }
            else if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                (e.Key is Key.O or Key.I or Key.S or Key.D || Keyboard.FocusedElement is not TextBox && e.Key is Key.Z or Key.Y or Key.A)) e.Handled=true;
            return;
        }
        if(handleMode != null)
        {
            if(e.Key == Key.Escape) { CancelTransform(); e.Handled=true; }
            else if(e.Key == Key.Enter && Keyboard.FocusedElement is not Control && e.OriginalSource is not Control && ApplyTransformButton.IsEnabled)
            { ApplyTransformClick(this,e); e.Handled=true; }
            else if(e.Key == Key.F && Keyboard.FocusedElement is not TextBox && !transformUpdating && handleDrag == null)
            { if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FitSelection(); else FitScene(); e.Handled=true; }
            else if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                (e.Key is Key.O or Key.I or Key.S or Key.D || (Keyboard.FocusedElement is not TextBox && e.Key is Key.Z or Key.Y))) e.Handled=true;
            return;
        }
        if(operation != null) { if(e.Key == Key.Escape) { operation.Cancel(); e.Handled=true; } return; }
        if(constructionMode != null)
        {
            if(e.Key == Key.Escape) { EndConstruction(false); e.Handled=true; }
            else if(e.Key == Key.F && Keyboard.FocusedElement is not TextBox) { if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FitSelection(); else FitScene(); e.Handled=true; }
            else if(BlockConstructionShortcut(e.Key,Keyboard.Modifiers,Keyboard.FocusedElement,e.OriginalSource as IInputElement)) e.Handled=true;
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var textEditing = Keyboard.FocusedElement is TextBox;
        if(ctrl && e.Key == Key.N) {e.Handled=true;await NavigateDocument(DocumentAction.New);}
        else if(ctrl && e.Key == Key.A && !textEditing) { e.Handled=true; SelectAllClick(this,e); }
        else if(ctrl && e.Key == Key.O) { e.Handled=true; OpenClick(this,e); }
        else if(ctrl && e.Key == Key.I) { e.Handled=true; InsertClick(this,e); }
        else if(ctrl && e.Key == Key.S) { e.Handled=true; await Save(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
        else if(ctrl && e.Key == Key.Z && !textEditing) { e.Handled=true; if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) RedoClick(this,e); else UndoClick(this,e); }
        else if(ctrl && e.Key == Key.Y && !textEditing) { e.Handled=true; RedoClick(this,e); }
        else if(ctrl && e.Key == Key.D && !textEditing) { e.Handled=true; DuplicateClick(this,e); }
        else if(e.Key == Key.Delete && !textEditing) { e.Handled=true; DeleteClick(this,e); }
        else if(e.Key == Key.F && !textEditing) { e.Handled=true; if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FitSelection(); else FitScene(); }
        else if(e.Key == Key.Escape) { e.Handled=true; Refresh(false); ViewportSurface.Focus(); Message("Uncommitted field changes discarded."); }
    }

    private void RenderScene()
    {
        RefreshViewportModels(); UpdateCamera();
    }
    private static void AddGrid(Model3DGroup group,Scene scene)
    {
        var size = scene.Objects.IsEmpty ? 80 : Math.Max(scene.Bounds.Size.X,scene.Bounds.Size.Y)*1.6;
        size = Math.Max(size,40); var step = Math.Pow(10,Math.Floor(Math.Log10(size/10)));
        var center = scene.Objects.IsEmpty ? new Vec3(10,10,0) : scene.Bounds.Center;
        var extent = Math.Ceiling(size/step)*step; var width = step*0.013;
        var mesh = new MeshGeometry3D();
        void Quad(double x1,double y1,double x2,double y2)
        {
            var start=mesh.Positions.Count;
            mesh.Positions.Add(new(x1,y1,-0.02));mesh.Positions.Add(new(x2,y1,-0.02));mesh.Positions.Add(new(x2,y2,-0.02));mesh.Positions.Add(new(x1,y2,-0.02));
            foreach(var n in new[]{0,1,2,0,2,3}) mesh.TriangleIndices.Add(start+n);
        }
        var cx = Math.Round(center.X/step)*step; var cy = Math.Round(center.Y/step)*step;
        for(double i=-extent;i<=extent;i+=step) { Quad(cx+i-width,cy-extent,cx+i+width,cy+extent); Quad(cx-extent,cy+i-width,cx+extent,cy+i+width); }
        mesh.Freeze(); var material = new DiffuseMaterial(new SolidColorBrush(SystemParameters.HighContrast?SystemColors.WindowTextColor:Color.FromRgb(201,213,222)));
        group.Children.Add(new GeometryModel3D(mesh,material) { BackMaterial=material });
    }
    private void FitClick(object sender,RoutedEventArgs e) => FitScene();
    private void FitSelectionClick(object sender,RoutedEventArgs e) => FitSelection();
    private void ViewIsometricClick(object sender,RoutedEventArgs e) => SetView(-Math.PI/4,Math.Asin(1/Math.Sqrt(3)),false);
    private void ViewTopClick(object sender,RoutedEventArgs e) => SetView(-Math.PI/2,Math.PI/2,true);
    private void ViewFrontClick(object sender,RoutedEventArgs e) => SetView(-Math.PI/2,0,true);
    private void ViewRightClick(object sender,RoutedEventArgs e) => SetView(0,0,true);
    private void SetView(double newYaw,double newPitch,bool orthographic)
    {
        if(handleDrag!=null) return;
        yaw=newYaw; pitch=newPitch; useOrthographic=orthographic; FitScene();
        ViewportSurface.Focus();
    }
    private Scene VisibleScene => DisplayedScene(editor.Snapshot);
    private Scene DisplayedScene(EditorSnapshot snapshot)
    {
        var original=snapshot.Preview?.Repair!=null && ShowOriginal.IsChecked==true ||
            simplifyActive && snapshot.Preview is {Request:SimplifyRequest} preview && preview.Id==ownedSimplifyPreview && SimplifyShowOriginal.IsChecked==true ||
            smoothActive && snapshot.Preview is {Request:SmoothRequest} smoothPreview &&
            smoothPreview.Id==ownedSmoothPreview && SmoothShowOriginal.IsChecked==true ||
            CurrentSeparatePreview(snapshot)!=null && SeparateShowOriginal.IsChecked==true ||
            extrudeDownActive && snapshot.Preview is {Request:ExtrudeDownRequest} extrudePreview && extrudePreview.Id==ownedExtrudeDownPreview && ExtrudeDownShowOriginal.IsChecked==true ||
            materialsActive && snapshot.Preview is {Request:MaterialRequest or SurfaceMaterialRequest or SurfaceBrushRequest} materialPreview && materialPreview.Id==ownedMaterialsPreview && MaterialShowOriginal.IsChecked==true;
        return original ? snapshot.Scene : snapshot.Preview?.Scene ?? snapshot.Scene;
    }
    private void FitScene()
    {
        if(handleDrag!=null) return;
        var scene=VisibleScene;
        FitBounds(scene.Objects.IsEmpty ? new Bounds3(new(-10,-10,0),new(30,30,40)) : scene.Bounds);
    }
    private void FitSelection()
    {
        if(handleDrag!=null) return;
        var snapshot=editor.Snapshot;
        var ids=snapshot.Preview?.SelectedObjectIds ?? snapshot.SelectedObjectIds;
        if(ids.IsEmpty) FitScene(); else FitBounds(SelectionBounds(VisibleScene,ids));
    }
    private void FitBounds(Bounds3 bounds)
    {
        var center=bounds.Center; focus=new(center.X,center.Y,center.Z);
        var s=bounds.Size; var radius=Math.Sqrt(s.X*s.X+s.Y*s.Y+s.Z*s.Z)*0.5;
        // WPF's field of view is horizontal. Fit the bounding sphere to the narrower
        // horizontal/vertical aperture, including tall windows, with a modest margin.
        var aspect=Math.Max(0.01,Viewport.ActualWidth/Math.Max(1,Viewport.ActualHeight));
        var halfHorizontal=camera.FieldOfView*Math.PI/360;
        var halfVertical=Math.Atan(Math.Tan(halfHorizontal)/aspect);
        distance=Math.Max(0.001,radius/Math.Sin(Math.Min(halfHorizontal,halfVertical))*1.12);
        UpdateCamera();
    }
    private void UpdateCamera()
    {
        CancelBrushInput();BrushCursorOverlay.Children.Clear();cameraGeneration++; CancelPendingPick();
        var direction=new Vector3D(Math.Cos(pitch)*Math.Cos(yaw),Math.Cos(pitch)*Math.Sin(yaw),Math.Sin(pitch));
        camera.Position=focus+direction*distance; camera.LookDirection=focus-camera.Position;
        camera.UpDirection=Math.Abs(Math.Cos(pitch))<1e-8 ? new(0,1,0) : new(0,0,1);
        camera.NearPlaneDistance=Math.Max(0.0001,distance/10000); camera.FarPlaneDistance=Math.Max(1000,distance*1000);
        orthographicCamera.Position=camera.Position; orthographicCamera.LookDirection=camera.LookDirection;
        orthographicCamera.UpDirection=camera.UpDirection; orthographicCamera.NearPlaneDistance=camera.NearPlaneDistance;
        orthographicCamera.FarPlaneDistance=camera.FarPlaneDistance;
        orthographicCamera.Width=2*distance*Math.Tan(camera.FieldOfView*Math.PI/360);
        Viewport.Camera=useOrthographic ? orthographicCamera : camera;
        DrawTransformHandles();
        DrawMeasurement();
        DrawMaterialSelection();
    }
    private async void ViewportDown(object sender,MouseButtonEventArgs e)
    {
        if(brushGesture!=null){e.Handled=true;return;}
        CancelPendingPick();
        ViewportSurface.Focus(); lastMouse=e.GetPosition(ViewportSurface);
        if(e.ChangedButton==MouseButton.Left&&inspectionMode==InspectionMode.Measure)
        {e.Handled=true;await PickMeasurementAt(e.GetPosition(Viewport));return;}
        if(BrushActive&&operation!=null){e.Handled=true;return;}
        if(e.ChangedButton==MouseButton.Left&&BrushActive)
        {e.Handled=true;if(BeginBrushStroke(e.GetPosition(Viewport)))ViewportSurface.CaptureMouse();return;}
        if(e.ChangedButton==MouseButton.Left&&materialsActive&&paintMode==PaintMode.Surface)
        {e.Handled=true;await PickSurfaceMaterialAt(e.GetPosition(Viewport));return;}
        if(e.ChangedButton == MouseButton.Left && BeginHandleDrag(e.GetPosition(Viewport)))
        { ViewportSurface.CaptureMouse(); e.Handled=true; return; }
        if(transformUpdating || handleDrag != null) return;
        if(e.ChangedButton is MouseButton.Right or MouseButton.Middle) { drag=e.ChangedButton; ViewportSurface.CaptureMouse(); e.Handled=true; }
        else if(e.ChangedButton == MouseButton.Left && !ModalToolActive && constructionMode == null && handleMode == null && arrangementMode == null && operation == null)
        {
            var point=e.GetPosition(Viewport); var modifiers=Keyboard.Modifiers;
            e.Handled=true; await SelectViewportAt(point,modifiers);
        }
    }
    private void ViewportMove(object sender,MouseEventArgs e)
    {
        if(brushGesture!=null){ExtendBrushStroke(e.GetPosition(Viewport));e.Handled=true;return;}
        if(BrushActive)DrawBrushCursor(e.GetPosition(Viewport));
        if(handleDrag != null) { UpdateHandleDrag(e.GetPosition(Viewport)); e.Handled=true; return; }
        if(drag == null) return;
        var point=e.GetPosition(ViewportSurface); var delta=point-lastMouse; lastMouse=point;
        if(drag == MouseButton.Right) { useOrthographic=false; yaw-=delta.X*0.008; pitch=Math.Clamp(pitch+delta.Y*0.008,-1.48,1.48); }
        else
        {
            var look=camera.LookDirection; look.Normalize(); var right=Vector3D.CrossProduct(look,camera.UpDirection); right.Normalize();
            var up=Vector3D.CrossProduct(right,look); up.Normalize();
            focus+=(-right*delta.X+up*delta.Y)*(distance/Math.Max(1,Viewport.ActualWidth)*0.8);
        }
        UpdateCamera();
    }
    private async void ViewportUp(object sender,MouseButtonEventArgs e)
    {
        if(brushGesture!=null&&e.ChangedButton==MouseButton.Left){e.Handled=true;await EndBrushStroke(e.GetPosition(Viewport));return;}
        if(handleDrag != null && e.ChangedButton == MouseButton.Left) { UpdateHandleDrag(e.GetPosition(Viewport)); EndHandleDrag(); ViewportSurface.ReleaseMouseCapture(); e.Handled=true; }
        if(drag != null) { drag=null; ViewportSurface.ReleaseMouseCapture(); }
    }
    private void ViewportLostCapture(object sender,MouseEventArgs e)
    { CancelBrushCapture();drag=null; if(handleDrag != null) CancelTransform(); }
    private void ViewportWheel(object sender,MouseWheelEventArgs e)
    { if(brushGesture!=null||BrushActive&&operation!=null){e.Handled=true;return;}if(handleDrag != null || transformUpdating) { e.Handled=true; return; } distance=Math.Clamp(distance*Math.Pow(0.85,e.Delta/120d),0.001,100_000_000); UpdateCamera(); e.Handled=true; }
}
