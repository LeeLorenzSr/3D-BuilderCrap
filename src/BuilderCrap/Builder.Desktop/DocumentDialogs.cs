using Builder.Core;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace Builder.Desktop;

public sealed record ModelImportChoice(ModelUnit Unit,StlImportMode StlMode=StlImportMode.Strict);

public interface IDocumentDialogs
{
    DirtyDecision ConfirmChanges(Window owner,EditorSnapshot snapshot,string action);
    string? ChooseOpenPath(Window owner,bool insert);
    string? ChooseSavePath(Window owner);
    ModelUnit? ChooseModelUnit(Window owner,string path,ModelUnit initialUnit,bool exporting=false);
    ModelImportChoice? ChooseImportOptions(Window owner,string path,ModelUnit initialUnit,bool insert=false,StlImportMode initialStlMode=StlImportMode.Strict)
    {
        var unit=ChooseModelUnit(owner,path,initialUnit);
        return unit is {} chosen?new(chosen):null;
    }
    string? ChooseExportPath(Window owner,bool selection,string suggestedName);
    void ShowModelMetadata(Window owner,ModelMetadataDialog dialog);
    Task ShowRecoveryAsync(Window owner,RecoveryDialog dialog);
}

public sealed class DocumentDialogs : IDocumentDialogs
{
    public const string ModelOpenFilter="3D models (*.stl;*.obj;*.ply;*.gltf;*.glb;*.3mf)|*.stl;*.obj;*.ply;*.gltf;*.glb;*.3mf";
    public const string SceneExportFilter="STL geometry only, millimeters (*.stl)|*.stl|OBJ geometry and display colors (*.obj)|*.obj|PLY combined mesh and display colors (*.ply)|*.ply|glTF objects and display colors (*.gltf)|*.gltf|GLB objects and display colors (*.glb)|*.glb";
    public const string SelectionExportFilter="3MF selection with display colors and model information (*.3mf)|*.3mf|STL geometry only, millimeters (*.stl)|*.stl|OBJ geometry and display colors (*.obj)|*.obj|PLY combined mesh and display colors (*.ply)|*.ply|glTF objects and display colors (*.gltf)|*.gltf|GLB objects and display colors (*.glb)|*.glb";
    public void ShowModelMetadata(Window owner,ModelMetadataDialog dialog){dialog.Owner=owner;dialog.ShowDialog();}
    public Task ShowRecoveryAsync(Window owner,RecoveryDialog dialog){dialog.Owner=owner;dialog.ShowDialog();return Task.CompletedTask;}
    public DirtyDecision ConfirmChanges(Window owner,EditorSnapshot snapshot,string action)
    {var dialog=new SaveChangesDialog(snapshot,action){Owner=owner};dialog.ShowDialog();return dialog.Decision;}
    public string? ChooseOpenPath(Window owner,bool insert)
    {var dialog=new OpenFileDialog{Filter=ModelOpenFilter,Title=insert?"Insert a model into this scene":"Open a model"};return dialog.ShowDialog(owner)==true?dialog.FileName:null;}
    public string? ChooseSavePath(Window owner)
    {var dialog=new SaveFileDialog{Filter="3MF scene with names and display materials (*.3mf)|*.3mf",DefaultExt=".3mf",FileName="My model.3mf",Title="Save scene"};return dialog.ShowDialog(owner)==true?dialog.FileName:null;}
    public ModelUnit? ChooseModelUnit(Window owner,string path,ModelUnit initialUnit,bool exporting=false)
    {var dialog=new UnitsDialog(System.IO.Path.GetFileName(path),initialUnit,exporting){Owner=owner};return dialog.ShowDialog()==true?dialog.Unit:null;}
    public ModelImportChoice? ChooseImportOptions(Window owner,string path,ModelUnit initialUnit,bool insert=false,StlImportMode initialStlMode=StlImportMode.Strict)
    {var dialog=new UnitsDialog(System.IO.Path.GetFileName(path),initialUnit,insert:insert,stlMode:initialStlMode){Owner=owner};return dialog.ShowDialog()==true?new(dialog.Unit,dialog.StlMode):null;}
    public string? ChooseExportPath(Window owner,bool selection,string suggestedName)
    {var dialog=new SaveFileDialog{Filter=selection?SelectionExportFilter:SceneExportFilter,DefaultExt=selection?".3mf":".stl",FileName=suggestedName,Title=selection?"Export selected objects":"Export all objects"};return dialog.ShowDialog(owner)==true?dialog.FileName:null;}
}

public sealed class SaveChangesDialog : Window
{
    public DirtyDecision Decision {get;private set;}=DirtyDecision.Cancel;
    public SaveChangesDialog(EditorSnapshot snapshot,string action)
    {
        SetResourceReference(StyleProperty,typeof(Window));
        Title="Unsaved changes";Width=490;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new(24)};
        panel.Children.Add(new TextBlock{Text="Save changes before "+action+"?",TextWrapping=TextWrapping.Wrap,FontSize=19,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,14)});
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,LineHeight=20,Text=snapshot.Scene.Objects.IsEmpty
            ?"There are no objects to save. Choose Cancel to add an object or use Undo to restore one, or choose Discard to continue."
            :"Save keeps your changes in a 3MF file. Discard continues without saving them. Cancel keeps this scene open.",Margin=new(0,0,0,22)});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var save=new Button{Content="Save",MinWidth=90,IsEnabled=!snapshot.Scene.Objects.IsEmpty};
        var discard=new Button{Content="Discard",MinWidth=90};
        var cancel=new Button{Content="Cancel",MinWidth=90,IsDefault=true,IsCancel=true,Margin=new(0)};
        save.Click+=(_,_)=>{Decision=DirtyDecision.Save;DialogResult=true;};
        discard.Click+=(_,_)=>{Decision=DirtyDecision.Discard;DialogResult=true;};
        buttons.Children.Add(save);buttons.Children.Add(discard);buttons.Children.Add(cancel);panel.Children.Add(buttons);Content=panel;
        ContentRendered+=(_,_)=>cancel.Focus();
    }
}
