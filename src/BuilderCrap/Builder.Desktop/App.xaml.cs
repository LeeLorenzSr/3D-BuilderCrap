using System.Windows;
using System.IO;
using Builder.Hosting;
namespace Builder.Desktop;
public partial class App : Application
{
    private InstallationLease? installation;
    protected override void OnStartup(StartupEventArgs e)
    {
        try { installation = InstallationLease.Acquire(AppContext.BaseDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(error.Message, "3D BuilderCrap installation", MessageBoxButton.OK, MessageBoxImage.Information); Shutdown(2); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    { base.OnExit(e); installation?.Dispose(); }
}
