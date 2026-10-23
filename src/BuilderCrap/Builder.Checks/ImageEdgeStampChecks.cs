using Builder.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageEdgeStampChecks
{
    public static async Task Run(string root,string? evidenceRoot=null)
    {
        var output=evidenceRoot??Path.Combine(root,".local/evidence/image-edge-stamp-20261021/core");Directory.CreateDirectory(output);
        var input=Path.Combine(root,".local/evidence/image-contour-20261021/inputs");var pixelPath=Path.Combine(input,"pixels.json");using var source=JsonDocument.Parse(File.ReadAllText(pixelPath));
        var worker=Path.Combine(root,"src/BuilderCrap/Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe");var cases=new List<string>();var outputs=new List<object>();
        RasterImage Pixels(string name){var p=source.RootElement.GetProperty(name);return new(p.GetProperty("width").GetInt32(),p.GetProperty("height").GetInt32(),p.GetProperty("rgba").EnumerateArray().Select(v=>v.GetByte()).ToImmutableArray(),name);}
        EditorSession New(IRasterImageDecoder? decoder=null)=>new(worker,imageDecoder:decoder);
        ReliefSource Source(string name)=>new PixelImageSource(Pixels(name));
        void Pass(string name){cases.Add(name);Console.WriteLine("PASS "+name);}
        async Task Save(string name,string fixture,GeometryRequest request)
        {
            var s=New();var before=s.Snapshot;Ok(await s.PreviewAsync(request));Accepted(s,before);var p=s.Snapshot.Preview!;var obj=p.Scene.Objects.Single();var mask=Mask(Pixels(fixture),request);var stamp=request as ImageStampRequest;
            var baseHeight=stamp?.BaseThicknessMillimeters??0;var height=stamp?.RaisedHeightMillimeters??((ImageEdgeRequest)request).ThicknessMillimeters;var area=mask.Cells.Count(b=>b)*mask.Width/mask.X*mask.Height/mask.Y;
            var volume=mask.Width*mask.Height*baseHeight+area*height;Near(Volume(obj),volume);Closed(obj);Check(obj.Material is null,"New image unexpectedly assigned material.");Near(obj.Bounds.Min.Z,0);Near(obj.Bounds.Max.Z,baseHeight+height);
            if(stamp is not null){Near(obj.Bounds.Min.X,0);Near(obj.Bounds.Min.Y,0);Near(obj.Bounds.Max.X,mask.Width);Near(obj.Bounds.Max.Y,mask.Height);}
            for(var y=0;y<mask.Y;y++)for(var x=0;x<mask.X;x++)
            {
                var hit=p.Scene.PickRay(new((x+.5)*mask.Width/mask.X,(y+.5)*mask.Height/mask.Y,baseHeight+height+5),new(0,0,-1));
                if(stamp is null)Check((hit is not null)==mask.Cells[y*mask.X+x],"Edge footprint differs from erosion.");else Check(hit is not null,"Stamp backing has a through hole.");
                if(hit is not null)Near(hit.Position.Z,mask.Cells[y*mask.X+x]?baseHeight+height:baseHeight);
            }
            if(request is ImageEdgeRequest e){var rx=(int)Math.Ceiling(e.StrokeWidthMillimeters/(mask.Width/mask.X));var ry=(int)Math.Ceiling(e.StrokeWidthMillimeters/(mask.Height/mask.Y));Check(p.Summary.Contains(FormattableString.Invariant($"X {rx*mask.Width/mask.X:G6}, Y {ry*mask.Height/mask.Y:G6}")),"Rounded physical stroke missing from summary.");}
            Ok(s.ApplyPreview(p.Id));var native=Path.Combine(output,name+".3mf");var stl=Path.Combine(output,name+".stl");Ok(await s.SaveAsync(native));Ok(await s.ExportStlAsync(stl));var saved=s.Snapshot;Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.IsDirty,"Undo save boundary failed.");Ok(s.Redo());Check(ReferenceEquals(s.Snapshot.Scene,saved.Scene)&&!s.Snapshot.IsDirty,"Redo saved candidate failed.");
            foreach(var path in new[]{native,stl}){var reader=New();Ok(await reader.OpenAsync(path));var read=reader.Snapshot.Scene.Objects.Single();Near(Volume(read),volume,.02);Closed(read);}
            object parameters=request is ImageEdgeRequest edge?new{edge.WidthMillimeters,edge.ThicknessMillimeters,edge.StrokeWidthMillimeters,edge.Threshold,edge.Samples,edge.Invert}:(object)new{stamp!.WidthMillimeters,stamp.RaisedHeightMillimeters,stamp.BaseThicknessMillimeters,stamp.Threshold,stamp.Samples,stamp.Invert,stamp.Mirror};
            outputs.Add(new{name,kind=request.GetType().Name,source=new{file=fixture+".png",path=Path.Combine(input,fixture+".png"),sha256=Hash(Path.Combine(input,fixture+".png")),pixels=pixelPath,pixelsSha256=Hash(pixelPath)},request=parameters,
                grid=new{x=mask.X,y=mask.Y,foregroundCells=mask.Cells.Count(b=>b)},expectedVolumeCubicMillimeters=volume,expectedBounds=obj.Bounds,
                native=new{path=Path.GetFileName(native),sha256=Hash(native)},stl=new{path=Path.GetFileName(stl),sha256=Hash(stl)}});
        }
        await Save("edge-white","white",new ImageEdgeRequest(Source("white"),40,3,2,Samples:16));
        await Save("edge-ring","ring-island",new ImageEdgeRequest(Source("ring-island"),80,3,2,Samples:96));
        var oversized=New();Ok(await oversized.PreviewImageEdgeAsync(new(Source("white"),.2,1,100,Samples:128)));Near(Volume(oversized.Snapshot.Preview!.Scene.Objects.Single()),.02);
        Pass("Inner border includes image/hole edges, reports whole-cell X/Y stroke and preserves thin features; oversized stroke remains full foreground without oversized window work; native/STL geometry agrees");

        await Save("stamp-mirrored","ring-island",new ImageStampRequest(Source("ring-island"),80,2,2,Samples:16));await Save("stamp-unmirrored","ring-island",new ImageStampRequest(Source("ring-island"),80,2,2,Samples:16,Mirror:false));await Save("stamp-alpha","alpha",new ImageStampRequest(Source("alpha"),40,2,2,Samples:8));
        Pass("Stamp mirrors X by default, keeps full frame backing under holes, joins islands and has frame-area backing plus silhouette raised volume; native/STL and history agree");

        var s=New();Ok(s.Execute(new AddCube()));var siblingId=s.Snapshot.SelectedObjectId!.Value;Ok(await s.PreviewMaterialAsync(new([siblingId],new("Seed blue",new(38,124,181)))));Ok(s.ApplyPreview(s.Snapshot.Preview!.Id));Ok(await s.SaveAsync(Path.Combine(output,"accepted-painted.3mf")));Ok(s.Execute(new RenameObject(siblingId,"temporary")));Ok(s.Undo());var before=s.Snapshot;
        var edgeRequest=new ImageEdgeRequest(Source("white"),40,3,2,Samples:16);Ok(await s.PreviewImageEdgeAsync(edgeRequest));var token=s.Snapshot.Preview!.Id;var candidateId=s.Snapshot.Preview.ResultObjectIds.Single();
        GeometryRequest[] transitions=[new ImageStampRequest(Source("ring-island"),Samples:16),new ImageContourRequest(Source("ring-island"),Samples:16),new ImageReliefRequest(Source("white"),Samples:8),edgeRequest];
        foreach(var transition in transitions)
        {
            var result=transition switch{ImageStampRequest r=>await s.PreviewImageStampAsync(r,token),ImageContourRequest r=>await s.PreviewImageContourAsync(r,token),ImageReliefRequest r=>await s.PreviewImageReliefAsync(r,token),ImageEdgeRequest r=>await s.PreviewImageEdgeAsync(r,token),_=>throw new Exception()};Ok(result);var p=s.Snapshot.Preview!;
            Check(p.Id!=token&&p.ResultObjectIds.Single()==candidateId&&p.Scene.Objects.Length==2&&ReferenceEquals(p.Scene.Objects[0],before.Scene.Objects[0]),"Cross-mode update changed identity, accepted material or accumulated candidates.");token=p.Id;
        }
        var held=s.Snapshot;Check((await s.PreviewImageStampAsync(new(Source("black")),token)).Outcome==Outcome.Invalid,"Empty stamp accepted.");Same(s,held);
        Check((await s.PreviewImageEdgeAsync(new(Source("diagonal"),Samples:2),token)).Outcome==Outcome.Invalid,"Point-touch edge accepted.");Same(s,held);
        Check((await s.PreviewImageStampAsync(new(Source("white")),Guid.NewGuid())).Outcome==Outcome.Conflict&&(await s.PreviewImageEdgeAsync(edgeRequest,token,expectedRevision:before.Revision-1)).Outcome==Outcome.Conflict,"Stale preview/revision accepted.");Same(s,held);
        foreach(var stage in new[]{"before","Building image edge","Ready to preview image edge"}){using var ct=new CancellationTokenSource();if(stage=="before")ct.Cancel();Check((await s.PreviewImageEdgeAsync(edgeRequest,token,ct.Token,new Progress(p=>{if(p.Stage==stage)ct.Cancel();}))).Outcome==Outcome.Cancelled,"Edge cancellation failed: "+stage);Same(s,held);}
        using(var ct=new CancellationTokenSource()){Check((await s.PreviewImageStampAsync(new(Source("ring-island"),Samples:16),token,ct.Token,new Progress(p=>{if(p.Stage=="Calculating geometry")ct.Cancel();}))).Outcome==Outcome.Cancelled,"Stamp worker cancellation failed.");Same(s,held);}
        Check(s.DiscardPreview(token).Outcome==Outcome.Cancelled,"Discard failed.");Same(s,before);
        Ok(await s.PreviewImageStampAsync(new(Source("ring-island"),Samples:16)));var acceptedId=s.Snapshot.Preview!.ResultObjectIds.Single();Ok(s.ApplyPreview(s.Snapshot.Preview.Id));Check(s.Snapshot.SelectedObjectId==acceptedId&&s.Snapshot.Scene.Objects[1].Material is null,"Stamp selection/default material incorrect.");Ok(s.Undo());Check(ReferenceEquals(s.Snapshot.Scene,before.Scene)&&s.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Undo lost accepted scene/selection.");Ok(s.Redo());
        Pass("All four modes share exact owned replacements with stable candidate identity; failed/stale/cancelled updates retain held preview and painted saved/redo state; acceptance is one Undo step");

        var guard=New();Ok(guard.Execute(new AddCube()));var a=guard.Snapshot.SelectedObjectId!.Value;Ok(guard.Execute(new AddCube()));var b=guard.Snapshot.SelectedObjectId!.Value;Ok(await guard.PreviewArrangementAsync(new GroupRequest([a,b],"Image scope")));Ok(guard.ApplyPreview(guard.Snapshot.Preview!.Id));var group=guard.Snapshot.Scene.Groups.Single().Id;Ok(guard.EnterGroup(group,a));var member=guard.Snapshot;
        Check((await guard.PreviewImageEdgeAsync(edgeRequest with{MemberGroupId=group})).Outcome==Outcome.Conflict&&(await guard.PreviewImageStampAsync(new(Source("white")){MemberGroupId=group})).Outcome==Outcome.Conflict,"Image creation accepted in member scope.");Same(guard,member);Ok(guard.ExitGroup());
        Ok(await guard.PreviewImageEdgeAsync(edgeRequest));held=guard.Snapshot;Check(guard.Select(null).Outcome==Outcome.Conflict&&guard.Undo().Outcome==Outcome.Conflict&&(await guard.SaveAsync(Path.Combine(output,"must-not-save.3mf"))).Outcome==Outcome.Conflict,"Preview guards failed.");Same(guard,held);
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var ct=new CancellationTokenSource())
        {var job=guard.PreviewImageStampAsync(new(Source("ring-island"),Samples:16),held.Preview!.Id,ct.Token,new Progress(p=>{if(p.Stage=="Sampling image stamp"){entered.Set();Check(release.Wait(10000),"Barrier timeout.");}}));Check(entered.Wait(10000),"Stamp did not begin sampling.");Check(guard.Select(null).Outcome==Outcome.Busy&&guard.ApplyPreview(held.Preview.Id).Outcome==Outcome.Busy,"Busy guard failed.");ct.Cancel();release.Set();Check((await job).Outcome==Outcome.Cancelled,"Busy stamp cancel failed.");Same(guard,held);}
        foreach(var bad in new[]{edgeRequest with{StrokeWidthMillimeters=0},edgeRequest with{StrokeWidthMillimeters=101},edgeRequest with{WidthMillimeters=double.NaN},edgeRequest with{Threshold=2},edgeRequest with{Samples=129}}){Check((await guard.PreviewImageEdgeAsync(bad,held.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid edge settings accepted.");Same(guard,held);}
        foreach(var bad in new[]{new ImageStampRequest(Source("white"),RaisedHeightMillimeters:0),new ImageStampRequest(Source("white"),BaseThicknessMillimeters:101),new ImageStampRequest(Source("white"),RaisedHeightMillimeters:double.PositiveInfinity)}){Check((await guard.PreviewImageStampAsync(bad,held.Preview!.Id)).Outcome==Outcome.Invalid,"Invalid stamp settings accepted.");Same(guard,held);}
        Check(guard.DiscardPreview(held.Preview!.Id).Outcome==Outcome.Cancelled,"Discard failed.");Ok(await guard.PreviewMaterialAsync(new([a],new("Held",new(1,2,3)))));var foreign=guard.Snapshot;Check((await guard.PreviewImageEdgeAsync(edgeRequest,foreign.Preview!.Id)).Outcome==Outcome.Conflict,"Image replaced foreign preview.");Same(guard,foreign);
        var decoder=new Decoder(Pixels("white"));var decoded=New(decoder);var fileRequest=edgeRequest with{Source=new ImageFileSource(Path.Combine(input,"white.png"))};Ok(await decoded.PreviewImageEdgeAsync(fileRequest));held=decoded.Snapshot;decoder.Fail=true;Check((await decoded.PreviewImageStampAsync(new(fileRequest.Source),held.Preview!.Id)).Outcome==Outcome.Failed,"Decoder failure not retained.");Same(decoded,held);
        Pass("Member/foreign/preview/busy guards, decoder failure and invalid physical/detail settings preserve accepted state and the reviewed candidate");

        var contour=New();Ok(await contour.PreviewImageContourAsync(new(Source("ring-island"),Samples:16)));Near(Volume(contour.Snapshot.Preview!.Scene.Objects.Single()),5175);Check(contour.Snapshot.Preview.Summary.Contains("2 island(s), 1 hole(s)"),"Adjacent contour topology changed.");
        var corner=new RasterImage(2,2,[255,255,255,255,0,0,0,255,0,0,0,255,0,0,0,255]);var relief=New();Ok(await relief.PreviewImageReliefAsync(new(new PixelImageSource(corner),10,6,1,2)));Near(Volume(relief.Snapshot.Preview!.Scene.Objects.Single()),200);Near(relief.Snapshot.Preview.Scene.Objects.Single().Mesh.Vertices[2].Z,7);
        Pass("Adjacent contour keeps sampled hole/island volume and relief keeps endpoint/diagonal orientation after shared transaction refactor");
        File.WriteAllText(Path.Combine(output,"image-edge-stamp-results.json"),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,scenarioCount=cases.Count,cases,outputs,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            core=new{path=typeof(EditorSession).Assembly.Location,sha256=Hash(typeof(EditorSession).Assembly.Location)},worker=new{path=worker,sha256=Hash(worker)},limitations="Original cell-boundary conversion; no original-app algorithm parity. Edge is inner rectangular erosion border with separately rounded X/Y grid stroke; thin regions stay filled. Stamp raises threshold foreground on full backing and defaults to X mirror. Existing outline and scene budgets apply; empty/point-touch outlines refuse. No worker changes or performance campaign."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{cases.Count} image Edge/Stamp groups passed. Evidence: {output}");
    }
    private sealed class Progress(Action<OperationProgress> action):IProgress<OperationProgress>{public void Report(OperationProgress p)=>action(p);}
    private sealed class Decoder(RasterImage image):IRasterImageDecoder{public bool Fail;public Task<RasterImage> DecodeAsync(string path,CancellationToken cancellation=default){cancellation.ThrowIfCancellationRequested();if(Fail)throw new IOException("Codec failure.");return Task.FromResult(image);}}
    private sealed record Grid(int X,int Y,double Width,double Height,bool[] Cells);
    private static Grid Mask(RasterImage image,GeometryRequest request)
    {
        var edge=request as ImageEdgeRequest;var stamp=request as ImageStampRequest;var width=edge?.WidthMillimeters??stamp!.WidthMillimeters;var samples=edge?.Samples??stamp!.Samples;var threshold=edge?.Threshold??stamp!.Threshold;var invert=edge?.Invert??stamp!.Invert;
        var shorter=Math.Max(2,(int)Math.Round(samples*(double)Math.Min(image.Width,image.Height)/Math.Max(image.Width,image.Height),MidpointRounding.AwayFromZero));var nx=image.Width>=image.Height?samples:shorter;var ny=image.Width>=image.Height?shorter:samples;var height=width*image.Height/image.Width;var cells=new bool[nx*ny];
        double Scalar(int x,int y){var i=4*(y*image.Width+x);var p=image.Rgba;var l=(2126d*p[i]+7152d*p[i+1]+722d*p[i+2])/2550000;return(invert?1-l:l)*p[i+3]/255;}
        for(var y=0;y<ny;y++)for(var x=0;x<nx;x++){var px=(x+.5)/nx*(image.Width-1);var py=(1-(y+.5)/ny)*(image.Height-1);var a=(int)Math.Floor(px);var b=(int)Math.Floor(py);var fx=px-a;var fy=py-b;var c=Math.Min(a+1,image.Width-1);var d=Math.Min(b+1,image.Height-1);cells[y*nx+x]=Scalar(a,b)*(1-fx)*(1-fy)+Scalar(c,b)*fx*(1-fy)+Scalar(a,d)*(1-fx)*fy+Scalar(c,d)*fx*fy>threshold;}
        if(stamp?.Mirror==true){var mirrored=new bool[cells.Length];for(var y=0;y<ny;y++)for(var x=0;x<nx;x++)mirrored[y*nx+x]=cells[y*nx+nx-1-x];cells=mirrored;}
        if(edge is not null){var rx=(int)Math.Ceiling(edge.StrokeWidthMillimeters/(width/nx));var ry=(int)Math.Ceiling(edge.StrokeWidthMillimeters/(height/ny));var border=new bool[cells.Length];for(var y=0;y<ny;y++)for(var x=0;x<nx;x++){if(!cells[y*nx+x])continue;var eroded=x>=rx&&y>=ry&&x+rx<nx&&y+ry<ny;if(eroded)for(var yy=y-ry;yy<=y+ry&&eroded;yy++)for(var xx=x-rx;xx<=x+rx;xx++)if(!cells[yy*nx+xx]){eroded=false;break;}border[y*nx+x]=!eroded;}cells=border;}
        return new(nx,ny,width,height,cells);
    }
    private static void Closed(SceneObject obj)
    {
        var vertices=new Dictionary<Vec3,int>();var edges=new Dictionary<(int,int),(int Count,int Direction)>();foreach(var face in obj.Mesh.Triangles){var p=new[]{face.A,face.B,face.C}.Select(i=>{var v=obj.Transform.Apply(obj.Mesh.Vertices[i]);if(!vertices.TryGetValue(v,out var id)){id=vertices.Count;vertices.Add(v,id);}return id;}).ToArray();for(var j=0;j<3;j++){var a=p[j];var b=p[(j+1)%3];var key=(Math.Min(a,b),Math.Max(a,b));var old=edges.GetValueOrDefault(key);edges[key]=(old.Count+1,old.Direction+(a<b?1:-1));}}Check(edges.Values.All(e=>e.Count==2&&e.Direction==0)&&Volume(obj)>0,"Mesh not closed/outward.");
    }
    private static double Volume(SceneObject obj)=>obj.Mesh.Triangles.Sum(t=>{var a=obj.Transform.Apply(obj.Mesh.Vertices[t.A]);var b=obj.Transform.Apply(obj.Mesh.Vertices[t.B]);var c=obj.Transform.Apply(obj.Mesh.Vertices[t.C]);var cross=Vec3.Cross(b,c);return a.X*cross.X+a.Y*cross.Y+a.Z*cross.Z;})/6;
    private static void Near(double actual,double expected,double tolerance=.001)=>Check(Math.Abs(actual-expected)<=tolerance,$"Expected {expected}, got {actual}.");
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Ok(OperationResult result)=>Check(result.Succeeded,result.ToString());
    private static void Same(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot==before,"Accepted state or held candidate changed.");
    private static void Accepted(EditorSession session,EditorSnapshot before)=>Check(session.Snapshot with{Preview=null}==before,"Preview mutated accepted state.");
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
