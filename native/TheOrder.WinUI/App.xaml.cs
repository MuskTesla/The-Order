using Microsoft.UI.Xaml;
using TheOrder.WinUI.Startup;

namespace TheOrder.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private NativeHost? host;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        host = Composition.Create();
        UnhandledException += (_, error) => host?.LogFatal(error.Exception);
        host.Window.Closed += (_, _) => { host.Dispose(); host = null; };
        host.Window.Activate();
    }
}
