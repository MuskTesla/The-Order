namespace TheOrder.Presentation;

public enum StartupPhase { Loading, PreparingFirstPage, Ready, Failed }

// The window can show progress immediately. Remote recognition/update jobs are outside
// this gate; only local state, the visible artwork attempts and the final layout belong here.
public sealed class StartupReadiness
{
    private bool started;
    public StartupPhase Phase { get; private set; } = StartupPhase.Loading;
    public bool CanReveal => Phase == StartupPhase.Ready;
    public async Task PrepareAsync(Func<Task> initialize, Func<Task> visibleArtwork, Func<Task> nextFrame)
    {
        if (started) throw new InvalidOperationException("Startup already started");
        started = true;
        try
        {
            await nextFrame(); // Present the lightweight loading surface before constructing pages.
            await initialize();
            Phase = StartupPhase.PreparingFirstPage;
            await nextFrame(); // Realize the virtualized first viewport, without exposing partial content.
            await visibleArtwork();
            await nextFrame(); // Include bitmap assignment and completed layout before the switch.
            Phase = StartupPhase.Ready;
        }
        catch { Phase = StartupPhase.Failed; throw; }
    }
}
