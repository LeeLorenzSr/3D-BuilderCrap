using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Builder.Core;

/// <summary>Bounded PLY1.0 geometry and opaque RGB. Interchange flattens objects/groups;
/// normals and other benign numeric attributes are validated, not retained.</summary>
internal static class PlyFiles
{
    private enum Scalar { I8,U8,I16,U16,I32,U32,F32,F64 }
    private sealed record Property(string Name,Scalar Item,Scalar? Count=null);
    private sealed record Element(string Name,int Count,List<Property> Properties);
    private static Scalar Type(string name)=>name switch
    {
        "char" or "int8"=>Scalar.I8,"uchar" or "uint8"=>Scalar.U8,"short" or "int16"=>Scalar.I16,"ushort" or "uint16"=>Scalar.U16,
        "int" or "int32"=>Scalar.I32,"uint" or "uint32"=>Scalar.U32,"float" or "float32"=>Scalar.F32,"double" or "float64"=>Scalar.F64,
        _=>throw new ModelException("Unsupported PLY scalar type: "+name)
    };
    private static bool Integer(Scalar type)=>type<=Scalar.U32;
    private static string[] Words(string text)=>text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
    private static bool Appearance(string name)=>name.Contains("texture",StringComparison.OrdinalIgnoreCase)||name.Contains("material",StringComparison.OrdinalIgnoreCase)||name.Contains("texcoord",StringComparison.OrdinalIgnoreCase);
    internal static Scene Read(string path,Stream stream,ModelUnit unit,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        try{return ReadBody(path,stream,unit,ct,progress);}
        catch(EndOfStreamException){throw new ModelException("The PLY header or data is truncated.");}
    }
    private static Scene ReadBody(string path,Stream stream,ModelUnit unit,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var factor=ModelFiles.UnitFactor(unit);var headerBytes=0;
        string Line()
        {
            var line=new StringBuilder();
            while(true)
            {
                ct.ThrowIfCancellationRequested();var b=stream.ReadByte();if(b<0)throw new EndOfStreamException();
                if(++headerBytes>65536||line.Length>4096)throw new ModelException("PLY headers are limited to 64 KiB and 4,096 characters per line.");
                if(b=='\n')return line.ToString().TrimEnd('\r');if(b>127||b==0)throw new ModelException("PLY headers must use ordinary ASCII declarations.");line.Append((char)b);
            }
        }
        if(Line()!="ply")throw new ModelException("Missing PLY signature.");
        var format=Words(Line());if(format.Length!=3||format[0]!="format"||format[2]!="1.0"||format[1] is not ("ascii" or "binary_little_endian" or "binary_big_endian"))throw new ModelException("Use PLY1.0 ASCII, binary little endian or binary big endian.");
        var ascii=format[1]=="ascii";var big=format[1]=="binary_big_endian";var elements=new List<Element>();Element? current=null;long recordCount=0;
        while(true)
        {
            var line=Line();var words=Words(line);if(words.Length==0)continue;
            if(words[0]=="end_header"){if(words.Length!=1)throw new ModelException("Malformed PLY end_header.");break;}
            if(words[0] is "comment" or "obj_info"){if(words.Length>1&&words[1].Equals("TextureFile",StringComparison.OrdinalIgnoreCase))throw new ModelException("PLY texture sidecars are not supported; use opaque RGB colors.");continue;}
            if(words[0]=="element")
            {
                if(words.Length!=3||!int.TryParse(words[2],NumberStyles.None,CultureInfo.InvariantCulture,out var count)||count<0||count>ModelLimits.MaxVertices||elements.Count>=32||elements.Any(e=>e.Name==words[1]))throw new ModelException("Invalid or oversized PLY element declaration.");
                if(Appearance(words[1]))throw new ModelException("PLY appearance extensions are not supported.");recordCount+=count;if(recordCount>700000)throw new ModelException("PLY exceeds the bounded numeric-record budget.");
                current=new(words[1],count,[]);elements.Add(current);continue;
            }
            if(words[0]=="property")
            {
                if(current is null||current.Properties.Count>=128)throw new ModelException("PLY properties need an element and are limited to 128 per element.");
                Property property;
                if(words.Length==3)property=new(words[2],Type(words[1]));
                else if(words.Length==5&&words[1]=="list"){var countType=Type(words[2]);if(!Integer(countType))throw new ModelException("PLY list counts must use integer types.");property=new(words[4],Type(words[3]),countType);}
                else throw new ModelException("Malformed PLY property declaration.");
                if(Appearance(property.Name)||current.Properties.Any(p=>p.Name==property.Name))throw new ModelException("Unsupported appearance or duplicate PLY property.");current.Properties.Add(property);continue;
            }
            throw new ModelException("Unsupported PLY header declaration: "+words[0]);
        }
        var vertexElement=elements.SingleOrDefault(e=>e.Name=="vertex")??throw new ModelException("PLY needs a vertex element.");
        var faceElement=elements.SingleOrDefault(e=>e.Name=="face")??throw new ModelException("PLY needs polygon faces; point clouds are not supported.");
        if(vertexElement.Count<1||faceElement.Count<1||faceElement.Count>ModelLimits.MaxTriangles)throw new ModelException("PLY exceeds scene limits or has no mesh faces.");
        foreach(var coordinate in new[]{"x","y","z"})if(vertexElement.Properties.SingleOrDefault(p=>p.Name==coordinate) is not {Count:null})throw new ModelException("PLY vertices need scalar x, y and z.");
        var indexProperties=faceElement.Properties.Where(p=>p.Name is "vertex_indices" or "vertex_index").ToArray();
        if(indexProperties.Length!=1||indexProperties[0].Count is null||!Integer(indexProperties[0].Item))throw new ModelException("PLY faces need one integer vertex_indices list.");
        bool HasColor(Element element)
        {
            var channels=element.Properties.Where(p=>p.Name is "red" or "green" or "blue").ToArray();var alpha=element.Properties.SingleOrDefault(p=>p.Name=="alpha");
            if(channels.Length!=0&&channels.Length!=3||channels.Any(p=>p.Item!=Scalar.U8||p.Count is not null)||alpha is not null&&(channels.Length!=3||alpha.Item!=Scalar.U8||alpha.Count is not null))
                throw new ModelException("PLY colors need complete uchar red/green/blue channels and optional opaque uchar alpha.");return channels.Length==3;
        }
        var vertexColored=HasColor(vertexElement);var faceColored=HasColor(faceElement);var scalarCount=0;
        double ReadNumber(Scalar type)
        {
            ct.ThrowIfCancellationRequested();if(++scalarCount>10000000)throw new ModelException("PLY exceeds the bounded numeric-value budget.");double value;
            if(ascii)
            {
                var token=new StringBuilder();int b;do{b=stream.ReadByte();if(b<0)throw new EndOfStreamException();}while(b is 9 or 10 or 13 or 32);
                do{if(b<33||b>126||token.Length>=128)throw new ModelException("Invalid or oversized PLY numeric token.");token.Append((char)b);b=stream.ReadByte();}while(b>=0&&b is not (9 or 10 or 13 or 32));
                if(!double.TryParse(token.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out value))throw new ModelException("Invalid PLY numeric value.");
            }
            else
            {
                Span<byte> bytes=stackalloc byte[8];var size=type is Scalar.I8 or Scalar.U8?1:type is Scalar.I16 or Scalar.U16?2:type is Scalar.I32 or Scalar.U32 or Scalar.F32?4:8;stream.ReadExactly(bytes[..size]);
                value=type switch
                {
                    Scalar.I8=>(sbyte)bytes[0],Scalar.U8=>bytes[0],Scalar.I16=>big?BinaryPrimitives.ReadInt16BigEndian(bytes):BinaryPrimitives.ReadInt16LittleEndian(bytes),Scalar.U16=>big?BinaryPrimitives.ReadUInt16BigEndian(bytes):BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                    Scalar.I32=>big?BinaryPrimitives.ReadInt32BigEndian(bytes):BinaryPrimitives.ReadInt32LittleEndian(bytes),Scalar.U32=>big?BinaryPrimitives.ReadUInt32BigEndian(bytes):BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                    Scalar.F32=>BitConverter.Int32BitsToSingle(big?BinaryPrimitives.ReadInt32BigEndian(bytes):BinaryPrimitives.ReadInt32LittleEndian(bytes)),Scalar.F64=>BitConverter.Int64BitsToDouble(big?BinaryPrimitives.ReadInt64BigEndian(bytes):BinaryPrimitives.ReadInt64LittleEndian(bytes)),_=>throw new ModelException("Unknown PLY type.")
                };
            }
            if(!double.IsFinite(value))throw new ModelException("PLY numeric values must be finite.");
            var (min,max)=type switch{Scalar.I8=>(-128d,127d),Scalar.U8=>(0d,255d),Scalar.I16=>(-32768d,32767d),Scalar.U16=>(0d,65535d),Scalar.I32=>((double)int.MinValue,(double)int.MaxValue),Scalar.U32=>(0d,(double)uint.MaxValue),Scalar.F32=>(-(double)float.MaxValue,(double)float.MaxValue),_=>(-double.MaxValue,double.MaxValue)};
            if(value<min||value>max||Integer(type)&&value!=Math.Truncate(value))throw new ModelException("PLY value is outside its declared scalar type.");return value;
        }
        var vertices=new List<Vec3>(vertexElement.Count);var vertexColors=new List<RgbColor>();var polygons=new List<(List<int> Indices,RgbColor? Color)>();var triangleBudget=0;
        foreach(var element in elements)
        {
            for(var i=0;i<element.Count;i++)
            {
                if(i%512==0){progress?.Report(new("Reading PLY "+element.Name,i,element.Count));ct.ThrowIfCancellationRequested();}
                double x=0,y=0,z=0;byte r=0,g=0,b=0;List<int>? indices=null;
                foreach(var property in element.Properties)
                {
                    if(property.Count is {} countType)
                    {
                        var countValue=ReadNumber(countType);var isFace=element==faceElement&&property==indexProperties[0];
                        if(countValue<0||countValue>(isFace?256:4096)||isFace&&countValue<3)throw new ModelException("PLY lists exceed the supported length (faces need 3–256 vertices).");var count=(int)countValue;
                        if(isFace){indices=new(count);triangleBudget+=count-2;if(triangleBudget>ModelLimits.MaxTriangles)throw new ModelException("PLY exceeds 100,000 resulting triangles.");}
                        for(var j=0;j<count;j++){var value=ReadNumber(property.Item);if(isFace){if(value<0||value>=vertexElement.Count)throw new ModelException("PLY face index is outside the declared vertex table.");indices!.Add((int)value);}}continue;
                    }
                    var scalar=ReadNumber(property.Item);
                    if(element==vertexElement||element==faceElement)
                    {
                        switch(property.Name){case "x" when element==vertexElement:x=scalar;break;case "y" when element==vertexElement:y=scalar;break;case "z" when element==vertexElement:z=scalar;break;case "red":r=(byte)scalar;break;case "green":g=(byte)scalar;break;case "blue":b=(byte)scalar;break;case "alpha":if(scalar!=255)throw new ModelException("Transparent PLY colors are not supported.");break;}
                    }
                }
                if(element==vertexElement){var point=new Vec3(x,y,z)*factor;_=Bounds3.From([point]);vertices.Add(point);if(vertexColored)vertexColors.Add(new(r,g,b));}
                else if(element==faceElement)polygons.Add((indices!,faceColored?new RgbColor(r,g,b):null));
            }
        }
        if(ascii){int b;while((b=stream.ReadByte())>=0)if(b is not(9 or 10 or 13 or 32))throw new ModelException("Unexpected data after PLY elements.");}
        else if(stream.Position!=stream.Length)throw new ModelException("Unexpected binary data after PLY elements.");
        var triangles=new List<Triangle>(triangleBudget);var colors=new List<RgbColor?>();
        foreach(var polygon in polygons)
        {
            ct.ThrowIfCancellationRequested();foreach(var triangle in PolygonTriangulation.Triangulate(polygon.Indices,vertices,"PLY"))
            {
                triangles.Add(triangle);var color=polygon.Color;
                if(color is null&&vertexColored)
                {var a=vertexColors[triangle.A];var b=vertexColors[triangle.B];var c=vertexColors[triangle.C];color=new((byte)Math.Round((a.R+b.R+c.R)/3d,MidpointRounding.AwayFromZero),(byte)Math.Round((a.G+b.G+c.G)/3d,MidpointRounding.AwayFromZero),(byte)Math.Round((a.B+b.B+c.B)/3d,MidpointRounding.AwayFromZero));}
                colors.Add(color);
            }
        }
        var palette=new Dictionary<RgbColor,SurfaceMaterial>();SurfaceMaterial Material(RgbColor color){if(!palette.TryGetValue(color,out var material)){if(palette.Count>=ModelLimits.MaxMaterials)throw new ModelException("PLY exceeds 1,000 flat colors.");palette.Add(color,material=new("Color "+color.Hex,color));}return material;}
        var assigned=colors.Select(c=>c is {} color?Material(color):null).ToArray();var fallback=assigned.All(c=>c is null)?null:assigned.Any(c=>c is null)?SurfaceMaterial.Neutral:assigned[0];var overrides=ImmutableDictionary.CreateBuilder<int,SurfaceMaterial>();
        for(var i=0;i<assigned.Length;i++)if(assigned[i] is {} material&&material!=fallback)overrides.Add(i,material);
        var name=Path.GetFileNameWithoutExtension(path);var length=Math.Min(name.Length,100);if(length>0&&char.IsHighSurrogate(name[length-1]))length--;name=name[..length];if(name.Length==0)name="PLY model";
        var scene=new Scene(Guid.NewGuid(),[new(Guid.NewGuid(),name,new(vertices,triangles),Affine.Identity){Material=fallback,TriangleMaterials=overrides.ToImmutable()}]);scene.Validate(ct);return scene;
    }
    internal static void WriteAtomic(string path,Scene scene,ModelUnit unit,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var factor=ModelFiles.UnitFactor(unit);if(!Path.GetExtension(path).Equals(".ply",StringComparison.OrdinalIgnoreCase))throw new ModelException("Use .ply for PLY export.");
        if(scene.Objects.IsEmpty)throw new ModelException("Add or restore an object before PLY export.");var triangleCount=scene.TriangleCount;
        if(triangleCount>ModelLimits.MaxTriangles||3L*triangleCount>ModelLimits.MaxVertices)throw new ModelException("Flattened PLY exceeds scene triangle/vertex limits.");
        var colors=scene.Objects.SelectMany(o=>Enumerable.Range(0,o.Mesh.Triangles.Length).Select(i=>(o.GetTriangleMaterial(i)??SurfaceMaterial.Neutral).Color)).Distinct().Count();if(colors>ModelLimits.MaxMaterials)throw new ModelException("PLY export supports up to 1,000 flat colors including neutral.");
        var temporary=Path.Combine(Path.GetDirectoryName(path)!,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");var own=false;
        try
        {
            progress?.Report(new("Preparing PLY export"));ct.ThrowIfCancellationRequested();
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                own=true;using(var writer=new BinaryWriter(file,new UTF8Encoding(false),true))
                {
                    var header=$"ply\nformat binary_little_endian 1.0\ncomment 3D BuilderCrap; coordinates in {unit}; native3MF retains objects/groups/model information\nelement vertex {3*triangleCount}\nproperty double x\nproperty double y\nproperty double z\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nelement face {triangleCount}\nproperty list uchar int vertex_indices\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n";writer.Write(Encoding.ASCII.GetBytes(header));var done=0;
                    foreach(var obj in scene.Objects)
                    {
                        var reflected=ModelFiles.Determinant(obj.Transform)<0;
                        for(var i=0;i<obj.Mesh.Triangles.Length;i++)
                        {
                            if(done++%512==0){progress?.Report(new("Writing PLY vertices",done,triangleCount));ct.ThrowIfCancellationRequested();}
                            var t=obj.Mesh.Triangles[i];var color=(obj.GetTriangleMaterial(i)??SurfaceMaterial.Neutral).Color;
                            foreach(var index in new[]{t.A,reflected?t.C:t.B,reflected?t.B:t.C}){var p=obj.Transform.Apply(obj.Mesh.Vertices[index])*(1/factor);writer.Write(p.X);writer.Write(p.Y);writer.Write(p.Z);writer.Write(color.R);writer.Write(color.G);writer.Write(color.B);}
                        }
                    }
                    var face=0;foreach(var obj in scene.Objects)for(var i=0;i<obj.Mesh.Triangles.Length;i++)
                    {if(face%512==0){progress?.Report(new("Writing PLY faces",face,triangleCount));ct.ThrowIfCancellationRequested();}var color=(obj.GetTriangleMaterial(i)??SurfaceMaterial.Neutral).Color;writer.Write((byte)3);writer.Write(3*face);writer.Write(3*face+1);writer.Write(3*face+2);writer.Write(color.R);writer.Write(color.G);writer.Write(color.B);face++;}writer.Flush();
                }file.Flush(true);
            }
            progress?.Report(new("Ready to export PLY"));ct.ThrowIfCancellationRequested();File.Move(temporary,path,true);
        }
        finally{if(own&&File.Exists(temporary))File.Delete(temporary);}
    }
}
