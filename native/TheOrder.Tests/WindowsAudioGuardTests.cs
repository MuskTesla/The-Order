using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using TheOrder.Platform.Windows;
using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

public sealed class WindowsAudioGuardTests
{
    private const string Nonce = "1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF";
    [Theory][InlineData(0)][InlineData(-1)][InlineData(8193)][InlineData(int.MaxValue)]
    public async Task FrameLengthIsBoundedBeforeAllocating(int length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioGuardProtocol.ReadAsync<AudioGuardCommand>(new MemoryStream(header), default));
    }
    [Fact] public async Task FrameRejectsUnknownPropertiesAndTruncation()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"Version\":1,\"Kind\":\"stop\",\"Nonce\":\"" + Nonce + "\",\"Process\":null,\"RunCommand\":\"anything\"}");
        var buffer = new byte[bytes.Length + 4]; BinaryPrimitives.WriteInt32LittleEndian(buffer, bytes.Length); bytes.CopyTo(buffer, 4);
        await Assert.ThrowsAsync<JsonException>(() => AudioGuardProtocol.ReadAsync<AudioGuardCommand>(new MemoryStream(buffer), default));
        await Assert.ThrowsAsync<EndOfStreamException>(() => AudioGuardProtocol.ReadAsync<AudioGuardCommand>(new MemoryStream(buffer[..8]), default));
    }
    [Fact] public void InvocationCannotOpenProfileOnInvalidGuardArguments()
    {
        Assert.True(WindowsAudioGuard.IsInvocation(["--audio-guard"]));
        Assert.Equal(64, WindowsAudioGuard.Run(["--audio-guard"]));
        var pipe = WindowsAudioGuard.PipePrefix + Guid.NewGuid().ToString("N");
        Assert.True(WindowsAudioGuard.TryParse(["--audio-guard", "123", "456", pipe, Nonce], out _));
        Assert.False(WindowsAudioGuard.TryParse(["--audio-guard", "123", "456", "arbitrary", Nonce], out _));
        Assert.False(WindowsAudioGuard.TryParse(["--audio-guard", "123", "456", pipe, "wrong"], out _));
        Assert.False(WindowsAudioGuard.TryParse(["--audio-guard", "123", "456", pipe, Nonce, "extra"], out _));
    }
    [Fact] public void TargetAndParentRequireExactIdentity()
    {
        using var current = Process.GetCurrentProcess(); var creation = current.StartTime.ToUniversalTime().ToFileTimeUtc();
        Assert.True(WindowsAudioGuard.ValidParent(current, creation)); Assert.False(WindowsAudioGuard.ValidParent(current, creation + 1));
        Assert.True(WindowsAudioGuard.ValidTarget(BackgroundAudioRecoveryTests.Game));
        Assert.False(WindowsAudioGuard.ValidTarget(BackgroundAudioRecoveryTests.Game with { Pid = Environment.ProcessId }));
        Assert.False(WindowsAudioGuard.ValidTarget(BackgroundAudioRecoveryTests.Game with { ImagePath = "relative.exe" }));
        Assert.False(WindowsAudioGuard.ValidTarget(BackgroundAudioRecoveryTests.Game with { ImagePath = @"C:\fake\script.cmd" }));
        Assert.False(AudioGuardProtocol.SameNonce(Nonce, Nonce[..^1] + "0"));
        Assert.False(AudioGuardProtocol.SameNonce(Nonce, "invalid"));
    }

    [Theory][InlineData("stop")][InlineData("parent-death")][InlineData("disconnect")][InlineData("bad-nonce")][InlineData("heartbeat")]
    public async Task GuardRestoresOwnedAudioAcrossTransportAndParentLifecycles(string end)
    {
        var platform = new BackgroundAudioRecoveryTests.AudioFake(); int parentAlive = 1;
        var name = "TheOrder.Audio.Test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connected = server.WaitForConnectionAsync(); await client.ConnectAsync(3000); await connected;
        var owner = Task.Run(() => WindowsAudioGuard.RunConnected(client, () => Volatile.Read(ref parentAlive) == 1, platform, Nonce,
            TimeSpan.FromMilliseconds(20), end == "heartbeat" ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(5)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("ready", (await AudioGuardProtocol.ReadAsync<AudioGuardReply>(server, deadline.Token)).Kind);
        await AudioGuardProtocol.WriteAsync(server, new AudioGuardCommand(1, "set", Nonce, BackgroundAudioRecoveryTests.Game), deadline.Token);
        Assert.Equal("status", (await AudioGuardProtocol.ReadAsync<AudioGuardReply>(server, deadline.Token)).Kind);
        Assert.True(platform.Audio.Muted);
        if (end == "stop")
        {
            await AudioGuardProtocol.WriteAsync(server, new AudioGuardCommand(1, "stop", Nonce, null), deadline.Token);
            var reply = await AudioGuardProtocol.ReadAsync<AudioGuardReply>(server, deadline.Token);
            Assert.Equal("stopped", reply.Kind); Assert.Null(reply.Failure); Assert.False(platform.Audio.Muted);
        }
        else if (end == "parent-death") Volatile.Write(ref parentAlive, 0);
        else if (end == "disconnect") server.Dispose();
        else if (end == "bad-nonce") await AudioGuardProtocol.WriteAsync(server, new AudioGuardCommand(1, "set", new string('A', 64), BackgroundAudioRecoveryTests.Game), deadline.Token);
        int result = await owner.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(end == "bad-nonce" ? 67 : end == "disconnect" ? 69 : 0, result);
        Assert.False(platform.Audio.Muted); Assert.Equal(2, platform.Audio.Writes); Assert.Equal(platform.Created, platform.Disposed);
    }

    [Fact] public void CoreAudioEventsIgnoreOwnContextAndRecordExternalMixerOverrides()
    {
        var context = Guid.NewGuid(); var events = new WindowsGameAudioPlatform.Events(context);
        events.OnSimpleVolumeChanged(1, true, ref context); Assert.Equal(0, events.Changes);
        var other = Guid.NewGuid(); events.OnSimpleVolumeChanged(1, false, ref other); Assert.Equal(1, events.Changes);
    }

    [Fact] public void VolumeOnlyEventsRetainMuteOwnershipWhileUnmuteAndRemuteTransferIt()
    {
        var own = Guid.NewGuid(); var external = Guid.NewGuid();
        var events = new WindowsGameAudioPlatform.Events(own); events.SeedMute(false);
        events.OnSimpleVolumeChanged(1, true, ref own);
        events.OnSimpleVolumeChanged(0.4f, true, ref external); Assert.Equal(0, events.Changes);
        events.OnSimpleVolumeChanged(0.7f, true, ref external); Assert.Equal(0, events.Changes);
        events.OnSimpleVolumeChanged(0.7f, false, ref external); Assert.Equal(1, events.Changes);
        events.OnSimpleVolumeChanged(0.7f, true, ref external); Assert.Equal(2, events.Changes);
    }

    [Theory][InlineData(AudioEnumerationStatus.Failed)][InlineData(AudioEnumerationStatus.Partial)]
    public void WindowsPlatformReportsInjectedIncompleteEmptyEnumerationAsFailure(AudioEnumerationStatus status)
    {
        using var current = Process.GetCurrentProcess();
        var identity = new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc(), Environment.ProcessPath!, null);
        var windows = new WindowsGameAudioPlatform(_ => new([], status));
        using var owner = new BackgroundAudioController(new ForegroundAdapter(windows));
        owner.Update(identity, true);
        Assert.Equal(status, windows.EnumerationStatus); Assert.Equal("AudioUnavailable", owner.Failure);
    }

    [Fact] public void WindowsPlatformExposesInjectedComFailureInsteadOfSuccessfulEmptyResult()
    {
        using var current = Process.GetCurrentProcess();
        var identity = new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc(), Environment.ProcessPath!, null);
        // Explicit fault injection. This is not reported as a real hardware COM failure.
        var windows = new WindowsGameAudioPlatform(_ => throw new COMException("Synthetic endpoint failure."));
        using var owner = new BackgroundAudioController(new ForegroundAdapter(windows));
        owner.Update(identity, true);
        Assert.Equal(AudioEnumerationStatus.Failed, windows.EnumerationStatus); Assert.Equal("AudioUnavailable", owner.Failure);
    }

    [Fact] public void WindowsPartialEnumerationStillUsesTheSuccessfulSyntheticSession()
    {
        using var current = Process.GetCurrentProcess();
        var identity = new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc(), Environment.ProcessPath!, null);
        var synthetic = new BackgroundAudioRecoveryTests.AudioFake();
        var windows = new WindowsGameAudioPlatform(_ => new(synthetic.Channels(identity), AudioEnumerationStatus.Partial));
        using var owner = new BackgroundAudioController(new ForegroundAdapter(windows));
        owner.Update(identity, true);
        Assert.True(synthetic.Audio.Muted); Assert.Equal("AudioUnavailable", owner.Failure);
        owner.Dispose(); Assert.False(synthetic.Audio.Muted);
    }

    private sealed class ForegroundAdapter(WindowsGameAudioPlatform inner) : IGameAudioPlatform
    {
        public AudioEnumerationStatus EnumerationStatus => inner.EnumerationStatus;
        public bool IsCurrent(ProcessIdentity process) => inner.IsCurrent(process);
        public bool? IsForeground(ProcessIdentity process) => false;
        public IReadOnlyList<IGameAudioChannel> Channels(ProcessIdentity process) => inner.Channels(process);
    }
}
