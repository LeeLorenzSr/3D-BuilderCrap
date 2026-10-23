using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class MultiSelectionConstructionChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/multiobject-20261019/selection-construction");
        Directory.CreateDirectory(output);
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var damaged=Path.Combine(root,"fixtures/generated/missing-top.stl"); var inputHash=Hash(damaged);
        var cases=new List<string>(); var measurements=new List<object>();
        async Task<(EditorSession Session,SceneGroup Group)> Grouped()
        {
            var s=new EditorSession(worker); Ok(s.Execute(new AddCube())); var a=s.Snapshot.SelectedObjectId!.Value;
            Ok(s.Execute(new MoveObject(a,new(-80,10,10))));
            Ok(s.Execute(new AddCube())); var b=s.Snapshot.SelectedObjectId!.Value;
            Ok(s.Execute(new MoveObject(b,new(-50,10,10))));
            Ok(await s.PreviewArrangementAsync(new GroupRequest([a,b],"Unrelated selected pair")));
            Ok(s.ApplyPreview(s.Snapshot.Preview!.Id));
            return (s,s.Snapshot.Scene.Groups.Single());
        }
        async Task<EditorSnapshot> Baseline(EditorSession s,SceneGroup group,ImmutableArray<Guid> operands,string name)
        {
            // Picking one member must expand to the full unrelated group while keeping the chosen
            // member as primary. The operands are selected independently and stay ungrouped.
            Ok(s.SelectMany(operands.Insert(0,group.ObjectIds[1]),group.ObjectIds[1]));
            Check(s.Snapshot.SelectedObjectIds.SequenceEqual(s.Snapshot.Scene.Objects.Select(o=>o.Id)),"Setup did not select all group members and operands.");
            Ok(await s.SaveAsync(Path.Combine(output,name+"-baseline.3mf")));
            var obj=s.Snapshot.Scene.Objects.Single(o=>o.Id==operands[0]);
            Ok(s.Execute(new RenameObject(obj.Id,obj.Name+" draft"))); Ok(s.Undo());
            Check(s.Snapshot.CanRedo && !s.Snapshot.IsDirty,"Baseline lacks a clean saved state and redo branch.");
            return s.Snapshot;
        }
        async Task Exercise(EditorSession s,SceneGroup group,GeometryRequest request,ImmutableArray<Guid> operands,
            string name,ImmutableArray<double> resultVolumes)
        {
            var before=await Baseline(s,group,operands,name);
            var groupMembers=before.Scene.Objects.Where(o=>group.ObjectIds.Contains(o.Id)).ToImmutableArray();
            await PreviewAndCheck(); var discarded=s.Snapshot.Preview!;
            Check(s.DiscardPreview(discarded.Id).Outcome==Outcome.Cancelled,"Discard did not report cancellation.");
            Check(s.Snapshot==before,"Discard changed full selection, group metadata, saved path, revision or redo.");
            await PreviewAndCheck(); var preview=s.Snapshot.Preview!;
            var expectedIds=ExpectedIds(preview,group); var primary=preview.ResultObjectIds.IsEmpty ? before.SelectedObjectId : preview.ResultObjectIds[0];
            Check(preview.SelectedObjectIds.SequenceEqual(expectedIds),"Preview omitted unrelated selected group or new result leaves.");
            Check(preview.SelectedObjectId==primary,"Preview primary selection did not select the result or retain unrelated primary for an empty result.");
            Ok(s.ApplyPreview(preview.Id,before.Revision)); var applied=s.Snapshot;
            Check(applied.Revision==before.Revision+1 && applied.Preview is null && applied.IsDirty && !applied.CanRedo,"Apply did not commit once and replace redo.");
            State(s,preview.Scene,expectedIds,primary); UnchangedGroup(s.Snapshot.Scene,group,groupMembers);
            Check(applied.Scene.Objects.Where(o=>preview.ResultObjectIds.Contains(o.Id)).Select(o=>o.Id).SequenceEqual(preview.ResultObjectIds),"Accepted result IDs changed.");
            Ok(s.Undo()); State(s,before.Scene,before.SelectedObjectIds,before.SelectedObjectId);
            Check(!s.Snapshot.IsDirty && s.Snapshot.FilePath==before.FilePath,"Undo did not restore the clean baseline boundary.");
            UnchangedGroup(s.Snapshot.Scene,group,groupMembers);
            Ok(s.Redo()); State(s,applied.Scene,expectedIds,primary); Check(s.Snapshot.IsDirty,"Redo to unsaved result should be dirty.");
            var saved=Path.Combine(output,name+"-accepted.3mf"); Ok(await s.SaveAsync(saved)); var savedPath=s.Snapshot.FilePath;
            Check(!s.Snapshot.IsDirty && s.Snapshot.CanUndo,"Save lost clean state or history.");
            Ok(s.Undo()); State(s,before.Scene,before.SelectedObjectIds,before.SelectedObjectId);
            Check(s.Snapshot.IsDirty && s.Snapshot.FilePath==savedPath,"Undo across result save lost dirty state or native path.");
            Ok(s.Redo()); State(s,applied.Scene,expectedIds,primary);
            Check(!s.Snapshot.IsDirty && s.Snapshot.FilePath==savedPath,"Redo lost saved result identity or filename.");
            UnchangedGroup(s.Snapshot.Scene,group,groupMembers);
            Check(!s.Snapshot.SelectedObjectIds.Any(id=>!s.Snapshot.Scene.Objects.Any(o=>o.Id==id)),"Selection contains consumed operand IDs.");
            measurements.Add(new {operation=name,request=request.GetType().Name,groupId=group.Id,
                beforeSelectedObjectIds=before.SelectedObjectIds,beforePrimary=before.SelectedObjectId,
                resultObjectIds=preview.ResultObjectIds,acceptedSelectedObjectIds=expectedIds,acceptedPrimary=primary,
                resultVolumesMm3=preview.Scene.Objects.Where(o=>preview.ResultObjectIds.Contains(o.Id)).Select(Volume),
                completeSceneVolumeMm3=preview.Scene.Objects.Sum(Volume),output=Path.GetFileName(saved)});
            cases.Add(name); Console.WriteLine("PASS "+name+": unrelated selected group, preview/discard, apply, full-selection undo/redo and saved boundary");

            async Task PreviewAndCheck()
            {
                Ok(await s.PreviewAsync(request,expectedRevision:before.Revision));
                Check(s.Snapshot with {Preview=null}==before,"Geometry preview mutated accepted state or full selection.");
                var p=s.Snapshot.Preview!; UnchangedGroup(p.Scene,group,groupMembers);
                Check(p.SelectedObjectIds.SequenceEqual(ExpectedIds(p,group)),"Predicted full selection differs from group members plus result leaves.");
                var actual=p.Scene.Objects.Where(o=>p.ResultObjectIds.Contains(o.Id)).Select(Volume).Order().ToArray();
                Check(actual.Length==resultVolumes.Length,"Wrong number of geometry result pieces.");
                foreach(var (a,b) in actual.Zip(resultVolumes.Order())) Near(a,b);
                Near(p.Scene.Objects.Sum(Volume),16000+resultVolumes.Sum());
            }
        }

        var (repair,repairGroup)=await Grouped(); Ok(await repair.InsertAsync(damaged)); var repairId=repair.Snapshot.SelectedObjectId!.Value;
        Ok(repair.Execute(new MoveObject(repairId,new(110,10,10))));
        await Exercise(repair,repairGroup,new RepairRequest(repairId),[repairId],"repair-selected-leaf-with-selected-group",[8000]);

        var (split,splitGroup)=await Grouped(); Ok(split.Execute(new AddCube())); var splitId=split.Snapshot.SelectedObjectId!.Value;
        Ok(split.Execute(new MoveObject(splitId,new(110,10,10))));
        await Exercise(split,splitGroup,new SplitRequest(splitId,new(0,0,1),10,SplitKeep.Both),[splitId],
            "split-selected-leaf-with-selected-group",[4000,4000]);

        foreach(var (kind,volume) in new[] {(BooleanKind.Union,12000d),(BooleanKind.Subtract,4000d),(BooleanKind.Intersect,4000d)})
        {
            var (s,group)=await Grouped(); Ok(s.Execute(new AddCube())); var a=s.Snapshot.SelectedObjectId!.Value;
            Ok(s.Execute(new MoveObject(a,new(110,10,10)))); Ok(s.Execute(new AddCube())); var b=s.Snapshot.SelectedObjectId!.Value;
            Ok(s.Execute(new MoveObject(b,new(120,10,10))));
            await Exercise(s,group,new BooleanRequest(a,b,kind),[a,b],kind.ToString().ToLowerInvariant()+"-selected-operands-with-selected-group",[volume]);
        }
        var (empty,emptyGroup)=await Grouped(); Ok(empty.Execute(new AddCube())); var emptyA=empty.Snapshot.SelectedObjectId!.Value;
        Ok(empty.Execute(new MoveObject(emptyA,new(110,10,10)))); Ok(empty.Execute(new AddCube())); var emptyB=empty.Snapshot.SelectedObjectId!.Value;
        Ok(empty.Execute(new MoveObject(emptyB,new(150,10,10))));
        await Exercise(empty,emptyGroup,new BooleanRequest(emptyA,emptyB,BooleanKind.Intersect),[emptyA,emptyB],
            "empty-intersection-retains-selected-group-and-primary",[]);

        Check(Hash(damaged)==inputHash,"The damaged synthetic input changed.");
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {
            timestamp=DateTimeOffset.UtcNow,observation="Public Core API runtime checks; no desktop/original-app observation",
            target=new {name="Builder.Core",version=typeof(EditorSession).Assembly.GetName().Version?.ToString(),sha256=Hash(typeof(EditorSession).Assembly.Location)},
            tools=new {runtime=Environment.Version.ToString(),workerSha256=Hash(worker)},
            inputs=new[] {new {file=Path.GetRelativePath(root,damaged),sha256=inputHash}},
            cases,measurements,limitations=new[] {"Focused analytic cubes plus one damaged cube; no general repair or arbitrary mesh guarantee.",
                "Group and selected leaf IDs are session-local identities; these checks exercise live session history, not interchange identity persistence.",
                "Only output writes are performed; no desktop input, rendering or native UI parity is claimed."},
            outputs=Directory.EnumerateFiles(output,"*.3mf").Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)})
        },new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"{cases.Count} multi-selection construction scenario groups passed. Evidence: {output}");
    }
    private static ImmutableArray<Guid> ExpectedIds(GeometryPreview p,SceneGroup group)
    {
        var desired=group.ObjectIds.Concat(p.ResultObjectIds).ToHashSet();
        return p.Scene.Objects.Where(o=>desired.Contains(o.Id)).Select(o=>o.Id).ToImmutableArray();
    }
    private static void UnchangedGroup(Scene scene,SceneGroup original,ImmutableArray<SceneObject> members)
    {
        Check(scene.Groups.Length==1 && scene.Groups[0]==original,"Unrelated group ID/name/member metadata changed.");
        foreach(var member in members)
        {
            var current=scene.Objects.Single(o=>o.Id==member.Id);
            Check(current==member && ReferenceEquals(current.Mesh,member.Mesh),"Unrelated group member identity/mesh/transform changed.");
        }
    }
    private static void State(EditorSession s,Scene scene,ImmutableArray<Guid> selected,Guid? primary)
    { Check(s.Snapshot.Scene==scene && s.Snapshot.SelectedObjectIds.SequenceEqual(selected) && s.Snapshot.SelectedObjectId==primary,"History did not restore complete scene/selection/primary."); }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-7,$"Expected {b:R}, got {a:R}.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static double Volume(SceneObject obj)
    {
        var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);
        return obj.Mesh.Triangles.Sum(t=> {
            var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin; var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin; var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin;
            var n=Vec3.Cross(b,c); return (a.X*n.X+a.Y*n.Y+a.Z*n.Z)/6;
        });
    }
}
