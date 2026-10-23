using Builder.Core;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

internal static partial class Program
{
    private static async Task PreviewFamilies()
    {
        await using var client=await Client("--headless");
        var scene=await Open(client,"fixtures/generated/cube-20mm.stl");var id=Primary(scene);
        var material=new SurfaceMaterial("MCP green",new(40,160,80));
        var outline=new OutlineTextSource(new([[(new Vec2(0,0)),new(4,0),new(4,4),new(0,4)]]));
        var image=new PixelImageSource(new(2,2,Enumerable.Repeat((byte)255,16).ToImmutableArray()));
        var requests=new (string Kind,GeometryRequest Request)[]{
            ("move",new MoveTransform(id,new(1,2,3))),
            ("rotate",new RotateTransform(id,new(0,0,45))),
            ("scale",new ScaleTransform(id,new(1.2,1,1))),
            ("mirror",new MirrorTransform(id,MirrorAxis.X)),
            ("polyhedron",new PolyhedronPrimitiveRequest(PrimitiveKind.Wedge,new(12,14,10))),
            ("radial_primitive",new RadialPrimitiveRequest(PrimitiveKind.Cylinder,new(12,12,10),8)),
            ("sphere",new SpherePrimitiveRequest(new(12,12,12),8,4)),
            ("torus",new TorusPrimitiveRequest(8,2,8,8)),
            ("split",new SplitRequest(id,new(1,0,0),10)),
            ("repair",new RepairRequest(id)),
            ("text",new TextRequest(new FontTextSource(new("MCP","Arial",6)),1)),
            ("emboss",new EmbossRequest(id,outline,new(EmbossFace.PositiveZ),1)),
            ("image_relief",new ImageReliefRequest(image,20,2,1,8)),
            ("image_contour",new ImageContourRequest(image,20,2,.5,8)),
            ("image_edge",new ImageEdgeRequest(image,20,2,3,.5,8)),
            ("image_stamp",new ImageStampRequest(image,20,2,1,.5,8)),
            ("hollow",new HollowRequest(id,2)),
            ("smooth",new SmoothRequest(id,20,.2,false)),
            ("extrude_down",new ExtrudeDownRequest(id,-5)),
            ("material",new MaterialRequest([id],material)),
            ("surface_material",new SurfaceMaterialRequest(id,0,material)),
            ("surface_brush",new SurfaceBrushRequest(id,[new(5,material,[new(new(10,10,40),new(0,0,-1),0,1000)])]))
        };
        var baseline=scene["state"]!["contentId"]!.GetValue<string>();
        foreach(var (kind,request)in requests)
        {
            var preview=await Preview(client,kind,request);Check(preview["state"]!["contentId"]!.GetValue<string>()==baseline,"Preview accepted "+kind);
            if(kind=="repair"&&preview["state"]!["preview"] is null)
            {Check(preview["result"]!["message"]!.GetValue<string>().Contains("No changes needed"),"Missing no-op repair guidance");continue;}
            Check(preview["state"]!["preview"] is not null,"No preview for "+kind+": "+preview.ToJsonString());
            var token=PreviewId(preview);var candidate=await Call(client,"builder_scene",new{previewId=token});
            Check(candidate["data"]!["objects"]!.AsArray().Count>0,"Candidate query is empty: "+kind);
            observations.Add(new{tool="builder_preview_"+kind,summary=preview["state"]!["preview"]!["summary"]!.GetValue<string>()});
            await Finish(client,preview);
        }
        scene=await Open(client,"fixtures/generated/missing-top.stl");await Finish(client,await Preview(client,"repair",new RepairRequest(Primary(scene))));
        scene=await Open(client,".local/evidence/simplify-20261020/inputs/dense-box.3mf");await Finish(client,await Preview(client,"simplify",new SimplifyRequest(Primary(scene),50,.1)));
        Pass("Twenty-three geometry/creation/text/image/material/brush preview families execute through real MCP JSON, expose private candidates and cancel without accepting content; healthy repair reports an explicit no-op");
        scene=await Open(client,".local/fixtures/distribution/distribution-assembly.3mf");
        var objects=scene["data"]?["objects"];scene=await Scene(client);objects=scene["data"]!["objects"]!;
        var ids=objects.AsArray().Select(o=>Guid.Parse(o!["id"]!.GetValue<string>())).ToImmutableArray();
        var groupIds=scene["data"]!["groups"]![0]!["objectIds"]!.AsArray().Select(v=>Guid.Parse(v!.GetValue<string>())).ToImmutableArray();
        foreach(var (kind,request)in new (string,GeometryRequest)[]{
            ("group",new GroupRequest(ids,"MCP group")),("ungroup",new UngroupRequest(groupIds)),
            ("align",new AlignmentRequest(ids,AxisAlignment.Center,AxisAlignment.Keep,AxisAlignment.Keep)),
            ("distribute",new DistributionRequest(ids)),("center_on_plate",new CenterOnPlateRequest(ids,true)),
            ("rectangular_array",new RectangularArrayRequest(groupIds,2,1,1,new(30,0,0))),
            ("radial_array",new RadialArrayRequest(groupIds,4,90,RadialArrayAxis.Z,new(0,0,0)))})
            await Finish(client,await Preview(client,kind,request));
        scene=await Open(client,"fixtures/generated/assembly-mm.3mf");scene=await Scene(client);
        ids=scene["data"]!["objects"]!.AsArray().Select(o=>Guid.Parse(o!["id"]!.GetValue<string>())).ToImmutableArray();
        Check(ids.Length==2,"Boolean fixture expected two objects");await Finish(client,await Preview(client,"boolean",new BooleanRequest(ids[0],ids[1],BooleanKind.Union)));
        scene=await Open(client,".local/evidence/separate-20261020/inputs/two-hollow-boxes.3mf");
        await Finish(client,await Preview(client,"separate",new SeparateRequest(Primary(scene))));
        Pass("Nine arrangement/boolean/separate preview families preserve grouped targets and owning solids, completing all32 registered preview-tool paths");
    }
}
