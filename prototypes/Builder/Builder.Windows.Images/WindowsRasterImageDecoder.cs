using Builder.Core;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Builder.Windows.Images;

/// <summary>Installed Windows codecs to immutable, top-down, straight RGBA8 pixels. This adapter
/// owns no window or viewport and does not apply EXIF orientation, DPI or color-profile transforms.</summary>
public sealed class WindowsRasterImageDecoder : IRasterImageDecoder
{
    private const long MaximumFileBytes=64L*1024*1024;
    private const int MaximumDimension=2048;
    public Task<RasterImage> DecodeAsync(string path,CancellationToken cancellation=default)
        =>Task.Run(()=>Decode(path,cancellation),cancellation);

    private static RasterImage Decode(string path,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(stream.Length==0||stream.Length>MaximumFileBytes)throw new ModelException("Choose an image file no larger than 64 MiB.");
            // Header validation precedes codec construction and pixel conversion. Never trust the
            // filename extension or allocate from unbounded encoded dimensions.
            var (width,height,container)=ReadHeader(stream,cancellation);
            ValidateDimensions(width,height);
            stream.Position=0;
            using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var hashBuffer=new byte[64*1024];int count;
            while((count=stream.Read(hashBuffer))!=0){cancellation.ThrowIfCancellationRequested();digest.AppendData(hashBuffer,0,count);}
            var sha256=Convert.ToHexString(digest.GetHashAndReset());
            stream.Position=0;cancellation.ThrowIfCancellationRequested();
            var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat|BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnDemand);
            if(decoder is not(PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder)||decoder.Frames.Count!=1)
                throw new ModelException("Choose a single-frame PNG, JPEG or BMP image. Animated and multi-frame images are not supported.");
            var frame=decoder.Frames[0];
            if(frame.PixelWidth!=width||frame.PixelHeight!=height)throw new ModelException("The image header and decoded dimensions do not agree.");
            cancellation.ThrowIfCancellationRequested();
            // BGRA32 is straight (unpremultiplied) alpha; PBGRA32 would lose the required channel
            // contract. WIC converts indexed/gray/high-bit-depth sources before this row copy.
            BitmapSource pixels=frame.Format==PixelFormats.Bgra32?frame:new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
            var stride=checked(width*4);var rgba=new byte[checked(stride*height)];var row=new byte[stride];
            for(var y=0;y<height;y++)
            {
                cancellation.ThrowIfCancellationRequested();pixels.CopyPixels(new Int32Rect(0,y,width,1),row,stride,0);
                for(var x=0;x<stride;x+=4)
                {var target=y*stride+x;rgba[target]=row[x+2];rgba[target+1]=row[x+1];rgba[target+2]=row[x];rgba[target+3]=row[x+3];}
            }
            cancellation.ThrowIfCancellationRequested();
            var provenance=JsonSerializer.Serialize(new{decoder="Windows WPF installed codecs",runtime=Environment.Version.ToString(),container,
                fileSha256=sha256,fileBytes=stream.Length,width,height,sourcePixelFormat=frame.Format.ToString(),pixelFormat="Unpremultiplied RGBA8",
                rowOrder="Top to bottom",channelSpace="Encoded sRGB channels; no gamma linearization",orientation="Encoded pixels; EXIF orientation ignored",
                dpi="Ignored",colorProfiles="Ignored",frames=1});
            return new(width,height,rgba.ToImmutableArray(),provenance);
        }
        catch(OperationCanceledException){throw;}
        catch(ModelException){throw;}
        catch(Exception error) when(error is IOException or FileFormatException or System.Runtime.InteropServices.COMException or
            UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {throw new ModelException("Could not decode this image. Choose a valid single-frame PNG, JPEG or BMP. "+error.Message);}
    }

    private static void ValidateDimensions(int width,int height)
    {
        if(width<1||height<1||width>MaximumDimension||height>MaximumDimension)
            throw new ModelException("Images must be 1–2,048 pixels on each axis (at most 4,194,304 pixels).");
    }
    private static byte[] Read(FileStream stream,int count,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var bytes=new byte[count];stream.ReadExactly(bytes);return bytes;
    }
    private static void Skip(FileStream stream,long count)
    {if(count<0||count>stream.Length-stream.Position)throw new ModelException("The image header is incomplete or malformed.");stream.Position+=count;}
    private static (int Width,int Height,string Container) ReadHeader(FileStream stream,CancellationToken cancellation)
    {
        var signature=Read(stream,8,cancellation);stream.Position=0;
        if(signature.AsSpan().SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
        {
            stream.Position=8;var chunk=Read(stream,8,cancellation);
            if(BinaryPrimitives.ReadUInt32BigEndian(chunk)!=13||!chunk.AsSpan(4).SequenceEqual("IHDR"u8))throw new ModelException("The PNG header is malformed.");
            var header=Read(stream,13,cancellation);var w=BinaryPrimitives.ReadUInt32BigEndian(header);var h=BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if(w>MaximumDimension||h>MaximumDimension)throw new ModelException("Images must be at most 2,048 pixels on each axis.");
            ValidateDimensions((int)w,(int)h);Skip(stream,4);
            while(stream.Position<stream.Length)
            {
                chunk=Read(stream,8,cancellation);var size=BinaryPrimitives.ReadUInt32BigEndian(chunk);
                if(chunk.AsSpan(4).SequenceEqual("acTL"u8)||chunk.AsSpan(4).SequenceEqual("fdAT"u8))throw new ModelException("Animated PNG images are not supported. Choose a single-frame image.");
                Skip(stream,(long)size+4);
                if(chunk.AsSpan(4).SequenceEqual("IEND"u8))
                {if(size!=0||stream.Position!=stream.Length)throw new ModelException("The PNG ending is malformed.");return((int)w,(int)h,"PNG");}
            }
            throw new ModelException("The PNG ending is missing.");
        }
        if(signature[0]==0xFF&&signature[1]==0xD8)
        {
            stream.Position=2;int width=0,height=0;
            while(stream.Position<stream.Length)
            {
                cancellation.ThrowIfCancellationRequested();if(stream.ReadByte()!=0xFF)throw new ModelException("The JPEG header is malformed.");
                int marker;do{marker=stream.ReadByte();}while(marker==0xFF);
                if(marker<0||marker==0xD9)break;
                if(marker is 0x01 or >=0xD0 and <=0xD7)continue;
                var length=BinaryPrimitives.ReadUInt16BigEndian(Read(stream,2,cancellation));if(length<2)throw new ModelException("The JPEG segment length is invalid.");
                var payload=length-2;var start=stream.Position;
                if(marker is >=0xC0 and <=0xCF && marker is not(0xC4 or 0xC8 or 0xCC))
                {
                    if(payload<6)throw new ModelException("The JPEG size header is incomplete.");var frame=Read(stream,5,cancellation);
                    height=BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1));width=BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3));ValidateDimensions(width,height);
                }
                if(marker==0xE2&&payload>=4&&Read(stream,4,cancellation).AsSpan().SequenceEqual("MPF\0"u8))
                    throw new ModelException("Multi-picture JPEG images are not supported. Choose a single-frame image.");
                stream.Position=start;Skip(stream,payload);
                if(marker==0xDA)
                {if(width==0||height==0)break;return(width,height,"JPEG");}
            }
            throw new ModelException("The JPEG dimensions are missing or malformed.");
        }
        if(signature[0]=='B'&&signature[1]=='M')
        {
            stream.Position=14;var size=BinaryPrimitives.ReadUInt32LittleEndian(Read(stream,4,cancellation));int width,height;
            if(size==12){var dimensions=Read(stream,4,cancellation);width=BinaryPrimitives.ReadUInt16LittleEndian(dimensions);height=BinaryPrimitives.ReadUInt16LittleEndian(dimensions.AsSpan(2));}
            else if(size is 40 or 52 or 56 or 64 or 108 or 124)
            {var dimensions=Read(stream,8,cancellation);width=BinaryPrimitives.ReadInt32LittleEndian(dimensions);var signedHeight=BinaryPrimitives.ReadInt32LittleEndian(dimensions.AsSpan(4));if(signedHeight==int.MinValue)throw new ModelException("The BMP height is invalid.");height=Math.Abs(signedHeight);}
            else throw new ModelException("This BMP header is unsupported.");
            ValidateDimensions(width,height);return(width,height,"BMP");
        }
        throw new ModelException("Choose a PNG, JPEG or BMP image. Other image formats are not supported.");
    }
}
