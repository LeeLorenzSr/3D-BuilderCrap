using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class TextEmbossChecks
{
    private static ImmutableArray<Vec2> Rect(double x,double y,double w,double h)=>[new(x,y),new(x+w,y),new(x+w,y+h),new(x,y+h)];
    private static readonly TextOutline LetterO=new([Rect(0,0,10,12),Rect(2,2,6,8)]);
    private static OutlineTextSource Direct(TextOutline outline)=>new(outline);
    public static async Task Run(string root)
    {
        var output=Path.Combine(root,".local/evidence/text-emboss-20261020/core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/text-emboss-20261020/inputs/notched-plate.3mf");
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var fault=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/checks/Builder.WorkerFaultProbe.exe");
        var cases=new List<string>();var files=new List<object>();
        EditorSession New(ITextOutlineProvider? provider=null)=>new(worker,provider);
        async Task<EditorSession> Plate(){var s=New();Ok(await s.OpenAsync(input));Near(Volume(s.Snapshot.Scene),11040);return s;}
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task Save(EditorSession s,string name,TextOutline outline,double expectedVolume,int expectedComponents,object parameters)
        {
            var snapshot=s.Snapshot;Near(Volume(snapshot.Scene),expectedVolume);Topology(snapshot.Scene,expectedComponents);
            var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");
            Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));
            foreach(var path in new[]{native,stl})
            {
                var reopened=New();Ok(await reopened.OpenAsync(path));Near(Volume(reopened.Snapshot.Scene),expectedVolume,path.EndsWith(".stl")?2e-4:1e-7);
                Topology(reopened.Snapshot.Scene,expectedComponents);VecNear(reopened.Snapshot.Scene.Bounds.Min,snapshot.Scene.Bounds.Min,2e-5);VecNear(reopened.Snapshot.Scene.Bounds.Max,snapshot.Scene.Bounds.Max,2e-5);
            }
            files.Add(new {name,parameters,explicitInputContoursMillimeters=outline.Contours,fillRule=outline.FillRule.ToString(),outline.Provenance,
                expectedVolumeMm3=expectedVolume,expectedComponents,bounds=snapshot.Scene.Bounds,
                native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=new {path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        async Task Commit(EditorSession s,GeometryRequest request)
        {
            var before=s.Snapshot;Ok(await s.PreviewAsync(request,expectedRevision:before.Revision));Accepted(s,before);
            var preview=s.Snapshot.Preview!;Check(preview.Id!=Guid.Empty,"Missing preview token.");Ok(s.ApplyPreview(preview.Id,before.Revision));
            var after=s.Snapshot;Check(after.Revision==before.Revision+1,"Not a single accepted revision.");
            Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds)&&s.Snapshot.EditingGroupId==before.EditingGroupId,"Undo lost original scene/selection/scope.");
            Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,after.Scene),"Redo lost immutable candidate.");
        }
        // Explicit O deliberately gives both contours the same winding: EvenOdd must preserve its counter.
        var o=New();await Commit(o,new TextRequest(Direct(LetterO),3));
        VecNear(o.Snapshot.Scene.Bounds.Min,new());VecNear(o.Snapshot.Scene.Bounds.Max,new(10,12,3));
        await Save(o,"text-o",LetterO,216,1,new {operation="text",depth=3,position=new Vec3(),genus=1});
        var bOutline=new TextOutline([Rect(0,0,10,18),Rect(2,2,6,5),Rect(2,10,6,6)]);
        var b=New();await Commit(b,new TextRequest(Direct(bOutline),2,new(3,-5,7)));
        await Save(b,"text-b",bOutline,228,1,new {operation="text",depth=2,position=new Vec3(3,-5,7),genus=2});
        var dots=new TextOutline([Rect(0,0,3,4),Rect(7,0,3,4)]);
        var islands=New();await Commit(islands,new TextRequest(Direct(dots),3));await Save(islands,"text-disconnected",dots,72,2,new {operation="text",depth=3,position=new Vec3()});
        var nonzero=LetterO with {FillRule=TextFillRule.NonZero,Contours=[LetterO.Contours[0],LetterO.Contours[1].Reverse().ToImmutableArray()]};
        var nz=New();await Commit(nz,new TextRequest(Direct(nonzero),3));Near(Volume(nz.Snapshot.Scene),216);
        var redundant=New();await Commit(redundant,new TextRequest(Direct(LetterO with {FillRule=TextFillRule.NonZero}),3));Near(Volume(redundant.Snapshot.Scene),360);
        var nested=new TextOutline([Rect(0,0,10,12),Rect(2,2,6,8),Rect(4,4,2,4)]);
        var nestedSession=New();await Commit(nestedSession,new TextRequest(Direct(nested),3));Near(Volume(nestedSession.Snapshot.Scene),240);Topology(nestedSession.Snapshot.Scene,2);
        Pass("Explicit O/B counters, disjoint glyph islands, nonzero/even-odd winding, redundant boundaries and nested filled islands extrude with analytic volume and outward topology");

        foreach(var mode in Enum.GetValues<EmbossMode>())
        {
            var s=await Plate();var original=s.Snapshot;var id=original.SelectedObjectId!.Value;var depth=mode==EmbossMode.Raised?2:1;
            await Commit(s,new EmbossRequest(id,Direct(LetterO),new(EmbossFace.PositiveZ),depth,mode));
            Check(s.Snapshot.Scene.Objects.Single().Id==id && s.Snapshot.Scene.Objects.Single().Name==original.Scene.Objects.Single().Name,"Emboss replaced target identity/name.");
            Near(s.Snapshot.Scene.Bounds.Max.Z,mode==EmbossMode.Raised?8:6);
            await Save(s,mode==EmbossMode.Raised?"raised-o":"engraved-o",LetterO,mode==EmbossMode.Raised?11184:10968,1,
                new {operation=mode.ToString(),target="notched-plate.3mf",face="PositiveZ",offset=new Vec2(),rotation=0,depth,overlap=Math.Min(.1,depth/10.0)});
        }
        var small=new TextOutline([Rect(0,0,2,2)]);
        foreach(var face in Enum.GetValues<EmbossFace>())
        {
            var s=await Plate();await Commit(s,new EmbossRequest(s.Snapshot.SelectedObjectId!.Value,Direct(small),new(face,new(.25,-.25),90),.5));
            await Save(s,"raised-face-"+face.ToString().ToLowerInvariant(),small,11042,1,new {operation="Raised",target="notched-plate.3mf",face=face.ToString(),offset=new Vec2(.25,-.25),rotation=90,depth=.5,overlap=.05});
        }
        var transformed=await Plate();var transformedId=transformed.Snapshot.SelectedObjectId!.Value;
        Ok(transformed.Execute(new MoveObject(transformedId,new(57,-12,23))));Ok(transformed.Execute(new RotateObject(transformedId,new(0,0,90))));
        Ok(transformed.Execute(new ResizeObject(transformedId,new(48,90,9))));Near(Volume(transformed.Snapshot.Scene),37260);
        await Commit(transformed,new EmbossRequest(transformedId,Direct(LetterO),new(EmbossFace.PositiveZ,new(1,-2),90),2));
        await Save(transformed,"raised-transformed-o",LetterO,37404,1,new {operation="Raised",target="notched-plate.3mf",moveCenter=new Vec3(57,-12,23),worldRotation=new Vec3(0,0,90),resize=new Vec3(48,90,9),face="PositiveZ",offset=new Vec2(1,-2),rotation=90,depth=2,overlap=.1});
        Pass("Raised/engraved rectangular O on notched plate and all six right-handed face frames with rotated/offset labels; identity, one-step history and native/STL roundtrip");

        var unsupported=await Plate();var uId=unsupported.Snapshot.SelectedObjectId!.Value;
        Ok(await unsupported.PreviewEmbossAsync(new(uId,Direct(LetterO),new(EmbossFace.PositiveZ),2)));var held=unsupported.Snapshot;
        foreach(var request in new[]{
            new EmbossRequest(uId,Direct(LetterO),new(EmbossFace.PositiveZ,new(25,12)),2), // Partial notch: only 20 of 72 mm2 supported.
            new EmbossRequest(uId,Direct(LetterO),new(EmbossFace.PositiveZ,new(500,0)),2),
            new EmbossRequest(uId,Direct(LetterO),new(EmbossFace.PositiveZ),6,EmbossMode.Engraved), // Exact thickness must retain backing.
            new EmbossRequest(uId,Direct(LetterO),new(EmbossFace.PositiveZ),7,EmbossMode.Engraved),
            new EmbossRequest(uId,Direct(new([Rect(0,0,10,12),Rect(50,30,.001,.001)])),new(EmbossFace.PositiveZ),2)})
        {Check((await unsupported.PreviewEmbossAsync(request,held.Preview!.Id)).Outcome==Outcome.Invalid,"Unsupported/through label was accepted.");Same(unsupported,held);}
        var wedge=New();Ok(wedge.Execute(new AddPrimitive(PrimitiveKind.Wedge,new(30,30,10))));var wedgeBefore=wedge.Snapshot;
        Check((await wedge.PreviewEmbossAsync(new(wedgeBefore.SelectedObjectId!.Value,Direct(small),new(EmbossFace.PositiveZ),1))).Outcome==Outcome.Invalid,"Sloped wedge AABB was accepted as a full planar face.");Same(wedge,wedgeBefore);
        Pass("Partial notch, floating word/tiny disconnected island, exact-depth/through engraving and sloped AABB false support reject without disturbing held preview");

        var cancel=await Plate();Ok(cancel.Execute(new RenameObject(cancel.Snapshot.SelectedObjectId!.Value,"temporary")));Ok(cancel.Undo());
        var accepted=cancel.Snapshot;var createRequest=new TextRequest(Direct(LetterO),3);
        Ok(await cancel.PreviewTextAsync(createRequest));var initial=cancel.Snapshot;
        Ok(await cancel.PreviewTextAsync(createRequest with {DepthMillimeters=4},initial.Preview!.Id));var current=cancel.Snapshot;
        Check(current.Preview!.Id!=initial.Preview.Id && current.Preview.ResultObjectIds.SequenceEqual(initial.Preview.ResultObjectIds),"Text replacement did not keep candidate ID/fresh token.");
        foreach(var stage in new[]{"before","Preparing text","Validating text outlines","Preparing geometry","Calculating geometry","Reading geometry result","Ready to preview text"})
        {
            using var cts=new CancellationTokenSource();if(stage=="before")cts.Cancel();
            var result=await cancel.PreviewTextAsync(createRequest,current.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage==stage)cts.Cancel();}));
            Check(result.Outcome==Outcome.Cancelled,"Cancellation failed at "+stage+": "+result);Same(cancel,current);
        }
        Check((await cancel.PreviewTextAsync(createRequest,initial.Preview.Id)).Outcome==Outcome.Conflict,"Stale preview update accepted.");Same(cancel,current);
        Check((await cancel.PreviewTextAsync(createRequest,current.Preview.Id,expectedRevision:accepted.Revision-1)).Outcome==Outcome.Conflict,"Stale revision accepted.");
        Check(cancel.Execute(new AddCube()).Outcome==Outcome.Conflict && cancel.Select(null).Outcome==Outcome.Conflict && cancel.Undo().Outcome==Outcome.Conflict &&
            (await cancel.SaveAsync(Path.Combine(output,"must-not-save.3mf"))).Outcome==Outcome.Conflict,"Preview guards failed.");
        Check((await cancel.PreviewEmbossAsync(new(accepted.SelectedObjectId!.Value,Direct(small),new(EmbossFace.PositiveZ),1),current.Preview.Id)).Outcome==Outcome.Conflict,"Foreign preview type replaced text.");Same(cancel,current);
        Check(cancel.DiscardPreview(current.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(cancel,accepted);
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cts=new CancellationTokenSource())
        {
            var job=cancel.PreviewTextAsync(createRequest,cancellation:cts.Token,progress:new Progress(p=>{if(p.Stage=="Preparing text"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));
            Check(entered.Wait(10000),"Job did not enter.");
            Check(cancel.Execute(new AddCube()).Outcome==Outcome.Busy && cancel.ApplyPreview(Guid.NewGuid()).Outcome==Outcome.Busy &&
                (await cancel.PreviewTextAsync(createRequest)).Outcome==Outcome.Busy && cancel.Select(null).Outcome==Outcome.Busy,"Busy guard failed.");
            cts.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy job did not cancel.");Same(cancel,accepted);
        }
        Pass("Replacement stable ID/fresh token, exact-type/token/revision ownership, pre/during/ready cancellation, preview/file/history/selection guards and serialized busy lease preserve accepted state and redo");

        var invalid=New();var baseline=invalid.Snapshot;
        TextOutline[] badOutlines=[new([]),new([Rect(0,0,0,3)]),new([[new(0,0),new(3,3),new(0,3),new(3,0)]]),
            new([Rect(0,0,4,4),Rect(4,0,3,3)]),new([[new(0,0),new(4,0),new(2,0),new(2,3),new(0,3)]]),
            new([Rect(0,0,1001,3)]),new([Rect(0,0,.01,3)]),new([[new(0,0),new(double.NaN,3),new(4,4)]]),
            new(Enumerable.Range(0,257).Select(i=>Rect(i*2,0,1,1)).ToImmutableArray()),
            new([Enumerable.Range(0,4097).Select(i=>new Vec2(Math.Cos(i*.01),Math.Sin(i*.01))).ToImmutableArray()])];
        foreach(var bad in badOutlines){Check((await invalid.PreviewTextAsync(new(Direct(bad)))).Outcome==Outcome.Invalid,"Invalid outline accepted.");Same(invalid,baseline);}
        foreach(var request in new[]{createRequest with {DepthMillimeters=0},createRequest with {DepthMillimeters=double.NaN},
            createRequest with {PositionMillimeters=new(double.PositiveInfinity,0,0)},new TextRequest(new FontTextSource(new("O","Absent",12)))})
        {Check((await invalid.PreviewTextAsync(request)).Outcome==Outcome.Invalid,"Invalid text request accepted.");Same(invalid,baseline);}
        // Object count is a scene-wide constraint, independent of contour/worker budgets.
        var full=New();for(var i=0;i<ModelLimits.MaxObjects;i++)Ok(full.Execute(new AddCube()));var fullBefore=full.Snapshot;
        Check((await full.PreviewTextAsync(createRequest)).Outcome==Outcome.Invalid,"Text exceeded scene object budget.");Same(full,fullBefore);
        Pass("Empty, zero-area, self-crossing, touching, overlapping/backtracking, physical-size and contour/point-budget failures plus unsupported font provider leave clean state");

        var provider=new Provider(LetterO);var font=New(provider);var catalogBefore=font.Snapshot;Check(font.GetFontFamilies().SequenceEqual(new[]{"Fixture font"}),"Catalog seam failed.");Same(font,catalogBefore);
        var fontRequest=new TextRequest(new FontTextSource(new("O","Fixture font",12)),3);
        await Commit(font,fontRequest);Near(Volume(font.Snapshot.Scene),216);
        await Save(font,"text-provider-o",LetterO,216,1,new {operation="text",source="Deterministic headless provider, not installed font",text="O",font="Fixture font",height=12,depth=3});
        var supplementary=New(provider);await Commit(supplementary,new TextRequest(new FontTextSource(new(new string('A',92)+"😀","Fixture font",12)),3));
        Check(supplementary.Snapshot.Scene.Objects.Single().Name.Length==99,"Generated name split supplementary Unicode.");Ok(await supplementary.SaveAsync(Path.Combine(output,"long-unicode-name.3mf")));
        var reopenedName=New();Ok(await reopenedName.OpenAsync(Path.Combine(output,"long-unicode-name.3mf")));Check(reopenedName.Snapshot.Scene.Objects.Single().Name==supplementary.Snapshot.Scene.Objects.Single().Name,"Unicode name failed roundtrip.");
        var fontHeld=font.Snapshot;Ok(await font.PreviewTextAsync(fontRequest));fontHeld=font.Snapshot;
        foreach(var failure in new[]{"cancel","fail","invalid"})
        {provider.Failure=failure;var r=await font.PreviewTextAsync(fontRequest,fontHeld.Preview!.Id);Check(r.Outcome==(failure=="cancel"?Outcome.Cancelled:failure=="invalid"?Outcome.Invalid:Outcome.Failed),"Provider failure classification incorrect.");Same(font,fontHeld);}provider.Failure=null;
        foreach(var spec in new[]{new TextOutlineSpec("\uD800","Fixture font",12),new("O\nB","Fixture font",12),new(" ","Fixture font",12),new(new string('A',129),"Fixture font",12),new("O","Fixture font",.01),new("O","Fixture font",12,2)})
        {Check((await font.PreviewTextAsync(new(new FontTextSource(spec)),fontHeld.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid font spec accepted.");Same(font,fontHeld);}
        Pass("Injected provider/catalog contract, deterministic headless font output, surrogate-safe generated name roundtrip and provider cancellation/failure/invalid-spec rollback");

        var group=await Plate();Ok(group.Execute(new AddCube()));var outsider=group.Snapshot.Scene.Objects[^1];
        Ok(group.Execute(new MoveObject(outsider.Id,new(100,10,10))));var ids=group.Snapshot.Scene.Objects.Select(x=>x.Id).ToImmutableArray();
        Ok(await group.PreviewArrangementAsync(new GroupRequest(ids,"Label assembly")));Ok(group.ApplyPreview(group.Snapshot.Preview!.Id));var grouped=group.Snapshot;
        Check((await group.PreviewEmbossAsync(new(ids[0],Direct(LetterO),new(EmbossFace.PositiveZ),2))).Outcome==Outcome.Invalid,"Scene-scope member edit accepted.");Same(group,grouped);
        Ok(group.EnterGroup(grouped.Scene.Groups.Single().Id,ids[0]));var scoped=group.Snapshot;var scope=scoped.EditingGroupId;
        Check((await group.PreviewTextAsync(createRequest with {MemberGroupId=scope})).Outcome==Outcome.Conflict,"Scoped add accepted.");Same(group,scoped);
        await Commit(group,new EmbossRequest(ids[0],Direct(LetterO),new(EmbossFace.PositiveZ),2){MemberGroupId=scope});
        Check(group.Snapshot.Scene.Groups.SequenceEqual(scoped.Scene.Groups)&&ReferenceEquals(group.Snapshot.Scene.Objects[1],scoped.Scene.Objects[1])&&group.Snapshot.EditingGroupId==scope,"Member emboss changed group/sibling/scope.");
        await Save(group,"raised-member-o",LetterO,19184,2,new {operation="Raised",target="notched-plate plus cube bounds90..110,0..20,0..20",face="PositiveZ",depth=2,memberScope=true});
        Ok(group.ExitGroup());var exited=group.Snapshot;Check((await group.PreviewEmbossAsync(new(ids[0],Direct(small),new(EmbossFace.PositiveZ),1){MemberGroupId=scope})).Outcome==Outcome.Conflict,"Stale scope replay accepted.");Same(group,exited);
        Pass("Grouped member-only policy, scoped addition guard, isolated member mesh replacement, stable group/name/siblings and scope-aware one-step history/replay");

        var adjacent=New();Ok(adjacent.Execute(new AddCube()));var aId=adjacent.Snapshot.SelectedObjectId!.Value;Ok(adjacent.Execute(new AddCube()));var bId=adjacent.Snapshot.SelectedObjectId!.Value;
        Ok(adjacent.Execute(new MoveObject(bId,new(20,10,10))));await Commit(adjacent,new BooleanRequest(aId,bId,BooleanKind.Union));Near(Volume(adjacent.Snapshot.Scene),12000);
        await Commit(adjacent,new SplitRequest(adjacent.Snapshot.SelectedObjectId!.Value,new(1,0,0),15));Near(Volume(adjacent.Snapshot.Scene),12000);Topology(adjacent.Snapshot.Scene,2);
        var damaged=New();Ok(await damaged.OpenAsync(Path.Combine(root,"fixtures/generated/missing-top.stl")));Ok(await damaged.PreviewAsync(new RepairRequest(damaged.Snapshot.SelectedObjectId!.Value)));Ok(damaged.ApplyPreview(damaged.Snapshot.Preview!.Id));Near(Volume(damaged.Snapshot.Scene),8000);
        var broken=new EditorSession(fault);Ok(await broken.OpenAsync(input));var brokenBefore=broken.Snapshot;
        foreach(var request in new GeometryRequest[]{createRequest,new EmbossRequest(brokenBefore.SelectedObjectId!.Value,Direct(small),new(EmbossFace.PositiveZ),1),
            new EmbossRequest(brokenBefore.SelectedObjectId!.Value,Direct(small),new(EmbossFace.PositiveZ),1,EmbossMode.Engraved),new SplitRequest(brokenBefore.SelectedObjectId!.Value,new(0,0,1),3)})
        {Check((await broken.PreviewAsync(request)).Outcome==Outcome.Failed,"Fault/version mismatch did not fail safely.");Same(broken,brokenBefore);}
        Pass("Protocol3 adjacent union/split/repair validation and text/emboss abrupt, truncated, missing and stale-version worker failures preserve state");
        File.WriteAllText(Path.Combine(output,"text-results.json"),JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,kernel="Manifold 3.5.4",kernelCommit="ce50d78021d64507f89e8c9fc2c2e51018117857",protocolVersion=3,
            core=new {path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new {path=worker,sha256=Hash(worker)},faultPeer=new {path=fault,sha256=Hash(fault)},
            input=new {path=input,sha256=Hash(input)},supportRelativeVolumeTolerance=1e-6,supportAbsoluteFloor=0,outputs=files,
            limits=new {points=4096,contours=256,textUtf16Characters=128,heightMm="0.1..1000",depthMm="0.05..1000",sceneTriangles=ModelLimits.MaxTriangles},
            limitations="Planar AABB face placement with exact footprint support; no curved projection, bevel or font-provider parity claim. Font provider output is deterministic test input; Windows font adapter has separate evidence."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} text/emboss scenario groups passed. Evidence: {output}");
    }
    private sealed class Provider(TextOutline outline):ITextOutlineProvider,ITextFontCatalog
    {
        public string? Failure;
        public IReadOnlyList<string> GetFontFamilies()=>["Fixture font"];
        public Task<TextOutline> BuildAsync(TextOutlineSpec specification,CancellationToken cancellation=default)
        {
            cancellation.ThrowIfCancellationRequested();
            if(Failure=="cancel")throw new OperationCanceledException();if(Failure=="fail")throw new IOException("fixture provider failure");
            return Task.FromResult(Failure=="invalid"?new TextOutline([]):outline with {Provenance="Deterministic analytic test provider"});
        }
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult r)=>Check(r.Succeeded,r.ToString());
    private static void Near(double actual,double expected,double tolerance=1e-6)=>Check(Math.Abs(actual-expected)<=tolerance,$"Expected {expected}, got {actual}.");
    private static void VecNear(Vec3 a,Vec3 b,double t=1e-7){Near(a.X,b.X,t);Near(a.Y,b.Y,t);Near(a.Z,b.Z,t);}
    private static void Same(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot==before,"Failure/cancel changed scene/path/selection/scope/history/preview.");
    private static void Accepted(EditorSession s,EditorSnapshot before)=>Check(s.Snapshot with {Preview=null}==before,"Preview changed accepted state.");
    private static double Volume(Scene scene)=>scene.Objects.Sum(o=>{
        var origin=o.Transform.Apply(o.Mesh.Vertices[0]);return o.Mesh.Triangles.Sum(t=>{var a=o.Transform.Apply(o.Mesh.Vertices[t.A])-origin;var b=o.Transform.Apply(o.Mesh.Vertices[t.B])-origin;var c=o.Transform.Apply(o.Mesh.Vertices[t.C])-origin;var cross=Vec3.Cross(b,c);return(a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z)/6;});});
    private static void Topology(Scene scene,int expectedComponents)
    {
        var count=0;
        foreach(var obj in scene.Objects)
        {
            var points=new Dictionary<Vec3,int>();var remap=obj.Mesh.Vertices.Select(obj.Transform.Apply).Select(v=>{if(!points.TryGetValue(v,out var i)){i=points.Count;points.Add(v,i);}return i;}).ToArray();
            var edges=new Dictionary<(int,int),int>();var adjacency=new Dictionary<int,HashSet<int>>();
            foreach(var t in obj.Mesh.Triangles)
            {
                var a=remap[t.A];var b=remap[t.B];var c=remap[t.C];Check(Vec3.Cross(obj.Mesh.Vertices[t.B]-obj.Mesh.Vertices[t.A],obj.Mesh.Vertices[t.C]-obj.Mesh.Vertices[t.A])!=new Vec3(),"Zero-area output face.");
                foreach(var e in new[]{(a,b),(b,c),(c,a)}){edges[e]=edges.GetValueOrDefault(e)+1;if(!adjacency.TryGetValue(e.Item1,out var neighbors))adjacency[e.Item1]=neighbors=[];neighbors.Add(e.Item2);}
            }
            Check(edges.All(e=>e.Value==1&&edges.GetValueOrDefault((e.Key.Item2,e.Key.Item1))==1),"Output not closed consistently directed indexed topology.");
            var unseen=adjacency.Keys.ToHashSet();while(unseen.Count>0){count++;var stack=new Stack<int>();stack.Push(unseen.First());while(stack.TryPop(out var v))if(unseen.Remove(v))foreach(var n in adjacency[v])stack.Push(n);}
        }
        Check(count==expectedComponents,$"Expected {expectedComponents} connected shells, got {count}.");Check(Volume(scene)>0,"Nonpositive output volume.");
    }
}
