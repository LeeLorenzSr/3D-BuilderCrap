using Builder.Core;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

internal static partial class Program
{
    private static void SetShading(bool smooth)=>Named<RadioButton>(smooth?"ViewShadingSmooth":"ViewShadingFlat").IsChecked=true;
    private static MeshGeometry3D ShownGeometry(Guid id)=>(MeshGeometry3D)SeparateDrawn().First(p=>p.Value==id).Key.Geometry;
    private static void SameCorners(MeshGeometry3D flat,MeshGeometry3D smooth)
    {
        Assert(smooth.IsFrozen&&ReferenceEquals(flat.Positions,smooth.Positions)&&ReferenceEquals(flat.TriangleIndices,smooth.TriangleIndices),"Shading changed positions/indices or left mutable display geometry.");
        Assert(smooth.Normals.Count==smooth.Positions.Count,"Smooth normals missing corners.");
        foreach(var n in smooth.Normals)Assert(double.IsFinite(n.X)&&double.IsFinite(n.Y)&&double.IsFinite(n.Z)&&Math.Abs(n.Length-1)<1e-8,"Invalid display normal.");
    }
    private static async Task RunShadingControls()
    {
        Ok(await editor.OpenAsync(Path.Combine(root,".local/evidence/shading-20261021/inputs/faceted-sphere.3mf"),decision:DirtyDecision.Discard));Refresh();await Workspace(4);Click(Named<Button>("FitSceneButton"));Click(Named<Button>("ResetDisplayButton"));Layout(1040,680);
        var before=editor.Snapshot;var obj=before.Scene.Objects.Single();var flat=(MeshGeometry3D)Invoke("DisplayMesh",obj.Mesh)!;var camera=CameraPose();
        Assert(editor.Presentation.Snapshot.Settings.Shading==ShadingMode.Flat&&Named<RadioButton>("ViewShadingFlat").IsChecked==true,"Default shading is not Flat.");
        foreach(var name in new[]{"ViewShadingFlat","ViewShadingSmooth","ViewGrid","ViewXRay","ResetDisplayButton"})Reachable(name);
        Assert(Named<System.Windows.FrameworkElement>("ViewportSurface").ActualHeight>=250,"Shading controls consumed viewport.");Capture("shading-sphere-flat-minimum",1040,680);
        SetShading(true);var smooth=ShownGeometry(obj.Id);SameCorners(flat,smooth);
        Assert(smooth.Normals.Where((n,i)=>(n-flat.Normals[i]).Length>1e-6).Any(),"Sphere lighting normals did not change.");
        foreach(var group in smooth.Positions.Select((p,i)=>(p,i)).GroupBy(v=>v.p))Assert(group.Select(v=>smooth.Normals[v.i]).Distinct().Count()==1,"Exact duplicate positions have a display seam.");
        SameAccepted(before);Assert(CameraPose()==camera&&ReferenceEquals(editor.Snapshot.Scene.Objects.Single().Mesh,obj.Mesh),"Shading changed Core mesh or camera.");Capture("shading-sphere-smooth-minimum",1040,680);
        var bounds=obj.Bounds;var hit=await editor.PickAsync(new(bounds.Center.X,bounds.Center.Y,bounds.Max.Z+20),new(0,0,-1));SetShading(false);var hitFlat=await editor.PickAsync(new(bounds.Center.X,bounds.Center.Y,bounds.Max.Z+20),new(0,0,-1));
        Assert(hit?.ObjectId==obj.Id&&hit==hitFlat&&ReferenceEquals(ShownGeometry(obj.Id),flat),"Shading changed Core pick or failed to restore Flat geometry.");
        Pass("Flat/Smooth controls fit the minimum View layout; sphere normals visibly change while exact corners, Core geometry, camera, selection and ray hit remain unchanged");

        Ok(await editor.PreviewSurfaceMaterialAsync(new SurfaceMaterialRequest(obj.Id,0,new("Test red",new(220,55,55)),SurfacePaintMode.Triangle)));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Refresh();before=editor.Snapshot;obj=before.Scene.Objects.Single();await Workspace(4);SetShading(true);
        smooth=(MeshGeometry3D)Invoke("SmoothDisplayMesh",flat)!;ViewFaces(obj,255);
        Assert(SeparateDrawn().Where(p=>p.Value==obj.Id).All(p=>ReferenceEquals(((MeshGeometry3D)p.Key.Geometry).Normals,smooth.Normals)),"Material batching introduced a normal seam.");
        ViewToggle("ViewXRay",true);ViewFaces(obj,72);ViewToggle("ViewColors",false);ViewToggle("ViewWireframe",true);ViewToggle("ViewColors",true);ViewFaces(obj,72);ViewToggle("ViewXRay",false);ViewFaces(obj,255);
        Assert(editor.Presentation.Snapshot.Settings.Shading==ShadingMode.Smooth,"Another display flag reset Smooth.");SameAccepted(before);SetShading(false);Assert(SeparateDrawn().Where(p=>p.Value==obj.Id).All(p=>ReferenceEquals(((MeshGeometry3D)p.Key.Geometry).Normals,flat.Normals)),"Flat painted cache kept smooth normals.");
        var opposed=new MeshGeometry3D{Positions=new Point3DCollection{new(0,0,0),new(1,0,0),new(0,1,0),new(0,0,0),new(0,1,0),new(1,0,0),new(2,2,2)},TriangleIndices=new Int32Collection{0,1,2,3,4,5,6,6,6}};opposed.Freeze();
        var safe=(MeshGeometry3D)Invoke("SmoothDisplayMesh",opposed)!;SameCorners(opposed,safe);Assert(ReferenceEquals(safe,Invoke("SmoothDisplayMesh",opposed)),"Smooth weak cache missed same source geometry.");Click(Named<Button>("ResetDisplayButton"));
        Pass("Paint batches share full-object smooth normals and restore exact face RGB/alpha across display toggles; opposed and degenerate triangles have finite cached fallback normals");

        await Open("cube-20mm.stl");await Workspace(2);Click(Named<Button>("HollowButton"));Click(Named<Button>("PreviewHollowButton"));await Idle();before=editor.Snapshot;var candidate=before.Preview!;obj=candidate.Scene.Objects.Single();
        var cut=(MeshGeometry3D)Invoke("HollowDisplayMesh",obj,before)!;Assert(cut!=null,"Hollow inspection did not produce actual cutaway geometry.");Named<TextBox>("HollowThickness").Text="2.5";var draft=Named<TextBox>("HollowThickness").Text;await Workspace(4);camera=CameraPose();SetShading(true);SameCorners(cut!,ShownGeometry(obj.Id));ViewToggle("ViewXRay",true);
        Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<TextBox>("HollowThickness").Text==draft&&!Named<Button>("ApplyHollowButton").IsEnabled&&CameraPose()==camera,"Shading changed hollow candidate, dirty draft or camera.");InspectionFooterInside("PreviewHollowButton","ApplyHollowButton","CancelHollowButton");Capture("shading-cutaway-smooth-minimum",1040,680);Click(Named<Button>("CancelHollowButton"));SameAccepted(before);Click(Named<Button>("ResetDisplayButton"));
        Pass("Smooth normals use the actual cutaway mesh; X-ray and View retain hollow preview/draft and fixed cancellation without accepting the inspection cut");

