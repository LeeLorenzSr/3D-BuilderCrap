using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class EditingChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/overnight-20261018/editing-checks");
        Directory.CreateDirectory(output);
        var cases=new List<string>(); var measurements=new List<object>();
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        EditorSession New()=>new(worker);
        string Fixture(string name)=>Path.Combine(root,"fixtures/generated",name);
        void Pass(string name) { cases.Add(name); Console.WriteLine("PASS "+name); }
        var dimensions=new Vec3(18,26,34);
        foreach(var kind in new[] {PrimitiveKind.Cube,PrimitiveKind.Cylinder,PrimitiveKind.Sphere})
        {
            var s=New(); var empty=s.Snapshot;
            Ok(s.Execute(new AddPrimitive(kind,dimensions),empty.Revision));
            var added=s.Snapshot; var obj=added.Scene.Objects.Single();
            Check(added.SelectedObjectId==obj.Id && added.Revision==empty.Revision+1 && added.IsDirty,"Primitive did not commit/select once.");
            Bounds(obj.Bounds,new(),dimensions); ClosedOutward(obj);
            var measured=Volume(obj);
            var analytic=kind switch {
                PrimitiveKind.Cube=>dimensions.X*dimensions.Y*dimensions.Z,
                PrimitiveKind.Cylinder=>Math.PI*dimensions.X*dimensions.Y*dimensions.Z/4,
                _=>Math.PI*dimensions.X*dimensions.Y*dimensions.Z/6 };
            var relativeError=Math.Abs(measured-analytic)/analytic;
            // Inscribed fixed tessellation: cylinder error <0.3%; ellipsoid error <1%.
            var tolerance=kind switch { PrimitiveKind.Cube=>1e-12,PrimitiveKind.Cylinder=>0.003,_=>0.01 };
            Check(relativeError<tolerance && measured<=analytic*(1+1e-12),"Unexpected primitive volume approximation: "+kind);
            Ok(s.Undo()); Check(s.Snapshot.Scene==empty.Scene,"Primitive Undo did not restore exact empty document.");
            Ok(s.Redo()); Check(s.Snapshot.Scene==added.Scene,"Primitive Redo changed mesh or identity.");
            var file=Path.Combine(output,kind.ToString().ToLowerInvariant()+".3mf");
            Ok(await s.SaveAsync(file)); Ok(await s.ExportStlAsync(Path.ChangeExtension(file,".stl")));
            var reopened=New(); Ok(await reopened.OpenAsync(file));
            var reopenedObject=reopened.Snapshot.Scene.Objects.Single(); Bounds(reopenedObject.Bounds,new(),dimensions);
            Near(Volume(reopenedObject),measured); ClosedOutward(reopenedObject);
            Ok(await reopened.PreviewAsync(new SplitRequest(reopenedObject.Id,new(0,0,1),dimensions.Z/2)));
            var preview=reopened.Snapshot.Preview!;
            Check(preview.Scene.Objects.Length==2,"Primitive native split returned wrong piece count: "+kind);
            foreach(var half in preview.Scene.Objects) { Near(Volume(half),measured/2,1e-7); ClosedOutward(half); }
            Ok(reopened.ApplyPreview(preview.Id));
            Ok(await reopened.SaveAsync(Path.Combine(output,kind.ToString().ToLowerInvariant()+"-split.3mf")));
            measurements.Add(new {kind=kind.ToString(),sizeMillimeters=dimensions,vertices=obj.Mesh.Vertices.Length,
                triangles=obj.Mesh.Triangles.Length,measuredVolumeMm3=measured,analyticVolumeMm3=analytic,relativeError,
                maximumRelativeError=tolerance,splitVolumeMm3=preview.Scene.Objects.Sum(Volume)});
        }
        Pass("Cube/cylinder/sphere dimensions, indexed closure/outward faces, nondegeneracy, tessellation volumes, native split and 3MF/STL output");

        var legacy=New(); Ok(legacy.Execute(new AddCube()));
        var legacyCube=legacy.Snapshot.Scene.Objects.Single(); Bounds(legacyCube.Bounds,new(),new(20,20,20));
        Check(legacyCube.Name=="Cube" && legacyCube.Transform==Affine.Identity && legacyCube.Mesh.Triangles.SequenceEqual(Mesh.Cube.Triangles),"AddCube contract changed.");
        Pass("Legacy AddCube retains original 20 mm cube contract");

        var rotate=New(); Ok(rotate.Execute(new AddPrimitive(PrimitiveKind.Cube,new(10,20,30))));
        var id=rotate.Snapshot.SelectedObjectId!.Value;
        var center=new Vec3(43,-12,30); Ok(rotate.Execute(new MoveObject(id,center)));
        var baseline=rotate.Snapshot; var originalObject=baseline.Scene.Objects.Single();
        Ok(rotate.Execute(new RotateObject(id,new(90,0,0)),baseline.Revision));
        var turned=rotate.Snapshot.Scene.Objects.Single();
        Bounds(turned.Bounds,center-new Vec3(5,15,10),center+new Vec3(5,15,10));
        Check(turned.Id==id && ReferenceEquals(turned.Mesh,originalObject.Mesh),"Rotation replaced mesh/identity.");
        Near(Volume(turned),6000); Check(rotate.Snapshot.Revision==baseline.Revision+1,"Rotation committed more than once.");
        Ok(rotate.Undo()); Check(rotate.Snapshot.Scene==baseline.Scene,"Rotation Undo failed.");
        Ok(rotate.Redo()); Check(rotate.Snapshot.Scene.Objects.Single()==turned,"Rotation Redo failed.");
        Ok(rotate.Execute(new RotateObject(id,new(-90,0,0)))); Points(rotate.Snapshot.Scene.Objects.Single(),originalObject);
        Ok(rotate.Execute(new RotateObject(id,new(23,-37,61))));
        var composite=rotate.Snapshot.Scene.Objects.Single(); VecNear(composite.Bounds.Center,center);
        foreach(var (point,index) in originalObject.Mesh.Vertices.Select(originalObject.Transform.Apply).Select((p,i)=>(p,i)))
            VecNear(composite.Transform.Apply(composite.Mesh.Vertices[index]),ReferenceRotation(point,center,new(23,-37,61)));
        Near(Volume(composite),6000); ClosedOutward(composite);
        var rotatedFile=Path.Combine(output,"rotated.3mf"); Ok(await rotate.SaveAsync(rotatedFile));
        var roundTrip=New(); Ok(await roundTrip.OpenAsync(rotatedFile)); Points(roundTrip.Snapshot.Scene.Objects.Single(),composite);
        Ok(rotate.Execute(new RotateObject(id,new(0,0,-61))));
        Ok(rotate.Execute(new RotateObject(id,new(0,37,0))));
        Ok(rotate.Execute(new RotateObject(id,new(-23,0,0)))); Points(rotate.Snapshot.Scene.Objects.Single(),originalObject);
        Pass("World-axis right-handed X→Y→Z rotation, analytic transformed vertices/bounds/center/volume, inverse, one-step history, native save/reopen");

        var instances=New(); Ok(await instances.OpenAsync(Fixture("assembly-mm.3mf")));
        var pair=instances.Snapshot.Scene.Objects; Check(ReferenceEquals(pair[0].Mesh,pair[1].Mesh),"Input instances are not shared.");
        Ok(instances.Execute(new RotateObject(pair[0].Id,new(17,29,43))));
        Check(instances.Snapshot.Scene.Objects[1]==pair[1] && ReferenceEquals(instances.Snapshot.Scene.Objects[0].Mesh,pair[0].Mesh),"Rotation changed another shared instance.");
        var noOp=instances.Snapshot;
        foreach(var angles in new[] {new Vec3(),new Vec3(360,-720,1080),new Vec3(360*1e12,0,0)})
        { Ok(instances.Execute(new RotateObject(pair[0].Id,angles))); Same(instances,noOp); }
        Ok(instances.Execute(new RotateObject(pair[0].Id,new(double.MaxValue,0,0))));
        Check(instances.Snapshot.Scene.Bounds.Size.IsFinite,"Finite large angle overflowed before reduction.");
        Pass("Rotation leaves shared instances untouched; exact full-turn no-ops; huge finite degrees reduce before radians");

        var rename=New(); Ok(rename.Execute(new AddCube())); var renameId=rename.Snapshot.SelectedObjectId!.Value;
        var beforeName=rename.Snapshot; Ok(rename.Execute(new RenameObject(renameId,"  Bracket & washer <A>  ")));
        var named=rename.Snapshot.Scene.Objects.Single();
        Check(named.Name=="Bracket & washer <A>" && named.Id==renameId && ReferenceEquals(named.Mesh,beforeName.Scene.Objects[0].Mesh),"Rename changed geometry or did not trim.");
        var namedSnapshot=rename.Snapshot; Ok(rename.Execute(new RenameObject(renameId," Bracket & washer <A> "))); Same(rename,namedSnapshot);
        Ok(rename.Undo()); Check(rename.Snapshot.Scene==beforeName.Scene,"Rename Undo failed.");
        Ok(rename.Redo()); Check(rename.Snapshot.Scene.Objects.Single()==named,"Rename Redo failed.");
        var namedFile=Path.Combine(output,"renamed.3mf"); Ok(await rename.SaveAsync(namedFile));
        var reopenedName=New(); Ok(await reopenedName.OpenAsync(namedFile)); Check(reopenedName.Snapshot.Scene.Objects.Single().Name==named.Name,"Name failed 3MF XML roundtrip.");
        Ok(rename.Execute(new RenameObject(renameId,new string('x',100))));
        Pass("Rename trims and preserves mesh/id; exact no-op, one-step undo/redo and escaped XML name save/reopen; 100-character boundary");

        var invalidBaseline=rename.Snapshot;
        foreach(var edit in new Edit[] {
            new RenameObject(renameId,""),new RenameObject(renameId,"  "),new RenameObject(renameId,new string('x',101)),
            new RenameObject(renameId,"bad\nname"),new RenameObject(renameId,"\ttrimmed control"),new RenameObject(renameId,null!),
            new RotateObject(renameId,new(double.NaN,0,0)),new RotateObject(renameId,new(0,double.PositiveInfinity,0)),
            new RotateObject(Guid.NewGuid(),new(90,0,0)),new RenameObject(Guid.NewGuid(),"missing"),
            new AddPrimitive((PrimitiveKind)99,new(10,10,10)),new AddPrimitive(PrimitiveKind.Cube,new(0,10,10)),
            new AddPrimitive(PrimitiveKind.Cylinder,new(-1,10,10)),new AddPrimitive(PrimitiveKind.Sphere,new(double.NaN,10,10)),
            new AddPrimitive(PrimitiveKind.Cube,new(10_000_001,10,10)),new AddPrimitive(PrimitiveKind.Sphere,new(double.Epsilon,double.Epsilon,double.Epsilon)) })
        { Check(rename.Execute(edit).Outcome==Outcome.Invalid,"Invalid edit accepted: "+edit); Same(rename,invalidBaseline); }
        foreach(var edit in Commands(renameId))
        { Check(rename.Execute(edit,invalidBaseline.Revision-1).Outcome==Outcome.Conflict,"Stale edit accepted."); Same(rename,invalidBaseline); }
        var atLimit=New(); Ok(atLimit.Execute(new AddCube())); var limitId=atLimit.Snapshot.SelectedObjectId!.Value;
        Ok(atLimit.Execute(new MoveObject(limitId,new(9_999_990,0,0))));
        var limitBefore=atLimit.Snapshot;
        Check(atLimit.Execute(new RotateObject(limitId,new(0,0,45))).Outcome==Outcome.Invalid,"Rotation outside coordinate limits accepted."); Same(atLimit,limitBefore);
        Pass("Invalid names/dimensions/angles/IDs, precision collapse, stale revision and rotated coordinate limits preserve entire accepted snapshot");

        var insertion=New(); Ok(insertion.Execute(new AddCube()));
        var insertionId=insertion.Snapshot.SelectedObjectId!.Value;
        Ok(insertion.Execute(new MoveObject(insertionId,new(-30,10,10))));
        var nativePath=Path.Combine(output,"insert-baseline.3mf"); Ok(await insertion.SaveAsync(nativePath));
        var beforeInsert=insertion.Snapshot;
        Ok(await insertion.InsertAsync(Fixture("assembly-inch.3mf"),expectedRevision:beforeInsert.Revision));
        var inserted=insertion.Snapshot; Check(inserted.Scene.Objects.Length==3 && inserted.Scene.Objects[0]==beforeInsert.Scene.Objects[0],"Insert replaced preexisting object.");
        Check(inserted.Revision==beforeInsert.Revision+1 && inserted.FilePath==beforeInsert.FilePath && inserted.IsDirty,"Insert changed filename/saved boundary or commit count.");
        Check(inserted.SelectedObjectId==inserted.Scene.Objects[1].Id,"Insert did not select first inserted object.");
        Check(inserted.Scene.Objects.Select(o=>o.Id).Distinct().Count()==3,"Inserted IDs collide.");
        Check(ReferenceEquals(inserted.Scene.Objects[1].Mesh,inserted.Scene.Objects[2].Mesh),"Insert lost imported shared instances.");
        Bounds(inserted.Scene.Objects[1].Bounds,new(),new(20,20,20)); Bounds(inserted.Scene.Objects[2].Bounds,new(30,0,0),new(50,20,20));
        Ok(insertion.Undo()); Check(insertion.Snapshot.Scene==beforeInsert.Scene && !insertion.Snapshot.IsDirty,"Insert Undo lost baseline/saved state.");
        Ok(insertion.Redo()); Check(insertion.Snapshot.Scene==inserted.Scene,"Insert Redo changed inserted identities.");
        var insertedFile=Path.Combine(output,"inserted.3mf"); Ok(await insertion.SaveAsync(insertedFile));
        var reopenedInsertion=New(); Ok(await reopenedInsertion.OpenAsync(insertedFile));
        Check(reopenedInsertion.Snapshot.Scene.Objects.Length==3,"Inserted scene failed reopen.");
        Near(reopenedInsertion.Snapshot.Scene.Objects.Sum(Volume),24000);
        Ok(insertion.Undo()); var insertionRollback=insertion.Snapshot;
        foreach(var stage in new[] {"before","Reading model","Ready to insert"})
        {
            using var cts=new CancellationTokenSource(); if(stage=="before") cts.Cancel();
            var result=await insertion.InsertAsync(Fixture("assembly-mm.3mf"),cancellation:cts.Token,
                progress:new EditingProgress(p=> {if(p.Stage==stage) cts.Cancel();}));
            Check(result.Outcome==Outcome.Cancelled,"Insert cancellation did not cancel at "+stage+": "+result); Same(insertion,insertionRollback);
        }
        foreach(var file in new[] {Path.Combine(root,"fixtures/failures/truncated-cube.stl"),Path.Combine(output,"absent.3mf")})
        { Check(!(await insertion.InsertAsync(file)).Succeeded,"Invalid insertion accepted."); Same(insertion,insertionRollback); }
        Check((await insertion.InsertAsync(Fixture("cube-20mm.stl"),expectedRevision:insertionRollback.Revision-1)).Outcome==Outcome.Conflict,"Stale insert accepted.");
        Same(insertion,insertionRollback);
        Ok(await insertion.InsertAsync(Fixture("cube-20mm.stl"),ModelUnit.Inches));
        VecNear(insertion.Snapshot.Scene.Objects[^1].Bounds.Size,new(508,508,508));
        Check(!insertion.Snapshot.CanRedo,"Insert after Undo retained redo branch.");
        Pass("Insert appends unit-normalized independent instances once, preserves native path/old objects; selection, undo/redo/save/reopen, STL unit choice, cancellation/failure/stale rollback");

        var guarded=New(); Ok(guarded.Execute(new AddCube())); var guardedId=guarded.Snapshot.SelectedObjectId!.Value;
        var accepted=guarded.Snapshot;
        Ok(await guarded.PreviewAsync(new SplitRequest(guardedId,new(0,0,1),10)));
        var previewSnapshot=guarded.Snapshot;
        foreach(var edit in Commands(guardedId)) { Check(guarded.Execute(edit).Outcome==Outcome.Conflict,"Edit escaped preview guard."); Same(guarded,previewSnapshot); }
        Check((await guarded.InsertAsync(Fixture("cube-20mm.stl"))).Outcome==Outcome.Conflict,"Insert escaped preview guard."); Same(guarded,previewSnapshot);
        Check(guarded.DiscardPreview(previewSnapshot.Preview!.Id).Outcome==Outcome.Cancelled,"Could not discard."); Same(guarded,accepted);
        using(var entered=new ManualResetEventSlim())
        using(var release=new ManualResetEventSlim())
        using(var cts=new CancellationTokenSource())
        {
            var job=guarded.InsertAsync(Fixture("assembly-mm.3mf"),cancellation:cts.Token,progress:new EditingProgress(p=> {
                if(p.Stage=="Ready to insert") { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(15)),"Insert barrier timed out."); }
            }));
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(15)),"Insertion did not reach busy barrier.");
                var busySnapshot=guarded.Snapshot;
                foreach(var edit in Commands(guardedId)) { Check(guarded.Execute(edit).Outcome==Outcome.Busy,"Edit escaped busy guard."); Same(guarded,busySnapshot); }
                Check((await guarded.InsertAsync(Fixture("cube-20mm.stl"))).Outcome==Outcome.Busy,"Concurrent insert admitted.");
                Check((await guarded.OpenAsync(Fixture("cube-20mm.stl"))).Outcome==Outcome.Busy,"Open raced insertion.");
                Check(guarded.Undo().Outcome==Outcome.Busy,"Undo raced insertion.");
            }
            finally { cts.Cancel(); release.Set(); }
            Check((await job).Outcome==Outcome.Cancelled,"Blocked insertion did not cancel."); Same(guarded,accepted);
        }
        Pass("Rotation/rename/primitive/insertion share preview and busy guards; cancelling blocked insertion preserves accepted state");

        // Append/primitive limits are checked on the combined candidate before any state/history commit.
        var crowded=New(); for(var i=0;i<ModelLimits.MaxObjects;i++) Ok(crowded.Execute(new AddCube()));
        var crowdedBefore=crowded.Snapshot;
        Check(crowded.Execute(new AddPrimitive(PrimitiveKind.Cylinder,new(10,10,10))).Outcome==Outcome.Invalid,"Primitive exceeded scene object limit."); Same(crowded,crowdedBefore);
        Check((await crowded.InsertAsync(Fixture("cube-20mm.stl"))).Outcome==Outcome.Invalid,"Insert exceeded combined scene object limit."); Same(crowded,crowdedBefore);
        Pass("Combined primitive/insertion scene-limit failures preserve objects, selection, path, revision and history");

        var inputs=new[] {Fixture("assembly-mm.3mf"),Fixture("assembly-inch.3mf"),Fixture("cube-20mm.stl")};
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {
            timestamp=DateTimeOffset.UtcNow,observation="Replacement Core runtime via public EditorSession; no desktop/original-app observation",
            target=new {name="Builder.Core",version=typeof(EditorSession).Assembly.GetName().Version?.ToString(),sha256=Hash(typeof(EditorSession).Assembly.Location)},
            tools=new {runtime=Environment.Version.ToString(),workerSha256=Hash(worker),kernel="Manifold 3.5.4"},
            inputs=inputs.Select(p=>new {file=Path.GetRelativePath(root,p),sha256=Hash(p)}),
            cases,measurements,limitations=new[] {"Fixed inscribed tessellation; no adjustable segment count or surface error bound.",
                "Small analytic corpus; no large-model performance, accessibility, desktop UI or original-app parity claim.",
                "Sphere means ellipsoid when axis extents differ; cylinder may have elliptical cross-section.",
                "Extremely small/disproportionate curved primitive dimensions that collapse sampled triangles are rejected.",
                "Inverse rotations are checked to 1e-8 mm; arbitrary rotations incur ordinary double-precision rounding."},
            outputs=Directory.EnumerateFiles(output).Where(p=>Path.GetExtension(p) is ".3mf" or ".stl")
                .Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)})
        },new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"{cases.Count} editing scenario groups passed. Evidence: {output}");
    }
    private static IEnumerable<Edit> Commands(Guid id)=>[
        new RotateObject(id,new(11,12,13)),new RenameObject(id,"guarded"),new AddPrimitive(PrimitiveKind.Cube,new(10,10,10))];
    private static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Operation changed full accepted snapshot.");
    private static void Near(double a,double b,double tolerance=1e-8)=>Check(Math.Abs(a-b)<tolerance,$"Expected {b:R}, got {a:R}.");
    private static void VecNear(Vec3 a,Vec3 b) { Near(a.X,b.X); Near(a.Y,b.Y); Near(a.Z,b.Z); }
    private static void Bounds(Bounds3 b,Vec3 min,Vec3 max) { VecNear(b.Min,min); VecNear(b.Max,max); }
    private static void Points(SceneObject a,SceneObject b)
    {
        Check(a.Mesh.Vertices.Length==b.Mesh.Vertices.Length,"Vertex count changed.");
        for(var i=0;i<a.Mesh.Vertices.Length;i++) VecNear(a.Transform.Apply(a.Mesh.Vertices[i]),b.Transform.Apply(b.Mesh.Vertices[i]));
    }
    private static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static double Volume(SceneObject obj)
    {
        var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);
        return obj.Mesh.Triangles.Sum(t=>Dot(obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin,
            Vec3.Cross(obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin,obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin))/6);
    }
    private static void ClosedOutward(SceneObject obj)
    {
        var edges=new Dictionary<(int,int),int>(); var center=obj.Bounds.Center;
        foreach(var t in obj.Mesh.Triangles)
        {
            var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A]); var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B]); var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C]);
            var normal=Vec3.Cross(b-a,c-a); Check(normal!=new Vec3(),"Degenerate triangle.");
            Check(Dot(normal,(a+b+c)*(1d/3)-center)>0,"Convex primitive face does not point outward.");
            foreach(var edge in new[] {(t.A,t.B),(t.B,t.C),(t.C,t.A)})
            { edges.TryGetValue(edge,out var count); edges[edge]=count+1; }
        }
        Check(edges.All(e=>e.Value==1 && edges.ContainsKey((e.Key.Item2,e.Key.Item1))),"Mesh is not a consistently wound closed indexed manifold.");
        Check(Volume(obj)>0,"Mesh has nonpositive signed volume.");
    }
    private static Vec3 ReferenceRotation(Vec3 point,Vec3 center,Vec3 degrees)
    {
        var p=point-center; var x=degrees.X*Math.PI/180; var y=degrees.Y*Math.PI/180; var z=degrees.Z*Math.PI/180;
        p=new(p.X,Math.Cos(x)*p.Y-Math.Sin(x)*p.Z,Math.Sin(x)*p.Y+Math.Cos(x)*p.Z);
        p=new(Math.Cos(y)*p.X+Math.Sin(y)*p.Z,p.Y,-Math.Sin(y)*p.X+Math.Cos(y)*p.Z);
        return new Vec3(Math.Cos(z)*p.X-Math.Sin(z)*p.Y,Math.Sin(z)*p.X+Math.Cos(z)*p.Y,p.Z)+center;
    }
    private sealed class EditingProgress(Action<OperationProgress> action):IProgress<OperationProgress>
    { public void Report(OperationProgress value)=>action(value); }
}
