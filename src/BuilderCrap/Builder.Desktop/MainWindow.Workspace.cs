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

    private bool hideItems;
    private void ToggleItemsClick(object sender, RoutedEventArgs e)
    {
        hideItems = !hideItems;
        RefreshWorkspaceNavigation();
    }

    private void RefreshItemsPane(bool active, bool busy)
    {
        // Preserve viewport width and the captured axis during a transform gesture.
        var show = !hideItems || (active && handleMode == null);
        ItemsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleItemsButton.Visibility = active || busy ? Visibility.Collapsed : Visibility.Visible;
        ToggleItemsButton.Content = hideItems ? "❮" : "❯";
        var action = hideItems ? "Show items pane" : "Hide items pane";
        ToggleItemsButton.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(ToggleItemsButton, action);
    }

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
            RefreshItemsPane(active,busy);
            if(active&&!workspaceHadTool)workspaceOwnerIndex=workspaceLastIndex;
            workspaceHadTool=active;
            var transformActive=handleMode.HasValue;
            WorkspaceTabs.IsEnabled=!busy || transformActive;
            for(var i=0;i<WorkspaceTabs.Items.Count;i++)
                ((TabItem)WorkspaceTabs.Items[i]).IsEnabled=transformActive || !busy&&(!active||i==workspaceOwnerIndex||i==ViewWorkspaceIndex);
            foreach(var (commands,index) in new[]{(InsertCommands,0),(ObjectCommands,1),(EditCommands,2),(PaintCommands,WorkspaceTabs.Items.IndexOf(PaintWorkspaceTab)),(ViewCommands,ViewWorkspaceIndex)})
            {
                // Keep the camera projection fixed from pointer-down through release.
                ((FrameworkElement)commands.Parent).Visibility=active&&handleMode==null&&index!=ViewWorkspaceIndex?Visibility.Collapsed:Visibility.Visible;
                commands.IsEnabled=!busy&&(!active||index==ViewWorkspaceIndex);
            }
            AboutModelButton.IsEnabled=!active&&!busy;
            RecoverButton.IsEnabled=recoveryDirectory!=null&&!active&&!busy;
            Toolbar.IsEnabled=!active&&!busy;
            ObjectsOverview.Visibility=active&&handleMode==null?Visibility.Collapsed:Visibility.Visible;
            ObjectsOverview.IsEnabled=!busy;
            ObjectList.IsEnabled=!active&&!busy;
            ConstructionTools.Visibility=HandleTools.Visibility=InspectionTools.Visibility=active&&handleMode==null?Visibility.Collapsed:Visibility.Visible;
            EditActions.Visibility=EditActionsDivider.Visibility=active&&!transformActive?Visibility.Collapsed:Visibility.Visible;
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
