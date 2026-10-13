using Microsoft.UI.Xaml.Media;
using TheOrder.Presentation;

namespace TheOrder.WinUI;

public sealed partial class MainWindow
{
    // Reachable only from the existing isolated Test command fixture. Observe
    // actual composition ticks around navigation; never run a game or edit data.
    private async Task<object> AuditHeroFramesAsync(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId) || !ViewModel.Items.Any(item => item.Id == gameId))
            throw new InvalidDataException("An existing Test library item is required.");
        var samples = new List<object>(120);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Sample(object? sender, object args)
        {
            if (samples.Count < 120)
                samples.Add(new { milliseconds = clock.ElapsedMilliseconds, detailsActive = ReferenceEquals(PageContent.Content, detailsPage), state = detailsPage.FramePresentationEvidence });
        }
        CompositionTarget.Rendering += Sample;
        try
        {
            await Navigate(WorkspacePage.Details, gameId);
            await Task.Delay(1500);
        }
        finally { CompositionTarget.Rendering -= Sample; }
        return new { gameId, samples, final = detailsPage.FramePresentationEvidence };
    }
}
