using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using TheOrder.WinUI.Startup;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;

namespace TheOrder.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    internal static App? Instance { get; private set; }
    private NativeHost? host;
    private WindowsTrayLifecycle? tray;
    private readonly WindowLifetime windowLifetime = new();
    private bool exiting, allowClose;
    public bool TrayAvailable => tray?.Available == true;
    public bool WindowHidden => windowLifetime.Hidden;
    public App()
    {
        Instance = this;
        if (Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_VISUAL_TEST") == "1" && Program.Profile?.Manifest.ProfileKind == TheOrder.Application.NativeProfileKind.Test)
            UnhandledException += (_, error) => TraceVisualFailure(error.Exception);
        InitializeComponent();
    }
    internal static void TraceVisualFailure(Exception exception)
    {
        if (Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_VISUAL_TEST") != "1" || Program.Profile?.Manifest.ProfileKind != TheOrder.Application.NativeProfileKind.Test) return;
        try { if (Program.Profile.Configuration(false).DataDirectory is { } directory) File.WriteAllText(Path.Combine(directory, "visual-fatal.txt"), exception.ToString()); } catch { }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        host = Composition.Create();
        host.LogStarted();
        UnhandledException += (_, error) => host?.LogFatal(error.Exception);
        host.Window.AppWindow.Closing += (sender, close) =>
        {
            if (allowClose) return;
            close.Cancel = true;
            if (exiting) return;
            windowLifetime.TrayAvailable = TrayAvailable;
            if (windowLifetime.CloseToTray()) { host.Window.SetVisualWindowVisible(false); host.Window.AppWindow.Hide(); _ = CheckpointHiddenAsync(); }
            // No tray means no way back to a hidden window. × then takes the same orderly
            // exit as the tray menu (drafts, Session finalization, cleanup) and can be cancelled.
            else { host.Window.ShowLifecycleError(windowLifetime.Error!); _ = RequestExitAsync(); }
        };
        host.Window.Closed += (_, _) => { tray?.Dispose(); tray = null; host = null; Instance = null; };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(host.Window);
        try
        {
            var failure = Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_DIAGNOSTICS") == "1" && Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_TRAY_FAILURE") == "1";
            tray = new(hwnd, TrayAction, QuerySystemEnd, EndSystemSession, failure);
            tray.Unavailable += TrayUnavailable;
            if (!tray.Available) TrayUnavailable();
        }
        catch (Exception) { TrayUnavailable(); }
        host.Window.Activate();
        if (Program.Activations > 0) RequestActivation();
    }
    private async Task CheckpointHiddenAsync()
    { try { if (host is { } owner) await owner.Sessions.CheckpointAsync(); } catch { RequestActivation(); host?.Window.ShowLifecycleError("游玩记录暂未保存，请检查后重试。"); } }
    private void TrayUnavailable() { RequestActivation(); host?.Window.ShowLifecycleError("托盘不可用：关闭窗口会按正常流程退出 The Order，先处理草稿并保存游玩记录。"); }
    internal void RefreshTray(SessionPresentation value) => tray?.Update(value.Title, value.State, value.CanPause, value.CanResume);
    internal void RequestActivation()
    {
        var window = host?.Window;
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (host is null || allowClose) return;
            windowLifetime.Show(); window.AppWindow.Show(); window.SetVisualWindowVisible(true);
            if (window.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
            window.Activate();
        });
    }
    private async void TrayAction(TrayCommand command)
    {
        try
        {
            if (host is not { } owner) return;
            if (command == TrayCommand.Show) RequestActivation();
            else if (command == TrayCommand.Pause) await owner.Sessions.PauseAsync();
            else if (command == TrayCommand.Resume) await owner.Sessions.ResumeAsync();
            else if (command == TrayCommand.Exit) await RequestExitAsync();
        }
        catch (Exception) { RequestActivation(); host?.Window.ShowLifecycleError("托盘操作未完成，请在窗口中重试。"); }
    }
    internal async Task RequestExitAsync()
    {
        if (exiting || host is not { } owner) return;
        exiting = true; RequestActivation(); owner.Window.SetCommandsBlocked(true);
        try
        {
            owner.Window.CancelBackgroundRequests();
            if (!await owner.Window.ResolveUnsavedAsync()) return;
            if (!await owner.Sessions.PrepareExitAsync()) { owner.Window.ShowLifecycleError(owner.Sessions.Error ?? "退出已取消，请重试。"); return; }
            await owner.DisposeAsync();
            tray?.Dispose(); tray = null;
            allowClose = true;
            owner.Window.DispatcherQueue.TryEnqueue(() => owner.Window.Close());
        }
        catch (Exception error) { owner.LogFatal(error); owner.Window.ShowLifecycleError("退出未完成，窗口已保留。请重试。"); }
        finally { if (!allowClose) { exiting = false; owner.Window.SetCommandsBlocked(false); } }
    }
    private bool QuerySystemEnd(bool logoff)
    {
        if (host is not { } owner) return true;
        owner.Window.SetCommandsBlocked(true); owner.Window.CancelBackgroundRequests();
        var work = Task.Run(() => owner.Sessions.PrepareSystemEndAsync(TimeSpan.FromSeconds(4)));
        try { work.Wait(TimeSpan.FromMilliseconds(4200)); } catch { }
        return true; // Bounded best effort, never veto shutdown because storage is unavailable.
    }
    private async void EndSystemSession(bool ending)
    {
        if (host is not { } owner) return;
        if (ending) { tray?.Dispose(); tray = null; return; } // OS can terminate now; only checkpoint completion is bounded above.
        try { await owner.Sessions.CancelSystemEndAsync(); }
        catch { owner.Window.ShowLifecycleError("系统取消退出后，记录核验未完成，请重试退出。"); }
        owner.Window.SetCommandsBlocked(false);
    }
}
