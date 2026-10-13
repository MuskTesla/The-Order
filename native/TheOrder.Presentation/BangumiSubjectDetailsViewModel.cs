using TheOrder.Application;

namespace TheOrder.Presentation;

public enum BangumiSubjectDetailsState { Idle, Loading, Ready, Cancelled, Failed }

public sealed class BangumiSubjectDetailsViewModel(IBangumiSubjectDetailsReader reader) : ObservableState, IDisposable
{
    private CancellationTokenSource? operation;
    private long generation;
    private bool active, disposed;
    public BangumiRelationItem? Item { get; private set; }
    public BangumiSubjectDetailsResult? Result { get; private set; }
    public BangumiSubjectDetailsState State { get; private set; }
    public string Message { get; private set; } = "";
    public bool IsBusy => State == BangumiSubjectDetailsState.Loading;
    public bool CanLoad => !disposed && active && Item is not null && !IsBusy;
    public bool CanCancel => !disposed && active && IsBusy;

    public Task OpenAsync(BangumiRelationItem item, CancellationToken cancellation = default)
    {
        if (disposed) return Task.CompletedTask;
        StopRequest(); Result = null; Item = item; active = true;
        State = BangumiSubjectDetailsState.Idle;
        if (item is null || !BangumiRelationIdentityResolver.ValidSubjectId(item.Id) || !Enum.IsDefined(item.Kind))
        { Item = null; State = BangumiSubjectDetailsState.Failed; Message = "作品身份无效，请返回关联列表重试。"; Notify(); return Task.CompletedTask; }
        return LoadAsync(false, cancellation);
    }
    public Task RefreshAsync(CancellationToken cancellation = default) => LoadAsync(true, cancellation);

    private async Task LoadAsync(bool forceRefresh, CancellationToken cancellation)
    {
        if (!CanLoad || Item is not { } item) return;
        StopRequest(); var version = generation;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        operation = request;
        bool Owns() => !disposed && active && generation == version && Item == item;
        bool Accept() => Owns() && !request.IsCancellationRequested;
        State = BangumiSubjectDetailsState.Loading; Message = "正在读取作品资料…"; Notify();
        try
        {
            var result = await reader.LoadAsync(item.Id, item.Kind, forceRefresh, request.Token);
            if (!Accept()) return;
            if (!BangumiSubjectDetailsService.Valid(item.Id, item.Kind, result.Details))
                throw new BangumiRelationsException(BangumiRelationsFailure.InvalidResponse, "返回的作品身份或资料格式不一致。");
            Result = result; State = BangumiSubjectDetailsState.Ready;
            Message = result.Warning ?? (result.IsStale ? "当前显示之前读取的资料。" : "");
        }
        catch (OperationCanceledException)
        {
            if (Owns()) { State = BangumiSubjectDetailsState.Cancelled; Message = CancelledMessage(); }
        }
        catch (Exception e)
        {
            if (!Accept()) return;
            // Never retain detail content after an identity/content-access rejection.
            if (e is not BangumiRelationsException { Failure: BangumiRelationsFailure.Offline or BangumiRelationsFailure.Timeout
                or BangumiRelationsFailure.RateLimited or BangumiRelationsFailure.Server }) Result = null;
            State = BangumiSubjectDetailsState.Failed;
            Message = (e is BangumiRelationsException ? e.Message : "作品资料暂时无法读取，请稍后重试。")
                + (Result is not null ? " 已保留上次资料。" : "");
        }
        finally
        {
            if (ReferenceEquals(operation, request)) operation = null;
            if (Owns())
            {
                if (State == BangumiSubjectDetailsState.Loading)
                { State = BangumiSubjectDetailsState.Cancelled; Message = CancelledMessage(); }
                Notify();
            }
        }
    }
    public void Cancel()
    {
        if (disposed || !IsBusy) return;
        StopRequest(); State = BangumiSubjectDetailsState.Cancelled; Message = CancelledMessage(); Notify();
    }
    public void Deactivate()
    {
        if (disposed) return;
        Cancel(); StopRequest(); active = false; Notify();
    }
    private string CancelledMessage() => Result is null ? "已取消读取，可重试或返回关联列表。" : "已取消刷新，保留上次资料。";
    private void StopRequest() { generation++; operation?.Cancel(); operation = null; }
    private void Notify() => Changed(string.Empty);
    public void Dispose()
    {
        if (disposed) return;
        Deactivate(); disposed = true;
    }
}
