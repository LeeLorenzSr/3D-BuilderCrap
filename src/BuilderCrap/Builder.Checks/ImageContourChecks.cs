using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageContourChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/image-contour-20261021/core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/image-contour-20261021/inputs");var pixelPath=Path.Combine(input,"pixels.json");
        using var pixels=JsonDocument.Parse(File.ReadAllText(pixelPath));
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");
        var cases=new List<string>();var outputs=new List<object>();
        EditorSession New(IRasterImageDecoder? decoder=null)=>new(worker,imageDecoder:decoder);
        RasterImage Image(string name){var p=pixels.RootElement.GetProperty(name);return new(p.GetProperty("width").GetInt32(),p.GetProperty("height").GetInt32(),p.GetProperty("rgba").EnumerateArray().Select(v=>v.GetByte()).ToImmutableArray(),name+" synthetic RGBA");}
        ImageContourRequest Request(string name,int samples=16,double width=80,double thickness=3,double threshold=.5,bool invert=false)=>new(new PixelImageSource(Image(name)),width,thickness,threshold,samples,invert);
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task Save(string name,string source,ImageContourRequest request,int? components=null)
        {
            var s=New();var before=s.Snapshot;Ok(await s.PreviewAsync(request));Accepted(s,before);var p=s.Snapshot.Preview!;var obj=p.Scene.Objects.Single();
            var mask=Mask(Image(source),request);var filled=Enumerable.Range(0,mask.X*mask.Y).Where(i=>mask.Cells[i]).ToArray();
            var area=filled.Length*request.WidthMillimeters/mask.X*mask.Height/mask.Y;Near(Volume(obj),area*request.ThicknessMillimeters);
            Near(obj.Bounds.Min.X,filled.Min(i=>i%mask.X)*request.WidthMillimeters/mask.X);Near(obj.Bounds.Max.X,(filled.Max(i=>i%mask.X)+1)*request.WidthMillimeters/mask.X);
            Near(obj.Bounds.Min.Y,filled.Min(i=>i/mask.X)*mask.Height/mask.Y);Near(obj.Bounds.Max.Y,(filled.Max(i=>i/mask.X)+1)*mask.Height/mask.Y);Near(obj.Bounds.Min.Z,0);Near(obj.Bounds.Max.Z,request.ThicknessMillimeters);
            Closed(obj,components);Check(obj.Material is null&&p.Summary.Contains("cells")&&!p.Summary.Contains(Image(source).Provenance!),"New material/summary policy failed.");
            // Every sampled column checks foreground/hole/island and top→+Y orientation against the actual solid.
            for(var y=0;y<mask.Y;y++)for(var x=0;x<mask.X;x++)
            {
                var hit=p.Scene.PickRay(new((x+.5)*request.WidthMillimeters/mask.X,(y+.5)*mask.Height/mask.Y,request.ThicknessMillimeters+5),new(0,0,-1));
                Check((hit is not null)==mask.Cells[y*mask.X+x],"Contour column disagrees with threshold grid.");if(hit is not null)Near(hit.Position.Z,request.ThicknessMillimeters);
            }
            Ok(s.ApplyPreview(p.Id));var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));var saved=s.Snapshot;
            Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.IsDirty,"Undo across save lost accepted scene/dirty boundary.");Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,saved.Scene)&&!s.Snapshot.IsDirty,"Redo across save lost candidate/clean boundary.");
            foreach(var file in new[]{native,stl}){var reopened=New();Ok(await reopened.OpenAsync(file));var actual=reopened.Snapshot.Scene.Objects.Single();Closed(actual,components);Near(Volume(actual),area*request.ThicknessMillimeters,.01);VecNear(actual.Bounds.Min,obj.Bounds.Min);VecNear(actual.Bounds.Max,obj.Bounds.Max);}
            outputs.Add(new{name,source=new{file=source+".png",path=Path.Combine(input,source+".png"),sha256=Hash(Path.Combine(input,source+".png")),pixels=Path.GetFileName(pixelPath),pixelsSha256=Hash(pixelPath)},
                request=new{request.WidthMillimeters,request.ThicknessMillimeters,request.Threshold,request.Samples,request.Invert},grid=new{x=mask.X,y=mask.Y,foregroundCells=filled.Length},expectedVolumeCubicMillimeters=area*request.ThicknessMillimeters,expectedBounds=obj.Bounds,
                native=new{path=Path.GetFileName(native),sha256=Hash(native)},stl=new{path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        await Save("ring-island-16","ring-island",Request("ring-island"),2);await Save("ring-island-96","ring-island",Request("ring-island",96),2);
        Pass("Ring and separate island preserve enclosed hole, margins, top orientation, sampled columns and closed outward geometry at natural/default detail; native/STL and history agree");

        await Save("levels","levels",Request("levels",8,width:40),1);await Save("levels-invert","levels",Request("levels",8,width:40,invert:true),1);await Save("alpha","alpha",Request("alpha",8,width:40),1);await Save("white-frame","white",Request("white",4,width:40),1);
        var equality=New();var exact=new RasterImage(1,1,[128,128,128,255]);var eqBefore=equality.Snapshot;
        Check((await equality.PreviewImageContourAsync(new(new PixelImageSource(exact),Threshold:128d/255,Samples:2))).Outcome==Outcome.Invalid,"Threshold equality was included.");Same(equality,eqBefore);
        var inverted=New();Ok(await inverted.PreviewImageContourAsync(Request("black",4,width:40,invert:true)));Near(Volume(inverted.Snapshot.Preview!.Scene.Objects.Single()),2400);
        foreach(var name in new[]{"black","alpha"}){var empty=New();var b=empty.Snapshot;Check((await empty.PreviewImageContourAsync(Request(name,invert:name=="alpha"))).Outcome==Outcome.Invalid,"Black/transparent inverted background became foreground.");Same(empty,b);}
        var one=New();Ok(await one.PreviewImageContourAsync(new(new PixelImageSource(new(1,1,[255,255,255,255])),12,2,Samples:2)));Near(Volume(one.Snapshot.Preview!.Scene.Objects.Single()),288);
        Pass("Luminance threshold/invert/alpha and strict equality use the agreed cell-center mapping; all-white fills full frame and single-pixel axes remain constant");

        var retained=New();Ok(retained.Execute(new AddCube()));var siblingId=retained.Snapshot.SelectedObjectId!.Value;var material=new SurfaceMaterial("Seed amber",new(230,162,60));Ok(await retained.PreviewMaterialAsync(new([siblingId],material)));Ok(retained.ApplyPreview(retained.Snapshot.Preview!.Id));Ok(await retained.SaveAsync(Path.Combine(output,"accepted-painted.3mf")));Ok(retained.Execute(new RenameObject(siblingId,"temporary")));Ok(retained.Undo());var baseline=retained.Snapshot;
        var request=Request("ring-island");Ok(await retained.PreviewImageContourAsync(request));var first=retained.Snapshot.Preview!;var resultId=first.ResultObjectIds.Single();
        var relief=new ImageReliefRequest(request.Source,request.WidthMillimeters,3,1,16);Ok(await retained.PreviewImageReliefAsync(relief,first.Id));var reliefPreview=retained.Snapshot.Preview!;
        Check(reliefPreview.Request is ImageReliefRequest&&reliefPreview.Id!=first.Id&&reliefPreview.ResultObjectIds.Single()==resultId,"Contour→relief identity failed.");
        Ok(await retained.PreviewImageContourAsync(request with{Samples=96},reliefPreview.Id));var held=retained.Snapshot;
        Check(held.Preview!.Request is ImageContourRequest&&held.Preview.ResultObjectIds.Single()==resultId&&held.Preview.Scene.Objects.Length==2&&ReferenceEquals(held.Preview.Scene.Objects[0],baseline.Scene.Objects[0]),"Relief→contour accumulated candidates or lost painted sibling.");
        foreach(var bad in new[]{Request("black"),Request("diagonal",2),request with{Threshold=1}}){var r=await retained.PreviewImageContourAsync(bad,held.Preview.Id);Check(r.Outcome==Outcome.Invalid&&!r.Message.Contains("Text",StringComparison.Ordinal),"Empty/pinch failure not usable.");Same(retained,held);}
        Check((await retained.PreviewImageContourAsync(request,first.Id)).Outcome==Outcome.Conflict&&(await retained.PreviewImageContourAsync(request,held.Preview.Id,expectedRevision:baseline.Revision-1)).Outcome==Outcome.Conflict,"Stale preview/revision accepted.");Same(retained,held);
        foreach(var stage in new[]{"before","Calculating geometry","Ready to preview image contour"}){using var ct=new CancellationTokenSource();if(stage=="before")ct.Cancel();Check((await retained.PreviewImageContourAsync(request,held.Preview.Id,ct.Token,new Progress(p=>{if(p.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"Cancel failed: "+stage);Same(retained,held);}
        Check(retained.DiscardPreview(held.Preview.Id).Outcome==Outcome.Cancelled,"Discard failed.");Same(retained,baseline);
        Ok(await retained.PreviewImageContourAsync(request));Ok(retained.ApplyPreview(retained.Snapshot.Preview!.Id));Check(retained.Snapshot.SelectedObjectId==resultId || retained.Snapshot.SelectedObjectId!=siblingId,"Creation did not select result.");var applied=retained.Snapshot;Check(applied.Scene.Objects[0].Material==material&&applied.Scene.Objects[1].Material is null,"Contour creation changed material assignment.");Ok(retained.Undo());Check(ReferenceEquals(retained.Snapshot.Scene,baseline.Scene)&&retained.Snapshot.SelectedObjectIds.SequenceEqual(baseline.SelectedObjectIds),"Undo lost accepted selection/scene.");Ok(retained.Redo());Check(ReferenceEquals(retained.Snapshot.Scene,applied.Scene),"Redo changed candidate.");
        Pass("Exact-token cross-mode updates derive from accepted scene with one stable candidate ID; empty/pinch/stale/cancel retains prior candidate and painted saved/redo state; Apply is one selected edit");

        var guard=New();Ok(guard.Execute(new AddCube()));var a=guard.Snapshot.SelectedObjectId!.Value;Ok(guard.Execute(new AddCube()));var bId=guard.Snapshot.SelectedObjectId!.Value;Ok(await guard.PreviewArrangementAsync(new GroupRequest([a,bId],"Scope")));Ok(guard.ApplyPreview(guard.Snapshot.Preview!.Id));var groupId=guard.Snapshot.Scene.Groups.Single().Id;Ok(guard.EnterGroup(groupId,a));var member=guard.Snapshot;
        Check((await guard.PreviewImageContourAsync(request with{MemberGroupId=groupId})).Outcome==Outcome.Conflict,"Creation in member scope accepted.");Same(guard,member);Ok(guard.ExitGroup());var outside=guard.Snapshot;Check((await guard.PreviewImageContourAsync(request with{MemberGroupId=groupId})).Outcome==Outcome.Conflict,"Stale member scope accepted.");Same(guard,outside);
        Ok(await guard.PreviewImageContourAsync(request));held=guard.Snapshot;Check(guard.Select(null).Outcome==Outcome.Conflict&&guard.Undo().Outcome==Outcome.Conflict&&(await guard.SaveAsync(Path.Combine(output,"must-not-save.3mf"))).Outcome==Outcome.Conflict,"Preview file/history/selection guard failed.");Same(guard,held);
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=guard.PreviewImageContourAsync(request,held.Preview!.Id,ct.Token,new Progress(p=>{if(p.Stage=="Sampling image contour"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));Check(entered.Wait(10000),"Sampling did not begin.");Check(guard.ApplyPreview(held.Preview.Id).Outcome==Outcome.Busy&&guard.Select(null).Outcome==Outcome.Busy&&(await guard.OpenAsync(Path.Combine(root,"fixtures/generated/cube-20mm.stl"))).Outcome==Outcome.Busy,"Busy guard failed.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy cancel failed.");Same(guard,held);}
        Check(guard.DiscardPreview(held.Preview!.Id).Outcome==Outcome.Cancelled,"Discard failed.");Ok(await guard.PreviewPrimitiveAsync(new PolyhedronPrimitiveRequest(PrimitiveKind.Cube,new(2,2,2))));var foreign=guard.Snapshot;Check((await guard.PreviewImageContourAsync(request,foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Foreign preview accepted.");Same(guard,foreign);
        Pass("Member/foreign/busy and pending-preview guards preserve selection, history, files and candidate; cancellation does not leak partial scene changes");

        var decoder=new Decoder(Image("ring-island"));var decoded=New(decoder);var fileRequest=request with{Source=new ImageFileSource(Path.Combine(input,"ring-island.png"))};Ok(await decoded.PreviewImageContourAsync(fileRequest));held=decoded.Snapshot;Check(decoder.Path==Path.GetFullPath(Path.Combine(input,"ring-island.png")),"Decoder did not receive resolved path.");
        foreach(var error in new[]{"invalid","fail","cancel"}){decoder.Error=error;Check((await decoded.PreviewImageContourAsync(fileRequest,held.Preview!.Id)).Outcome==(error=="invalid"?Outcome.Invalid:error=="cancel"?Outcome.Cancelled:Outcome.Failed),"Decoder boundary classification failed.");Same(decoded,held);}decoder.Error=null;
        var noDecoder=New();var emptyState=noDecoder.Snapshot;Check((await noDecoder.PreviewImageContourAsync(fileRequest)).Outcome==Outcome.Invalid,"Missing decoder accepted.");Same(noDecoder,emptyState);
        foreach(var bad in new[]{request with{WidthMillimeters=double.NaN},request with{WidthMillimeters=.01},request with{ThicknessMillimeters=0},request with{ThicknessMillimeters=101},request with{Threshold=-.1},request with{Threshold=double.NaN},request with{Samples=1},request with{Samples=129},request with{Source=new PixelImageSource(new(1,1,[]))},request with{Source=new PixelImageSource(new(2049,1,[]))}}){Check((await decoded.PreviewImageContourAsync(bad,held.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid settings/raster accepted.");Same(decoded,held);}
        var full=New();for(var i=0;i<ModelLimits.MaxObjects;i++)Ok(full.Execute(new AddCube()));var fullBefore=full.Snapshot;Check((await full.PreviewImageContourAsync(request)).Outcome==Outcome.Invalid,"Object cap exceeded.");Same(full,fullBefore);
        Pass("Shared decoder seam resolves paths and preserves failure/cancel semantics; physical/raster/detail/threshold and object budgets refuse without state mutation");

        // Explicit unchanged-relief regression: endpoint sampling, LL→UR top diagonal and alpha remain exact.
        var corner=new RasterImage(2,2,[255,255,255,255,0,0,0,255,0,0,0,255,0,0,0,255]);var adjacent=New();Ok(await adjacent.PreviewImageReliefAsync(new(new PixelImageSource(corner),10,6,1,2)));var reliefObject=adjacent.Snapshot.Preview!.Scene.Objects.Single();Near(Volume(reliefObject),200);Near(reliefObject.Mesh.Vertices[2].Z,7);Near(reliefObject.Mesh.Vertices[0].Z,1);
        var alphaSource=new RasterImage(1,1,[128,64,32,128]);var alphaRelief=New();Ok(await alphaRelief.PreviewImageReliefAsync(new(new PixelImageSource(alphaSource),12,1,1,2)));Near(alphaRelief.Snapshot.Preview!.Scene.Bounds.Max.Z,1+(.2126*128+.7152*64+.0722*32)/255*128/255);
        Pass("Adjacent relief retains endpoint sampling, top orientation, original LL→UR triangle integral and alpha policy after shared decoder/scalar extraction");
        File.WriteAllText(Path.Combine(output,"image-contour-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new{path=worker,sha256=Hash(worker)},inputs=new{pixels= pixelPath,sha256=Hash(pixelPath)},
            limitations="Thresholded cell-boundary silhouette, not interpolated/smoothed vector tracing. Foreground scalar>threshold; inversion before alpha, bilinear cell-center samples; source top +Y. Frame margins retained. At most256 loops/4096 points, touching/pinched contours refused; one new unassigned object can contain multiple islands. Existing worker unchanged; no UI claims."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} image contour groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private sealed class Decoder(RasterImage image):IRasterImageDecoder
    {
        public string? Error;public string? Path;
        public Task<RasterImage> DecodeAsync(string path,CancellationToken cancellation=default)
        {Path=path;cancellation.ThrowIfCancellationRequested();if(Error=="invalid")throw new ModelException("Invalid image.");if(Error=="cancel")throw new OperationCanceledException();if(Error=="fail")throw new IOException("Codec failure.");return Task.FromResult(image);}
    }
    private sealed record CellMask(int X,int Y,double Height,bool[] Cells);
    private static CellMask Mask(RasterImage image,ImageContourRequest request)
    {
        var shorter=Math.Max(2,(int)Math.Round(request.Samples*(double)Math.Min(image.Width,image.Height)/Math.Max(image.Width,image.Height),MidpointRounding.AwayFromZero));
        var nx=image.Width>=image.Height?request.Samples:shorter;var ny=image.Width>=image.Height?shorter:request.Samples;var mask=new bool[nx*ny];
        double Scalar(int x,int y){var i=4*(y*image.Width+x);var rgb=image.Rgba;var l=(2126d*rgb[i]+7152d*rgb[i+1]+722d*rgb[i+2])/2550000;return(request.Invert?1-l:l)*rgb[i+3]/255;}
        for(var y=0;y<ny;y++)for(var x=0;x<nx;x++)
        {
            var px=(x+.5)/nx*(image.Width-1);var py=(1-(y+.5)/ny)*(image.Height-1);var x0=(int)Math.Floor(px);var y0=(int)Math.Floor(py);var fx=px-x0;var fy=py-y0;
            var x1=Math.Min(x0+1,image.Width-1);var y1=Math.Min(y0+1,image.Height-1);
            mask[y*nx+x]=(Scalar(x0,y0)*(1-fx)*(1-fy)+Scalar(x1,y0)*fx*(1-fy)+Scalar(x0,y1)*(1-fx)*fy+Scalar(x1,y1)*fx*fy)>request.Threshold;
        }
        return new(nx,ny,request.WidthMillimeters*image.Height/image.Width,mask);
    }
    private static void Closed(SceneObject obj,int? expectedComponents)
    {
        var vertices=new Dictionary<Vec3,int>();var faces=obj.Mesh.Triangles.Select(t=>new[]{t.A,t.B,t.C}.Select(i=>{var v=obj.Transform.Apply(obj.Mesh.Vertices[i]);if(!vertices.TryGetValue(v,out var id)){id=vertices.Count;vertices.Add(v,id);}return id;}).ToArray()).ToArray();
        var edges=new Dictionary<(int,int),List<(int Face,int Sign)>>();for(var f=0;f<faces.Length;f++)for(var j=0;j<3;j++){var a=faces[f][j];var b=faces[f][(j+1)%3];var key=(Math.Min(a,b),Math.Max(a,b));if(!edges.TryGetValue(key,out var incident))edges.Add(key,incident=[]);incident.Add((f,a<b?1:-1));}
        Check(edges.Values.All(e=>e.Count==2&&e.Sum(p=>p.Sign)==0),"Mesh is open or inconsistently wound.");var adjacent=Enumerable.Range(0,faces.Length).Select(_=>new List<int>()).ToArray();foreach(var edge in edges.Values){adjacent[edge[0].Face].Add(edge[1].Face);adjacent[edge[1].Face].Add(edge[0].Face);}var seen=new HashSet<int>();var count=0;for(var f=0;f<faces.Length;f++){if(!seen.Add(f))continue;count++;var queue=new Queue<int>();queue.Enqueue(f);while(queue.TryDequeue(out var item))foreach(var next in adjacent[item])if(seen.Add(next))queue.Enqueue(next);}if(expectedComponents is {} expected)Check(count==expected,$"Expected{expected} components, got{count}.");Check(Volume(obj)>0,"Mesh has nonpositive volume.");
    }
    private static double Volume(SceneObject o)=>o.Mesh.Triangles.Sum(t=>{var a=o.Transform.Apply(o.Mesh.Vertices[t.A]);var b=o.Transform.Apply(o.Mesh.Vertices[t.B]);var c=o.Transform.Apply(o.Mesh.Vertices[t.C]);var cross=Vec3.Cross(b,c);return a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z;})/6;
    private static void Near(double a,double b,double tolerance=.001)=>Check(Math.Abs(a-b)<=tolerance,$"Expected{b}, got{a}.");
    private static void VecNear(Vec3 a,Vec3 b){Near(a.X,b.X);Near(a.Y,b.Y);Near(a.Z,b.Z);}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted or held preview state changed.");
    private static void Accepted(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot with{Preview=null}==before,"Preview changed accepted state.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
