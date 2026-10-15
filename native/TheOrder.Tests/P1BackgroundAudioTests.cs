using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;
public sealed class P1BackgroundAudioTests
{
    private static readonly ProcessIdentity Game = new(1, 42, @"C:\synthetic\game.exe", null);
    [Fact] public void OnlyConfirmedBackgroundGameIsMutedAndForegroundRestores()
    {
        var platform = new Fake(); using var controller = new BackgroundAudioController(platform);
        platform.Foreground = true; controller.Update(Game, true); Assert.False(platform.A.Muted);
        platform.Foreground = false; controller.Update(Game, false); Assert.False(platform.A.Muted);
        controller.Update(Game, true); Assert.True(platform.A.Muted);
        controller.Update(Game, true); Assert.Equal(1, platform.A.Writes);
        platform.Foreground = true; controller.Update(Game, true); Assert.False(platform.A.Muted); Assert.Equal(2, platform.A.Writes);
    }
    [Fact] public void OriginallyMutedChannelAndUnrelatedChannelsRemainUntouched()
    {
        var platform = new Fake(); platform.A.Muted = true; platform.B.Muted = false; platform.Visible.Add(platform.B);
        using (var controller = new BackgroundAudioController(platform))
        { controller.Update(Game, true); Assert.True(platform.A.Muted); Assert.True(platform.B.Muted); }
        Assert.True(platform.A.Muted); Assert.False(platform.B.Muted); Assert.Equal(0, platform.A.Writes);
    }
    [Fact] public void ExternalMixerOverrideIsRespectedUntilNextBackgroundInterval()
    {
        var platform = new Fake(); using var controller = new BackgroundAudioController(platform);
        controller.Update(Game, true); platform.A.External(false); controller.Update(Game, true);
        Assert.False(platform.A.Muted); Assert.Equal(1, platform.A.Writes);
        platform.Foreground = true; controller.Update(Game, true); Assert.False(platform.A.Muted);
        platform.Foreground = false; controller.Update(Game, true); Assert.True(platform.A.Muted);
        platform.A.External(true); controller.Update(null, false); Assert.True(platform.A.Muted); // User kept mute: no forced unmute.
    }
    [Theory][InlineData("disable")][InlineData("unknown-window")][InlineData("stop")][InlineData("change")]
    public void LeavingManagedBackgroundIntervalRestoresOurChange(string action)
    {
        var platform = new Fake(); using var controller = new BackgroundAudioController(platform); controller.Update(Game, true);
        if (action == "unknown-window") platform.Foreground = null;
        controller.Update(action == "stop" ? null : action == "change" ? Game with { Pid = 2 } : Game, action != "disable");
        Assert.False(platform.A.Muted);
    }
    [Fact] public void ReusedPidIsNeverLookedUpForRestoreButRetainedOldSessionIsRestored()
    {
        var platform = new Fake(); using var controller = new BackgroundAudioController(platform); controller.Update(Game, true);
        platform.Current = Game with { CreationFileTime = 99 }; controller.Update(Game, true);
        Assert.Equal(2, platform.A.Writes); Assert.False(platform.A.Muted);
        platform.Visible.Clear(); platform.Visible.Add(platform.B);
        controller.Update(platform.Current, false); Assert.Equal(0, platform.B.Writes);
    }
    [Fact] public void NewAudioSessionIsMutedAndRemovedSessionIsReleased()
    {
        var platform = new Fake(); using var controller = new BackgroundAudioController(platform); controller.Update(Game, true);
        platform.Visible.Add(platform.B); controller.Update(Game, true); Assert.True(platform.B.Muted);
        platform.Visible.Remove(platform.A); controller.Update(Game, true); Assert.False(platform.A.Muted); Assert.True(platform.B.Muted);
        controller.Update(null, false); Assert.False(platform.B.Muted); Assert.Equal(platform.Created, platform.Disposed);
    }
    private sealed class State(string id)
    {
        public string Id = id; public bool Muted; public long Changes; public int Writes;
        public void External(bool value) { Muted = value; Changes++; }
    }
    private sealed class Fake : IGameAudioPlatform
    {
        public State A = new("A"), B = new("B"); public ProcessIdentity Current = Game;
        public bool? Foreground = false; public List<State> Visible; public int Created, Disposed;
        public Fake() { Visible = [A]; }
        public bool IsCurrent(ProcessIdentity process) => process == Current;
        public bool? IsForeground(ProcessIdentity process) => Foreground;
        public IReadOnlyList<IGameAudioChannel> Channels(ProcessIdentity process) => Visible.Select(s => { Created++; return (IGameAudioChannel)new Lease(s, this); }).ToArray();
        private sealed class Lease(State state, Fake owner) : IGameAudioChannel
        {
            private bool disposed;
            public string Id => state.Id; public long ExternalChanges => state.Changes; public bool Muted => state.Muted;
            public bool IsOriginalSession => true;
            public void SetMuted(bool value) { if (disposed) throw new ObjectDisposedException(nameof(Lease)); state.Muted = value; state.Writes++; }
            public void Dispose() { if (disposed) return; disposed = true; owner.Disposed++; }
        }
    }
}
