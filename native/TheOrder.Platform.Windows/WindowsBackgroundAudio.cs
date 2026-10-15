using System.Diagnostics;
using System.Runtime.InteropServices;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsBackgroundAudio : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private AudioGuardClient? client;
    private bool stopped;
    private Task? disposal;
    private string? failure;
    private DateTimeOffset retryAfter;
    public string? Failure => Volatile.Read(ref failure);
    public bool Active => client is { Connected: true };
    public async Task UpdateAsync(ProcessIdentity? process, bool enabled)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (stopped) return;
            if (!enabled || process is null)
            {
                if (client is not null)
                {
                    var previous = client; client = null;
                    var restored = await previous.StopAsync().ConfigureAwait(false);
                    if (!restored) failure = "后台静音未能确认恢复，请检查游戏的系统音量。";
                    else failure = null;
                }
                return;
            }
            if (client is null && DateTimeOffset.UtcNow < retryAfter) return;
            try
            {
                client ??= await AudioGuardClient.StartAsync().ConfigureAwait(false);
                var result = await client.UpdateAsync(process).ConfigureAwait(false);
                failure = WindowsAudioGuard.DescribeFailure(result.Failure);
            }
            catch
            {
                if (client is not null) await client.StopAsync().ConfigureAwait(false);
                client = null; retryAfter = DateTimeOffset.UtcNow.AddSeconds(10);
                failure = "后台静音暂不可用，未确认辅助进程正常工作；可检查游戏音量后重试。";
            }
        }
        finally { gate.Release(); }
    }
    public ValueTask DisposeAsync() => new(disposal ??= Stop());
    private async Task Stop()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            stopped = true;
            if (client is not null)
            {
                var previous = client; client = null;
                if (!await previous.StopAsync().ConfigureAwait(false)) failure = "后台静音未能确认恢复，请检查游戏的系统音量。";
            }
        }
        finally { gate.Release(); }
    }
}

public sealed class WindowsGameAudioPlatform : IGameAudioPlatform
{
    private readonly Func<ProcessIdentity, AudioChannelEnumeration>? enumerate;
    public WindowsGameAudioPlatform() { }
    internal WindowsGameAudioPlatform(Func<ProcessIdentity, AudioChannelEnumeration> enumerate) { this.enumerate = enumerate; }
    public AudioEnumerationStatus EnumerationStatus { get; private set; }
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
        EnumerationStatus = AudioEnumerationStatus.Complete;
        if (!IsCurrent(identity)) return [];
        try
        {
            var batch = enumerate?.Invoke(identity) ?? EnumerateWindows(identity);
            EnumerationStatus = batch.Status;
            if (IsCurrent(identity)) return batch.Channels;
            foreach (var channel in batch.Channels) channel.Dispose();
            return [];
        }
        catch { EnumerationStatus = AudioEnumerationStatus.Failed; throw; }
    }
    internal sealed record AudioChannelEnumeration(IReadOnlyList<IGameAudioChannel> Channels, AudioEnumerationStatus Status);
    private static AudioChannelEnumeration EnumerateWindows(ProcessIdentity identity)
    {
        var result = new List<IGameAudioChannel>(); bool incomplete = false; int inspected = 0;
        var enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new("bcde0395-e52f-467c-8e3d-c4579291692e"))!)!;
        IDevices? devices = null;
        try
        {
            enumerator.EnumAudioEndpoints(0, 1, out devices); devices.GetCount(out var count);
            incomplete = count > 16;
            for (uint d = 0; d < Math.Min(count, 16); d++)
            {
                IDevice? device = null; object? manager = null; ISessions? sessions = null;
                try
                {
                    devices.Item(d, out device); var iid = typeof(ISessionManager).GUID;
                    device.Activate(ref iid, 1, IntPtr.Zero, out manager); ((ISessionManager)manager).GetSessionEnumerator(out sessions); sessions.GetCount(out var amount);
                    if (amount > 512) incomplete = true;
                    for (int s = 0; s < Math.Min(amount, 512); s++)
                    {
                        object? control = null; bool retained = false;
                        try
                        {
                            sessions.GetSession(s, out control); var session = (ISessionControl)control;
                            int processResult = session.GetProcessId(out var pid);
                            // NO_SINGLE_PROCESS is a successful but deliberately
                            // excluded cross-process session, not a device failure.
                            if (processResult < 0) { incomplete = true; continue; }
                            inspected++;
                            if (processResult != 0 || pid != identity.Pid) continue;
                            int system = session.IsSystemSoundsSession();
                            if (system != 0 && system != 1) { incomplete = true; continue; }
                            if (system != 1) continue;
                            session.GetSessionInstanceIdentifier(out var id);
                            if (string.IsNullOrEmpty(id) || id.Length > 32768) { incomplete = true; continue; }
                            result.Add(new Channel(control, session, (ISimpleVolume)control, id)); retained = true;
                        }
                        catch (COMException) { incomplete = true; }
                        finally { if (!retained && control is not null) Marshal.ReleaseComObject(control); }
                    }
                }
                catch (COMException) { incomplete = true; }
                finally { Release(sessions); Release(manager); Release(device); }
            }
            return new(result, incomplete ? inspected > 0 ? AudioEnumerationStatus.Partial : AudioEnumerationStatus.Failed : AudioEnumerationStatus.Complete);
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
        {
            this.handle = handle; this.session = session; this.volume = volume; Id = id; events = new(context);
            session.RegisterAudioSessionNotification(events);
            try { volume.GetMute(out var muted); events.SeedMute(muted); }
            catch { try { session.UnregisterAudioSessionNotification(events); } catch { } throw; }
        }
        public string Id { get; }
        public bool IsOriginalSession
        {
            get { session.GetSessionInstanceIdentifier(out var current); return string.Equals(Id, current, StringComparison.Ordinal); }
        }
        public long ExternalChanges => Interlocked.Read(ref events.Changes);
        public bool Muted { get { volume.GetMute(out var value); return value; } }
        public void SetMuted(bool value) { var marker = context; volume.SetMute(value, ref marker); }
        public void Dispose() { if (disposed) return; disposed = true; try { session.UnregisterAudioSessionNotification(events); } catch { } finally { Release(handle); } }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Events(Guid owner) : IAudioEvents
    {
        public long Changes;
        private int lastMute = -1;
        public void SeedMute(bool muted) => Interlocked.CompareExchange(ref lastMute, muted ? 1 : 0, -1);
        public int OnDisplayNameChanged(string value, ref Guid context) => 0;
        public int OnIconPathChanged(string value, ref Guid context) => 0;
        public int OnSimpleVolumeChanged(float volume, bool muted, ref Guid context)
        {
            int next = muted ? 1 : 0; int previous = Interlocked.Exchange(ref lastMute, next);
            // A volume slider adjustment does not transfer ownership of our mute.
            // An external mute transition does, including unmute then mute again.
            if (context != owner && (previous < 0 || previous != next)) Interlocked.Increment(ref Changes);
            return 0;
        }
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
