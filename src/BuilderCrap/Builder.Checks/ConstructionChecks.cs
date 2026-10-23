using Builder.Core;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

internal static class ConstructionChecks
{
    public static async Task Run(string root)
    {
        var output = Path.Combine(root,".local/evidence/construction-20261018/checks"); Directory.CreateDirectory(output);
        var cases = new List<string>();
        void Pass(string name) { cases.Add(name); Console.WriteLine("PASS " + name); }
        string Fixture(string name) => Path.Combine(root,"fixtures/generated",name);
        async Task<(EditorSession editor, Guid a, Guid b)> Pair(double centerX = 20)
        {
            var editor = new EditorSession(); Ok(await editor.OpenAsync(Fixture("assembly-mm.3mf")));
            var objects = editor.Snapshot.Scene.Objects;
            Ok(editor.Execute(new MoveObject(objects[1].Id,new(centerX,10,10))));
            return (editor,objects[0].Id,objects[1].Id);
        }
        foreach(var (kind,volume) in new[] { (BooleanKind.Union,12000d),(BooleanKind.Subtract,4000d),(BooleanKind.Intersect,4000d) })
        {
            var (s,a,b) = await Pair(); var before = s.Snapshot;
            Ok(await s.PreviewAsync(new BooleanRequest(a,b,kind),expectedRevision:before.Revision));
            var p = s.Snapshot.Preview!; SameAccepted(s,before); Near(Volume(p.Scene),volume);
            Check(s.Execute(new AddCube()).Outcome == Outcome.Conflict,"Edit escaped preview guard.");
            Check(s.Select(b).Outcome == Outcome.Conflict,"Selection escaped preview guard.");
            Check(s.Undo().Outcome == Outcome.Conflict,"Undo escaped preview guard.");
            Check((await s.SaveAsync(Path.Combine(output,"unaccepted.3mf"))).Outcome == Outcome.Conflict,"Save persisted unaccepted geometry.");
            Check(s.ApplyPreview(Guid.NewGuid()).Outcome == Outcome.Conflict,"Unowned preview accepted.");
            Check(s.ApplyPreview(p.Id,before.Revision-1).Outcome == Outcome.Conflict,"Stale revision accepted.");
            Ok(s.ApplyPreview(p.Id)); Near(Volume(s.Snapshot.Scene),volume);
            Check(s.Snapshot.Revision == before.Revision+1 && s.Snapshot.IsDirty,"Apply did not commit exactly once.");
            Ok(await s.SaveAsync(Path.Combine(output,kind.ToString().ToLowerInvariant()+".3mf")));
            Ok(await s.ExportStlAsync(Path.Combine(output,kind.ToString().ToLowerInvariant()+".stl")));
            var reopened = new EditorSession(); Ok(await reopened.OpenAsync(Path.Combine(output,kind.ToString().ToLowerInvariant()+".3mf")));
            Near(Volume(reopened.Snapshot.Scene),volume);
            Ok(await reopened.PreviewAsync(new SplitRequest(reopened.Snapshot.SelectedObjectId!.Value,new(0,0,1),50)));
            Near(Volume(reopened.Snapshot.Preview!.Scene),volume); // Plane outside the object: all volume stays on one side.
            var accepted = s.Snapshot;
            Ok(s.Undo()); Check(s.Snapshot.Scene == before.Scene && s.Snapshot.SelectedObjectId == before.SelectedObjectId,"Undo failed to restore operands/selection.");
            Ok(s.Redo()); Check(s.Snapshot.Scene == accepted.Scene && !s.Snapshot.IsDirty,"Redo changed result IDs or saved boundary.");
            Check(s.ApplyPreview(p.Id).Outcome == Outcome.Conflict,"Already consumed preview accepted twice.");
        }
        Pass("Union/subtract/intersect analytic volumes; preview guards; one-step undo/redo; saved clean boundary; stale IDs");

        var split = new EditorSession(); Ok(await split.OpenAsync(Fixture("cube-20mm.stl"))); var id = split.Snapshot.SelectedObjectId!.Value;
        Ok(split.Execute(new ResizeObject(id,new(30,20,20)))); var splitBefore = split.Snapshot;
        foreach(var keep in Enum.GetValues<SplitKeep>())
        {
            Ok(await split.PreviewAsync(new SplitRequest(id,new(0,0,7),10,keep)));
            var p = split.Snapshot.Preview!; Near(Volume(p.Scene),keep == SplitKeep.Both ? 12000 : 6000);
            Check(p.Scene.Objects.Length == (keep == SplitKeep.Both ? 2 : 1),"Wrong split sides."); SameAccepted(split,splitBefore);
            if(keep == SplitKeep.Both)
            {
                foreach(var obj in p.Scene.Objects) { Near(Volume(new(Guid.NewGuid(),[obj])),6000); Near(obj.Bounds.Size.Z,10); }
                Ok(split.ApplyPreview(p.Id)); Ok(await split.SaveAsync(Path.Combine(output,"split-both.3mf")));
                Ok(split.Undo()); splitBefore = split.Snapshot;
            }
            else { Check(split.DiscardPreview(p.Id).Outcome == Outcome.Cancelled,"Discard failed."); Check(split.Snapshot == splitBefore,"Discard changed accepted snapshot."); }
        }
        Ok(await split.PreviewAsync(new SplitRequest(id,new(1,1,0),20/Math.Sqrt(2))));
        Near(Volume(split.Snapshot.Preview!.Scene),12000);
        foreach(var obj in split.Snapshot.Preview!.Scene.Objects) Near(Volume(new(Guid.NewGuid(),[obj])),6000);
        Ok(split.ApplyPreview(split.Snapshot.Preview!.Id)); Ok(await split.SaveAsync(Path.Combine(output,"split-diagonal.3mf")));
        Ok(split.Undo());
        Pass("World-space midpoint and diagonal splits conserve volume; side choices; discard restores exact state");

        foreach(var (center,kind,expected) in new[] {
            (40d,BooleanKind.Union,16000d),(40d,BooleanKind.Intersect,0d),(40d,BooleanKind.Subtract,8000d),
            (30d,BooleanKind.Union,16000d),(30d,BooleanKind.Intersect,0d),
            (10d,BooleanKind.Subtract,0d),(10d,BooleanKind.Intersect,8000d),(10d,BooleanKind.Union,8000d) })
        {
            var (s,a,b) = await Pair(center); var before = s.Snapshot;
            var result = await s.PreviewAsync(new BooleanRequest(a,b,kind));
            Check(result.Succeeded,$"{kind}, center {center}: {result}"); Near(Volume(s.Snapshot.Preview!.Scene),expected);
            Ok(s.ApplyPreview(s.Snapshot.Preview!.Id)); Ok(s.Undo()); Check(s.Snapshot.Scene == before.Scene,"Empty-result undo lost input.");
        }
        Pass("Disjoint, face-touching and identical solids; empty previews are explicit and undoable");

        var (hollow,outer,inner) = await Pair(10);
        Ok(hollow.Execute(new ResizeObject(inner,new(10,10,10))));
        Ok(await hollow.PreviewAsync(new BooleanRequest(outer,inner,BooleanKind.Subtract)));
        Near(Volume(hollow.Snapshot.Preview!.Scene),7000); Ok(hollow.ApplyPreview(hollow.Snapshot.Preview!.Id));
        Ok(await hollow.PreviewAsync(new SplitRequest(hollow.Snapshot.SelectedObjectId!.Value,new(0,0,1),10)));
        foreach(var obj in hollow.Snapshot.Preview!.Scene.Objects) Near(Volume(new(Guid.NewGuid(),[obj])),3500);
        Ok(hollow.ApplyPreview(hollow.Snapshot.Preview!.Id)); Ok(await hollow.SaveAsync(Path.Combine(output,"hollow-split.3mf")));
        Pass("Contained subtraction makes a cavity; subsequent plane split caps both hollow halves");

        async Task<EditorSession> TransformedPair(string name,string transform)
        {
            var file = Path.Combine(output,name+"-input.3mf"); File.Copy(Fixture("assembly-mm.3mf"),file,true);
            using(var zip = ZipFile.Open(file,ZipArchiveMode.Update))
            {
                var part = zip.Entries.Single(e=>e.FullName.EndsWith(".model")); var partName = part.FullName;
                XDocument xml; using(var stream = part.Open()) xml = XDocument.Load(stream);
                XNamespace ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
                xml.Descendants(ns+"item").Last().SetAttributeValue("transform",transform);
                part.Delete(); using var destination = zip.CreateEntry(partName).Open(); xml.Save(destination);
            }
            var s = new EditorSession(); Ok(await s.OpenAsync(file)); return s;
        }
        var cosine = Math.Sqrt(0.5); var rotatedTransform = string.Join(" ",new[] {cosine,cosine,0,-cosine,cosine,0,0,0,1,10,10-20*cosine,0}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));
        var octagonVolume = 16000*(Math.Sqrt(2)-1);
        foreach(var (kind,expected) in new[] { (BooleanKind.Union,16000-octagonVolume), (BooleanKind.Intersect,octagonVolume), (BooleanKind.Subtract,8000-octagonVolume) })
        {
            var s = await TransformedPair("rotated",rotatedTransform); var objects = s.Snapshot.Scene.Objects;
            Ok(await s.PreviewAsync(new BooleanRequest(objects[0].Id,objects[1].Id,kind)));
            Near(Volume(s.Snapshot.Preview!.Scene),expected); Ok(s.ApplyPreview(s.Snapshot.Preview!.Id));
            Ok(await s.SaveAsync(Path.Combine(output,"rotated-"+kind.ToString().ToLowerInvariant()+".3mf")));
        }
        var mirrored = await TransformedPair("mirrored","-1 0 0 0 1 0 0 0 1 20 0 0"); var mirroredId = mirrored.Snapshot.Scene.Objects[1].Id;
        Ok(await mirrored.PreviewAsync(new SplitRequest(mirroredId,new(0,0,-1),-10,SplitKeep.Positive)));
        var reflectedResult = mirrored.Snapshot.Preview!.Scene.Objects.Single(o=>mirrored.Snapshot.Preview.ResultObjectIds.Contains(o.Id));
        Near(reflectedResult.Bounds.Min.Z,0); Near(reflectedResult.Bounds.Max.Z,10); Near(Volume(new(Guid.NewGuid(),[reflectedResult])),4000);
        Pass("45-degree rotated cube booleans match analytic octagon volumes; mirrored transform and negative plane normal");

