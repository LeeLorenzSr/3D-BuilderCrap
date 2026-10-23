using System.Text.Json;

namespace Builder.Core;

/// <summary>Optional settings storage. Load returns null for missing data and must not write;
/// implementations report expected storage/format failures with IO, authorization, JSON or ModelException.</summary>
public interface IViewPreferenceStore
{
    ViewSettings? Load();
    void Save(ViewSettings settings);
}
public enum ViewPreferenceState { MemoryOnly, Missing, Loaded, Saved, LoadFailed, SaveFailed }
/// <summary>Persistence outcome, independent of the accepted display and document. Missing is pending
/// but has no error; failures keep pending true until an explicit Update or Reset saves successfully.</summary>
public sealed record ViewPreferenceStatus(ViewPreferenceState State,bool PendingSave,string? Error=null);

/// <summary>Small version1 full-snapshot JSON preferences. Reads never create/repair files. Writes use
/// a unique same-directory temporary file and atomic last-step replacement; concurrent windows use
/// last completed write wins. This file is separate from native documents and recovery checkpoints.</summary>
public sealed class JsonViewPreferenceStore : IViewPreferenceStore
{
    public const int MaximumFileBytes=16*1024;
    public string FilePath { get; }
    public JsonViewPreferenceStore(string filePath)
    {
        FilePath=Path.GetFullPath(filePath);if(string.IsNullOrWhiteSpace(Path.GetFileName(FilePath)))throw new ArgumentException("Choose a file for view preferences.",nameof(filePath));
    }
    public ViewSettings? Load()
    {
        FileStream stream;
        try{stream=new(FilePath,FileMode.Open,FileAccess.Read,FileShare.Read);}
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
        using(stream)
        {
            if(stream.Length is <1 or >MaximumFileBytes)throw new ModelException("Saved view choices must be a JSON file of at most 16 KiB.");
            using var document=JsonDocument.Parse(stream,new JsonDocumentOptions{MaxDepth=8});var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Version",out var version)||version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out var number)||number!=1||!root.TryGetProperty("Settings",out var settings)||settings.ValueKind!=JsonValueKind.Object)throw new ModelException("Saved view choices need the supported version1 settings snapshot.");
            bool Flag(string name)
            {
                if(!settings.TryGetProperty(name,out var value)||value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new ModelException("Saved view choices need a boolean "+name+" value.");return value.GetBoolean();
            }
            if(!settings.TryGetProperty("Shading",out var shading)||shading.ValueKind!=JsonValueKind.String||shading.GetString() is not("Flat" or "Smooth"))throw new ModelException("Saved view choices need Flat or Smooth shading.");
            return new(Flag("Grid"),Flag("Colors"),Flag("Wireframe"),Flag("XRay"),shading.GetString()=="Flat"?ShadingMode.Flat:ShadingMode.Smooth,Flag("Shadows"),Flag("Reflections"));
        }
    }
    public void Save(ViewSettings settings)
    {
        if(settings is null||!Enum.IsDefined(settings.Shading))throw new ModelException("Supply valid view settings before remembering them.");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{Version=1,Settings=new{settings.Grid,settings.Colors,settings.Wireframe,settings.XRay,Shading=settings.Shading.ToString(),settings.Shadows,settings.Reflections}});
        var directory=Path.GetDirectoryName(FilePath)!;Directory.CreateDirectory(directory);var temporary=Path.Combine(directory,"."+Path.GetFileName(FilePath)+"."+Guid.NewGuid().ToString("N")+".tmp");var own=false;
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){own=true;stream.Write(bytes);stream.Flush(true);}
            File.Move(temporary,FilePath,true);
        }
        finally{if(own&&File.Exists(temporary))File.Delete(temporary);}
    }
}
