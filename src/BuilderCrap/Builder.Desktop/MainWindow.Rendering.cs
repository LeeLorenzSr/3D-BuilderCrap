using Builder.Core;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Builder.Desktop;

public partial class MainWindow
{
    // These are presentation resources for current visible objects only. Core/history retain meshes,
    // while the weak mesh cache permits old imported/preview meshes to leave with their documents.
    private sealed class ObjectVisual(MeshGeometry3D geometry,Affine transform,Color color)
    {
        public List<GeometryModel3D> Models {get;}=[];
        public MeshGeometry3D Geometry {get;set;}=geometry;
        public ImmutableDictionary<int,SurfaceMaterial> TriangleMaterials {get;set;}=ImmutableDictionary<int,SurfaceMaterial>.Empty;
        public Affine Transform {get;set;}=transform;
        public Color Color {get;set;}=color;
    }
    private readonly Dictionary<Guid,ObjectVisual> objectVisuals=[];
    private readonly Dictionary<Color,Material> objectMaterials=[];
    private readonly ConditionalWeakTable<MeshGeometry3D,MeshGeometry3D> smoothDisplayCache=new();
    private readonly Model3DGroup sceneModels=new(), gridModels=new(), cutPlaneModels=new(), wireframeModels=new(), shadowModels=new(), reflectionModels=new();
    private ShadowSurface[] shadowSources=[];
    private ReflectionSurface[] reflectionSources=[];
    private readonly List<Model3D> desiredModels=[];
    private Bounds3? gridBounds;
    private bool gridWasEmpty;
    private bool viewportModelsInitialized;