        var (cancel,aCancel,bCancel) = await Pair();
        // Keep a redo branch and saved filename: rollback must preserve more than visible triangles.
        Ok(await cancel.SaveAsync(Path.Combine(output,"cancel-baseline.3mf")));
        Ok(cancel.Execute(new MoveObject(bCancel,new(22,10,10)))); Ok(cancel.Undo()); var unchanged = cancel.Snapshot;
        foreach(var stage in new[] { "before","Preparing geometry","Calculating geometry","Reading geometry result","Ready to preview" })
        {
            using var cts = new CancellationTokenSource(); if(stage == "before") cts.Cancel();
            var r = await cancel.PreviewAsync(new BooleanRequest(aCancel,bCancel,BooleanKind.Union),cts.Token,
                new InlineProgress(p => { if(p.Stage == stage) cts.Cancel(); }));
            Check(r.Outcome == Outcome.Cancelled,"Cancellation failed at " + stage + ": " + r);
            Check(cancel.Snapshot == unchanged,"Cancellation lost accepted state/history at " + stage);
        }
        Check(!Process.GetProcessesByName("Builder.GeometryWorker").Any(),"Cancelled worker still running.");
        foreach(var request in new GeometryRequest[] {new BooleanRequest(aCancel,aCancel,BooleanKind.Union),
            new BooleanRequest(aCancel,Guid.NewGuid(),BooleanKind.Union),new BooleanRequest(aCancel,bCancel,(BooleanKind)99),
            new SplitRequest(aCancel,new(),10),new SplitRequest(aCancel,new(0,0,1),double.NaN)})
        { Check((await cancel.PreviewAsync(request)).Outcome == Outcome.Invalid,"Bad request accepted."); Check(cancel.Snapshot == unchanged,"Failed request changed state."); }
        Check((await cancel.PreviewAsync(new SplitRequest(aCancel,new(0,0,1),10),expectedRevision:unchanged.Revision-1)).Outcome == Outcome.Conflict,"Stale request accepted.");
        Ok(await cancel.PreviewAsync(new SplitRequest(aCancel,new(0,0,double.Epsilon),10)));
        cancel.DiscardPreview(cancel.Snapshot.Preview!.Id); Check(cancel.Snapshot == unchanged,"Tiny-normal preview mutated state.");
        Pass("Pre-cancel, worker cancellation, result-read and pre-publish cancellation; invalid input; redo/path/selection survive");

