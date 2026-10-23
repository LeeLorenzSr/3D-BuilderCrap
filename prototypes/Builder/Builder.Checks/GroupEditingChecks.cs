using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class GroupEditingChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/group-editing-20261019/core"); Directory.CreateDirectory(output);
        var fixture=Path.Combine(root,"fixtures/generated/assembly-mm.3mf"); var fixtureHash=Hash(fixture);
        var cases=new List<string>(); var measurements=new List<object>();
        void Pass(string message) { cases.Add(message); Console.WriteLine("PASS "+message); }
        async Task<EditorSession> Setup()
        {
            var s=new EditorSession(); Ok(await s.OpenAsync(fixture));
            Ok(s.Execute(new AddPrimitive(PrimitiveKind.Cube,new(10,30,40)))); var third=s.Snapshot.SelectedObjectId!.Value;
            Ok(s.Execute(new MoveObject(third,new(80,-15,40))));
            var leaves=s.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
            Ok(await s.PreviewArrangementAsync(new GroupRequest([leaves[0],leaves[1]],"Pair"))); Ok(s.ApplyPreview(s.Snapshot.Preview!.Id));
            Ok(s.SelectMany([leaves[1],third],leaves[1])); return s;
        }
        var editor=await Setup(); var baseline=editor.Snapshot; var ids=baseline.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        var group=baseline.Scene.Groups.Single(); var tree=baseline.Scene.GetObjectTree();
        Check(tree.Length==2 && tree[0].Id==group.Id && tree[0].Name==group.Name,"Tree roots did not replace grouped members in document order.");
        Check(tree[0].ObjectIds.SequenceEqual(ids.Take(2)) && tree[0].Children.Select(n=>n.Id).SequenceEqual(ids.Take(2)),"Group member children/leaf target order changed.");
        Check(tree[0].Children.All(n=>n.Children.IsEmpty && n.ObjectIds.SequenceEqual(new[]{n.Id})),"Tree child is not a leaf.");
        Check(tree[1].Id==ids[2] && tree[1].Children.IsEmpty && tree[1].ObjectIds.SequenceEqual(new[]{ids[2]}),"Ungrouped leaf root is wrong.");
        Same(editor,baseline); Ok(editor.Select(tree[0].Children[1].Id));
        Check(editor.Snapshot.SelectedObjectIds.SequenceEqual(ids.Take(2)) && editor.Snapshot.SelectedObjectId==ids[1],"A member-row target bypassed whole-group selection.");
        Ok(editor.SelectMany(ids,ids[1]));
        Pass("Immutable group/member tree metadata in document order; queries do not mutate document and member picks expand whole groups");

        var beforeRename=editor.Snapshot; const string renamedName="Print plate & brackets <A>";
        Ok(editor.Execute(new RenameGroup(group.Id,"  "+renamedName+"  "),beforeRename.Revision));
        var renamed=editor.Snapshot;
        Check(renamed.Scene.Groups.Single().Id==group.Id && renamed.Scene.Groups.Single().Name==renamedName,"Group rename changed identity or did not trim.");
        Check(renamed.Scene.Objects.SequenceEqual(beforeRename.Scene.Objects),"Group rename touched geometry or leaf identity.");
        Selection(renamed,beforeRename.SelectedObjectIds,beforeRename.SelectedObjectId); Check(renamed.Revision==beforeRename.Revision+1,"Rename did not commit once.");
        Ok(editor.Execute(new RenameGroup(group.Id," "+renamedName+" "))); Same(editor,renamed);
        foreach(var name in new[] {"","  ",new string('x',101),"bad\nname","\tPair",null!})
        { Check(editor.Execute(new RenameGroup(group.Id,name)).Outcome==Outcome.Invalid,"Invalid group name accepted."); Same(editor,renamed); }
        Check(editor.Execute(new RenameGroup(Guid.NewGuid(),"Missing")).Outcome==Outcome.Invalid,"Unknown group renamed."); Same(editor,renamed);
        Check(editor.Execute(new RenameGroup(group.Id,"Stale"),renamed.Revision-1).Outcome==Outcome.Conflict,"Stale rename accepted."); Same(editor,renamed);
        Ok(editor.Undo()); State(editor,beforeRename.Scene,beforeRename.SelectedObjectIds,beforeRename.SelectedObjectId);
        Ok(editor.Redo()); State(editor,renamed.Scene,renamed.SelectedObjectIds,renamed.SelectedObjectId);
        var renamedPath=Path.Combine(output,"renamed.3mf"); Ok(await editor.SaveAsync(renamedPath)); var beforeCopy=editor.Snapshot;
        var reopened=new EditorSession(); Ok(await reopened.OpenAsync(renamedPath));
        Check(reopened.Snapshot.Scene.Groups.Single().Name==renamedName,"Renamed group failed XML/native persistence.");
        Pass("Group rename validation/no-op/stale guard, stable geometry/selection/group ID, one-step history and XML-escaped save/reopen");

        // Both group members and an independent leaf are copied as one placement set. Duplicate input
        // IDs and a single group-member target must canonicalize without producing duplicate copies.
        Ok(await editor.DuplicateSelectionAsync([ids[1],ids[2],ids[1]],expectedRevision:beforeCopy.Revision));
        var duplicated=editor.Snapshot; var copies=duplicated.Scene.Objects.Skip(3).ToImmutableArray();
        Check(duplicated.Revision==beforeCopy.Revision+1 && duplicated.Scene.Objects.Length==6 && duplicated.Scene.Groups.Length==2,"Duplicate did not commit the whole set once.");
        Check(duplicated.FilePath==beforeCopy.FilePath && duplicated.IsDirty && !duplicated.CanRedo,"Duplicate changed native path/saved boundary or redo branch.");
        Check(duplicated.Scene.Objects.Take(3).SequenceEqual(beforeCopy.Scene.Objects) && duplicated.Scene.Groups[0]==beforeCopy.Scene.Groups[0],"Duplicate modified originals.");
        var allIds=duplicated.Scene.Objects.Select(o=>o.Id).Concat(duplicated.Scene.Groups.Select(g=>g.Id)).ToArray();
        Check(allIds.Distinct().Count()==allIds.Length && allIds.All(id=>id!=Guid.Empty),"Copied object/group IDs collide.");
        for(var i=0;i<3;i++)
        {
            var original=beforeCopy.Scene.Objects[i]; var copy=copies[i];
            Check(ReferenceEquals(original.Mesh,copy.Mesh),"Copy duplicated a source mesh instead of sharing it.");
            Vec(copy.Bounds.Min,original.Bounds.Min+new Vec3(90,0,0)); Vec(copy.Bounds.Max,original.Bounds.Max+new Vec3(90,0,0));
            Check(copy.Name==original.Name+" copy","Ordinary copy name is unexpected.");
        }
        var copiedGroup=duplicated.Scene.Groups[1];
        Check(copiedGroup.Id!=group.Id && copiedGroup.Name==renamedName+" copy" && copiedGroup.ObjectIds.SequenceEqual(copies.Take(2).Select(o=>o.Id)),"Copied group membership/name/identity is wrong.");
        Selection(duplicated,copies.Select(o=>o.Id).ToImmutableArray(),copies[1].Id);
        var duplicatedPath=Path.Combine(output,"duplicated.3mf"); Ok(await editor.SaveAsync(duplicatedPath));
        Ok(editor.Undo()); State(editor,beforeCopy.Scene,beforeCopy.SelectedObjectIds,beforeCopy.SelectedObjectId); Check(editor.Snapshot.IsDirty,"Undo across duplicate Save should be dirty.");
        Ok(editor.Redo()); State(editor,duplicated.Scene,duplicated.SelectedObjectIds,duplicated.SelectedObjectId); Check(!editor.Snapshot.IsDirty,"Redo to saved duplicate should be clean.");
        Ok(await reopened.OpenAsync(duplicatedPath));
        Check(reopened.Snapshot.Scene.Objects.Length==6 && reopened.Snapshot.Scene.Groups.Length==2,"Copied group persistence failed.");
        Near(reopened.Snapshot.Scene.Objects.Sum(Volume),56000); Vec(reopened.Snapshot.Scene.Bounds.Min,new(0,-30,0)); Vec(reopened.Snapshot.Scene.Bounds.Max,new(175,20,60));
        measurements.Add(new {file="duplicated.3mf",leafObjects=6,groups=2,copyOffsetMillimeters=new Vec3(90,0,0),volumeMm3=56000});
        Pass("Whole-group plus independent-leaf duplicate canonicalization, aggregate placement, new object/group IDs, shared meshes, mapped primary, save boundary and group persistence");

        var beforeDelete=editor.Snapshot; var copiedIds=copies.Select(o=>o.Id).ToImmutableArray();
        Ok(await editor.DeleteSelectionAsync([copiedIds[1],copiedIds[2]],expectedRevision:beforeDelete.Revision));
        var deleted=editor.Snapshot;
        Check(deleted.Scene.Objects.SequenceEqual(beforeCopy.Scene.Objects) && deleted.Scene.Groups.SequenceEqual(beforeCopy.Scene.Groups),"Deleting copied selection removed originals or left copied membership.");
        Check(deleted.SelectedObjectIds.IsEmpty && deleted.SelectedObjectId==null,"Delete selected a fallback object despite no selected survivors.");
        Check(deleted.Revision==beforeDelete.Revision+1 && deleted.FilePath==beforeDelete.FilePath,"Delete was not one edit or changed the native path.");
        var deletedPath=Path.Combine(output,"deleted.3mf"); Ok(await editor.SaveAsync(deletedPath));
        Ok(editor.Undo()); State(editor,beforeDelete.Scene,beforeDelete.SelectedObjectIds,beforeDelete.SelectedObjectId); Check(editor.Snapshot.IsDirty,"Undo across deletion Save should be dirty.");
        Ok(editor.Redo()); State(editor,deleted.Scene,[],null); Check(!editor.Snapshot.IsDirty,"Redo to saved deletion should be clean.");
        var partial=await Setup(); var partialBefore=partial.Snapshot; var partialIds=partialBefore.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(partial.SelectMany(partialIds,partialIds[2])); partialBefore=partial.Snapshot;
        Ok(await partial.DeleteSelectionAsync([partialIds[0]]));
        Check(partial.Snapshot.Scene.Groups.IsEmpty && partial.Snapshot.Scene.Objects.Single().Id==partialIds[2],"Member-target delete did not remove the entire group.");
        Selection(partial.Snapshot,[partialIds[2]],partialIds[2]); Ok(partial.Undo()); State(partial,partialBefore.Scene,partialBefore.SelectedObjectIds,partialBefore.SelectedObjectId);
        Ok(await partial.DeleteSelectionAsync([partialIds[2]]));
        Check(partial.Snapshot.Scene.Groups.SequenceEqual(partialBefore.Scene.Groups),"Independent deletion dropped unrelated group.");
        Selection(partial.Snapshot,[partialIds[0],partialIds[1]],partialIds[0]); Ok(partial.Undo()); State(partial,partialBefore.Scene,partialBefore.SelectedObjectIds,partialBefore.SelectedObjectId);
        Ok(await partial.DeleteSelectionAsync(partialIds)); var empty=partial.Snapshot;
        Check(empty.Scene.Objects.IsEmpty && empty.Scene.Groups.IsEmpty && empty.SelectedObjectIds.IsEmpty && empty.SelectedObjectId==null,"Delete all left scene/group/selection state.");
        Ok(partial.Undo()); State(partial,partialBefore.Scene,partialBefore.SelectedObjectIds,partialBefore.SelectedObjectId); Ok(partial.Redo()); State(partial,empty.Scene,[],null);
        Ok(partial.Undo()); Ok(partial.Execute(new RenameGroup(partialBefore.Scene.Groups[0].Id,"Branch"))); Check(!partial.Snapshot.CanRedo,"Group rename after undo retained redo.");
        Pass("Whole-group deletion retains surviving selection/groups, explicit empty selection, delete-all Undo/Redo and saved-state/branch behavior");

        var named=await Setup(); var namedIds=named.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray(); var namedGroup=named.Snapshot.Scene.Groups.Single();
        var longName=new string('x',94)+"😀yyyy"; Check(longName.Length==100,"Unicode test name is not the100-character boundary.");
        Ok(named.Execute(new RenameGroup(namedGroup.Id,longName))); Ok(named.Execute(new RenameObject(namedIds[2],longName)));
        Ok(named.SelectMany(namedIds,namedIds[0]));
        // Existing primary is outside this explicit singleton target, so the first new copy is primary.
        Ok(await named.DuplicateSelectionAsync([namedIds[2]])); var singleCopy=named.Snapshot.Scene.Objects[^1];
        Selection(named.Snapshot,[singleCopy.Id],singleCopy.Id); Ok(named.Undo());
        Ok(await named.DuplicateSelectionAsync(namedIds));
        var copyNames=named.Snapshot.Scene.Objects.Skip(3).Select(o=>o.Name).Append(named.Snapshot.Scene.Groups[1].Name).ToArray();
        foreach(var name in copyNames)
            Check(name.Length is >=1 and <=100 && !name.Any(char.IsControl) && name.EndsWith(" copy",StringComparison.Ordinal),"Copy name is unsafe or exceeds bound.");
        Check(named.Snapshot.Scene.Objects[^1].Name==new string('x',94)+" copy" && named.Snapshot.Scene.Groups[1].Name==new string('x',94)+" copy","Copy name split a surrogate pair at truncation boundary.");
        var namesPath=Path.Combine(output,"bounded-copy-names.3mf"); Ok(await named.SaveAsync(namesPath)); Ok(await reopened.OpenAsync(namesPath));
        Check(reopened.Snapshot.Scene.Groups.Any(g=>g.Name==new string('x',94)+" copy"),"Unicode-safe copied group name did not persist.");
        Pass("Bounded Unicode-safe copy names and singleton target primary fallback; copied name persistence at100-character input boundary");

        var guarded=await Setup(); var guardBefore=guarded.Snapshot; var guardIds=guardBefore.Scene.Objects.Select(o=>o.Id).ToImmutableArray(); var guardGroup=guardBefore.Scene.Groups[0];
        foreach(var request in new[] {default(ImmutableArray<Guid>),ImmutableArray<Guid>.Empty,ImmutableArray.Create(Guid.NewGuid()),ImmutableArray.Create(guardGroup.Id),Enumerable.Repeat(guardIds[0],1001).ToImmutableArray()})
        {
            Check((await guarded.DuplicateSelectionAsync(request)).Outcome==Outcome.Invalid,"Bad duplicate input accepted."); Same(guarded,guardBefore);
            Check((await guarded.DeleteSelectionAsync(request)).Outcome==Outcome.Invalid,"Bad delete input accepted."); Same(guarded,guardBefore);
        }
        Check((await guarded.DuplicateSelectionAsync(guardIds,expectedRevision:guardBefore.Revision-1)).Outcome==Outcome.Conflict,"Stale duplicate accepted.");
        Check((await guarded.DeleteSelectionAsync(guardIds,expectedRevision:guardBefore.Revision-1)).Outcome==Outcome.Conflict,"Stale delete accepted."); Same(guarded,guardBefore);
        Ok(await guarded.PreviewArrangementAsync(new GroupRequest([guardIds[0]],guardGroup.Name))); var owned=guarded.Snapshot;
        Check((await guarded.DuplicateSelectionAsync(guardIds)).Outcome==Outcome.Conflict && (await guarded.DeleteSelectionAsync(guardIds)).Outcome==Outcome.Conflict,"Selection edit escaped preview ownership.");
        Check(guarded.Execute(new RenameGroup(guardGroup.Id,"Blocked")).Outcome==Outcome.Conflict,"Rename escaped preview ownership."); Same(guarded,owned);
        Check(guarded.DiscardPreview(owned.Preview!.Id).Outcome==Outcome.Cancelled,"Could not discard guard preview."); Same(guarded,guardBefore);
        Pass("Default/empty/unknown/group-ID/oversized targets and stale revisions reject without mutation; preview guards cover rename/duplicate/delete");

        var cancel=new EditorSession(); Ok(await cancel.OpenAsync(renamedPath));
        Ok(cancel.Execute(new AddPrimitive(PrimitiveKind.Sphere,new(18,26,34)))); var sphere=cancel.Snapshot.SelectedObjectId!.Value;
        var cancelGroup=cancel.Snapshot.Scene.Groups.Single(); var cancelTargets=ImmutableArray.Create(cancelGroup.ObjectIds[1],sphere);
        Ok(cancel.SelectMany(cancelTargets,cancelGroup.ObjectIds[1]));
        Ok(cancel.Execute(new RenameObject(sphere,"redo marker"))); Ok(cancel.Undo()); var cancelBefore=cancel.Snapshot;
        Check(cancelBefore.CanRedo && cancelBefore.IsDirty && cancelBefore.FilePath!=null,"Cancellation baseline lacks dirty scene/path/redo.");
        foreach(var stage in new[] {"before","Preparing duplicate","Measuring duplicate bounds","Duplicating objects","Ready to duplicate"})
        {
            using var cts=new CancellationTokenSource(); if(stage=="before") cts.Cancel();
            var result=await cancel.DuplicateSelectionAsync(cancelTargets,cts.Token,new GroupProgress(p=> {
                if(p.Stage==stage && (stage is not ("Measuring duplicate bounds" or "Duplicating objects") || p.Completed>=1024)) cts.Cancel();
            }));
            Check(result.Outcome==Outcome.Cancelled,"Duplicate cancellation failed at "+stage); Same(cancel,cancelBefore);
        }
        foreach(var stage in new[] {"before","Preparing deletion","Deleting objects","Ready to delete"})
        {
            using var cts=new CancellationTokenSource(); if(stage=="before") cts.Cancel();
            var result=await cancel.DeleteSelectionAsync(cancelTargets,cts.Token,new GroupProgress(p=> {if(p.Stage==stage) cts.Cancel();}));
            Check(result.Outcome==Outcome.Cancelled,"Delete cancellation failed at "+stage); Same(cancel,cancelBefore);
        }
        foreach(var deleting in new[] {false,true})
        using(var entered=new ManualResetEventSlim())
        using(var release=new ManualResetEventSlim())
        using(var cts=new CancellationTokenSource())
        {
            var stage=deleting ? "Ready to delete" : "Ready to duplicate";
            var progress=new GroupProgress(p=> {if(p.Stage==stage) {entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(15)),"Selection edit barrier timed out.");}});
            var job=deleting ? cancel.DeleteSelectionAsync(cancelTargets,cts.Token,progress) : cancel.DuplicateSelectionAsync(cancelTargets,cts.Token,progress);
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(15)),"Selection edit did not enter busy barrier."); var busy=cancel.Snapshot;
                Check(busy.IsBusy,"Selection edit lacks busy lease."); Check(busy.Scene.GetObjectTree().Length==3,"Read-only tree failed while busy.");
                Check((await cancel.DuplicateSelectionAsync(cancelTargets)).Outcome==Outcome.Busy && (await cancel.DeleteSelectionAsync(cancelTargets)).Outcome==Outcome.Busy,"Concurrent selection edit admitted.");
                Check(cancel.Execute(new RenameGroup(cancelGroup.Id,"Busy")).Outcome==Outcome.Busy,"Rename raced selection edit.");
                Check(cancel.SelectMany([]).Outcome==Outcome.Busy && cancel.Undo().Outcome==Outcome.Busy,"Selection/history raced selection edit.");
                Check((await cancel.SaveAsync(Path.Combine(output,"must-not-exist.3mf"))).Outcome==Outcome.Busy,"Save raced selection edit."); Same(cancel,busy);
            }
            finally {cts.Cancel(); release.Set();}
            Check((await job).Outcome==Outcome.Cancelled,"Busy selection edit did not cancel."); Same(cancel,cancelBefore);
        }
        Pass("Duplicate vertex-walk/delete/precommit cancellation and busy leases preserve accepted scene, full selection, path, dirty state and redo");

        var crowded=new EditorSession(); for(var i=0;i<ModelLimits.MaxObjects;i++) Ok(crowded.Execute(new AddCube()));
        var crowdedBefore=crowded.Snapshot;
        Check((await crowded.DuplicateSelectionAsync([crowdedBefore.Scene.Objects[0].Id])).Outcome==Outcome.Invalid,"Duplicate exceeded object limit."); Same(crowded,crowdedBefore);
        var faces=new EditorSession(); for(var i=0;i<45;i++) Ok(faces.Execute(new AddPrimitive(PrimitiveKind.Sphere,new(10,10,10))));
        var facesBefore=faces.Snapshot;
        Check((await faces.DuplicateSelectionAsync([facesBefore.Scene.Objects[0].Id])).Outcome==Outcome.Invalid,"Duplicate exceeded face limit."); Same(faces,facesBefore);
        var bounds=new EditorSession(); Ok(bounds.Execute(new AddCube())); var boundsId=bounds.Snapshot.SelectedObjectId!.Value;
        Ok(bounds.Execute(new MoveObject(boundsId,new(9_999_990,10,10)))); var boundsBefore=bounds.Snapshot;
        Check((await bounds.DuplicateSelectionAsync([boundsId])).Outcome==Outcome.Invalid,"Duplicate crossed physical coordinate limit."); Same(bounds,boundsBefore);
        Pass("Duplicate scene object/face and physical-coordinate limit failures preserve accepted state");

        Check(Hash(fixture)==fixtureHash,"The original assembly fixture changed.");
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {
            timestamp=DateTimeOffset.UtcNow,observation="Replacement Core runtime through public EditorSession; no desktop/original-app input",
            target=new {name="Builder.Core",version=typeof(EditorSession).Assembly.GetName().Version?.ToString(),sha256=Hash(typeof(EditorSession).Assembly.Location)},
            tools=new {runtime=Environment.Version.ToString()},inputs=new[] {new {file=Path.GetRelativePath(root,fixture),sha256=fixtureHash}},
            cases,measurements,limitations=new[] {"Flat groups only; tree leaves reveal membership but selection still expands the whole group.",
                "Synthetic boxes/sphere; no large-model performance, renderer, accessibility or original-app parity claim.",
                "Scene geometry limits are enforced; separate 3MF writer resource limits can still reject a later Save safely."},
            outputs=Directory.EnumerateFiles(output,"*.3mf").Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)})
        },new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"{cases.Count} group editing scenario groups passed. Evidence: {output}");
    }
    private sealed class GroupProgress(Action<OperationProgress> action):IProgress<OperationProgress>
    {public void Report(OperationProgress value)=>action(value);}
    private static void Check(bool value,string message) {if(!value) throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Operation changed accepted state/selection/history/path.");
    private static void Selection(EditorSnapshot snapshot,ImmutableArray<Guid> ids,Guid? primary)=>Check(snapshot.SelectedObjectIds.SequenceEqual(ids) && snapshot.SelectedObjectId==primary,"Full selection or primary is wrong.");
    private static void State(EditorSession s,Scene scene,ImmutableArray<Guid> ids,Guid? primary) {Check(s.Snapshot.Scene==scene,"Scene identity changed."); Selection(s.Snapshot,ids,primary);}
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-7,$"Expected {b:R}, got {a:R}.");
    private static void Vec(Vec3 a,Vec3 b) {Near(a.X,b.X); Near(a.Y,b.Y); Near(a.Z,b.Z);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static double Volume(SceneObject obj)
    {
        var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);
        return obj.Mesh.Triangles.Sum(t=> {var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin; var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin; var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin; var n=Vec3.Cross(b,c); return (a.X*n.X+a.Y*n.Y+a.Z*n.Z)/6;});
    }
}
