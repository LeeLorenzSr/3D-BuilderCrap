using System.Windows.Controls;

internal static partial class Program
{
    // Release smoke checks only: no Narrator, contrast, DPI or accessibility test entry points.
    private static async Task RunNormalWorkflowControls()
    {
        await CheckMetricFloor();
        await CheckSelectionSilhouette();
        await CheckScaleFootprint();
        await V1TransformKeys();
        await ExactTypedTransforms();
        await V1ConstructionTextKeys();
        await V1OperationDrafts();
        await V1NativePromptGuard();
        await RunDocumentNavigationControls();
        await RunStlImportControls();
    }

    private static async Task ExactTypedTransforms()
    {
        await Open("cube-20mm.stl");
        await Workspace(1);
        var before = editor.Snapshot;
        var snap = Named<CheckBox>("SnapTransforms");
        snap.IsChecked = true;
        async Task CommitNumber(string mode, string field, double value)
        {
            Click(Named<Button>(mode+"HandleButton"));
            ArrayNumber(field,value);
            Reachable("CommitTransformButton");
            Click(Named<Button>("CommitTransformButton"));
            await Idle();
        }
        await CommitNumber("Move","PositionX",10.4);
        Near(editor.Snapshot.Scene.Bounds.Center.X,10.4);
        Click(Named<Button>("UndoButton"));
        await CommitNumber("Rotate","RotationZ",7.5);
        var radians=7.5*Math.PI/180;
        Near(editor.Snapshot.Scene.Bounds.Size.X,20*(Math.Cos(radians)+Math.Sin(radians)));
        Click(Named<Button>("UndoButton"));
        await CommitNumber("Scale","SizeX",20.4);
        Near(editor.Snapshot.Scene.Bounds.Size.X,20.4);
        Click(Named<Button>("UndoButton"));
        Assert(ReferenceEquals(editor.Snapshot.Scene,before.Scene),"One Undo must restore the original size.");
        Click(Named<Button>("RedoButton"));
        var saved = System.IO.Path.Combine(output,"typed-dimension.3mf");
        navigationDialogs.SavePath = saved;
        Assert(await (Task<bool>)Invoke("Save",true)!,"Typed dimension Save failed.");
        navigationDialogs.OpenPath = saved;
        await ChooseDocument();
        Near(editor.Snapshot.Scene.Bounds.Size.X,20.4);
        Assert(snap.IsChecked==true && !editor.Snapshot.IsDirty,"Reopening lost snap preference or dirty state.");
        Pass("Typed position, angle and size keep fractional values with handle snapping enabled. A 20.4 mm size survives Apply, Undo, Redo, Save and Open.");
    }
}
