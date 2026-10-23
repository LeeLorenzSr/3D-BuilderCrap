using Builder.Core;
using Builder.Desktop;
using System.IO;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private sealed class PreferenceFaultStore : IViewPreferenceStore
    {
        public ViewSettings? Value=ViewSettings.Default;
        public bool FailSave;
        public int Loads,Saves;
        public ViewSettings? Load(){Loads++;return Value;}
        public void Save(ViewSettings settings){Saves++;if(FailSave)throw new IOException("Injected preferences write failure.");Value=settings;}
    }
    private static async Task PreferenceWindow(IViewPreferenceStore? store)
    {
        editor.Dispose();
        window=new MainWindow(new JsonRecentFileStore(Path.Combine(output,Guid.NewGuid()+"-recents.json")),navigationDialogs,viewPreferenceStore:store);
        editor=(EditorSession)typeof(MainWindow).GetField("editor",PrivateInstance)!.GetValue(window)!;surface=(FrameworkElement)window.Content;
        Refresh();Layout(1040,680);await Workspace(4);
    }
    private static bool PreferenceWarning()=>Named<Border>("ViewPreferenceWarning").Visibility==Visibility.Visible;
    private static async Task RunViewPreferenceControls()
    {
        Assert(editor.Presentation.PreferenceStatus.State==ViewPreferenceState.MemoryOnly&&!PreferenceWarning(),"Existing injected check windows silently configured real preferences.");
        var path=Path.Combine(output,"view-preferences.json");Assert(!File.Exists(path),"Use a fresh evidence root for preference lifecycle checks.");await PreferenceWindow(new JsonViewPreferenceStore(path));
        Assert(!File.Exists(path)&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Missing&&!PreferenceWarning(),"Missing preferences caused startup writes/warnings.");
        ViewToggle("ViewGrid",false);ViewToggle("ViewColors",false);ViewToggle("ViewWireframe",true);ViewToggle("ViewXRay",true);ViewToggle("ViewShadows",false);ViewToggle("ViewReflections",true);SetShading(true);
        var choices=editor.Presentation.Snapshot.Settings;var bytes=File.ReadAllBytes(path);await PreferenceWindow(new JsonViewPreferenceStore(path));
        Assert(editor.Presentation.Snapshot.Settings==choices&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Loaded,"A second actual window did not restore all saved choices.");
        Assert(Named<CheckBox>("ViewGrid").IsChecked==false&&Named<CheckBox>("ViewColors").IsChecked==false&&Named<CheckBox>("ViewWireframe").IsChecked==true&&Named<CheckBox>("ViewXRay").IsChecked==true&&Named<CheckBox>("ViewShadows").IsChecked==false&&Named<CheckBox>("ViewReflections").IsChecked==true&&Named<RadioButton>("ViewShadingSmooth").IsChecked==true,"Loaded choices did not initialize controls before rendering.");
        Assert(File.ReadAllBytes(path).SequenceEqual(bytes)&&editor.Snapshot.Scene.Objects.IsEmpty&&!editor.Snapshot.IsDirty,"Startup rewrote preferences or changed the new document.");
        Pass("Injected windows remain memory-only; a missing store is quiet/no-write, and two actual windows sharing only a temporary JSON store restore every display choice without document mutation");

        var camera=CameraPose();Click(Named<Button>("ResetDisplayButton"));Assert(editor.Presentation.Snapshot.Settings==ViewSettings.Default&&CameraPose()==camera,"Reset did not restore defaults or changed camera.");await PreferenceWindow(new JsonViewPreferenceStore(path));Assert(editor.Presentation.Snapshot.Settings==ViewSettings.Default,"Reset defaults did not survive a new window.");
        new JsonViewPreferenceStore(path).Save(choices);var revision=editor.Presentation.Snapshot.Revision;Click(Named<Button>("ResetDisplayButton"));Assert(editor.Presentation.Snapshot.Revision==revision,"Equal Reset changed presentation revision.");await PreferenceWindow(new JsonViewPreferenceStore(path));Assert(editor.Presentation.Snapshot.Settings==ViewSettings.Default,"Equal Reset failed to explicitly overwrite stale persisted choices.");
        var corrupt=Path.Combine(output,"corrupt-preferences.json");File.WriteAllText(corrupt,"{",new System.Text.UTF8Encoding(false));await PreferenceWindow(new JsonViewPreferenceStore(corrupt));
        Assert(editor.Presentation.Snapshot.Settings==ViewSettings.Default&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.LoadFailed&&PreferenceWarning()&&File.ReadAllText(corrupt)=="{","Corrupt startup lost defaults, rewrote file or hid warning.");
        Assert(Named<TextBlock>("ViewPreferenceWarningText").Text.Contains("loaded"),"Load failure guidance does not distinguish failed reading.");Layout(1040,680);foreach(var name in new[]{"SaveViewChoicesButton","ViewPreferenceWarningText","ResetDisplayButton","ViewReflections","ViewPreferenceHelp"})Reachable(name);Assert(Named<FrameworkElement>("ViewportSurface").ActualHeight>=250,"Preferences warning consumed minimum viewport.");Capture("preferences-load-warning-minimum",1040,680);
        Click(Named<Button>("SaveViewChoicesButton"));Assert(!PreferenceWarning()&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.Saved,"Save view choices did not repair invalid persisted settings.");await PreferenceWindow(new JsonViewPreferenceStore(corrupt));Assert(editor.Presentation.Snapshot.Settings==ViewSettings.Default&&!PreferenceWarning(),"Repaired choices could not load in another actual window.");Capture("preferences-remembered-minimum",1040,680);
        Pass("Reset remembers defaults even when already selected, without camera/revision changes; corrupt load preserves file/defaults, shows compact minimum-window guidance and is repaired by explicitly saving current choices");

        var store=new PreferenceFaultStore{FailSave=true};await PreferenceWindow(store);await Open("cube-20mm.stl");await Workspace(4);var before=editor.Snapshot;camera=CameraPose();Named<TextBox>("ObjectName").Text="Keep this unsaved name draft";var status=Named<TextBlock>("Status").Text;
        ViewToggle("ViewReflections",true);Assert(editor.Presentation.Snapshot.Settings.Reflections&&editor.Presentation.PreferenceStatus.State==ViewPreferenceState.SaveFailed&&PreferenceWarning(),"Write failure rolled back the display or omitted its warning.");SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Keep this unsaved name draft"&&Named<TextBlock>("Status").Text==status&&CameraPose()==camera,"Persistence warning overwrote edit status, idle draft or camera.");Assert(Named<TextBlock>("ViewPreferenceWarningText").Text.Contains("session only"),"Save failure does not explain current-session persistence.");Capture("preferences-save-warning-minimum",1040,680);
        revision=editor.Presentation.Snapshot.Revision;var loads=store.Loads;store.FailSave=false;Click(Named<Button>("SaveViewChoicesButton"));Assert(store.Loads==loads&&store.Value==editor.Presentation.Snapshot.Settings&&editor.Presentation.Snapshot.Revision==revision&&!PreferenceWarning(),"Save view choices reloaded old data, changed settings revision or left warning.");SameAccepted(before);Assert(Named<TextBox>("ObjectName").Text=="Keep this unsaved name draft"&&Named<TextBlock>("Status").Text==status,"Successful preference retry reset editing drafts/status.");
        Pass("Failed writes remain nonmodal and keep selected display choices, accepted history, camera and idle text/status; Save view choices retries current settings without reloading or changing revision");

        store=new PreferenceFaultStore{FailSave=true};await PreferenceWindow(store);await Open("cube-20mm.stl");Click(Named<Button>("MoveHandleButton"));Named<TextBox>("PositionX").Text="27.5";Click(Named<Button>("PreviewTransformButton"));await Idle();before=editor.Snapshot;var candidate=before.Preview!;Named<TextBox>("PositionY").Text="13.25";await Workspace(4);camera=CameraPose();status=Named<TextBlock>("Status").Text;ViewToggle("ViewGrid",false);
        Assert(PreferenceWarning()&&ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<TextBox>("PositionY").Text=="13.25"&&!Named<Button>("ApplyTransformButton").IsEnabled&&Named<TextBlock>("Status").Text==status&&CameraPose()==camera,"Preference save failure changed owned dirty preview/draft or edit status.");TransformFooterVisible();Capture("preferences-preview-warning-minimum",1040,680);store.FailSave=false;Click(Named<Button>("SaveViewChoicesButton"));Assert(!PreferenceWarning()&&ReferenceEquals(editor.Snapshot.Preview,candidate)&&Named<TextBox>("PositionY").Text=="13.25","Preference retry changed preview/draft.");Click(Named<Button>("CancelTransformButton"));SameAccepted(before);
        Pass("Active dirty transform preview survives failed preference saving and successful retry with fixed minimum-window Cancel, then cancels back to the exact accepted document");
    }
}
