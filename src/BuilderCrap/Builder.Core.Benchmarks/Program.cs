using Builder.Core;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

// Usage: dotnet run -c Release --project ... -p:BuilderCoreAssembly=<pinned DLL> -- <root> <report.json>
var root=Path.GetFullPath(args[0]); var output=Path.GetFullPath(args[1]);
var rows=new List<Row>(); var cancellations=new List<object>(); var history=new List<object>();
var assembly=typeof(EditorSession).Assembly.Location; var binaryHash=Hash(assembly);
var fixtureNames=new[]{"dense-100000.stl","objects-1000.3mf","combined-1000-100000.3mf"};
var inputs=new List<object>(); var started=DateTimeOffset.UtcNow;
var pick=typeof(EditorSession).GetMethod("PickAsync");
const int warmups=3, samples=20;
foreach(var file in fixtureNames)
{
    var input=Path.Combine(root,".local/evidence/performance-20261019/inputs",file); var inputHash=Hash(input);
    var session=new EditorSession();
    Ok(await Timed(file,"open",0,()=>session.OpenAsync(input)));
    var original=session.Snapshot; var target=original.Scene.Objects[0]; var ids=original.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
    inputs.Add(new{file,sha256=inputHash,objects=ids.Length,triangles=original.Scene.TriangleCount,vertices=original.Scene.Objects.Sum(o=>o.Mesh.Vertices.Length)});
    var rayCenter=target.Bounds.Center;
    if(pick!=null) await Timed(file,"core-pick-first",0,()=>CorePick(session,new(rayCenter.X+0.123,rayCenter.Y+0.321,300),new(0,0,-1)));
    for(var i=-warmups;i<samples;i++)
    {
        await Timed(file,"scene-bounds",i,()=>{_=session.Snapshot.Scene.Bounds;return Task.FromResult(true);});
        await Timed(file,"selection-units",i,()=>{_=original.Scene.GetSelectionUnits(ids);return Task.FromResult(true);});
        await Timed(file,"validate",i,()=>{original.Scene.Validate();return Task.FromResult(true);});
    }
    foreach(var mode in new[]{"move","rotate","scale"})
    {
        Guid? preview=null;
        for(var i=-warmups;i<samples;i++)
        {
            TransformRequest request=mode switch{
                "move"=>new MoveTransform(target.Id,new(0.25*(i+warmups+1),0,0),0.1),
                "rotate"=>new RotateTransform(target.Id,new(7,13+(i+warmups)*0.2,19),1),
                _=>new ScaleTransform(target.Id,new(1.01+(i+warmups)*0.001,0.99,1.01),0.1)};
            request=request with {ObjectIds=ids};
            Ok(await Timed(file,mode+"-preview",i,()=>session.PreviewTransformAsync(request,preview)));
            preview=session.Snapshot.Preview!.Id; Accepted(session,original);
        }
        Ok(await Timed(file,mode+"-apply",0,()=>Task.FromResult(session.ApplyPreview(preview!.Value))));
        Ok(await Timed(file,mode+"-undo",0,()=>Task.FromResult(session.Undo())));
        Check(ReferenceEquals(session.Snapshot.Scene,original.Scene),"Undo changed source identity.");
        Ok(await Timed(file,mode+"-redo",0,()=>Task.FromResult(session.Redo()))); Ok(session.Undo());
        var accepted=session.Snapshot;
        Ok(await session.PreviewTransformAsync(requestForCancel()));
        var prior=session.Snapshot;
        foreach(var stage in new[]{"Preparing transform","Transforming vertices","Ready to preview"})
        {
            using var cts=new CancellationTokenSource(); long signal=0;
            var result=await Timed(file,mode+"-cancel-"+stage,0,()=>session.PreviewTransformAsync(requestForCancel(),prior.Preview!.Id,cancellation:cts.Token,
                progress:new DirectProgress(p=>{if(p.Stage==stage && signal==0){signal=Stopwatch.GetTimestamp();cts.Cancel();}})));
            Check(result.Outcome==Outcome.Cancelled,"Cancellation outcome."); Check(session.Snapshot==prior,"Cancellation mutated accepted state or prior preview.");
            Check(signal!=0,"Cancellation stage not observed: "+stage);
            cancellations.Add(new{file,mode,stage,signalToCompletionMilliseconds=Stopwatch.GetElapsedTime(signal).TotalMilliseconds});
        }
        Check(session.DiscardPreview(prior.Preview!.Id).Outcome==Outcome.Cancelled,"Discard outcome.");
        Check(session.Snapshot==accepted,"Discard changed accepted state.");
        TransformRequest requestForCancel()=>(mode switch{
            "move"=>(TransformRequest)new MoveTransform(target.Id,new(3,2,1)),
            "rotate"=>new RotateTransform(target.Id,new(7,13,19)),
            _=>new ScaleTransform(target.Id,new(1.01,0.99,1.01))}) with {ObjectIds=ids};
    }
    // Baseline has no Core picker. Record the same uncached brute triangle oracle separately;
    // optimized runs additionally measure the real public PickAsync query through reflection.
    for(var i=-warmups;i<samples;i++)
    {
        foreach(var miss in new[]{false,true})
        {
            var origin=miss ? new Vec3(-100,-100,300) : new Vec3(rayCenter.X+0.123+i*0.013,rayCenter.Y+0.321,300);
            var direction=new Vec3(0,0,-1);var suffix=miss ? "-miss" : "-hit";
            var expected=await Timed(file,"reference-brute-ray"+suffix,i,()=>Task.FromResult(Brute(original.Scene,origin,direction)));
            Check(expected.HasValue!=miss,"Expected ray fixture did not hit/miss.");
            if(pick!=null)
            {
                var distance=await Timed(file,"core-pick"+suffix,i,()=>CorePick(session,origin,direction));
                Check(distance.HasValue==expected.HasValue && (!distance.HasValue || Math.Abs(distance.Value-expected!.Value)<1e-7),"Core ray disagrees with independent brute oracle.");
            }
        }
    }
    // Five cycles distinguish weak-table capacity growth from unbounded retained history.
    for(var cycle=0;cycle<5;cycle++)
    {
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var before=GC.GetTotalMemory(true);
        for(var step=0;step<100;step++)
        {
            Ok(await Timed(file,"history-preview",step,()=>session.PreviewTransformAsync(new MoveTransform(target.Id,new(0.01,0,0)){ObjectIds=ids})));
            Ok(session.ApplyPreview(session.Snapshot.Preview!.Id));
        }
        var retained=GC.GetTotalMemory(true)-before;
        for(var step=0;step<100;step++) Ok(await Timed(file,"history-undo",step,()=>Task.FromResult(session.Undo())));
        Check(ReferenceEquals(session.Snapshot.Scene,original.Scene),"100 Undo steps did not restore exact original scene.");
        Accepted(session,original);
        for(var step=0;step<100;step++) Ok(await Timed(file,"history-redo",step,()=>Task.FromResult(session.Redo())));
        for(var step=0;step<100;step++) Ok(session.Undo());
        history.Add(new{file,cycle,incrementalRetainedManagedBytes=retained,liveManagedBytes=GC.GetTotalMemory(true)});
    }
    Check(Hash(input)==inputHash,"Fixture changed."); Console.WriteLine("Finished "+file);
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var summaries=rows.Where(r=>r.Index>=0).GroupBy(r=>(r.File,r.Stage)).Select(g=>{
    var times=g.Select(x=>x.Milliseconds).Order().ToArray();var allocations=g.Select(x=>x.ManagedAllocatedBytes).Order().ToArray();
    return new{g.Key.File,g.Key.Stage,count=times.Length,min=times[0],median=Percentile(times,0.5),p95=Percentile(times,0.95),max=times[^1],
        medianAllocatedBytes=Percentile(allocations.Select(x=>(double)x).ToArray(),0.5),totalAllocatedBytes=allocations.Sum()};}).ToArray();
File.WriteAllText(output,JsonSerializer.Serialize(new{started,finished=DateTimeOffset.UtcNow,target=new{assembly,sha256=binaryHash,version=typeof(EditorSession).Assembly.GetName().Version?.ToString()},
    tools=new{runtime=Environment.Version.ToString(),framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),Environment.ProcessorCount,serverGc=System.Runtime.GCSettings.IsServerGC,
        sourceSha256=Hash(Path.Combine(root,"src/BuilderCrap/Builder.Core.Benchmarks/Program.cs"))},
    method=new{warmups,samples,percentile="Linear interpolation p*(n-1)",allocation="Process-wide GC.GetTotalAllocatedBytes(true) delta; not native/peak/live memory",cold="First row (index -3) of each operation retained; first-ever process operation is Open; core-pick-first is first query after Open and target bounds; only first fixture is process-cold"},
    goals=new{warmPickP95Milliseconds=16,coldPickMilliseconds=250,transformP95Milliseconds=33,undoRedoMilliseconds=5,cancelMilliseconds=100,history100IncrementalBytes=33554432},
    inputs,rows,summaries,cancellations,history,limitations=new[]{"Single machine sequential process; no UI or worker timing; filesystem caches are not purged.","Three fixed warmups do not guarantee stable tiered JIT; raw warmup/cold rows are retained and fresh-process repeats are recommended.","Baseline has no portable Core picker; reference-brute-ray is harness oracle, not a baseline product API.","Core picking includes reflection adapter allocation/timing overhead; side tests and affine oracles belong to focused safety checks.","Five history cycles report incremental managed live retention after full GC, not process peak or native memory."}},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(output);

async Task<T> Timed<T>(string file,string stage,int index,Func<Task<T>> action){var bytes=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();var value=await action();watch.Stop();rows.Add(new(file,stage,index,watch.Elapsed.TotalMilliseconds,GC.GetTotalAllocatedBytes(true)-bytes));return value;}
async Task<double?> CorePick(EditorSession session,Vec3 origin,Vec3 direction)
{
    var values=pick!.GetParameters().Select(p=>p.DefaultValue).ToArray();values[0]=origin;values[1]=direction;values[4]=CancellationToken.None;
    var task=(Task)pick.Invoke(session,values)!;await task;
    var hit=task.GetType().GetProperty("Result")!.GetValue(task);
    return hit is null ? null : (double)hit.GetType().GetProperty("Distance")!.GetValue(hit)!;
}
static void Check(bool yes,string message){if(!yes)throw new Exception(message);}
static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
static void Accepted(EditorSession session,EditorSnapshot original){var current=session.Snapshot;Check(ReferenceEquals(current.Scene,original.Scene)&&current.FilePath==original.FilePath&&current.IsDirty==original.IsDirty&&current.SelectedObjectIds.SequenceEqual(original.SelectedObjectIds),"Preview changed accepted document.");}
static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
static double Percentile(double[] values,double p){var rank=p*(values.Length-1);var lo=(int)rank;return values[lo]+(values[Math.Min(lo+1,values.Length-1)]-values[lo])*(rank-lo);}
static double? Brute(Scene scene,Vec3 origin,Vec3 direction){double nearest=double.PositiveInfinity;foreach(var obj in scene.Objects)foreach(var t in obj.Mesh.Triangles){var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A]);var e1=obj.Transform.Direction(obj.Mesh.Vertices[t.B]-obj.Mesh.Vertices[t.A]);var e2=obj.Transform.Direction(obj.Mesh.Vertices[t.C]-obj.Mesh.Vertices[t.A]);var p=Vec3.Cross(direction,e2);var determinant=Dot(e1,p);if(determinant==0)continue;var q=origin-a;var u=Dot(q,p)/determinant;if(u<0||u>1)continue;var c=Vec3.Cross(q,e1);var v=Dot(direction,c)/determinant;if(v<0||u+v>1)continue;var distance=Dot(e2,c)/determinant;if(distance>=0&&distance<nearest)nearest=distance;}return double.IsPositiveInfinity(nearest)?null:nearest;}
static double Dot(Vec3 a,Vec3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
sealed record Row(string File,string Stage,int Index,double Milliseconds,long ManagedAllocatedBytes);
sealed class DirectProgress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress value)=>action(value);}
