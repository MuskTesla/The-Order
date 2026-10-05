using System.Diagnostics;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using TheOrder.WinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TheOrder.WinUI;

// Explicit diagnostics + Test profile only. Drives the actual controls and navigation, without external clicks.
public sealed partial class MainWindow
{
    private readonly List<double> performanceUiGaps = [];
    private double performanceLastUiTick;
    private async Task RunPerformanceAfterInitializationAsync()
    {
        try { await initialization!; await RunPerformanceFixtureAsync(); }
        catch (Exception) { ViewModel.ReportUnexpected(); }
    }
    private async Task RunPerformanceFixtureAsync()
    {
        string output = Path.GetFullPath(Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_PERFORMANCE_OUTPUT")!);
        var samples = new List<object>();
        try
        {
            if (!output.Contains(Path.DirectorySeparatorChar + ".artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact output required");
            NativeProfilePaths.Validate(new(NativeProfileKind.Test, output)); Directory.CreateDirectory(output);
            if (!ViewModel.IsLoaded) throw new InvalidOperationException("Library not usable");
            double viewModelReadyMs = Program.StartupClock.Elapsed.TotalMilliseconds;
            await Task.Delay(100);
            var firstViewport = libraryPage.RuntimeViewport();
            double firstUsableMs = Program.StartupClock.Elapsed.TotalMilliseconds;
            var coverWait = Stopwatch.StartNew();
            while (!HasVisibleBitmap(libraryPage) && coverWait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
            double? firstCoverMs = HasVisibleBitmap(libraryPage) ? Program.StartupClock.Elapsed.TotalMilliseconds : null;
            WritePerformance(output, "startup.json", new { status = "USABLE", profileOpenMs = Program.ProfileOpenMilliseconds, viewModelReadyMs, firstUsableMs, firstCoverMs, firstViewport, entries = ViewModel.Items.Count, images = images.Metrics, note = "Process-relative managed entry clock; OS cache uncontrolled. First usable requires loaded query and actual realized viewport; first cover requires an Image surface." });
            int rounds = int.TryParse(Environment.GetEnvironmentVariable("THE_ORDER_NATIVE_PERFORMANCE_ROUNDS"), out var value) ? Math.Clamp(value, 0, 1000) : 120;
            for (int i = 0; i < rounds; i++)
            {
                if (i % 12 == 0) await ViewModel.ChangeAppearanceAsync(ViewModel.Theme, ViewModel.Layout == "grid" ? "list" : "grid");
                var viewport = await libraryPage.PerformanceScrollAsync((i % 11) / 10d, i * 47);
                if (i % 3 == 0)
                {
                    var id = ViewModel.Items[(i * 47) % ViewModel.Items.Count].Id;
                    await Navigate(WorkspacePage.Details, id); await Task.Delay(60);
                    await Navigate(WorkspacePage.Library); await Task.Delay(60);
                }
                if (i % 20 == 0) { AppWindow.Hide(); await Sessions.CheckpointAsync(); AppWindow.Show(); }
                using var process = Process.GetCurrentProcess();
                var sample = new { round = i, elapsedMs = Program.StartupClock.Elapsed.TotalMilliseconds, layout = ViewModel.Layout, viewport, images = images.Metrics,
                    workingSet = process.WorkingSet64, privateBytes = process.PrivateMemorySize64, managedHeap = GC.GetTotalMemory(false), handles = process.HandleCount, threads = process.Threads.Count,
                    imageSurfaces = CountCoverPresenters(Root), outstandingThreadPool = ThreadPool.PendingWorkItemCount };
                samples.Add(sample);
                if (i % 10 == 0) await Task.Run(() => WritePerformance(output, "progress.json", sample));
            }
            await Navigate(WorkspacePage.Library); await Task.Delay(300);
            await Task.Run(() => WritePerformance(output, "ui-result.json", new { status = "UI_WORKLOAD_COMPLETED", rounds, samples, images = images.Metrics, uiTickGapsMs = performanceUiGaps, actualXaml = true, productionAccess = false, systemTrayMenuAutomated = false }));
        }
        catch (Exception e)
        {
            if (Directory.Exists(output)) await Task.Run(() => WritePerformance(output, "ui-failure.json", new { type = e.GetType().Name, e.Message, samples }));
        }
        finally { if (App.Instance is { } app) await app.RequestExitAsync(); }
    }
    private static bool HasVisibleBitmap(DependencyObject root)
    {
        if (root is Microsoft.UI.Xaml.Controls.Image image && image.Source is not null) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (HasVisibleBitmap(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }
    private static int CountCoverPresenters(DependencyObject root)
    {
        int count = root is CoverPresenter ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) count += CountCoverPresenters(VisualTreeHelper.GetChild(root, i));
        return count;
    }
    private static void WritePerformance(string output, string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
