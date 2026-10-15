using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// One short-lived child owns the actual audio leases. Its parent's death therefore
// does not lose mute ownership. It opens no Profile and has no service/autostart.
public static class WindowsAudioGuard
{
    private const string Marker = "--audio-guard";
    internal const string PipePrefix = "TheOrder.Audio.";
    internal static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);
    public static bool IsInvocation(string[] args) => args.Contains(Marker, StringComparer.Ordinal);

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !TryParse(args, out var request)) return 64;
        int result = 65;
        // Audio leases are created, observed and released on one MTA thread.
        var owner = new Thread(() =>
        {
            try
            {
                using var parent = Process.GetProcessById(request!.ParentPid);
                _ = parent.Handle; // Retain this process generation, not a later reused PID.
                if (!ValidParent(parent, request.ParentCreation)) return;
                using var pipe = new NamedPipeClientStream(".", request.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.Connect(5000);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != request.ParentPid
                    || !ValidParent(parent, request.ParentCreation)) return;
                result = RunConnected(pipe, () => !parent.HasExited, new WindowsGameAudioPlatform(), request.Nonce);
            }
            catch { result = 66; } // No paths, credentials, command lines or exception payloads.
        }) { IsBackground = false, Name = "The Order audio owner" };
        owner.SetApartmentState(ApartmentState.MTA); owner.Start(); owner.Join();
        return result;
    }

    internal sealed record Request(int ParentPid, long ParentCreation, string Pipe, string Nonce);
    internal static bool TryParse(string[] args, out Request? request)
    {
        request = null;
        if (args.Length != 5 || args[0] != Marker
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 || pid == Environment.ProcessId
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var creation) || creation <= 0
            || !args[3].StartsWith(PipePrefix, StringComparison.Ordinal) || !Guid.TryParseExact(args[3][PipePrefix.Length..], "N", out _)
            || args[4].Length != 64 || !args[4].All(Uri.IsHexDigit)) return false;
        request = new(pid, creation, args[3], args[4]); return true;
    }
    internal static bool ValidParent(Process parent, long creation)
    {
        try
        {
            return !parent.HasExited && parent.StartTime.ToUniversalTime().ToFileTimeUtc() == creation
                && string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    internal static bool ValidTarget(ProcessIdentity? identity) => identity is { Pid: > 0, CreationFileTime: > 0 }
        && identity.Pid != Environment.ProcessId && identity.ImagePath.Length is > 0 and <= 2048
        && Path.IsPathFullyQualified(identity.ImagePath) && identity.ImagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !identity.ImagePath.Any(char.IsControl);

    // Transport is already authenticated by OS client/server PIDs before entering.
    // The internal seam enables fault tests with synthetic channels, without a Profile.
    internal static int RunConnected(PipeStream pipe, Func<bool> parentAlive, IGameAudioPlatform platform, string nonce,
        TimeSpan? interval = null, TimeSpan? heartbeatLimit = null)
    {
        using var cancellation = new CancellationTokenSource();
        var controller = new BackgroundAudioController(platform);
        ProcessIdentity? target = null;
        var lastCommand = Stopwatch.StartNew(); var sample = Stopwatch.StartNew();
        try
        {
            AudioGuardProtocol.WriteAsync(pipe, new AudioGuardReply(1, "ready", nonce, null), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var read = AudioGuardProtocol.ReadAsync<AudioGuardCommand>(pipe, cancellation.Token);
            while (parentAlive() && lastCommand.Elapsed < (heartbeatLimit ?? TimeSpan.FromSeconds(15)))
            {
                if (read.IsCompleted)
                {
                    var command = read.GetAwaiter().GetResult();
                    if (command.Version != 1 || !AudioGuardProtocol.SameNonce(command.Nonce, nonce)) return 67;
                    if (command.Kind == "stop" && command.Process is null)
                    {
                        controller.Dispose();
                        AudioGuardProtocol.WriteAsync(pipe, new AudioGuardReply(1, "stopped", nonce, controller.Failure), CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                        return controller.Failure is null ? 0 : 68;
                    }
                    if (command.Kind != "set" || !ValidTarget(command.Process) || !parentAlive()) return 67;
                    // A PID alone never grants permission. The platform revalidates PID,
                    // creation and exact image before every acquisition and mutation.
                    target = command.Process;
                    controller.Update(target, true); sample.Restart(); lastCommand.Restart();
                    AudioGuardProtocol.WriteAsync(pipe, new AudioGuardReply(1, "status", nonce, controller.Failure), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                    read = AudioGuardProtocol.ReadAsync<AudioGuardCommand>(pipe, cancellation.Token);
                }
                else if (sample.Elapsed >= (interval ?? Tick))
                {
                    controller.Update(target, target is not null); sample.Restart();
                }
                Thread.Sleep(20); // Bounded wait; parent death and foreground checks continue.
            }
            controller.Dispose();
            return controller.Failure is null ? 0 : 68;
        }
        catch { return 69; }
        finally { cancellation.Cancel(); controller.Dispose(); }
    }

    internal static string? DescribeFailure(string? code) => code switch
    {
        null => null,
        "AudioRestoreFailed" => "后台静音未能恢复，请检查游戏的系统音量。",
        "AudioCapacity" => "游戏的音频会话过多，后台静音未完整应用。",
        _ => "无法访问游戏音频，后台静音未确认生效。"
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}

internal sealed record AudioGuardCommand(int Version, string Kind, string Nonce, ProcessIdentity? Process);
internal sealed record AudioGuardReply(int Version, string Kind, string Nonce, string? Failure);
internal static class AudioGuardProtocol
{
    internal const int MaximumFrame = 8192;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static bool SameNonce(string? first, string? second)
    {
        if (first?.Length != 64 || second?.Length != 64 || !first.All(Uri.IsHexDigit) || !second.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrame) throw new InvalidDataException("Audio frame size rejected.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Audio frame rejected.");
    }
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("Audio frame size rejected.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
}

internal sealed class AudioGuardClient : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process child;
    private readonly string nonce;
    private bool stopped;
    private AudioGuardClient(NamedPipeServerStream pipe, Process child, string nonce) { this.pipe = pipe; this.child = child; this.nonce = nonce; }
    public bool Connected
    {
        get { try { return !stopped && pipe.IsConnected && !child.HasExited; } catch { return false; } }
    }
    internal int ProcessId => child.Id;

    public static async Task<AudioGuardClient> StartAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Audio host unavailable.");
        var name = WindowsAudioGuard.PipePrefix + Guid.NewGuid().ToString("N");
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
        Process? child = null;
        try
        {
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var argument in new[] { "--audio-guard", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                parent.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), name, nonce }) start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new InvalidOperationException("Audio child unavailable.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != child.Id || child.HasExited
                || !string.Equals(child.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Audio peer rejected.");
            var reply = await AudioGuardProtocol.ReadAsync<AudioGuardReply>(pipe, timeout.Token).ConfigureAwait(false);
            if (reply.Version != 1 || reply.Kind != "ready" || !AudioGuardProtocol.SameNonce(reply.Nonce, nonce)) throw new InvalidDataException("Audio handshake rejected.");
            return new(pipe, child, nonce);
        }
        catch { pipe.Dispose(); child?.Dispose(); throw; } // Child times out/exits; never terminate a game or another application.
    }
    public async Task<AudioGuardReply> UpdateAsync(ProcessIdentity process)
    {
        if (stopped || !WindowsAudioGuard.ValidTarget(process)) throw new InvalidDataException("Audio target rejected.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await AudioGuardProtocol.WriteAsync(pipe, new AudioGuardCommand(1, "set", nonce, process), timeout.Token).ConfigureAwait(false);
        var reply = await AudioGuardProtocol.ReadAsync<AudioGuardReply>(pipe, timeout.Token).ConfigureAwait(false);
        if (reply.Version != 1 || reply.Kind != "status" || !AudioGuardProtocol.SameNonce(reply.Nonce, nonce)) throw new InvalidDataException("Audio reply rejected.");
        return reply;
    }
    public async Task<bool> StopAsync()
    {
        if (stopped) return true;
        stopped = true; bool restored = false;
        try
        {
            if (!child.HasExited && pipe.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await AudioGuardProtocol.WriteAsync(pipe, new AudioGuardCommand(1, "stop", nonce, null), timeout.Token).ConfigureAwait(false);
                var reply = await AudioGuardProtocol.ReadAsync<AudioGuardReply>(pipe, timeout.Token).ConfigureAwait(false);
                restored = reply.Version == 1 && reply.Kind == "stopped" && reply.Failure is null && AudioGuardProtocol.SameNonce(reply.Nonce, nonce);
            }
        }
        catch { restored = false; }
        finally
        {
            pipe.Dispose(); // A disconnected or stalled parent also releases the guard's leases.
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); restored &= child.ExitCode == 0; }
            catch { restored = false; }
            child.Dispose();
        }
        return restored;
    }
    public async ValueTask DisposeAsync() => _ = await StopAsync().ConfigureAwait(false);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
