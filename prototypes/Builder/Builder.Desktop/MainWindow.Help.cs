using System.Windows;

namespace Builder.Desktop;

public partial class MainWindow
{
    private HelpWindow? helpWindow;
    private void HelpClick(object sender,RoutedEventArgs e)
    {
        if(helpWindow is not null){helpWindow.Activate();return;}
        helpWindow=new(){Owner=this};helpWindow.Closed+=(_,_)=>helpWindow=null;helpWindow.Show();
    }
}
