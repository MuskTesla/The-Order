using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

public sealed class BackgroundAudioRecoveryTests
{
    internal static readonly ProcessIdentity Game = new(4321, 123456, @"C:\synthetic\audio-game.exe", null);

    [Fact] public void GameExitRestoresRetainedSessionWithoutReopeningPid()
    {
        var platform = new AudioFake(); using var owner = new BackgroundAudioController(platform);
        owner.Update(Game, true); Assert.True(platform.Audio.Muted);
        platform.Current = null; owner.Update(Game, true);
        Assert.False(platform.Audio.Muted); Assert.Equal(2, platform.Audio.Writes); Assert.Equal(0, owner.OwnedCount);
        Assert.Equal(1, platform.Enumerations); // No lookup after the process is gone.
    }

    [Fact] public void FailedRestoreRetainsLeaseForNextSample()
    {
        var platform = new AudioFake(); using var owner = new BackgroundAudioController(platform);
        owner.Update(Game, true); platform.Audio.FailRestores = 1; platform.Foreground = true;
        owner.Update(Game, true); Assert.Equal("AudioRestoreFailed", owner.Failure); Assert.Equal(1, owner.OwnedCount);
        owner.Update(Game, true); Assert.Null(owner.Failure); Assert.False(platform.Audio.Muted); Assert.Equal(0, owner.OwnedCount);
    }

    [Fact] public void DisposalRetriesTransientRestoreAndReportsPermanentFailure()
    {
        var platform = new AudioFake(); var owner = new BackgroundAudioController(platform);
        owner.Update(Game, true); platform.Audio.FailRestores = 2; owner.Dispose();
        Assert.Null(owner.Failure); Assert.False(platform.Audio.Muted); Assert.Equal(platform.Created, platform.Disposed);
        platform.Audio.FailRestores = 10; owner.Update(Game, true); owner.Dispose();
        Assert.Equal("AudioRestoreFailed", owner.Failure); Assert.True(platform.Audio.Muted); Assert.Equal(platform.Created, platform.Disposed);
    }

    [Fact] public void AmbiguousMuteFailureStillAttemptsRestoration()
    {
        var platform = new AudioFake(); platform.Audio.FailMuteAfterChange = true;
        using var owner = new BackgroundAudioController(platform); owner.Update(Game, true);
        Assert.False(platform.Audio.Muted); Assert.Equal(0, owner.OwnedCount); Assert.Equal("AudioUnavailable", owner.Failure);
    }

    [Fact] public void ExternalUserMuteOverrideRetainsUserChoiceAtExit()
    {
        var platform = new AudioFake(); using var owner = new BackgroundAudioController(platform);
        owner.Update(Game, true); platform.Audio.Muted = false; platform.Audio.External++;
        platform.Audio.Muted = true; platform.Audio.External++; platform.Current = null;
        owner.Update(Game, true); Assert.True(platform.Audio.Muted); Assert.Equal(1, platform.Audio.Writes);
    }

    [Fact] public void OriginalMuteIsPreservedAndForeignSessionCannotBeAcquired()
    {
        var platform = new AudioFake(); platform.Audio.Muted = true;
        using var owner = new BackgroundAudioController(platform); owner.Update(Game, true);
        Assert.Equal(0, owner.OwnedCount); Assert.Equal(0, platform.Audio.Writes);
        platform.Audio.Muted = false; platform.Audio.Original = false; owner.Update(Game, true);
        Assert.False(platform.Audio.Muted); Assert.Equal(0, platform.Audio.Writes);
    }

    [Fact] public void InitiallyMutedSessionRespectsUserUnmuteUntilNextBackgroundInterval()
    {
        var platform = new AudioFake(); platform.Audio.Muted = true;
        using var owner = new BackgroundAudioController(platform); owner.Update(Game, true);
        Assert.Equal(1, owner.ObservedCount); Assert.Equal(0, owner.OwnedCount);
        platform.Audio.Muted = false; platform.Audio.External++;
        owner.Update(Game, true); owner.Update(Game, true);
        Assert.False(platform.Audio.Muted); Assert.Equal(0, platform.Audio.Writes);
        platform.Foreground = true; owner.Update(Game, true); Assert.Equal(0, owner.ObservedCount);
        platform.Foreground = false; owner.Update(Game, true);
        Assert.True(platform.Audio.Muted); Assert.Equal(1, platform.Audio.Writes);
        owner.Dispose(); Assert.False(platform.Audio.Muted); Assert.Equal(platform.Created, platform.Disposed);
    }

    [Fact] public void PartialEnumerationDoesNotDropObservedUserChoiceOrOwnedRestoration()
    {
        var platform = new AudioFake(); using var owner = new BackgroundAudioController(platform);
        owner.Update(Game, true); Assert.True(platform.Audio.Muted);
        platform.Visible = false; platform.EnumerationStatus = AudioEnumerationStatus.Partial;
        owner.Update(Game, true); Assert.Equal("AudioUnavailable", owner.Failure);
        Assert.Equal(1, owner.OwnedCount); Assert.True(platform.Audio.Muted);
        platform.Foreground = true; owner.Update(Game, true); Assert.False(platform.Audio.Muted);
    }

    [Fact] public void EnumerationFailureIsDistinctFromWaitingForAudio()
    {
        var platform = new AudioFake { FailEnumeration = true }; using var owner = new BackgroundAudioController(platform);
        owner.Update(Game, true); Assert.Equal("AudioUnavailable", owner.Failure);
        platform.FailEnumeration = false; platform.Visible = false; owner.Update(Game, true);
        Assert.Null(owner.Failure); Assert.Equal(0, owner.OwnedCount);
    }

    internal sealed class AudioState
    {
        public bool Muted, FailMuteAfterChange, Original = true;
        public int Writes, FailRestores;
        public long External;
    }
    internal sealed class AudioFake : IGameAudioPlatform
    {
        public readonly AudioState Audio = new();
        public ProcessIdentity? Current = Game;
        public bool Foreground, FailEnumeration, Visible = true;
        public AudioEnumerationStatus EnumerationStatus { get; set; }
        public int Created, Disposed, Enumerations;
        public bool IsCurrent(ProcessIdentity process) => process == Current;
        public bool? IsForeground(ProcessIdentity process) => Foreground;
        public IReadOnlyList<IGameAudioChannel> Channels(ProcessIdentity process)
        {
            Enumerations++; if (FailEnumeration) throw new IOException("Synthetic unavailable audio.");
            if (!Visible) return [];
            Created++; return [new Lease(this)];
        }
        private sealed class Lease(AudioFake owner) : IGameAudioChannel
        {
            private bool disposed;
            public string Id => "exact-retained-synthetic-session";
            public long ExternalChanges => owner.Audio.External;
            public bool IsOriginalSession => owner.Audio.Original;
            public bool Muted => owner.Audio.Muted;
            public void SetMuted(bool value)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!value && owner.Audio.FailRestores-- > 0) throw new IOException("Synthetic restore failure.");
                owner.Audio.Writes++; owner.Audio.Muted = value;
                if (value && owner.Audio.FailMuteAfterChange) throw new IOException("Synthetic lost mute reply.");
            }
            public void Dispose() { if (!disposed) { disposed = true; owner.Disposed++; } }
        }
    }
}