    private void RefreshViewportModels()
    {
        if(!viewportModelsInitialized)
        {
            sceneModels.Children.Add(new AmbientLight(Color.FromRgb(72,80,90)));
            sceneModels.Children.Add(new DirectionalLight(Colors.White,DisplayShadow.LightDirection));
            sceneModels.Children.Add(new DirectionalLight(Color.FromRgb(75,85,100),new(.6,-.5,-.3)));
            Viewport.Children.Add(new ModelVisual3D {Content=sceneModels});
            viewportModelsInitialized=true;
        }
        var snapshot=editor.Snapshot;
        var settings=editor.Presentation.Snapshot.Settings;
        SyncViewControls();
        var selectedIds=snapshot.SelectedObjectIds.ToHashSet();
        var scene=DisplayedScene(snapshot);
        var visibleObjects=CurrentSeparatePreview(snapshot)!=null&&separateInspectId!=null&&SeparateShowOriginal.IsChecked!=true
            ?scene.Objects.Where(o=>SeparateDisplayVisible(o,snapshot)).ToImmutableArray():scene.Objects;
        var visibleIds=visibleObjects.Select(o=>o.Id).ToHashSet();
        foreach(var id in objectVisuals.Keys.Where(id=>!visibleIds.Contains(id)).ToArray()) objectVisuals.Remove(id);
        var noRetainedObjectModels=objectVisuals.Count==0 && visibleObjects.Length>0;
        desiredModels.Clear();
        wireframeModels.Children.Clear();
        for(var i=0;i<3;i++) desiredModels.Add(sceneModels.Children[i]);
        var shadows=new List<ShadowSurface>();
        var reflections=new List<ReflectionSurface>();
        if(settings.Reflections)desiredModels.Add(reflectionModels);
        if(settings.Shadows)desiredModels.Add(shadowModels);
        hitObjects.Clear();
        foreach(var obj in visibleObjects)
        {
            var color=DisplayAlpha(ObjectDisplayColor(obj,snapshot,selectedIds),settings.XRay);
            // Cutaway vertices are already in world space and are inspection-only. The ordinary
            // retained geometry/transform path is restored even when the Core mesh itself is unchanged.
            var cutaway=HollowDisplayMesh(obj,snapshot);
            var flatGeometry=cutaway??DisplayMesh(obj.Mesh);
            var geometry=settings.Shading==ShadingMode.Smooth?SmoothDisplayMesh(flatGeometry):flatGeometry;
            var transform=cutaway==null?obj.Transform:Affine.Identity;
            if(settings.Shadows)shadows.Add(new(flatGeometry,transform));
            var faces=settings.Colors&&cutaway==null&&!HasMaterialRoleColor(obj,snapshot)?obj.TriangleMaterials:ImmutableDictionary<int,SurfaceMaterial>.Empty;
            if(!objectVisuals.TryGetValue(obj.Id,out var visual))
            {
                visual=new(geometry,transform,color);objectVisuals.Add(obj.Id,visual);
            }
            if(faces.IsEmpty&&visual.TriangleMaterials.IsEmpty&&visual.Models.Count==1)
            {
                var model=visual.Models[0];
                if(!ReferenceEquals(visual.Geometry,geometry))model.Geometry=geometry;
                if(visual.Color!=color)model.Material=model.BackMaterial=ObjectMaterial(color);
            }
            else if(visual.Models.Count==0||!ReferenceEquals(visual.Geometry,geometry)||!ReferenceEquals(visual.TriangleMaterials,faces)||visual.Color!=color)
            {
                visual.Models.Clear();
                if(faces.IsEmpty)AddBatch(geometry,color);
                else
                {
                    // Every source triangle appears once. Batches change presentation only; Core
                    // ray picking still returns the canonical accepted-mesh triangle index.
                    var batches=new Dictionary<Color,List<int>>();
                    for(var face=0;face<obj.Mesh.Triangles.Length;face++)
                    {
                        var material=obj.GetTriangleMaterial(face);var faceColor=material==null?color:DisplayAlpha(Color.FromRgb(material.Color.R,material.Color.G,material.Color.B),settings.XRay);
                        if(!batches.TryGetValue(faceColor,out var indices)){indices=[];batches.Add(faceColor,indices);}
                        indices.Add(3*face);indices.Add(3*face+1);indices.Add(3*face+2);
                    }
                    foreach(var (faceColor,indices) in batches)
                    {
                        var part=new MeshGeometry3D{Positions=geometry.Positions,Normals=geometry.Normals,TriangleIndices=new Int32Collection(indices)};part.Freeze();AddBatch(part,faceColor);
                    }
                }
                void AddBatch(MeshGeometry3D part,Color faceColor)
                {var material=ObjectMaterial(faceColor);visual.Models.Add(new GeometryModel3D(part,material){BackMaterial=material,Transform=DisplayTransform(transform)});}
            }
            if(visual.Transform!=transform)foreach(var model in visual.Models)model.Transform=DisplayTransform(transform);
            visual.Geometry=geometry;visual.TriangleMaterials=faces;visual.Transform=transform;visual.Color=color;
            foreach(var model in visual.Models)
            {
                desiredModels.Add(model);hitObjects.Add(model,obj.Id);
                if(settings.Reflections)reflections.Add(new((MeshGeometry3D)model.Geometry,transform,model.Material));
            }
            if(settings.Wireframe)AddWireframe(geometry,transform,scene.Bounds);
        }
        RefreshReflectionModels(reflections);
        RefreshShadowModels(shadows);
        var acceptedBounds=snapshot.Scene.Bounds;
        if(gridBounds!=acceptedBounds || gridWasEmpty!=snapshot.Scene.Objects.IsEmpty)
        {
            gridModels.Children.Clear(); AddGrid(gridModels,snapshot.Scene); gridBounds=acceptedBounds; gridWasEmpty=snapshot.Scene.Objects.IsEmpty;
        }
        cutPlaneModels.Children.Clear(); AddCutPlane(cutPlaneModels); AddExtrudeDownPlane(cutPlaneModels);
        if(settings.Grid)desiredModels.Add(gridModels);
        if(settings.Wireframe)desiredModels.Add(wireframeModels);
        desiredModels.Add(cutPlaneModels);
        DisplayNotice.Visibility=!settings.Colors&&(materialsActive||scene.Objects.Any(o=>o.Material!=null||!o.TriangleMaterials.IsEmpty))?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
        if(noRetainedObjectModels)
        {
            // Attach an entirely new object set once; retained scenes use the incremental path below.
            sceneModels.Children=new Model3DCollection(desiredModels);
            return;
        }
        // Keep unchanged scene nodes attached. Selection, preview replacement and Undo update only
        // the mesh/transform/material that changed, without recreating every WPF model and light.
        for(var i=0;i<desiredModels.Count;i++)
        {
            if(i<sceneModels.Children.Count && ReferenceEquals(sceneModels.Children[i],desiredModels[i])) continue;
            var existing=sceneModels.Children.IndexOf(desiredModels[i]);
            if(existing>=0) sceneModels.Children.RemoveAt(existing);
            sceneModels.Children.Insert(i,desiredModels[i]);
        }
        while(sceneModels.Children.Count>desiredModels.Count) sceneModels.Children.RemoveAt(sceneModels.Children.Count-1);
    }

