using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class MultiObjectChecks
{
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/multiobject-20261019/core"); Directory.CreateDirectory(output);
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var cases=new List<string>();
        void Pass(string name) { cases.Add(name); Console.WriteLine("PASS "+name); }
        async Task<EditorSession> Setup()
        {
            var s=new EditorSession(worker);
            Ok(await s.OpenAsync(Path.Combine(root,"fixtures/generated/assembly-mm.3mf")));
            Ok(s.Execute(new AddPrimitive(PrimitiveKind.Cube,new(10,30,40))));
            Ok(s.Execute(new MoveObject(s.Snapshot.SelectedObjectId!.Value,new(80,-15,40))));
            Ok(await s.SaveAsync(Path.Combine(output,"source.3mf")));
            return s;
        }
        static ImmutableArray<Guid> Ids(EditorSession s)=>s.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        static void Apply(EditorSession s)=>Ok(s.ApplyPreview(s.Snapshot.Preview!.Id,s.Snapshot.Revision));
        var session=await Setup(); var ids=Ids(session); var clean=session.Snapshot;
        Ok(session.SelectMany([ids[2],ids[0],ids[2]],ids[2],clean.Revision));
        Check(session.Snapshot.SelectedObjectIds.SequenceEqual(new[]{ids[0],ids[2]}),"Selection was not deduplicated/document ordered.");
        Check(session.Snapshot.SelectedObjectId==ids[2] && !session.Snapshot.IsDirty && session.Snapshot.Scene==clean.Scene,"Selection changed content or primary.");
        Check(session.Snapshot.CanUndo==clean.CanUndo && session.Snapshot.CanRedo==clean.CanRedo,"Selection changed history.");
        var selected=session.Snapshot;
        Ok(session.SelectMany([ids[0],ids[2]],ids[2])); Same(session,selected);
        Check(session.SelectMany([ids[0]],ids[2]).Outcome==Outcome.Invalid,"Unselected primary accepted."); Same(session,selected);
        Check(session.SelectMany([Guid.NewGuid()]).Outcome==Outcome.Invalid,"Unknown leaf selected."); Same(session,selected);
        Check(session.SelectMany(Enumerable.Repeat(ids[0],1001)).Outcome==Outcome.Invalid,"Unbounded input selected."); Same(session,selected);
        Check(session.SelectMany([],expectedRevision:clean.Revision).Outcome==Outcome.Conflict,"Stale selection accepted."); Same(session,selected);
        Ok(session.Select(null)); Check(session.Snapshot.SelectedObjectIds.IsEmpty && session.Snapshot.SelectedObjectId==null,"Clear failed.");
        Pass("Multi-selection canonical ordering, primary, bounded input, stale revision, clear and non-dirty/non-history behavior");

        Ok(session.SelectMany([ids[0],ids[1]],ids[1])); var beforeGroup=session.Snapshot;
        Ok(await session.PreviewAsync(new GroupRequest([ids[1],ids[0]],"  Pair & brackets  ")));
        Accepted(session,beforeGroup); var groupPreview=session.Snapshot.Preview!;
        Check(groupPreview.Scene.Groups.Single().Name=="Pair & brackets","Group name normalization failed.");
        Check(groupPreview.Scene.Objects.SequenceEqual(beforeGroup.Scene.Objects),"Grouping changed geometry, leaf identity or transform.");
        Check(session.Select(ids[2]).Outcome==Outcome.Conflict,"Selection changed during preview.");
        Check(session.Execute(new AddCube()).Outcome==Outcome.Conflict,"Edit escaped preview guard.");
        Check(session.Undo().Outcome==Outcome.Conflict,"Undo escaped preview guard.");
        Check((await session.SaveAsync(Path.Combine(output,"must-not-exist.3mf"))).Outcome==Outcome.Conflict,"Save escaped preview guard.");
        Check(session.DiscardPreview(groupPreview.Id).Outcome==Outcome.Cancelled,"Group cancellation failed."); Same(session,beforeGroup);
        Ok(await session.PreviewArrangementAsync(new GroupRequest([ids[0],ids[1]],"Pair & brackets"))); Apply(session);
        var grouped=session.Snapshot; var group=grouped.Scene.Groups.Single();
        Check(grouped.Revision==beforeGroup.Revision+1 && grouped.SelectedObjectIds.SequenceEqual(beforeGroup.SelectedObjectIds),"Group Apply failed one-step/full-selection contract.");
        Ok(await session.SaveAsync(Path.Combine(output,"grouped.3mf")));
        Ok(session.Undo()); Check(session.Snapshot.Scene==beforeGroup.Scene && session.Snapshot.SelectedObjectIds.SequenceEqual(beforeGroup.SelectedObjectIds) && session.Snapshot.SelectedObjectId==ids[1],"Group Undo did not restore full selection.");
        Check(session.Snapshot.IsDirty,"Undo across saved group should be dirty.");
        Ok(session.Redo()); Check(session.Snapshot.Scene==grouped.Scene && !session.Snapshot.IsDirty,"Group Redo lost saved content.");
        Ok(session.Select(ids[1])); Check(session.Snapshot.SelectedObjectIds.SequenceEqual(new[]{ids[0],ids[1]}) && session.Snapshot.SelectedObjectId==ids[1],"Member pick did not expand group.");
        Check(session.Snapshot.Scene.GetSelectionUnits([ids[1],ids[0]]).Single().Id==group.Id,"Group selection unit unstable.");
        Pass("Group preview/cancel, identity/mesh preservation, transaction guards, one-step history, saved boundary and member selection expansion");

        var safe=session.Snapshot;
        foreach(var edit in new Edit[]{new MoveObject(ids[0],new()),new RotateObject(ids[0],new(0,0,90)),new ResizeObject(ids[0],new(10,10,10)),new PlaceOnBed(ids[0]),new DuplicateObject(ids[0]),new DeleteObject(ids[0])})
        { Check(session.Execute(edit).Outcome==Outcome.Invalid,"Grouped member edited by singleton API: "+edit); Same(session,safe); }
        foreach(var request in new GeometryRequest[]{new SplitRequest(ids[0],new(0,0,1),10,SplitKeep.Both),new BooleanRequest(ids[0],ids[2],BooleanKind.Union),new RepairRequest(ids[0],true)})
        { Check((await session.PreviewAsync(request)).Outcome==Outcome.Invalid,"Grouped member construction accepted."); Same(session,safe); }
        Ok(session.Execute(new RenameObject(ids[0],"Left")));
        Check(session.Snapshot.Scene.Groups.SequenceEqual(safe.Scene.Groups),"Leaf rename dropped group."); Ok(session.Undo());
        Ok(session.Execute(new MoveObject(ids[2],new(90,0,40))));
        Check(session.Snapshot.Scene.Groups.SequenceEqual(safe.Scene.Groups),"Unrelated edit dropped group."); Ok(session.Undo());
        Ok(await session.PreviewAsync(new RepairRequest(ids[2],true)));
        Check((session.Snapshot.Preview?.Scene ?? session.Snapshot.Scene).Groups.SequenceEqual(safe.Scene.Groups),"Unrelated repair dropped group.");
        if(session.Snapshot.Preview is {} repaired) session.DiscardPreview(repaired.Id);
        Ok(await session.PreviewAsync(new SplitRequest(ids[2],new(0,0,1),40,SplitKeep.Both)));
        Check(session.Snapshot.Preview!.Scene.Groups.SequenceEqual(safe.Scene.Groups),"Unrelated split dropped group."); session.DiscardPreview(session.Snapshot.Preview.Id);
        Pass("Grouped singleton edits/construction reject safely; rename, unrelated edits, repair and split retain group metadata");

        var beforeUngroup=session.Snapshot;
        Ok(await session.PreviewArrangementAsync(new UngroupRequest([ids[1]])));
        Check(session.Snapshot.Preview!.Scene.Groups.IsEmpty && session.Snapshot.Preview.Scene.Objects.SequenceEqual(beforeUngroup.Scene.Objects),"Ungroup modified geometry.");
        Apply(session); Check(session.Snapshot.SelectedObjectIds.SequenceEqual(new[]{ids[0],ids[1]}),"Ungroup lost selected members.");
        Ok(session.Undo()); Check(session.Snapshot.Scene==beforeUngroup.Scene,"Ungroup Undo failed.");
        Ok(await session.PreviewArrangementAsync(new GroupRequest([ids[0],ids[2]],"Assembly"))); Apply(session);
        Check(session.Snapshot.Scene.Groups.Single().ObjectIds.SequenceEqual(ids),"Regroup did not flatten all selected group members.");
        Ok(session.Undo()); Check(session.Snapshot.Scene==beforeUngroup.Scene,"Regroup Undo failed.");
        var noOp=session.Snapshot;
        Ok(await session.PreviewArrangementAsync(new GroupRequest([ids[0]],group.Name))); Apply(session); Same(session,noOp);
        Ok(await session.PreviewArrangementAsync(new UngroupRequest([ids[2]]))); Apply(session); Same(session,noOp);
        Pass("Ungroup and regroup keep world geometry, flatten groups, preserve selected members and support true no-op history");

        // All axis/anchor combinations use unequal bounds, including a pre-rotated object.
        var align=await Setup(); var aid=Ids(align);
        Ok(align.Execute(new RotateObject(aid[2],new(0,0,90)))); Ok(align.SelectMany(aid,aid[2]));
        var original=align.Snapshot; var combined=original.Scene.Bounds;
        foreach(var axis in Enumerable.Range(0,3)) foreach(var anchor in new[]{AxisAlignment.Minimum,AxisAlignment.Center,AxisAlignment.Maximum})
        {
            var modes=new[]{AxisAlignment.Keep,AxisAlignment.Keep,AxisAlignment.Keep}; modes[axis]=anchor;
            Ok(await align.PreviewArrangementAsync(new AlignmentRequest(aid,modes[0],modes[1],modes[2])));
            foreach(var obj in align.Snapshot.Preview!.Scene.Objects)
            {
                var old=original.Scene.Objects.Single(o=>o.Id==obj.Id);
                Near(Anchor(obj.Bounds,anchor,axis),Anchor(combined,anchor,axis)); Vec(obj.Bounds.Size,old.Bounds.Size);
                for(var other=0;other<3;other++) if(other!=axis) Near(Component(obj.Bounds.Center,other),Component(old.Bounds.Center,other));
                Check(ReferenceEquals(obj.Mesh,old.Mesh),"Alignment remeshed.");
            }
            Accepted(align,original); align.DiscardPreview(align.Snapshot.Preview.Id); Same(align,original);
        }
        Ok(await align.PreviewArrangementAsync(new AlignmentRequest(aid,AxisAlignment.Minimum,AxisAlignment.Center,AxisAlignment.Maximum,aid[2])));
        var aligned=align.Snapshot.Preview!;
        Check(aligned.Scene.Objects[2]==original.Scene.Objects[2],"Reference object moved.");
        foreach(var obj in aligned.Scene.Objects)
        { Near(obj.Bounds.Min.X,original.Scene.Objects[2].Bounds.Min.X); Near(obj.Bounds.Center.Y,original.Scene.Objects[2].Bounds.Center.Y); Near(obj.Bounds.Max.Z,original.Scene.Objects[2].Bounds.Max.Z); }
        Apply(align); Ok(await align.SaveAsync(Path.Combine(output,"aligned-reference.3mf"))); Ok(align.Undo());
        Check(align.Snapshot.Scene==original.Scene && align.Snapshot.SelectedObjectId==aid[2] && align.Snapshot.SelectedObjectIds.SequenceEqual(aid),"Alignment Undo lost source/selection.");
        Ok(align.Redo()); Check(!align.Snapshot.IsDirty,"Alignment saved boundary lost.");
        Pass("Nine axis/anchor combinations and simultaneous XYZ primary-reference alignment match analytic unequal/rotated bounds; one-step Undo/Redo");

        Ok(session.SelectMany(ids,ids[2])); var beforeAlign=session.Snapshot;
        Ok(await session.PreviewArrangementAsync(new AlignmentRequest([ids[1],ids[2]],AxisAlignment.Center,AxisAlignment.Minimum,AxisAlignment.Maximum,ids[2])));
        var alignedGroup=session.Snapshot.Preview!;
        Vec(alignedGroup.Scene.Objects[1].Bounds.Center-alignedGroup.Scene.Objects[0].Bounds.Center,
            beforeAlign.Scene.Objects[1].Bounds.Center-beforeAlign.Scene.Objects[0].Bounds.Center);
        var unit=alignedGroup.Scene.GetSelectionUnits([ids[0]]).Single(); var reference=beforeAlign.Scene.Objects[2].Bounds;
        Near(unit.Bounds.Center.X,reference.Center.X); Near(unit.Bounds.Min.Y,reference.Min.Y); Near(unit.Bounds.Max.Z,reference.Max.Z);
        Check(alignedGroup.Scene.Objects[2]==beforeAlign.Scene.Objects[2] && alignedGroup.Scene.Groups.SequenceEqual(beforeAlign.Scene.Groups),"Grouped align changed reference/group metadata.");
        Apply(session); Ok(await session.SaveAsync(Path.Combine(output,"aligned-group.3mf"))); Ok(session.Undo());
        Check(session.Snapshot.Scene==beforeAlign.Scene,"Grouped alignment Undo failed.");
        Ok(await session.PreviewArrangementAsync(new AlignmentRequest(ids,AxisAlignment.Minimum,AxisAlignment.Center,AxisAlignment.Maximum,ids[0])));
        Check(session.Snapshot.Preview!.Scene.Objects.Take(2).SequenceEqual(beforeAlign.Scene.Objects.Take(2)),"Reference group moved."); session.DiscardPreview(session.Snapshot.Preview.Id);
        Pass("Groups align as rigid units with unchanged member spacing; either a leaf or group can be the fixed reference");

        var failures=session.Snapshot;
        Ok(await session.PreviewArrangementAsync(new AlignmentRequest(ids,AxisAlignment.Center,AxisAlignment.Keep,AxisAlignment.Keep)));
        var held=session.Snapshot;
        foreach(var request in new ArrangementRequest[]{new GroupRequest(ids,"\n"),new GroupRequest(ids,new string('x',101)),new AlignmentRequest(ids,(AxisAlignment)99,AxisAlignment.Keep,AxisAlignment.Keep),new AlignmentRequest(ids,AxisAlignment.Center,AxisAlignment.Keep,AxisAlignment.Keep,Guid.NewGuid())})
        { Check((await session.PreviewArrangementAsync(request,held.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid replacement accepted."); Same(session,held); }
        Check((await session.PreviewArrangementAsync(new UngroupRequest(ids),Guid.NewGuid())).Outcome==Outcome.Conflict,"Foreign token accepted."); Same(session,held);
        Check((await session.PreviewArrangementAsync(new UngroupRequest([ids[0]]),held.Preview!.Id)).Outcome==Outcome.Conflict,"Changed target set accepted."); Same(session,held);
        using(var ct=new CancellationTokenSource())
        {
            ct.Cancel(); Check((await session.PreviewArrangementAsync(new UngroupRequest(ids),held.Preview!.Id,ct.Token)).Outcome==Outcome.Cancelled,"Early cancellation failed."); Same(session,held);
        }
        foreach(var stage in new[]{"Measuring selection","Aligning objects","Ready to preview"})
        {
            using var ct=new CancellationTokenSource(); var saw=false;
            var result=await session.PreviewArrangementAsync(new AlignmentRequest(ids,AxisAlignment.Maximum,AxisAlignment.Center,AxisAlignment.Minimum),held.Preview!.Id,ct.Token,
                new InlineProgress(p=>{ if(p.Stage==stage) { saw=true; ct.Cancel(); } }));
            Check(saw && result.Outcome==Outcome.Cancelled,"Stage cancellation not exercised: "+stage); Same(session,held);
        }
        Ok(await session.PreviewArrangementAsync(new UngroupRequest(ids),held.Preview!.Id));
        Check(session.Snapshot.Preview!.Id!=held.Preview.Id,"Replacement reused ownership token.");
        Check(session.ApplyPreview(held.Preview.Id).Outcome==Outcome.Conflict,"Old token committed replacement.");
        session.DiscardPreview(session.Snapshot.Preview.Id); Same(session,failures);
        Check((await session.PreviewArrangementAsync(new AlignmentRequest([ids[0]],AxisAlignment.Center,AxisAlignment.Keep,AxisAlignment.Keep))).Outcome==Outcome.Invalid,"Single group aligned to itself."); Same(session,failures);
        Pass("Invalid/early/measurement/geometry/late cancellation preserves accepted state and prior preview; stale tokens and target changes reject");

        using(var entered=new ManualResetEventSlim()) using(var release=new ManualResetEventSlim()) using(var ct=new CancellationTokenSource())
        {
            var before=session.Snapshot;
            var job=session.PreviewArrangementAsync(new AlignmentRequest(ids,AxisAlignment.Center,AxisAlignment.Keep,AxisAlignment.Keep),cancellation:ct.Token,
                progress:new InlineProgress(p=>{ if(p.Stage=="Preparing arrangement") { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } }));
            Check(entered.Wait(TimeSpan.FromSeconds(5)),"Busy lease stage not reached.");
            try
            {
                Check(session.Snapshot.IsBusy,"No busy lease.");
                Check(session.SelectMany([]).Outcome==Outcome.Busy && session.Undo().Outcome==Outcome.Busy && session.Execute(new AddCube()).Outcome==Outcome.Busy,"Mutation entered a running arrangement.");
                Check((await session.PreviewArrangementAsync(new GroupRequest(ids))).Outcome==Outcome.Busy,"Concurrent preview entered.");
                ct.Cancel();
            }
            finally { release.Set(); }
            Check((await job).Outcome==Outcome.Cancelled,"Busy cancellation failed."); Same(session,before);
        }
        Pass("A running arrangement serializes selection, history, edits and other preview jobs; cancellation releases its lease intact");

        Ok(session.Select(ids[1])); var beforeTransform=session.Snapshot;
        Ok(await session.PreviewTransformAsync(new MoveTransform(ids[1],new(2.5,-2.5,7.5),5)));
        var moved=session.Snapshot.Preview!;
        Check(((TransformRequest)moved.Request).ObjectIds.SequenceEqual(new[]{ids[0],ids[1]}),"Implicit member transform failed group expansion.");
        foreach(var i in new[]{0,1}) Vec(moved.Scene.Objects[i].Bounds.Center,beforeTransform.Scene.Objects[i].Bounds.Center+new Vec3(5,-5,10));
        Check(moved.Scene.Objects[2]==beforeTransform.Scene.Objects[2],"Group transform moved unselected object.");
        var pivot=beforeTransform.Scene.GetSelectionUnits([ids[0]]).Single().Bounds.Center;
        Ok(await session.PreviewTransformAsync(new RotateTransform(ids[0],new(0,0,90)),moved.Id));
        foreach(var i in new[]{0,1}) foreach(var v in beforeTransform.Scene.Objects[i].Mesh.Vertices)
        {
            var point=beforeTransform.Scene.Objects[i].Transform.Apply(v)-pivot;
            Vec(session.Snapshot.Preview!.Scene.Objects[i].Transform.Apply(v),pivot+new Vec3(-point.Y,point.X,point.Z));
        }
        Ok(await session.PreviewTransformAsync(new ScaleTransform(ids[1],new(2,0.5,1)),session.Snapshot.Preview!.Id));
        foreach(var i in new[]{0,1}) foreach(var v in beforeTransform.Scene.Objects[i].Mesh.Vertices)
        {
            var point=beforeTransform.Scene.Objects[i].Transform.Apply(v)-pivot;
            Vec(session.Snapshot.Preview!.Scene.Objects[i].Transform.Apply(v),pivot+new Vec3(point.X*2,point.Y*0.5,point.Z));
        }
        Apply(session); Check(session.Snapshot.SelectedObjectIds.SequenceEqual(beforeTransform.SelectedObjectIds),"Group transform lost selection.");
        Ok(await session.SaveAsync(Path.Combine(output,"scaled-group.3mf"))); Ok(await session.ExportStlAsync(Path.Combine(output,"scaled-group.stl"))); Ok(session.Undo());
        Check(session.Snapshot.Scene==beforeTransform.Scene,"Group transform Undo failed.");
        Ok(session.SelectMany(ids,ids[2])); var beforeAll=session.Snapshot;
        Ok(await session.PreviewTransformAsync(new MoveTransform(ids[2],new(3,4,5)) {ObjectIds=ids}));
        foreach(var obj in session.Snapshot.Preview!.Scene.Objects) Vec(obj.Bounds.Center,beforeAll.Scene.Objects.Single(o=>o.Id==obj.Id).Bounds.Center+new Vec3(3,4,5));
        session.DiscardPreview(session.Snapshot.Preview.Id); Same(session,beforeAll);
        Pass("Group-expanded and explicit multi-object transforms use one aggregate pivot, snap and accepted baseline; IDs/meshes/groups/selection persist through Apply/Undo/export");

        var reopen=new EditorSession(worker); Ok(await reopen.OpenAsync(Path.Combine(output,"grouped.3mf")));
        Check(reopen.Snapshot.Scene.Groups.Length==1 && reopen.Snapshot.SelectedObjectIds.Length==2,"Open did not restore group and whole first-unit selection.");
        var beforeInsert=reopen.Snapshot; Ok(await reopen.InsertAsync(Path.Combine(output,"aligned-group.3mf")));
        Check(reopen.Snapshot.Scene.Groups.Length==2 && reopen.Snapshot.Scene.Objects.Length==6 && reopen.Snapshot.SelectedObjectIds.Length==2,"Insert lost source/imported group or selection.");
        Check(reopen.Snapshot.Scene.Objects.Take(3).SequenceEqual(beforeInsert.Scene.Objects),"Insert changed original geometry.");
        Ok(reopen.Undo()); Check(reopen.Snapshot.Scene==beforeInsert.Scene && reopen.Snapshot.SelectedObjectIds.SequenceEqual(beforeInsert.SelectedObjectIds),"Insert Undo lost group selection.");
        Ok(reopen.Redo()); Ok(await reopen.SaveAsync(Path.Combine(output,"inserted-groups.3mf")));
        Pass("Open/Insert/save use standard group persistence, retain original IDs/transforms and undo complete imported groups with selection");

        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,runtime=Environment.Version.ToString(),cases,
            coreSha256=Hash(typeof(EditorSession).Assembly.Location),outputs=Directory.GetFiles(output).Where(p=>Path.GetExtension(p) is ".3mf" or ".stl").Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)})},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} multi-object scenario groups passed. Evidence: {output}");
    }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-7,$"Expected {b}, got {a}.");
    private static void Vec(Vec3 a,Vec3 b) { Near(a.X,b.X); Near(a.Y,b.Y); Near(a.Z,b.Z); }
    private static double Component(Vec3 v,int axis)=>axis==0?v.X:axis==1?v.Y:v.Z;
    private static double Anchor(Bounds3 b,AxisAlignment mode,int axis)=>Component(mode==AxisAlignment.Minimum?b.Min:mode==AxisAlignment.Maximum?b.Max:b.Center,axis);
    private static void Same(EditorSession s,EditorSnapshot prior)=>Check(s.Snapshot==prior,"Snapshot changed unexpectedly.");
    private static void Accepted(EditorSession s,EditorSnapshot prior)=>Check((s.Snapshot with {Preview=null})==prior,"Preview changed accepted state.");
}
