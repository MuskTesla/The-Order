namespace TheOrder.Application;

public sealed record PersonalBannerWrite(string? Key, string? Warning = null);

// Original personal artwork belongs to this profile, separate from the small poster
// embedded in the compatible library format. Refreshing metadata cannot replace it.
public interface IPersonalBannerStore : IImageSourceProvider
{
    Task<string?> GetAsync(string gameId, CancellationToken cancellation = default);
    Task<PersonalBannerWrite> SaveAsync(string gameId, string? path, CancellationToken cancellation = default);
}

public static class PersonalBannerClient
{
    public static async Task<ClientResult> SaveAsync(IPersonalBannerStore store, string gameId, string? path, CancellationToken cancellation)
    {
        try
        {
            var result = await store.SaveAsync(gameId, path, cancellation).ConfigureAwait(false);
            return new(result.Warning is null ? ClientOutcome.Committed : ClientOutcome.CommittedWithWarning,
                result.Warning ?? UiText.Get(path is null ? "已恢复自动横幅。" : "本地横幅已保存，原图清晰度保留。"));
        }
        catch (Exception e) when (OfflineLibraryClient.Expected(e) || e is System.Runtime.InteropServices.COMException)
        { return e is System.Runtime.InteropServices.COMException ? new(ClientOutcome.ValidationFailure, UiText.Get("无法解码此图片，原横幅保持不变。")) : OfflineLibraryClient.Failure(e); }
    }
}
