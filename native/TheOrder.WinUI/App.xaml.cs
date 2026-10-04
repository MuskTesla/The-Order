using Microsoft.UI.Xaml;
using TheOrder.WinUI.Startup;

namespace TheOrder.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private NativeHost? host;
    private bool closing, allowClose;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        host = Composition.Create();
        UnhandledException += (_, error) => host?.LogFatal(error.Exception);
        host.Window.AppWindow.Closing += async (_, close) =>
        {
            if (allowClose) return;
            close.Cancel = true; // Keep the dispatcher/window alive while accepted work drains.
            if (closing || host is null) return;
            closing = true;
            var owner = host;
            owner.Window.CancelOnlineRequests();
            try
            {
                if (!await owner.Window.ResolveUnsavedAsync()) { closing = false; return; }
                await owner.DisposeAsync();
                // Post a new close after this Closing callback returns, including idle shutdown.
                owner.Window.DispatcherQueue.TryEnqueue(() => { allowClose = true; owner.Window.Close(); });
            }
            catch (Exception error) { owner.LogFatal(error); closing = false; }
        };
        host.Window.Closed += (_, _) => host = null;
        host.Window.Activate();
    }
}
