using Builder.Core;
using System.Windows;
using System.Windows.Controls;

namespace Builder.Desktop;

public partial class MainWindow
{
    // Workspace selection is presentation state only; tool drafts and Core previews keep their owner.
    private bool workspaceReady,workspaceUpdating,workspaceHadTool;
    private int workspaceLastIndex,workspaceOwnerIndex;
    private bool WorkspaceToolActive=>ModalToolActive||constructionMode!=null||handleMode!=null||arrangementMode!=null;
    private int ViewWorkspaceIndex=>WorkspaceTabs.Items.IndexOf(ViewWorkspaceTab);
    private bool WorkspaceBusy=>operation!=null||editor.Snapshot.IsBusy||transformUpdating||handleDrag!=null||documentPromptActive||brushGesture!=null;

    private void InitializeWorkspaceNavigation()
    {workspaceReady=true;workspaceLastIndex=WorkspaceTabs.SelectedIndex;RefreshWorkspaceNavigation();}
    private void WorkspaceTabChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!workspaceReady||workspaceUpdating||e.Source!=WorkspaceTabs)return;
        var next=WorkspaceTabs.SelectedIndex;
        if(WorkspaceBusy||WorkspaceToolActive&&next!=workspaceOwnerIndex&&next!=ViewWorkspaceIndex)
        {
            workspaceUpdating=true;WorkspaceTabs.SelectedIndex=workspaceLastIndex;workspaceUpdating=false;
            Message("Finish the active tool with Apply, Cancel or Done before changing workspaces.");return;
        }
        workspaceLastIndex=next;
        // Do not call Refresh: it initializes document fields and would overwrite an uncommitted draft.
        RefreshWorkspaceNavigation();
    }
    private void RefreshWorkspaceNavigation()
    {
        if(!workspaceReady||workspaceUpdating)return;
        workspaceUpdating=true;
        try
        {
            var active=WorkspaceToolActive;var busy=WorkspaceBusy;
            if(active&&!workspaceHadTool)workspaceOwnerIndex=workspaceLastIndex;
            workspaceHadTool=active;
            WorkspaceTabs.IsEnabled=!busy;
            for(var i=0;i<WorkspaceTabs.Items.Count;i++)
                ((TabItem)WorkspaceTabs.Items[i]).IsEnabled=!busy&&(!active||i==workspaceOwnerIndex||i==ViewWorkspaceIndex);
            foreach(var (commands,index) in new[]{(InsertCommands,0),(ObjectCommands,1),(EditCommands,2),(PaintCommands,WorkspaceTabs.Items.IndexOf(PaintWorkspaceTab)),(ViewCommands,ViewWorkspaceIndex)})
            {
                // The active category keeps its header; View keeps compatible camera commands.
                ((FrameworkElement)commands.Parent).Visibility=active&&index!=ViewWorkspaceIndex?Visibility.Collapsed:Visibility.Visible;
                commands.IsEnabled=!busy&&(!active||index==ViewWorkspaceIndex);
            }
            AboutModelButton.IsEnabled=!active&&!busy;
            RecoverButton.IsEnabled=recoveryDirectory!=null&&!active&&!busy;
            Toolbar.IsEnabled=!active&&!busy;
            ObjectsOverview.Visibility=active?Visibility.Collapsed:Visibility.Visible;
            ObjectsOverview.IsEnabled=!busy;
            ObjectList.IsEnabled=!active&&!busy;
            EditPanel.Visibility=handleMode!=null?Visibility.Visible:Visibility.Collapsed;
            ConstructionTools.Visibility=HandleTools.Visibility=InspectionTools.Visibility=active?Visibility.Collapsed:Visibility.Visible;
            EditActions.Visibility=EditActionsDivider.Visibility=active?Visibility.Collapsed:Visibility.Visible;
            if(!active){RenameFields.Visibility=ObjectSummary.Visibility=Visibility.Visible;}
            var canCreate=!active&&!busy&&editor.Snapshot.EditingGroupId==null;
            foreach(var button in InsertShapePalette.Children.OfType<Button>())button.IsEnabled=canCreate;
            NewTextButton.IsEnabled=ImageReliefButton.IsEnabled=InsertButton.IsEnabled=canCreate;
            if(!active)
            {
                var selected=SelectedTarget!=null;
                MoveHandleButton.IsEnabled=RotateHandleButton.IsEnabled=ScaleHandleButton.IsEnabled=BedButton.IsEnabled=selected&&!busy;
            }
        }
        finally{workspaceUpdating=false;}
    }
}
