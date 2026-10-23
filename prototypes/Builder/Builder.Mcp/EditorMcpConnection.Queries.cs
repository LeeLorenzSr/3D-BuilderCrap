using Builder.Core;

namespace Builder.Mcp;

public sealed partial class EditorMcpConnection
{
    private static void Page(int offset,int limit,int maximum=200)
    {if(offset<0||limit<1||limit>maximum)throw new ModelException($"Use offset>=0 and limit1–{maximum}.");}
    private static Scene ReadScene(EditorSnapshot s,Guid? previewId)
    {
        return previewId is null?s.Scene:s.Preview?.Id==previewId?s.Preview.Scene:
            throw new McpConflict("The preview changed. Read builder_scene again for its current token.");
    }
    private void RegisterQueries()
    {
        Add<SceneArguments>("builder_scene","Read accepted scene state, explicit units, revision, selection/member scope, history, metadata, groups and paged object summaries. Supply previewId to inspect a candidate; this never accepts it.",false,(a,ct,_)=>
        {
            ct.ThrowIfCancellationRequested();Page(a.Offset,a.Limit);var captured=editor.Snapshot;var scene=ReadScene(captured,a.PreviewId);
            var objects=scene.Objects.Skip(a.Offset).Take(a.Limit).Select(o=>new{o.Id,o.Name,o.Bounds,o.Transform,
                vertices=o.Mesh.Vertices.Length,triangles=o.Mesh.Triangles.Length,
                material=o.Material is {} m?new{name=m.Name,color=m.Color.Hex}:null,paintedTriangles=o.TriangleMaterials.Count}).ToArray();
            return Task.FromResult(Success(new{scene.Metadata,bounds=scene.Objects.IsEmpty?(Bounds3?)null:scene.Bounds,scene.Groups,
                total=scene.Objects.Length,a.Offset,a.Limit,objects,previewId=a.PreviewId},captured));
        });
        Add<SceneArguments>("builder_tree","Read the shared group/member tree, paged by root. Leaf/group identities are stable; use builder_enter_group to edit members independently.",false,(a,ct,_)=>
        {
            ct.ThrowIfCancellationRequested();Page(a.Offset,a.Limit);var captured=editor.Snapshot;var tree=ReadScene(captured,a.PreviewId).GetObjectTree();
            return Task.FromResult(Success(new{total=tree.Length,a.Offset,roots=tree.Skip(a.Offset).Take(a.Limit).ToArray()},captured));
        });
        Add<MeshArguments>("builder_mesh","Read paged local vertices, canonical triangles and effective triangle materials for an object at expectedRevision. Transform local points using its scene transform. Each page is at most1000 entries; optional previewId reads the candidate.",false,(a,ct,_)=>
        {
            var captured=Context(a.ExpectedRevision,null,false);ct.ThrowIfCancellationRequested();Page(a.Offset,a.Limit,1000);
            var obj=ReadScene(captured,a.PreviewId).Objects.FirstOrDefault(o=>o.Id==a.ObjectId)??throw new ModelException("Object not found.");
            return Task.FromResult(Success(new{obj.Id,obj.Transform,a.Offset,vertexCount=obj.Mesh.Vertices.Length,triangleCount=obj.Mesh.Triangles.Length,
                vertices=obj.Mesh.Vertices.Skip(a.Offset).Take(a.Limit).ToArray(),
                triangles=obj.Mesh.Triangles.Skip(a.Offset).Take(a.Limit).Select((triangle,i)=>new{index=a.Offset+i,triangle,material=obj.GetTriangleMaterial(a.Offset+i)}).ToArray()},captured));
        });
        Add<NoArguments>("builder_fonts","List installed font families available to text/emboss. Headless Windows and desktop use the same outline adapter.",false,(_,ct,_)=>
        {ct.ThrowIfCancellationRequested();return Task.FromResult(Success(editor.GetFontFamilies()));});
        Add<PickArguments>("builder_pick","Query the nearest accepted-scene triangle from a world ray in millimeters, independent of the camera. Outside members can occlude edited members. This does not select anything.",false,async(a,ct,_)=>
            Success(await editor.PickAsync(a.Origin,a.Direction,a.MinimumDistance,a.MaximumDistance??double.PositiveInfinity,ct,a.Sides).ConfigureAwait(false)));
        Add<MeasureArguments>("builder_measure","Measure two world points in millimeters without editing the document.",false,(a,ct,_)=>
            Task.FromResult(Success(editor.MeasureDistance(a.FirstMillimeters,a.SecondMillimeters,a.ExpectedRevision,a.MemberGroupId,ct))));
        Add<DiagnosisArguments>("builder_diagnose","Diagnose one accepted object without repairing it. Owns the Core busy lease, supports cancellation and reports the captured revision.",true,async(a,ct,p)=>
        {Context(a.ExpectedRevision,null,false);var r=await editor.DiagnoseAsync(a.ObjectId,ct,p,a.ExpectedRevision).ConfigureAwait(false);return Reply(r.Result,r.Diagnosis);});
    }
}