        using(var entered = new ManualResetEventSlim())
        using(var release = new ManualResetEventSlim())
        using(var cts = new CancellationTokenSource())
        {
            var job = cancel.PreviewAsync(new BooleanRequest(aCancel,bCancel,BooleanKind.Union),cts.Token,new InlineProgress(p => {
                if(p.Stage == "Calculating geometry") { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)),"Job barrier timeout."); }
            }));
            Check(entered.Wait(TimeSpan.FromSeconds(10)),"Worker did not start.");
            Check(cancel.Execute(new AddCube()).Outcome == Outcome.Busy,"Edit interleaved with geometry job.");
            Check(cancel.ApplyPreview(Guid.NewGuid()).Outcome == Outcome.Busy,"Apply interleaved with geometry job.");
            Check((await cancel.PreviewAsync(new SplitRequest(aCancel,new(0,0,1),10))).Outcome == Outcome.Busy,"Concurrent job admitted.");
            cts.Cancel(); release.Set(); Check((await job).Outcome == Outcome.Cancelled,"Busy job did not cancel."); Check(cancel.Snapshot == unchanged,"Busy cancellation changed state.");
        }
        Ok(await cancel.PreviewAsync(new BooleanRequest(aCancel,bCancel,BooleanKind.Subtract)));
        var unrelated = cancel.Snapshot.Scene.Objects[0];
        cancel.DiscardPreview(cancel.Snapshot.Preview!.Id);
        Ok(cancel.Execute(new AddCube())); var neighbor = cancel.Snapshot.Scene.Objects[^1];
        Ok(await cancel.PreviewAsync(new BooleanRequest(aCancel,bCancel,BooleanKind.Union)));
        Check(cancel.Snapshot.Preview!.Scene.Objects.Contains(neighbor),"Unrelated instance was changed.");
        Ok(cancel.ApplyPreview(cancel.Snapshot.Preview!.Id)); Ok(cancel.Undo()); Ok(cancel.Execute(new MoveObject(unrelated.Id,new(5,10,10))));
        Check(!cancel.Snapshot.CanRedo,"Branch after geometry undo retained redo.");
        Pass("Busy serialization; cancellation reaps process; unrelated object preserved; branch after undo clears redo");

        foreach(var file in new[] {"missing-top.stl","duplicate-face.stl","degenerate-face.stl","inverted-cube.stl"})
        {
            var bad = new EditorSession(); Ok(await bad.OpenAsync(Fixture(file))); var before = bad.Snapshot;
            Check((await bad.PreviewAsync(new SplitRequest(before.SelectedObjectId!.Value,new(0,0,1),10))).Outcome == Outcome.Invalid,"Damaged input accepted: " + file);
            Check(bad.Snapshot == before,"Rejected geometry changed scene.");
        }
        var missing = new EditorSession(Path.Combine(output,"missing-worker.exe")); Ok(missing.Execute(new AddCube())); var missingBefore = missing.Snapshot;
        Check(!(await missing.PreviewAsync(new SplitRequest(missingBefore.SelectedObjectId!.Value,new(0,0,1),10))).Succeeded,"Missing worker accepted.");
        Check(missing.Snapshot == missingBefore,"Missing worker changed scene.");
        Pass("Open, duplicate-face, degenerate and inverted inputs fail without repair; missing worker preserves scene");
        var fault = new EditorSession(Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/checks/Builder.WorkerFaultProbe.exe"));
        Ok(await fault.OpenAsync(Fixture("assembly-mm.3mf"))); var faultBefore = fault.Snapshot;
        foreach(var kind in Enum.GetValues<BooleanKind>())
        {
            var request = new BooleanRequest(faultBefore.Scene.Objects[0].Id,faultBefore.Scene.Objects[1].Id,kind);
            Check((await fault.PreviewAsync(request)).Outcome == Outcome.Failed,"Worker fault did not return Failed: " + kind);
            Check(fault.Snapshot == faultBefore,"Worker fault changed accepted state.");
        }
        Pass("Abrupt worker exit, malformed response and missing response preserve full session state");
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new { timestamp=DateTimeOffset.UtcNow,
            kernel="Manifold 3.5.4", commit="ce50d78021d64507f89e8c9fc2c2e51018117857", cases,
            outputs=Directory.EnumerateFiles(output).Where(p=>Path.GetExtension(p) is ".3mf" or ".stl").Select(p=>new {file=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))})
        },new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"{cases.Count} construction scenario groups passed. Evidence: {output}");
    }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static void Ok(OperationResult r) => Check(r.Succeeded,r.ToString());
    private static void Near(double actual,double expected) => Check(Math.Abs(actual-expected)<1e-7,$"Expected {expected}, got {actual}.");
    private static void SameAccepted(EditorSession s,EditorSnapshot before) => Check(s.Snapshot with {Preview=null} == before,"Preview mutated accepted state.");
    private static double Volume(Scene scene) => scene.Objects.Sum(obj => obj.Mesh.Triangles.Sum(t => {
        var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A]); var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B]); var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C]);
        var cross=Vec3.Cross(b,c); return (a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z)/6;
    }));
}
