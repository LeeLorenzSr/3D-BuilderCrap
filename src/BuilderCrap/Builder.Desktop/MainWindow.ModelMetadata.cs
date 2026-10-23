using System.Windows;

namespace Builder.Desktop;

public partial class MainWindow
{
    private void AboutModelClick(object sender,RoutedEventArgs e)
    {
        if(!DocumentNavigationAvailable){Message("Finish the active tool or operation before editing model information.");return;}
        var captured=editor.Snapshot;
        var dialog=new ModelMetadataDialog(captured.Scene.Metadata,metadata=>editor.UpdateModelMetadata(metadata,captured.Revision));
        documentPromptActive=true;RefreshWorkspaceNavigation();
        try{documentDialogs.ShowModelMetadata(this,dialog);}
        finally{documentPromptActive=false;RefreshWorkspaceNavigation();}
        if(dialog.Accepted)
        {if(editor.Snapshot.Revision!=captured.Revision)Refresh();Message(dialog.LastResult!.Message);}
        else Message("Model information cancelled. Your scene is unchanged.");
    }
}
