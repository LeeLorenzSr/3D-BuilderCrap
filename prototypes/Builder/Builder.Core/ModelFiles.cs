using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Builder.Core;

internal static class ModelFiles
{
    private static readonly XNamespace Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ModelRelationship = "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel";
    internal static double UnitFactor(ModelUnit unit)=>unit switch
    {ModelUnit.Millimeters=>1,ModelUnit.Centimeters=>10,ModelUnit.Inches=>25.4,ModelUnit.Meters=>1000,_=>throw new ModelException("Choose valid model units.")};
    private static string Number(double value) => value.ToString("G17",CultureInfo.InvariantCulture);
    private static double Parse(string value) => double.Parse(value,NumberStyles.Float,CultureInfo.InvariantCulture);
    private static string Required(XElement e, string attribute) => (string?)e.Attribute(attribute)
        ?? throw new ModelException($"Missing {attribute} on {e.Name.LocalName}.");
    private static int Index(XElement e,string name) => int.Parse(Required(e,name),CultureInfo.InvariantCulture);
    private static void Check(CancellationToken token, IProgress<OperationProgress>? progress, string stage, int count = 0, int total = 0)
    { progress?.Report(new(stage,count,total)); token.ThrowIfCancellationRequested(); }

    public static Scene Read(string path, ModelUnit? unit, CancellationToken token, IProgress<OperationProgress>? progress,StlImportMode stlMode=StlImportMode.Strict)
    {
        Check(token,progress,"Reading model");
        if(!Enum.IsDefined(stlMode))throw new ModelException("Choose Strict or Geometry only for STL import.");
        using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (stream.Length > ModelLimits.MaxFileBytes) throw new ModelException("This prototype opens files up to 64 MiB.");
        var extension=Path.GetExtension(path).ToLowerInvariant();
        var resolvedUnit=unit??(extension is ".gltf" or ".glb"?ModelUnit.Meters:ModelUnit.Millimeters);
        return extension switch {
            ".stl" => new Scene(Guid.NewGuid(),[new(Guid.NewGuid(),Path.GetFileNameWithoutExtension(path),ReadStl(stream,resolvedUnit,token,progress,stlMode),Affine.Identity)]),
            ".3mf" => Read3mf(stream,token,progress),
            ".obj" => ObjFiles.Read(path,stream,resolvedUnit,token,progress),
            ".ply" => PlyFiles.Read(path,stream,resolvedUnit,token,progress),
            ".gltf" or ".glb" => GltfFiles.Read(path,stream,resolvedUnit,token,progress),
            _ => throw new ModelException("Open STL, OBJ, PLY, glTF/GLB or a supported 3MF file.") };
    }

