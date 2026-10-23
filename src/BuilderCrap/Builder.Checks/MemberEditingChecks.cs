using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class MemberEditingChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/member-editing-20261019/core"); Directory.CreateDirectory(output);
        var exports=evidenceRoot==null?Path.Combine(root,".local/evidence/member-editing-20261019/exports"):Path.Combine(output,"exports"); Directory.CreateDirectory(exports);
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var cases=new List<string>(); var measurements=new List<object>();
        void Pass(string name) {cases.Add(name); Console.WriteLine("PASS "+name);}
        async Task<(EditorSession Session,ImmutableArray<Guid> Ids,Guid Group)> Setup(bool damaged=false,bool pair=false)
        {
            var s=new EditorSession(worker);
            Ok(await s.OpenAsync(Path.Combine(root,"fixtures/generated",damaged ? "missing-top.stl" : "assembly-mm.3mf")));
            if(damaged) Ok(await s.InsertAsync(Path.Combine(root,"fixtures/generated/assembly-mm.3mf")));
            else if(!pair) Ok(s.Execute(new AddCube()));
            var members=s.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
            for(var i=1;i<members.Length;i++) Ok(s.Execute(new MoveObject(members[i],new(10+30*i,10,10))));
            Ok(s.Execute(new AddPrimitive(PrimitiveKind.Cube,new(10,30,40))));
            Ok(s.Execute(new MoveObject(s.Snapshot.SelectedObjectId!.Value,new(110,-15,40))));
            Ok(await s.PreviewArrangementAsync(new GroupRequest(members,"Member assembly"))); Apply(s);
            Ok(await s.SaveAsync(Path.Combine(output,damaged ? "source-damaged.3mf" : pair ? "source-pair.3mf" : "source.3mf")));
            return (s,s.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray(),s.Snapshot.Scene.Groups.Single().Id);
        }
        var fixture=await Setup(); var session=fixture.Session; var ids=fixture.Ids; var group=fixture.Group;
        var sceneMode=session.Snapshot;
        Check(session.EnterGroup(Guid.NewGuid()).Outcome==Outcome.Invalid,"Unknown group entered."); Same(session,sceneMode);
        Check(session.EnterGroup(group,ids[3]).Outcome==Outcome.Invalid,"Outside member entered."); Same(session,sceneMode);
        Check(session.EnterGroup(group,ids[0],sceneMode.Revision-1).Outcome==Outcome.Conflict,"Stale enter succeeded."); Same(session,sceneMode);
        var staleSceneMove=new MoveTransform(ids[0],new(1,0,0));
        Ok(session.EnterGroup(group,ids[0],sceneMode.Revision)); var entered=session.Snapshot;
        Check(entered.EditingGroupId==group && entered.SelectedObjectIds.SequenceEqual(new[]{ids[0]}) && entered.Revision==sceneMode.Revision+1,"Enter scope/selection/revision failed.");
        Check(entered.Scene==sceneMode.Scene && entered.IsDirty==sceneMode.IsDirty && entered.CanUndo==sceneMode.CanUndo && entered.CanRedo==sceneMode.CanRedo && entered.FilePath==sceneMode.FilePath,"Entering group changed document/history/path.");
        Ok(session.SelectMany([ids[2],ids[0],ids[2]],ids[2]));
        Check(session.Snapshot.SelectedObjectIds.SequenceEqual(new[]{ids[0],ids[2]}) && session.Snapshot.GetSelectionUnits().Length==2,"Member selection expanded whole group or lost independent units.");
        var selected=session.Snapshot;
        Ok(session.EnterGroup(group)); Same(session,selected);
        Check(session.EnterGroup(Guid.NewGuid()).Outcome==Outcome.Conflict,"Active scope switched directly to another group."); Same(session,selected);
        Check(session.EnterGroup(group,ids[3]).Outcome==Outcome.Invalid,"Active scope accepted an outside member."); Same(session,selected);
        Check(session.Select(ids[3]).Outcome==Outcome.Invalid,"Outside leaf selected in member mode."); Same(session,selected);
        Check(session.SelectMany([ids[0],ids[3]]).Outcome==Outcome.Invalid,"Mixed outside selection entered scope."); Same(session,selected);
        Check(session.ExitGroup(selected.Revision-1).Outcome==Outcome.Conflict,"Stale exit succeeded."); Same(session,selected);
        Check((await session.PreviewTransformAsync(staleSceneMove)).Outcome==Outcome.Conflict,"Scene request changed meaning after entering group."); Same(session,selected);
        Check(session.Execute(new MoveObject(ids[0],new(15,10,10))).Outcome==Outcome.Conflict,"Unscoped legacy edit changed meaning."); Same(session,selected);
        Check((await session.PreviewTransformAsync(new MoveTransform(ids[3],new(1,0,0)) {MemberGroupId=group})).Outcome==Outcome.Invalid,"Scoped transform reached outside leaf."); Same(session,selected);
        Check((await session.ExportSelectionAsync(Path.Combine(exports,"wrong-scope.3mf"),[ids[0]])).Outcome==Outcome.Conflict,"Unscoped export changed meaning."); Same(session,selected);
        var staleMemberMove=new MoveTransform(ids[0],new(1,0,0)) {MemberGroupId=group};
        Ok(session.ExitGroup()); var exited=session.Snapshot;
        Check(exited.EditingGroupId==null && exited.SelectedObjectIds.SequenceEqual(ids.Take(3)) && exited.GetSelectionUnits().Length==1,"Exit did not restore whole-group selection semantics.");
        Check(exited.Scene==sceneMode.Scene && exited.IsDirty==sceneMode.IsDirty && exited.CanUndo==sceneMode.CanUndo,"Exit changed accepted content/history.");
        Check((await session.PreviewTransformAsync(staleMemberMove)).Outcome==Outcome.Conflict,"Member request changed meaning after exit."); Same(session,exited);
        Pass("Enter/exit are revisioned non-dirty scope changes; independent member selection, invalid/stale/outside targets and exact scene/member request guards");

        Ok(session.EnterGroup(group,ids[0])); var legacyBefore=session.Snapshot;
        Ok(session.Execute(new MoveObject(ids[0],new(15,10,30)) {MemberGroupId=group}));
        Bounds(Object(session,ids[0]).Bounds,new(5,0,20),new(25,20,40)); Siblings(legacyBefore.Scene,session.Snapshot.Scene,[ids[0]]);
        Ok(session.Execute(new PlaceOnBed(ids[0]) {MemberGroupId=group})); Near(Object(session,ids[0]).Bounds.Min.Z,0);
        Ok(session.Execute(new ResizeObject(ids[0],new(30,10,20)) {MemberGroupId=group})); Vec(Object(session,ids[0]).Bounds.Size,new(30,10,20));
        var old=Object(session,ids[0]); var pivot=old.Bounds.Center;
        Ok(session.Execute(new RotateObject(ids[0],new(0,0,90)) {MemberGroupId=group}));
        foreach(var v in old.Mesh.Vertices)
        {var p=old.Transform.Apply(v)-pivot; Vec(Object(session,ids[0]).Transform.Apply(v),pivot+new Vec3(-p.Y,p.X,p.Z));}
        Ok(session.Execute(new RenameObject(ids[0],"Edited member") {MemberGroupId=group})); Check(Object(session,ids[0]).Name=="Edited member","Scoped leaf rename failed.");
        SameGroup(legacyBefore.Scene,session.Snapshot.Scene,group); Siblings(legacyBefore.Scene,session.Snapshot.Scene,[ids[0]]);
        Ok(await session.SaveAsync(Path.Combine(output,"member-rotated.3mf")));
        measurements.Add(new {file="member-rotated.3mf",expected="A=(10,-5,0)..(20,25,20), name Edited member; B/C/outside baseline; group3 Member assembly; summedVolume=34000 mm3"});
        var legacyAfter=session.Snapshot;
        foreach(var edit in new Edit[]{new AddCube(){MemberGroupId=group},new AddPrimitive(PrimitiveKind.Sphere,new(20,20,20)){MemberGroupId=group},new DuplicateObject(ids[0]){MemberGroupId=group},new DeleteObject(ids[0]){MemberGroupId=group}})
        {Check(session.Execute(edit).Outcome==(edit is AddCube or AddPrimitive ? Outcome.Conflict : Outcome.Invalid),"Unsafe scoped legacy/add edit accepted: "+edit); Same(session,legacyAfter);}
        foreach(var request in new ArrangementRequest[]{new GroupRequest([ids[0],ids[1]]){MemberGroupId=group},new UngroupRequest([ids[0]]){MemberGroupId=group}})
        {Check((await session.PreviewArrangementAsync(request)).Outcome==Outcome.Conflict,"Nested group/ungroup accepted in member mode."); Same(session,legacyAfter);}
        Check((await session.InsertAsync(Path.Combine(root,"fixtures/generated/cube-20mm.stl"))).Outcome==Outcome.Conflict,"Insert accepted in member mode."); Same(session,legacyAfter);
        Pass("Scoped legacy move/bed/resize/rotate/rename change one member only; group identity and sibling records survive; add/group/ungroup/insert and singleton duplicate/delete reject");

        fixture=await Setup(); session=fixture.Session; ids=fixture.Ids; group=fixture.Group; Ok(session.EnterGroup(group,ids[0])); var transformBefore=session.Snapshot;
        Ok(await session.PreviewTransformAsync(new MoveTransform(ids[0],new(2.5,-2.5,7.5),5){MemberGroupId=group}));
        var moving=session.Snapshot.Preview!; Accepted(session,transformBefore); Bounds(moving.Scene.Objects.Single(o=>o.Id==ids[0]).Bounds,new(5,-5,10),new(25,15,30));
        Siblings(transformBefore.Scene,moving.Scene,[ids[0]]); SameGroup(transformBefore.Scene,moving.Scene,group);
        Check(session.ExitGroup().Outcome==Outcome.Conflict,"Scope exited during preview.");
        Ok(await session.PreviewTransformAsync(new RotateTransform(ids[0],new(0,0,90)){MemberGroupId=group},moving.Id));
        var rotated=session.Snapshot.Preview!; var original=Object(session,ids[0]);
        foreach(var v in original.Mesh.Vertices) {var p=original.Transform.Apply(v)-original.Bounds.Center; Vec(rotated.Scene.Objects.Single(o=>o.Id==ids[0]).Transform.Apply(v),original.Bounds.Center+new Vec3(-p.Y,p.X,p.Z));}
        Ok(await session.PreviewTransformAsync(new ScaleTransform(ids[0],new(2,.5,1)){MemberGroupId=group},rotated.Id));
        var scaled=session.Snapshot.Preview!; Bounds(scaled.Scene.Objects.Single(o=>o.Id==ids[0]).Bounds,new(-10,5,0),new(30,15,20));
        Siblings(transformBefore.Scene,scaled.Scene,[ids[0]]); SameGroup(transformBefore.Scene,scaled.Scene,group);
        var held=session.Snapshot;
        Check((await session.PreviewTransformAsync(new ScaleTransform(ids[0],new(-1,1,1)){MemberGroupId=group},scaled.Id)).Outcome==Outcome.Invalid,"Invalid member replacement accepted."); Same(session,held);
        Check(session.ApplyPreview(moving.Id).Outcome==Outcome.Conflict,"Stale member preview token accepted."); Same(session,held);
        Check(session.DiscardPreview(scaled.Id).Outcome==Outcome.Cancelled,"Member preview discard failed."); Same(session,transformBefore);
        Ok(await session.PreviewTransformAsync(new ScaleTransform(ids[0],new(2,.5,1)){MemberGroupId=group})); Apply(session);
        var acceptedScale=session.Snapshot; Check(acceptedScale.Revision==transformBefore.Revision+1 && acceptedScale.EditingGroupId==group,"Transform Apply lost scope or committed multiple steps.");
        Ok(await session.SaveAsync(Path.Combine(output,"member-scaled.3mf"))); Ok(session.ExitGroup()); Ok(session.Undo());
        Check(session.Snapshot.Scene==transformBefore.Scene && session.Snapshot.EditingGroupId==group && session.Snapshot.SelectedObjectIds.SequenceEqual(transformBefore.SelectedObjectIds),"Undo after exit did not restore scoped source/selection.");
        Ok(session.Redo()); Check(session.Snapshot.Scene==acceptedScale.Scene && session.Snapshot.EditingGroupId==null && session.Snapshot.SelectedObjectIds.SequenceEqual(ids.Take(3)) && !session.Snapshot.IsDirty,"Redo lost the exited transformed save boundary/scope.");
        measurements.Add(new {file="member-scaled.3mf",expected="A=(-10,5,0)..(30,15,20); B=(30,0,0)..(50,20,20); C=(60,0,0)..(80,20,20); outside=(105,-30,20)..(115,0,60); group=3; volume=36000 mm3"});
        Pass("Member move/rotate/scale previews use accepted baseline, preserve siblings/group, support replacement/cancel/failure/stale guards and one-step scope-aware history across Save/Exit");

        fixture=await Setup(); session=fixture.Session; ids=fixture.Ids; group=fixture.Group; Ok(session.EnterGroup(group,ids[0])); Ok(session.SelectMany([ids[0],ids[2]],ids[2])); var multiBefore=session.Snapshot;
        Ok(await session.PreviewTransformAsync(new MoveTransform(ids[0],new(5,0,0)){MemberGroupId=group,ObjectIds=[ids[0],ids[2]]}));
        var mp=session.Snapshot.Preview!; Bounds(mp.Scene.Objects.Single(o=>o.Id==ids[0]).Bounds,new(5,0,0),new(25,20,20)); Bounds(mp.Scene.Objects.Single(o=>o.Id==ids[2]).Bounds,new(65,0,0),new(85,20,20));
        Siblings(multiBefore.Scene,mp.Scene,[ids[0],ids[2]]); Apply(session); Ok(await session.SaveAsync(Path.Combine(output,"members-moved.3mf")));
        Ok(session.Undo()); Check(session.Snapshot.Scene==multiBefore.Scene && session.Snapshot.EditingGroupId==group && session.Snapshot.SelectedObjectIds.SequenceEqual(multiBefore.SelectedObjectIds) && session.Snapshot.SelectedObjectId==ids[2],"Multi-member Undo lost independent full selection.");
        Ok(await session.PreviewArrangementAsync(new AlignmentRequest([ids[0],ids[1]],AxisAlignment.Center,AxisAlignment.Keep,AxisAlignment.Keep,ids[0]){MemberGroupId=group}));
        var aligned=session.Snapshot.Preview!; Near(aligned.Scene.Objects.Single(o=>o.Id==ids[1]).Bounds.Center.X,10); Check(aligned.Scene.Objects.Single(o=>o.Id==ids[0])==Object(session,ids[0]),"Alignment moved its reference member."); Siblings(session.Snapshot.Scene,aligned.Scene,[ids[0],ids[1]]); SameGroup(session.Snapshot.Scene,aligned.Scene,group);
        Check(session.DiscardPreview(aligned.Id).Outcome==Outcome.Cancelled,"Member alignment discard failed.");
        measurements.Add(new {file="members-moved.3mf",expected="A=(5,0,0)..(25,20,20); C=(65,0,0)..(85,20,20); B/outside unchanged; group=3; volume=36000 mm3"});
        Pass("Explicit multi-member transform and member-reference alignment operate independently within one group and restore full scoped selection on Undo");

        // More scenarios follow below; every fixture remains an original synthetic API input.
        await RepairAndConstruction();
        await DuplicateAndDelete();
        await ExportAndScopeLifetime();
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,runtime=Environment.Version.ToString(),cases,measurements,
            coreSha256=Hash(typeof(EditorSession).Assembly.Location),outputs=Directory.GetFiles(output).Concat(Directory.GetFiles(exports)).Where(p=>Path.GetExtension(p) is ".3mf" or ".stl").Select(p=>new {file=Path.GetRelativePath(Path.GetDirectoryName(output)!,p),sha256=Hash(p)})},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} member-editing scenario groups passed. Evidence: {output}");

        async Task RepairAndConstruction()
        {
            var f=await Setup(damaged:true); var s=f.Session; var a=f.Ids; var g=f.Group; Ok(s.EnterGroup(g,a[0])); var baseline=s.Snapshot;
            var diagnosis=await s.DiagnoseAsync(a[0]); Ok(diagnosis.Result); Check(diagnosis.Diagnosis!.Assessment.BoundaryEdges==4,"Damaged input is not the expected open box."); Same(s,baseline);
            foreach(var stage in new[]{"Checking mesh","Orienting faces","Filling planar holes","Preparing geometry","Ready to preview"})
            {
                using var ct=new CancellationTokenSource(); var reached=false;
                var result=await s.PreviewAsync(new RepairRequest(a[0]){MemberGroupId=g},ct.Token,new ProgressInline(p=>{if(p.Stage==stage){reached=true;ct.Cancel();}}));
                Check(reached && result.Outcome==Outcome.Cancelled,"Scoped repair cancellation failed at "+stage); Same(s,baseline);
            }
            Ok(await s.PreviewAsync(new RepairRequest(a[0]){MemberGroupId=g})); var repaired=s.Snapshot.Preview!;
            Check(repaired.Repair!.FilledHoles==1 && repaired.Repair.After.BoundaryEdges==0,"Scoped repair did not cap the original planar hole.");
            var repairObject=repaired.Scene.Objects.Single(o=>o.Id==a[0]);
            foreach(var v in baseline.Scene.Objects.Single(o=>o.Id==a[0]).Mesh.Vertices)
                Check(repairObject.Mesh.Vertices.Select(repairObject.Transform.Apply).Contains(baseline.Scene.Objects.Single(o=>o.Id==a[0]).Transform.Apply(v)),"Repair moved an original world-space vertex.");
            SameGroup(baseline.Scene,repaired.Scene,g); Siblings(baseline.Scene,repaired.Scene,[a[0]]); Accepted(s,baseline);
            Apply(s); Ok(await s.SaveAsync(Path.Combine(output,"member-repaired.3mf"))); Ok(s.Undo());
            Check(s.Snapshot.Scene==baseline.Scene && s.Snapshot.EditingGroupId==g,"Repair Undo lost damaged source/scope.");
            Pass("Scoped repair cancels at mesh/orientation/capping/kernel/precommit stages and caps one member with unchanged group/siblings; Apply and Undo retain scope");

            f=await Setup(); s=f.Session; a=f.Ids; g=f.Group; Ok(s.EnterGroup(g,a[0])); Ok(s.SelectMany([a[0],a[2]],a[2])); baseline=s.Snapshot;
            Check((await s.PreviewAsync(new SplitRequest(a[0],new(0,0,0),10){MemberGroupId=g})).Outcome==Outcome.Invalid,"Invalid split plane accepted."); Same(s,baseline);
            foreach(var stage in new[]{"Preparing geometry","Calculating geometry","Reading geometry result","Ready to preview"})
            {
                using var ct=new CancellationTokenSource(); var reached=false;
                var result=await s.PreviewAsync(new SplitRequest(a[0],new(0,0,1),10){MemberGroupId=g},ct.Token,new ProgressInline(p=>{if(p.Stage==stage){reached=true;ct.Cancel();}}));
                Check(reached && result.Outcome==Outcome.Cancelled,"Scoped split cancellation failed at "+stage); Same(s,baseline);
            }
            Ok(await s.PreviewAsync(new SplitRequest(a[0],new(0,0,1),10){MemberGroupId=g})); var split=s.Snapshot.Preview!;
            Check(split.ResultObjectIds.Length==2 && !split.Scene.Objects.Any(o=>o.Id==a[0]),"Split did not replace its member with two new leaves.");
            var sg=split.Scene.Groups.Single(); Check(sg.Id==g && sg.Name=="Member assembly" && sg.ObjectIds.Length==4 && split.ResultObjectIds.All(sg.ObjectIds.Contains),"Split membership/identity/name is wrong.");
            Check(split.SelectedObjectIds.Contains(a[2]) && split.ResultObjectIds.All(split.SelectedObjectIds.Contains) && split.SelectedObjectIds.Length==3 && split.SelectedObjectId==split.ResultObjectIds[0],"Split lost a surviving selected member or selected an outside leaf.");
            Siblings(baseline.Scene,split.Scene,[a[0]]); Accepted(s,baseline); Apply(s); Ok(await s.SaveAsync(Path.Combine(output,"member-split.3mf"))); Ok(s.Undo());
            Check(s.Snapshot.Scene==baseline.Scene && s.Snapshot.EditingGroupId==g && s.Snapshot.SelectedObjectIds.SequenceEqual(baseline.SelectedObjectIds),"Split Undo lost source/scope/selection.");
            Ok(s.Execute(new MoveObject(a[1],new(20,10,10)){MemberGroupId=g})); Ok(s.SelectMany([a[0],a[1]],a[1])); var unionBefore=s.Snapshot;
            Ok(await s.PreviewAsync(new BooleanRequest(a[0],a[1],BooleanKind.Union){MemberGroupId=g})); var union=s.Snapshot.Preview!;
            Check(union.ResultObjectIds.Length==1 && union.Scene.Groups.Single().ObjectIds.Length==2 && union.Scene.Groups.Single().Id==g,"Union did not replace operands inside the existing group.");
            Bounds(union.Scene.Objects.Single(o=>o.Id==union.ResultObjectIds[0]).Bounds,new(0,0,0),new(30,20,20)); Siblings(unionBefore.Scene,union.Scene,[a[0],a[1]]);
            Apply(s); Ok(await s.SaveAsync(Path.Combine(output,"member-union.3mf"))); Ok(s.Undo());
            Check(s.Snapshot.Scene==unionBefore.Scene && s.Snapshot.EditingGroupId==g && s.Snapshot.SelectedObjectIds.SequenceEqual(unionBefore.SelectedObjectIds) && s.Snapshot.SelectedObjectId==a[1],"Union Undo lost consumed members/full selection/scope.");
            var pair=await Setup(pair:true); Ok(pair.Session.EnterGroup(pair.Group,pair.Ids[0])); var pairBefore=pair.Session.Snapshot;
            Check((await pair.Session.PreviewAsync(new BooleanRequest(pair.Ids[0],pair.Ids[1],BooleanKind.Union){MemberGroupId=pair.Group})).Outcome==Outcome.Invalid,"Boolean left a singleton group."); Same(pair.Session,pairBefore);
            Check((await pair.Session.DeleteSelectionAsync([pair.Ids[0]],memberGroupId:pair.Group)).Outcome==Outcome.Invalid,"Delete left a singleton group."); Same(pair.Session,pairBefore);
            var outsideBefore=s.Snapshot;
            Check((await s.PreviewAsync(new SplitRequest(a[3],new(0,0,1),40){MemberGroupId=g})).Outcome==Outcome.Invalid,"Split reached outside the editing group."); Same(s,outsideBefore);
            measurements.Add(new {file="member-repaired.3mf / member-split.3mf",expected="baseline four boxes; repaired group3 / split group4; summedVolume=36000 mm3"});
            measurements.Add(new {file="member-union.3mf",expected="union=(0,0,0)..(30,20,20), C/outside baseline; group2; summedVolume=32000 mm3"});
            Pass("Scoped split/union replace only operand membership, preserve stable group and sibling records, restore full scope/selection on Undo, and reject outside targets or singleton groups");
        }
        async Task DuplicateAndDelete()
        {
            var f=await Setup(); var s=f.Session; var a=f.Ids; var g=f.Group; Ok(s.EnterGroup(g,a[0])); Ok(s.SelectMany([a[0],a[1]],a[1]));
            Ok(s.Execute(new RenameObject(a[0],"Redo marker"){MemberGroupId=g})); Ok(s.Undo()); var baseline=s.Snapshot; Check(baseline.CanRedo,"Cancellation baseline lacks redo history.");
            foreach(var selection in new[]{default(ImmutableArray<Guid>),ImmutableArray<Guid>.Empty,ImmutableArray.Create(a[3]),ImmutableArray.Create(Guid.NewGuid())})
            {
                Check((await s.DuplicateSelectionAsync(selection,memberGroupId:g)).Outcome==Outcome.Invalid,"Invalid/outside member duplicate accepted."); Same(s,baseline);
                Check((await s.DeleteSelectionAsync(selection,memberGroupId:g)).Outcome==Outcome.Invalid,"Invalid/outside member delete accepted."); Same(s,baseline);
            }
            Check((await s.DuplicateSelectionAsync([a[0]])).Outcome==Outcome.Conflict && (await s.DeleteSelectionAsync([a[0]],memberGroupId:Guid.NewGuid())).Outcome==Outcome.Conflict,"Bulk edit scope guard failed."); Same(s,baseline);
            Check((await s.DuplicateSelectionAsync([a[0]],expectedRevision:baseline.Revision-1,memberGroupId:g)).Outcome==Outcome.Conflict,"Stale scoped duplicate accepted."); Same(s,baseline);
            Check((await s.DeleteSelectionAsync([a[0],a[1]],memberGroupId:g)).Outcome==Outcome.Invalid,"Delete left fewer than two members."); Same(s,baseline);
            foreach(var deleting in new[]{false,true})
            foreach(var stage in deleting ? new[]{"before","Preparing deletion","Deleting objects","Ready to delete"} : new[]{"before","Preparing duplicate","Measuring duplicate bounds","Duplicating objects","Ready to duplicate"})
            {
                using var ct=new CancellationTokenSource(); var reached=stage=="before"; if(reached) ct.Cancel();
                var progress=new ProgressInline(p=>{if(p.Stage==stage){reached=true;ct.Cancel();}});
                var result=deleting ? await s.DeleteSelectionAsync([a[0]],ct.Token,progress,memberGroupId:g) : await s.DuplicateSelectionAsync([a[0]],ct.Token,progress,memberGroupId:g);
                Check(reached && result.Outcome==Outcome.Cancelled,"Member bulk cancellation failed: "+stage); Same(s,baseline);
            }
            Ok(await s.DuplicateSelectionAsync([a[0]],memberGroupId:g)); var copied=s.Snapshot; var copy=copied.Scene.Objects.Single(o=>!a.Contains(o.Id));
            Bounds(copy.Bounds,new(25,0,0),new(45,20,20)); Check(ReferenceEquals(copy.Mesh,baseline.Scene.Objects[0].Mesh),"Member duplicate copied mesh data.");
            Check(copied.Scene.Groups.Length==1 && copied.Scene.Groups[0].Id==g && copied.Scene.Groups[0].Name=="Member assembly" && copied.Scene.Groups[0].ObjectIds.SequenceEqual(a.Take(3).Append(copy.Id)),"Member duplicate cloned the group or lost member order.");
            Check(copied.EditingGroupId==g && copied.SelectedObjectIds.SequenceEqual(new[]{copy.Id}),"Member duplicate selected unrelated leaves."); Siblings(baseline.Scene,copied.Scene,[]);
            Ok(await s.SaveAsync(Path.Combine(output,"member-duplicated.3mf"))); Ok(s.ExitGroup()); Ok(s.Undo());
            Check(s.Snapshot.Scene==baseline.Scene && s.Snapshot.EditingGroupId==g && s.Snapshot.SelectedObjectIds.SequenceEqual(baseline.SelectedObjectIds) && s.Snapshot.SelectedObjectId==a[1],"Duplicate Undo lost scope/full selection.");
            Ok(s.Redo()); Check(s.Snapshot.Scene==copied.Scene && s.Snapshot.EditingGroupId==null && s.Snapshot.SelectedObjectId==copy.Id,"Duplicate Redo changed generated ID or exited scope."); Ok(s.EnterGroup(g,copy.Id));
            Ok(await s.DeleteSelectionAsync([copy.Id],memberGroupId:g)); Check(s.Snapshot.Scene.Groups.Single().ObjectIds.SequenceEqual(a.Take(3)),"Deleting copy did not restore original member IDs.");
            Siblings(baseline.Scene,s.Snapshot.Scene,[]); Ok(s.Undo()); Check(s.Snapshot.Scene==copied.Scene && s.Snapshot.SelectedObjectId==copy.Id,"Delete Undo did not restore copied leaf selection.");
            Ok(s.Redo()); Ok(await s.DeleteSelectionAsync([a[0]],memberGroupId:g));
            Check(s.Snapshot.Scene.Groups.Single().Id==g && s.Snapshot.Scene.Groups.Single().ObjectIds.SequenceEqual(a.Skip(1).Take(2)) && s.Snapshot.EditingGroupId==g,"Member delete lost surviving membership/scope.");
            Siblings(baseline.Scene,s.Snapshot.Scene,[a[0]]); Ok(await s.SaveAsync(Path.Combine(output,"member-deleted.3mf")));
            measurements.Add(new {file="member-duplicated.3mf",expected="baseline plus A copy=(25,0,0)..(45,20,20), same group4; summedVolume=44000 mm3"});
            measurements.Add(new {file="member-deleted.3mf",expected="baseline without A, group B/C; summedVolume=28000 mm3"});
            Pass("Member bulk duplicate/delete validate scope/targets/revision/minimum membership, cancel every preparation stage, preserve original mesh/sibling references and restore stable copy IDs/full scoped history");

            f=await Setup(); s=f.Session; a=f.Ids; g=f.Group; Ok(s.EnterGroup(g,a[0])); baseline=s.Snapshot;
            using(var entered=new ManualResetEventSlim()) using(var release=new ManualResetEventSlim()) using(var ct=new CancellationTokenSource())
            {
                var job=s.DuplicateSelectionAsync([a[0]],ct.Token,new ProgressInline(p=>{if(p.Stage=="Ready to duplicate"){entered.Set();Check(release.Wait(TimeSpan.FromSeconds(15)),"Busy barrier timed out.");}}),memberGroupId:g);
                try
                {
                    Check(entered.Wait(TimeSpan.FromSeconds(10)),"Member duplicate never acquired busy lease."); var busy=s.Snapshot;
                    Check(busy.IsBusy && s.ExitGroup().Outcome==Outcome.Busy && s.EnterGroup(g).Outcome==Outcome.Busy && s.Select(a[1]).Outcome==Outcome.Busy && s.Undo().Outcome==Outcome.Busy,"Scope/selection/history escaped busy lease.");
                    Check(s.Execute(new MoveObject(a[0],new(15,10,10)){MemberGroupId=g}).Outcome==Outcome.Busy,"Member edit escaped busy lease.");
                    Check((await s.DeleteSelectionAsync([a[0]],memberGroupId:g)).Outcome==Outcome.Busy && (await s.ExportSelectionAsync(Path.Combine(exports,"busy.3mf"),[a[0]],memberGroupId:g)).Outcome==Outcome.Busy,"Member I/O escaped busy lease."); Same(s,busy);
                }
                finally {ct.Cancel();release.Set();}
                Check((await job).Outcome==Outcome.Cancelled,"Busy member duplicate did not cancel."); Same(s,baseline);
            }
            Pass("Busy member work blocks scope entry/exit, selection/history, scoped edits and other bulk/export work; cancellation releases the lease with accepted state intact");
        }
        async Task ExportAndScopeLifetime()
        {
            var f=await Setup(); var s=f.Session; var a=f.Ids; var g=f.Group; Ok(s.EnterGroup(g,a[0])); Ok(s.SelectMany([a[0],a[2]],a[2])); var baseline=s.Snapshot;
            var reopened=new EditorSession();
            foreach(var ext in new[]{".3mf",".stl"})
            {
                var path=Path.Combine(exports,"member-one"+ext); Ok(await s.ExportSelectionAsync(path,[a[0]],memberGroupId:g)); Same(s,baseline); Ok(await reopened.OpenAsync(path));
                Check(reopened.Snapshot.Scene.Objects.Length==1 && reopened.Snapshot.Scene.Groups.IsEmpty && reopened.Snapshot.Scene.TriangleCount==12,"Partial export expanded the whole active group."); Bounds(reopened.Snapshot.Scene.Bounds,new(0,0,0),new(20,20,20));
            }
            var pairPath=Path.Combine(exports,"member-pair.3mf"); Ok(await s.ExportSelectionAsync(pairPath,[a[1],a[0],a[0]],memberGroupId:g)); Same(s,baseline); Ok(await reopened.OpenAsync(pairPath));
            Check(reopened.Snapshot.Scene.Objects.Length==2 && reopened.Snapshot.Scene.Groups.IsEmpty,"Partial group export wrote an artificial group."); Bounds(reopened.Snapshot.Scene.Bounds,new(0,0,0),new(50,20,20));
            var groupPath=Path.Combine(exports,"member-whole-group.3mf"); Ok(await s.ExportSelectionAsync(groupPath,a.Take(3).ToImmutableArray(),memberGroupId:g)); Same(s,baseline); Ok(await reopened.OpenAsync(groupPath));
            Check(reopened.Snapshot.Scene.Objects.Length==3 && reopened.Snapshot.Scene.Groups.Single().Name=="Member assembly" && reopened.Snapshot.Scene.Groups.Single().ObjectIds.Length==3,"Full active group export lost metadata.");
            Check((await s.ExportSelectionAsync(Path.Combine(exports,"outside.3mf"),[a[3]],memberGroupId:g)).Outcome==Outcome.Invalid,"Member export reached outside group."); Same(s,baseline);
            Check((await s.ExportSelectionAsync(Path.Combine(exports,"stale.3mf"),[a[0]],expectedRevision:baseline.Revision-1,memberGroupId:g)).Outcome==Outcome.Conflict,"Stale scoped export accepted."); Same(s,baseline);
            var native=baseline.FilePath!; var nativeBytes=File.ReadAllBytes(native);
            foreach(var path in new[]{native,native.ToUpperInvariant(),Path.Combine(Path.GetDirectoryName(native)!,".",Path.GetFileName(native))})
            {Check((await s.ExportSelectionAsync(path,[a[0]],memberGroupId:g)).Outcome==Outcome.Invalid,"Member export overwrote whole native document."); Same(s,baseline); Check(File.ReadAllBytes(native).SequenceEqual(nativeBytes),"Native bytes changed.");}
            Ok(await s.PreviewTransformAsync(new MoveTransform(a[0],new(1,0,0)){MemberGroupId=g})); var held=s.Snapshot;
            Check((await s.ExportSelectionAsync(Path.Combine(exports,"preview.3mf"),[a[0]],memberGroupId:g)).Outcome==Outcome.Conflict && s.EnterGroup(g).Outcome==Outcome.Conflict && s.ExitGroup().Outcome==Outcome.Conflict,"Preview allowed scope change/export."); Same(s,held);
            Check(s.DiscardPreview(held.Preview!.Id).Outcome==Outcome.Cancelled,"Scoped preview cancel failed."); Same(s,baseline);
            Pass("Scoped partial 3MF/STL exports independent leaves, full active group retains metadata; selection and native document survive stale/outside/current-path and preview rejections");

            var lastGood=Path.Combine(exports,"last-good.3mf"); File.Copy(pairPath,lastGood,true); var good=File.ReadAllBytes(lastGood);
            foreach(var stage in new[]{"before","Preparing selection export","Preparing selected objects","Preparing selected groups","Preparing save","Writing vertices","Ready to save"})
            {
                using var ct=new CancellationTokenSource(); var reached=stage=="before"; if(reached) ct.Cancel();
                var result=await s.ExportSelectionAsync(lastGood,[a[0]],ct.Token,new ProgressInline(p=>{if(p.Stage==stage){reached=true;ct.Cancel();}}),memberGroupId:g);
                Check(reached && result.Outcome==Outcome.Cancelled,"Member export cancellation failed at "+stage); Same(s,baseline); Check(File.ReadAllBytes(lastGood).SequenceEqual(good),"Cancelled member export replaced destination.");
            }
            using(var locked=new FileStream(lastGood,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {Check((await s.ExportSelectionAsync(lastGood,[a[0]],memberGroupId:g)).Outcome==Outcome.Failed,"Locked member export succeeded."); Same(s,baseline);}
            Check(File.ReadAllBytes(lastGood).SequenceEqual(good) && !Directory.EnumerateFiles(exports,"*.tmp").Any(),"Member export failure changed last good bytes or left temporary files.");
            var whole=Path.Combine(exports,"whole-document-in-scope.3mf"); Ok(await s.SaveAsync(whole)); var saved=s.Snapshot;
            Check(saved.EditingGroupId==g && saved.SelectedObjectIds.SequenceEqual(baseline.SelectedObjectIds) && !saved.IsDirty,"Whole Save lost scope/selection or saved boundary.");
            Check((await s.OpenAsync(Path.Combine(exports,"missing.3mf"))).Outcome==Outcome.Failed,"Missing open unexpectedly succeeded."); Same(s,saved);
            var staleRequest=new MoveTransform(a[0],new(1,0,0)){MemberGroupId=g}; Ok(await s.OpenAsync(whole)); var opened=s.Snapshot;
            Check(opened.EditingGroupId==null && opened.Scene.Objects.Length==4 && opened.Scene.Groups.Single().Name=="Member assembly" && !opened.CanUndo && !opened.CanRedo && !opened.IsDirty,"Open did not reset member scope/history while preserving whole scene.");
            Check(opened.Scene.Objects.All(o=>!a.Contains(o.Id)) && opened.Scene.Groups.Single().Id!=g,"Open reused session-only identities.");
            Check((await s.PreviewTransformAsync(staleRequest)).Outcome==Outcome.Conflict,"Old member request changed meaning after reopen."); Same(s,opened);
            measurements.Add(new {file="member-one.3mf / member-one.stl",expected="A only; no groups; summedVolume=8000 mm3"});
            measurements.Add(new {file="member-pair.3mf / last-good.3mf",expected="A/B only; no groups; summedVolume=16000 mm3"});
            measurements.Add(new {file="member-whole-group.3mf",expected="A/B/C only; group3 Member assembly; summedVolume=24000 mm3"});
            measurements.Add(new {file="whole-document-in-scope.3mf",expected="baseline four boxes/group3; summedVolume=36000 mm3"});
            Pass("Partial export cancellation/pre-replace/fault preserves scoped session and last good bytes; whole Save preserves scope and successful Open resets it with new identities while failed Open preserves it");
        }
    }
    private static SceneObject Object(EditorSession session,Guid id)=>session.Snapshot.Scene.Objects.Single(o=>o.Id==id);
    private static void SameGroup(Scene before,Scene after,Guid group)
    {var a=before.Groups.Single(g=>g.Id==group); var b=after.Groups.Single(g=>g.Id==group); Check(a.Id==b.Id && a.Name==b.Name && a.ObjectIds.SequenceEqual(b.ObjectIds),"Group identity/name/membership changed unexpectedly.");}
    private static void Siblings(Scene before,Scene after,ImmutableArray<Guid> changed)
    {foreach(var obj in before.Objects) if(!changed.Contains(obj.Id)) Check(ReferenceEquals(obj,after.Objects.Single(o=>o.Id==obj.Id)),"An untouched sibling/outside record was replaced.");}
    private static void Apply(EditorSession s)=>Ok(s.ApplyPreview(s.Snapshot.Preview!.Id,s.Snapshot.Preview.BaseRevision));
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Check(bool value,string message) {if(!value) throw new InvalidOperationException(message);}
    private static void Same(EditorSession s,EditorSnapshot prior)=>Check(s.Snapshot==prior,"Member operation changed accepted state/prior preview.");
    private static void Accepted(EditorSession s,EditorSnapshot prior)=>Check((s.Snapshot with {Preview=null})==prior,"Preview changed accepted scoped state.");
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<1e-7,$"Expected {b}, got {a}.");
    private static void Vec(Vec3 a,Vec3 b) {Near(a.X,b.X); Near(a.Y,b.Y); Near(a.Z,b.Z);}
    private static void Bounds(Bounds3 b,Vec3 min,Vec3 max) {Vec(b.Min,min); Vec(b.Max,max);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class ProgressInline(Action<OperationProgress> action):IProgress<OperationProgress> {public void Report(OperationProgress p)=>action(p);}
}
