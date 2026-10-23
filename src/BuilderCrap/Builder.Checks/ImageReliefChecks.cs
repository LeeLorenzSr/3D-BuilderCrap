using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageReliefChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var evidence=evidenceRoot??Path.Combine(root,".local/evidence/image-relief-20261020");var output=Path.Combine(evidence,"core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/image-relief-20261020/inputs");var pixelsPath=Path.Combine(input,"pixels.json");
        using var fixture=JsonDocument.Parse(File.ReadAllText(pixelsPath));
        RasterImage Pixels(string name)
        {
            var p=fixture.RootElement.GetProperty(name);
            return new(p.GetProperty("width").GetInt32(),p.GetProperty("height").GetInt32(),p.GetProperty("rgba").EnumerateArray().Select(v=>v.GetByte()).ToImmutableArray(),"Synthetic pixels.json / "+name);
        }
        var black=Pixels("black");var white=Pixels("white");var ramp=Pixels("ramp");var marker=Pixels("marker");var alpha=Pixels("alpha");var single=Pixels("single-pixel");
        var cases=new List<string>();var files=new List<object>();
        EditorSession New(IRasterImageDecoder? decoder=null)=>new(imageDecoder:decoder);
        ImageReliefRequest Request(RasterImage image,double width=40,double depth=3,double backing=1,int samples=9,bool invert=false)=>new(new PixelImageSource(image),width,depth,backing,samples,invert);
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task SaveCase(string name,RasterImage image,ImageReliefRequest request)
        {
            var s=New();var before=s.Snapshot;Ok(await s.PreviewAsync(request));Accepted(s,before);var preview=s.Snapshot.Preview!;
            var obj=preview.Scene.Objects.Single();var(gx,gy)=Grid(image,request.Samples);Geometry(obj,image,request,gx,gy);
            Check(preview.Summary.Contains($"{gx} × {gy}")&&preview.Summary.Contains("triangles")&&!preview.Summary.Contains(image.Provenance??"\0"),"Summary missed grid or exposed raw provenance.");
            Ok(s.ApplyPreview(preview.Id));Check(s.Snapshot.SelectedObjectIds.SequenceEqual(new[]{obj.Id})&&s.Snapshot.Revision==before.Revision+1,"Relief acceptance not one selected edit.");
            var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));
            var saved=s.Snapshot;Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.IsDirty,"Undo across save lost original empty scene or dirty boundary.");
            Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,saved.Scene)&&!s.Snapshot.IsDirty,"Redo lost saved candidate/clean boundary.");
            foreach(var path in new[]{native,stl})
            {
                var reopened=New();Ok(await reopened.OpenAsync(path));var actual=reopened.Snapshot.Scene.Objects.Single();
                Near(Volume(actual),Integral(obj,gx,gy),path.EndsWith(".stl")?1e-2:1e-6);Closed(actual);
                VecNear(actual.Bounds.Min,obj.Bounds.Min,2e-5);VecNear(actual.Bounds.Max,obj.Bounds.Max,2e-5);
                if(path.EndsWith(".3mf"))Check(actual.Mesh.Triangles.Length==4*(gx*gy-1),"Native save lost exact relief triangles.");
            }
            files.Add(new {name,request=new {request.WidthMillimeters,request.ReliefDepthMillimeters,request.BaseThicknessMillimeters,request.Samples,request.Invert},
                source=new {image.Width,image.Height,rgba=image.Rgba.Select(b=>(int)b).ToArray(),image.Provenance,rgbaSha256=HashBytes(image.Rgba.ToArray())},
                grid=new {x=gx,y=gy,vertices=2*gx*gy,triangles=4*(gx*gy-1)},expectedBounds=obj.Bounds,expectedVolumeMm3=Integral(obj,gx,gy),
                native=new {path=Path.GetFileName(native),sha256=Hash(native)},stl=new {path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        await SaveCase("direct-black",black,Request(black,samples:2));
        await SaveCase("direct-white",white,Request(white,samples:5));
        await SaveCase("direct-ramp",ramp,Request(ramp,width:45,samples:9));
        await SaveCase("direct-marker",marker,Request(marker,width:64,depth:4,backing:1.5,samples:32));
        await SaveCase("direct-alpha",alpha,Request(alpha,width:30,depth:4,backing:.5,samples:5));
        await SaveCase("direct-alpha-invert",alpha,Request(alpha,width:30,depth:4,backing:.5,samples:5,invert:true));
        await SaveCase("direct-single-pixel",single,Request(single,width:12,depth:6,backing:.5,samples:3));
        Pass("Generated direct RGBA black/white/ramp/asymmetric marker/alpha/invert/single-pixel reliefs have analytic grid heights, positive closed topology and 3MF/STL roundtrip with Undo/Redo across save");

        // This impulse distinguishes the LL→UR diagonal (volume 200) from the opposite diagonal (300).
        var corner=new RasterImage(2,2,[255,255,255,255,0,0,0,255,0,0,0,255,0,0,0,255],"Analytic upper-left corner impulse");
        await SaveCase("direct-diagonal-corner",corner,Request(corner,width:10,depth:6,backing:1,samples:2));
        var diagonal=New();Ok(await diagonal.PreviewImageReliefAsync(Request(corner,width:10,depth:6,backing:1,samples:2)));var dm=diagonal.Snapshot.Preview!.Scene.Objects.Single();
        Near(Volume(dm),200);Near(dm.Mesh.Vertices[0].Z,1);Near(dm.Mesh.Vertices[2].Z,7);Near(dm.Mesh.Vertices[3].Z,1);
        var flat=New();Ok(await flat.PreviewImageReliefAsync(Request(alpha,depth:0,backing:2,samples:2)));Near(flat.Snapshot.Preview!.Scene.Bounds.Max.Z,2);
        var transparent=alpha with {Width=1,Height=1,Rgba=[255,255,255,0]};
        foreach(var invert in new[]{false,true}){var s=New();Ok(await s.PreviewImageReliefAsync(Request(transparent,invert:invert)));Near(s.Snapshot.Preview!.Scene.Bounds.Max.Z,1);}
        var oneAxis=new RasterImage(1,3,[255,255,255,255,128,128,128,255,0,0,0,255],"Analytic single-column ramp");
        var tall=New();Ok(await tall.PreviewImageReliefAsync(Request(oneAxis,width:3,samples:5)));var tm=tall.Snapshot.Preview!.Scene.Objects.Single();Geometry(tm,oneAxis,Request(oneAxis,width:3,samples:5),2,5);Near(tm.Mesh.Vertices[0].Z,1);Near(tm.Mesh.Vertices[8].Z,4);
        Check(Grid(white,5)==(5,3),"Short grid dimension did not round halfway away from zero.");
        var exactAlpha=New();Ok(await exactAlpha.PreviewImageReliefAsync(Request(single,width:12,depth:1,backing:1,samples:2)));Near(exactAlpha.Snapshot.Preview!.Scene.Bounds.Max.Z,1+((.2126*128+.7152*64+.0722*32)/255)*(128d/255));
        Pass("Source top maps +Y; LL→UR diagonal has independent impulse integral; alpha inversion occurs before alpha, no gamma normalization, zero depth and single-pixel axes remain solid; short-grid ties round away");

        var accepted=New();Ok(accepted.Execute(new AddCube()));Ok(await accepted.SaveAsync(Path.Combine(output,"accepted-baseline.3mf")));
        Ok(accepted.Execute(new RenameObject(accepted.Snapshot.SelectedObjectId!.Value,"temporary")));Ok(accepted.Undo());var baseState=accepted.Snapshot;
        var request=Request(marker,samples:32);Ok(await accepted.PreviewImageReliefAsync(request));var initial=accepted.Snapshot;
        Ok(await accepted.PreviewImageReliefAsync(request with {Invert=true,WidthMillimeters=72},initial.Preview!.Id));var held=accepted.Snapshot;
        Check(held.Preview!.Id!=initial.Preview.Id&&held.Preview.ResultObjectIds.SequenceEqual(initial.Preview.ResultObjectIds),"Replacement failed fresh token/stable candidate ID policy.");
        Check(ReferenceEquals(held.Preview.Scene.Objects[0],baseState.Scene.Objects[0])&&held.Preview.Scene.Objects.Length==2,"Replacement accumulated candidates/touched accepted object.");
        foreach(var stage in new[]{"before","Preparing image relief","Sampling image relief","Building image relief","Validating image relief","Ready to preview image relief"})
        {
            using var cts=new CancellationTokenSource();if(stage=="before")cts.Cancel();
            var r=await accepted.PreviewImageReliefAsync(request,held.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage==stage&&(p.Stage is not("Sampling image relief" or "Building image relief")||p.Completed>0))cts.Cancel();}));
            Check(r.Outcome==Outcome.Cancelled,"Cancellation failed at "+stage+": "+r);Same(accepted,held);
        }
        foreach(var stale in new[]{initial.Preview.Id,Guid.NewGuid()})Check((await accepted.PreviewImageReliefAsync(request,stale)).Outcome==Outcome.Conflict,"Stale preview token accepted.");
        Check((await accepted.PreviewImageReliefAsync(request,held.Preview.Id,expectedRevision:baseState.Revision-1)).Outcome==Outcome.Conflict,"Stale revision accepted.");
        Check((await accepted.PreviewAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,2,2)))).Outcome==Outcome.Conflict,"Foreign preview replaced image relief.");
        Check(accepted.Execute(new AddCube()).Outcome==Outcome.Conflict&&accepted.Select(null).Outcome==Outcome.Conflict&&accepted.Undo().Outcome==Outcome.Conflict&&
            (await accepted.SaveAsync(Path.Combine(output,"must-not-save.3mf"))).Outcome==Outcome.Conflict,"Preview guard failed.");Same(accepted,held);
        Check(accepted.DiscardPreview(held.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(accepted,baseState);
        Ok(await accepted.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,2,2))));var foreign=accepted.Snapshot;
        Check((await accepted.PreviewImageReliefAsync(request,foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Image replaced foreign geometry preview.");Same(accepted,foreign);accepted.DiscardPreview(foreign.Preview.Id);
        Pass("Preview replacements derive from accepted scene with stable candidate identity/fresh token; cancellation at every bounded generation/publication stage and stale/foreign/history/file/selection guards preserve prior preview and full saved/redo state");

        var invalid=New();var invalidBefore=invalid.Snapshot;
        foreach(var bad in new[]{request with {WidthMillimeters=double.NaN},request with {WidthMillimeters=.01},request with {WidthMillimeters=1001},request with {ReliefDepthMillimeters=-1},
            request with {ReliefDepthMillimeters=double.PositiveInfinity},request with {ReliefDepthMillimeters=101},request with {BaseThicknessMillimeters=0},request with {BaseThicknessMillimeters=101},
            request with {Samples=1},request with {Samples=129},Request(new(0,1,[])),Request(new(1,1,default)),Request(new(1,1,[0,0,0])),Request(Pixels("oversized")),
            Request(new(2,2049,[])),Request(new(int.MaxValue,int.MaxValue,[])),Request(new(1,2048,Enumerable.Repeat((byte)255,8192).ToImmutableArray()),width:1)})
        {Check((await invalid.PreviewImageReliefAsync(bad)).Outcome==Outcome.Invalid,"Invalid size/raster/detail accepted.");Same(invalid,invalidBefore);}
        var budget=New();for(var i=0;i<45;i++)Ok(budget.Execute(new AddPrimitive(PrimitiveKind.Sphere,new(2,2,2))));
        Ok(await budget.PreviewImageReliefAsync(Request(black,samples:2)));var budgetHeld=budget.Snapshot;var sampled=false;
        Check((await budget.PreviewImageReliefAsync(Request(black,samples:128),budgetHeld.Preview!.Id,progress:new Progress(p=>{if(p.Stage=="Sampling image relief")sampled=true;}))).Outcome==Outcome.Invalid,"Combined scene triangle budget exceeded.");
        Check(!sampled,"Scene-budget check ran after relief mesh allocation/sampling.");Same(budget,budgetHeld);
        var full=New();for(var i=0;i<ModelLimits.MaxObjects;i++)Ok(full.Execute(new AddCube()));var fullBefore=full.Snapshot;
        Check((await full.PreviewImageReliefAsync(Request(black,samples:2))).Outcome==Outcome.Invalid,"Scene object budget exceeded.");Same(full,fullBefore);
        Pass("Finite physical/aspect bounds, RGBA shape/dimension/overflow and detail caps reject clearly; combined scene triangle/object budgets reject before mesh generation and preserve held candidate");

        var decoder=new Decoder(ramp);var fileRequest=new ImageReliefRequest(new ImageFileSource(Path.Combine(input,"ramp.png")),45,3,1,9);
        var decoded=New(decoder);Ok(await decoded.PreviewImageReliefAsync(fileRequest));var decodeHeld=decoded.Snapshot;
        Geometry(decodeHeld.Preview!.Scene.Objects.Single(),ramp,fileRequest,9,5);Check(decoder.LastPath==Path.GetFullPath(Path.Combine(input,"ramp.png")),"Decoder did not receive resolved file path.");
        foreach(var failure in new[]{"cancel","fail","invalid"})
        {
            decoder.Failure=failure;var r=await decoded.PreviewImageReliefAsync(fileRequest,decodeHeld.Preview.Id);
            Check(r.Outcome==(failure=="cancel"?Outcome.Cancelled:failure=="invalid"?Outcome.Invalid:Outcome.Failed),"Decoder failure classified incorrectly.");Same(decoded,decodeHeld);
        }
        decoder.Failure=null;
        using(var cts=new CancellationTokenSource())
        {var r=await decoded.PreviewImageReliefAsync(fileRequest,decodeHeld.Preview.Id,cts.Token,new Progress(p=>{if(p.Stage=="Decoding image")cts.Cancel();}));Check(r.Outcome==Outcome.Cancelled,"Predecode cancellation failed.");Same(decoded,decodeHeld);}
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cts=new CancellationTokenSource())
        {
            decoder.Entered=entered;decoder.Release=release;var job=decoded.PreviewImageReliefAsync(fileRequest,decodeHeld.Preview.Id,cts.Token);
            Check(entered.Wait(10000),"Decoder did not enter busy barrier.");
            Check(decoded.Execute(new AddCube()).Outcome==Outcome.Busy&&decoded.ApplyPreview(decodeHeld.Preview.Id).Outcome==Outcome.Busy&&decoded.Select(null).Outcome==Outcome.Busy&&
                (await decoded.PreviewImageReliefAsync(request,decodeHeld.Preview.Id)).Outcome==Outcome.Busy,"Busy guard failed.");
            cts.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Decoder cancellation failed.");Same(decoded,decodeHeld);decoder.Entered=null;decoder.Release=null;
        }
        var unsupported=New();var unsupportedBefore=unsupported.Snapshot;Check((await unsupported.PreviewImageReliefAsync(fileRequest)).Outcome==Outcome.Invalid,"No-decoder file request accepted.");Same(unsupported,unsupportedBefore);
        var missing=New(new Decoder(black));var missingBefore=missing.Snapshot;Check((await missing.PreviewImageReliefAsync(fileRequest with {Source=new ImageFileSource(Path.Combine(output,"not-found.png"))})).Outcome==Outcome.Failed,"Missing file accepted.");Same(missing,missingBefore);
        Pass("Injected asynchronous decoder receives resolved path; explicit pixels need no decoder; decode cancellation, codec error, malformed pixels and missing-provider/file preserve state; decoder busy lease blocks competing mutations");

        var grouped=New();Ok(await grouped.OpenAsync(Path.Combine(root,"fixtures/generated/assembly-mm.3mf")));var groupIds=grouped.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();
        Ok(await grouped.PreviewArrangementAsync(new GroupRequest(groupIds,"Relief neighbors")));Ok(grouped.ApplyPreview(grouped.Snapshot.Preview!.Id));Ok(grouped.SelectMany(groupIds,groupIds[1]));
        Ok(await grouped.SaveAsync(Path.Combine(output,"group-baseline.3mf")));var groupBefore=grouped.Snapshot;
        Ok(await grouped.PreviewImageReliefAsync(Request(white,samples:5)));var gp=grouped.Snapshot.Preview!;
        Check(gp.Scene.Groups.SequenceEqual(groupBefore.Scene.Groups)&&groupBefore.Scene.Objects.Select((o,i)=>ReferenceEquals(o,gp.Scene.Objects[i])).All(x=>x),"Relief changed unrelated group/object references.");
        Ok(grouped.ApplyPreview(gp.Id));Check(grouped.Snapshot.SelectedObjectIds.SequenceEqual(gp.ResultObjectIds)&&grouped.Snapshot.FilePath==groupBefore.FilePath&&grouped.Snapshot.IsDirty,"Relief selection/path/dirty acceptance failed.");
        Ok(grouped.Undo());Check(ReferenceEquals(grouped.Snapshot.Scene,groupBefore.Scene)&&grouped.Snapshot.SelectedObjectIds.SequenceEqual(groupBefore.SelectedObjectIds)&&grouped.Snapshot.SelectedObjectId==groupBefore.SelectedObjectId&&!grouped.Snapshot.IsDirty,"Undo lost full selection or save boundary.");
        Ok(grouped.Redo());Ok(grouped.Undo());Ok(grouped.Execute(new RenameGroup(groupBefore.Scene.Groups.Single().Id,"Branched")));Check(!grouped.Snapshot.CanRedo,"Branch retained relief redo.");
        Ok(grouped.EnterGroup(grouped.Snapshot.Scene.Groups.Single().Id,groupIds[0]));var scoped=grouped.Snapshot;
        Check((await grouped.PreviewImageReliefAsync(Request(black) with {MemberGroupId=scoped.EditingGroupId})).Outcome==Outcome.Conflict,"Member-scope creation accepted.");Same(grouped,scoped);
        Ok(grouped.ExitGroup());var exited=grouped.Snapshot;Check((await grouped.PreviewImageReliefAsync(Request(black) with {MemberGroupId=scoped.EditingGroupId})).Outcome==Outcome.Conflict,"Stale member authority accepted.");Same(grouped,exited);
        Pass("Image addition preserves unrelated groups/immutable objects; Apply selects only new relief, Undo restores complete primary/multiple selection and clean boundary, branching clears redo; active/stale member scope rejects creation");

        await SaveCase("direct-max-grid",black,Request(black,width:128,depth:0,backing:.1,samples:128));
        var large=New();Ok(await large.PreviewImageReliefAsync(Request(black,width:128,depth:0,backing:.1,samples:128)));var lm=large.Snapshot.Preview!.Scene.Objects.Single().Mesh;
        Check(lm.Vertices.Length==32768&&lm.Triangles.Length==65532,"128-square cap mesh counts drifted.");
        Pass("Maximum128×128 grid has exactly32768 vertices/65532 outward triangles, connected genus-zero topology and minimum backing; independent files roundtrip within scene caps");

        File.WriteAllText(Path.Combine(output,"image-relief-results.json"),JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,
            targetVersion=typeof(EditorSession).Assembly.GetName().Version?.ToString(),runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new {path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},
            fixturePixels=new {path=pixelsPath,sha256=Hash(pixelsPath)},fixtureManifest=new {path=Path.Combine(input,"manifest.json"),sha256=Hash(Path.Combine(input,"manifest.json"))},
            method="Top-down RGBA scalar=(.2126R+.7152G+.0722B)/255, invert before alpha; endpoint source-center bilinear interpolation, +Y source top, LL-to-UR grid diagonal. Volume is integral of planar triangles, not continuous-image volume.",
            outputs=files,limits=new {pixelsPerAxis=2048,pixels=4194304,rgbaBytesPerPixel=4,samples="2..128",widthHeightMm="0.1..1000",backingMm="0.1..100",reliefMm="0..100",sceneTriangles=ModelLimits.MaxTriangles,sceneObjects=ModelLimits.MaxObjects},
            limitations="Core direct-pixel and controlled decoder seam proof only. Windows PNG/JPEG/BMP decoding and real UI paths are separately exercised; no contour/stamp/edge/texture/curved attachment or original-app algorithm parity claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} image-relief scenario groups passed. Evidence: {output}");
    }
    private sealed class Decoder(RasterImage image):IRasterImageDecoder
    {
        public string? Failure,LastPath;public ManualResetEventSlim? Entered,Release;
        public Task<RasterImage> DecodeAsync(string path,CancellationToken cancellation=default)
        {
            LastPath=path;Entered?.Set();if(Release is {} release)Check(release.Wait(10000),"Decode barrier timeout.");cancellation.ThrowIfCancellationRequested();
            if(Failure=="cancel")throw new OperationCanceledException();if(Failure=="fail")throw new IOException("controlled codec failure");
            return Task.FromResult(Failure=="invalid"?image with {Rgba=[]}:image);
        }
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private static (int X,int Y) Grid(RasterImage image,int samples)
    {
        var shortest=Math.Max(2,(int)Math.Round(samples*(double)Math.Min(image.Width,image.Height)/Math.Max(image.Width,image.Height),MidpointRounding.AwayFromZero));
        return image.Width>=image.Height?(samples,shortest):(shortest,samples);
    }
    private static double ExpectedScalar(RasterImage image,double x,double y,bool invert)
    {
        double At(int sx,int sy){var k=4*(sy*image.Width+sx);var p=image.Rgba;var l=(.2126*p[k]+.7152*p[k+1]+.0722*p[k+2])/255;return(invert?1-l:l)*p[k+3]/255;}
        var ix=(int)Math.Floor(x);var iy=(int)Math.Floor(y);var xw=x-ix;var yw=y-iy;var sum=0d;
        for(var j=0;j<2;j++)for(var i=0;i<2;i++)sum+=At(Math.Min(ix+i,image.Width-1),Math.Min(iy+j,image.Height-1))*(i==0?1-xw:xw)*(j==0?1-yw:yw);
        return sum;
    }
    private static void Geometry(SceneObject obj,RasterImage image,ImageReliefRequest request,int gx,int gy)
    {
        var mesh=obj.Mesh;Check(obj.Transform==Affine.Identity&&mesh.Vertices.Length==2*gx*gy&&mesh.Triangles.Length==4*(gx*gy-1),"Relief mesh counts/transform mismatch.");
        for(var y=0;y<gy;y++)for(var x=0;x<gx;x++)
        {
            var expected=request.BaseThicknessMillimeters+request.ReliefDepthMillimeters*ExpectedScalar(image,x*(image.Width-1d)/(gx-1),(gy-1-y)*(image.Height-1d)/(gy-1),request.Invert);
            var v=mesh.Vertices[y*gx+x];Near(v.Z,expected,1e-10);Near(v.X,request.WidthMillimeters*x/(gx-1));Near(v.Y,request.WidthMillimeters*image.Height/image.Width*y/(gy-1));
            Check(mesh.Vertices[gx*gy+y*gx+x]==new Vec3(v.X,v.Y,0),"Bottom grid did not match top XY.");
        }
        Near(Volume(obj),Integral(obj,gx,gy),1e-5);Closed(obj);
    }
    private static double Integral(SceneObject obj,int gx,int gy)
    {
        var mesh=obj.Mesh;var cellArea=obj.Bounds.Size.X*obj.Bounds.Size.Y/((gx-1)*(gy-1));var v=0d;
        for(var y=0;y<gy-1;y++)for(var x=0;x<gx-1;x++)
        {var a=y*gx+x;v+=cellArea*(2*mesh.Vertices[a].Z+mesh.Vertices[a+1].Z+mesh.Vertices[a+gx].Z+2*mesh.Vertices[a+gx+1].Z)/6;}
        return v;
    }
    private static void Closed(SceneObject obj)
    {
        var mesh=obj.Mesh;var unique=new Dictionary<Vec3,int>();var map=mesh.Vertices.Select(obj.Transform.Apply).Select(p=>{if(!unique.TryGetValue(p,out var id)){id=unique.Count;unique.Add(p,id);}return id;}).ToArray();
        var edges=new Dictionary<(int,int),int>();var adjacency=new Dictionary<int,HashSet<int>>();
        foreach(var t in mesh.Triangles)
        {
            Check(Vec3.Cross(mesh.Vertices[t.B]-mesh.Vertices[t.A],mesh.Vertices[t.C]-mesh.Vertices[t.A])!=new Vec3(),"Degenerate relief face.");
            foreach(var e in new[]{(map[t.A],map[t.B]),(map[t.B],map[t.C]),(map[t.C],map[t.A])}){edges[e]=edges.GetValueOrDefault(e)+1;if(!adjacency.TryGetValue(e.Item1,out var n))adjacency[e.Item1]=n=[];n.Add(e.Item2);}
        }
        Check(edges.All(e=>e.Value==1&&edges.GetValueOrDefault((e.Key.Item2,e.Key.Item1))==1),"Relief not closed/consistently outward directed.");
        var visited=new HashSet<int>();var stack=new Stack<int>();stack.Push(adjacency.Keys.First());while(stack.TryPop(out var v))if(visited.Add(v))foreach(var n in adjacency[v])stack.Push(n);
        Check(visited.Count==adjacency.Count&&unique.Count-edges.Count/2+mesh.Triangles.Length==2&&Volume(obj)>0,"Relief not one positive genus-zero solid.");
    }
    private static double Volume(SceneObject obj)
    {
        var origin=obj.Transform.Apply(obj.Mesh.Vertices[0]);return obj.Mesh.Triangles.Sum(t=>{var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A])-origin;var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B])-origin;var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C])-origin;var cross=Vec3.Cross(b,c);return(a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z)/6;});
    }
    private static string Hash(string path)=>HashBytes(File.ReadAllBytes(path));
    private static string HashBytes(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Near(double actual,double expected,double tolerance=1e-7)=>Check(Math.Abs(actual-expected)<=tolerance,$"Expected {expected}, got {actual}.");
    private static void VecNear(Vec3 a,Vec3 b,double tolerance){Near(a.X,b.X,tolerance);Near(a.Y,b.Y,tolerance);Near(a.Z,b.Z,tolerance);}
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Failure/cancellation changed accepted scene/path/selection/scope/history/preview.");
    private static void Accepted(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot with {Preview=null}==before,"Relief preview mutated accepted state.");
}
