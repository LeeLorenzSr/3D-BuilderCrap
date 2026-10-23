using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class SelectionExportChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/group-editing-20261019/exports"); Directory.CreateDirectory(output);
        var cases=new List<string>(); var measurements=new List<object>();
        void Pass(string name) {cases.Add(name); Console.WriteLine("PASS "+name);}
        var session=new EditorSession(); Ok(await session.OpenAsync(Path.Combine(root,"fixtures/generated/assembly-mm.3mf")));
        Ok(session.Execute(new AddPrimitive(PrimitiveKind.Cube,new(10,30,40))));
        var third=session.Snapshot.SelectedObjectId!.Value; Ok(session.Execute(new MoveObject(third,new(80,-15,40))));
        var ids=session.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(await session.PreviewArrangementAsync(new GroupRequest([ids[0],ids[1]],"Export pair & brackets"))); Ok(session.ApplyPreview(session.Snapshot.Preview!.Id));
        var native=Path.Combine(output,"whole-scene.3mf"); Ok(await session.SaveAsync(native));
        Ok(session.Execute(new RenameObject(third,"Redo marker"))); Ok(session.Undo());
        Ok(session.Select(third)); var before=session.Snapshot;
        Check(before.CanRedo && !before.IsDirty,"Export probe must retain a saved boundary and redo branch.");

        var selected3mf=Path.Combine(output,"selected-group.3mf");
        Ok(await session.ExportSelectionAsync(selected3mf,[ids[1]],expectedRevision:before.Revision)); Same(session,before);
        var opened=new EditorSession(); Ok(await opened.OpenAsync(selected3mf));
        Check(opened.Snapshot.Scene.Objects.Length==2 && opened.Snapshot.Scene.Groups.Length==1,"Selecting one member did not export the whole group.");
        Check(opened.Snapshot.Scene.Groups.Single().Name=="Export pair & brackets","Selection export lost group name.");
        Bounds(opened.Snapshot.Scene.Bounds,new(0,0,0),new(50,20,20));
        Check(opened.Snapshot.Scene.Objects.All(o=>!ids.Contains(o.Id)),"Interchange reopen reused session leaf IDs.");
        var selectedStl=Path.Combine(output,"selected-group.stl");
        Ok(await session.ExportSelectionAsync(selectedStl,[ids[0],ids[0]])); Same(session,before);
        Ok(await opened.OpenAsync(selectedStl)); Check(opened.Snapshot.Scene.TriangleCount==24,"STL selection export lost group triangles.");
        Bounds(opened.Snapshot.Scene.Bounds,new(0,0,0),new(50,20,20));
        var onlyThird=Path.Combine(output,"selected-third.3mf"); Ok(await session.ExportSelectionAsync(onlyThird,[third])); Same(session,before);
        Ok(await opened.OpenAsync(onlyThird)); Check(opened.Snapshot.Scene.Objects.Length==1 && opened.Snapshot.Scene.Groups.IsEmpty,"Ungrouped subset included unrelated members/groups.");
        Bounds(opened.Snapshot.Scene.Bounds,new(75,-30,20),new(85,0,60));
        var all=Path.Combine(output,"explicit-all.3mf"); Ok(await session.ExportSelectionAsync(all,[third,ids[1]])); Same(session,before);
        Ok(await opened.OpenAsync(all)); Check(opened.Snapshot.Scene.Objects.Length==3 && opened.Snapshot.Scene.Groups.Length==1,"Explicit mixed subset lost group expansion.");
        Check(session.Snapshot.SelectedObjectId==third && session.Snapshot.SelectedObjectIds.SequenceEqual(new[]{third}),"Explicit export changed current selection.");
        Ok(session.Redo()); var dirty=session.Snapshot; Check(dirty.IsDirty,"Redo marker failed.");
        Ok(await session.ExportSelectionAsync(Path.Combine(output,"dirty-selected-group.3mf"),[ids[0]])); Same(session,dirty);
        Ok(session.Undo()); SameContent(session,before); before=session.Snapshot;
        measurements.Add(new {file="selected-group.3mf / .stl",objects=2,groups=1,triangles=24,bounds=new Bounds3(new(0,0,0),new(50,20,20)),summedVolumeMm3=16000});
        measurements.Add(new {file="selected-third.3mf",objects=1,groups=0,triangles=12,bounds=new Bounds3(new(75,-30,20),new(85,0,60)),summedVolumeMm3=12000});
        measurements.Add(new {file="explicit-all.3mf",objects=3,groups=1,triangles=36,bounds=new Bounds3(new(0,-30,0),new(85,20,60)),summedVolumeMm3=28000});
        Pass("Explicit member/duplicate/mixed targets expand whole groups; 3MF/STL subsets roundtrip and preserve selection, native path, dirty/save boundary and redo");

        var nativeBytes=File.ReadAllBytes(native);
        foreach(var target in new[]{native,native.ToUpperInvariant(),Path.Combine(output,".","whole-scene.3mf")})
        {Check((await session.ExportSelectionAsync(target,[ids[0]])).Outcome==Outcome.Invalid,"Selection export replaced its native document path."); Same(session,before); Check(File.ReadAllBytes(native).SequenceEqual(nativeBytes),"Native path guard changed document bytes.");}
        foreach(var selection in new[]{default(ImmutableArray<Guid>),ImmutableArray<Guid>.Empty,ImmutableArray.Create(Guid.NewGuid()),ImmutableArray.Create(session.Snapshot.Scene.Groups[0].Id),Enumerable.Repeat(ids[0],1001).ToImmutableArray()})
        {Check((await session.ExportSelectionAsync(Path.Combine(output,"invalid-selection.3mf"),selection)).Outcome==Outcome.Invalid,"Invalid leaf selection accepted."); Same(session,before);}
        Check((await session.ExportSelectionAsync(Path.Combine(output,"unsupported.obj"),[ids[0]])).Outcome==Outcome.Invalid,"Unsupported selection format accepted."); Same(session,before);
        Check((await session.ExportSelectionAsync(Path.Combine(output,"stale.3mf"),[ids[0]],expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale selection export started."); Same(session,before);
        Check(!File.Exists(Path.Combine(output,"invalid-selection.3mf")) && !File.Exists(Path.Combine(output,"stale.3mf")),"Rejected request wrote a destination.");
        Pass("Current-native exact/case/dot paths, default/empty/unknown/group IDs, oversized input, unsupported formats and stale revisions reject without writing or mutation");

        Ok(await session.PreviewTransformAsync(new MoveTransform(ids[0],new(2,0,0)))); var previewed=session.Snapshot;
        Check((await session.ExportSelectionAsync(Path.Combine(output,"unaccepted.3mf"),[ids[0]])).Outcome==Outcome.Conflict,"Selection export bypassed preview guard."); Same(session,previewed);
        Check(session.DiscardPreview(previewed.Preview!.Id).Outcome==Outcome.Cancelled,"Discard failed."); Same(session,before);
        using(var entered=new ManualResetEventSlim()) using(var release=new ManualResetEventSlim()) using(var ct=new CancellationTokenSource())
        {
            var job=session.ExportSelectionAsync(Path.Combine(output,"busy.3mf"),[ids[0]],ct.Token,new Inline(p=>{
                if(p.Stage=="Preparing selection export") {entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();}}));
            Check(entered.Wait(TimeSpan.FromSeconds(5)),"Selection export did not acquire its job lease.");
            try
            {
                Check(session.Select(third).Outcome==Outcome.Busy && session.Undo().Outcome==Outcome.Busy && session.Execute(new AddCube()).Outcome==Outcome.Busy,"Mutation escaped selection-export lease.");
                Check((await session.ExportSelectionAsync(Path.Combine(output,"concurrent.stl"),[ids[0]])).Outcome==Outcome.Busy,"Concurrent export escaped lease.");
                Check((await session.SaveAsync(native)).Outcome==Outcome.Busy,"Save escaped lease."); ct.Cancel();
            }
            finally {release.Set();}
            Check((await job).Outcome==Outcome.Cancelled,"Busy export did not cancel."); Same(session,before);
        }
        Pass("Preview and busy guards serialize selection export with selection/history/edits/save; cancellation releases the lease intact");

        var lastGood=Path.Combine(output,"last-good.3mf"); File.Copy(selected3mf,lastGood,true); var goodBytes=File.ReadAllBytes(lastGood);
        using(var ct=new CancellationTokenSource())
        {ct.Cancel(); Check((await session.ExportSelectionAsync(lastGood,[ids[0]],ct.Token)).Outcome==Outcome.Cancelled,"Pre-cancelled export wrote."); Same(session,before);}
        foreach(var stage in new[]{"Preparing selection export","Preparing selected objects","Preparing selected groups","Preparing save","Writing vertices","Writing groups","Ready to save"})
        {
            using var ct=new CancellationTokenSource(); var reached=false;
            var result=await session.ExportSelectionAsync(lastGood,[ids[0]],ct.Token,new Inline(p=>{
                if(p.Stage==stage && (stage!="Preparing selected objects" || p.Completed==1)) {reached=true;ct.Cancel();}}));
            Check(reached && result.Outcome==Outcome.Cancelled,"Cancellation stage not exercised: "+stage); Same(session,before);
            Check(File.ReadAllBytes(lastGood).SequenceEqual(goodBytes),"Cancellation replaced last-good destination: "+stage);
            Check(!Directory.EnumerateFiles(output,"*.tmp").Any(),"Cancellation left a temporary file.");
        }
        var stlGood=File.ReadAllBytes(selectedStl);
        using(var ct=new CancellationTokenSource())
        {
            var reached=false; var result=await session.ExportSelectionAsync(selectedStl,[ids[0]],ct.Token,new Inline(p=>{if(p.Stage=="Writing triangles"){reached=true;ct.Cancel();}}));
            Check(reached && result.Outcome==Outcome.Cancelled && File.ReadAllBytes(selectedStl).SequenceEqual(stlGood),"STL cancellation replaced destination."); Same(session,before);
        }
        Pass("Pre-cancel and private subset/group preparation, mesh/group/STL writing and pre-replace cancellation retain accepted state and last-good files with no temporary leftovers");

        using(var locked=new FileStream(lastGood,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {Check((await session.ExportSelectionAsync(lastGood,[ids[0]])).Outcome==Outcome.Failed,"Locked destination accepted selection export."); Same(session,before);}
        Check(File.ReadAllBytes(lastGood).SequenceEqual(goodBytes),"Writer fault replaced last-good selection file.");
        Check((await session.ExportSelectionAsync(Path.Combine(output,"missing-parent","selection.stl"),[ids[0]])).Outcome==Outcome.Failed,"Missing destination directory accepted."); Same(session,before);
        Check(!Directory.EnumerateFiles(output,"*.tmp").Any(),"Writer fault left a temporary file.");
        Check(File.ReadAllBytes(native).SequenceEqual(nativeBytes),"Any selection export changed the native document bytes.");
        Pass("Locked/missing-folder writer faults preserve document/native bytes and last-good subset destination; temporary files are removed");

        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,runtime=Environment.Version.ToString(),cases,measurements,
            coreSha256=Hash(typeof(EditorSession).Assembly.Location),outputs=Directory.GetFiles(output).Where(p=>Path.GetExtension(p) is ".3mf" or ".stl").Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)})},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} selection-export scenario groups passed. Evidence: {output}");
    }
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Check(bool value,string message) {if(!value) throw new InvalidOperationException(message);}
    private static void Same(EditorSession session,EditorSnapshot snapshot)=>Check(session.Snapshot==snapshot,"Selection export changed session state.");
    private static void SameContent(EditorSession session,EditorSnapshot snapshot)
    {var after=session.Snapshot; Check(after.Scene==snapshot.Scene && after.SelectedObjectId==snapshot.SelectedObjectId && after.SelectedObjectIds.SequenceEqual(snapshot.SelectedObjectIds) && after.FilePath==snapshot.FilePath && after.IsDirty==snapshot.IsDirty && after.CanRedo==snapshot.CanRedo,"History failed to restore export baseline.");}
    private static void Bounds(Bounds3 actual,Vec3 min,Vec3 max)
    {Check(Math.Abs(actual.Min.X-min.X)<1e-8 && Math.Abs(actual.Min.Y-min.Y)<1e-8 && Math.Abs(actual.Min.Z-min.Z)<1e-8 && Math.Abs(actual.Max.X-max.X)<1e-8 && Math.Abs(actual.Max.Y-max.Y)<1e-8 && Math.Abs(actual.Max.Z-max.Z)<1e-8,"Unexpected selection output dimensions.");}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class Inline(Action<OperationProgress> report):IProgress<OperationProgress> {public void Report(OperationProgress p)=>report(p);}
}
