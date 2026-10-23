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
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

// Exercises real WPF controls in an isolated application instance. No desktop input,
// file-dialog automation, production test hooks, or access to an existing user scene.
internal static partial class Program
{
    private static MainWindow window = null!;
    private static EditorSession editor = null!;
    private static FrameworkElement surface = null!;
    private static string root = "", output = "";
    private static readonly List<object> observations = [];
    private static readonly List<string> passed = [];
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        root = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith("--",StringComparison.Ordinal) ? args[0] : "../..");
        var focusedModes=new HashSet<string>(StringComparer.Ordinal){"--normal-workflow-only","--mcp-only","--v1-usability-only","--surface-brush-only","--radial-array-only","--center-on-plate-only","--rectangular-array-only","--distribution-only","--view-preferences-only","--reflections-only","--shadows-only","--shading-only","--view-controls-only","--recovery-only","--gltf-only","--ply-only","--obj-only","--model-metadata-only","--document-navigation-only","--surface-paint-only","--image-edge-stamp-only","--image-contour-only","--materials-only","--extrude-down-only","--workspace-tabs-only","--separate-only","--smooth-only","--simplify-only","--hollow-only","--baseline-controls-only","--image-relief-only","--text-emboss-only","--mirror-measurement-only","--primitives-only","--editing-controls-only","--expanded-groups-only","--expanded-limits-only","--input-policy-only"};
        if(args.Any(a=>a.StartsWith("--",StringComparison.Ordinal)&&a!="--evidence-root"&&!focusedModes.Contains(a))||args.Count(focusedModes.Contains)>1)
        {Console.Error.WriteLine("Choose one known focused check option; unknown options cannot run the default suite.");return 2;}
        var normalWorkflowOnly=args.Contains("--normal-workflow-only");
        var mcpOnly=args.Contains("--mcp-only");
        var v1UsabilityOnly=args.Contains("--v1-usability-only");
        var surfaceBrushOnly=args.Contains("--surface-brush-only");
        var radialArrayOnly=args.Contains("--radial-array-only");
        var centerOnPlateOnly=args.Contains("--center-on-plate-only");
        var rectangularArrayOnly=args.Contains("--rectangular-array-only");
        var distributionOnly=args.Contains("--distribution-only");
        var viewPreferencesOnly=args.Contains("--view-preferences-only");
        var reflectionsOnly=args.Contains("--reflections-only");
        var shadowsOnly=args.Contains("--shadows-only");
        var shadingOnly=args.Contains("--shading-only");
        var viewControlsOnly=args.Contains("--view-controls-only");
        var recoveryOnly=args.Contains("--recovery-only");
        var gltfOnly=args.Contains("--gltf-only");
        var plyOnly=args.Contains("--ply-only");
        var objOnly=args.Contains("--obj-only");
        var modelMetadataOnly=args.Contains("--model-metadata-only");
        var documentNavigationOnly=args.Contains("--document-navigation-only");
        var surfacePaintOnly=args.Contains("--surface-paint-only");
        var imageEdgeStampOnly=args.Contains("--image-edge-stamp-only");
        var imageContourOnly=args.Contains("--image-contour-only");
        var materialsOnly=args.Contains("--materials-only");
        var extrudeDownOnly=args.Contains("--extrude-down-only");
        var workspaceTabsOnly=args.Contains("--workspace-tabs-only");
        var separateOnly=args.Contains("--separate-only");
        var smoothOnly=args.Contains("--smooth-only");
        var simplifyOnly=args.Contains("--simplify-only");
        var hollowOnly=args.Contains("--hollow-only");
        var baselineOnly=args.Contains("--baseline-controls-only");
        var imageReliefOnly=args.Contains("--image-relief-only");
        var textEmbossOnly=args.Contains("--text-emboss-only");
        var mirrorMeasurementOnly=args.Contains("--mirror-measurement-only");
        var primitiveOnly=args.Contains("--primitives-only");
        var editingOnly=args.Contains("--editing-controls-only");
        var expandedOnly=args.Contains("--expanded-groups-only");
        var expandedLimits=args.Contains("--expanded-limits-only");
        var inputPolicyOnly=args.Contains("--input-policy-only");
        // Existing callers keep their historical destination; new slices can isolate every artifact.
        var evidenceRoot=Path.Combine(root,".local","evidence",mcpOnly?"mcp-20261022/desktop":v1UsabilityOnly?"v1-usability-20261022/desktop":surfaceBrushOnly?"surface-brush-20261022/desktop":radialArrayOnly?"radial-array-20261022/desktop":centerOnPlateOnly?"center-on-plate-20261022/desktop":rectangularArrayOnly?"rectangular-array-20261022/desktop":distributionOnly?"distribution-20261022/desktop":viewPreferencesOnly?"view-preferences-20261022/desktop":reflectionsOnly?"reflections-20261021/desktop":shadowsOnly?"shadows-20261021/desktop":shadingOnly?"shading-20261021/desktop":viewControlsOnly?"view-controls-20261021/desktop":recoveryOnly?"recovery-20261021/desktop":gltfOnly?"gltf-20261021/desktop":plyOnly?"ply-20261021/desktop":objOnly?"obj-20261021/desktop":modelMetadataOnly?"model-metadata-20261021/desktop":documentNavigationOnly?"document-navigation-20261021/desktop":surfacePaintOnly?"surface-paint-20261021/desktop":imageEdgeStampOnly?"image-edge-stamp-20261021/desktop":imageContourOnly?"image-contour-20261021":materialsOnly?"paint-materials-20261021":extrudeDownOnly?"extrude-down-20261020":workspaceTabsOnly?"workspace-tabs-20261020":"expanded-groups-20261019");
        var evidenceOptions=args.Select((value,index)=>(value,index)).Where(p=>p.value=="--evidence-root").ToArray();
        if(evidenceOptions.Length>1 || evidenceOptions.Length==1 &&
            (evidenceOptions[0].index+1>=args.Length || args[evidenceOptions[0].index+1].StartsWith("--",StringComparison.Ordinal)))
        {Console.Error.WriteLine("Use --evidence-root once, followed by an output directory.");return 2;}
        if(evidenceOptions.Length==1) evidenceRoot=Path.GetFullPath(args[evidenceOptions[0].index+1],root);
        output = Path.Combine(evidenceRoot,normalWorkflowOnly ? "desktop-normal-workflow" : mcpOnly ? "desktop-mcp" : v1UsabilityOnly ? "desktop-v1-usability" : surfaceBrushOnly ? "desktop-surface-brush" : radialArrayOnly ? "desktop-radial-array" : centerOnPlateOnly ? "desktop-center-on-plate" : rectangularArrayOnly ? "desktop-rectangular-array" : distributionOnly ? "desktop-distribution" : viewPreferencesOnly ? "desktop-view-preferences" : reflectionsOnly ? "desktop-reflections" : shadowsOnly ? "desktop-shadows" : shadingOnly ? "desktop-shading" : viewControlsOnly ? "desktop-view-controls" : recoveryOnly ? "desktop-recovery" : gltfOnly ? "desktop-gltf" : plyOnly ? "desktop-ply" : objOnly ? "desktop-obj" : modelMetadataOnly ? "desktop-model-metadata" : documentNavigationOnly ? "desktop-document-navigation" : surfacePaintOnly ? "desktop-surface-paint" : imageEdgeStampOnly ? "desktop-image-edge-stamp" : imageContourOnly ? "desktop-image-contour" : materialsOnly ? "desktop-materials" : extrudeDownOnly ? "desktop-extrude-down" : workspaceTabsOnly ? "desktop-workspace-tabs" : separateOnly ? "desktop-separate" : smoothOnly ? "desktop-smooth" : simplifyOnly ? "desktop-simplify" : hollowOnly ? "desktop-hollow" : baselineOnly ? "desktop-baseline" : imageReliefOnly ? "desktop-image-relief" : textEmbossOnly ? "desktop-text-emboss" : mirrorMeasurementOnly ? "desktop-mirror-measurement" : primitiveOnly ? "desktop-primitives" : editingOnly ? "desktop-editing" : inputPolicyOnly ? "desktop-input-policy" : expandedLimits ? "desktop-limits" : expandedOnly ? "desktop-expanded" : "desktop-checks");
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/Builder;component/Theme.xaml",UriKind.Relative)});
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_,_) => {
            var exit = 0;
            try { if(normalWorkflowOnly) { await InitializeExpandedGroupHarness(); await RunNormalWorkflowControls(); } else if(mcpOnly) { await InitializeExpandedGroupHarness(); await RunMcpControls(); } else if(v1UsabilityOnly) { await InitializeExpandedGroupHarness(); await RunV1UsabilityControls(); await RunStlImportControls(); } else if(surfaceBrushOnly) { await InitializeExpandedGroupHarness(); await RunSurfaceBrushControls(); await RunSurfacePaintControls(); } else if(radialArrayOnly) { await InitializeExpandedGroupHarness(); await RunRadialArrayControls(); await RunRectangularArrayControls(); } else if(centerOnPlateOnly) { await InitializeExpandedGroupHarness(); await RunCenterOnPlateControls(); await RunRectangularArrayControls(); } else if(rectangularArrayOnly) { await InitializeExpandedGroupHarness(); await RunRectangularArrayControls(); await RunDistributionControls(); } else if(distributionOnly) { await InitializeExpandedGroupHarness(); await RunDistributionControls(); await RunArrangementControls(); } else if(viewPreferencesOnly) { await InitializeExpandedGroupHarness(); await RunViewPreferenceControls(); } else if(reflectionsOnly) { await InitializeExpandedGroupHarness(); await RunReflectionControls(); } else if(shadowsOnly) { await InitializeExpandedGroupHarness(); await RunShadowControls(); } else if(shadingOnly) { await InitializeExpandedGroupHarness(); await RunShadingControls(); } else if(viewControlsOnly) { await InitializeExpandedGroupHarness(); await RunViewControls(); } else if(recoveryOnly) { await InitializeExpandedGroupHarness(); await RunRecoveryControls(); } else if(gltfOnly) { await InitializeExpandedGroupHarness(); await RunGltfControls(); } else if(plyOnly) { await InitializeExpandedGroupHarness(); await RunPlyControls(); } else if(objOnly) { await InitializeExpandedGroupHarness(); await RunObjControls(); } else if(modelMetadataOnly) { await InitializeExpandedGroupHarness(); await RunModelMetadataControls(); } else if(documentNavigationOnly) { await InitializeExpandedGroupHarness(); await RunDocumentNavigationControls(); } else if(surfacePaintOnly) { await InitializeExpandedGroupHarness(); await RunSurfacePaintControls(); } else if(imageEdgeStampOnly) { await InitializeExpandedGroupHarness(); await RunImageEdgeStampControls(); } else if(imageContourOnly) { await InitializeExpandedGroupHarness(); await RunImageContourControls(); } else if(materialsOnly) { await InitializeExpandedGroupHarness(); await RunMaterialControls(); } else if(extrudeDownOnly) { await InitializeExpandedGroupHarness(); await RunExtrudeDownControls(); } else if(workspaceTabsOnly) { await InitializeExpandedGroupHarness(); await RunWorkspaceTabs(); } else if(separateOnly) { await InitializeExpandedGroupHarness(); await RunSeparateControls(); } else if(smoothOnly) { await InitializeExpandedGroupHarness(); await RunSmoothControls(); } else if(simplifyOnly) { await InitializeExpandedGroupHarness(); await RunSimplifyControls(); } else if(hollowOnly) { await InitializeExpandedGroupHarness(); await RunHollowControls(); } else if(baselineOnly) { await Run(); } else if(imageReliefOnly) { await InitializeExpandedGroupHarness(); await RunImageReliefControls(); } else if(textEmbossOnly) { await InitializeExpandedGroupHarness(); await RunTextEmbossControls(); } else if(mirrorMeasurementOnly) { await InitializeExpandedGroupHarness(); await RunMirrorMeasurementControls(); } else if(primitiveOnly) { await InitializeExpandedGroupHarness(); await RunPrimitiveControls(); } else if(editingOnly) { await InitializeExpandedGroupHarness(); await TransformControls(); await RunInputPolicyControls(); await RunLazyTransformFieldControls(); } else if(inputPolicyOnly) { await InitializeExpandedGroupHarness(); await RunInputPolicyControls(); await RunLazyTransformFieldControls(); } else if(expandedLimits) await RunExpandedGroupLimitControls(); else if(expandedOnly) await RunExpandedGroupControls(); else { await Run(); await TransformControls(); await RunArrangementControls(); await MultiTransformControls(); await RunObjectTreeControls(); await RunMemberControls(); await RunPerformanceControls(); await RunExpandedGroupControls(); await RunInputPolicyControls(); await RunLazyTransformFieldControls(); await RunPrimitiveControls(); await RunMirrorMeasurementControls(); await RunTextEmbossControls(); await RunImageReliefControls(); await RunHollowControls(); await RunSimplifyControls(); await RunSmoothControls(); await RunSeparateControls(); await RunExtrudeDownControls(); await RunMaterialControls(); await RunImageContourControls(); await RunImageEdgeStampControls(); await RunSurfacePaintControls(); await RunDocumentNavigationControls(); await RunModelMetadataControls(); await RunObjControls(); await RunPlyControls(); await RunGltfControls(); await RunRecoveryControls(); await RunViewControls(); await RunShadingControls(); await RunShadowControls(); await RunReflectionControls(); await RunViewPreferenceControls(); await RunDistributionControls(); await RunRectangularArrayControls(); await RunCenterOnPlateControls(); await RunRadialArrayControls(); await RunSurfaceBrushControls(); await RunV1UsabilityControls(); } }
            catch(Exception ex) { Console.Error.WriteLine(ex); observations.Add(new { failure=ex.ToString() }); exit=1; }
            finally
            {
                File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {
                    utc=DateTime.UtcNow, framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,evidenceRoot,outputDirectory=output,
                    method="Isolated WPF layout, routed control events and software rendering; no live mouse/keyboard or OS file dialogs",
                    passed, observations, artifacts=Directory.GetFiles(output).Where(p=>!p.EndsWith("results.json",StringComparison.Ordinal))
                        .Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)}).ToArray(),
                    desktopSha256=Hash(typeof(MainWindow).Assembly.Location),coreSha256=Hash(typeof(EditorSession).Assembly.Location),
                    limitations=new[]{"Does not verify OS dialogs, desktop focus, mouse hit testing, keyboard gestures, DPI changes or the original Microsoft application."}
                },new JsonSerializerOptions {WriteIndented=true}));
                if(window != null) { typeof(MainWindow).GetField("closeApproved",PrivateInstance)!.SetValue(window,true);typeof(MainWindow).GetField("closeApprovedRevision",PrivateInstance)!.SetValue(window,editor.Snapshot.Revision); }
                app.Shutdown(exit);
            }
        };
        return app.Run();
    }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Assert(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private static void Near(double actual,double expected,double tolerance=1e-7)=>Assert(Math.Abs(actual-expected)<tolerance,$"Expected {expected}, got {actual}.");
    private static void Ok(OperationResult r)=>Assert(r.Succeeded,r.ToString());
    private static void Pass(string name) { passed.Add(name); Console.WriteLine("PASS "+name); }
    private static T Named<T>(string name) where T:class => (T)window.FindName(name);
    private static void Refresh()=>typeof(MainWindow).GetMethod("Refresh",PrivateInstance)!.Invoke(window,[true]);
    private static IEnumerable<DependencyObject> Tree(DependencyObject parent)
    {
        yield return parent;
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach(var item in Tree(child)) yield return item;
    }
    private static Button Button(string text)=>Tree(window).OfType<Button>().Single(b=>b.Content?.ToString()==text);
    private static void Click(Button button)
    {
        Assert(button.IsEnabled,"Disabled button: "+button.Content);
        Assert(button.Visibility==Visibility.Visible,"Hidden button: "+button.Content);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,button));
    }
    private static void Click(string text)=>Click(Button(text));
    private static Button ConstructionCancel()=>Tree(Named<Border>("ConstructionActions")).OfType<Button>().Single(b=>b.Content?.ToString()=="Cancel");
    private static async Task Add(string shape)
    {
        var item=Tree(window).OfType<MenuItem>().Single(m=>m.Header?.ToString()==shape);
        Assert(item.IsEnabled,"Disabled primitive menu");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,item));
        Click(Named<Button>("PreviewPrimitiveButton"));await Idle();Click(Named<Button>("AddPrimitiveButton"));
    }
    private static async Task Idle()
    {
        var watch=Stopwatch.StartNew();
        do
        {
            await Task.Delay(20);
            Assert(watch.Elapsed<TimeSpan.FromSeconds(30),"WPF operation did not finish.");
        } while(editor.Snapshot.IsBusy || typeof(MainWindow).GetField("operation",PrivateInstance)!.GetValue(window)!=null);
        Layout(surface.ActualWidth,surface.ActualHeight);
    }
    private static void Layout(double width,double height)
    {
        surface.Measure(new Size(width,height)); surface.Arrange(new Rect(0,0,width,height)); surface.UpdateLayout();
    }
    private static void Capture(string name,double width=1280,double height=780)
    {
        Layout(width,height);
        var bitmap=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(output,name+".png")); png.Save(file);
        observations.Add(new {image=name+".png",width,height,objects=editor.Snapshot.Scene.Objects.Length,
            preview=editor.Snapshot.Preview!=null,status=Named<TextBlock>("Status").Text});
    }
    private static Rect Bounds(FrameworkElement element)=>element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
    private static void FooterVisible()
    {
        Layout(1040,680); // Client area smaller than the 1040 x 720 minimum outer window.
        foreach(var name in new[]{"PreviewButton","ApplyPreviewButton","ConstructionActions"})
        {
            var element=Named<FrameworkElement>(name); var b=Bounds(element);
            Assert(element.Visibility==Visibility.Visible && b.Width>0 && b.Height>0,"Missing footer: "+name);
            Assert(b.Left>=0 && b.Top>=0 && b.Right<=1040.1 && b.Bottom<=680.1,"Clipped footer: "+name+" "+b);
            observations.Add(new {control=name,client="1040 x 680",bounds=b.ToString()});
        }
        var cancel=Bounds(ConstructionCancel());
        Assert(cancel.Bottom<=680 && cancel.Top>=0 && cancel.Right<=1040,"Cancel is clipped.");
    }
    private static void SameAccepted(EditorSnapshot before)
    {
        var after=editor.Snapshot;
        Assert(after.Scene==before.Scene && after.SelectedObjectId==before.SelectedObjectId && after.FilePath==before.FilePath &&
            after.IsDirty==before.IsDirty && after.CanUndo==before.CanUndo && after.CanRedo==before.CanRedo && after.Revision==before.Revision && after.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds) && after.EditingGroupId==before.EditingGroupId,
            "Accepted document changed before Apply.");
    }
    private static void CameraContains(Bounds3 bounds)
    {
        var viewport=Named<Viewport3D>("Viewport"); var camera=(ProjectionCamera)viewport.Camera;
        var forward=camera.LookDirection; forward.Normalize(); var right=Vector3D.CrossProduct(forward,camera.UpDirection); right.Normalize();
        var up=Vector3D.CrossProduct(right,forward); up.Normalize();
        foreach(var x in new[]{bounds.Min.X,bounds.Max.X}) foreach(var y in new[]{bounds.Min.Y,bounds.Max.Y}) foreach(var z in new[]{bounds.Min.Z,bounds.Max.Z})
        {
            var delta=new Point3D(x,y,z)-camera.Position;
            var depth=Vector3D.DotProduct(delta,forward);
            var halfWidth=camera is OrthographicCamera oc ? oc.Width/2 : depth*Math.Tan(((PerspectiveCamera)camera).FieldOfView*Math.PI/360);
            var halfHeight=halfWidth*viewport.ActualHeight/viewport.ActualWidth;
            Assert(depth>camera.NearPlaneDistance && depth<camera.FarPlaneDistance,"Camera clips object depth.");
            Assert(Math.Abs(Vector3D.DotProduct(delta,right))<halfWidth && Math.Abs(Vector3D.DotProduct(delta,up))<halfHeight,"Fit does not contain model.");
        }
        observations.Add(new {camera=camera.GetType().Name,position=camera.Position.ToString(),up=camera.UpDirection.ToString(),viewport=$"{viewport.ActualWidth} x {viewport.ActualHeight}"});
    }
    private static async Task Open(string file)
    {
        Ok(await editor.OpenAsync(Path.Combine(root,"fixtures","generated",file),decision:DirtyDecision.Discard));Refresh();Layout(1280,780);
        if(window.FindName("WorkspaceTabs") is TabControl){await Workspace(4);Click(Named<Button>("FitSceneButton"));await Workspace(1);}
        else Click("Fit scene");
    }
    private static async Task Run()
    {
        window=CreateCheckWindow();
        editor=(EditorSession)typeof(MainWindow).GetField("editor",PrivateInstance)!.GetValue(window)!;
        surface=(FrameworkElement)window.Content;
        Refresh(); Capture("01-empty");
        Assert(!Named<Button>("SaveButton").IsEnabled && !Named<Button>("UndoButton").IsEnabled,"Empty scene buttons enabled.");
        Pass("Empty scene layout and command availability");

        await Add("Cube"); Layout(1280,780); Click("Fit scene");
        var cube=editor.Snapshot.Scene.Objects.Single();
        var beforeSize=editor.Snapshot;
        Named<TextBox>("SizeX").Text="30"; Click("Preview size"); await Idle(); SameAccepted(beforeSize);
        Click(Named<Button>("ApplyTransformButton")); Near(editor.Snapshot.Scene.Bounds.Size.X,30);
        Click(Named<Button>("UndoButton")); Near(editor.Snapshot.Scene.Bounds.Size.X,20);
        Click(Named<Button>("RedoButton")); Near(editor.Snapshot.Scene.Bounds.Size.X,30);
        Named<TextBox>("ObjectName").Text="Test bracket"; Click("Rename");
        Assert(editor.Snapshot.Scene.Objects[0].Name=="Test bracket","Rename not applied.");
        Named<TabControl>("TransformTabs").SelectedIndex=2; Layout(1280,780);
        var beforeRotation=editor.Snapshot;
        Named<TextBox>("RotationZ").Text="90"; Click("Preview rotation"); await Idle(); SameAccepted(beforeRotation);
        Click(Named<Button>("ApplyTransformButton"));
        Near(editor.Snapshot.Scene.Bounds.Size.X,20); Near(editor.Snapshot.Scene.Bounds.Size.Y,30);
        Assert(editor.Snapshot.Scene.Objects[0].Id==cube.Id,"Rotation changed identity.");
        var before=editor.Snapshot; Named<TextBox>("RotationX").Text="NaN"; Click("Preview rotation"); await Idle(); SameAccepted(before);
        Assert(Named<TextBlock>("Status").Text.Contains("finite number"),"Missing invalid rotation message.");
        Click(Named<Button>("CancelTransformButton")); SameAccepted(before);
        Named<TextBox>("RotationX").Text="0"; Click("Isometric"); Capture("02-rotate");
        Pass("Actual size, rename, rotate, invalid input and undo/redo controls");

        before=editor.Snapshot;
        foreach(var size in new[]{new Size(1040,680),new Size(1600,720),new Size(1040,1000)})
        {
            Layout(size.Width,size.Height);
            foreach(var view in new[]{"Top","Front","Right","Isometric"}) { Click(view); CameraContains(editor.Snapshot.Scene.Bounds); SameAccepted(before); }
        }
        await Add("Cylinder"); await Add("Sphere"); Assert(editor.Snapshot.Scene.Objects.Length==3,"Primitive menu lost objects.");
        var selected=editor.Snapshot.Scene.Objects.Last(); Ok(editor.Execute(new MoveObject(selected.Id,new(80,10,10)))); Refresh();
        Layout(1280,780); Click("Fit selection"); CameraContains(editor.Snapshot.Scene.Objects.Last().Bounds);
        var fitted=(ProjectionCamera)Named<Viewport3D>("Viewport").Camera;
        var target=fitted.Position+fitted.LookDirection;
        Near(target.X,80); Near(target.Y,10); Near(target.Z,10);
        Click("Isometric"); Capture("03-shapes");
        Pass("Primitive menus and camera presets frame models without document mutations");

        await Open("missing-top.stl"); before=editor.Snapshot;
        Click(Named<Button>("RepairButton")); await Idle();
        Assert(Named<TextBlock>("RepairReport").Text.Contains("Open edges: 4"),"Missing diagnosis in UI."); SameAccepted(before);
        FooterVisible(); Click(Named<Button>("PreviewButton")); await Idle();
        Assert(editor.Snapshot.Preview?.Repair!=null,"No repair preview: "+Named<TextBlock>("Status").Text); SameAccepted(before);
        Named<CheckBox>("ShowOriginal").IsChecked=true; SameAccepted(before); Capture("04-repair-original-minimum",1040,680); FooterVisible();
        Named<CheckBox>("ShowOriginal").IsChecked=false; Capture("05-repair-preview-minimum",1040,680);
        Click(ConstructionCancel()); SameAccepted(before); Assert(editor.Snapshot.Preview==null,"Cancel retained preview.");
        Click(Named<Button>("RepairButton")); await Idle(); Click(Named<Button>("PreviewButton")); await Idle();
        Click(Named<Button>("ApplyPreviewButton")); Assert(editor.Snapshot.Scene.Objects[0].Mesh.Triangles.Length==12,"Repair apply failed.");
        Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"Repair undo failed.");
        Click(Named<Button>("RedoButton")); Ok(await editor.SaveAsync(Path.Combine(output,"ui-repaired.3mf")));
        Capture("06-repaired");
        Pass("Repair diagnosis, original/preview toggle, pinned footer, cancel, apply and undo/redo controls");

        await Open("cube-20mm.stl"); before=editor.Snapshot;
        Click(Named<Button>("SplitButton")); FooterVisible(); Click(Named<Button>("PreviewButton")); await Idle();
        SameAccepted(before); Assert(editor.Snapshot.Preview?.Scene.Objects.Length==2,"Split preview did not create halves.");
        Capture("07-split-minimum",1040,680); Click(ConstructionCancel()); SameAccepted(before);
        Click(Named<Button>("SplitButton")); Click(Named<Button>("PreviewButton")); await Idle(); Click(Named<Button>("ApplyPreviewButton"));
        Assert(editor.Snapshot.Scene.Objects.Length==2,"Split apply failed.");
        Ok(await editor.SaveAsync(Path.Combine(output,"ui-split.3mf"))); Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"Split undo failed.");
        Pass("Split preview, cancel, apply and undo controls at minimum client size");

        await Open("assembly-mm.3mf");
        Named<ListBox>("ObjectList").SelectedIndex=1; Named<TabControl>("TransformTabs").SelectedIndex=1; Layout(1280,780);
        var beforeMove=editor.Snapshot;
        Named<TextBox>("PositionX").Text="20"; Click("Preview move"); await Idle(); SameAccepted(beforeMove);
        Click(Named<Button>("ApplyTransformButton")); Near(editor.Snapshot.Scene.Objects[1].Bounds.Min.X,10);
        Named<ListBox>("ObjectList").SelectedIndex=0;
        foreach(var kind in new[]{0,1,2})
        {
            before=editor.Snapshot; Click(Named<Button>("CombineButton"));
            Named<ComboBox>("BooleanOperation").SelectedIndex=kind; FooterVisible(); Click(Named<Button>("PreviewButton")); await Idle();
            Assert(editor.Snapshot.Preview!=null,"Boolean preview failed: "+Named<TextBlock>("Status").Text); SameAccepted(before);
            Capture("08-boolean-"+kind,1040,680); Click(Named<Button>("ApplyPreviewButton"));
            if(editor.Snapshot.Scene.Objects.Length>0) Ok(await editor.SaveAsync(Path.Combine(output,$"ui-boolean-{kind}.3mf")));
            Click(Named<Button>("UndoButton")); Assert(editor.Snapshot.Scene==before.Scene,"Boolean undo failed.");
        }
        Pass("Union, subtract and intersect controls, preview acceptance and undo");

        // Exercise rendering after an inserted model without asserting OS file-picker coverage.
        await Open("cube-20mm.stl"); Ok(await editor.SaveAsync(Path.Combine(output,"ui-insert-baseline.3mf")));
        var path=editor.Snapshot.FilePath; Assert(path!=null,"Insert setup has no native destination.");
        Ok(await editor.InsertAsync(Path.Combine(root,"fixtures","generated","assembly-inch.3mf"))); Refresh();
        Assert(editor.Snapshot.FilePath==path && Named<ListBox>("ObjectList").Items.Count==3,"Inserted models not reflected in inspector.");
        Click("Isometric"); Capture("09-inserted");
        Pass("Inserted objects appear in inspector; native destination is preserved");

        await Add("Sphere"); before=editor.Snapshot; Click(Named<Button>("SplitButton"));
        Click(Named<Button>("PreviewButton")); Click(Named<Button>("CancelOperation")); await Idle();
        Assert(Named<TextBlock>("Status").Text.Contains("Cancelled") && editor.Snapshot.Preview==null,"Cancel operation did not discard in-flight preview.");
        SameAccepted(before); Click(ConstructionCancel());
        Pass("Cancel operation button stops an in-flight geometry request and preserves accepted scene");
    }
}
