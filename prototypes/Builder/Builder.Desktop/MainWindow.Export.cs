using Builder.Core;
using System.IO;

namespace Builder.Desktop;

public partial class MainWindow
{
    private async Task ExportDocument(bool selection)
    {
        if(!DocumentNavigationAvailable)return;
        var captured=editor.Snapshot;
        if(captured.Scene.Objects.IsEmpty||selection&&captured.SelectedObjectIds.IsEmpty)return;
        var name=selection?(SelectedGroup?.Name??(SingleUngroupedSelection?Selected!.Name:"Selected objects"))+" selection":"My model";
        name=string.Concat(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
        string? path;var unit=ModelUnit.Millimeters;
        documentPromptActive=true;RefreshWorkspaceNavigation();
        try
        {
            path=documentDialogs.ChooseExportPath(this,selection,name);
            if(path==null){Message("Export cancelled. Your scene is unchanged.");return;}
            if(Path.GetExtension(path).ToLowerInvariant() is ".obj" or ".ply" or ".gltf" or ".glb")
            {
                var chosen=documentDialogs.ChooseModelUnit(this,path,UnitsDialog.DefaultUnit(path),exporting:true);
                if(chosen==null){Message("Export cancelled. Your scene is unchanged.");return;}
                unit=chosen.Value;
            }
        }
        finally{documentPromptActive=false;RefreshWorkspaceNavigation();}
        if(selection)await ExportSelectionInUnits(path,captured,unit);
        else
        {
            var extension=Path.GetExtension(path).ToLowerInvariant();
            var result=await RunOperation((ct,p)=>extension switch{
                ".obj"=>editor.ExportObjAsync(path,unit,ct,p,captured.Revision),
                ".ply"=>editor.ExportPlyAsync(path,unit,ct,p,captured.Revision),
                ".gltf" or ".glb"=>editor.ExportGltfAsync(path,unit,ct,p,captured.Revision),
                _=>editor.ExportStlAsync(path,ct,p,captured.Revision)});
            if(result.Succeeded)ExportFeedback(path);
        }
    }
    private async Task<OperationResult> ExportSelectionInUnits(string filePath,EditorSnapshot captured,ModelUnit unit)
    {
        var result=await RunOperation((ct,p)=>editor.ExportSelectionAsync(filePath,captured.SelectedObjectIds,ct,p,captured.Revision,captured.EditingGroupId,unit));
        if(result.Succeeded)ExportFeedback(filePath);
        return result;
    }
    private void ExportFeedback(string path)
    {
        var extension=Path.GetExtension(path);
        if(extension.Equals(".stl",StringComparison.OrdinalIgnoreCase))Message("STL exported: geometry only, in millimeters. Save 3MF to keep names, colors, groups and model information.");
        else if(extension.Equals(".obj",StringComparison.OrdinalIgnoreCase))Message("OBJ exported. Keep its companion MTL file for display colors. Save 3MF to keep model information.");
        else if(extension.Equals(".ply",StringComparison.OrdinalIgnoreCase))Message("PLY exported with display colors. Objects are combined into one mesh. Save 3MF to keep names, groups and model information.");
        else if(extension.Equals(".gltf",StringComparison.OrdinalIgnoreCase)||extension.Equals(".glb",StringComparison.OrdinalIgnoreCase))Message("glTF exported with objects and display colors. Physical size is retained in standard meters. Save 3MF to keep model information.");
    }
}
