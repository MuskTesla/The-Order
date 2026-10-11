using System.Diagnostics;
using System.Runtime.InteropServices;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class AppLifecycleWindowsTests(ITestOutputHelper output)
{
    [Fact]
    public void InstanceKeyUsesPhysicalProfileAndDoesNotOpenWriter()
    {
        using var first = new TransactionProfile(); using var second = new TransactionProfile();
        var key = NativeInstanceIdentity.ForProfile(first.Root);
        Assert.Equal(key, NativeInstanceIdentity.ForProfile(first.Root.ToUpperInvariant() + "\\."));
        Assert.NotEqual(key, NativeInstanceIdentity.ForProfile(second.Root));
        Assert.False(File.Exists(first.Main)); Assert.False(File.Exists(second.Main));
        Assert.Throws<DataIsolationException>(() => NativeInstanceIdentity.ForProfile(null));
    }
    [Fact]
    public async Task NativeTrayAndShutdownAdapterReleaseCallbacksAcrossThirtyLifetimes()
    {
        // Owned, hidden test window only. These messages simulate the adapter protocol;
        // no broadcast, desktop input, real shutdown, logoff, sleep or reboot is requested.
        await Sta(() =>
        {
            var hwnd = CreateWindowEx(0, "STATIC", "The Order lifecycle adapter fixture", 0, 0, 0, 0, 0, (IntPtr)(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, hwnd);
            try
            {
                for (int i = 0; i < 3; i++) { using var warm = new WindowsTrayLifecycle(hwnd, _ => { }, _ => true, _ => { }); }
                using var process = Process.GetCurrentProcess(); process.Refresh(); var handles = process.HandleCount;
                var user = GetGuiResources(process.Handle, 1); var gdi = GetGuiResources(process.Handle, 0);
                int commands = 0, query = 0, logoff = 0, cancelled = 0, ending = 0;
                for (int i = 0; i < 30; i++)
                {
                    using (var tray = new WindowsTrayLifecycle(hwnd, c => { Assert.Equal(TrayCommand.Show, c); commands++; },
                        isLogoff => { query++; if (isLogoff) logoff++; return true; }, end => { if (end) ending++; else cancelled++; }))
                    {
                        Assert.True(tray.Available, "Windows fixture requires the interactive shell notification area.");
                        int settings = 0;
                        tray.SystemSettingsChanged += () => settings++;
                        SendMessage(hwnd, 0x1a, 0, 0);
                        SendMessage(hwnd, 0x15, 0, 0);
                        SendMessage(hwnd, 0x31a, 0, 0);
                        Assert.Equal(3, settings);
                        tray.Update("Synthetic game", "计时已暂停；游戏可能仍在运行", false, true);
                        Assert.Equal((IntPtr)1, SendMessage(hwnd, 0x11, 0, 0));
                        SendMessage(hwnd, 0x11, 0, unchecked((nint)0x80000000L));
                        SendMessage(hwnd, 0x16, 0, 0); SendMessage(hwnd, 0x16, 1, 0);
                        SendMessage(hwnd, WindowsTrayLifecycle.CallbackMessage, 0, 0x400);
                        tray.Dispose(); Assert.False(tray.Available);
                        SendMessage(hwnd, 0x1a, 0, 0);
                        Assert.Equal(3, settings); // Disposed owners receive no settings callbacks.
                    }
                    // An old callback must no longer receive this message.
                    SendMessage(hwnd, WindowsTrayLifecycle.CallbackMessage, 0, 0x400);
                }
                Assert.Equal(30, commands); Assert.Equal(60, query); Assert.Equal(30, logoff);
                Assert.Equal(30, cancelled); Assert.Equal(30, ending);
                using (var failed = new WindowsTrayLifecycle(hwnd, _ => { }, _ => true, _ => { }, simulateFailure: true)) Assert.False(failed.Available);
                process.Refresh();
                output.WriteLine($"30 shell icon/subclass lifetimes: handles {handles}->{process.HandleCount}; USER {user}->{GetGuiResources(process.Handle, 1)}; GDI {gdi}->{GetGuiResources(process.Handle, 0)}; commands={commands}; query={query}; logoff={logoff}.");
                Assert.True(process.HandleCount <= handles + 8);
                Assert.True(GetGuiResources(process.Handle, 1) <= user + 2); Assert.True(GetGuiResources(process.Handle, 0) <= gdi + 2);
            }
            finally { DestroyWindow(hwnd); }
        });
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ApplicationHostOwnsRealProcessAcrossExitRecoveryAndFiftyWindowCycles(bool paused)
    {
        await using var fixture = new LaunchFixture(); fixture.Set("game");
        var entry = SessionTestRig.Entry() with { Installations = fixture.Library.Entry.Installations };
        File.WriteAllBytes(fixture.Profile.Main, SessionTestRig.Codec.Encode([entry], SessionTestRig.Date));
        string id; long seconds;
        await using (var library = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(fixture.Profile.Root)))
        await using (var host = new AppSessionHost(() => Task.FromResult<SessionEngine?>(new(library, fixture.Identity, fixture.Platform, fixture.Store,
            new WindowsSessionClock(), new WindowsSessionRecoveryTokens(fixture.Profile.Root))), () => library.Snapshot))
        {
            await host.InitializeAsync(); var prep = await host.PrepareAsync("g"); await host.ConfirmPositionAsync(prep.Snapshot!);
            Assert.True((await host.LaunchAsync("g")).ProcessCreated); await fixture.Report("run");
            var wait = Stopwatch.StartNew();
            while (host.Session?.Session?.Seconds is not >= 1)
            {
                Assert.True(wait.Elapsed < TimeSpan.FromSeconds(12), "Real sampler did not establish a confirmed interval.");
                await Task.Delay(50);
            }
            if (paused) await host.PauseAsync();
            var window = new TheOrder.Presentation.WindowLifetime { TrayAvailable = true };
            id = host.Session!.Session!.Id;
            for (int i = 0; i < 50; i++) { Assert.True(window.CloseToTray()); await host.CheckpointAsync(); window.Show(); }
            Assert.Equal(id, host.Session.Session!.Id); Assert.True(await host.PrepareExitAsync());
            seconds = host.Session.Session.Seconds;
            Assert.Equal(SessionLifecycle.RecoveryPending, host.Session.Session.Native!.Lifecycle);
            output.WriteLine($"before exit: session={id}; seconds={seconds}; paused={paused}; count={library.Snapshot.Entries[0].PlayCount}.");
        }
        await Task.Delay(1200);
        await using var reopened = TransactionalLibrary.Open(SessionTestRig.Codec, new IsolatedLibraryPersistence(fixture.Profile.Root));
        await using var recovery = new AppSessionHost(() => Task.FromResult<SessionEngine?>(new(reopened, fixture.Identity, fixture.Platform, fixture.Store,
            new WindowsSessionClock(), new WindowsSessionRecoveryTokens(fixture.Profile.Root))), () => reopened.Snapshot);
        await recovery.InitializeAsync();
        Assert.Equal(id, recovery.Session!.Session!.Id); Assert.Equal(seconds, recovery.Session.Session.Seconds);
        Assert.Equal(paused, recovery.Session.Session.Native!.UserPaused); Assert.Equal(1, recovery.Session.Session.Native.Recovery.Count);
        Assert.Equal(SessionTracking.CurrentProcessOnly, recovery.Session.Tracking); Assert.Equal(6, reopened.Snapshot.Entries[0].PlayCount);
        using (var process = Process.GetProcessById(recovery.Session.Current!.Pid)) Assert.False(process.HasExited);
        await fixture.Stop(); // Fixture's normal stop-file protocol; never terminate a game process.
        output.WriteLine("real Windows process survived host disposal; same Session recovered once; offline gap excluded; normal fixture exit requested.");
    }
    [Fact]
    public async Task ThemeBrandIconsAreReusedAndRetiredAcrossTwentyLifetimes()
    {
        await Sta(() => {
            var hwnd = CreateWindowEx(0,"STATIC","The Order icon fixture",0,0,0,0,0,(IntPtr)(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            try {
                var asset=Path.Combine(AppContext.BaseDirectory,"material-fixtures","Brand.png");
                using(var warm=WindowsBrandIcons.LoadAsync(asset).GetAwaiter().GetResult()) { warm.Apply(hwnd,true); WindowsBrandIcons.Detach(hwnd); }
                using var process=Process.GetCurrentProcess();var user=GetGuiResources(process.Handle,1);var gdi=GetGuiResources(process.Handle,0);
                for(int n=0;n<20;n++) {
                    using var owner=WindowsBrandIcons.LoadAsync(asset).GetAwaiter().GetResult();
                    Assert.NotEqual(IntPtr.Zero,owner.Small(true)); Assert.NotEqual(owner.Small(true),owner.Small(false));
                    for(int k=0;k<10;k++) { owner.Apply(hwnd,k%2==0); Assert.Equal(owner.Small(k%2==0),SendMessage(hwnd,0x7F,0,0)); }
                    WindowsBrandIcons.Detach(hwnd); Assert.Equal(IntPtr.Zero,SendMessage(hwnd,0x7F,0,0));
                }
                Assert.True(GetGuiResources(process.Handle,1)<=user+2); Assert.True(GetGuiResources(process.Handle,0)<=gdi+2);
                output.WriteLine($"20 brand owners / 200 theme assignments: USER {user}->{GetGuiResources(process.Handle,1)}, GDI {gdi}->{GetGuiResources(process.Handle,0)}.");
            } finally { DestroyWindow(hwnd); }
        });
    }
    private static Task Sta(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); result.SetResult(); } catch (Exception e) { result.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
}
