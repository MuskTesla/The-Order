using TheOrder.Application;

namespace TheOrder.Presentation;

public enum BangumiEpisodesState { Idle, Loading, Ready, Empty, Cancelled, Failed }
public sealed class BangumiEpisodesViewModel(IBangumiEpisodesReader reader) : ObservableState, IDisposable
{
    public const int MaximumDisplayedRows = 500;
    private CancellationTokenSource? operation;
    private long generation;
    private bool disposed;
    public string? SubjectId { get; private set; }
    public long Version => generation;
    public BangumiEpisode[] Episodes { get; private set; } = [];
    public BangumiEpisodesResult? Result { get; private set; }
    public BangumiEpisodesState State { get; private set; }
    public string Message { get; private set; } = "";
    public int NextOffset { get; private set; }
    public int Total => Result?.Page.Total ?? 0;
    public bool IsBusy => State == BangumiEpisodesState.Loading;
    public bool CanLoad => !disposed && SubjectId is not null && !IsBusy;
    public bool CanMore => CanLoad && Result is not null && NextOffset < Total
        && NextOffset <= BangumiEpisodesService.MaximumOffset && Episodes.Length < MaximumDisplayedRows;
    public bool AtDisplayLimit => Result is not null && NextOffset < Total
        && (NextOffset > BangumiEpisodesService.MaximumOffset || Episodes.Length >= MaximumDisplayedRows);

    // Callers supply only the current, verified Bangumi animation identity. A
    // source switch always advances generation, including A -> B -> A.
    public void SetSource(string? verifiedBangumiId)
    {
        if (disposed) return;
        Stop(); SubjectId = BangumiRelationIdentityResolver.ValidSubjectId(verifiedBangumiId) ? verifiedBangumiId : null;
        Episodes = []; Result = null; NextOffset = 0; State = BangumiEpisodesState.Idle; Message = ""; Notify();
    }
    public Task ExpandAsync() => Result is not null ? Task.CompletedTask : ReadAsync(false, false);
    public Task RefreshAsync() => ReadAsync(true, false);
    public Task MoreAsync() => CanMore ? ReadAsync(false, true) : Task.CompletedTask;

    private async Task ReadAsync(bool refresh, bool append)
    {
        if (!CanLoad || SubjectId is not { } id) return;
        Stop(); var version = generation; var offset = append ? NextOffset : 0;
        using var request = new CancellationTokenSource(); operation = request;
        bool Owns() => !disposed && SubjectId == id && generation == version;
        bool Accept() => Owns() && !request.IsCancellationRequested;
        State = BangumiEpisodesState.Loading; Message = "正在读取 Bangumi 章节目录…"; Notify();
        try
        {
            var result = await reader.LoadAsync(id, offset, refresh, request.Token);
            if (!Accept()) return;
            if (!BangumiEpisodesService.Valid(id, offset, result.Page))
                throw new BangumiRelationsException(BangumiRelationsFailure.InvalidResponse, "章节资料与当前作品不一致。");
            Episodes = (append ? Episodes.Concat(result.Page.Episodes) : result.Page.Episodes)
                .DistinctBy(x => x.Id).Take(MaximumDisplayedRows).ToArray();
            var stale = result.IsStale || append && Result?.IsStale == true;
            Result = result with { IsStale = stale }; NextOffset = offset + BangumiEpisodesService.PageSize;
            State = Episodes.Length == 0 ? BangumiEpisodesState.Empty : BangumiEpisodesState.Ready;
            Message = result.Warning ?? (stale ? "部分章节使用之前读取的资料；刷新目录可重新核对。" : State == BangumiEpisodesState.Empty ? "Bangumi 当前未收录章节目录。" : "");
            if (AtDisplayLimit) Message = (Message.Length > 0 ? Message + " " : "") + "本次已显示最多 500 项；完整章节目录可在 Bangumi 查看。";
        }
        catch (OperationCanceledException)
        {
            if (Owns()) { State = BangumiEpisodesState.Cancelled; Message = "已取消读取，可重试。"; }
        }
        catch (Exception e)
        {
            if (!Accept()) return;
            if (e is not BangumiRelationsException { Failure: BangumiRelationsFailure.Offline or BangumiRelationsFailure.Timeout
                or BangumiRelationsFailure.RateLimited or BangumiRelationsFailure.Server })
            { Episodes = []; Result = null; NextOffset = 0; }
            State = BangumiEpisodesState.Failed;
            Message = e is BangumiRelationsException ? e.Message : "章节目录暂时无法读取，请重试。";
        }
        finally
        {
            if (ReferenceEquals(operation, request)) operation = null;
            if (Owns()) Notify();
        }
    }
    public void Cancel()
    {
        if (disposed || !IsBusy) return;
        Stop(); State = BangumiEpisodesState.Cancelled; Message = "已取消读取，可重试。"; Notify();
    }
    private void Stop() { generation++; operation?.Cancel(); operation = null; }
    private void Notify() => Changed(string.Empty);
    public void Dispose() { if (disposed) return; Stop(); disposed = true; SubjectId = null; Episodes = []; Result = null; }
}
