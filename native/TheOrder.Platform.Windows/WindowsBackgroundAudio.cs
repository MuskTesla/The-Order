using System.Diagnostics;
using System.Runtime.InteropServices;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsBackgroundAudio : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly BackgroundAudioController controller = new(new WindowsGameAudioPlatform());
    private bool stopped;
    private Task? disposal;
    public async Task UpdateAsync(ProcessIdentity? process, bool enabled)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (!stopped) await Task.Run(() => { try { controller.Update(process, enabled); } catch { } }).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public ValueTask DisposeAsync() => new(disposal ??= Stop());
    private async Task Stop()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { stopped = true; await Task.Run(controller.Dispose).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}

public sealed class WindowsGameAudioPlatform : IGameAudioPlatform
{
    public bool IsCurrent(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == identity.CreationFileTime
                && string.Equals(process.MainModule?.FileName, identity.ImagePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    public bool? IsForeground(ProcessIdentity identity)
    {
        var window = GetForegroundWindow(); if (window == IntPtr.Zero) return null;
        if (GetWindowThreadProcessId(window, out var pid) == 0) return null;
        return pid == identity.Pid;
    }
    public IReadOnlyList<IGameAudioChannel> Channels(ProcessIdentity identity)
    {
        var result = new List<IGameAudioChannel>(); if (!IsCurrent(identity)) return result;
        var enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new("bcde0395-e52f-467c-8e3d-c4579291692e"))!)!;
        IDevices? devices = null;
        try
        {
            enumerator.EnumAudioEndpoints(0, 1, out devices); devices.GetCount(out var count);
            for (uint d = 0; d < Math.Min(count, 16); d++)
            {
                IDevice? device = null; object? manager = null; ISessions? sessions = null;
                try
                {
                    devices.Item(d, out device); var iid = typeof(ISessionManager).GUID;
                    device.Activate(ref iid, 1, IntPtr.Zero, out manager); ((ISessionManager)manager).GetSessionEnumerator(out sessions); sessions.GetCount(out var amount);
                    for (int s = 0; s < Math.Min(amount, 512); s++)
                    {
                        object? control = null; bool retained = false;
                        try
                        {
                            sessions.GetSession(s, out control); var session = (ISessionControl)control;
                            if (session.GetProcessId(out var pid) != 0 || pid != identity.Pid || session.IsSystemSoundsSession() == 0) continue;
                            session.GetSessionInstanceIdentifier(out var id); if (string.IsNullOrEmpty(id) || id.Length > 32768) continue;
                            result.Add(new Channel(control, session, (ISimpleVolume)control, id)); retained = true;
                        }
                        catch (COMException) { }
                        finally { if (!retained && control is not null) Marshal.ReleaseComObject(control); }
                    }
                }
                catch (COMException) { }
                finally { Release(sessions); Release(manager); Release(device); }
            }
            if (!IsCurrent(identity)) { foreach (var channel in result) channel.Dispose(); result.Clear(); }
            return result;
        }
        catch { foreach (var channel in result) channel.Dispose(); throw; }
        finally { Release(devices); Release(enumerator); }
    }
    private static void Release(object? value) { if (value is not null) Marshal.ReleaseComObject(value); }
    private sealed class Channel : IGameAudioChannel
    {
        private readonly object handle; private readonly ISessionControl session; private readonly ISimpleVolume volume;
        private readonly Guid context = Guid.NewGuid(); private readonly Events events; private bool disposed;
        public Channel(object handle, ISessionControl session, ISimpleVolume volume, string id)
        { this.handle = handle; this.session = session; this.volume = volume; Id = id; events = new(context); session.RegisterAudioSessionNotification(events); }
        public string Id { get; }
        public long ExternalChanges => Interlocked.Read(ref events.Changes);
        public bool Muted { get { volume.GetMute(out var value); return value; } }
        public void SetMuted(bool value) { var marker = context; volume.SetMute(value, ref marker); }
        public void Dispose() { if (disposed) return; disposed = true; try { session.UnregisterAudioSessionNotification(events); } catch { } finally { Release(handle); } }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Events(Guid owner) : IAudioEvents
    {
        public long Changes;
        public int OnDisplayNameChanged(string value, ref Guid context) => 0;
        public int OnIconPathChanged(string value, ref Guid context) => 0;
        public int OnSimpleVolumeChanged(float volume, bool muted, ref Guid context) { if (context != owner) Interlocked.Increment(ref Changes); return 0; }
        public int OnChannelVolumeChanged(uint count, IntPtr values, uint changed, ref Guid context) => 0;
        public int OnGroupingParamChanged(ref Guid group, ref Guid context) => 0;
        public int OnStateChanged(int state) => 0;
        public int OnSessionDisconnected(int reason) => 0;
    }
    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint state, out IDevices devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        void RegisterEndpointNotificationCallback(IntPtr callback); void UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDevices
    { void GetCount(out uint count); void Item(uint index, out IDevice device); }
    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IDevice
    { void Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value); void OpenPropertyStore(uint mode, out IntPtr store); void GetId(out IntPtr id); void GetState(out uint state); }
    [ComImport, Guid("77aa99a0-1bd6-484f-8bc7-2c654c9a9b6f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface ISessionManager
    {
        void GetAudioSessionControl(ref Guid id, uint flags, out IntPtr control); void GetSimpleAudioVolume(ref Guid id, uint flags, out IntPtr volume);
        void GetSessionEnumerator(out ISessions sessions); void RegisterSessionNotification(IntPtr value); void UnregisterSessionNotification(IntPtr value);
        void RegisterDuckNotification(IntPtr id, IntPtr value); void UnregisterDuckNotification(IntPtr value);
    }
    [ComImport, Guid("e2f5bb11-0570-40ca-acdd-3aa01277dee8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface ISessions
    { void GetCount(out int count); void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control); }
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface ISessionControl
    {
        void GetState(out int state); void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string value); void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string value); void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        void GetGroupingParam(out Guid value); void SetGroupingParam(ref Guid value, ref Guid context);
        void RegisterAudioSessionNotification(IAudioEvents value); void UnregisterAudioSessionNotification(IAudioEvents value);
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string value); void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int GetProcessId(out uint value); [PreserveSig] int IsSystemSoundsSession(); void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool enabled);
    }
    [ComImport, Guid("87ce5498-68d6-44e5-9215-6da47ef883d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface ISimpleVolume
    { void SetMasterVolume(float value, ref Guid context); void GetMasterVolume(out float value); void SetMute([MarshalAs(UnmanagedType.Bool)] bool value, ref Guid context); void GetMute([MarshalAs(UnmanagedType.Bool)] out bool value); }
    [ComVisible(true), Guid("24918acc-64b3-37c1-8ca9-74a66e9957a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] public interface IAudioEvents
    {
        [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        [PreserveSig] int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr values, uint changed, ref Guid context);
        [PreserveSig] int OnGroupingParamChanged(ref Guid group, ref Guid context);
        [PreserveSig] int OnStateChanged(int state); [PreserveSig] int OnSessionDisconnected(int reason);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