        await OpenSurfacePaint();Click(Named<Button>("SurfaceMaterialButton"));Named<TextBox>("MaterialHex").Text="#5F9B64";await PaintWorld(new(6,11,20));before=editor.Snapshot;candidate=before.Preview!;await Workspace(4);SetShading(true);
        Assert(ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<Button>("ApplyMaterialButton").IsEnabled,"Shading invalidated reviewed paint.");Click(Named<Button>("ApplyMaterialButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,candidate.Scene),"Paint Apply lost reviewed candidate.");Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Presentation.Snapshot.Settings.Shading==ShadingMode.Smooth,"Undo changed display settings or lost accepted geometry.");
        Ok(editor.Presentation.Update(editor.Presentation.Snapshot.Settings with{Grid=false}));SetShading(false);Assert(!editor.Presentation.Snapshot.Settings.Grid&&editor.Presentation.Snapshot.Settings.Shading==ShadingMode.Flat,"Shading overwrote unrelated shared settings.");Click(Named<Button>("ResetDisplayButton"));Assert(editor.Presentation.Snapshot.Settings==ViewSettings.Default,"Reset did not restore Flat/default flags.");
        Pass("Owned paint preview Apply/Undo remains exact under Smooth; shading updates only its own shared setting and Reset intentionally restores Flat with all defaults");
    }
}
