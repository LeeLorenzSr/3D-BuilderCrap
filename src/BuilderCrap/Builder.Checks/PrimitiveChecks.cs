using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class PrimitiveChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/primitives-20261020/core"); Directory.CreateDirectory(output);
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        EditorSession New()=>new(worker);
        var cases=new List<string>(); var files=new List<object>();
        void Pass(string text) {cases.Add(text);Console.WriteLine("PASS "+text);}
        var size=new Vec3(18,26,34);
        PrimitiveRequest[] requests=[
            new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,size),new RadialPrimitiveRequest(PrimitiveKind.Cylinder,size),
            new SpherePrimitiveRequest(size),new PolyhedronPrimitiveRequest(PrimitiveKind.Pyramid,size),
            new RadialPrimitiveRequest(PrimitiveKind.Cone,size),new PolyhedronPrimitiveRequest(PrimitiveKind.Hexagon,size),
            new PolyhedronPrimitiveRequest(PrimitiveKind.Wedge,size),new TorusPrimitiveRequest(20,5),
            new PolyhedronPrimitiveRequest(PrimitiveKind.Tetrahedron,size)];
        async Task Output(PrimitiveRequest request,string name)
        {
            var s=New(); var empty=s.Snapshot;
            Ok(await s.PreviewPrimitiveAsync(request,expectedRevision:empty.Revision)); Accepted(s,empty);
            var p=s.Snapshot.Preview!;var obj=p.Scene.Objects.Single();
            Check(p.Request==request && p.SelectedObjectId==obj.Id && p.SelectedObjectIds.SequenceEqual(new[]{obj.Id}),"Creation preview target/selection mismatch.");
            Check(obj.Transform==Affine.Identity,"Creation changed the placement transform.");
            var expectedSize=request is TorusPrimitiveRequest t ? new Vec3(2*(t.MajorRadiusMillimeters+t.TubeRadiusMillimeters),2*(t.MajorRadiusMillimeters+t.TubeRadiusMillimeters),2*t.TubeRadiusMillimeters) : Size(request);
            VecNear(obj.Bounds.Min,new()); VecNear(obj.Bounds.Max,expectedSize);
            var topology=Topology(obj.Mesh,request.Kind==PrimitiveKind.Torus ? 0 : 2);var volume=Volume(obj);
            Near(volume,ExpectedVolume(request),1e-10);
            Ok(s.ApplyPreview(p.Id));Check(s.Snapshot.Revision==empty.Revision+1 && s.Snapshot.IsDirty && s.Snapshot.CanUndo,"Add was not one dirty history step.");
            var accepted=s.Snapshot;var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");
            Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));
            Ok(s.Undo());Check(s.Snapshot.Scene==empty.Scene && s.Snapshot.IsDirty,"Undo did not restore original scene across save.");
            Ok(s.Redo());Check(s.Snapshot.Scene==accepted.Scene && !s.Snapshot.IsDirty,"Redo did not restore saved creation identity.");
            var reopen=New();Ok(await reopen.OpenAsync(native));VecNear(reopen.Snapshot.Scene.Bounds.Min,new());VecNear(reopen.Snapshot.Scene.Bounds.Max,expectedSize);
            Near(Volume(reopen.Snapshot.Scene.Objects.Single()),volume,1e-10);
            var stlOpen=New();Ok(await stlOpen.OpenAsync(stl));Near(Volume(stlOpen.Snapshot.Scene.Objects.Single()),volume,2e-6);
            files.Add(new {name,request=JsonSerializer.SerializeToElement(request,request.GetType()),bounds=new {min=obj.Bounds.Min,max=obj.Bounds.Max},volumeMm3=volume,topology,
                native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=new {path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        foreach(var request in requests) await Output(request,request.Kind.ToString().ToLowerInvariant());
        Pass("Nine shapes: exact bounds, independent faceted volumes, closed outward indexed topology/Euler characteristic, one-step history, 3MF/STL roundtrip");
        foreach(var (request,name) in new (PrimitiveRequest,string)[]{
            (new RadialPrimitiveRequest(PrimitiveKind.Cylinder,size,8),"cylinder-low"),(new RadialPrimitiveRequest(PrimitiveKind.Cone,size,8),"cone-low"),
            (new SpherePrimitiveRequest(size,8,4),"sphere-low"),(new TorusPrimitiveRequest(20,5,8,8),"torus-low"),
            (new RadialPrimitiveRequest(PrimitiveKind.Cylinder,size,32),"cylinder-custom"),(new RadialPrimitiveRequest(PrimitiveKind.Cone,size,32),"cone-custom"),
            (new SpherePrimitiveRequest(size,32,16),"sphere-custom"),(new TorusPrimitiveRequest(20,5,32,16),"torus-custom")}) await Output(request,name);
        Pass("Low and custom tessellation preserves bounds and analytic polygon/frustum/torus volumes, with closed topology and saved outputs");

        Check((int)PrimitiveKind.Cube==0 && (int)PrimitiveKind.Cylinder==1 && (int)PrimitiveKind.Sphere==2,"Legacy enum values changed.");
        foreach(var kind in new[]{PrimitiveKind.Cube,PrimitiveKind.Cylinder,PrimitiveKind.Sphere})
        {
            var s=New();Ok(s.Execute(new AddPrimitive(kind,size)));var legacy=s.Snapshot.Scene.Objects.Single().Mesh;
            var p=New();Ok(await p.PreviewPrimitiveAsync(requests.Single(r=>r.Kind==kind)));var current=p.Snapshot.Preview!.Scene.Objects.Single().Mesh;
            Check(legacy.Vertices.SequenceEqual(current.Vertices) && legacy.Triangles.SequenceEqual(current.Triangles),"Default creation changed legacy mesh ordering/coordinates.");
        }
        var cube=New();Ok(cube.Execute(new AddCube()));var cubeMesh=cube.Snapshot.Scene.Objects.Single().Mesh;
        var cubePreview=New();Ok(await cubePreview.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(20,20,20))));
        Check(cubeMesh.Vertices.SequenceEqual(cubePreview.Snapshot.Preview!.Scene.Objects.Single().Mesh.Vertices),"AddCube contract changed.");
        var legacyTorus=New();Check(legacyTorus.Execute(new AddPrimitive(PrimitiveKind.Torus,size)).Outcome==Outcome.Invalid,"Legacy torus guessed radii.");
        Pass("Original enum values, AddCube and original-three default vertex/face sequences preserved; legacy torus requires explicit radii");

        var update=New();Ok(update.Execute(new AddCube()));var before=update.Snapshot;
        Ok(await update.PreviewAsync(new SpherePrimitiveRequest(size)));var first=update.Snapshot.Preview!;
        Ok(await update.PreviewPrimitiveAsync(new TorusPrimitiveRequest(20,5,16,8),first.Id,expectedRevision:before.Revision));
        var second=update.Snapshot.Preview!;
        Check(second.Id!=first.Id && second.ResultObjectIds.SequenceEqual(first.ResultObjectIds),"Replacement token/object identity policy changed.");
        Check(second.Scene.Objects.Length==before.Scene.Objects.Length+1 && ReferenceEquals(second.Scene.Objects[0],before.Scene.Objects[0]),"Replacement accumulated candidates or touched existing objects.");
        Accepted(update,before);var withPreview=update.Snapshot;
        Check((await update.PreviewPrimitiveAsync(requests[0],first.Id)).Outcome==Outcome.Conflict,"Stale replacement accepted.");Same(update,withPreview);
        Check((await update.PreviewPrimitiveAsync(requests[0],second.Id,expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale revision accepted.");Same(update,withPreview);
        Check((await update.PreviewPrimitiveAsync(requests[0])).Outcome==Outcome.Conflict,"Unowned preview replaced.");Same(update,withPreview);
        Check(update.Select(null).Outcome==Outcome.Conflict && update.Undo().Outcome==Outcome.Conflict,"Preview allowed selection/history changes.");
        Check((await update.OpenAsync(Path.Combine(root,"fixtures/generated/cube-20mm.stl"))).Outcome==Outcome.Conflict,"Preview allowed Open.");Same(update,withPreview);
        Check(update.DiscardPreview(second.Id).Outcome==Outcome.Cancelled,"Discard did not cancel preview.");Same(update,before);
        Ok(await update.PreviewTransformAsync(new MoveTransform(before.SelectedObjectId!.Value,new(1,0,0))));var foreign=update.Snapshot;
        Check((await update.PreviewPrimitiveAsync(requests[0],foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Primitive replaced transform preview.");Same(update,foreign);
        Pass("Generic dispatch, replacement derives from accepted content, stable new leaf/fresh token, foreign/stale preview and revision guards, selection/history/file guards");

        var invalid=New();Ok(await invalid.OpenAsync(Path.Combine(root,"fixtures/generated/assembly-mm.3mf")));
        Ok(invalid.Execute(new RenameObject(invalid.Snapshot.SelectedObjectId!.Value,"temporary")));Ok(invalid.Undo());
        Check(invalid.Snapshot.CanRedo && invalid.Snapshot.FilePath!=null,"Cancellation baseline lacks file/history state.");
        Ok(await invalid.PreviewPrimitiveAsync(requests[0]));var held=invalid.Snapshot;
        PrimitiveRequest[] bad=[
            new PolyhedronPrimitiveRequest(PrimitiveKind.Torus,size),new PolyhedronPrimitiveRequest((PrimitiveKind)99,size),
            new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(0,2,3)),new SpherePrimitiveRequest(new(double.NaN,2,3)),
            new RadialPrimitiveRequest(PrimitiveKind.Sphere,size),new RadialPrimitiveRequest(PrimitiveKind.Cone,size,6),
            new SpherePrimitiveRequest(size,48,3),new SpherePrimitiveRequest(size,48,129),new TorusPrimitiveRequest(5,5),
            new TorusPrimitiveRequest(4,5),new TorusPrimitiveRequest(double.PositiveInfinity,1),new TorusPrimitiveRequest(20,5,256,256),
            new TorusPrimitiveRequest(20,5,257,24),new PolyhedronPrimitiveRequest(PrimitiveKind.Pyramid,new(10_000_001,2,3)),
            new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(double.Epsilon,20,20)),new TorusPrimitiveRequest(1,Math.BitDecrement(1),48,24)];
        foreach(var request in bad) {Check((await invalid.PreviewPrimitiveAsync(request,held.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid creation accepted: "+request);Same(invalid,held);}
        foreach(var stage in new[]{"Preparing primitive","Generating primitive","Validating primitive","Ready to preview primitive"})
        {
            using var cancel=new CancellationTokenSource();
            var progress=new Callback(p=>{if(p.Stage==stage && (stage!="Generating primitive" || p.Completed>=256))cancel.Cancel();});
            Check((await invalid.PreviewPrimitiveAsync(new TorusPrimitiveRequest(20,5,128,128),held.Preview!.Id,cancel.Token,progress)).Outcome==Outcome.Cancelled,"Cancellation failed at "+stage);Same(invalid,held);
        }
        using(var cancel=new CancellationTokenSource()) {cancel.Cancel();Check((await invalid.PreviewPrimitiveAsync(requests[0],held.Preview!.Id,cancel.Token)).Outcome==Outcome.Cancelled,"Pre-cancel failed.");Same(invalid,held);}
        Pass("Invalid type/size/radius/resolution/mesh budget and pre/during/validation/before-publication cancellation retain complete accepted state and prior preview");

        var busy=New();using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
        {
            var job=busy.PreviewPrimitiveAsync(requests[0],progress:new Callback(p=>{if(p.Stage=="Preparing primitive"){entered.Set();if(!release.Wait(10000))throw new IOException("Test barrier timeout.");}}));
            Check(entered.Wait(10000),"Creation did not acquire busy lease.");
            Check((await busy.PreviewPrimitiveAsync(requests[0])).Outcome==Outcome.Busy && busy.Execute(new AddCube()).Outcome==Outcome.Busy && busy.Select(null).Outcome==Outcome.Busy,"Busy lease not shared with edit/selection/creation.");
            release.Set();Ok(await job);
        }
        var grouped=New();Ok(await grouped.OpenAsync(Path.Combine(root,"fixtures/generated/assembly-mm.3mf")));
        Ok(await grouped.PreviewArrangementAsync(new GroupRequest(grouped.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray())));Ok(grouped.ApplyPreview(grouped.Snapshot.Preview!.Id));
        var group=grouped.Snapshot.Scene.Groups.Single().Id;Ok(grouped.EnterGroup(group));var scoped=grouped.Snapshot;
        Check((await grouped.PreviewPrimitiveAsync(requests[0] with {MemberGroupId=group})).Outcome==Outcome.Conflict,"Creation allowed active member scope.");Same(grouped,scoped);
        Check((await grouped.PreviewPrimitiveAsync(requests[0])).Outcome==Outcome.Conflict,"Missing scope authority accepted.");Same(grouped,scoped);
        Ok(grouped.ExitGroup());var exited=grouped.Snapshot;
        Check((await grouped.PreviewPrimitiveAsync(requests[0] with {MemberGroupId=group})).Outcome==Outcome.Conflict,"Stale member authority accepted.");Same(grouped,exited);
        Ok(await grouped.PreviewPrimitiveAsync(requests[0]));var groupedPreview=grouped.Snapshot.Preview!;
        Check(groupedPreview.Scene.Groups.SequenceEqual(exited.Scene.Groups),"Creation changed groups.");
        Ok(grouped.ApplyPreview(groupedPreview.Id));Ok(grouped.Undo());
        Check(grouped.Snapshot.Scene==exited.Scene && grouped.Snapshot.SelectedObjectIds.SequenceEqual(exited.SelectedObjectIds) &&
            grouped.Snapshot.SelectedObjectId==exited.SelectedObjectId && grouped.Snapshot.EditingGroupId==exited.EditingGroupId,"Creation Undo lost group/full selection/scope.");
        Ok(grouped.Redo());Check(grouped.Snapshot.Scene==groupedPreview.Scene && grouped.Snapshot.SelectedObjectIds.SequenceEqual(groupedPreview.SelectedObjectIds),"Creation Redo lost new selection.");
        Pass("Shared busy lease, exact member authority, creation Done-first guard and preservation of unrelated groups");

        var limit=New();for(var i=0;i<45;i++) Ok(limit.Execute(new AddPrimitive(PrimitiveKind.Sphere,new(10,10,10))));var full=limit.Snapshot;
        var generated=false;Check((await limit.PreviewPrimitiveAsync(new SpherePrimitiveRequest(size),progress:new Callback(p=>generated|=p.Stage=="Generating primitive"))).Outcome==Outcome.Invalid,"Combined triangle budget accepted.");
        Check(!generated,"Combined triangle budget was checked after generation.");Same(limit,full);
        var objectLimit=New();for(var i=0;i<1000;i++) Ok(objectLimit.Execute(new AddCube()));var objectsFull=objectLimit.Snapshot;
        Check((await objectLimit.PreviewPrimitiveAsync(requests[0])).Outcome==Outcome.Invalid,"Object budget accepted.");Same(objectLimit,objectsFull);
        Pass("Combined accepted-scene triangle/object budgets reject before generation without accepted mutation");

        foreach(var request in new PrimitiveRequest[]{new RadialPrimitiveRequest(PrimitiveKind.Cone,size,16),new TorusPrimitiveRequest(20,5,16,8)})
        {
            var s=New();Ok(await s.PreviewPrimitiveAsync(request));Ok(s.ApplyPreview(s.Snapshot.Preview!.Id));var accepted=s.Snapshot;
            var obj=accepted.Scene.Objects.Single();var volume=Volume(obj);
            Ok(await s.PreviewAsync(new SplitRequest(obj.Id,new(0,0,1),obj.Bounds.Center.Z)));
            Check(s.Snapshot.Preview!.Scene.Objects.Length==2,"Worker split did not create two halves.");
            foreach(var part in s.Snapshot.Preview.Scene.Objects) Topology(part.Mesh,request.Kind==PrimitiveKind.Torus ? 0 : 2);
            Near(s.Snapshot.Preview.Scene.Objects.Sum(Volume),volume,2e-6);
            Check(s.DiscardPreview(s.Snapshot.Preview.Id).Outcome==Outcome.Cancelled,"Split cancel failed.");Same(s,accepted);
        }
        Pass("New cone and genus-one torus are compatible with actual native split worker; closed halves conserve volume and Cancel restores accepted state");
        File.WriteAllText(Path.Combine(output,"primitive-results.json"),JsonSerializer.Serialize(new {cases,files,
            runtime=Environment.Version.ToString(),coreSha256=Hash(typeof(EditorSession).Assembly.Location),
            workerSha256=Hash(worker),inputAssemblySha256=Hash(Path.Combine(root,"fixtures/generated/assembly-mm.3mf"))},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static Vec3 Size(PrimitiveRequest p)=>p switch {PolyhedronPrimitiveRequest x=>x.SizeMillimeters,RadialPrimitiveRequest x=>x.SizeMillimeters,SpherePrimitiveRequest x=>x.SizeMillimeters,_=>throw new Exception("No size.")};
    private static double ExpectedVolume(PrimitiveRequest p)
    {
        if(p is TorusPrimitiveRequest t)return t.RadialSegments*t.TubeSegments*.5*t.MajorRadiusMillimeters*t.TubeRadiusMillimeters*t.TubeRadiusMillimeters*Math.Sin(2*Math.PI/t.RadialSegments)*Math.Sin(2*Math.PI/t.TubeSegments);
        var s=Size(p);var xyz=s.X*s.Y*s.Z;
        if(p is SpherePrimitiveRequest sphere)
        {
            double sum=0;var area=sphere.RadialSegments*Math.Sin(2*Math.PI/sphere.RadialSegments)*s.X*s.Y/8;
            for(var k=0;k<sphere.LatitudeSegments;k++)
            {var a=k*Math.PI/sphere.LatitudeSegments;var b=(k+1)*Math.PI/sphere.LatitudeSegments;var ra=Math.Sin(a);var rb=Math.Sin(b);sum+=(Math.Cos(a)-Math.Cos(b))*s.Z/2*area*(ra*ra+ra*rb+rb*rb)/3;}
            return sum;
        }
        return p.Kind switch {PrimitiveKind.Cube=>xyz,PrimitiveKind.Pyramid=>xyz/3,PrimitiveKind.Wedge=>xyz/2,PrimitiveKind.Tetrahedron=>xyz/6,PrimitiveKind.Hexagon=>xyz*.75,
            PrimitiveKind.Cylinder=>xyz*((RadialPrimitiveRequest)p).RadialSegments*Math.Sin(2*Math.PI/((RadialPrimitiveRequest)p).RadialSegments)/8,
            PrimitiveKind.Cone=>xyz*((RadialPrimitiveRequest)p).RadialSegments*Math.Sin(2*Math.PI/((RadialPrimitiveRequest)p).RadialSegments)/24,_=>throw new Exception("No volume.")};
    }
    private static object Topology(Mesh mesh,int expectedEuler)
    {
        var edges=new Dictionary<(int,int),(int Count,int Direction)>();var used=new HashSet<int>();
        foreach(var t in mesh.Triangles)
        {
            var a=mesh.Vertices[t.A];var b=mesh.Vertices[t.B];var c=mesh.Vertices[t.C];Check(Vec3.Cross(b-a,c-a)!=new Vec3(),"Degenerate face.");
            foreach(var (x,y) in new[]{(t.A,t.B),(t.B,t.C),(t.C,t.A)})
            {used.Add(x);used.Add(y);var key=(Math.Min(x,y),Math.Max(x,y));var old=edges.GetValueOrDefault(key);edges[key]=(old.Count+1,old.Direction+(x<y?1:-1));}
        }
        Check(edges.Values.All(e=>e.Count==2 && e.Direction==0),"Mesh is open/nonmanifold/inconsistently wound.");
        Check(used.Count==mesh.Vertices.Length && used.Count-edges.Count+mesh.Triangles.Length==expectedEuler,"Unexpected unused vertices/Euler characteristic.");
        Check(mesh.Vertices.Distinct().Count()==mesh.Vertices.Length,"Coincident indexed vertices.");
        Check(Volume(new(Guid.NewGuid(),"check",mesh,Affine.Identity))>0,"Inward or zero-volume mesh.");
        return new {vertices=mesh.Vertices.Length,triangles=mesh.Triangles.Length,edges=edges.Count,euler=expectedEuler};
    }
    private static double Volume(SceneObject obj)=>obj.Mesh.Triangles.Sum(t=>{
        var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A]);var cross=Vec3.Cross(obj.Transform.Apply(obj.Mesh.Vertices[t.B]),obj.Transform.Apply(obj.Mesh.Vertices[t.C]));
        return (a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z)/6;
    });
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Near(double value,double expected,double relative=1e-12)=>Check(Math.Abs(value-expected)<=relative*Math.Max(1,Math.Abs(expected)),$"Expected {expected:G17}, got {value:G17}.");
    private static void VecNear(Vec3 a,Vec3 b){Near(a.X,b.X);Near(a.Y,b.Y);Near(a.Z,b.Z);}
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Operation changed full session state.");
    private static void Accepted(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot with {Preview=before.Preview}==before,"Preview changed accepted session state.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class Callback(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
}
