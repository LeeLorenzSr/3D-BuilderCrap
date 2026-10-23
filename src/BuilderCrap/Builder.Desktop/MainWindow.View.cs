using Builder.Core;
using System.Windows;

namespace Builder.Desktop;

public partial class MainWindow
{
    private bool viewControlsReady,viewControlsUpdating;
    private void InitializeViewControls(){viewControlsReady=true;SyncViewControls();}
    private void SyncViewControls()
    {
        if(!viewControlsReady||viewControlsUpdating)return;
        viewControlsUpdating=true;
        try{var settings=editor.Presentation.Snapshot.Settings;ViewGrid.IsChecked=settings.Grid;ViewColors.IsChecked=settings.Colors;ViewWireframe.IsChecked=settings.Wireframe;ViewXRay.IsChecked=settings.XRay;ViewShadows.IsChecked=settings.Shadows;ViewReflections.IsChecked=settings.Reflections;ViewShadingFlat.IsChecked=settings.Shading==ShadingMode.Flat;ViewShadingSmooth.IsChecked=settings.Shading==ShadingMode.Smooth;SyncViewPreferenceStatus();}
        finally{viewControlsUpdating=false;}
    }
    private void ViewSettingsChanged(object sender,RoutedEventArgs e)
    {
        if(!viewControlsReady||viewControlsUpdating)return;
        var snapshot=editor.Presentation.Snapshot;
        var settings=snapshot.Settings;
        if(ReferenceEquals(sender,ViewGrid))settings=settings with{Grid=ViewGrid.IsChecked==true};
        else if(ReferenceEquals(sender,ViewColors))settings=settings with{Colors=ViewColors.IsChecked==true};
        else if(ReferenceEquals(sender,ViewWireframe))settings=settings with{Wireframe=ViewWireframe.IsChecked==true};
        else if(ReferenceEquals(sender,ViewXRay))settings=settings with{XRay=ViewXRay.IsChecked==true};
        else if(ReferenceEquals(sender,ViewShadows))settings=settings with{Shadows=ViewShadows.IsChecked==true};
        else if(ReferenceEquals(sender,ViewReflections))settings=settings with{Reflections=ViewReflections.IsChecked==true};
        else return;
        var result=editor.Presentation.Update(settings,snapshot.Revision);
        if(!result.Succeeded)Message(result.Message,true);
        RenderScene(); // Presentation only: retain tool drafts, owned preview and accepted history.
    }
    private void SyncViewPreferenceStatus()
    {
        var status=editor.Presentation.PreferenceStatus;
        var warning=status.State is ViewPreferenceState.LoadFailed or ViewPreferenceState.SaveFailed;
        ViewPreferenceWarning.Visibility=warning?Visibility.Visible:Visibility.Collapsed;
        ViewPreferenceHelp.Text=status.State==ViewPreferenceState.MemoryOnly
            ?"View choices apply to this session. Reset leaves the camera unchanged."
            :"View choices are remembered. Reset saves defaults; camera unchanged.";
        ViewPreferenceWarningText.Text=status.State==ViewPreferenceState.LoadFailed
            ?"Saved view choices could not be loaded. This session's choices are in use."
            :"View choices could not be saved. They apply to this session only.";
        ViewPreferenceWarning.ToolTip=status.Error;
    }
    private void SaveViewChoicesClick(object sender,RoutedEventArgs e)
    {
        var snapshot=editor.Presentation.Snapshot;
        // Save the displayed choices without reloading. Persistence warnings stay separate from edit status.
        var result=editor.Presentation.Update(snapshot.Settings,snapshot.Revision);
        if(!result.Succeeded)Message(result.Message,true);
        RenderScene();
    }
    private void ResetDisplayClick(object sender,RoutedEventArgs e)
    {
        var snapshot=editor.Presentation.Snapshot;var result=editor.Presentation.Reset(snapshot.Revision);
        if(!result.Succeeded)Message(result.Message,true);
        RenderScene();
    }
    private void ViewShadingChanged(object sender,RoutedEventArgs e)
    {
        if(!viewControlsReady||viewControlsUpdating)return;
        var snapshot=editor.Presentation.Snapshot;
        var shading=ReferenceEquals(sender,ViewShadingSmooth)?ShadingMode.Smooth:ShadingMode.Flat;
        var result=editor.Presentation.Update(snapshot.Settings with{Shading=shading},snapshot.Revision);
        if(!result.Succeeded)Message(result.Message,true);
        RenderScene();
    }
}
