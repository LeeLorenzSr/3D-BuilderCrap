using Builder.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;

internal static class NativeSaveChecks
{
    public static async Task Run(string root,string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Choose a fresh native-save evidence directory.");
        Directory.CreateDirectory(output);
        var fixtures = Path.Combine(root,"fixtures","generated");
        await CheckWorkingCopy(Path.Combine(fixtures,"assembly-inch.3mf"), output);
        await CheckTransforms(Path.Combine(fixtures,"cube-20mm.stl"), output);
    }

    private static async Task CheckWorkingCopy(string assembly,string output)
    {
        var project = Path.Combine(output,"slicer-project.3mf");
        File.Copy(assembly,project);
        using (var package = ZipFile.Open(project,ZipArchiveMode.Update))
        using (var profile = new StreamWriter(package.CreateEntry("Metadata/profile.config").Open()))
            profile.Write("layer_height = 0.20");
        var original = File.ReadAllBytes(project);
        using var session = new EditorSession();
        var opened = await session.OpenAsync(project);
        Ok(opened);
        Require(session.Snapshot.FilePath is null && opened.Message.Contains("new file"),"Missing working-copy guidance.");
        Ok(session.Execute(new AddCube()));
        var before = session.Snapshot;
        var close = await session.TransitionAsync(new CloseDocumentTransition(before.Revision,DirtyDecision.Save,project));
        Require(!close.SaveCommitted && !close.ReadyToClose && !close.Result.Succeeded,"Close overwrote the project.");
        Require(!(await session.ExportSelectionAsync(project,before.SelectedObjectIds)).Succeeded,"Selection overwrote project.");
        Require(!(await session.SaveAsync(project)).Succeeded && session.Snapshot==before,"Save changed accepted state.");
        Require(original.SequenceEqual(File.ReadAllBytes(project)),"Original slicer settings or geometry changed.");
        Ok(session.Undo());
        Ok(session.Redo());
        var copy = Path.Combine(output,"editable-copy.3mf");
        Ok(await session.SaveAsync(copy));
        Ok(await session.OpenAsync(copy));
        Require(session.Snapshot.FilePath==copy && session.Snapshot.Scene.Objects.Length==3,"Working copy lost objects or path.");
        Console.WriteLine("PASS 3MF working copy, history, original-project protection for Save, Close and selection export. Original SHA-256 "+Convert.ToHexString(SHA256.HashData(original)));
    }

    private static async Task CheckTransforms(string cube,string output)
    {
        using var session = new EditorSession();
        Ok(await session.OpenAsync(cube,ModelUnit.Millimeters));
        var miniature = Path.Combine(output,"miniature.3mf");
        Ok(await session.SaveAsync(miniature));
        // Simulate a file from an earlier release: a 1 km source cube placed at 5 mm scale.
        using (var package = ZipFile.Open(miniature,ZipArchiveMode.Update))
        {
            var part = package.GetEntry("3D/3dmodel.model")!;
            XDocument model;
            using(var input=part.Open()) model=XDocument.Load(input);
            var ns = model.Root!.Name.Namespace;
            foreach (var coordinate in model.Descendants(ns+"vertex").Attributes())
                coordinate.Value = (double)coordinate==0 ? "0" : "1000000";
            model.Descendants(ns+"item").Single().SetAttributeValue("transform","5e-6 0 0 0 5e-6 0 0 0 5e-6 0 0 0");
            part.Delete();
            using var updated = package.CreateEntry("3D/3dmodel.model").Open();
            model.Save(updated);
        }
        Ok(await session.OpenAsync(miniature));
        var dimensions = session.Snapshot.Scene.Bounds.Size;
        Require(dimensions==new Vec3(5,5,5),"Valid small-scale placement changed dimensions.");
        Ok(await session.SaveAsync(miniature));
        Ok(await session.OpenAsync(miniature));
        Require(session.Snapshot.Scene.Bounds.Size==dimensions,"Miniature failed native round trip.");
        Console.WriteLine("PASS 5 mm miniature reopens with its small placement scale.");

        var panel = Path.Combine(output,"flat-panel.gltf");
        Ok(await session.ExportGltfAsync(panel,ModelUnit.Meters));
        var json = JsonNode.Parse(File.ReadAllText(panel))!;
        json["nodes"]![0]!["scale"] = new JsonArray(1,0,1);
        File.WriteAllText(panel,json.ToJsonString());
        Ok(await session.InsertAsync(panel));
        var expected = session.Snapshot.Scene.Objects.Last();
        var saved = Path.Combine(output,"flat-panel-and-miniature.3mf");
        Ok(await session.SaveAsync(saved));
        Ok(await session.OpenAsync(saved));
        var reopened = session.Snapshot.Scene.Objects.Last();
        Require(expected.Bounds.Size.Z==0 && expected.Bounds==reopened.Bounds &&
            expected.Mesh.Triangles.SequenceEqual(reopened.Mesh.Triangles) && expected.Material==reopened.Material,
            "Saving a collapsed glTF instance changed its world geometry or appearance.");
        Console.WriteLine("PASS Collapsed glTF instance survives native Save and Open beside the original solid.");
    }

    private static void Ok(OperationResult result) => Require(result.Succeeded,result.Message);
    private static void Require(bool condition,string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
