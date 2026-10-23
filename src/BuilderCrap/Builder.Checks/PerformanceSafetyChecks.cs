using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class PerformanceSafetyChecks
{
    public static async Task Run(string root,bool includeLimitFixtures=false)
    {
        var output=Path.Combine(root,".local/evidence/performance-20261019/safety"); Directory.CreateDirectory(output);
        var cases=new List<string>();
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        var mesh=Tetra(); var id=Guid.NewGuid();
        var obj=new SceneObject(id,"Asymmetric tetrahedron",mesh,Affine.Identity); var original=obj.Bounds;
        Bounds(original,OracleBounds([obj]));
        var transformations=new[]{Affine.Translation(new(83,-27,14)),Affine.RotateAbout(new(37,-19,73),new(3,4,2)),
            Affine.ScaleAbout(new(2,.35,1.6),new(-3,4,8)),new Affine(new(-1,.2,0),new(.1,2,.3),new(0,.2,.5),new(20,-10,7)),
            new Affine(new(1,0,0),new(0,1,0),new(0,0,0),new(4,5,6))};
        foreach(var transform in transformations)
        {
            var clone=obj with {Transform=transform}; Bounds(clone.Bounds,OracleBounds([clone])); Bounds(obj.Bounds,original);
            var reconstructed=new SceneObject(id,obj.Name,obj.Mesh,transform); Bounds(reconstructed.Bounds,clone.Bounds);
            var renamed=clone with {Name="Same geometry"}; Bounds(renamed.Bounds,clone.Bounds);
        }
        var changed=obj with {Mesh=new Mesh(mesh.Vertices.Select(p=>p+new Vec3(-11,8,21)),mesh.Triangles)};
        Bounds(changed.Bounds,OracleBounds([changed])); Bounds(obj.Bounds,original);
        var independent=new Mesh(mesh.Vertices,mesh.Triangles); var sameData=obj with {Mesh=independent}; Bounds(sameData.Bounds,original);
        Pass("Bounds caches distinguish immutable mesh identity, same-ID object reconstruction, record-cloned transforms/meshes, rotations/shear/reflection/singular transforms and arbitrary tetrahedral vertex extrema");

        var second=obj with {Id=Guid.NewGuid(),Name="Second",Transform=Affine.Translation(new(35,20,-10))};
        var third=obj with {Id=Guid.NewGuid(),Name="Outside",Transform=Affine.Translation(new(-50,60,30))};
        var group=new SceneGroup(Guid.NewGuid(),"Cache pair",[obj.Id,second.Id]);
        var scene=new Scene(Guid.NewGuid(),[obj,second,third]){Groups=[group]}; scene.Validate();
        var sceneBounds=scene.Bounds; Bounds(sceneBounds,OracleBounds(scene.Objects));
        var moved=second with {Transform=second.Transform.Then(Affine.Translation(new(200,0,0)))};
        var cloneScene=scene with {Objects=[obj,moved,third]}; Bounds(cloneScene.Bounds,OracleBounds(cloneScene.Objects));
        Bounds(scene.Bounds,sceneBounds); Check(cloneScene.ContentId==scene.ContentId,"Direct record clone unexpectedly changed content identity.");
        var changedScene=scene.WithObjects([obj,changed with {Id=second.Id},third]); Bounds(changedScene.Bounds,OracleBounds(changedScene.Objects));
        var metadataOnly=scene with {Groups=[]}; Bounds(metadataOnly.Bounds,sceneBounds);
        var groupUnits=cloneScene.GetSelectionUnits([second.Id]); Check(groupUnits.Length==1 && groupUnits[0].Id==group.Id,"Group query lost its current membership.");
        Bounds(groupUnits[0].Bounds,OracleBounds([obj,moved]));
        var memberUnits=cloneScene.GetSelectionUnits([second.Id,obj.Id],group.Id); Check(memberUnits.Length==2,"Member scope reused whole-group units.");
        Bounds(memberUnits[0].Bounds,OracleBounds([obj])); Bounds(memberUnits[1].Bounds,OracleBounds([moved]));
        var regrouped=cloneScene with {Groups=[group with {ObjectIds=[second.Id,third.Id],Name="Changed membership"}]};
        var regroupedUnits=regrouped.GetSelectionUnits([second.Id]); Bounds(regroupedUnits.Single().Bounds,OracleBounds([moved,third]));
        Check(regroupedUnits.Single().Name=="Changed membership","Group query reused cached metadata after record clone.");
        Check(metadataOnly.GetSelectionUnits([second.Id]).Single().Id==second.Id,"Ungrouped clone reused group selection units.");
        foreach(var invalid in new[]{scene with {Objects=[obj,obj]},scene with {Groups=default},scene with {Groups=[group with {ObjectIds=[obj.Id]}]},
            scene with {Groups=[group with {ObjectIds=[obj.Id,Guid.NewGuid()]}]},scene with {Objects=[obj with {Transform=Affine.Translation(new(10_000_001,0,0))},second,third]}})
        {
            Throws<ModelException>(()=>invalid.Validate(),"Record clone reused a successful cached validation despite changed invalid objects/groups.");
            Throws<ModelException>(()=>invalid.Validate(),"A failed validation was cached as success.");
        }
        using(var ct=new CancellationTokenSource()){ct.Cancel();Throws<OperationCanceledException>(()=>scene.Validate(ct.Token),"Cached validation ignored cancellation.");}
        Pass("Scene record clones retaining ContentId, WithObjects, group metadata/membership changes and scene/member selection scopes return current exact bounds while originals remain unchanged");

        var rayObjects=new[]{obj,
            obj with {Id=Guid.NewGuid(),Transform=Affine.RotateAbout(new(23,51,-37),new(4,3,2)).Then(Affine.Translation(new(20,-5,9)))},
            obj with {Id=Guid.NewGuid(),Transform=new(new(-1.5,.1,0),new(.2,.75,0),new(0,.3,2),new(-20,12,-7))},
            obj with {Id=Guid.NewGuid(),Transform=new(new(1,0,0),new(0,1,0),new(0,0,0),new(40,0,10))}};
        var rayScene=new Scene(Guid.NewGuid(),rayObjects.ToImmutableArray());
        var rayCount=0;
        foreach(var target in rayObjects)
        foreach(var triangle in target.Mesh.Triangles)
        {
            var a=target.Transform.Apply(target.Mesh.Vertices[triangle.A]); var b=target.Transform.Apply(target.Mesh.Vertices[triangle.B]); var c=target.Transform.Apply(target.Mesh.Vertices[triangle.C]);
            var normal=Vec3.Cross(b-a,c-a); if(Dot(normal,normal)==0) continue; normal=Unit(normal);
            var point=a*.23+b*.31+c*.46;
            foreach(var sign in new[]{-1d,1d})
            foreach(var sides in Enum.GetValues<RayPickSides>())
            {
                var origin=point+normal*(sign*100); var direction=normal*(-sign);
                CompareHit(rayScene.PickRay(origin,direction,sides:sides),OracleRay(rayScene,origin,direction,sides:sides)); rayCount++;
                CompareHit(rayScene.PickRay(origin,direction*1e300,sides:sides),OracleRay(rayScene,origin,direction,sides:sides)); rayCount++;
                CompareHit(rayScene.PickRay(origin,direction*1e-300,sides:sides),OracleRay(rayScene,origin,direction,sides:sides)); rayCount++;
            }
        }
        var random=new Random(8675309);
        for(var i=0;i<300;i++)
        {
            var origin=new Vec3(random.NextDouble()*120-40,random.NextDouble()*70-20,random.NextDouble()*70-20);
            var aim=rayObjects[i%rayObjects.Length].Bounds.Center; var direction=aim-origin;
            foreach(var sides in Enum.GetValues<RayPickSides>())
            {CompareHit(rayScene.PickRay(origin,direction,sides:sides),OracleRay(rayScene,origin,direction,sides:sides));rayCount++;}
        }
        Bounds(rayScene.Bounds,OracleBounds(rayScene.Objects));
        Pass($"{rayCount} deterministic plane/edge-function oracle comparisons match nearest ray hits across asymmetric tetrahedra, affine shear/nonuniform rotation/reflection, singular planar survivors, front/back/both sides and huge/tiny direction normalization");

        var planar=new SceneObject(Guid.NewGuid(),"Boundary triangle",new Mesh([new(0,0,0),new(30,0,0),new(0,30,0)],[new(0,1,2)]),Affine.Identity);
        var tie=planar with {Id=Guid.NewGuid(),Name="Exact tie"}; var boundaryScene=new Scene(Guid.NewGuid(),[planar,tie]);
        foreach(var point in new[]{new Vec3(0,0,0),new(15,0,0),new(15,15,0),new(0,30,0),new(7,8,0)})
        {
            var h=boundaryScene.PickRay(point+new Vec3(0,0,10),new(0,0,-2));
            Check(h?.ObjectId==planar.Id && h.TriangleIndex==0,"Boundary/tie ray lost deterministic first object/triangle."); CompareHit(h,OracleRay(boundaryScene,point+new Vec3(0,0,10),new(0,0,-2)));
        }
        var nearOrigin=new Vec3(2,2,1e-7); var nearDirection=new Vec3(1,0,-1e-8);
        CompareHit(boundaryScene.PickRay(nearOrigin,nearDirection),OracleRay(boundaryScene,nearOrigin,nearDirection));
        var rangeOrigin=new Vec3(5,5,10); var down=new Vec3(0,0,-1);
        Check(boundaryScene.PickRay(rangeOrigin,down,10,10)?.ObjectId==planar.Id,"Inclusive exact distance range rejected a boundary hit.");
        Check(boundaryScene.PickRay(rangeOrigin,down,0,9.999)==null && boundaryScene.PickRay(rangeOrigin,down,10.001)==null,"Distance range admitted an excluded hit.");
        Check(boundaryScene.PickRay(new(31,31,10),down)==null && boundaryScene.PickRay(new(5,5,0),new(1,0,0))==null,"Miss/parallel ray produced a hit.");
        foreach(var bad in new Action[]{()=>boundaryScene.PickRay(new(double.NaN,0,0),down),()=>boundaryScene.PickRay(rangeOrigin,new()),()=>boundaryScene.PickRay(rangeOrigin,down,-1),()=>boundaryScene.PickRay(rangeOrigin,down,5,4),()=>boundaryScene.PickRay(rangeOrigin,down,sides:(RayPickSides)99),()=>boundaryScene.PickRay(rangeOrigin,down,memberGroupId:Guid.NewGuid())})
            Throws<ModelException>(bad,"Invalid ray/query scope was accepted.");
        using(var ct=new CancellationTokenSource()){ct.Cancel();Throws<OperationCanceledException>(()=>boundaryScene.PickRay(rangeOrigin,down,cancellation:ct.Token),"Pre-cancelled pure query completed.");}
        var collapsed=planar with {Transform=new(new(1,0,0),new(0,0,0),new(0,0,0),new())};
        Check(new Scene(Guid.NewGuid(),[collapsed]).PickRay(rangeOrigin,down)==null,"Fully collapsed triangle remained pickable.");
        var shiftedBoundary=boundaryScene with {Objects=[planar with {Transform=Affine.Translation(new(100,0,0))}]};
        Check(shiftedBoundary.PickRay(rangeOrigin,down)==null && shiftedBoundary.PickRay(rangeOrigin+new Vec3(100,0,0),down)!=null,"Cloned scene reused stale picking bounds.");
        Pass("Ray boundary vertices/edges, near-parallel intersections, inclusive mm ranges, exact tie order, misses/parallel/collapsed faces, invalid inputs/scopes and pre-cancellation behave deterministically; cloned scene picking invalidates old bounds");

        await SessionRayLifetime();
        if(includeLimitFixtures) await LimitsAndCancellation();
        File.WriteAllText(Path.Combine(output,includeLimitFixtures ? "performance-results.json" : "results.json"),JsonSerializer.Serialize(new{
            utc=DateTimeOffset.UtcNow,runtime=Environment.Version.ToString(),coreSha256=Hash(typeof(EditorSession).Assembly.Location),includeLimitFixtures,cases,
            limitations=new[]{"Public Core API and synthetic mesh checks; no UI or timing claim.","Performance-limit fixtures require scripts/generate_fixtures.py --performance."}},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} performance-safety scenario groups passed. Evidence: {output}");

        async Task LimitsAndCancellation()
        {
            var inputs=Path.Combine(root,".local/evidence/performance-20261019/inputs");
            Check(File.Exists(Path.Combine(inputs,"manifest.json")),"Generate opt-in fixtures with python scripts/generate_fixtures.py --performance first.");
            using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(inputs,"manifest.json")));
            foreach(var input in manifest.RootElement.GetProperty("original_inputs").EnumerateArray())
                Check(Hash(Path.Combine(root,input.GetProperty("file").GetString()!)).Equals(input.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase),"An original input hash changed.");
            foreach(var input in manifest.RootElement.GetProperty("cases").EnumerateArray())
                Check(Hash(Path.Combine(inputs,input.GetProperty("file").GetString()!)).Equals(input.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase),"A performance input hash changed.");
            foreach(var entry in new[]{("dense-100000.stl",1,100000,300000,100d,0),("objects-1000.3mf",1000,12000,8000,145d,0),("combined-1000-100000.3mf",1000,100000,52000,145d,1)})
            {
                var s=new EditorSession(); Ok(await s.OpenAsync(Path.Combine(inputs,entry.Item1))); var snapshot=s.Snapshot;
                Check(snapshot.Scene.Objects.Length==entry.Item2 && snapshot.Scene.TriangleCount==entry.Item3 && snapshot.Scene.Objects.Sum(o=>o.Mesh.Vertices.Length)==entry.Item4 && snapshot.Scene.Groups.Length==entry.Item6,"Limit fixture counts differ: "+entry.Item1);
                Bounds(snapshot.Scene.Bounds,new(new(0,0,0),new(entry.Item5,entry.Item5,entry.Item5)));
                Check(snapshot.Scene.Objects.Select(o=>o.Mesh).Distinct(ReferenceEqualityComparer.Instance).Count()==(entry.Item6==1 ? 2 : 1),"Shared resource meshes were expanded unnecessarily.");
                if(entry.Item6==1) Check(snapshot.Scene.Groups.Single().Name=="Performance assembly" && snapshot.Scene.Groups.Single().ObjectIds.Length==1000,"Combined limit group lost its members/name.");
                var before=s.Snapshot;
                foreach(var bad in new[]{"over-triangles-100001.stl","over-objects-1001.3mf","truncated-dense.stl"})
                {Check((await s.OpenAsync(Path.Combine(inputs,bad))).Outcome==Outcome.Invalid,"Outside-limit/malformed input accepted: "+bad); Same(s,before);}
            }
            Pass("Exact 100000-face/300000-vertex, 1000-object and combined-limit fixtures open with shared meshes/group; one-over limits and truncated dense input preserve each accepted document");

            var densePath=Path.Combine(inputs,"dense-100000.stl"); var editor=new EditorSession(); Ok(editor.Execute(new AddCube())); var small=editor.Snapshot;
            foreach(var stage in new[]{"before","Reading triangles","Ready to open"})
            {
                using var ct=new CancellationTokenSource(); var reached=stage=="before"; if(reached) ct.Cancel();
                var result=await editor.OpenAsync(densePath,cancellation:ct.Token,progress:new Inline(p=>{if(p.Stage==stage && (stage!="Reading triangles" || p.Completed>=1024)){reached=true;ct.Cancel();}}));
                Check(reached && result.Outcome==Outcome.Cancelled,"Dense Open cancellation failed at "+stage); Same(editor,small);
            }
            Ok(await editor.OpenAsync(densePath)); var denseBefore=editor.Snapshot; var target=denseBefore.Scene.Objects[0].Id;
            Ok(await editor.PreviewTransformAsync(new MoveTransform(target,new(1,2,3)))); var prior=editor.Snapshot;
            foreach(var stage in new[]{"before","Transforming vertices","Ready to preview"})
            {
                using var ct=new CancellationTokenSource(); var reached=stage=="before"; if(reached) ct.Cancel();
                var result=await editor.PreviewTransformAsync(new RotateTransform(target,new(11,27,43)),prior.Preview!.Id,ct.Token,new Inline(p=>{if(p.Stage==stage && (stage!="Transforming vertices" || p.Completed>=1024)){reached=true;ct.Cancel();}}));
                Check(reached && result.Outcome==Outcome.Cancelled,"Dense replacement cancellation failed at "+stage); Same(editor,prior);
            }
            Check(editor.DiscardPreview(prior.Preview!.Id).Outcome==Outcome.Cancelled,"Dense discard failed."); Same(editor,denseBefore);
            Pass("Dense-limit Open and transform replacement cancellation before/mid/late work preserve full accepted snapshot and the exact prior candidate");

            var shared=denseBefore.Scene.Objects[0].Mesh;
            for(var i=0;i<12;i++) {Ok(await editor.PreviewTransformAsync(new MoveTransform(target,new(1,0,0)))); Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id)); Check(ReferenceEquals(shared,editor.Snapshot.Scene.Objects[0].Mesh),"Large history duplicated a mesh.");}
            var final=editor.Snapshot; Bounds(final.Scene.Bounds,new(new(12,0,0),new(112,100,100)));
            for(var i=0;i<12;i++) Ok(editor.Undo()); Check(editor.Snapshot.Scene==denseBefore.Scene && !editor.Snapshot.IsDirty,"Large Undo chain failed to return to clean source.");
            for(var i=0;i<12;i++) Ok(editor.Redo()); Check(editor.Snapshot.Scene==final.Scene && editor.Snapshot.Scene.Objects[0].Id==target && ReferenceEquals(shared,editor.Snapshot.Scene.Objects[0].Mesh),"Large Redo chain lost mesh/identity/final scene.");
            var specimens=Path.Combine(output,"outputs");Directory.CreateDirectory(specimens);
            var denseNative=Path.Combine(specimens,"dense-moved.3mf");Ok(await editor.SaveAsync(denseNative));
            var saved=editor.Snapshot;Ok(await editor.ExportStlAsync(Path.Combine(specimens,"dense-moved.stl")));Same(editor,saved);
            var lastGood=File.ReadAllBytes(denseNative);
            Ok(await editor.PreviewTransformAsync(new MoveTransform(target,new(1,0,0))));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));var dirty=editor.Snapshot;
            using(var ct=new CancellationTokenSource())
            {
                var reached=false;var cancelled=await editor.SaveAsync(denseNative,ct.Token,new Inline(p=>{if(p.Stage=="Ready to save"){reached=true;ct.Cancel();}}));
                Check(reached && cancelled.Outcome==Outcome.Cancelled,"Dense pre-replace save cancellation was not exercised.");Same(editor,dirty);
                Check(File.ReadAllBytes(denseNative).SequenceEqual(lastGood) && !Directory.EnumerateFiles(specimens,"*.tmp").Any(),"Dense save cancellation changed last-good bytes or left a temp file.");
            }
            Ok(editor.Undo());Check(editor.Snapshot.Scene==saved.Scene && !editor.Snapshot.IsDirty,"Cancelled dense-save Undo lost saved boundary.");
            var combined=new EditorSession();Ok(await combined.OpenAsync(Path.Combine(inputs,"combined-1000-100000.3mf")));
            var combinedBefore=combined.Snapshot;var active=combinedBefore.Scene.Groups.Single();var member=active.ObjectIds[0];Ok(combined.EnterGroup(active.Id,member));
            Ok(await combined.PreviewTransformAsync(new MoveTransform(member,new(2,3,4)){MemberGroupId=active.Id}));Ok(combined.ApplyPreview(combined.Snapshot.Preview!.Id));
            Check(combined.Snapshot.Scene.TriangleCount==100000 && combined.Snapshot.Scene.Objects.Length==1000 && combined.Snapshot.Scene.Groups.Single().ObjectIds.SequenceEqual(active.ObjectIds),"Combined member edit altered cap counts/group membership.");
            foreach(var sibling in combinedBefore.Scene.Objects.Skip(1))Check(ReferenceEquals(sibling,combined.Snapshot.Scene.Objects.Single(o=>o.Id==sibling.Id)),"Combined-cap member edit replaced a sibling.");
            Ok(await combined.SaveAsync(Path.Combine(specimens,"combined-member-moved.3mf")));
            File.WriteAllText(Path.Combine(specimens,"manifest.json"),JsonSerializer.Serialize(new{expectations=new[]{"dense-moved.3mf/.stl: one 100000-triangle box, bounds(12,0,0)..(112,100,100), volume1000000mm3; original dense vertices translated exactly12X",
                "combined-member-moved.3mf:1000objects/100000triangles,groupPerformance assembly with1000members; grid10^3 stride15,10mmcube each; firstmember additionally translated(2,3,4); totalbounds(0,0,0)..(145,145,145),summedvolume1000000mm3"},
                files=Directory.GetFiles(specimens).Where(p=>Path.GetExtension(p) is ".3mf" or ".stl").Select(p=>new{file=Path.GetFileName(p),sha256=Hash(p)})},new JsonSerializerOptions{WriteIndented=true}));
            Pass("Twelve dense-limit transforms retain mesh/identity and exact Undo/Redo; dense native/STL and combined-cap member output specimens save, with dense pre-replace cancellation preserving last-good bytes");
        }

        async Task SessionRayLifetime()
        {
            var fixture=Path.Combine(output,"ray-occlusion.3mf");
            WriteRayFixture(fixture);
            var s=new EditorSession(); Ok(await s.OpenAsync(fixture)); var before=s.Snapshot; var leaves=before.Scene.Objects;
            var origin=new Vec3(3,3,0); var direction=new Vec3(0,0,1);
            var normalHit=await s.PickAsync(origin,direction); CompareHit(normalHit,OracleRay(before.Scene,origin,direction));
            Check(normalHit?.ObjectId==leaves[2].Id && normalHit.IsSelectable && normalHit.CapturedRevision==before.Revision && normalHit.EditingGroupId is null,"Scene query did not return nearest outside leaf/captured revision."); Same(s,before);
            var scope=before.Scene.Groups.Single().Id; Ok(s.EnterGroup(scope,leaves[0].Id)); var entered=s.Snapshot;
            var scopedHit=await s.PickAsync(origin,direction); Check(scopedHit?.ObjectId==leaves[2].Id && !scopedHit.IsSelectable && scopedHit.EditingGroupId==scope && scopedHit.CapturedRevision==entered.Revision,"Outside leaf stopped occluding the active group."); Same(s,entered);
            Ok(await s.PreviewTransformAsync(new MoveTransform(leaves[0].Id,new(100,0,0)){MemberGroupId=scope})); var held=s.Snapshot;
            CompareHit(await s.PickAsync(origin,direction,minimumDistance:6),OracleRay(held.Scene,origin,direction,minimum:6)); Same(s,held);
            using(var ct=new CancellationTokenSource())
            {ct.Cancel();await ThrowsAsync<OperationCanceledException>(()=>s.PickAsync(origin,direction,cancellation:ct.Token),"Cancelled session query completed.");Same(s,held);}
            await ThrowsAsync<ModelException>(()=>s.PickAsync(origin,new()),"Invalid session ray completed.");Same(s,held);
            Check(s.DiscardPreview(held.Preview!.Id).Outcome==Outcome.Cancelled,"Query lifetime discard failed.");Same(s,entered);
            using(var barrier=new ManualResetEventSlim()) using(var release=new ManualResetEventSlim()) using(var ct=new CancellationTokenSource())
            {
                var edit=s.DuplicateSelectionAsync([leaves[0].Id],ct.Token,new Inline(p=>{if(p.Stage=="Ready to duplicate"){barrier.Set();Check(release.Wait(TimeSpan.FromSeconds(15)),"Busy query barrier timed out.");}}),memberGroupId:scope);
                try
                {
                    Check(barrier.Wait(TimeSpan.FromSeconds(10)),"Busy query lease not acquired.");var busy=s.Snapshot;
                    var hit=await s.PickAsync(origin,direction);Check(hit?.ObjectId==leaves[2].Id && !hit.IsSelectable && busy.IsBusy,"Read-only query failed while edit owns the job lease.");Same(s,busy);
                }
                finally{ct.Cancel();release.Set();}
                Check((await edit).Outcome==Outcome.Cancelled,"Busy query edit did not cancel.");Same(s,entered);
            }
            var pending=s.PickAsync(origin,direction); Ok(s.ExitGroup());
            var captured=await pending; Check(captured?.EditingGroupId==scope && captured.CapturedRevision==entered.Revision && !captured.IsSelectable,"Concurrent scope change altered captured query semantics.");
            Pass("Session picking reports captured revision/scope, keeps outside-group occluders unselectable, queries accepted geometry during previews, and preserves exact preview/state on invalid/cancelled queries or concurrent scope changes");
        }
    }
    private static Mesh Tetra()=>new([new(0,0,0),new(13,1,0),new(2,9,1),new(3,2,7)],[new(0,2,1),new(0,1,3),new(1,2,3),new(2,0,3)]);
    private static Bounds3 OracleBounds(IEnumerable<SceneObject> objects)
    {
        var points=objects.SelectMany(o=>o.Mesh.Vertices.Select(p=>o.Transform.Apply(p))).ToArray();
        return points.Length==0 ? new(new(),new()) : new(new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)));
    }
    private static void Bounds(Bounds3 a,Bounds3 b){Near(a.Min.X,b.Min.X);Near(a.Min.Y,b.Min.Y);Near(a.Min.Z,b.Min.Z);Near(a.Max.X,b.Max.X);Near(a.Max.Y,b.Max.Y);Near(a.Max.Z,b.Max.Z);}
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-7,$"Expected {b}, got {a}.");
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Same(EditorSession s,EditorSnapshot prior)=>Check(s.Snapshot==prior,"Performance safety operation changed accepted snapshot/prior preview.");
    private static void Check(bool b,string message){if(!b)throw new InvalidOperationException(message);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private sealed class Inline(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static Vec3 Unit(Vec3 v){var m=Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)));var scaled=new Vec3(v.X/m,v.Y/m,v.Z/m);return scaled*(1/Math.Sqrt(Dot(scaled,scaled)));}
    private static RayPickHit? OracleRay(Scene scene,Vec3 origin,Vec3 direction,double minimum=0,double maximum=double.PositiveInfinity,RayPickSides sides=RayPickSides.Both)
    {
        direction=Unit(direction); RayPickHit? best=null;
        foreach(var obj in scene.Objects)
        for(var i=0;i<obj.Mesh.Triangles.Length;i++)
        {
            var t=obj.Mesh.Triangles[i]; var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A]);var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B]);var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C]);
            var normal=Vec3.Cross(b-a,c-a); var denominator=Dot(normal,direction);
            if(denominator==0 || sides==RayPickSides.Front && denominator>0 || sides==RayPickSides.Back && denominator<0)continue;
            var distance=Dot(normal,a-origin)/denominator;if(distance<minimum || distance>maximum || best is not null && distance>=best.Distance)continue;
            var point=origin+direction*distance;
            var drop=Math.Abs(normal.X)>=Math.Abs(normal.Y) && Math.Abs(normal.X)>=Math.Abs(normal.Z) ? 0 : Math.Abs(normal.Y)>=Math.Abs(normal.Z) ? 1 : 2;
            (double X,double Y) Project(Vec3 p)=>drop==0 ? (p.Y,p.Z) : drop==1 ? (p.Z,p.X) : (p.X,p.Y);
            var pa=Project(a);var pb=Project(b);var pc=Project(c);var pp=Project(point);
            static double Edge((double X,double Y) u,(double X,double Y) v,(double X,double Y) p)=>(v.X-u.X)*(p.Y-u.Y)-(v.Y-u.Y)*(p.X-u.X);
            var area=Edge(pa,pb,pc);if(area==0)continue;var sign=Math.Sign(area);var epsilon=Math.Abs(area)*1e-10;
            if(Edge(pa,pb,pp)*sign < -epsilon || Edge(pb,pc,pp)*sign < -epsilon || Edge(pc,pa,pp)*sign < -epsilon)continue;
            best=new(obj.Id,point,distance,i,true);
        }
        return best;
    }
    private static void CompareHit(RayPickHit? actual,RayPickHit? expected)
    {
        Check((actual is null)==(expected is null),"Ray hit presence differs from independent plane/edge oracle.");if(actual is null || expected is null)return;
        Check(actual.ObjectId==expected.ObjectId,"Nearest ray leaf differs from oracle.");Near(actual.Distance,expected.Distance);
        Near(actual.Position.X,expected.Position.X);Near(actual.Position.Y,expected.Position.Y);Near(actual.Position.Z,expected.Position.Z);
    }
    private static void Throws<T>(Action action,string message) where T:Exception {try{action();}catch(T){return;}throw new InvalidOperationException(message);}
    private static async Task ThrowsAsync<T>(Func<Task> action,string message) where T:Exception {try{await action();}catch(T){return;}throw new InvalidOperationException(message);}
    private static void WriteRayFixture(string path)
    {
        var tetra=Tetra();var objects=new[]{(tetra,15),(tetra,30),(new Mesh([new(0,0,0),new(30,0,0),new(0,30,0)],[new(0,1,2)]),5)};
        System.Xml.Linq.XNamespace ns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
        var resources=new System.Xml.Linq.XElement(ns+"resources");
        for(var i=0;i<objects.Length;i++)
        {
            var mesh=objects[i].Item1;
            resources.Add(new System.Xml.Linq.XElement(ns+"object",new System.Xml.Linq.XAttribute("id",i+1),new System.Xml.Linq.XAttribute("type","model"),new System.Xml.Linq.XElement(ns+"mesh",
                new System.Xml.Linq.XElement(ns+"vertices",mesh.Vertices.Select(v=>new System.Xml.Linq.XElement(ns+"vertex",new System.Xml.Linq.XAttribute("x",v.X),new System.Xml.Linq.XAttribute("y",v.Y),new System.Xml.Linq.XAttribute("z",v.Z)))),
                new System.Xml.Linq.XElement(ns+"triangles",mesh.Triangles.Select(t=>new System.Xml.Linq.XElement(ns+"triangle",new System.Xml.Linq.XAttribute("v1",t.A),new System.Xml.Linq.XAttribute("v2",t.B),new System.Xml.Linq.XAttribute("v3",t.C)))))));
        }
        resources.Add(new System.Xml.Linq.XElement(ns+"object",new System.Xml.Linq.XAttribute("id",4),new System.Xml.Linq.XAttribute("type","model"),new System.Xml.Linq.XAttribute("name","Ray pair"),
            new System.Xml.Linq.XElement(ns+"components",Enumerable.Range(0,2).Select(i=>new System.Xml.Linq.XElement(ns+"component",new System.Xml.Linq.XAttribute("objectid",i+1),new System.Xml.Linq.XAttribute("transform",$"1 0 0 0 1 0 0 0 1 0 0 {objects[i].Item2}"))))));
        var model=new System.Xml.Linq.XElement(ns+"model",new System.Xml.Linq.XAttribute("unit","millimeter"),resources,new System.Xml.Linq.XElement(ns+"build",
            new System.Xml.Linq.XElement(ns+"item",new System.Xml.Linq.XAttribute("objectid",4)),new System.Xml.Linq.XElement(ns+"item",new System.Xml.Linq.XAttribute("objectid",3),new System.Xml.Linq.XAttribute("transform","1 0 0 0 1 0 0 0 1 0 0 5"))));
        using var stream=new FileStream(path,FileMode.Create,FileAccess.Write);
        using var archive=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create);
        using(var writer=new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open()))writer.Write(model.ToString());
        using(var writer=new StreamWriter(archive.CreateEntry("_rels/.rels").Open()))writer.Write("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");
        using(var writer=new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()))writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
    }
}
