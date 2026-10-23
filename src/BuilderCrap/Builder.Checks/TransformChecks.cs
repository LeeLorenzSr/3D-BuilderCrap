using Builder.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class TransformChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/transforms-20261019/core"); Directory.CreateDirectory(output);
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var cases=new List<string>(); var measurements=new List<object>();
        string Fixture(string file)=>Path.Combine(root,"fixtures/generated",file);
        EditorSession New()=>new(worker);
        void Pass(string message) { cases.Add(message); Console.WriteLine("PASS "+message); }
        async Task<EditorSession> Pair()
        { var session=New(); Ok(await session.OpenAsync(Fixture("assembly-mm.3mf"))); return session; }
        var move=await Pair(); var original=move.Snapshot; var id=original.Scene.Objects[0].Id;
        Ok(await move.PreviewTransformAsync(new MoveTransform(id,new(2.5,-2.5,1.1),5),expectedRevision:original.Revision));
        var first=move.Snapshot.Preview!; Accepted(move,original);
        Check(first.Request is MoveTransform {DeltaMillimeters:var effective} && effective==new Vec3(5,-5,0),"Move snap ties did not round away from zero.");
        Bounds(first.Scene.Objects[0].Bounds,new(5,-5,0),new(25,15,20));
        Check(first.Scene.Objects[1]==original.Scene.Objects[1],"Move preview touched unrelated instance.");
        Check(first.ResultObjectIds.SequenceEqual(new[] {id}) && first.SelectedObjectId==original.SelectedObjectId,"Transform result identity/selection changed.");
        Check(ReferenceEquals(first.Scene.Objects[0].Mesh,original.Scene.Objects[0].Mesh),"Preview replaced source mesh.");
        Ok(await move.PreviewTransformAsync(new MoveTransform(id,new(-7.5,7.5,2.5),5),first.Id));
        var replacement=move.Snapshot.Preview!; Check(replacement.Id!=first.Id && replacement.BaseRevision==original.Revision,"Replacement did not receive new ownership ID.");
        Bounds(replacement.Scene.Objects[0].Bounds,new(-10,10,5),new(10,30,25)); Accepted(move,original);
        var afterReplacement=move.Snapshot;
        Check((await move.PreviewTransformAsync(new MoveTransform(id,new(99,0,0)),first.Id)).Outcome==Outcome.Conflict,"Stale update overwrote newer preview."); Same(move,afterReplacement);
        Check(move.ApplyPreview(first.Id).Outcome==Outcome.Conflict,"Stale preview was applied."); Same(move,afterReplacement);
        Ok(move.ApplyPreview(replacement.Id,original.Revision));
        Check(move.Snapshot.Revision==original.Revision+1 && move.Snapshot.IsDirty,"Preview Apply did not make exactly one edit.");
        var moved=move.Snapshot; Ok(await move.SaveAsync(Path.Combine(output,"move-snapped.3mf")));
        Ok(move.Undo()); Check(move.Snapshot.Scene==original.Scene && move.Snapshot.IsDirty,"Undo failed across transformed save boundary.");
        Ok(move.Redo()); Check(move.Snapshot.Scene==moved.Scene && !move.Snapshot.IsDirty,"Redo lost saved transform identity.");
        var reopen=New(); Ok(await reopen.OpenAsync(Path.Combine(output,"move-snapped.3mf")));
        Bounds(reopen.Snapshot.Scene.Objects[0].Bounds,new(-10,10,5),new(10,30,25));
        Near(reopen.Snapshot.Scene.Objects.Sum(Volume),16000);
        measurements.Add(new {file="move-snapped.3mf",objects=2,firstBounds=new {min=new Vec3(-10,10,5),max=new Vec3(10,30,25)},volumeMm3=16000});
        Pass("Move snap ties, effective request, replace from accepted source, fresh/stale IDs, shared-instance isolation, one Apply/history step and saved-state roundtrip");

        var rotate=New(); Ok(rotate.Execute(new AddPrimitive(PrimitiveKind.Cube,new(10,20,30))));
        var rotatedId=rotate.Snapshot.SelectedObjectId!.Value; var center=new Vec3(-12,7,40);
        Ok(rotate.Execute(new MoveObject(rotatedId,center))); var beforeRotate=rotate.Snapshot;
        Ok(await rotate.PreviewTransformAsync(new RotateTransform(rotatedId,new(22.5,-22.5,67.5),45)));
        var rotatePreview=rotate.Snapshot.Preview!;
        Check(rotatePreview.Request is RotateTransform {Degrees:var snappedDegrees} && snappedDegrees==new Vec3(45,-45,90),"Rotation effective request was not snapped.");
        var source=beforeRotate.Scene.Objects.Single(); var result=rotatePreview.Scene.Objects.Single();
        VecNear(result.Bounds.Center,center); Near(Volume(result),6000);
        for(var i=0;i<source.Mesh.Vertices.Length;i++)
            VecNear(result.Transform.Apply(result.Mesh.Vertices[i]),ReferenceRotate(source.Transform.Apply(source.Mesh.Vertices[i]),center,new(45,-45,90)));
        Accepted(rotate,beforeRotate); Ok(rotate.ApplyPreview(rotatePreview.Id));
        Ok(await rotate.SaveAsync(Path.Combine(output,"rotate-snapped.3mf")));
        var acceptedRotation=rotate.Snapshot; source=acceptedRotation.Scene.Objects.Single();
        Ok(await rotate.PreviewTransformAsync(new ScaleTransform(rotatedId,new(1.125,0.875,1),25)));
        var scalePreview=rotate.Snapshot.Preview!; var factors=new Vec3(1.25,0.75,1);
        Check(scalePreview.Request is ScaleTransform {Factors:var snappedFactors} && snappedFactors==factors,"Scale percent delta did not snap around identity.");
        var scaled=scalePreview.Scene.Objects.Single(); VecNear(scaled.Bounds.Center,center);
        VecNear(scaled.Bounds.Size,new(source.Bounds.Size.X*1.25,source.Bounds.Size.Y*0.75,source.Bounds.Size.Z));
        for(var i=0;i<source.Mesh.Vertices.Length;i++)
        {
            var p=source.Transform.Apply(source.Mesh.Vertices[i])-center;
            VecNear(scaled.Transform.Apply(scaled.Mesh.Vertices[i]),center+new Vec3(p.X*factors.X,p.Y*factors.Y,p.Z*factors.Z));
        }
        Near(Volume(scaled),5625); Accepted(rotate,acceptedRotation); Ok(rotate.ApplyPreview(scalePreview.Id));
        Ok(await rotate.SaveAsync(Path.Combine(output,"scale-snapped.3mf")));
        Ok(await rotate.ExportStlAsync(Path.Combine(output,"scale-snapped.stl")));
        Ok(rotate.Undo()); Check(rotate.Snapshot.Scene==acceptedRotation.Scene,"Scale Undo did not restore accepted rotated transform.");
        Ok(rotate.Redo()); Check(rotate.Snapshot.Scene.Objects.Single()==scaled,"Scale Redo changed candidate transform.");
        measurements.Add(new {file="rotate-snapped.3mf",sourceSizeMillimeters=new Vec3(10,20,30),center,worldDegrees=new Vec3(45,-45,90),volumeMm3=6000});
        measurements.Add(new {file="scale-snapped.3mf",worldFactors=factors,center,volumeMm3=5625});
        Pass("Rotation world XYZ bounds-center semantics and percentage scaling in world axes on an existing rotated object; analytic vertices/volume, persistence and undo/redo");

        var noOp=await Pair(); var noOpId=noOp.Snapshot.Scene.Objects[0].Id;
        Ok(noOp.Execute(new MoveObject(noOpId,new(15,10,10)))); Ok(noOp.Undo());
        var untouched=noOp.Snapshot;
        foreach(var request in new TransformRequest[] {
            new MoveTransform(noOpId,new()),new MoveTransform(noOpId,new(0.1,-0.1,0),1),
            new RotateTransform(noOpId,new(360,-720,1080)),new RotateTransform(noOpId,new(360,-720,1080),15),
            new ScaleTransform(noOpId,new(1,1,1),37),
            new ScaleTransform(noOpId,new(1.01,0.99,1),10) })
        {
            Ok(await noOp.PreviewTransformAsync(request)); var candidate=noOp.Snapshot.Preview!;
            Check(candidate.Scene==untouched.Scene,"Identity draft changed document content identity."); Accepted(noOp,untouched);
            Ok(noOp.ApplyPreview(candidate.Id)); Same(noOp,untouched);
        }
        // Replacing a changed draft with identity removes the previous candidate, not the accepted document.
        Ok(await noOp.PreviewTransformAsync(new MoveTransform(noOpId,new(100,0,0))));
        Ok(await noOp.PreviewTransformAsync(new ScaleTransform(noOpId,new(1,1,1)),noOp.Snapshot.Preview!.Id));
        Ok(noOp.ApplyPreview(noOp.Snapshot.Preview!.Id)); Same(noOp,untouched);
        Ok(await noOp.PreviewAsync(new MoveTransform(noOpId,new(1,0,0))));
        Check(noOp.DiscardPreview(noOp.Snapshot.Preview!.Id).Outcome==Outcome.Cancelled,"Generic preview transform dispatch failed."); Same(noOp,untouched);
        Pass("True no-op previews and cross-kind replacement preserve accepted content/revision/dirty/history/redo; generic PreviewAsync dispatch shares transform path");

        foreach(var (input,step,expected) in new[] {(170d,100d,200d),(360d,50d,350d),(-170d,100d,-200d),(-360d,50d,-350d),(720d,250d,750d)})
        {
            Ok(await noOp.PreviewTransformAsync(new RotateTransform(noOpId,new(input,0,0),step)));
            var snapped=noOp.Snapshot.Preview!;
            Check(snapped.Request is RotateTransform {Degrees:var angles} && angles==new Vec3(expected,0,0),"Unwrapped degree snap did not quantize to a multiple of its step.");
            Ok(await noOp.PreviewTransformAsync((RotateTransform)snapped.Request,snapped.Id));
            var replayed=noOp.Snapshot.Preview!;
            Check(replayed.Id!=snapped.Id && replayed.Request==snapped.Request && replayed.Scene.Objects.SequenceEqual(snapped.Scene.Objects),"Effective rotation request replay changed the candidate.");
            var rotatedTarget=replayed.Scene.Objects.First(o=>o.Id==noOpId); var acceptedTarget=untouched.Scene.Objects.First(o=>o.Id==noOpId);
            for(var i=0;i<acceptedTarget.Mesh.Vertices.Length;i++)
                VecNear(rotatedTarget.Transform.Apply(rotatedTarget.Mesh.Vertices[i]),ReferenceRotate(acceptedTarget.Transform.Apply(acceptedTarget.Mesh.Vertices[i]),acceptedTarget.Bounds.Center,new(expected,0,0)));
            Check(noOp.DiscardPreview(replayed.Id).Outcome==Outcome.Cancelled,"Unwrapped snap discard failed."); Same(noOp,untouched);
        }
        Pass("Unwrapped rotation snap at non-divisor steps is replayable and idempotent: 170/100→200, 360/50→350, negative counterparts and multi-turn input");

        var invalid=await Pair(); var invalidId=invalid.Snapshot.Scene.Objects[0].Id; var neighborId=invalid.Snapshot.Scene.Objects[1].Id;
        Ok(await invalid.PreviewTransformAsync(new MoveTransform(invalidId,new(3,4,5)))); var preserved=invalid.Snapshot;
        foreach(var request in new TransformRequest[] {
            new MoveTransform(invalidId,new(double.NaN,0,0)),new MoveTransform(invalidId,new(double.MaxValue,0,0)),
            new MoveTransform(invalidId,new(double.MaxValue,0,0),1e308),new MoveTransform(invalidId,new(),-1),
            new MoveTransform(invalidId,new(),double.PositiveInfinity),new RotateTransform(invalidId,new(0,double.NaN,0)),
            new RotateTransform(invalidId,new(),double.NaN),new RotateTransform(invalidId,new(),-1),
            new ScaleTransform(invalidId,new(0,1,1)),new ScaleTransform(invalidId,new(-1,1,1)),
            new ScaleTransform(invalidId,new(double.PositiveInfinity,1,1)),new ScaleTransform(invalidId,new(double.NaN,1,1)),
            new ScaleTransform(invalidId,new(0.5,1,1),100),new ScaleTransform(invalidId,new(1,1,1),-1),
            new ScaleTransform(invalidId,new(1,1,1),double.PositiveInfinity),new ScaleTransform(invalidId,new(double.MaxValue,1,1)),
            new ScaleTransform(invalidId,new(1e-300,1e-300,1e-300)),new UnsupportedTransform(invalidId) })
        {
            Check((await invalid.PreviewTransformAsync(request,preserved.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid replacement accepted: "+request);
            Same(invalid,preserved);
        }
        Check((await invalid.PreviewTransformAsync(new MoveTransform(neighborId,new(1,0,0)),preserved.Preview!.Id)).Outcome==Outcome.Conflict,"Replacement changed target."); Same(invalid,preserved);
        Check((await invalid.PreviewTransformAsync(new MoveTransform(invalidId,new(1,0,0)))).Outcome==Outcome.Conflict,"Missing replacement ownership admitted."); Same(invalid,preserved);
        Check((await invalid.PreviewTransformAsync(new MoveTransform(invalidId,new()),preserved.Preview!.Id,expectedRevision:preserved.Revision-1)).Outcome==Outcome.Conflict,"Stale revision admitted."); Same(invalid,preserved);
        Check(invalid.DiscardPreview(preserved.Preview!.Id).Outcome==Outcome.Cancelled,"Discard failed.");
        var invalidAccepted=invalid.Snapshot;
        Check((await invalid.PreviewTransformAsync(new MoveTransform(Guid.NewGuid(),new()))).Outcome==Outcome.Invalid,"Unknown target accepted."); Same(invalid,invalidAccepted);
        Check((await invalid.PreviewTransformAsync(null!)).Outcome==Outcome.Invalid,"Null transform accepted."); Same(invalid,invalidAccepted);
        Check((await invalid.PreviewTransformAsync(new MoveTransform(invalidId,new()),Guid.NewGuid())).Outcome==Outcome.Conflict,"Nonexistent replacement accepted."); Same(invalid,invalidAccepted);
        foreach(var request in new TransformRequest[] {new MoveTransform(invalidId,new(0.25,-0.25,0),double.Epsilon),
            new RotateTransform(invalidId,new(double.MaxValue,0,0)),new ScaleTransform(invalidId,new(1.25,1,1),double.Epsilon)})
        { Ok(await invalid.PreviewTransformAsync(request)); Check(invalid.DiscardPreview(invalid.Snapshot.Preview!.Id).Outcome==Outcome.Cancelled,"Discard extreme finite preview failed."); Same(invalid,invalidAccepted); }
        Pass("Bad factors/steps/nonfinite/overflow/collapsed dimensions, stale IDs/revisions and changed target reject atomically; huge degrees/tiny positive snap remain finite");

        var guarded=await Pair(); var guardedBefore=guarded.Snapshot; var guardId=guardedBefore.SelectedObjectId!.Value;
        Ok(await guarded.PreviewTransformAsync(new MoveTransform(guardId,new(1,2,3)))); var guardedPreview=guarded.Snapshot;
        Check(guarded.Execute(new AddCube()).Outcome==Outcome.Conflict,"Edit escaped transform preview guard.");
        Check(guarded.Select(null).Outcome==Outcome.Conflict,"Selection escaped transform preview guard.");
        Check(guarded.Undo().Outcome==Outcome.Conflict && guarded.Redo().Outcome==Outcome.Conflict,"History escaped transform preview guard.");
        Check((await guarded.OpenAsync(Fixture("cube-20mm.stl"))).Outcome==Outcome.Conflict,"Open escaped preview guard.");
        Check((await guarded.InsertAsync(Fixture("cube-20mm.stl"))).Outcome==Outcome.Conflict,"Insert escaped preview guard.");
        Check((await guarded.SaveAsync(Path.Combine(output,"unaccepted.3mf"))).Outcome==Outcome.Conflict,"Save escaped preview guard.");
        Check((await guarded.ExportStlAsync(Path.Combine(output,"unaccepted.stl"))).Outcome==Outcome.Conflict,"Export escaped preview guard.");
        Check((await guarded.DiagnoseAsync(guardId)).Result.Outcome==Outcome.Conflict,"Diagnosis escaped preview guard.");
        Check((await guarded.PreviewAsync(new SplitRequest(guardId,new(0,0,1),10))).Outcome==Outcome.Conflict,"Geometry replaced transform preview."); Same(guarded,guardedPreview);
        Check(guarded.DiscardPreview(guardedPreview.Preview!.Id).Outcome==Outcome.Cancelled,"Discard transform failed."); Same(guarded,guardedBefore);
        Ok(await guarded.PreviewAsync(new SplitRequest(guardId,new(0,0,1),10))); var geometryPreview=guarded.Snapshot;
        Check((await guarded.PreviewTransformAsync(new MoveTransform(guardId,new(1,2,3)),geometryPreview.Preview!.Id)).Outcome==Outcome.Conflict,"Transform replaced unrelated geometry preview."); Same(guarded,geometryPreview);
        Check(guarded.DiscardPreview(geometryPreview.Preview!.Id).Outcome==Outcome.Cancelled,"Discard geometry failed."); Same(guarded,guardedBefore);
        Pass("Transform ownership excludes unrelated geometry previews; edits, selection, history, file operations and diagnosis share preview guard");

        var cancelled=New(); Ok(cancelled.Execute(new AddPrimitive(PrimitiveKind.Sphere,new(18,26,34)))); var cancelledId=cancelled.Snapshot.SelectedObjectId!.Value;
        Ok(await cancelled.SaveAsync(Path.Combine(output,"cancel-baseline.3mf")));
        Ok(cancelled.Execute(new RenameObject(cancelledId,"redo marker"))); Ok(cancelled.Undo()); var cancelOriginal=cancelled.Snapshot;
        foreach(var replacing in new[] {false,true})
        {
            if(replacing) Ok(await cancelled.PreviewTransformAsync(new MoveTransform(cancelledId,new(2,0,0))));
            var cancelBefore=cancelled.Snapshot;
            foreach(var stage in new[] {"before","Preparing transform","Transforming vertices","Ready to preview"})
            {
                using var cts=new CancellationTokenSource(); if(stage=="before") cts.Cancel();
                var outcome=await cancelled.PreviewTransformAsync(new RotateTransform(cancelledId,new(17,29,43)),cancelBefore.Preview?.Id,cts.Token,
                    new TransformProgress(p=> { if(p.Stage==stage && (stage!="Transforming vertices" || p.Completed>=1024)) cts.Cancel(); }));
                Check(outcome.Outcome==Outcome.Cancelled,"Transform cancellation failed at "+stage); Same(cancelled,cancelBefore);
            }
            if(replacing) { Check(cancelled.DiscardPreview(cancelBefore.Preview!.Id).Outcome==Outcome.Cancelled,"Discard retained replacement failed."); Same(cancelled,cancelOriginal); }
        }
        Pass("Cancellation before work, during preparation, after 1024 transformed sphere vertices and before publish preserves accepted state and any prior candidate");

        Ok(await cancelled.PreviewTransformAsync(new MoveTransform(cancelledId,new(2,0,0)))); var busyBefore=cancelled.Snapshot;
        using(var entered=new ManualResetEventSlim())
        using(var release=new ManualResetEventSlim())
        using(var cts=new CancellationTokenSource())
        {
            var job=cancelled.PreviewTransformAsync(new ScaleTransform(cancelledId,new(2,2,2)),busyBefore.Preview!.Id,cts.Token,new TransformProgress(p=> {
                if(p.Stage=="Transforming vertices" && p.Completed==1024) { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(15)),"Transform barrier timed out."); }
            }));
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(15)),"Transform did not reach vertex barrier.");
                var busy=cancelled.Snapshot; Check(busy.IsBusy && busy.Preview==busyBefore.Preview,"In-flight replacement removed prior candidate.");
                Check(cancelled.Execute(new AddCube()).Outcome==Outcome.Busy,"Edit escaped busy guard.");
                Check(cancelled.Select(null).Outcome==Outcome.Busy && cancelled.Undo().Outcome==Outcome.Busy,"Selection/history escaped busy guard.");
                Check(cancelled.ApplyPreview(busyBefore.Preview!.Id).Outcome==Outcome.Busy && cancelled.DiscardPreview(busyBefore.Preview.Id).Outcome==Outcome.Busy,"Preview acceptance escaped busy guard.");
                Check((await cancelled.PreviewTransformAsync(new MoveTransform(cancelledId,new()),busyBefore.Preview.Id)).Outcome==Outcome.Busy,"Concurrent update admitted.");
                Check((await cancelled.SaveAsync(Path.Combine(output,"busy.3mf"))).Outcome==Outcome.Busy,"File save escaped busy guard.");
                Check((await cancelled.OpenAsync(Fixture("cube-20mm.stl"))).Outcome==Outcome.Busy,"Open escaped busy guard.");
                Check((await cancelled.InsertAsync(Fixture("cube-20mm.stl"))).Outcome==Outcome.Busy,"Insert escaped busy guard."); Same(cancelled,busy);
            }
            finally { cts.Cancel(); release.Set(); }
            Check((await job).Outcome==Outcome.Cancelled,"Busy replacement did not cancel."); Same(cancelled,busyBefore);
        }
        var oldId=busyBefore.Preview!.Id;
        var competing=await Task.WhenAll(Task.Run(()=>cancelled.PreviewTransformAsync(new MoveTransform(cancelledId,new(5,0,0)),oldId)),
            Task.Run(()=>cancelled.PreviewTransformAsync(new MoveTransform(cancelledId,new(9,0,0)),oldId)));
        Check(competing.Count(r=>r.Succeeded)==1 && competing.Count(r=>r.Outcome is Outcome.Busy or Outcome.Conflict)==1,"Concurrent clients both replaced one preview ownership token.");
        Check(cancelled.Snapshot.Preview!.Id!=oldId,"Winning replacement did not change ownership token."); Accepted(cancelled,cancelOriginal);
        Check(cancelled.DiscardPreview(cancelled.Snapshot.Preview.Id).Outcome==Outcome.Cancelled,"Discard competing result failed."); Same(cancelled,cancelOriginal);
        Pass("Busy lease spans cancellable geometry work outside gate; prior preview survives; concurrent clients cannot both replace one ownership token");

        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {
            timestamp=DateTimeOffset.UtcNow,observation="Replacement Core runtime through public EditorSession; no desktop/original-app input coverage",
            target=new {name="Builder.Core",version=typeof(EditorSession).Assembly.GetName().Version?.ToString(),sha256=Hash(typeof(EditorSession).Assembly.Location)},
            tool=new {runtime=Environment.Version.ToString(),workerSha256=Hash(worker)},
            inputs=new[] {"assembly-mm.3mf","cube-20mm.stl"}.Select(name=>new {file="fixtures/generated/"+name,sha256=Hash(Fixture(name))}),
            cases,measurements,correctionNotes=new[] {"Rotation snapping now quantizes unwrapped raw degrees before Affine.RotateAbout reduces angles for trigonometry. Earlier pre-normalization could change a replayed effective request when the snap step did not divide 360; explicit replay checks now cover those cases."},
            limitations=new[] {"Single object and relative world-axis transforms; no grouping/pointer/camera behavior is implemented in Core.",
                "Double-precision arithmetic and existing ±10,000,000 mm physical limits apply; collapsed scaling is rejected.",
                "Rotation snap quantizes unwrapped degrees: 360 degrees at a 50-degree step becomes 350. Full turns are identity with snap off or a step dividing 360. Scaling snap quantizes percent delta from identity.",
                "Focused small analytic/sphere corpus does not establish production interaction latency or broad geometry parity."},
            outputs=Directory.EnumerateFiles(output).Where(p=>Path.GetExtension(p) is ".3mf" or ".stl").Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)})
        },new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"{cases.Count} transform scenario groups passed. Evidence: {output}");
    }
    private sealed record UnsupportedTransform(Guid ObjectId):TransformRequest(ObjectId);
    private sealed class TransformProgress(Action<OperationProgress> callback):IProgress<OperationProgress>
    { public void Report(OperationProgress value)=>callback(value); }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted state or prior preview changed.");
    private static void Accepted(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot with {Preview=null}==before with {Preview=null},"Preview mutated accepted scene/path/selection/history/revision.");
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-8,$"Expected {b:R}, got {a:R}.");
    private static void VecNear(Vec3 a,Vec3 b) { Near(a.X,b.X); Near(a.Y,b.Y); Near(a.Z,b.Z); }
    private static void Bounds(Bounds3 bounds,Vec3 min,Vec3 max) { VecNear(bounds.Min,min); VecNear(bounds.Max,max); }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static double Volume(SceneObject obj)
    {
        var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);
        return obj.Mesh.Triangles.Sum(t=> {
            var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin; var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin; var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin;
            var cross=Vec3.Cross(b,c); return (a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z)/6;
        });
    }
    private static Vec3 ReferenceRotate(Vec3 point,Vec3 center,Vec3 degrees)
    {
        var p=point-center; var x=degrees.X*Math.PI/180; var y=degrees.Y*Math.PI/180; var z=degrees.Z*Math.PI/180;
        p=new(p.X,Math.Cos(x)*p.Y-Math.Sin(x)*p.Z,Math.Sin(x)*p.Y+Math.Cos(x)*p.Z);
        p=new(Math.Cos(y)*p.X+Math.Sin(y)*p.Z,p.Y,-Math.Sin(y)*p.X+Math.Cos(y)*p.Z);
        return center+new Vec3(Math.Cos(z)*p.X-Math.Sin(z)*p.Y,Math.Sin(z)*p.X+Math.Cos(z)*p.Y,p.Z);
    }
}
