using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Builder.Core;

/// <summary>Bounded OBJ polygon meshes and opaque diffuse MTL. Normals/UVs/smoothing/render-only
/// MTL properties are validated but not retained. OBJ has no physical unit or document-metadata model.</summary>
internal static class ObjFiles
{
    private sealed class Part(string name,string? group)
    {
        internal string Name=name;internal string? Group=group;
        internal List<(Triangle Triangle,string? Material)> Faces=[];
    }
    private static double Number(string text)
    {
        if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))throw new ModelException("OBJ/MTL numbers must be finite.");return value;
    }
    private static string Format(double value)=>value.ToString("G17",CultureInfo.InvariantCulture);
    private static IEnumerable<(string Key,string Value)> Lines(Stream stream,CancellationToken ct)
    {
        using var reader=new StreamReader(stream,new UTF8Encoding(false,true),true,4096,true);string? line;
        while((line=reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();if(line.Length>65536)throw new ModelException("OBJ/MTL lines are limited to 65,536 characters.");
            var comment=line.IndexOf('#');if(comment>=0)line=line[..comment];line=line.Trim();if(line.Length==0)continue;
            if(line.EndsWith('\\'))throw new ModelException("OBJ line continuations are not supported; use ordinary polygon lines.");
            var split=line.IndexOfAny([' ','\t']);yield return split<0?(line,""):(line[..split],line[(split+1)..].Trim());
        }
    }
    private static string[] Words(string text)=>text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
    private static string Name(string text,string label)
    {
        text=text.Trim();if(text.Length is <1 or >100||text.Any(char.IsControl))throw new ModelException(label+" needs 1–100 characters without controls.");return text;
    }
    internal static Scene Read(string path,Stream stream,ModelUnit unit,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var factor=ModelFiles.UnitFactor(unit);var vertices=new List<Vec3>();var textureCount=0;var normalCount=0;var triangleCount=0;
        var parts=new List<Part>();var name=Path.GetFileNameWithoutExtension(path);if(name.Length>100){var length=char.IsHighSurrogate(name[99])?99:100;name=name[..length];}string? group=null;string? activeMaterial=null;var current=new Part(name,group);
        var materials=new Dictionary<string,SurfaceMaterial>(StringComparer.Ordinal);var libraries=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var sourceBytes=stream.Length;
        void Flush(){if(current.Faces.Count>0){parts.Add(current);if(parts.Count>ModelLimits.MaxObjects)throw new ModelException("OBJ exceeds the 1,000-object limit.");}current=new(name,group);}
        int Index(string text,int count)
        {
            if(!int.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)||n==0)throw new ModelException("OBJ indices must be nonzero integers.");
            var index=n>0?n-1:(long)count+n;if(index<0||index>=count)throw new ModelException("OBJ face index refers to an unavailable coordinate.");return(int)index;
        }
        foreach(var (key,value) in Lines(stream,ct))
        {
            switch(key)
            {
                case "v":
                    var xyz=Words(value);if(xyz.Length is not (3 or 4)||xyz.Length==4&&Number(xyz[3])!=1)throw new ModelException("OBJ vertices need XYZ and optional homogeneous W=1; vertex colors are not supported.");
                    var point=new Vec3(Number(xyz[0]),Number(xyz[1]),Number(xyz[2]))*factor;_=Bounds3.From([point]);vertices.Add(point);
                    if(vertices.Count>ModelLimits.MaxVertices)throw new ModelException("OBJ exceeds the 300,000-vertex limit.");break;
                case "vt":
                    var uv=Words(value);if(uv.Length is <1 or >3)throw new ModelException("Invalid OBJ texture-coordinate declaration.");foreach(var number in uv)_=Number(number);
                    if(++textureCount>ModelLimits.MaxVertices)throw new ModelException("Too many OBJ texture coordinates.");break;
                case "vn":
                    var normal=Words(value);if(normal.Length!=3)throw new ModelException("Invalid OBJ normal declaration.");foreach(var number in normal)_=Number(number);
                    if(++normalCount>ModelLimits.MaxVertices)throw new ModelException("Too many OBJ normals.");break;
                case "s":
                    if(value is not ("off" or "on")&&(!int.TryParse(value,out var smoothing)||smoothing<0))throw new ModelException("Invalid OBJ smoothing declaration.");break;
                case "o":Flush();name=Name(value,"OBJ object name");current=new(name,group);break;
                case "g":
                    Flush();var groups=Words(value);if(groups.Length>1)throw new ModelException("Multiple overlapping OBJ group memberships are not supported. Use one group identifier.");
                    group=groups.Length==0?null:Name(groups[0],"OBJ group name");current=new(name,group);break;
                case "mtllib":
                    var library=Name(value,"MTL filename");
                    if(Path.GetFileName(library)!=library||library.IndexOfAny(['/', '\\', ':'])>=0||Path.IsPathRooted(library)||!Path.GetExtension(library).Equals(".mtl",StringComparison.OrdinalIgnoreCase))
                        throw new ModelException("MTL companions must be simple .mtl filenames in the OBJ directory; external paths are not supported.");
                    var full=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,library));
                    if(full.StartsWith("\\\\",StringComparison.Ordinal)||File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))throw new ModelException("Network or linked MTL companions are not supported.");
                    if(libraries.Add(full))
                    {
                        sourceBytes+=new FileInfo(full).Length;
                        if(libraries.Count>32||sourceBytes>ModelLimits.MaxFileBytes)throw new ModelException("OBJ and MTL companions together are limited to 64 MiB and 32 libraries.");
                        ReadMaterials(full,materials,ct);
                    }break;
                case "usemtl":activeMaterial=Name(value,"OBJ material name");break;
                case "f":
                    var refs=Words(value);if(refs.Length is <3 or >256)throw new ModelException("OBJ faces need 3–256 vertices.");var polygon=new List<int>(refs.Length);
                    foreach(var reference in refs)
                    {
                        var indices=reference.Split('/');if(indices.Length is <1 or >3||indices[0].Length==0)throw new ModelException("Invalid OBJ face reference.");polygon.Add(Index(indices[0],vertices.Count));
                        if(indices.Length>=2&&indices[1].Length>0)_=Index(indices[1],textureCount);else if(indices.Length==2)throw new ModelException("Missing OBJ texture-coordinate index.");
                        if(indices.Length==3){if(indices[2].Length==0)throw new ModelException("Missing OBJ normal index.");_=Index(indices[2],normalCount);}
                    }
                    foreach(var triangle in PolygonTriangulation.Triangulate(polygon,vertices))
                    {if(++triangleCount>ModelLimits.MaxTriangles)throw new ModelException("OBJ exceeds the 100,000-triangle limit.");current.Faces.Add((triangle,activeMaterial));}
                    if(triangleCount%128==0){progress?.Report(new("Reading OBJ faces",triangleCount,ModelLimits.MaxTriangles));ct.ThrowIfCancellationRequested();}break;
                default:throw new ModelException("Unsupported OBJ feature: "+key+". Use plain polygon geometry and diffuse materials.");
            }
        }
        Flush();if(parts.Count==0)throw new ModelException("OBJ contains no polygon faces.");
        var objects=ImmutableArray.CreateBuilder<SceneObject>();var groupMembers=new Dictionary<string,List<Guid>>(StringComparer.Ordinal);var totalVertices=0;
        foreach(var part in parts)
        {
            progress?.Report(new("Packing OBJ objects",objects.Count,parts.Count));ct.ThrowIfCancellationRequested();
            var local=new List<Vec3>();var triangles=new List<Triangle>();var remap=new Dictionary<int,int>();
            int Local(int index){if(!remap.TryGetValue(index,out var mapped)){mapped=local.Count;remap.Add(index,mapped);local.Add(vertices[index]);}return mapped;}
            var assigned=part.Faces.Select(f=>f.Material is null?null:materials.GetValueOrDefault(f.Material)??throw new ModelException("OBJ references a missing MTL material: "+f.Material)).ToArray();
            var fallback=assigned.All(m=>m is null)?null:assigned.Any(m=>m is null)?SurfaceMaterial.Neutral:assigned[0];var overrides=ImmutableDictionary.CreateBuilder<int,SurfaceMaterial>();
            for(var i=0;i<part.Faces.Count;i++){ct.ThrowIfCancellationRequested();var t=part.Faces[i].Triangle;triangles.Add(new(Local(t.A),Local(t.B),Local(t.C)));if(assigned[i] is {} m&&m!=fallback)overrides.Add(i,m);}
            totalVertices+=local.Count;if(totalVertices>ModelLimits.MaxVertices)throw new ModelException("Expanded OBJ objects exceed the vertex limit.");
            var obj=new SceneObject(Guid.NewGuid(),part.Name,new(local,triangles),Affine.Identity){Material=fallback,TriangleMaterials=overrides.ToImmutable()};objects.Add(obj);
            if(part.Group is {} label){if(!groupMembers.TryGetValue(label,out var members))groupMembers.Add(label,members=[]);members.Add(obj.Id);}
        }
        var scene=new Scene(Guid.NewGuid(),objects.ToImmutable()){Groups=groupMembers.Where(p=>p.Value.Count>=2).Select(p=>new SceneGroup(Guid.NewGuid(),p.Key,p.Value.ToImmutableArray())).ToImmutableArray()};scene.Validate(ct);return scene;
    }
    private static void ReadMaterials(string path,Dictionary<string,SurfaceMaterial> materials,CancellationToken ct)
    {
        using var stream=File.OpenRead(path);if(stream.Length>ModelLimits.MaxFileBytes)throw new ModelException("MTL companions are limited to 64 MiB.");string? name=null;RgbColor? color=null;
        void Finish(){if(name is null)return;if(color is null)throw new ModelException("MTL materials need an explicit diffuse Kd color.");if(!materials.TryAdd(name,new(name,color.Value)))throw new ModelException("Duplicate MTL material names are ambiguous.");if(materials.Count>ModelLimits.MaxMaterials)throw new ModelException("MTL exceeds 1,000 materials.");}
        foreach(var (key,value) in Lines(stream,ct))
        {
            if(key=="newmtl"){Finish();name=Name(value,"MTL material name");color=null;continue;}
            if(name is null)throw new ModelException("MTL properties need a preceding newmtl declaration.");var numbers=Words(value);
            switch(key)
            {
                case "Kd":if(numbers.Length!=3)throw new ModelException("MTL Kd needs three diffuse channels.");var rgb=numbers.Select(Number).ToArray();if(rgb.Any(n=>n<0||n>1))throw new ModelException("MTL diffuse channels must be from 0 to 1.");color=new((byte)Math.Round(rgb[0]*255,MidpointRounding.AwayFromZero),(byte)Math.Round(rgb[1]*255,MidpointRounding.AwayFromZero),(byte)Math.Round(rgb[2]*255,MidpointRounding.AwayFromZero));break;
                case "d":case "Tr":if(numbers.Length!=1||Number(numbers[0])!=(key=="d"?1:0))throw new ModelException("Transparent MTL materials are not supported.");break;
                case "Ka":case "Ks":case "Ke":if(numbers.Length!=3)throw new ModelException("Invalid MTL render-only color.");foreach(var n in numbers)_=Number(n);break;
                case "Ns":case "Ni":case "illum":if(numbers.Length!=1)throw new ModelException("Invalid MTL render-only property.");_=Number(numbers[0]);break;
                default:throw new ModelException("Unsupported MTL property or texture: "+key+". Only opaque diffuse colors are retained.");
            }
        }
        Finish();
    }
    internal static void WriteAtomic(string path,Scene scene,ModelUnit unit,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var factor=ModelFiles.UnitFactor(unit);if(!Path.GetExtension(path).Equals(".obj",StringComparison.OrdinalIgnoreCase))throw new ModelException("Use .obj for OBJ export.");if(scene.Objects.IsEmpty)throw new ModelException("Add or restore an object before OBJ export.");
        var directory=Path.GetDirectoryName(path)!;var unique=Guid.NewGuid().ToString("N");var sidecar=Path.Combine(directory,"materials-"+unique+".mtl");var temporary=Path.Combine(directory,"."+Path.GetFileName(path)+"."+unique+".tmp");var committed=false;var ownSidecar=false;var ownTemporary=false;
        try
        {
            progress?.Report(new("Preparing OBJ export"));ct.ThrowIfCancellationRequested();var materials=new Dictionary<SurfaceMaterial,string>();
            string Safe(string value)=>string.Concat(value.Select(c=>char.IsWhiteSpace(c)||c is '#' or '\\'?'_':c));
            string UniqueIdentifier(string value,HashSet<string> used)
            {
                value=Safe(value);if(value.Length==0)value="Material";
                static string Truncate(string text,int max){var length=Math.Min(text.Length,max);if(length>0&&char.IsHighSurrogate(text[length-1]))length--;return text[..length];}
                var stem=Truncate(value,100);var result=stem;var n=2;
                while(!used.Add(result)){var suffix="_"+n++;result=Truncate(stem,100-suffix.Length)+suffix;}return result;
            }
            string ObjectLabel(string value)
            {
                value=string.Concat(value.Select(c=>char.IsControl(c)||c is '#' or '\\'?'_':c)).Trim();if(value.Length==0)value="Object";
                var length=Math.Min(value.Length,100);if(length>0&&char.IsHighSurrogate(value[length-1]))length--;return value[..length];
            }
            var materialNames=new HashSet<string>(StringComparer.Ordinal);
            foreach(var obj in scene.Objects)foreach(var material in obj.TriangleMaterials.Values.Concat([obj.Material??SurfaceMaterial.Neutral]))
                if(!materials.ContainsKey(material))materials.Add(material,UniqueIdentifier(material.Name,materialNames));
            if(materials.Count>ModelLimits.MaxMaterials)throw new ModelException("OBJ export supports up to 1,000 materials including the neutral display fallback.");
            using(var file=new FileStream(sidecar,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                ownSidecar=true;using(var writer=new StreamWriter(file,new UTF8Encoding(false),4096,true))
                {
                    writer.WriteLine("# 3D BuilderCrap opaque diffuse display colors");foreach(var (material,name) in materials){ct.ThrowIfCancellationRequested();writer.WriteLine("newmtl "+name);writer.WriteLine("Kd "+Format(material.Color.R/255d)+" "+Format(material.Color.G/255d)+" "+Format(material.Color.B/255d));writer.WriteLine("d 1");}writer.Flush();
                }file.Flush(true);
            }
            progress?.Report(new("OBJ material companion staged"));ct.ThrowIfCancellationRequested();
            var usedGroups=new HashSet<string>(StringComparer.Ordinal);var groupNames=scene.Groups.Select(g=>(g.Id,Name:UniqueIdentifier(g.Name,usedGroups))).ToDictionary(p=>p.Id,p=>p.Name);var membership=scene.Groups.SelectMany(g=>g.ObjectIds.Select(id=>(id,g.Id))).ToDictionary(p=>p.id,p=>p.Id);
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                ownTemporary=true;
                using(var writer=new StreamWriter(file,new UTF8Encoding(false),4096,true))
                {
                    writer.WriteLine("# 3D BuilderCrap; unitless coordinates exported in "+unit+"; native 3MF retains model information.");writer.WriteLine("mtllib "+Path.GetFileName(sidecar));var offset=1;var done=0;
                    foreach(var obj in scene.Objects)
                    {
                        ct.ThrowIfCancellationRequested();writer.WriteLine("o "+ObjectLabel(obj.Name));writer.WriteLine("g "+(membership.TryGetValue(obj.Id,out var group)?groupNames[group]:""));
                        foreach(var local in obj.Mesh.Vertices){ct.ThrowIfCancellationRequested();var p=obj.Transform.Apply(local)*(1/factor);writer.WriteLine("v "+Format(p.X)+" "+Format(p.Y)+" "+Format(p.Z));}
                        SurfaceMaterial? previous=null;var first=true;var reflected=ModelFiles.Determinant(obj.Transform)<0;
                        for(var i=0;i<obj.Mesh.Triangles.Length;i++)
                        {
                            if(done++%512==0){progress?.Report(new("Writing OBJ triangles",done,scene.TriangleCount));ct.ThrowIfCancellationRequested();}
                            var material=obj.GetTriangleMaterial(i)??SurfaceMaterial.Neutral;if(first||material!=previous){writer.WriteLine("usemtl "+materials[material]);first=false;previous=material;}
                            var t=obj.Mesh.Triangles[i];writer.WriteLine("f "+(offset+t.A).ToString(CultureInfo.InvariantCulture)+" "+(offset+(reflected?t.C:t.B)).ToString(CultureInfo.InvariantCulture)+" "+(offset+(reflected?t.B:t.C)).ToString(CultureInfo.InvariantCulture));
                        }
                        offset+=obj.Mesh.Vertices.Length;
                    }writer.Flush();
                }file.Flush(true);
            }
            progress?.Report(new("Ready to export OBJ"));ct.ThrowIfCancellationRequested();File.Move(temporary,path,true);committed=true;
        }
        finally
        {
            if(ownTemporary&&File.Exists(temporary))File.Delete(temporary);
            if(!committed&&ownSidecar&&File.Exists(sidecar))File.Delete(sidecar);
        }
    }
}
