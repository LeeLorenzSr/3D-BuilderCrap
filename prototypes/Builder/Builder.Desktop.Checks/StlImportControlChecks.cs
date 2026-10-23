using Builder.Core;
using Builder.Desktop;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private static async Task RunStlImportControls()
    {
        var strict=new UnitsDialog("fan-shroud.stl",ModelUnit.Millimeters);
        var strictContent=(FrameworkElement)strict.Content;
        var checkbox=Tree(strictContent).OfType<CheckBox>().Single();
        Assert(checkbox.IsChecked==false&&strict.StlMode==StlImportMode.Strict,"STL color loss was opted in by default.");
        checkbox.IsChecked=true;
        Assert(strict.StlMode==StlImportMode.GeometryOnly,"Geometry-only choice did not reach the typed import mode.");
        Assert(Tree(strictContent).OfType<TextBlock>().Any(t=>t.Text.Contains("without those colors")&&t.TextWrapping==TextWrapping.Wrap),"STL appearance-loss guidance missing.");
        V1DialogCapture(strictContent,392,"stl-geometry-only-dialog");
        var insert=new UnitsDialog("fan-shroud.stl",ModelUnit.Millimeters,insert:true,stlMode:StlImportMode.GeometryOnly);
        Assert(insert.StlMode==StlImportMode.GeometryOnly&&Tree((DependencyObject)insert.Content).OfType<Button>().Any(b=>b.Content?.ToString()=="Insert model"),"Insert prompt lost its context or remembered explicit mode.");
        var obj=new UnitsDialog("part.obj",ModelUnit.Millimeters);
        Assert(!Tree((DependencyObject)obj.Content).OfType<CheckBox>().Any(),"STL-only policy leaked into another format.");
        var storePath=Path.Combine(output,"stl-mode-recents.json");
        File.WriteAllText(storePath,JsonSerializer.Serialize(new[]{new{Path=Path.GetFullPath("legacy.stl"),StlUnit=2}}));
        var store=new JsonRecentFileStore(storePath);var legacy=store.Load().Single();
        Assert(legacy.ModelUnit==ModelUnit.Inches&&legacy.StlMode==StlImportMode.Strict,"Old recent file changed units or implicitly permitted color loss.");
        store.Save([legacy with{StlMode=StlImportMode.GeometryOnly}]);
        Assert(store.Load().Single().StlMode==StlImportMode.GeometryOnly,"Explicit mode did not persist separately from model files.");
        Assert(JsonRecentFileStore.Canonical([legacy with{StlMode=(StlImportMode)99}]).Count==0,"Invalid persisted import mode admitted.");
        Pass("STL geometry-only mode requires explicit checkbox choice, explains ignored colors, distinguishes Insert, and keeps legacy recent-file units/default refusal with validated mode persistence");

        try
        {
            await Open("cube-20mm.stl");var before=editor.Snapshot;
            navigationDialogs.OpenPath=Path.GetFullPath(Path.Combine(root,".local/evidence/v1-usability-20261022/inputs/prusa-mk3s/fan-shroud.stl"));
            navigationDialogs.Unit=ModelUnit.Millimeters;navigationDialogs.ImportStlMode=StlImportMode.Strict;
            navigationDialogs.Decision=DirtyDecision.Discard;await ChooseDocument();SameAccepted(before);
            navigationDialogs.ImportStlMode=StlImportMode.GeometryOnly;await ChooseDocument();
            Assert(editor.Snapshot.Scene.Objects.Single().Mesh.Triangles.Length==12588&&editor.Snapshot.FilePath==null,"Explicit geometry-only Open failed or claimed a native save path.");
            var recent=RecentEntries().Single(i=>((RecentFile)i.Tag).Path==navigationDialogs.OpenPath);
            Assert(((RecentFile)recent.Tag).StlMode==StlImportMode.GeometryOnly&&recent.ToolTip!.ToString()!.Contains("colors ignored"),"Recent STL lost its explicit mode or disclosure.");
            var prompts=navigationDialogs.UnitPrompts;await NewDocument();await ClickRecent(RecentEntries().Single(i=>((RecentFile)i.Tag).Path==navigationDialogs.OpenPath));
            Assert(editor.Snapshot.Scene.Objects.Single().Mesh.Triangles.Length==12588&&navigationDialogs.UnitPrompts==prompts,"Recent silently changed explicit mode or source units.");
            await Open("cube-20mm.stl");before=editor.Snapshot;await (Task)Invoke("ChooseModel",true)!;
            Assert(navigationDialogs.LastImportInsert==true&&editor.Snapshot.Scene.Objects.Length==2&&editor.Snapshot.SelectedObjectIds.Length==1,"Insert did not forward geometry-only choice or preserve existing objects.");
            var insertedRecent=(RecentFile)RecentEntries().Single(i=>((RecentFile)i.Tag).Path==Path.GetFullPath(navigationDialogs.OpenPath!)).Tag;Assert(insertedRecent.StlMode==StlImportMode.GeometryOnly&&insertedRecent.ModelUnit==ModelUnit.Millimeters,"Successful Insert did not remember its explicit STL mode and units.");
            Click(Named<Button>("UndoButton"));Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"Geometry-only Insert was not one undoable edit.");
            before=editor.Snapshot;navigationDialogs.Unit=null;await ChooseDocument();SameAccepted(before);
            navigationDialogs.Unit=ModelUnit.Millimeters;navigationDialogs.ImportStlMode=(StlImportMode)99;await ChooseDocument();SameAccepted(before);
            Pass("Actual Open/Insert adapters forward strict versus explicit geometry-only STL choices; Recent retains consent/units, Insert is undoable, and cancelled/invalid choices preserve the scene");
        }
        finally
        {
            navigationDialogs.Unit=ModelUnit.Millimeters;navigationDialogs.ImportStlMode=StlImportMode.Strict;
            navigationDialogs.OpenPath=null;navigationDialogs.Decision=DirtyDecision.Cancel;
        }
    }
}
