using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class MirrorMeasurementChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/mirror-measurement-20261020/core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/mirror-measurement-20261020/inputs");
        var plain=Path.Combine(input,"asymmetric-tetrahedron.3mf");var affine=Path.Combine(input,"reflected-affine-tetrahedron.3mf");
        var worker=Path.Combine(root,"prototypes/Builder/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var cases=new List<string>();var files=new List<object>();
        EditorSession New()=>new(worker);
        async Task<EditorSession> Load(string? path=null){var s=New();Ok(await s.OpenAsync(path??plain));return s;}
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task SaveCase(EditorSession s,MirrorTransform request,string name)
        {
            var before=s.Snapshot;Ok(await s.PreviewAsync(request,expectedRevision:before.Revision));Accepted(s,before);
            var p=s.Snapshot.Preview!;var effective=(MirrorTransform)p.Request;var plane=effective.PlanePositionMillimeters!.Value;
            var targets=effective.ObjectIds.ToHashSet();
            Check(p.Scene.Groups.SequenceEqual(before.Scene.Groups) && p.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds) &&
                p.SelectedObjectId==before.SelectedObjectId && p.EditingGroupId==before.EditingGroupId,"Mirror changed group/selection/scope.");
            var expected=new List<object>();
            for(var i=0;i<before.Scene.Objects.Length;i++)
            {
                var original=before.Scene.Objects[i];var result=p.Scene.Objects[i];var mirrored=targets.Contains(original.Id);
                var points=original.Mesh.Vertices.Select(original.Transform.Apply).Select(v=>mirrored?Reflect(v,request.Axis,plane):v).ToArray();
                var actual=result.Mesh.Vertices.Select(result.Transform.Apply).ToArray();
                Check(result.Id==original.Id && result.Name==original.Name,"Mirror replaced identity/name.");
                for(var j=0;j<points.Length;j++)VecNear(actual[j],points[j]);
                if(mirrored)Check(result.Transform==Affine.Identity && !ReferenceEquals(result.Mesh,original.Mesh),"Mirror did not bake canonical mesh.");
                else Check(ReferenceEquals(result,original),"Mirror touched an unrelated object.");
                Closed(result);Near(CanonicalVolume(result),CanonicalVolume(original));
                expected.Add(new {objectIndex=i,name=result.Name,mirrored,worldVertices=points,volumeMm3=CanonicalVolume(original),bounds=result.Bounds});
            }
            Ok(s.ApplyPreview(p.Id,before.Revision));var accepted=s.Snapshot;
            Check(accepted.Revision==before.Revision+1 && accepted.CanUndo,"Mirror was not one accepted edit.");
            var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");
            Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));
            Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene) && s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds) &&
                s.Snapshot.SelectedObjectId==before.SelectedObjectId && s.Snapshot.EditingGroupId==before.EditingGroupId,"Undo did not restore original references/full selection/scope.");
            Ok(s.Redo());Check(s.Snapshot.Scene==accepted.Scene && !s.Snapshot.IsDirty,"Redo lost saved identity.");
            var reopened=await Load(native);Check(reopened.Snapshot.Scene.Objects.Length==p.Scene.Objects.Length,"3MF lost instances.");
            for(var i=0;i<p.Scene.Objects.Length;i++)
            {
                var a=p.Scene.Objects[i];var b=reopened.Snapshot.Scene.Objects[i];
                PointSets(a.Mesh.Vertices.Select(a.Transform.Apply),b.Mesh.Vertices.Select(b.Transform.Apply));Closed(b);Near(CanonicalVolume(b),CanonicalVolume(a));
            }
            var stlOpened=await Load(stl);var allExpected=p.Scene.Objects.SelectMany(o=>o.Mesh.Vertices.Select(o.Transform.Apply));
            PointSets(allExpected,stlOpened.Snapshot.Scene.Objects.Single().Mesh.Vertices,2e-5);
            Closed(stlOpened.Snapshot.Scene.Objects.Single());Near(CanonicalVolume(stlOpened.Snapshot.Scene.Objects.Single()),p.Scene.Objects.Sum(CanonicalVolume),2e-6);
            files.Add(new {name,axis=request.Axis.ToString(),planePositionMillimeters=plane,expectedWorldObjects=expected,
                native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=new {path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        var reference=await Load();Vec3[] vertices=[new(0,0,0),new(18,0,0),new(3,26,0),new(5,7,34)];
        Check(reference.Snapshot.Scene.Objects.Single().Mesh.Vertices.SequenceEqual(vertices),"Unexpected analytic fixture vertices.");Near(CanonicalVolume(reference.Snapshot.Scene.Objects.Single()),2652);
        foreach(var axis in Enum.GetValues<MirrorAxis>())
        {
            var s=await Load();await SaveCase(s,new(s.Snapshot.SelectedObjectId!.Value,axis),$"mirror-{axis.ToString().ToLowerInvariant()}-center");
            s=await Load();await SaveCase(s,new(s.Snapshot.SelectedObjectId!.Value,axis,-7),$"mirror-{axis.ToString().ToLowerInvariant()}-offset");
        }
        Pass("Asymmetric tetrahedron all three center/offset reflection planes: analytic world vertices, canonical orientation, exact IDs/selection, one Undo/Redo and 3MF/STL roundtrip");

        var transformed=await Load();var id=transformed.Snapshot.SelectedObjectId!.Value;
        Ok(transformed.Execute(new MoveObject(id,new(-12,7,40))));Ok(transformed.Execute(new RotateObject(id,new(35,-20,70))));
        Ok(transformed.Execute(new ResizeObject(id,new(33,22,17))));await SaveCase(transformed,new(id,MirrorAxis.Y),"mirror-transformed-y");
        var negative=await Load(affine);var negativeOriginal=negative.Snapshot;var obj=negativeOriginal.Scene.Objects.Single();
        Vec3[] affinePoints=[new(40,-10,6),new(22,-1,6),new(37,43.5,6),new(43.5,6.5,40)];
        for(var i=0;i<4;i++)VecNear(obj.Transform.Apply(obj.Mesh.Vertices[i]),affinePoints[i]);
        Near(Determinant(obj.Transform),-2);Near(CanonicalVolume(obj),5304);
        await SaveCase(negative,new(obj.Id,MirrorAxis.X),"mirror-affine-x");
        await SaveCase(negative,new(obj.Id,MirrorAxis.X),"mirror-affine-double-x");
        var twice=negative.Snapshot.Scene.Objects.Single();PointSets(twice.Mesh.Vertices,affinePoints);Closed(twice);Near(CanonicalVolume(twice),5304);
        var inward=await Load(Path.Combine(root,"fixtures/generated/inverted-cube.stl"));var inwardBefore=inward.Snapshot;
        Check(CanonicalVolume(inwardBefore.Scene.Objects.Single())<0,"Inward-source fixture was not inward.");
        Ok(await inward.PreviewTransformAsync(new MirrorTransform(inwardBefore.SelectedObjectId!.Value,MirrorAxis.X)));
        Check(CanonicalVolume(inward.Snapshot.Preview!.Scene.Objects.Single())<0,"Mirror silently repaired authored inward winding.");
        Cancel(inward,inward.Snapshot.Preview.Id);Same(inward,inwardBefore);
        Pass("Rotated/nonuniform and imported negative-affine instances use composed determinant exactly once; double mirror restores world geometry/canonical orientation; authored inward source is not silently repaired");

        async Task<(EditorSession Session,ImmutableArray<Guid> Members,Guid Group)> Grouped()
        {
            var s=await Load();Ok(await s.InsertAsync(plain));var members=s.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
            Ok(s.Execute(new MoveObject(members[1],new(60,13,17))));Ok(s.Execute(new AddCube()));
            Ok(s.Execute(new MoveObject(s.Snapshot.SelectedObjectId!.Value,new(120,20,10))));
            Ok(await s.PreviewArrangementAsync(new GroupRequest(members,"Mirror pair")));Ok(s.ApplyPreview(s.Snapshot.Preview!.Id));
            return(s,members,s.Snapshot.Scene.Groups.Single().Id);
        }
        var grouped=await Grouped();await SaveCase(grouped.Session,new(grouped.Members[0],MirrorAxis.X),"mirror-group-x");
        var member=await Grouped();Ok(member.Session.EnterGroup(member.Group,member.Members[0]));
        await SaveCase(member.Session,new(member.Members[0],MirrorAxis.Z){MemberGroupId=member.Group},"mirror-member-z");
        Pass("Whole-group expansion around aggregate center and isolated member reflection preserve sibling object references, group identity/name and full history selection/scope");

        var invalid=await Load();Ok(invalid.Execute(new AddPrimitive(PrimitiveKind.Sphere,new(18,26,34))));var sphere=invalid.Snapshot.SelectedObjectId!.Value;
        Ok(invalid.Execute(new RenameObject(sphere,"temporary")));Ok(invalid.Undo());
        var baseline=invalid.Snapshot;Ok(await invalid.PreviewTransformAsync(new MirrorTransform(sphere,MirrorAxis.X)));var firstHeld=invalid.Snapshot;
        Ok(await invalid.PreviewTransformAsync(new MirrorTransform(sphere,MirrorAxis.Z,-7),firstHeld.Preview!.Id));var held=invalid.Snapshot;
        Check(held.Preview!.Id!=firstHeld.Preview.Id && held.Preview.ResultObjectIds.SequenceEqual(firstHeld.Preview.ResultObjectIds),"Mirror replacement lost token/target policy.");
        var acceptedSphere=baseline.Scene.Objects.Single(o=>o.Id==sphere);var replacedSphere=held.Preview.Scene.Objects.Single(o=>o.Id==sphere);
        for(var i=0;i<acceptedSphere.Mesh.Vertices.Length;i++)VecNear(replacedSphere.Mesh.Vertices[i],Reflect(acceptedSphere.Transform.Apply(acceptedSphere.Mesh.Vertices[i]),MirrorAxis.Z,-7));
        Accepted(invalid,baseline);
        foreach(var bad in new MirrorTransform[]{new(sphere,(MirrorAxis)99),new(sphere,MirrorAxis.X,double.NaN),new(sphere,MirrorAxis.X,double.PositiveInfinity),new(sphere,MirrorAxis.X,10_000_001),new(sphere,MirrorAxis.X,10_000_000)})
        {Check((await invalid.PreviewTransformAsync(bad,held.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid mirror accepted.");Same(invalid,held);}
        Check((await invalid.PreviewTransformAsync(new MirrorTransform(sphere,MirrorAxis.Y),Guid.NewGuid())).Outcome==Outcome.Conflict,"Stale token accepted.");Same(invalid,held);
        Check((await invalid.PreviewTransformAsync(new MirrorTransform(sphere,MirrorAxis.Y),held.Preview!.Id,expectedRevision:baseline.Revision-1)).Outcome==Outcome.Conflict,"Stale revision accepted.");Same(invalid,held);
        Check((await invalid.PreviewTransformAsync(new MirrorTransform(baseline.Scene.Objects[0].Id,MirrorAxis.Y),held.Preview!.Id)).Outcome==Outcome.Conflict,"Replacement changed targets.");Same(invalid,held);
        foreach(var stage in new[]{"Preparing transform","Mirroring vertices","Orienting mirrored faces","Transforming vertices","Ready to preview"})
        {
            using var ct=new CancellationTokenSource();var progress=new Callback(p=>{if(p.Stage==stage && (stage is not ("Mirroring vertices" or "Orienting mirrored faces" or "Transforming vertices") || p.Completed>=1024))ct.Cancel();});
            Check((await invalid.PreviewTransformAsync(new MirrorTransform(sphere,MirrorAxis.Y),held.Preview!.Id,ct.Token,progress)).Outcome==Outcome.Cancelled,"Mirror cancellation failed at "+stage);Same(invalid,held);
        }
        using(var ct=new CancellationTokenSource()){ct.Cancel();Check((await invalid.PreviewTransformAsync(new MirrorTransform(sphere,MirrorAxis.Y),held.Preview!.Id,ct.Token)).Outcome==Outcome.Cancelled,"Precancel accepted.");Same(invalid,held);}
        Check(invalid.ApplyPreview(Guid.NewGuid()).Outcome==Outcome.Conflict && invalid.Select(null).Outcome==Outcome.Conflict && invalid.Undo().Outcome==Outcome.Conflict,"Mirror preview allowed stale Apply/selection/history.");Same(invalid,held);
        Check((await invalid.SaveAsync(Path.Combine(output,"must-not-save.3mf"))).Outcome==Outcome.Conflict,"Mirror preview allowed Save.");Same(invalid,held);
        Cancel(invalid,held.Preview!.Id);Same(invalid,baseline);
        Ok(await invalid.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,3,4))));var foreign=invalid.Snapshot;
        Check((await invalid.PreviewTransformAsync(new MirrorTransform(sphere,MirrorAxis.X),foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Mirror took primitive preview.");Same(invalid,foreign);
        var thin=New();Ok(thin.Execute(new AddPrimitive(PrimitiveKind.Cube,new(1e-12,20,20))));var thinBefore=thin.Snapshot;
        Ok(await thin.PreviewTransformAsync(new MirrorTransform(thinBefore.SelectedObjectId!.Value,MirrorAxis.X)));var thinHeld=thin.Snapshot;
        Check((await thin.PreviewTransformAsync(new MirrorTransform(thinBefore.SelectedObjectId.Value,MirrorAxis.X,1e6),thinHeld.Preview!.Id)).Outcome==Outcome.Invalid,"Mirror collapsed a valid thin object.");Same(thin,thinHeld);Accepted(thin,thinBefore);
        Pass("Invalid axis/planes/result bounds, stale/foreign/retargeted requests, cancellation at vertex/face/bounds/publication stages and proved precision-collapse regression preserve accepted state and previous preview");

        var measured=await Load();var beforeMeasure=measured.Snapshot;
        var distance=measured.MeasureDistance(vertices[2],vertices[3],beforeMeasure.Revision);
        VecNear(distance.DeltaMillimeters,new(2,-19,34));Near(distance.DistanceMillimeters,39);
        Check(distance.FirstMillimeters==vertices[2] && distance.SecondMillimeters==vertices[3] && distance.CapturedRevision==beforeMeasure.Revision && distance.EditingGroupId==null,"Measurement metadata mismatch.");
        var reversed=measured.MeasureDistance(vertices[3],vertices[2]);Near(reversed.DistanceMillimeters,39);VecNear(reversed.DeltaMillimeters,new(-2,19,-34));
        Near(measured.MeasureDistance(vertices[0],vertices[0]).DistanceMillimeters,0);
        Check(measured.MeasureDistance(new(),new(double.Epsilon,0,0)).DistanceMillimeters==double.Epsilon,"Tiny distance underflowed.");
        Near(measured.MeasureDistance(new(-10_000_000,-10_000_000,-10_000_000),new(10_000_000,10_000_000,10_000_000)).DistanceMillimeters,20_000_000*Math.Sqrt(3));
        foreach(var p in new[]{new Vec3(double.NaN,0,0),new Vec3(0,double.PositiveInfinity,0),new Vec3(0,0,10_000_001)})Throws<ModelException>(()=>measured.MeasureDistance(p,new()));
        Throws<ModelException>(()=>measured.MeasureDistance(new(),new(),beforeMeasure.Revision-1));Throws<ModelException>(()=>measured.MeasureDistance(new(),new(),memberGroupId:Guid.NewGuid()));
        using(var ct=new CancellationTokenSource()){ct.Cancel();Throws<OperationCanceledException>(()=>measured.MeasureDistance(new(),new(),cancellation:ct.Token));}
        Same(measured,beforeMeasure);
        Ok(await measured.PreviewTransformAsync(new MirrorTransform(beforeMeasure.SelectedObjectId!.Value,MirrorAxis.Z)));var previewState=measured.Snapshot;
        Near(measured.MeasureDistance(vertices[2],vertices[3],previewState.Revision).DistanceMillimeters,39);Same(measured,previewState);
        var scope=await Grouped();Ok(scope.Session.EnterGroup(scope.Group,scope.Members[0]));var scopeState=scope.Session.Snapshot;
        Throws<ModelException>(()=>scope.Session.MeasureDistance(new(),new(),scopeState.Revision));
        var scoped=scope.Session.MeasureDistance(vertices[2],vertices[3],scopeState.Revision,scope.Group);Check(scoped.EditingGroupId==scope.Group,"Measurement lost scope.");Same(scope.Session,scopeState);
        Check((await scope.Session.PreviewTransformAsync(new MirrorTransform(scope.Members[0],MirrorAxis.Z))).Outcome==Outcome.Conflict,"Mirror ignored exact scope authority.");Same(scope.Session,scopeState);
        Ok(scope.Session.ExitGroup());Throws<ModelException>(()=>scope.Session.MeasureDistance(new(),new(),memberGroupId:scope.Group));
        Pass("Read-only point query: exact39mm/delta and metadata, zero/tiny/physical-bound distances, invalid/stale/scope/cancel errors, unchanged accepted and preview state");

        var busy=await Load();using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
        {
            var job=busy.PreviewTransformAsync(new MirrorTransform(busy.Snapshot.SelectedObjectId!.Value,MirrorAxis.X),progress:new Callback(p=>{if(p.Stage=="Preparing transform"){entered.Set();if(!release.Wait(10000))throw new IOException("Test barrier timed out.");}}));
            Check(entered.Wait(10000),"Mirror did not acquire busy lease.");var inFlight=busy.Snapshot;
            Near(busy.MeasureDistance(vertices[2],vertices[3],inFlight.Revision).DistanceMillimeters,39);Same(busy,inFlight);
            Check((await busy.PreviewTransformAsync(new MirrorTransform(inFlight.SelectedObjectId!.Value,MirrorAxis.Y))).Outcome==Outcome.Busy && busy.Select(null).Outcome==Outcome.Busy,"Mirror busy lease bypassed.");
            release.Set();Ok(await job);
        }
        Pass("Measurement stays available/read-only during a real mirror busy lease while concurrent edits and selection remain blocked");

        var solid=await Load(Path.Combine(output,"mirror-affine-x.3mf"));var solidBefore=solid.Snapshot;var source=solidBefore.Scene.Objects.Single();
        Ok(await solid.PreviewAsync(new SplitRequest(source.Id,new(0,0,1),source.Bounds.Center.Z)));
        Check(solid.Snapshot.Preview!.Scene.Objects.Length==2,"Mirrored imported solid failed native split.");foreach(var part in solid.Snapshot.Preview.Scene.Objects)Closed(part);
        Near(solid.Snapshot.Preview.Scene.Objects.Sum(CanonicalVolume),5304,2e-6);Cancel(solid,solid.Snapshot.Preview.Id);Same(solid,solidBefore);
        Pass("Mirrored negative-affine output passes actual native split with outward closed halves, conserved volume and cancellation preservation");
        File.WriteAllText(Path.Combine(output,"mirror-measurement-results.json"),JsonSerializer.Serialize(new {cases,scenarioCount=cases.Count,files,measurement=distance,
            runtime=Environment.Version.ToString(),coreSha256=Hash(typeof(EditorSession).Assembly.Location),workerSha256=Hash(worker),
            inputs=new[]{plain,affine,Path.Combine(input,"asymmetric-tetrahedron.stl"),Path.Combine(root,"fixtures/generated/inverted-cube.stl")}
                .Select(p=>new {path=Path.GetRelativePath(root,p),sha256=Hash(p)})},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static Vec3 Reflect(Vec3 p,MirrorAxis axis,double plane)=>axis switch {MirrorAxis.X=>p with {X=2*plane-p.X},MirrorAxis.Y=>p with {Y=2*plane-p.Y},_=>p with {Z=2*plane-p.Z}};
    private static double Determinant(Affine t){var c=Vec3.Cross(t.X,t.Y);return c.X*t.Z.X+c.Y*t.Z.Y+c.Z*t.Z.Z;}
    private static double CanonicalVolume(SceneObject obj)
    {
        var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);var volume=obj.Mesh.Triangles.Sum(t=>{
            var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin;var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin;var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin;var cross=Vec3.Cross(b,c);
            return (a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z)/6;});
        return Determinant(obj.Transform)<0 ? -volume : volume;
    }
    private static void Closed(SceneObject obj)
    {
        var welded=new Dictionary<Vec3,int>();var edges=new Dictionary<(int,int),(int Count,int Direction)>();
        int Vertex(Vec3 p){if(!welded.TryGetValue(p,out var n)){n=welded.Count;welded.Add(p,n);}return n;}
        foreach(var face in obj.Mesh.Triangles)
        {
            var a=obj.Transform.Apply(obj.Mesh.Vertices[face.A]);var b=obj.Transform.Apply(obj.Mesh.Vertices[face.B]);var c=obj.Transform.Apply(obj.Mesh.Vertices[face.C]);
            Check(Vec3.Cross(b-a,c-a)!=new Vec3(),"Mirrored face degenerated.");var ids=new[]{Vertex(a),Vertex(b),Vertex(c)};if(Determinant(obj.Transform)<0)(ids[1],ids[2])=(ids[2],ids[1]);
            foreach(var (x,y) in new[]{(ids[0],ids[1]),(ids[1],ids[2]),(ids[2],ids[0])}){var key=(Math.Min(x,y),Math.Max(x,y));var e=edges.GetValueOrDefault(key);edges[key]=(e.Count+1,e.Direction+(x<y?1:-1));}
        }
        Check(edges.Values.All(e=>e.Count==2 && e.Direction==0) && CanonicalVolume(obj)>0,"Output is open, nonmanifold, inconsistent or inward.");
    }
    private static void PointSets(IEnumerable<Vec3> expected,IEnumerable<Vec3> actual,double tolerance=1e-9)
    {
        var a=expected.Distinct().ToArray();var b=actual.Distinct().ToArray();Check(a.Length==b.Length,"World point-set count differs.");
        foreach(var p in a)Check(b.Any(q=>Math.Abs(p.X-q.X)<=tolerance && Math.Abs(p.Y-q.Y)<=tolerance && Math.Abs(p.Z-q.Z)<=tolerance),"World point set differs.");
    }
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Cancel(EditorSession s,Guid id)=>Check(s.DiscardPreview(id).Outcome==Outcome.Cancelled,"Discard failed.");
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Near(double a,double b,double relative=1e-10)=>Check(Math.Abs(a-b)<=relative*Math.Max(1,Math.Abs(b)),$"Expected{b:G17}, got{a:G17}.");
    private static void VecNear(Vec3 a,Vec3 b){Near(a.X,b.X);Near(a.Y,b.Y);Near(a.Z,b.Z);}
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Operation changed full session state.");
    private static void Accepted(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot with {Preview=before.Preview}==before,"Preview mutated accepted state.");
    private static void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
    private static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
    private sealed class Callback(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
}
