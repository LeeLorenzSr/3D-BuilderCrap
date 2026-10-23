using Builder.Core;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Builder.Mcp;

public static class McpJson
{
    public static JsonSerializerOptions Options { get; }=Create();
    private static JsonSerializerOptions Create()
    {
        var resolver=new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info=>
        {
            if(info.Kind==JsonTypeInfoKind.Object&&info.Type.Assembly==typeof(EditorSession).Assembly)
            {
                var parameters=info.Type.GetConstructors().OrderByDescending(c=>c.GetParameters().Length).FirstOrDefault()?.GetParameters()??[];
                foreach(var property in info.Properties.Where(p=>p.Set is null&&!parameters.Any(a=>string.Equals(a.Name,p.Name,StringComparison.OrdinalIgnoreCase))).ToArray())
                    info.Properties.Remove(property);
            }
            if(info.Type==typeof(TextSource))info.PolymorphismOptions=new()
            {TypeDiscriminatorPropertyName="sourceKind",DerivedTypes={new(typeof(FontTextSource),"font"),new(typeof(OutlineTextSource),"outline")}};
            if(info.Type==typeof(ReliefSource))info.PolymorphismOptions=new()
            {TypeDiscriminatorPropertyName="sourceKind",DerivedTypes={new(typeof(ImageFileSource),"file"),new(typeof(PixelImageSource),"pixels")}};
        });
        return new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver=resolver,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true,AllowOutOfOrderMetadataProperties=true,
            Converters={new JsonStringEnumConverter(allowIntegerValues:false)}
        };
    }
    public static JsonObject Object(object value)=>(JsonSerializer.SerializeToNode(value,Options) as JsonObject)!;
}

public sealed record McpPreviewState(Guid Id,long BaseRevision,string Kind,string Summary,int ObjectCount,int TriangleCount,
    ImmutableArray<Guid> ResultObjectIds,ImmutableArray<Guid> SelectedObjectIds,Guid? MemberGroupId,object Details);
public sealed record McpState(long Revision,Guid ContentId,string Units,string? FilePath,bool IsDirty,bool CanUndo,bool CanRedo,
    bool IsBusy,Guid? PrimaryObjectId,ImmutableArray<Guid> SelectedObjectIds,Guid? MemberGroupId,int ObjectCount,int TriangleCount,
    McpPreviewState? Preview,PresentationSnapshot Presentation)
{
    public McpOperationState? Operation {get;init;}
}
public sealed record McpOperationState(Guid Id,string ToolName,bool CancellationRequested);
public sealed record McpReply(OperationResult Result,McpState State,object? Data=null);
public sealed record McpInvocation(string ToolName,bool Mutating,
    Func<CancellationToken,IProgress<OperationProgress>?,Task<McpReply>> Run,IProgress<OperationProgress>? Progress);
public delegate Task<McpReply> McpDispatcher(McpInvocation invocation,CancellationToken cancellation);

internal sealed record NoArguments;
internal sealed record SceneArguments(int Offset=0,int Limit=100,Guid? PreviewId=null);
internal sealed record MeshArguments(Guid ObjectId,long ExpectedRevision,int Offset=0,int Limit=500,Guid? PreviewId=null);
internal sealed record SelectionArguments(ImmutableArray<Guid> ObjectIds,long ExpectedRevision,Guid? PrimaryObjectId=null,Guid? MemberGroupId=null);
internal sealed record EnterGroupArguments(Guid GroupId,long ExpectedRevision,Guid? MemberId=null);
internal sealed record RevisionArguments(long ExpectedRevision);
internal sealed record EditArguments<T>(T Edit,long ExpectedRevision) where T:Edit;
internal sealed record PreviewArguments<T>(T Request,long ExpectedRevision,Guid? ReplacePreviewId=null) where T:GeometryRequest;
internal sealed record PreviewTokenArguments(Guid PreviewId,long ExpectedRevision);
internal sealed record CancelOperationArguments(Guid OperationId);
internal sealed record BulkArguments(ImmutableArray<Guid> ObjectIds,long ExpectedRevision,Guid? MemberGroupId=null);
internal sealed record OpenArguments(string Path,long ExpectedRevision,ModelUnit? Unit=null,DirtyDecision Decision=DirtyDecision.RequireDecision,
    string? SavePath=null,StlImportMode StlMode=StlImportMode.Strict);
internal sealed record NewArguments(long ExpectedRevision,DirtyDecision Decision=DirtyDecision.RequireDecision,string? SavePath=null);
internal sealed record InsertArguments(string Path,long ExpectedRevision,ModelUnit? Unit=null,StlImportMode StlMode=StlImportMode.Strict);
internal sealed record SaveArguments(long ExpectedRevision,string? Path=null);
internal sealed record ExportArguments(string Path,ModelUnit Unit,long ExpectedRevision,ImmutableArray<Guid>? ObjectIds=null,Guid? MemberGroupId=null);
internal sealed record MetadataArguments(ModelMetadata Metadata,long ExpectedRevision);
internal sealed record DiagnosisArguments(Guid ObjectId,long ExpectedRevision);
internal sealed record PickArguments(Vec3 Origin,Vec3 Direction,double MinimumDistance=0,double? MaximumDistance=null,RayPickSides Sides=RayPickSides.Both);
internal sealed record MeasureArguments(Vec3 FirstMillimeters,Vec3 SecondMillimeters,long ExpectedRevision,Guid? MemberGroupId=null);
internal sealed record ViewArguments(ViewSettings Settings,long ExpectedPresentationRevision);
internal sealed record ViewResetArguments(long ExpectedPresentationRevision);
internal sealed record RecoveryConfigureArguments(string Directory,long ExpectedRevision);
internal sealed record RecoveryArguments(RecoveryCandidate Candidate,long ExpectedRevision,DirtyDecision Decision=DirtyDecision.RequireDecision,string? SavePath=null);
