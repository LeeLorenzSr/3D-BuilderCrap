using Builder.Core;
using Builder.Windows.Images;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static string ReliefInputs=>Path.Combine(root,".local","evidence","image-relief-20261020","inputs");
    private static void BeginImageRelief()
    {
        var item=Tree(window).OfType<MenuItem>().Single(m=>m.Header?.ToString()=="Image…");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,item));
        Assert(Named<FrameworkElement>("ImageReliefPanel").Visibility==Visibility.Visible,"Image relief panel did not open.");
    }
    private static void ReliefFile(string path)=>Invoke("SetReliefImage",Path.GetFullPath(path));
    private static async Task PreviewRelief()
    {
        Click(Named<Button>("PreviewImageReliefButton"));await Idle();
        Assert(editor.Snapshot.Preview?.Request is ImageReliefRequest,"Relief preview failed: "+Named<TextBlock>("Status").Text);
    }
    private static async Task RunImageReliefControls()
    {
        var decoder=new WindowsRasterImageDecoder();var decoded=new Dictionary<string,RasterImage>();
        using var pixels=JsonDocument.Parse(File.ReadAllText(Path.Combine(ReliefInputs,"pixels.json")));
        foreach(var name in new[]{"black","white","ramp","marker","alpha","single-pixel"})
        {
            var image=await decoder.DecodeAsync(Path.Combine(ReliefInputs,name+".png"));decoded[name+".png"]=image;
            var expected=pixels.RootElement.GetProperty(name);
            Assert(image.Width==expected.GetProperty("width").GetInt32()&&image.Height==expected.GetProperty("height").GetInt32(),"PNG dimensions changed: "+name);
            Assert(image.Rgba.SequenceEqual(expected.GetProperty("rgba").EnumerateArray().Select(v=>(byte)v.GetInt32())),"PNG straight RGBA/row orientation changed: "+name);
            Assert(image.Provenance!.Contains(Hash(Path.Combine(ReliefInputs,name+".png"))),"Image provenance omitted input hash.");
        }
        foreach(var name in new[]{"marker","ramp"})
        {
            var image=await decoder.DecodeAsync(Path.Combine(ReliefInputs,name+".bmp"));decoded[name+".bmp"]=image;
            Assert(image.Width==decoded[name+".png"].Width&&image.Height==decoded[name+".png"].Height&&image.Rgba.SequenceEqual(decoded[name+".png"].Rgba),"BMP rows/channels differ from independent source pixels.");
        }
        // JPEG is deliberately lossy; retain actual decoded pixels for independent geometry checks,
        // without claiming an independent codec comparison from a WPF-generated fixture.
        var marker=decoded["marker.png"];var bgra=marker.Rgba.ToArray();
        for(var i=0;i<bgra.Length;i+=4)(bgra[i],bgra[i+2])=(bgra[i+2],bgra[i]);
        var bitmap=BitmapSource.Create(marker.Width,marker.Height,300,300,PixelFormats.Bgra32,null,bgra,marker.Width*4);
        var metadata=new BitmapMetadata("jpg");metadata.SetQuery("/app1/ifd/{ushort=274}",(ushort)6);
        var encoder=new JpegBitmapEncoder{QualityLevel=90};encoder.Frames.Add(BitmapFrame.Create(bitmap,null,metadata,null));
        var jpeg=Path.Combine(output,"synthetic-marker-oriented.jpg");using(var file=File.Create(jpeg))encoder.Save(file);
        var jpegImage=await decoder.DecodeAsync(jpeg);decoded["synthetic-marker-oriented.jpg"]=jpegImage;
        Assert(jpegImage.Width==64&&jpegImage.Height==48&&jpegImage.Rgba.Where((_,i)=>i%4==3).All(a=>a==255),"JPEG EXIF/DPI changed encoded dimensions or alpha.");
        File.WriteAllText(Path.Combine(output,"decoded-pixels.json"),JsonSerializer.Serialize(decoded,new JsonSerializerOptions{WriteIndented=true}));
        Pass("Actual PNG/BMP codecs match independent straight RGBA/source rows; JPEG retains encoded dimensions despite EXIF/DPI with decoded pixels and provenance recorded");

        async Task Reject(string path,string? fragment=null)
        {
            try{await decoder.DecodeAsync(path);throw new InvalidOperationException("Invalid image accepted: "+path);}
            catch(ModelException error){if(fragment!=null)Assert(error.Message.Contains(fragment,StringComparison.OrdinalIgnoreCase),"Wrong rejection: "+error.Message);}
        }
        await Reject(Path.Combine(ReliefInputs,"truncated.png"));await Reject(Path.Combine(ReliefInputs,"oversized.png"),"2,048");
        var noPixels=Path.Combine(output,"header-without-pixels.png");var validPng=File.ReadAllBytes(Path.Combine(ReliefInputs,"black.png"));
        File.WriteAllBytes(noPixels,validPng[..33].Concat(validPng[^12..]).ToArray());await Reject(noPixels);
        var unsupported=Path.Combine(output,"unsupported.gif");File.WriteAllBytes(unsupported,"GIF89a-not-supported"u8.ToArray());await Reject(unsupported,"PNG");
        var animated=Path.Combine(output,"animated-header.png");var original=File.ReadAllBytes(Path.Combine(ReliefInputs,"black.png"));
        var control=new byte[20];BinaryPrimitives.WriteUInt32BigEndian(control,8);"acTL"u8.CopyTo(control.AsSpan(4));BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(8),2);
        uint crc=0xFFFFFFFF;foreach(var value in control.AsSpan(4,12)){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc&1)!=0?(crc>>1)^0xEDB88320:crc>>1;}BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(16),crc^0xFFFFFFFF);
        File.WriteAllBytes(animated,original[..33].Concat(control).Concat(original[33..]).ToArray());await Reject(animated,"Animated");
        var large=Path.Combine(output,"over-file-limit.bmp");using(var file=File.Create(large))file.SetLength(64L*1024*1024+1);await Reject(large,"64 MiB");File.Delete(large);
        using(var cancellation=new CancellationTokenSource())
        {cancellation.Cancel();try{await decoder.DecodeAsync(Path.Combine(ReliefInputs,"marker.png"),cancellation.Token);throw new InvalidOperationException("Cancelled decoder returned pixels.");}catch(OperationCanceledException){}}
        Pass("Decoder rejects unsupported, malformed, animated, oversized and over-file-budget inputs before conversion and honors pre-cancellation");

        await Open("cube-20mm.stl");var before=editor.Snapshot;BeginImageRelief();
        Assert(!Named<Button>("AddImageReliefButton").IsEnabled,"Add relief enabled without preview.");
        Click(Named<Button>("PreviewImageReliefButton"));await Idle();SameAccepted(before);Assert(editor.Snapshot.Preview==null,"Preview without image created geometry.");
        ReliefFile(Path.Combine(ReliefInputs,"ramp.png"));Named<TextBox>("ImageReliefDetail").Text="9";
        InspectionFooterInside("PreviewImageReliefButton","AddImageReliefButton","CancelImageReliefButton");Capture("image-relief-settings-minimum",1040,680);
        await PreviewRelief();SameAccepted(before);var candidate=editor.Snapshot.Preview!;var id=candidate.ResultObjectIds.Single();var mesh=candidate.Scene.Objects.Single(o=>o.Id==id);
        Near(mesh.Bounds.Min.X,0);Near(mesh.Bounds.Min.Y,0);Near(mesh.Bounds.Min.Z,0);Near(mesh.Bounds.Size.X,80);Near(mesh.Bounds.Size.Y,80*5d/9);Near(mesh.Bounds.Max.Z,4);
        Assert(Named<TextBlock>("ImageReliefSummary").Text.Contains("9 × 5 grid")&&Named<TextBlock>("ImageReliefSummary").Text.Contains("triangles"),"Preview lacks physical grid/triangle review.");
        Capture("image-relief-preview-minimum",1040,680);Click(Named<Button>("AddImageReliefButton"));
        Assert(editor.Snapshot.SelectedObjectId==id&&editor.Snapshot.Scene.Objects.Length==before.Scene.Objects.Length+1&&editor.Snapshot.FilePath==before.FilePath,"Relief Add changed identity/count/native path.");
        Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene)&&editor.Snapshot.SelectedObjectIds.SequenceEqual(before.SelectedObjectIds),"Relief Undo did not restore exact accepted scene/selection.");
        Click(Named<Button>("RedoButton"));Assert(editor.Snapshot.Scene.Objects.Any(o=>o.Id==id),"Relief Redo lost stable object ID.");Click(Named<Button>("UndoButton"));
        Pass("Image file preview keeps aspect/physical backing and relief, reviews grid/triangles, and Add/Undo/Redo retains one stable mesh and native path");

        before=editor.Snapshot;BeginImageRelief();ReliefFile(Path.Combine(ReliefInputs,"marker.png"));Named<TextBox>("ImageReliefDetail").Text="32";await PreviewRelief();candidate=editor.Snapshot.Preview!;
        Named<TextBox>("ImageReliefWidth").Text="NaN";Assert(!Named<Button>("AddImageReliefButton").IsEnabled,"Changed relief draft allowed stale Add.");Invoke("AddImageReliefClick",window,new RoutedEventArgs());SameAccepted(before);
        Click(Named<Button>("PreviewImageReliefButton"));await Idle();Assert(editor.Snapshot.Preview?.Id==candidate.Id,"Invalid numeric draft erased prior relief.");
        Named<TextBox>("ImageReliefWidth").Text="40";ReliefFile(Path.Combine(ReliefInputs,"truncated.png"));Click(Named<Button>("PreviewImageReliefButton"));await Idle();SameAccepted(before);
        Assert(editor.Snapshot.Preview?.Id==candidate.Id&&!Named<Button>("AddImageReliefButton").IsEnabled,"Decode failure changed or accepted old candidate.");
        ReliefFile(Path.Combine(ReliefInputs,"marker.bmp"));Named<CheckBox>("ImageReliefInvert").IsChecked=true;await PreviewRelief();
        Assert(editor.Snapshot.Preview!.Id!=candidate.Id,"Replacement relief did not receive a fresh preview token.");
        Assert(editor.Snapshot.Preview.ResultObjectIds.Single()==candidate.ResultObjectIds.Single(),"Replacement relief lost new-object identity.");
        Near(editor.Snapshot.Preview.Scene.Objects.Single(o=>o.Id==candidate.ResultObjectIds.Single()).Bounds.Size.X,40);
        Click(Named<Button>("CancelImageReliefButton"));SameAccepted(before);Assert(editor.Snapshot.Preview==null,"Cancel kept relief candidate.");
        BeginImageRelief();ReliefFile(Path.Combine(ReliefInputs,"marker.png"));Click(Named<Button>("PreviewImageReliefButton"));Named<TextBox>("ImageReliefWidth").Text="50";await Idle();
        Assert(!Named<Button>("AddImageReliefButton").IsEnabled,"In-flight draft change allowed old geometry acceptance.");Click(Named<Button>("CancelImageReliefButton"));SameAccepted(before);
        BeginImageRelief();ReliefFile(Path.Combine(ReliefInputs,"marker.png"));Click(Named<Button>("PreviewImageReliefButton"));Click(Named<Button>("CancelImageReliefButton"));await Idle();SameAccepted(before);
        Assert(Named<FrameworkElement>("ImageReliefActions").Visibility==Visibility.Collapsed&&editor.Snapshot.Preview==null,"In-flight cancellation kept tool/preview.");
        Pass("Relief draft/path/invert changes reject stale Add; malformed input preserves prior candidate and ready/in-flight cancel preserves accepted state");

        BeginImageRelief();ReliefFile(Path.Combine(ReliefInputs,"alpha.png"));await PreviewRelief();
        foreach(var name in new[]{"ImageReliefWidth","ImageReliefDepth","ImageReliefBacking","ImageReliefDetail"})
        {var field=Named<TextBox>(name);Assert(field.IsUndoEnabled&&InputMethod.GetIsInputMethodEnabled(field),"Relief field lost local Undo/IME: "+name);}
        CheckLocalTextUndo(Named<TextBox>("ImageReliefWidth"),"80","40");SameAccepted(before);Assert(!Named<Button>("AddImageReliefButton").IsEnabled,"Local Undo resurrected stale Add.");
        Invoke("NewTextClick",window,new RoutedEventArgs());Invoke("MirrorClick",window,new RoutedEventArgs());Invoke("MoveHandleClick",window,new RoutedEventArgs());Invoke("UndoClick",window,new RoutedEventArgs());SameAccepted(before);
        Assert(Named<FrameworkElement>("TextToolActions").Visibility==Visibility.Collapsed&&Named<FrameworkElement>("ImageReliefActions").Visibility==Visibility.Visible,"Conflicting tool replaced relief.");
        using(var sourceWindow=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("isolated-relief-keys"){Width=1,Height=1,WindowStyle=0}))
        {
            foreach(var name in new[]{"PreviewImageReliefButton","AddImageReliefButton","CancelImageReliefButton"})
            {var key=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>(name).RaiseEvent(key);Assert(!key.Handled,"Global Enter intercepted relief footer.");}
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,sourceWindow,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};Named<Button>("CancelImageReliefButton").RaiseEvent(escape);Assert(escape.Handled,"Relief Escape not handled.");
        }
        SameAccepted(before);await Open("assembly-mm.3mf");var ids=editor.Snapshot.Scene.Objects.Select(o=>o.Id).ToImmutableArray();Ok(await editor.PreviewArrangementAsync(new GroupRequest(ids)));Ok(editor.ApplyPreview(editor.Snapshot.Preview!.Id));Ok(editor.EnterGroup(editor.Snapshot.Scene.Groups.Single().Id));Refresh();before=editor.Snapshot;
        Invoke("ImageReliefClick",window,new RoutedEventArgs());SameAccepted(before);Assert(Named<FrameworkElement>("ImageReliefActions").Visibility==Visibility.Collapsed,"Relief creation opened in member scope.");Ok(editor.ExitGroup());Refresh();
        Pass("Relief preserves ordinary text Undo/IME and button Enter, Escape/conflicting tools and member scope rules");

        var outputCases=new List<object>();
        foreach(var (name,image) in decoded)
        {
            var path=name.EndsWith(".jpg",StringComparison.Ordinal)?jpeg:Path.Combine(ReliefInputs,name);
            var session=new EditorSession(imageDecoder:decoder);var request=new ImageReliefRequest(new ImageFileSource(path),40,3,1,name.Contains("marker")?32:8);
            Ok(await session.PreviewImageReliefAsync(request));Ok(session.ApplyPreview(session.Snapshot.Preview!.Id));
            var basename="decoded-"+name.Replace('.','-');var stl=Path.Combine(output,basename+".stl");var threemf=Path.Combine(output,basename+".3mf");
            Ok(await session.ExportStlAsync(stl));Ok(await session.SaveAsync(threemf));
            outputCases.Add(new{input=path,inputSha256=Hash(path),decodedKey=name,widthMillimeters=40,reliefDepthMillimeters=3,baseThicknessMillimeters=1,samples=request.Samples,invert=false,
                outputs=new[]{new{path=stl,sha256=Hash(stl)},new{path=threemf,sha256=Hash(threemf)}}});
        }
        File.WriteAllText(Path.Combine(output,"decoded-output-manifest.json"),JsonSerializer.Serialize(new{method="Actual Windows decoder → shared file request → Apply → STL/3MF; independent sessions, no native UI",decodedPixels=Path.Combine(output,"decoded-pixels.json"),decodedPixelsSha256=Hash(Path.Combine(output,"decoded-pixels.json")),cases=outputCases},new JsonSerializerOptions{WriteIndented=true}));
        Pass("Actual PNG/BMP/JPEG file requests export accepted reliefs with pinned decoded pixels/parameters for independent dimensions/topology/height/volume verification");
    }
}
