using System.Xml;

namespace Builder.Core;

/// <summary>Document-wide plain text, independent of file and object names. Description/license
/// allow tabs and line feeds; CRLF/CR canonicalize to LF. Ordinary spaces are retained.</summary>
public sealed record ModelMetadata(string Title="",string Designer="",string Description="",string Copyright="",string LicenseTerms="")
{
    public static ModelMetadata Empty { get; }=new();
    /// <summary>Retained XML language tag, without language interpretation. "und" means undetermined.</summary>
    public string Language { get; init; }="und";
    public const int MaxTitleLength=256,MaxDesignerLength=256,MaxCopyrightLength=1024,
        MaxDescriptionLength=16384,MaxLicenseTermsLength=16384;
    public bool IsEmpty=>Title.Length==0&&Designer.Length==0&&Description.Length==0&&Copyright.Length==0&&LicenseTerms.Length==0;
    internal static ModelMetadata Canonical(ModelMetadata metadata)
    {
        if(metadata is null)throw new ModelException("Supply model information, or ModelMetadata.Empty to clear it.");
        static string Text(string value,string name,int limit,bool multiline=false)
        {
            if(value is null)throw new ModelException(name+" must be plain text, not null.");
            if(multiline)value=value.Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n');
            if(value.Length>limit)throw new ModelException(name+$" is limited to {limit:N0} characters.");
            if(value.Any(c=>char.IsControl(c)&&!(multiline&&c is '\n' or '\t')||!multiline&&c is '\u2028' or '\u2029'))
                throw new ModelException(name+(multiline?" allows ordinary text, line breaks and tabs only.":" must be one line without control characters."));
            try{XmlConvert.VerifyXmlChars(value);}catch(XmlException){throw new ModelException(name+" contains invalid XML Unicode.");}
            return value;
        }
        var language=metadata.Language;
        if(string.IsNullOrEmpty(language)||language.Length>128)throw new ModelException("Model language must be a language tag of 1–128 characters, or und.");
        var segments=language.Split('-');
        if(segments.Any(s=>s.Length is <1 or >8)||!segments[0].All(char.IsAsciiLetter)||segments.Skip(1).Any(s=>!s.All(char.IsAsciiLetterOrDigit)))
            throw new ModelException("Model language must use letters, digits and hyphens in a valid XML language tag.");
        var result=new ModelMetadata(Text(metadata.Title,"Title",MaxTitleLength),Text(metadata.Designer,"Designer",MaxDesignerLength),
            Text(metadata.Description,"Description",MaxDescriptionLength,true),Text(metadata.Copyright,"Copyright",MaxCopyrightLength),
            Text(metadata.LicenseTerms,"License terms",MaxLicenseTermsLength,true)){Language=language};
        return result==metadata?metadata:result;
    }
}

public sealed partial class EditorSession
{
    /// <summary>Replaces document model information in one history step, retaining full selection,
    /// member scope, groups, geometry and materials. Stale/busy/preview/invalid/cancel leaves accepted
    /// work untouched. Canonically equal information is a history/revision-neutral no-op.</summary>
    public OperationResult UpdateModelMetadata(ModelMetadata metadata,long expectedRevision,CancellationToken cancellation=default)
    {
        lock(gate)
        {
            if(Guard(expectedRevision) is {} blocked)return blocked;
            try
            {
                cancellation.ThrowIfCancellationRequested();var value=ModelMetadata.Canonical(metadata);
                cancellation.ThrowIfCancellationRequested();
                if(value==scene.Metadata)return new(Outcome.Success,"Model information is unchanged.");
                var candidate=scene with{ContentId=Guid.NewGuid(),Metadata=value};candidate.Validate(cancellation);
                cancellation.ThrowIfCancellationRequested();
                undo.Add((scene,selected,selectedIds,editingGroupId));if(undo.Count>100)undo.RemoveAt(0);
                redo.Clear();scene=candidate;revision++;
                return new(Outcome.Success,"Model information updated. You can undo this change.");
            }
            catch(OperationCanceledException){return new(Outcome.Cancelled,"Cancelled. Model information and the document are unchanged.");}
            catch(ModelException e){return new(Outcome.Invalid,e.Message);}
        }
    }
}
