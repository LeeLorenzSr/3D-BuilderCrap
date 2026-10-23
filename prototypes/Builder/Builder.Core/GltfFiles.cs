using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Builder.Core;

/// <summary>Static glTF2 meshes with opaque base/vertex color. No rendering/framework dependencies.</summary>
internal static class GltfFiles
{
    private static bool Has(JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out _);
    private static JsonElement Required(JsonElement value,string name)=>value.TryGetProperty(name,out var child)?child:throw new ModelException("Missing glTF "+name+".");
    private static int Int(JsonElement value)=>value.TryGetInt32(out var n)?n:throw new ModelException("glTF indices and counts must be integers.");
    private static int Int(JsonElement value,string name,int fallback=0)=>value.TryGetProperty(name,out var child)?Int(child):fallback;
    private static double Number(JsonElement value)=>value.TryGetDouble(out var n)&&double.IsFinite(n)?n:throw new ModelException("glTF numbers must be finite.");
    private static JsonElement[] Array(JsonElement value,string name,int max)
    {
        if(!value.TryGetProperty(name,out var array))return [];if(array.ValueKind!=JsonValueKind.Array||array.GetArrayLength()>max)throw new ModelException("Invalid or oversized glTF "+name+" array.");return array.EnumerateArray().ToArray();
    }
    private static JsonElement At(JsonElement[] values,int index,string label)=>index>=0&&index<values.Length?values[index]:throw new ModelException("Invalid glTF "+label+" reference.");
    private static double[] Vector(JsonElement value,string name,int count,double[] fallback)
    {
        if(!value.TryGetProperty(name,out var child))return fallback;if(child.ValueKind!=JsonValueKind.Array||child.GetArrayLength()!=count)throw new ModelException("Invalid glTF "+name+" vector.");return child.EnumerateArray().Select(Number).ToArray();
    }
    private static string Name(JsonElement value,string fallback)
    {
        var name=value.TryGetProperty("name",out var child)&&child.ValueKind==JsonValueKind.String?child.GetString()!.Trim():fallback;
        if(name.Length==0)name=fallback;if(name.Any(char.IsControl))throw new ModelException("glTF names cannot contain controls.");var length=Math.Min(name.Length,100);if(length>0&&char.IsHighSurrogate(name[length-1]))length--;return name[..length];
    }
    private static double Linear(byte value){var c=value/255d;return c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);}
    private static byte Srgb(double value){var c=value<=.0031308?12.92*value:1.055*Math.Pow(value,1/2.4)-.055;return(byte)Math.Clamp(Math.Round(c*255,MidpointRounding.AwayFromZero),0,255);}
    internal static Scene Read(string path,Stream stream,ModelUnit unit,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        try
        {
            byte[]? bin=null;JsonDocument document;
            if(Path.GetExtension(path).Equals(".glb",StringComparison.OrdinalIgnoreCase))
            {
                using var reader=new BinaryReader(stream,Encoding.UTF8,true);
                if(reader.ReadUInt32()!=0x46546c67||reader.ReadUInt32()!=2||reader.ReadUInt32()!=stream.Length)throw new ModelException("Invalid glTF2 GLB header.");
                byte[]? json=null;var chunk=0;
                while(stream.Position<stream.Length)
                {
                    ct.ThrowIfCancellationRequested();var length=reader.ReadUInt32();var type=reader.ReadUInt32();if(length%4!=0||length>stream.Length-stream.Position)throw new ModelException("Invalid GLB chunk length.");
                    if(chunk++==0&&type!=0x4e4f534a)throw new ModelException("GLB JSON must be the first chunk.");
                    if(type==0x4e4f534a){if(json is not null)throw new ModelException("Duplicate GLB JSON chunk.");json=reader.ReadBytes((int)length);}
                    else if(type==0x004e4942){if(bin is not null)throw new ModelException("Duplicate GLB binary chunk.");bin=reader.ReadBytes((int)length);}
                    else stream.Position+=length;
                }
                document=JsonDocument.Parse(json??throw new ModelException("GLB contains no JSON."),new JsonDocumentOptions{MaxDepth=64});
            }
            else document=JsonDocument.Parse(stream,new JsonDocumentOptions{MaxDepth=64});
            using(document)return new Reader(path,stream.Length,document.RootElement,bin,unit,ct,progress).Read();
        }
        catch(Exception e) when(e is JsonException or EndOfStreamException or FormatException or OverflowException or InvalidOperationException)
        {throw new ModelException("Malformed glTF/GLB: "+e.Message);}
    }
    private sealed class Reader
    {
        private readonly string path;private readonly JsonElement root;private readonly CancellationToken ct;private readonly IProgress<OperationProgress>? progress;private readonly double factor;
        private readonly JsonElement[] views,accessors,meshes,nodes,materials;private readonly byte[][] buffers;private readonly Dictionary<int,(Mesh Mesh,SurfaceMaterial? Base,ImmutableDictionary<int,SurfaceMaterial> Faces)> meshCache=[];
        private readonly HashSet<SurfaceMaterial> palette=[];private long totalBytes;
        internal Reader(string path,long sourceBytes,JsonElement root,byte[]? bin,ModelUnit unit,CancellationToken ct,IProgress<OperationProgress>? progress)
        {
            this.path=path;this.root=root;this.ct=ct;this.progress=progress;factor=ModelFiles.UnitFactor(unit);totalBytes=sourceBytes;
            ValidateJson(root);
            var asset=Required(root,"asset");if(Required(asset,"version").GetString()!="2.0"||Has(asset,"minVersion")&&Required(asset,"minVersion").GetString()!="2.0")throw new ModelException("Use glTF2.0 static meshes.");
            foreach(var unsupported in new[]{"animations","skins","textures","images"})if(Array(root,unsupported,10000).Length>0)throw new ModelException("glTF "+unsupported+" are not supported. Export static opaque meshes.");
            if(Array(root,"extensionsRequired",128).Length>0)throw new ModelException("Required glTF extensions are not supported.");
            views=Array(root,"bufferViews",10000);accessors=Array(root,"accessors",10000);meshes=Array(root,"meshes",1000);nodes=Array(root,"nodes",4000);materials=Array(root,"materials",1000);
            var declarations=Array(root,"buffers",32);buffers=new byte[declarations.Length][];
            for(var i=0;i<declarations.Length;i++)
            {
                ct.ThrowIfCancellationRequested();var declaration=declarations[i];var length=Int(declaration,"byteLength",-1);if(length<1||length>ModelLimits.MaxFileBytes)throw new ModelException("Invalid glTF buffer length.");
                byte[] data;
                if(declaration.TryGetProperty("uri",out var uriValue))
                {
                    var uri=uriValue.GetString()??throw new ModelException("Invalid glTF buffer URI.");
                    if(uri.StartsWith("data:",StringComparison.Ordinal))
                    {
                        var comma=uri.IndexOf(',');if(comma<0||uri[..comma] is not("data:application/octet-stream;base64" or "data:application/gltf-buffer;base64")||uri.Length-comma-1>ModelLimits.MaxFileBytes*4/3+8)throw new ModelException("Use bounded base64 glTF buffers.");data=Convert.FromBase64String(uri[(comma+1)..]);
                    }
                    else
                    {
                        uri=Uri.UnescapeDataString(uri);
                        if(uri.Length==0||Path.GetFileName(uri)!=uri||uri.IndexOfAny(['/', '\\', ':','?','#'])>=0||Path.IsPathRooted(uri)||!Path.GetExtension(uri).Equals(".bin",StringComparison.OrdinalIgnoreCase))throw new ModelException("glTF buffer companions must be simple local .bin filenames beside the model.");
                        var full=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,uri));
                        if(full.StartsWith("\\\\",StringComparison.Ordinal)||File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))throw new ModelException("Network/linked glTF buffers are not supported.");
                        if(new FileInfo(full).Length>ModelLimits.MaxFileBytes)throw new ModelException("glTF buffers exceed 64 MiB.");data=File.ReadAllBytes(full);
                    }
                    totalBytes+=data.Length;if(totalBytes>ModelLimits.MaxFileBytes)throw new ModelException("glTF model and decoded/companion buffers together are limited to 64 MiB.");if(data.Length!=length)throw new ModelException("glTF buffer byteLength does not match its data.");
                }
                else
                {
                    if(i!=0||bin is null||bin.Length<length||bin.Length-length>3)throw new ModelException("Missing or mismatched GLB binary buffer.");data=bin[..length];
                }
                buffers[i]=data;
            }
            for(var i=0;i<accessors.Length;i++)_=Accessor(i);
            foreach(var material in materials)_=Material(material);
        }
        private void ValidateJson(JsonElement value)
        {
            ct.ThrowIfCancellationRequested();if(value.ValueKind==JsonValueKind.Object)
            {
                var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject())
                {
                    if(!names.Add(p.Name))throw new ModelException("Duplicate glTF JSON properties are ambiguous.");
                    if(p.Name=="extensions")foreach(var extension in p.Value.EnumerateObject())if(extension.Name!="KHR_materials_unlit")throw new ModelException("Unsupported glTF extension: "+extension.Name);
                    ValidateJson(p.Value);
                }
            }
            else if(value.ValueKind==JsonValueKind.Array){if(value.GetArrayLength()>1000000)throw new ModelException("Oversized glTF JSON array.");foreach(var child in value.EnumerateArray())ValidateJson(child);}
        }
        private sealed record Access(byte[] Data,int Offset,int Stride,int Count,int Components,int Component,bool Normalized);
        private Access Accessor(int index)
        {
            var a=At(accessors,index,"accessor");if(Has(a,"sparse"))throw new ModelException("Sparse glTF accessors are not supported.");var view=At(views,Int(a,"bufferView",-1),"bufferView");var buffer=Int(view,"buffer",-1);if(buffer<0||buffer>=buffers.Length)throw new ModelException("Invalid glTF buffer reference.");
            var component=Int(a,"componentType");var size=component switch{5120 or 5121=>1,5122 or 5123=>2,5125 or 5126=>4,_=>throw new ModelException("Unsupported glTF accessor component type.")};
            var components=Required(a,"type").GetString() switch{"SCALAR"=>1,"VEC2"=>2,"VEC3"=>3,"VEC4"=>4,_=>throw new ModelException("Only scalar/vector static glTF accessors are supported.")};
            var count=Int(a,"count");var vo=Int(view,"byteOffset");var length=Int(view,"byteLength");var offset=Int(a,"byteOffset");var stride=Int(view,"byteStride",size*components);
            if(count<1||count>ModelLimits.MaxVertices||vo<0||length<1||offset<0||(long)vo+length>buffers[buffer].Length||offset%size!=0||(vo+offset)%size!=0||stride<size*components||stride>252||Has(view,"byteStride")&&(stride<4||stride%4!=0)||(long)offset+(long)stride*(count-1)+size*components>length)throw new ModelException("glTF accessor length, stride or alignment is invalid.");
            var normalized=a.TryGetProperty("normalized",out var norm)&&norm.GetBoolean();if(normalized&&component is 5125 or 5126)throw new ModelException("Invalid normalized glTF accessor type.");return new(buffers[buffer],vo+offset,stride,count,components,component,normalized);
        }
        private double Value(Access a,int index,int channel)
        {
            if(index<0||index>=a.Count||channel>=a.Components)throw new ModelException("glTF index is outside the accessor.");var size=a.Component is 5120 or 5121?1:a.Component is 5122 or 5123?2:4;var span=a.Data.AsSpan(a.Offset+index*a.Stride+channel*size,size);
            double value=a.Component switch{5120=>(sbyte)span[0],5121=>span[0],5122=>BinaryPrimitives.ReadInt16LittleEndian(span),5123=>BinaryPrimitives.ReadUInt16LittleEndian(span),5125=>BinaryPrimitives.ReadUInt32LittleEndian(span),5126=>BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),_=>0};
            if(!double.IsFinite(value))throw new ModelException("glTF accessor values must be finite.");if(a.Normalized)value=a.Component switch{5120=>Math.Max(value/127,-1),5121=>value/255,5122=>Math.Max(value/32767,-1),5123=>value/65535,_=>value};return value;
        }
        private (string Name,double[] Factor) Material(JsonElement material)
        {
            if(material.TryGetProperty("alphaMode",out var alpha)&&alpha.GetString()!="OPAQUE")throw new ModelException("Transparent/masked glTF materials are not supported.");
            foreach(var p in material.EnumerateObject())if(p.Name.EndsWith("Texture",StringComparison.Ordinal))throw new ModelException("glTF texture materials are not supported.");
            var factor=new[]{1d,1,1,1};if(material.TryGetProperty("pbrMetallicRoughness",out var pbr))
            {foreach(var p in pbr.EnumerateObject())if(p.Name.EndsWith("Texture",StringComparison.Ordinal))throw new ModelException("glTF texture materials are not supported.");factor=Vector(pbr,"baseColorFactor",4,factor);}
            if(factor.Any(v=>v<0||v>1))throw new ModelException("glTF base colors need channels from zero to one.");return(Name(material,"Material"),factor);
        }
        private (Mesh Mesh,SurfaceMaterial? Base,ImmutableDictionary<int,SurfaceMaterial> Faces) Mesh(int index)
        {
            if(meshCache.TryGetValue(index,out var cached))return cached;var mesh=At(meshes,index,"mesh");if(Has(mesh,"weights"))throw new ModelException("Morph targets are not supported.");var vertices=new List<Vec3>();var triangles=new List<Triangle>();var assigned=new List<SurfaceMaterial>();var positionOffsets=new Dictionary<int,int>();
            foreach(var primitive in Array(mesh,"primitives",1000))
            {
                ct.ThrowIfCancellationRequested();if(Has(primitive,"targets"))throw new ModelException("Morph targets are not supported.");var attributes=Required(primitive,"attributes");var positionIndex=Int(Required(attributes,"POSITION"));var position=Accessor(positionIndex);if(position.Component!=5126||position.Components!=3||position.Normalized)throw new ModelException("glTF POSITION must be float VEC3.");
                Access? color=null;foreach(var attribute in attributes.EnumerateObject())
                {
                    var accessor=Accessor(Int(attribute.Value));if(accessor.Count!=position.Count)throw new ModelException("glTF vertex attribute lengths differ.");
                    if(attribute.Name=="COLOR_0"){if(accessor.Components is not(3 or 4)||accessor.Component is not(5121 or 5123 or 5126)||accessor.Component!=5126&&!accessor.Normalized||accessor.Component==5126&&accessor.Normalized)throw new ModelException("glTF COLOR_0 must be normalized unsigned RGB/RGBA or float RGB/RGBA.");color=accessor;}
                    else if(attribute.Name.StartsWith("COLOR_",StringComparison.Ordinal)||attribute.Name.StartsWith("JOINTS",StringComparison.Ordinal)||attribute.Name.StartsWith("WEIGHTS",StringComparison.Ordinal))throw new ModelException("Unsupported additional glTF colors/skinning.");
                    else if(attribute.Name!="POSITION")for(var j=0;j<accessor.Count;j++){if(j%1024==0)ct.ThrowIfCancellationRequested();for(var c=0;c<accessor.Components;c++)_=Value(accessor,j,c);}
                }
                if(!positionOffsets.TryGetValue(positionIndex,out var offset))
                {
                    offset=vertices.Count;if((long)offset+position.Count>ModelLimits.MaxVertices)throw new ModelException("glTF mesh exceeds vertex limits.");
                    for(var i=0;i<position.Count;i++){if(i%1024==0)ct.ThrowIfCancellationRequested();vertices.Add(new(Value(position,i,0),Value(position,i,1),Value(position,i,2)));}positionOffsets.Add(positionIndex,offset);
                }
                int[] indices;if(primitive.TryGetProperty("indices",out var idx))
                {var a=Accessor(Int(idx));if(a.Components!=1||a.Component is not(5121 or 5123 or 5125)||a.Normalized)throw new ModelException("glTF indices must be unsigned scalar integers.");indices=new int[a.Count];for(var i=0;i<a.Count;i++){var value=Value(a,i,0);if(value>=position.Count)throw new ModelException("glTF face index is outside POSITION.");indices[i]=(int)value;}}
                else indices=Enumerable.Range(0,position.Count).ToArray();
                var mode=Int(primitive,"mode",4);if(mode is not(4 or 5 or 6)||mode==4&&indices.Length%3!=0||indices.Length<3)throw new ModelException("Use glTF triangles, triangle strips or triangle fans.");
                var material=primitive.TryGetProperty("material",out var mi)?Material(At(materials,Int(mi),"material")):("Material",new[]{1d,1,1,1});
                void Add(int a,int b,int c)
                {
                    if(a==b||b==c||a==c){if(mode!=4)return;throw new ModelException("Degenerate glTF triangle indices.");}
                    if(triangles.Count>=ModelLimits.MaxTriangles)throw new ModelException("glTF exceeds triangle limits.");triangles.Add(new(offset+a,offset+b,offset+c));
                    var rgb=new double[3];for(var channel=0;channel<3;channel++)
                    {double value=1;if(color is {} col){value=0;foreach(var vertex in new[]{a,b,c}){for(var k=0;k<col.Components;k++){var v=Value(col,vertex,k);if(v<0||v>1)throw new ModelException("glTF vertex colors need channels from zero to one.");if(k==channel)value+=v/3;}}}rgb[channel]=value*material.Item2[channel];}
                    var resolved=new SurfaceMaterial(material.Item1,new(Srgb(rgb[0]),Srgb(rgb[1]),Srgb(rgb[2])));palette.Add(resolved);if(palette.Count>ModelLimits.MaxMaterials)throw new ModelException("glTF exceeds 1,000 flat colors/materials.");assigned.Add(resolved);
                }
                if(mode==4)for(var i=0;i<indices.Length;i+=3)Add(indices[i],indices[i+1],indices[i+2]);
                else for(var i=2;i<indices.Length;i++){if(mode==6)Add(indices[0],indices[i-1],indices[i]);else if(i%2==0)Add(indices[i-2],indices[i-1],indices[i]);else Add(indices[i-1],indices[i-2],indices[i]);}
            }
            if(triangles.Count==0)throw new ModelException("glTF mesh has no usable triangles.");var fallback=assigned[0];var faces=ImmutableDictionary.CreateBuilder<int,SurfaceMaterial>();for(var i=0;i<assigned.Count;i++)if(assigned[i]!=fallback)faces.Add(i,assigned[i]);
            cached=(new(vertices,triangles),fallback,faces.ToImmutable());meshCache.Add(index,cached);return cached;
        }
        private Affine Transform(JsonElement node)
        {
            if(Has(node,"skin")||Has(node,"weights"))throw new ModelException("Skinned/morphed glTF nodes are not supported.");
            if(Has(node,"matrix"))
            {
                if(Has(node,"translation")||Has(node,"rotation")||Has(node,"scale"))throw new ModelException("glTF nodes cannot combine matrix and TRS.");var m=Vector(node,"matrix",16,[]);if(m[3]!=0||m[7]!=0||m[11]!=0||m[15]!=1)throw new ModelException("glTF node matrices must be affine.");return new(new(m[0],m[1],m[2]),new(m[4],m[5],m[6]),new(m[8],m[9],m[10]),new(m[12],m[13],m[14]));
            }
            var t=Vector(node,"translation",3,[0,0,0]);var s=Vector(node,"scale",3,[1,1,1]);var q=Vector(node,"rotation",4,[0,0,0,1]);var norm=q.Sum(v=>v*v);if(Math.Abs(norm-1)>.00001)throw new ModelException("glTF rotations need normalized quaternions.");
            var x=q[0];var y=q[1];var z=q[2];var w=q[3];var rotation=new Affine(new(1-2*(y*y+z*z),2*(x*y+z*w),2*(x*z-y*w)),new(2*(x*y-z*w),1-2*(x*x+z*z),2*(y*z+x*w)),new(2*(x*z+y*w),2*(y*z-x*w),1-2*(x*x+y*y)),new());
            return new Affine(new(s[0],0,0),new(0,s[1],0),new(0,0,s[2]),new()).Then(rotation).Then(Affine.Translation(new(t[0],t[1],t[2])));
        }
        internal Scene Read()
        {
            var local=nodes.Select(Transform).ToArray();var children=nodes.Select(n=>Array(n,"children",4000).Select(Int).ToArray()).ToArray();var parents=new int[nodes.Length];
            foreach(var list in children)foreach(var child in list){_=At(nodes,child,"node");if(++parents[child]>1)throw new ModelException("glTF nodes must form a tree without multiple parents.");}
            var marks=new byte[nodes.Length];void Cycle(int index,int depth){if(depth>64||marks[index]==1)throw new ModelException("Cyclic or overly deep glTF node tree.");if(marks[index]==2)return;marks[index]=1;foreach(var child in children[index])Cycle(child,depth+1);marks[index]=2;}for(var i=0;i<nodes.Length;i++)Cycle(i,0);
            var scenes=Array(root,"scenes",1000);var selected=At(scenes,Int(root,"scene"),"scene");var roots=Array(selected,"nodes",4000).Select(Int).ToArray();if(roots.Distinct().Count()!=roots.Length)throw new ModelException("Duplicate glTF scene roots.");
            var objects=ImmutableArray.CreateBuilder<SceneObject>();var groups=ImmutableArray.CreateBuilder<SceneGroup>();var seen=new HashSet<int>();var vertices=0;var triangles=0;
            var convert=new Affine(new(factor,0,0),new(0,0,factor),new(0,-factor,0),new());
            void Visit(int index,Affine parent,List<Guid> members)
            {
                ct.ThrowIfCancellationRequested();if(!seen.Add(index))throw new ModelException("glTF scene contains overlapping node roots.");var node=At(nodes,index,"node");var world=local[index].Then(parent);
                if(node.TryGetProperty("mesh",out var meshIndex))
                {
                    var m=Int(meshIndex);var source=Mesh(m);vertices+=source.Mesh.Vertices.Length;triangles+=source.Mesh.Triangles.Length;if(objects.Count>=ModelLimits.MaxObjects||vertices>ModelLimits.MaxVertices||triangles>ModelLimits.MaxTriangles)throw new ModelException("Expanded glTF instances exceed scene limits.");
                    var obj=new SceneObject(Guid.NewGuid(),Name(node,Name(At(meshes,m,"mesh"),"Object")),source.Mesh,world.Then(convert)){Material=source.Base,TriangleMaterials=source.Faces};objects.Add(obj);members.Add(obj.Id);progress?.Report(new("Packing glTF instances",objects.Count));ct.ThrowIfCancellationRequested();
                }
                foreach(var child in children[index])Visit(child,world,members);
            }
            foreach(var index in roots){_=At(nodes,index,"scene root");if(parents[index]!=0)throw new ModelException("glTF scene roots cannot have parents.");var members=new List<Guid>();Visit(index,Affine.Identity,members);if(members.Count>=2)groups.Add(new(Guid.NewGuid(),Name(nodes[index],"Group"),members.ToImmutableArray()));}
            if(objects.Count==0)throw new ModelException("The selected glTF scene has no static triangle meshes.");var scene=new Scene(Guid.NewGuid(),objects.ToImmutable()){Groups=groups.ToImmutable()};scene.Validate(ct);return scene;
        }
    }
    internal static void WriteAtomic(string path,Scene scene,ModelUnit unit,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var factor=ModelFiles.UnitFactor(unit);var glb=Path.GetExtension(path).Equals(".glb",StringComparison.OrdinalIgnoreCase);if(!glb&&!Path.GetExtension(path).Equals(".gltf",StringComparison.OrdinalIgnoreCase))throw new ModelException("Export glTF as .gltf or .glb.");if(scene.Objects.IsEmpty)throw new ModelException("Add or restore an object before glTF export.");
        var materialMap=new Dictionary<SurfaceMaterial,int>();foreach(var obj in scene.Objects)foreach(var material in obj.TriangleMaterials.Values.Concat([obj.Material??SurfaceMaterial.Neutral]))if(!materialMap.ContainsKey(material))materialMap.Add(material,materialMap.Count);if(materialMap.Count>ModelLimits.MaxMaterials)throw new ModelException("glTF supports up to 1,000 exported flat materials including neutral.");
        var views=new List<object>();var accessors=new List<object>();var meshes=new List<object>();var nodes=new List<Dictionary<string,object>>();var objectNodes=new Dictionary<Guid,int>();
        using var buffer=new MemoryStream();using(var writer=new BinaryWriter(buffer,Encoding.UTF8,true))
        {
            foreach(var obj in scene.Objects)
            {
                progress?.Report(new("Packing glTF geometry",meshes.Count,scene.Objects.Length));ct.ThrowIfCancellationRequested();var start=(int)buffer.Position;var min=new[]{float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity};var max=new[]{float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity};
                foreach(var vertex in obj.Mesh.Vertices){ct.ThrowIfCancellationRequested();var p=obj.Transform.Apply(vertex)*(1/factor);var values=new[]{(float)p.X,(float)p.Z,(float)-p.Y};for(var c=0;c<3;c++){if(!float.IsFinite(values[c]))throw new ModelException("glTF float positions cannot represent these coordinates.");writer.Write(values[c]);min[c]=Math.Min(min[c],values[c]);max[c]=Math.Max(max[c],values[c]);}}
                var view=views.Count;views.Add(new{buffer=0,byteOffset=start,byteLength=(int)buffer.Position-start,target=34962});var position=accessors.Count;accessors.Add(new{bufferView=view,componentType=5126,count=obj.Mesh.Vertices.Length,type="VEC3",min,max});var primitives=new List<object>();
                var buckets=Enumerable.Range(0,obj.Mesh.Triangles.Length).GroupBy(i=>obj.GetTriangleMaterial(i)??SurfaceMaterial.Neutral);var reflected=ModelFiles.Determinant(obj.Transform)<0;
                foreach(var bucket in buckets)
                {
                    start=(int)buffer.Position;var count=0;foreach(var index in bucket){if(count%1024==0)ct.ThrowIfCancellationRequested();var t=obj.Mesh.Triangles[index];writer.Write((uint)t.A);writer.Write((uint)(reflected?t.C:t.B));writer.Write((uint)(reflected?t.B:t.C));count+=3;}
                    view=views.Count;views.Add(new{buffer=0,byteOffset=start,byteLength=(int)buffer.Position-start,target=34963});var accessor=accessors.Count;accessors.Add(new{bufferView=view,componentType=5125,count,type="SCALAR"});primitives.Add(new{attributes=new{POSITION=position},indices=accessor,material=materialMap[bucket.Key],mode=4});
                }
                meshes.Add(new{name=obj.Name,primitives});objectNodes.Add(obj.Id,nodes.Count);nodes.Add(new(){["name"]=obj.Name,["mesh"]=meshes.Count-1,["scale"]=new[]{factor/1000,factor/1000,factor/1000}});
            }
        }
        if(views.Count>10000||accessors.Count>10000)throw new ModelException("Export exceeds 10,000 glTF buffer views/accessors. Reduce distinct surface assignments first.");
        var rootNodes=new List<int>();var grouped=new HashSet<Guid>();foreach(var group in scene.Groups){foreach(var id in group.ObjectIds)grouped.Add(id);rootNodes.Add(nodes.Count);nodes.Add(new(){["name"]=group.Name,["children"]=group.ObjectIds.Select(id=>objectNodes[id]).ToArray()});}foreach(var obj in scene.Objects)if(!grouped.Contains(obj.Id))rootNodes.Add(objectNodes[obj.Id]);
        var data=buffer.ToArray();var declaration=new Dictionary<string,object>{{"byteLength",data.Length}};if(!glb)declaration.Add("uri","data:application/octet-stream;base64,"+Convert.ToBase64String(data));
        var document=new{asset=new{version="2.0",generator="3D BuilderCrap"},scene=0,scenes=new[]{new{nodes=rootNodes}},nodes,meshes,materials=materialMap.Keys.Select(m=>new{name=m.Name,pbrMetallicRoughness=new{baseColorFactor=new[]{Linear(m.Color.R),Linear(m.Color.G),Linear(m.Color.B),1d},metallicFactor=0,roughnessFactor=1},alphaMode="OPAQUE"}).ToArray(),buffers=new[]{declaration},bufferViews=views,accessors};
        var json=JsonSerializer.SerializeToUtf8Bytes(document);var jsonLength=(json.Length+3)&~3;var binLength=(data.Length+3)&~3;var length=glb?12L+8+jsonLength+8+binLength:json.Length;if(length>ModelLimits.MaxFileBytes)throw new ModelException("Exported glTF exceeds 64 MiB.");
        var temporary=Path.Combine(Path.GetDirectoryName(path)!,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");var own=false;
        try
        {
            ct.ThrowIfCancellationRequested();using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                own=true;using(var writer=new BinaryWriter(file,Encoding.UTF8,true))
                {
                    if(glb){writer.Write(0x46546c67u);writer.Write(2u);writer.Write((uint)length);writer.Write((uint)jsonLength);writer.Write(0x4e4f534au);writer.Write(json);for(var i=json.Length;i<jsonLength;i++)writer.Write((byte)32);writer.Write((uint)binLength);writer.Write(0x004e4942u);writer.Write(data);for(var i=data.Length;i<binLength;i++)writer.Write((byte)0);}
                    else writer.Write(json);writer.Flush();
                }file.Flush(true);
            }
            progress?.Report(new("Ready to export glTF"));ct.ThrowIfCancellationRequested();File.Move(temporary,path,true);
        }
        finally{if(own&&File.Exists(temporary))File.Delete(temporary);}
    }
}