    private void RefreshReflectionModels(List<ReflectionSurface> sources)
    {
        // Retain only the current visible batch identities. Camera/grid/shadow changes reuse
        // these resources; actual geometry, affine, shading or material changes replace them.
        if(sources.SequenceEqual(reflectionSources))return;
        reflectionModels.Children.Clear();
        foreach(var source in sources)if(DisplayReflection.Build(source) is {} model)reflectionModels.Children.Add(model);
        reflectionSources=sources.ToArray();
    }
    private void RefreshShadowModels(List<ShadowSurface> sources)
    {
        // Shading, selection, color and camera changes reuse the same silhouette. Geometry,
        // transforms, isolation and preview replacement invalidate it by actual display identity.
        if(sources.SequenceEqual(shadowSources))return;
        var model=sources.Count==0?null:DisplayShadow.Build(sources);
        shadowModels.Children.Clear();
        if(model!=null)shadowModels.Children.Add(model);
        shadowSources=sources.ToArray();
    }

    private Material ObjectMaterial(Color color)
    {
        if(objectMaterials.TryGetValue(color,out var existing)) return existing;
        var material=new DiffuseMaterial(new SolidColorBrush(color)); material.Freeze(); objectMaterials.Add(color,material); return material;
    }
    private static Color DisplayAlpha(Color color,bool xray)=>Color.FromArgb(xray?(byte)72:(byte)255,color.R,color.G,color.B);
    private MeshGeometry3D SmoothDisplayMesh(MeshGeometry3D flat)=>smoothDisplayCache.GetValue(flat,DisplayNormals.Smooth);
    private void AddWireframe(MeshGeometry3D geometry,Affine transform,Bounds3 bounds)
    {
        // Narrow ribbons follow each actual displayed triangle, so the ordinary 3D depth buffer
        // hides covered edges. This inspection geometry is deliberately absent from hitObjects.
        var size=bounds.Size;var width=Math.Max(.000001,Math.Max(size.X,Math.Max(size.Y,size.Z))*.0025);
        var mesh=new MeshGeometry3D();
        Vec3 Point(int index){var p=geometry.Positions[index];return transform.Apply(new(p.X,p.Y,p.Z));}
        static double Length(Vec3 v)=>Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);
        for(var i=0;i<geometry.TriangleIndices.Count;i+=3)
        {
            var a=Point(geometry.TriangleIndices[i]);var b=Point(geometry.TriangleIndices[i+1]);var c=Point(geometry.TriangleIndices[i+2]);
            var cross=Vec3.Cross(b-a,c-a);var length=Length(cross);if(length==0)continue;var normal=cross*(1/length);
            Edge(a,b);Edge(b,c);Edge(c,a);
            void Edge(Vec3 start,Vec3 end)
            {
                var direction=end-start;var distance=Length(direction);if(distance==0)return;
                var thickness=Math.Min(width,distance*.08);var inward=Vec3.Cross(normal,direction*(1/distance))*thickness;var offset=normal*(width*.025);
                var first=mesh.Positions.Count;
                foreach(var p in new[]{start+offset,end+offset,end+offset+inward,start+offset+inward})mesh.Positions.Add(new(p.X,p.Y,p.Z));
                foreach(var index in new[]{0,1,2,0,2,3})mesh.TriangleIndices.Add(first+index);
            }
        }
        mesh.Freeze();var material=ObjectMaterial(Color.FromRgb(35,51,61));wireframeModels.Children.Add(new GeometryModel3D(mesh,material){BackMaterial=material});
    }

    private static MatrixTransform3D DisplayTransform(Affine t)
    {
        var transform=new MatrixTransform3D(new Matrix3D(t.X.X,t.X.Y,t.X.Z,0,t.Y.X,t.Y.Y,t.Y.Z,0,t.Z.X,t.Z.Y,t.Z.Z,0,t.Offset.X,t.Offset.Y,t.Offset.Z,1));
        transform.Freeze(); return transform;
    }

    private MeshGeometry3D DisplayMesh(Mesh mesh)
    {
        if(meshCache.TryGetValue(mesh,out var existing)) return existing;
        var count=mesh.Triangles.Length*3;
        var points=new Point3D[count]; var normals=new Vector3D[count]; var indices=new int[count];
        var offset=0;
        foreach(var triangle in mesh.Triangles)
        {
            var a=mesh.Vertices[triangle.A]; var b=mesh.Vertices[triangle.B]; var c=mesh.Vertices[triangle.C];
            var n=Vec3.Cross(b-a,c-a); var normal=new Vector3D(n.X,n.Y,n.Z);
            if(normal.LengthSquared>0) normal.Normalize();
            points[offset]=new(a.X,a.Y,a.Z); points[offset+1]=new(b.X,b.Y,b.Z); points[offset+2]=new(c.X,c.Y,c.Z);
            for(var i=0;i<3;i++) {normals[offset+i]=normal; indices[offset+i]=offset+i;}
            offset+=3;
        }
        var display=new MeshGeometry3D {Positions=new Point3DCollection(points),Normals=new Vector3DCollection(normals),TriangleIndices=new Int32Collection(indices)};
        display.Freeze(); meshCache.Add(mesh,display); return display;
    }
}
