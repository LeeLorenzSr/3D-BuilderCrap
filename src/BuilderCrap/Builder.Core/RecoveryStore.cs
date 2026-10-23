using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Builder.Core;

/// <summary>An abandoned checkpoint generation. Fingerprint guards the exact file displayed by a caller;
/// Problem marks a damaged/unsupported entry that can still be explicitly discarded.</summary>
public sealed record RecoveryCandidate(Guid SessionId,Guid CheckpointId,string Fingerprint,DateTimeOffset UpdatedUtc,
    string? NativePath,string Title,int ObjectCount,string? Problem=null);

internal sealed class RecoveryStore : IDisposable
{
    internal sealed record MeshData(Vec3[] Vertices,Triangle[] Triangles);
    internal sealed record ObjectData(Guid Id,string Name,int Mesh,Affine Transform,SurfaceMaterial? Material,Dictionary<int,SurfaceMaterial> TriangleMaterials);
    internal sealed record Document(Guid ContentId,MeshData[] Meshes,ObjectData[] Objects,SceneGroup[] Groups,ModelMetadata Metadata,
        Guid? Primary,Guid[] Selection,Guid? EditingGroupId,string? NativePath);
    internal sealed record Envelope(int Version,Guid SessionId,Guid CheckpointId,DateTimeOffset UpdatedUtc,bool Retired,Document? Document);
    internal sealed class Claim(RecoveryStore owner,FileStream lease,RecoveryCandidate candidate,Envelope envelope) : IDisposable
    {
        internal readonly RecoveryStore Owner=owner;internal FileStream? Lease=lease;internal readonly RecoveryCandidate Candidate=candidate;internal readonly Envelope Envelope=envelope;
        public void Dispose(){Lease?.Dispose();Lease=null;}
    }
    private FileStream? lease;
    internal string DirectoryPath { get; }
    internal Guid SessionId { get; private set; }
    private string DataPath(Guid id)=>Path.Combine(DirectoryPath,id.ToString("N")+".recovery.json");
    private string LockPath(Guid id)=>Path.Combine(DirectoryPath,id.ToString("N")+".lock");
    private static void Regular(string path){if(File.Exists(path)&&File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))throw new ModelException("Linked recovery files are not supported.");}
    internal RecoveryStore(string directory)
    {
        DirectoryPath=Path.GetFullPath(directory);
        if(DirectoryPath.StartsWith("\\\\",StringComparison.Ordinal))throw new ModelException("Use a local directory for recovery checkpoints.");
        System.IO.Directory.CreateDirectory(DirectoryPath);if(File.GetAttributes(DirectoryPath).HasFlag(FileAttributes.ReparsePoint))throw new ModelException("Use a direct local recovery directory, not a link.");
        SessionId=Guid.NewGuid();lease=new FileStream(LockPath(SessionId),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
    }
    private FileStream Lock(Guid id){var path=LockPath(id);Regular(path);return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private byte[] Bytes(Guid id)
    {
        var path=DataPath(id);Regular(path);var length=new FileInfo(path).Length;if(length<1||length>ModelLimits.MaxFileBytes)throw new ModelException("Recovery checkpoints are limited to 64 MiB.");return File.ReadAllBytes(path);
    }
    private static JsonElement Must(JsonElement value,string name,JsonValueKind kind,bool nullable=false)
    {
        if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(name,out var child)||
            child.ValueKind!=kind&&!(kind==JsonValueKind.True&&child.ValueKind==JsonValueKind.False)&&!(nullable&&child.ValueKind==JsonValueKind.Null))
            throw new ModelException("Recovery v1 requires a valid "+name+" field.");
        return child;
    }
    private static void VectorFields(JsonElement value){_=Must(value,"X",JsonValueKind.Number);_=Must(value,"Y",JsonValueKind.Number);_=Must(value,"Z",JsonValueKind.Number);}
    private static void MaterialFields(JsonElement value)
    {
        _=Must(value,"Name",JsonValueKind.String);var color=Must(value,"Color",JsonValueKind.Object);
        _=Must(color,"R",JsonValueKind.Number);_=Must(color,"G",JsonValueKind.Number);_=Must(color,"B",JsonValueKind.Number);
    }
    private static void RequiredFields(JsonElement root)
    {
        _=Must(root,"Version",JsonValueKind.Number);_=Must(root,"SessionId",JsonValueKind.String);_=Must(root,"CheckpointId",JsonValueKind.String);_=Must(root,"UpdatedUtc",JsonValueKind.String);
        var retired=Must(root,"Retired",JsonValueKind.True).GetBoolean();var doc=Must(root,"Document",JsonValueKind.Object,retired);if(retired)return;
        _=Must(doc,"ContentId",JsonValueKind.String);_=Must(doc,"Primary",JsonValueKind.String,true);_=Must(doc,"EditingGroupId",JsonValueKind.String,true);_=Must(doc,"NativePath",JsonValueKind.String,true);
        foreach(var id in Must(doc,"Selection",JsonValueKind.Array).EnumerateArray())if(id.ValueKind!=JsonValueKind.String)throw new ModelException("Recovery selection IDs must be strings.");
        var metadata=Must(doc,"Metadata",JsonValueKind.Object);foreach(var name in new[]{"Title","Designer","Description","Copyright","LicenseTerms","Language"})_=Must(metadata,name,JsonValueKind.String);
        foreach(var group in Must(doc,"Groups",JsonValueKind.Array).EnumerateArray())
        {_=Must(group,"Id",JsonValueKind.String);_=Must(group,"Name",JsonValueKind.String);foreach(var id in Must(group,"ObjectIds",JsonValueKind.Array).EnumerateArray())if(id.ValueKind!=JsonValueKind.String)throw new ModelException("Recovery group member IDs must be strings.");}
        foreach(var mesh in Must(doc,"Meshes",JsonValueKind.Array).EnumerateArray())
        {
            foreach(var vertex in Must(mesh,"Vertices",JsonValueKind.Array).EnumerateArray())VectorFields(vertex);
            foreach(var face in Must(mesh,"Triangles",JsonValueKind.Array).EnumerateArray()){_=Must(face,"A",JsonValueKind.Number);_=Must(face,"B",JsonValueKind.Number);_=Must(face,"C",JsonValueKind.Number);}
        }
        foreach(var item in Must(doc,"Objects",JsonValueKind.Array).EnumerateArray())
        {
            _=Must(item,"Id",JsonValueKind.String);_=Must(item,"Name",JsonValueKind.String);_=Must(item,"Mesh",JsonValueKind.Number);var transform=Must(item,"Transform",JsonValueKind.Object);
            foreach(var axis in new[]{"X","Y","Z","Offset"})VectorFields(Must(transform,axis,JsonValueKind.Object));
            var material=Must(item,"Material",JsonValueKind.Object,true);if(material.ValueKind!=JsonValueKind.Null)MaterialFields(material);
            foreach(var entry in Must(item,"TriangleMaterials",JsonValueKind.Object).EnumerateObject())MaterialFields(entry.Value);
        }
    }
    private static Envelope Parse(byte[] bytes,Guid id)
    {
        try
        {
            using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});var root=json.RootElement;RequiredFields(root);
            if(root.GetProperty("Version").GetInt32()!=1||root.GetProperty("SessionId").GetGuid()!=id)throw new ModelException("This recovery file uses an unsupported version or owner identity.");
            if(!root.GetProperty("Retired").GetBoolean())
            {
                var doc=root.GetProperty("Document");
                if(doc.GetProperty("Objects").GetArrayLength()>ModelLimits.MaxObjects||doc.GetProperty("Meshes").GetArrayLength()>ModelLimits.MaxObjects||doc.GetProperty("Groups").GetArrayLength()>ModelLimits.MaxObjects||doc.GetProperty("Selection").GetArrayLength()>ModelLimits.MaxObjects)throw new ModelException("Recovery document exceeds scene limits.");
                long vertices=0,triangles=0;foreach(var mesh in doc.GetProperty("Meshes").EnumerateArray()){vertices+=mesh.GetProperty("Vertices").GetArrayLength();triangles+=mesh.GetProperty("Triangles").GetArrayLength();}
                if(vertices>ModelLimits.MaxVertices||triangles>ModelLimits.MaxTriangles)throw new ModelException("Recovery mesh data exceeds scene limits.");
            }
            var value=JsonSerializer.Deserialize<Envelope>(bytes)??throw new ModelException("Recovery file is empty.");
            if(value.CheckpointId==Guid.Empty||value.UpdatedUtc==default||!value.Retired&&value.Document is null)throw new ModelException("Recovery checkpoint information is incomplete.");return value;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {throw new ModelException("Recovery file is damaged: "+e.Message);}
    }
    internal ImmutableArray<RecoveryCandidate> Discover(CancellationToken ct)
    {
        var candidates=ImmutableArray.CreateBuilder<RecoveryCandidate>();var files=System.IO.Directory.EnumerateFiles(DirectoryPath,"*.recovery.json").Take(1001).ToArray();if(files.Length>1000)throw new ModelException("There are more than 1,000 recovery records. Move older retired records out of the recovery directory.");
        foreach(var file in files)
        {
            ct.ThrowIfCancellationRequested();var stem=Path.GetFileName(file)[..^".recovery.json".Length];if(!Guid.TryParseExact(stem,"N",out var id))continue;FileStream claim;
            try{claim=Lock(id);}catch(IOException){continue;}
            using(claim)
            {
                byte[] bytes;
                try{bytes=Bytes(id);}catch(Exception e) when(e is ModelException or IOException or UnauthorizedAccessException)
                {candidates.Add(new(id,Guid.Empty,"",File.GetLastWriteTimeUtc(file),null,"Unreadable recovery",0,e.Message+" Move this unreadable file out of the recovery directory if it cannot be retried."));continue;}
                var fingerprint=Hash(bytes);Envelope envelope;
                try{envelope=Parse(bytes,id);}
                catch(ModelException e){candidates.Add(new(id,Guid.Empty,fingerprint,File.GetLastWriteTimeUtc(file),null,"Damaged recovery",0,e.Message));continue;}
                if(envelope.Retired)continue;var doc=envelope.Document!;candidates.Add(new(id,envelope.CheckpointId,fingerprint,envelope.UpdatedUtc,doc.NativePath,doc.Metadata?.Title??"",doc.Objects?.Length??0));
            }
        }
        return candidates.OrderByDescending(c=>c.UpdatedUtc).ToImmutableArray();
    }
    internal Claim Acquire(RecoveryCandidate candidate,CancellationToken ct,bool allowDamaged=false)
    {
        if(candidate is null||candidate.SessionId==Guid.Empty||candidate.Fingerprint is not {Length:64})throw new ModelException("Choose an exact recovery candidate.");
        FileStream held;try{held=Lock(candidate.SessionId);}catch(IOException){throw new RecoveryConflictException("This recovery belongs to an active session or is already being handled.");}
        try
        {
            ct.ThrowIfCancellationRequested();if(!File.Exists(DataPath(candidate.SessionId)))throw new RecoveryConflictException("This recovery checkpoint is no longer available.");var bytes=Bytes(candidate.SessionId);if(Hash(bytes)!=candidate.Fingerprint)throw new RecoveryConflictException("The recovery checkpoint changed. Refresh the list before restoring or discarding it.");
            Envelope envelope;
            try{envelope=Parse(bytes,candidate.SessionId);}catch(ModelException) when(allowDamaged){envelope=new(1,candidate.SessionId,Guid.NewGuid(),DateTimeOffset.UtcNow,false,null);}
            if(envelope.Retired||candidate.CheckpointId!=Guid.Empty&&envelope.CheckpointId!=candidate.CheckpointId)throw new RecoveryConflictException("This recovery checkpoint is no longer available.");return new(this,held,candidate,envelope);
        }
        catch{held.Dispose();throw;}
    }
    private void Write(Guid owner,Envelope value,CancellationToken ct,IProgress<OperationProgress>? progress,string stage)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.LongLength>ModelLimits.MaxFileBytes)throw new ModelException("Recovery checkpoints are limited to 64 MiB.");
        var target=DataPath(owner);Regular(target);var temporary=Path.Combine(DirectoryPath,"."+owner.ToString("N")+"."+Guid.NewGuid().ToString("N")+".tmp");var own=false;
        try
        {
            ct.ThrowIfCancellationRequested();using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {own=true;for(var offset=0;offset<bytes.Length;offset+=65536){ct.ThrowIfCancellationRequested();stream.Write(bytes,offset,Math.Min(65536,bytes.Length-offset));}stream.Flush(true);}
            progress?.Report(new(stage));ct.ThrowIfCancellationRequested();File.Move(temporary,target,true);
        }
        finally{if(own&&File.Exists(temporary))File.Delete(temporary);}
    }
    internal void Checkpoint(EditorSnapshot snapshot,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        var meshes=new List<Mesh>();var objects=new List<ObjectData>();
        foreach(var obj in snapshot.Scene.Objects)
        {
            ct.ThrowIfCancellationRequested();var index=meshes.FindIndex(m=>ReferenceEquals(m,obj.Mesh));if(index<0){index=meshes.Count;meshes.Add(obj.Mesh);}objects.Add(new(obj.Id,obj.Name,index,obj.Transform,obj.Material,obj.TriangleMaterials.ToDictionary()));
        }
        var document=new Document(snapshot.Scene.ContentId,meshes.Select(m=>new MeshData(m.Vertices.ToArray(),m.Triangles.ToArray())).ToArray(),objects.ToArray(),snapshot.Scene.Groups.ToArray(),snapshot.Scene.Metadata,
            snapshot.SelectedObjectId,snapshot.SelectedObjectIds.ToArray(),snapshot.EditingGroupId,snapshot.FilePath);
        Write(SessionId,new(1,SessionId,Guid.NewGuid(),DateTimeOffset.UtcNow,false,document),ct,progress,"Ready to checkpoint recovery");
    }
    internal void RetireOwn()
    {
        if(!File.Exists(DataPath(SessionId)))return;
        // A small atomic tombstone is the durable retirement. Keep it even if directory cleanup is unavailable.
        Write(SessionId,new(1,SessionId,Guid.NewGuid(),DateTimeOffset.UtcNow,true,null),default,null,"Recovery retired");
        RemoveRetired(SessionId);
    }
    private void RemoveRetired(Guid owner)
    {
        // Deletion is only housekeeping after a durable tombstone, under the owner's exclusive lock.
        try{File.Delete(DataPath(owner));}catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
    }
    internal void RetireClaim(Claim claim,CancellationToken ct,IProgress<OperationProgress>? progress)
    {
        Write(claim.Candidate.SessionId,new(1,claim.Candidate.SessionId,Guid.NewGuid(),DateTimeOffset.UtcNow,true,null),ct,progress,"Ready to discard recovery");
        RemoveRetired(claim.Candidate.SessionId);
    }
    internal void Adopt(Claim claim)
    {
        if(!ReferenceEquals(claim.Owner,this)||claim.Lease is null)throw new ModelException("The recovery claim is no longer owned.");
        lease?.Dispose();lease=claim.Lease;claim.Lease=null;SessionId=claim.Candidate.SessionId;
    }
    internal static (Scene Scene,Guid? Primary,ImmutableArray<Guid> Selection,Guid? Scope,string? NativePath) Restore(Envelope envelope,CancellationToken ct)
    {
        var doc=envelope.Document??throw new ModelException("Recovery contains no document.");if(doc.ContentId==Guid.Empty||doc.Meshes is null||doc.Objects is null||doc.Groups is null||doc.Selection is null||doc.Metadata is null)throw new ModelException("Recovery document information is incomplete.");
        var meshes=new Mesh[doc.Meshes.Length];for(var i=0;i<meshes.Length;i++){ct.ThrowIfCancellationRequested();var data=doc.Meshes[i];if(data is null||data.Vertices is null||data.Triangles is null)throw new ModelException("Recovery mesh is missing.");meshes[i]=new(data.Vertices,data.Triangles);}
        var objects=ImmutableArray.CreateBuilder<SceneObject>();foreach(var item in doc.Objects)
        {
            ct.ThrowIfCancellationRequested();if(item is null||item.Id==Guid.Empty||item.Name is null||item.Mesh<0||item.Mesh>=meshes.Length||item.TriangleMaterials is null)throw new ModelException("Recovery object information is invalid.");
            objects.Add(new(item.Id,item.Name,meshes[item.Mesh],item.Transform){Material=item.Material,TriangleMaterials=item.TriangleMaterials.ToImmutableDictionary()});
        }
        var scene=new Scene(doc.ContentId,objects.ToImmutable()){Groups=doc.Groups.ToImmutableArray(),Metadata=doc.Metadata};scene.Validate(ct);
        var selection=doc.Selection.ToImmutableArray();if(!scene.ExpandObjectIds(selection,doc.EditingGroupId).SequenceEqual(selection)||doc.Primary.HasValue&&!selection.Contains(doc.Primary.Value)||!doc.Primary.HasValue&&!selection.IsEmpty)throw new ModelException("Recovery selection or group scope is invalid.");
        if(doc.NativePath is {} path&&(!Path.IsPathFullyQualified(path)||!Path.GetExtension(path).Equals(".3mf",StringComparison.OrdinalIgnoreCase)))throw new ModelException("Recovery native save path is invalid.");
        return(scene,doc.Primary,selection,doc.EditingGroupId,doc.NativePath);
    }
    public void Dispose(){lease?.Dispose();lease=null;}
}
internal sealed class RecoveryConflictException(string message):Exception(message);