    private static Mesh ReadStl(Stream stream, ModelUnit unit, CancellationToken token, IProgress<OperationProgress>? progress,StlImportMode stlMode)
    {
        var factor = UnitFactor(unit);
        List<Vec3> vertices = []; List<Triangle> triangles = [];
        using var reader = new BinaryReader(stream,Encoding.UTF8,true);
        uint count = 0;
        if (stream.Length >= 84) { stream.Position = 80; count = reader.ReadUInt32(); }
        if (stream.Length >= 84 && stream.Length == 84L + 50L*count)
        {
            if (count > ModelLimits.MaxTriangles) throw new ModelException("This prototype opens up to 100,000 triangles.");
            for (int i=0; i<count; i++)
            {
                if (i % 1024 == 0) Check(token,progress,"Reading triangles",i,(int)count);
                reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle();
                for (int j=0;j<3;j++) vertices.Add(new Vec3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle())*factor);
                if (reader.ReadUInt16() != 0 && stlMode==StlImportMode.Strict) throw new ModelException("This binary STL has nonzero attribute/color data. Choose Geometry only when importing to ignore it explicitly.");
                triangles.Add(new(i*3,i*3+1,i*3+2));
            }
        }
        else
        {
            stream.Position = 0;
            using var text = new StreamReader(stream,Encoding.UTF8,true,4096,true);
            string? Next()
            {
                token.ThrowIfCancellationRequested();
                string? line;
                do { line = text.ReadLine(); } while(line != null && string.IsNullOrWhiteSpace(line));
                return line?.Trim();
            }
            static string[] Words(string line) => line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            void Expect(string expected)
            { if (!string.Equals(Next(),expected,StringComparison.OrdinalIgnoreCase)) throw new ModelException("Malformed ASCII STL: expected " + expected + "."); }
            var first = Next();
            if (first == null || !Words(first)[0].Equals("solid",StringComparison.OrdinalIgnoreCase))
                throw new ModelException("The STL is truncated or has an invalid triangle count.");
            while(true)
            {
                var line = Next() ?? throw new ModelException("ASCII STL is missing endsolid.");
                var words = Words(line);
                if (words[0].Equals("endsolid",StringComparison.OrdinalIgnoreCase))
                {
                    if (Next() != null) throw new ModelException("Multiple ASCII STL solids are not supported yet.");
                    break;
                }
                if (words.Length != 5 || !words[0].Equals("facet",StringComparison.OrdinalIgnoreCase) || !words[1].Equals("normal",StringComparison.OrdinalIgnoreCase))
                    throw new ModelException("Malformed ASCII STL facet.");
                if (triangles.Count >= ModelLimits.MaxTriangles) throw new ModelException("This prototype opens up to 100,000 triangles.");
                Expect("outer loop");
                for (int j=0;j<3;j++)
                {
                    var v = Words(Next() ?? "");
                    if (v.Length != 4 || !v[0].Equals("vertex",StringComparison.OrdinalIgnoreCase)) throw new ModelException("Malformed ASCII STL vertex.");
                    vertices.Add(new Vec3(Parse(v[1]),Parse(v[2]),Parse(v[3]))*factor);
                }
                Expect("endloop"); Expect("endfacet");
                var start = vertices.Count-3; triangles.Add(new(start,start+1,start+2));
                if (triangles.Count % 1024 == 0) Check(token,progress,"Reading triangles",triangles.Count);
            }
        }
        return new(vertices,triangles);
    }

    private static XDocument ReadXml(ZipArchiveEntry entry, CancellationToken token)
    {
        if (entry.Length > ModelLimits.MaxFileBytes) throw new ModelException("A 3MF XML part exceeds the 64 MiB prototype limit.");
        using var source = entry.Open();
        // Poll between stream reads as well as mesh loops; DTDs and external resolution are disabled.
        using var cancellable = new CancellationReadStream(source,token);
        using var reader = XmlReader.Create(cancellable,new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ModelLimits.MaxFileBytes });
        return XDocument.Load(reader,LoadOptions.PreserveWhitespace);
    }
    private static Scene Read3mf(Stream stream, CancellationToken token, IProgress<OperationProgress>? progress)
    {
        using var zip = new ZipArchive(stream,ZipArchiveMode.Read,true);
        if (zip.Entries.Count > 1000) throw new ModelException("The 3MF package has too many parts for this prototype.");
        var rels = ReadXml(zip.GetEntry("_rels/.rels") ?? throw new ModelException("3MF package relationships are missing."),token);
        var starts = rels.Root?.Elements(Relationships+"Relationship").Where(e => (string?)e.Attribute("Type") == ModelRelationship).ToArray() ?? [];
        if (starts.Length != 1 || (string?)starts[0].Attribute("TargetMode") == "External")
            throw new ModelException("3MF needs one local primary model.");
        var target = Uri.UnescapeDataString(Required(starts[0],"Target")).TrimStart('/');
        var model = ReadXml(zip.GetEntry(target) ?? throw new ModelException("The primary 3MF model part is missing."),token).Root;
        if (model?.Name != Core+"model") throw new ModelException("Unrecognized 3MF model namespace.");
        if (!string.IsNullOrWhiteSpace((string?)model.Attribute("requiredextensions")))
            throw new ModelException("This 3MF requires extensions that the prototype does not support yet.");
        foreach (var e in model.DescendantsAndSelf())
        {
            token.ThrowIfCancellationRequested();
            if (e.Name.Namespace != Core || e.Attributes().Any(a => !a.IsNamespaceDeclaration &&
                a.Name.Namespace != XNamespace.None && a.Name.Namespace != XNamespace.Xml))
                throw new ModelException("This 3MF uses extension data. Open a geometry-only copy for now.");
            if(e.Name==Core+"metadatagroup" || e.Name==Core+"metadata"&&e.Parent!=model)
                throw new ModelException("Object- or build-scoped metadata is not supported. Keep model information at the model root.");
            if (e.Name!=Core+"metadata" && (string?)e.Attribute("preserve") is "true" or "1")
                throw new ModelException("This 3MF contains metadata that must be preserved, which is not supported yet.");
            if(e.Name==Core+"triangle" && e.Attributes().Any(a=>a.Name.LocalName is not ("v1" or "v2" or "v3" or "pid" or "p1" or "p2" or "p3")))
                throw new ModelException("Unsupported triangle properties.");
            if(e.Name==Core+"component" && (e.Attribute("pid") is not null || e.Attribute("pindex") is not null))
                throw new ModelException("3MF component references cannot carry material defaults.");
        }
        var metadataValues=new Dictionary<string,string>(StringComparer.Ordinal);
        var modelLanguage=(string?)model.Attribute(XNamespace.Xml+"lang")??"und";
        if(modelLanguage.Length==0)modelLanguage="und";
        foreach(var entry in model.Elements(Core+"metadata"))
        {
            token.ThrowIfCancellationRequested();var name=Required(entry,"name");
            if(metadataValues.ContainsKey(name))throw new ModelException("Duplicate 3MF model metadata names are not allowed.");
            if(entry.HasElements || entry.Attributes().Any(a=>!a.IsNamespaceDeclaration&&a.Name!=XNamespace.Xml+"lang"&&
                !(a.Name.Namespace==XNamespace.None&&a.Name.LocalName is "name" or "preserve" or "type")))
                throw new ModelException("Model information must be plain text with supported attributes.");
            var preserve=(string?)entry.Attribute("preserve");
            if(preserve is not (null or "true" or "false" or "0" or "1"))throw new ModelException("Invalid metadata preserve attribute.");
            var type=(string?)entry.Attribute("type");
            if(type is not null)
            {
                var parts=type.Split(':');
                if(parts.Length!=2||parts[1]!="string"||entry.GetNamespaceOfPrefix(parts[0])?.NamespaceName!="http://www.w3.org/2001/XMLSchema")
                    throw new ModelException("Only ordinary string model metadata is supported.");
            }
            var language=(string?)entry.Attribute(XNamespace.Xml+"lang");
            if(language is not null&&!language.Equals(modelLanguage,StringComparison.OrdinalIgnoreCase))
                throw new ModelException("Per-field language overrides cannot be retained by this model-information editor.");
            if(name is not ("Title" or "Designer" or "Description" or "Copyright" or "LicenseTerms") && preserve is "true" or "1")
                throw new ModelException("This 3MF contains unknown metadata that must be preserved.");
            metadataValues.Add(name,entry.Value);
        }
        var metadata=ModelMetadata.Canonical(new(metadataValues.GetValueOrDefault("Title",""),metadataValues.GetValueOrDefault("Designer",""),
            metadataValues.GetValueOrDefault("Description",""),metadataValues.GetValueOrDefault("Copyright",""),metadataValues.GetValueOrDefault("LicenseTerms","")){Language=modelLanguage});
        var factor = ((string?)model.Attribute("unit") ?? "millimeter") switch {
            "micron" => 0.001, "millimeter" => 1d, "centimeter" => 10d, "inch" => 25.4, "foot" => 304.8,
            "meter" => 1000d, _ => throw new ModelException("Unknown 3MF units.") };
        var resources = model.Element(Core+"resources") ?? throw new ModelException("3MF resources are missing.");
        if (resources.Elements().Any(e => e.Name != Core+"object" && e.Name != Core+"basematerials"))
            throw new ModelException("Only opaque base materials are supported; textures and other properties are not supported.");
        var resourceIds=new HashSet<int>();
        foreach(var resource in resources.Elements())
            if(Index(resource,"id")<1 || !resourceIds.Add(Index(resource,"id"))) throw new ModelException("Invalid or duplicate 3MF resource ID.");
        var materialGroups=new Dictionary<int,ImmutableArray<SurfaceMaterial>>(); var materialCount=0;
        foreach(var resource in resources.Elements(Core+"basematerials"))
        {
            var materials=ImmutableArray.CreateBuilder<SurfaceMaterial>();
            foreach(var entry in resource.Elements())
            {
                token.ThrowIfCancellationRequested();
                if(entry.Name!=Core+"base" || entry.Attributes().Any(a=>a.Name.LocalName is not ("name" or "displaycolor")))
                    throw new ModelException("Unsupported 3MF base material property.");
                var text=Required(entry,"displaycolor");
                if(text.Length==9 && text.EndsWith("FF",StringComparison.OrdinalIgnoreCase)) text=text[..7];
                if(!RgbColor.TryParse(text,out var color)) throw new ModelException("3MF base materials need opaque #RRGGBB or #RRGGBBFF display colors.");
                materials.Add(SurfaceMaterial.Canonical(new(Required(entry,"name"),color))!);
                if(++materialCount>ModelLimits.MaxMaterials) throw new ModelException("This prototype supports up to 1,000 base material entries.");
            }
            if(materials.Count==0) throw new ModelException("A 3MF base materials resource cannot be empty.");
            materialGroups.Add(Index(resource,"id"),materials.ToImmutable());
        }
        var definitions = new Dictionary<int,XElement>();
        foreach (var e in resources.Elements(Core+"object"))
        {
            var id = Index(e,"id");
            if (id < 1 || !definitions.TryAdd(id,e)) throw new ModelException("Invalid or duplicate 3MF resource ID.");
            if (definitions.Count > ModelLimits.MaxObjects) throw new ModelException("Too many 3MF object resources.");
            if ((string?)e.Attribute("type") is not (null or "model"))
                throw new ModelException("Only model objects are supported in this prototype.");
        }
        var objectMaterials=new Dictionary<int,SurfaceMaterial?>();
        foreach(var (id,definition) in definitions)
        {
            var pid=definition.Attribute("pid"); var index=definition.Attribute("pindex");
            if(definition.Element(Core+"components") is not null && (pid is not null || index is not null))
                throw new ModelException("3MF component objects cannot carry material defaults.");
            if(pid is null && index is null) {objectMaterials.Add(id,null); continue;}
            if(pid is null || index is null || !materialGroups.TryGetValue(Index(definition,"pid"),out var materials))
                throw new ModelException("The 3MF object material reference is missing or invalid.");
            var materialIndex=Index(definition,"pindex");
            if(materialIndex<0 || materialIndex>=materials.Length) throw new ModelException("The 3MF object material index is invalid.");
            objectMaterials.Add(id,materials[materialIndex]);
        }
        // Cache only mesh resources. Components remain instances with their own transforms;
        // baking a component tree into one mesh would destroy editable group membership.
        var cache = new Dictionary<int,Mesh>();
        var faceMaterials=new Dictionary<int,ImmutableDictionary<int,SurfaceMaterial>>();
        SurfaceMaterial? ReadTriangleMaterial(int objectId,XElement triangle)
        {
            if(!triangle.Attributes().Any(a=>a.Name.LocalName is "pid" or "p1" or "p2" or "p3"))return null;
            if(objectMaterials[objectId] is null)throw new ModelException("Triangle properties require object pid and pindex defaults.");
            var definition=definitions[objectId];
            var pid=triangle.Attribute("pid") is null?Index(definition,"pid"):Index(triangle,"pid");
            if(triangle.Attribute("p1") is null)
            {
                if(!materialGroups.ContainsKey(pid))throw new ModelException("The triangle material reference is invalid.");
                if(triangle.Attribute("p2") is not null||triangle.Attribute("p3") is not null)
                    throw new ModelException("Triangle p2 and p3 require an explicit p1 in this supported subset.");
                // Without p1 the default OBJECT property applies, not its index in a different pid group.
                return objectMaterials[objectId];
            }
            var p1=Index(triangle,"p1");
            var p2=triangle.Attribute("p2") is null?p1:Index(triangle,"p2");
            var p3=triangle.Attribute("p3") is null?p1:Index(triangle,"p3");
            if(p1!=p2||p1!=p3)throw new ModelException("Base-material gradients are not supported; triangle material indices must agree.");
            if(!materialGroups.TryGetValue(pid,out var group)||p1<0||p1>=group.Length)throw new ModelException("The triangle material reference is invalid.");
            return group[p1];
        }
        // Refuse unsupported properties even in unused mesh resources instead of silently dropping them.
        foreach(var (id,definition) in definitions)
            foreach(var triangle in definition.Elements(Core+"mesh").Elements(Core+"triangles").Elements(Core+"triangle"))
            { token.ThrowIfCancellationRequested();_=ReadTriangleMaterial(id,triangle); }
        Mesh ReadMesh(int id,XElement mesh)
        {
            if (cache.TryGetValue(id,out var cached)) return cached;
            var v = mesh.Element(Core+"vertices")?.Elements(Core+"vertex").ToArray() ?? [];
            var t = mesh.Element(Core+"triangles")?.Elements(Core+"triangle").ToArray() ?? [];
            if (v.Length > ModelLimits.MaxVertices || t.Length > ModelLimits.MaxTriangles) throw new ModelException("3MF mesh exceeds prototype limits.");
            var points = new List<Vec3>(v.Length); var faces = new List<Triangle>(t.Length);
            var overrides=ImmutableDictionary.CreateBuilder<int,SurfaceMaterial>();
            foreach(var p in v) { token.ThrowIfCancellationRequested(); points.Add(new Vec3(Parse(Required(p,"x")),Parse(Required(p,"y")),Parse(Required(p,"z")))*factor); }
            foreach(var f in t)
            {
                token.ThrowIfCancellationRequested();
                if (f.Attributes().Any(a => a.Name.LocalName is not ("v1" or "v2" or "v3" or "pid" or "p1" or "p2" or "p3")))
                    throw new ModelException("Unsupported triangle properties.");
                var material=ReadTriangleMaterial(id,f);
                if(material is not null && material!=objectMaterials[id])overrides.Add(faces.Count,material);
                faces.Add(new(Index(f,"v1"),Index(f,"v2"),Index(f,"v3")));
            }
            var result=new Mesh(points,faces); cache.Add(id,result); faceMaterials.Add(id,overrides.ToImmutable()); return result;
        }
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var groups = ImmutableArray.CreateBuilder<SceneGroup>();
        long triangleCount = 0, vertexCount = 0;
        var visiting = new HashSet<int>();
        void Expand(int id,Affine outer)
        {
            Check(token,progress,"Reading objects",objects.Count,ModelLimits.MaxObjects);
            if (!definitions.TryGetValue(id,out var definition)) throw new ModelException("3MF object reference is missing.");
            if (visiting.Count >= 32 || !visiting.Add(id)) throw new ModelException("3MF components are cyclic or nested too deeply.");
            try
            {
                var meshes=definition.Elements(Core+"mesh").ToArray(); var components=definition.Elements(Core+"components").ToArray();
                if(meshes.Length+components.Length!=1) throw new ModelException("A 3MF object needs exactly one mesh or components element.");
                if(meshes.Length==1)
                {
                    if(objects.Count>=ModelLimits.MaxObjects) throw new ModelException("Expanded 3MF components exceed the 1,000 object limit.");
                    var mesh=ReadMesh(id,meshes[0]); triangleCount+=mesh.Triangles.Length; vertexCount+=mesh.Vertices.Length;
                    if(triangleCount>ModelLimits.MaxTriangles || vertexCount>ModelLimits.MaxVertices)
                        throw new ModelException("Expanded 3MF scene exceeds prototype limits.");
                    objects.Add(new(Guid.NewGuid(),(string?)definition.Attribute("name") ?? "Object "+id,mesh,outer) {Material=objectMaterials[id],TriangleMaterials=faceMaterials[id]});
                }
                else
                {
                    var children=components[0].Elements().ToArray();
                    if(children.Length==0 || children.Any(c=>c.Name!=Core+"component"))
                        throw new ModelException("A 3MF components object needs component references.");
                    foreach(var c in children)
                    {
                        token.ThrowIfCancellationRequested();
                        var transform=ReadTransform((string?)c.Attribute("transform"),factor);
                        // Row-vector convention: child placement first, then its enclosing placement.
                        Expand(Index(c,"objectid"),transform.Then(outer));
                    }
                }
            }
            finally { visiting.Remove(id); }
        }
        var items = model.Element(Core+"build")?.Elements(Core+"item").ToArray() ?? [];
        if (items.Length == 0 || items.Length > ModelLimits.MaxObjects) throw new ModelException("3MF needs 1–1,000 build items.");
        foreach(var item in items)
        {
            if ((string?)item.Attribute("printable") is "false" or "0") throw new ModelException("Non-printable build items are not supported yet.");
            var id=Index(item,"objectid"); var first=objects.Count;
            Expand(id,ReadTransform((string?)item.Attribute("transform"),factor));
            if(objects.Count-first>1)
            {
                var name=((string?)definitions[id].Attribute("name"))?.Trim();
                if(string.IsNullOrEmpty(name)) name="Group "+id;
                if(name.Length>100 || name.Any(char.IsControl)) throw new ModelException("3MF group names must have 1–100 characters without controls.");
                groups.Add(new(Guid.NewGuid(),name,objects.Skip(first).Select(o=>o.Id).ToImmutableArray()));
            }
        }
        token.ThrowIfCancellationRequested();
        return new(Guid.NewGuid(),objects.ToImmutable()) {Groups=groups.ToImmutable(),Metadata=metadata};
    }
    private static Affine ReadTransform(string? text, double factor)
    {
        if (text == null) return Affine.Identity;
        var parts = text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 12) throw new ModelException("3MF transforms must have 12 numbers.");
        var a = parts.Select(Parse).ToArray();
        if (a.Any(n => !double.IsFinite(n))) throw new ModelException("3MF transform is not finite.");
        var value = new Affine(new(a[0],a[1],a[2]),new(a[3],a[4],a[5]),new(a[6],a[7],a[8]),new Vec3(a[9],a[10],a[11])*factor);
        if (!double.IsFinite(Determinant(value)) || Math.Abs(Determinant(value)) < 1e-15) throw new ModelException("3MF transform is singular.");
        return value;
    }
    internal static double Determinant(Affine value)
    { var cross = Vec3.Cross(value.X,value.Y); return cross.X*value.Z.X + cross.Y*value.Z.Y + cross.Z*value.Z.Z; }
    private static string TransformText(Affine a) => string.Join(" ",new[] { a.X.X,a.X.Y,a.X.Z,a.Y.X,a.Y.Y,a.Y.Z,a.Z.X,a.Z.Y,a.Z.Z,a.Offset.X,a.Offset.Y,a.Offset.Z }.Select(Number));

    public static void WriteAtomic(string path, Scene scene, bool stl, CancellationToken token, IProgress<OperationProgress>? progress)
    {
        Check(token,progress,"Preparing save");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!,"." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using(var stream = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                if (stl) WriteStl(stream,scene,token,progress); else Write3mf(stream,scene,token,progress);
                stream.Flush(true);
            }
            Check(token,progress,"Ready to save");
            // Same-directory rename/replace is the commit point. The old destination stays intact on write/cancel failure.
            File.Move(temporary,path,true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteStl(Stream stream, Scene scene, CancellationToken token, IProgress<OperationProgress>? progress)
    {
        using var writer = new BinaryWriter(stream,Encoding.UTF8,true);
        var header = new byte[80]; Encoding.ASCII.GetBytes("Builder prototype; coordinates in millimeters").CopyTo(header,0);
        writer.Write(header); writer.Write((uint)scene.TriangleCount); var done = 0;
        foreach(var obj in scene.Objects)
        foreach(var triangle in obj.Mesh.Triangles)
        {
            if (done % 1024 == 0) Check(token,progress,"Writing triangles",done,scene.TriangleCount);
            var a = obj.Transform.Apply(obj.Mesh.Vertices[triangle.A]);
            var b = obj.Transform.Apply(obj.Mesh.Vertices[triangle.B]);
            var c = obj.Transform.Apply(obj.Mesh.Vertices[triangle.C]);
            if (Determinant(obj.Transform) < 0) (b,c) = (c,b);
            var n = Vec3.Cross(b-a,c-a); var length = Math.Sqrt(n.X*n.X+n.Y*n.Y+n.Z*n.Z);
            n = length > 0 ? n*(1/length) : new();
            foreach(var v in new[] { n,a,b,c }) { writer.Write((float)v.X); writer.Write((float)v.Y); writer.Write((float)v.Z); }
            writer.Write((ushort)0); done++;
        }
    }
    private static void Write3mf(Stream stream, Scene scene, CancellationToken token, IProgress<OperationProgress>? progress)
    {
        // Name and uniform material are part of the mesh-resource identity; sharing geometry must not merge colors.
        // Components encode group membership using standard 3MF, with no private metadata.
        var materials=new Dictionary<SurfaceMaterial,int>();
        foreach(var obj in scene.Objects)
        {
            token.ThrowIfCancellationRequested();
            foreach(var material in obj.TriangleMaterials.Values.Concat(obj.Material is {} fallback?[fallback]:[]))
                if(!materials.ContainsKey(material)){SurfaceMaterial.Canonical(material);materials.Add(material,materials.Count);}
        }
        if(materials.Count>ModelLimits.MaxMaterials) throw new ModelException("This prototype supports up to 1,000 base material entries.");
        var materialResource=materials.Count>0 ? 1 : 0;
        var resources=new Dictionary<(Mesh Mesh,string Name,SurfaceMaterial? Material,string Faces),int>();
        var objectResources=new Dictionary<Guid,int>();
        foreach(var obj in scene.Objects)
        {
            token.ThrowIfCancellationRequested();
            var faces=string.Concat(obj.TriangleMaterials.OrderBy(p=>p.Key).Select(p=>$"{p.Key}:{p.Value.Name.Length}:{p.Value.Name}{p.Value.Color.Hex};"));
            var key=(obj.Mesh,obj.Name,obj.Material,faces);
            if(!resources.TryGetValue(key,out var id)){id=resources.Count+1+materialResource;resources.Add(key,id);}
            objectResources.Add(obj.Id,id);
        }
        if(resources.Count+scene.Groups.Length>ModelLimits.MaxObjects)
            throw new ModelException("Saving these groups needs more than the 1,000 supported 3MF object resources.");
        var groupResources=scene.Groups.Select((group,index)=>(group.Id,Resource:resources.Count+index+1+materialResource)).ToDictionary(p=>p.Id,p=>p.Resource);
        var membership=scene.Groups.SelectMany(group=>group.ObjectIds.Select(id=>(id,group))).ToDictionary(p=>p.id,p=>p.group);
        var objectsById=scene.Objects.ToDictionary(o=>o.Id);
        using var zip = new ZipArchive(stream,ZipArchiveMode.Create,true);
        void TextPart(string name, string value)
        { using var output = new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false)); output.Write(value); }
        TextPart("[Content_Types].xml","<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
        TextPart("_rels/.rels","<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"" + ModelRelationship + "\"/></Relationships>");
        using var modelStream = zip.CreateEntry("3D/3dmodel.model").Open();
        using var writer = XmlWriter.Create(modelStream,new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        writer.WriteStartDocument(); writer.WriteStartElement("model",Core.NamespaceName); writer.WriteAttributeString("unit","millimeter");
        writer.WriteAttributeString("xml","lang",null,scene.Metadata.Language);
        foreach(var (name,value) in new[]{("Title",scene.Metadata.Title),("Designer",scene.Metadata.Designer),("Description",scene.Metadata.Description),
            ("Copyright",scene.Metadata.Copyright),("LicenseTerms",scene.Metadata.LicenseTerms)})
        {
            token.ThrowIfCancellationRequested();if(value.Length==0)continue;
            writer.WriteStartElement("metadata");writer.WriteAttributeString("name",name);writer.WriteAttributeString("preserve","1");writer.WriteString(value);writer.WriteEndElement();
        }
        writer.WriteStartElement("resources");
        if(materialResource!=0)
        {
            writer.WriteStartElement("basematerials"); writer.WriteAttributeString("id",materialResource.ToString(CultureInfo.InvariantCulture));
            foreach(var material in materials.Keys)
            {
                token.ThrowIfCancellationRequested(); writer.WriteStartElement("base");
                writer.WriteAttributeString("name",material.Name); writer.WriteAttributeString("displaycolor",material.Color.Hex+"FF"); writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        var written=new HashSet<int>();
        foreach(var obj in scene.Objects)
        {
            var id=objectResources[obj.Id];
            if(!written.Add(id)) continue;
            Check(token,progress,"Writing objects",id,scene.Objects.Length);
            writer.WriteStartElement("object"); writer.WriteAttributeString("id",id.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("type","model"); writer.WriteAttributeString("name",obj.Name);
            if(obj.Material is {} material)
            {
                writer.WriteAttributeString("pid",materialResource.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("pindex",materials[material].ToString(CultureInfo.InvariantCulture));
            }
            writer.WriteStartElement("mesh");
            writer.WriteStartElement("vertices"); var done = 0;
            foreach(var v in obj.Mesh.Vertices)
            {
                if (done++ % 1024 == 0) Check(token,progress,"Writing vertices",done,obj.Mesh.Vertices.Length);
                writer.WriteStartElement("vertex"); writer.WriteAttributeString("x",Number(v.X)); writer.WriteAttributeString("y",Number(v.Y)); writer.WriteAttributeString("z",Number(v.Z)); writer.WriteEndElement();
            }
            writer.WriteEndElement(); writer.WriteStartElement("triangles"); done = 0;
            foreach(var t in obj.Mesh.Triangles)
            {
                var triangleIndex=done;
                if (done++ % 1024 == 0) Check(token,progress,"Writing triangles",done,obj.Mesh.Triangles.Length);
                writer.WriteStartElement("triangle"); writer.WriteAttributeString("v1",t.A.ToString(CultureInfo.InvariantCulture)); writer.WriteAttributeString("v2",t.B.ToString(CultureInfo.InvariantCulture)); writer.WriteAttributeString("v3",t.C.ToString(CultureInfo.InvariantCulture));
                if(obj.TriangleMaterials.TryGetValue(triangleIndex,out var faceMaterial))
                {
                    writer.WriteAttributeString("pid",materialResource.ToString(CultureInfo.InvariantCulture));
                    var index=materials[faceMaterial].ToString(CultureInfo.InvariantCulture);
                    writer.WriteAttributeString("p1",index);writer.WriteAttributeString("p2",index);writer.WriteAttributeString("p3",index);
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        }
        foreach(var group in scene.Groups)
        {
            Check(token,progress,"Writing groups",groupResources[group.Id],resources.Count+scene.Groups.Length);
            writer.WriteStartElement("object"); writer.WriteAttributeString("id",groupResources[group.Id].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("type","model"); writer.WriteAttributeString("name",group.Name);
            writer.WriteStartElement("components");
            foreach(var member in group.ObjectIds)
            {
                token.ThrowIfCancellationRequested(); var obj=objectsById[member];
                writer.WriteStartElement("component"); writer.WriteAttributeString("objectid",objectResources[obj.Id].ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("transform",TransformText(obj.Transform)); writer.WriteEndElement();
            }
            writer.WriteEndElement(); writer.WriteEndElement();
        }
        writer.WriteEndElement(); writer.WriteStartElement("build");
        var emittedGroups=new HashSet<Guid>();
        foreach(var obj in scene.Objects)
        {
            token.ThrowIfCancellationRequested();
            if(membership.TryGetValue(obj.Id,out var group))
            {
                if(!emittedGroups.Add(group.Id)) continue;
                writer.WriteStartElement("item"); writer.WriteAttributeString("objectid",groupResources[group.Id].ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
            }
            else
            {
                writer.WriteStartElement("item"); writer.WriteAttributeString("objectid",objectResources[obj.Id].ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("transform",TransformText(obj.Transform)); writer.WriteEndElement();
            }
        }
        writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
    }

    private sealed class CancellationReadStream(Stream inner, CancellationToken token) : Stream
    {
        public override int Read(byte[] buffer,int offset,int count) { token.ThrowIfCancellationRequested(); return inner.Read(buffer,offset,count); }
        public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return inner.Read(buffer); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    }
}
