using System.Text.Json;

namespace Builder.Core;

/// <summary>Viewport shading only; smooth display normals do not modify document geometry.</summary>
public enum ShadingMode { Flat=0, Smooth=1 }

/// <summary>Session-scoped display preferences. They never change mesh, material, picking, file or
/// recovery data. A viewport adapter decides how these flags are rendered.</summary>
public sealed record ViewSettings(bool Grid=true,bool Colors=true,bool Wireframe=false,bool XRay=false,ShadingMode Shading=ShadingMode.Flat,bool Shadows=true,bool Reflections=false)
{
    public static ViewSettings Default { get; }=new();
}
public sealed record PresentationSnapshot(ViewSettings Settings,long Revision);

/// <summary>Independent presentation boundary shared by UI and future MCP callers. Its revision is
/// unrelated to the document revision. New/Open/restore/history retain this session's settings.
/// Updates are allowed during document previews and busy operations; no document lock is acquired.</summary>
public sealed class SessionPresentation
{
    private readonly object gate=new();
    private ViewSettings settings=ViewSettings.Default;
    private long revision;
    private IViewPreferenceStore? preferenceStore;
    private ViewPreferenceStatus preferenceStatus=new(ViewPreferenceState.MemoryOnly,false);
    public ViewPreferenceStatus PreferenceStatus { get{lock(gate)return preferenceStatus;} }
    private static bool PreferenceFailure(Exception error)=>error is IOException or UnauthorizedAccessException or JsonException or ModelException or ArgumentException or NotSupportedException;
    /// <summary>Opt-in once. Missing data keeps current settings without creating a file; invalid/unreadable
    /// data also keeps current settings and its original file. Explicit later Update/Reset can repair it.
    /// Configuration succeeds with a separate persistence warning if loading fails.</summary>
    public OperationResult ConfigurePreferences(IViewPreferenceStore store)
    {
        lock(gate)
        {
            if(store is null)return new(Outcome.Invalid,"Supply a view preference store.");
            if(preferenceStore is not null)return new(Outcome.Conflict,"View preferences are already configured.");
            preferenceStore=store;
            try
            {
                var loaded=store.Load();
                if(loaded is null){preferenceStatus=new(ViewPreferenceState.Missing,true);return new(Outcome.Success,"No saved view choices; the current display is unchanged.");}
                if(!Enum.IsDefined(loaded.Shading))throw new ModelException("Saved view choices need Flat or Smooth shading.");
                if(settings!=loaded){settings=loaded;revision++;}
                preferenceStatus=new(ViewPreferenceState.Loaded,false);return new(Outcome.Success,"Saved view choices loaded.");
            }
            catch(Exception error) when(PreferenceFailure(error))
            {
                preferenceStatus=new(ViewPreferenceState.LoadFailed,true,"Could not load saved view choices: "+error.Message);
                return new(Outcome.Success,"View preferences enabled, but saved choices could not be loaded. The current display and preference file are unchanged.");
            }
        }
    }
    private string Persist(bool force)
    {
        if(preferenceStore is null||!force&&!preferenceStatus.PendingSave)return "";
        try{preferenceStore.Save(settings);preferenceStatus=new(ViewPreferenceState.Saved,false);return " View choices saved.";}
        catch(Exception error) when(PreferenceFailure(error))
        {
            preferenceStatus=new(ViewPreferenceState.SaveFailed,true,"Could not remember view choices: "+error.Message);
            return " The display change is active, but view choices could not be remembered. Retry saving view choices.";
        }
    }
    public PresentationSnapshot Snapshot { get{lock(gate)return new(settings,revision);} }
    /// <summary>Replaces all display flags atomically. Null and stale presentation revision fail
    /// without changes. An equal value is revision-neutral, but retries a pending preference save.
    /// Persistence failure keeps the accepted display and reports success with PreferenceStatus warning.</summary>
    public OperationResult Update(ViewSettings settings,long? expectedRevision=null)=>UpdateCore(settings,expectedRevision,false);
    private OperationResult UpdateCore(ViewSettings settings,long? expectedRevision,bool forceSave)
    {
        lock(gate)
        {
            if(settings is null)return new(Outcome.Invalid,"Supply view settings, or use Reset for defaults.");
            if(!Enum.IsDefined(settings.Shading))return new(Outcome.Invalid,"Choose Flat or Smooth shading.");
            if(expectedRevision.HasValue&&expectedRevision.Value!=revision)return new(Outcome.Conflict,"View settings changed. Read the presentation snapshot again.");
            var changed=this.settings!=settings;
            if(changed){this.settings=settings;revision++;}
            return new(Outcome.Success,(changed?"View settings updated.":"View settings are unchanged.")+Persist(forceSave||changed));
        }
    }
    public OperationResult Reset(long? expectedRevision=null)=>UpdateCore(ViewSettings.Default,expectedRevision,true);
}

public sealed partial class EditorSession
{
    /// <summary>Presentation-only flags, with independent state/revision and lifetime. Never persisted
    /// in scene files or recovery checkpoints and never included in document Undo/Redo.</summary>
    public SessionPresentation Presentation { get; }=new();
}
