using Builder.Core;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Builder.Desktop;

// Keep the original JSON key/enum ordinals so existing STL recent entries retain their scale.
public sealed record RecentFile(string Path,[property:JsonPropertyName("StlUnit")] ModelUnit ModelUnit=ModelUnit.Millimeters,StlImportMode StlMode=StlImportMode.Strict);
public interface IRecentFileStore
{
    IReadOnlyList<RecentFile> Load();
    void Save(IReadOnlyList<RecentFile> entries);
}

/// <summary>Local navigation metadata only. Documents and history never depend on this file.</summary>
public sealed class JsonRecentFileStore(string path) : IRecentFileStore
{
    public static JsonRecentFileStore ForCurrentUser()=>new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"3D BuilderCrap","recent-files.json"));
    public IReadOnlyList<RecentFile> Load()
    {
        if(!File.Exists(path))return [];
        if(new FileInfo(path).Length>1_048_576)throw new IOException("The recent file list is too large.");
        var loaded=JsonSerializer.Deserialize<RecentFile[]>(File.ReadAllText(path,Encoding.UTF8))??[];
        return Canonical(loaded);
    }
    public void Save(IReadOnlyList<RecentFile> entries)
    {
        var full=System.IO.Path.GetFullPath(path);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var temporary=full+".tmp-"+Guid.NewGuid().ToString("N");
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(Canonical(entries),new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));File.Move(temporary,full,true);}
        finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    public static IReadOnlyList<RecentFile> Canonical(IEnumerable<RecentFile> entries)
    {
        var result=new List<RecentFile>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in entries)
        {
            if(entry is null||string.IsNullOrWhiteSpace(entry.Path)||!Enum.IsDefined(entry.ModelUnit)||!Enum.IsDefined(entry.StlMode))continue;
            try{var full=System.IO.Path.GetFullPath(entry.Path);if(seen.Add(full))result.Add(new(full,entry.ModelUnit,entry.StlMode));}
            catch(ArgumentException){}catch(NotSupportedException){}catch(PathTooLongException){}
            if(result.Count==10)break;
        }
        return result;
    }
}
