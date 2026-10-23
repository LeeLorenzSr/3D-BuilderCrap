using Builder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Builder.Desktop;

public partial class MainWindow
{
    private readonly IRecentFileStore recentStore;
    private readonly IDocumentDialogs documentDialogs;
    private IReadOnlyList<RecentFile> recentFiles=[];
    private bool documentPromptActive;
    private long? closeApprovedRevision;
    private bool DocumentNavigationAvailable=>!WorkspaceToolActive&&!WorkspaceBusy&&!documentPromptActive;
    private enum DocumentAction { New,Open,Close }
    private void InitializeDocumentNavigation()
    {
        try{recentFiles=JsonRecentFileStore.Canonical(recentStore.Load());}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {Message("Recent files could not be loaded. Your scene is unaffected.",true);}
        RefreshRecentMenu();
    }
    private void RefreshRecentMenu()
    {
        RecentFilesMenu.Items.Clear();
        if(recentFiles.Count==0)RecentFilesMenu.Items.Add(new MenuItem{Header="No recent files",IsEnabled=false});
        foreach(var entry in recentFiles)
        {
            var label=new StackPanel();label.Children.Add(new TextBlock{Text=Path.GetFileName(entry.Path),FontWeight=FontWeights.SemiBold});
            var folder=new TextBlock{Text=Path.GetDirectoryName(entry.Path),FontSize=11,MaxWidth=420,TextTrimming=TextTrimming.CharacterEllipsis,TextWrapping=TextWrapping.NoWrap};folder.SetResourceReference(TextBlock.ForegroundProperty,"MutedInk");label.Children.Add(folder);
            var item=new MenuItem{Header=label,Tag=entry,ToolTip=entry.Path+(NeedsModelUnits(entry.Path)?"\nSource units: "+entry.ModelUnit:"")+(entry.StlMode==StlImportMode.GeometryOnly?"\nSTL: geometry only; nonstandard colors ignored":"")};
            System.Windows.Automation.AutomationProperties.SetName(item,Path.GetFileName(entry.Path)+" · "+Path.GetDirectoryName(entry.Path));
            item.Click+=RecentFileClick;RecentFilesMenu.Items.Add(item);
        }
        RecentFilesMenu.Items.Add(new Separator());var clear=new MenuItem{Header="Clear recent list",IsEnabled=recentFiles.Count>0};clear.Click+=ClearRecentFilesClick;RecentFilesMenu.Items.Add(clear);
    }
    private void RememberFile(string path,ModelUnit unit=ModelUnit.Millimeters,StlImportMode stlMode=StlImportMode.Strict)
    {
        var full=Path.GetFullPath(path);recentFiles=JsonRecentFileStore.Canonical(new[]{new RecentFile(full,unit,stlMode)}.Concat(recentFiles.Where(e=>!e.Path.Equals(full,StringComparison.OrdinalIgnoreCase))));PersistRecents();
    }
    private void PersistRecents()
    {
        RefreshRecentMenu();
        try{recentStore.Save(recentFiles);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {Message("The document action succeeded, but the recent list could not be saved. It is available for this session.",true);}
    }
    private void ClearRecentFilesClick(object sender,RoutedEventArgs e)
    {if(!DocumentNavigationAvailable)return;recentFiles=[];PersistRecents();}
    private async void RecentFileClick(object sender,RoutedEventArgs e)
    {if(sender is MenuItem{Tag:RecentFile entry})await OpenRecentFile(entry);}
    private Task<DocumentTransitionResult?> OpenRecentFile(RecentFile entry)=>NavigateDocument(DocumentAction.Open,entry.Path,entry.ModelUnit,stlMode:entry.StlMode);
    private async void NewClick(object sender,RoutedEventArgs e)=>await NavigateDocument(DocumentAction.New);

    // Dialog decisions belong to this adapter; Core owns the revision-checked Save+transition lease.
    private async Task<DocumentTransitionResult?> NavigateDocument(DocumentAction action,string? openPath=null,ModelUnit units=ModelUnit.Millimeters,EditorSnapshot? initial=null,StlImportMode stlMode=StlImportMode.Strict)
    {
        if(!DocumentNavigationAvailable){Message("Finish the active tool or operation before changing documents.");return null;}
        var captured=initial??editor.Snapshot;DirtyDecision decision=DirtyDecision.RequireDecision;string? savePath=null;
        documentPromptActive=true;RefreshWorkspaceNavigation();
        try
        {
            if(captured.IsDirty)
            {
                decision=documentDialogs.ConfirmChanges(this,captured,action switch{DocumentAction.New=>"starting a new scene",DocumentAction.Open=>"opening another model",_=>"closing"});
                if(decision==DirtyDecision.Cancel){Message("Document action cancelled. Your scene is unchanged.");return null;}
                if(decision==DirtyDecision.Save)
                {
                    savePath=captured.FilePath??documentDialogs.ChooseSavePath(this);
                    if(savePath==null){Message("Save cancelled. Your scene is unchanged.");return null;}
                }
            }
        }
        finally{documentPromptActive=false;RefreshWorkspaceNavigation();}
        DocumentTransition request=action switch
        {
            DocumentAction.New=>new NewDocumentTransition(captured.Revision,decision,savePath),
            DocumentAction.Open=>new OpenDocumentTransition(openPath!,captured.Revision,units,decision,savePath){StlMode=stlMode},
            _=>new CloseDocumentTransition(captured.Revision,decision,savePath)
        };
        DocumentTransitionResult? transitioned=null;
        await RunOperation(async(ct,progress)=>{transitioned=await editor.TransitionAsync(request,ct,progress);return transitioned.Result;});
        if(transitioned==null)return null;
        if(transitioned.SaveCommitted&&transitioned.SavedPath is {} saved)RememberFile(saved);
        if(transitioned.Result.Succeeded&&action!=DocumentAction.Close)
        {
            if(action==DocumentAction.Open)RememberFile(openPath!,units,stlMode);
            CancelPendingPick();useOrthographic=false;yaw=-.85;pitch=.55;WorkspaceTabs.SelectedIndex=action==DocumentAction.New?0:1;Refresh();FitScene();ViewportSurface.Focus();
        }
        if(transitioned.SaveCommitted&&!transitioned.Result.Succeeded)Message("The current scene was saved. "+transitioned.Result.Message,true);
        return transitioned;
    }
    private async Task ChooseModel(bool insert)
    {
        if(!DocumentNavigationAvailable)return;
        var captured=editor.Snapshot;
        if(insert&&captured.EditingGroupId!=null){Message("Choose Done before inserting models into the scene.");return;}
        string? file;var unit=ModelUnit.Millimeters;var stlMode=StlImportMode.Strict;
        documentPromptActive=true;RefreshWorkspaceNavigation();
        try
        {
            file=documentDialogs.ChooseOpenPath(this,insert);if(file==null)return;
            if(NeedsModelUnits(file))
            {
                var known=recentFiles.FirstOrDefault(e=>e.Path.Equals(Path.GetFullPath(file),StringComparison.OrdinalIgnoreCase));
                var choice=documentDialogs.ChooseImportOptions(this,file,known?.ModelUnit??UnitsDialog.DefaultUnit(file),insert,known?.StlMode??StlImportMode.Strict);
                if(choice==null){Message("Import cancelled. Your scene is unchanged.");return;}unit=choice.Unit;stlMode=choice.StlMode;
            }
        }
        finally{documentPromptActive=false;RefreshWorkspaceNavigation();}
        if(insert)
        {var result=await RunOperation((ct,p)=>editor.InsertAsync(file,unit,ct,p,captured.Revision,stlMode));if(result.Succeeded){RememberFile(file,unit,stlMode);FitScene();}}
        else await NavigateDocument(DocumentAction.Open,file,unit,captured,stlMode);
    }
    private static bool NeedsModelUnits(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".stl" or ".obj" or ".ply" or ".gltf" or ".glb";
    private async Task<bool> Save(bool choosePath)
    {
        if(!DocumentNavigationAvailable)return false;
        var captured=editor.Snapshot;
        if(captured.Scene.Objects.IsEmpty){Message("There are no objects to save. Add an object or use Undo to restore one. When changing documents, choose Cancel to keep editing or Discard to continue.",true);return false;}
        var path=choosePath?null:captured.FilePath;
        if(path==null)
        {
            documentPromptActive=true;RefreshWorkspaceNavigation();
            try{path=documentDialogs.ChooseSavePath(this);}
            finally{documentPromptActive=false;RefreshWorkspaceNavigation();}
            if(path==null){Message("Save cancelled. Your scene is unchanged.");return false;}
        }
        var result=await RunOperation((ct,p)=>editor.SaveAsync(path,ct,p,captured.Revision));
        if(result.Succeeded)RememberFile(path);
        return result.Succeeded;
    }
    private async Task<bool> RequestCloseDocument()
    {
        recoveryClosePending=true;
        recoveryCheckpointCancellation?.Cancel();await recoveryCheckpointTask;
        var result=await NavigateDocument(DocumentAction.Close);
        if(result is not {ReadyToClose:true}){recoveryClosePending=false;return false;}
        var current=editor.Snapshot;
        if(current.Revision!=result.Revision||current.IsBusy||current.Preview!=null||!DocumentNavigationAvailable){recoveryClosePending=false;return false;}
        closeApproved=true;closeApprovedRevision=result.Revision;
        return true;
    }
}
