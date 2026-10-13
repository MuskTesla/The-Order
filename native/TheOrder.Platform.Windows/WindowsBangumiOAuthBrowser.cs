using System.Diagnostics;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsBangumiOAuthBrowser : IBangumiOAuthBrowser
{
    public Task OpenAsync(Uri authorization, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!BangumiOAuthBrowserPolicy.ValidOrigin(authorization)) throw new BangumiAuthorizationException(BangumiAuthorizationFailure.InvalidResponse);
        try { using var process = Process.Start(new ProcessStartInfo(authorization.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }
        catch { throw new BangumiAuthorizationException(BangumiAuthorizationFailure.Offline); }
    }
}
