using System.Collections.Immutable;
using TheOrder.Application;

namespace TheOrder.Presentation;

public enum BangumiRelatedState { Idle, NoIdentity, Loading, Ready, Empty, Cancelled, Failed }

// A read-only page session. Its identity includes the local entry incarnation as well as
// the exact upstream anchor, so even an A -> B -> A navigation rejects A's first request.
public sealed class BangumiRelatedViewModel(IBangumiRelationsReader reader,
    Func<BangumiRelationIdentity, bool>? isCurrent = null) : ObservableState, IDisposable
{
    private CancellationTokenSource? operation;
    private long generation;
    private bool active, expanded, disposed;
    private string missingReason = "尚无可靠的 Bangumi 作品编号。";
    public BangumiRelationIdentity? Identity { get; private set; }
    public BangumiRelationsLoadResult? Result { get; private set; }
    public BangumiRelatedState State { get; private set; } = BangumiRelatedState.Idle;
    public string Message { get; private set; } = "展开后读取 Bangumi 公开关联。";
    public bool IsExpanded => expanded;
    public bool IsBusy => State == BangumiRelatedState.Loading;
    public bool CanLoad => !disposed && active && expanded && Identity is not null && !IsBusy;
    public bool CanCancel => !disposed && active && IsBusy;
    public bool HasResult => Result is not null;
    public ImmutableArray<BangumiRelationItem> Items => Result?.Snapshot.Items ?? [];

    public void SetContext(BangumiRelationIdentity? identity, string? reason = null)
    {
        if (disposed) return;
        var explanation = string.IsNullOrWhiteSpace(reason) ? "尚无可靠的 Bangumi 作品编号。" : reason;
        if (identity == Identity && (identity is not null || explanation == missingReason))
        {
            if (!active)
            {
                active = true;
                if (identity is null) { State = BangumiRelatedState.NoIdentity; Message = explanation; }
                Notify();
            }
            return;
        }
        StopRequest();
        active = true; expanded = false; Identity = identity; missingReason = explanation;
        Result = null;
        State = identity is null ? BangumiRelatedState.NoIdentity : BangumiRelatedState.Idle;
        Message = identity is null ? explanation : "展开后读取 Bangumi 公开关联。";
        Notify();
    }

    public Task ExpandAsync()
    {
        if (disposed || !active) return Task.CompletedTask;
        expanded = true;
        if (Identity is null)
        { State = BangumiRelatedState.NoIdentity; Message = missingReason; Notify(); return Task.CompletedTask; }
        Notify();
        return Result is not null && State is BangumiRelatedState.Ready or BangumiRelatedState.Empty
            ? Task.CompletedTask : LoadAsync(false);
    }

    public void Collapse()
    {
        if (disposed) return;
        expanded = false; Cancel(); Notify();
    }

    public Task RefreshAsync() => LoadAsync(true);

    private async Task LoadAsync(bool forceRefresh)
    {
        if (!CanLoad || Identity is not { } identity) return;
        if (!(isCurrent?.Invoke(identity) ?? true))
        { InvalidateContext(); Notify(); return; }
        StopRequest();
        using var request = new CancellationTokenSource();
        operation = request;
        var version = generation;
        bool Owns() => !disposed && active && expanded && generation == version && Identity == identity;
        bool Accept() => Owns() && !request.IsCancellationRequested && (isCurrent?.Invoke(identity) ?? true);
        State = BangumiRelatedState.Loading;
        Message = HasResult ? "正在刷新 Bangumi 公开关联，当前结果仍可查看…" : "正在读取 Bangumi 公开关联…";
        Notify();
        try
        {
            var result = await reader.LoadAsync(identity, forceRefresh, request.Token);
            if (!Accept()) return;
            if (!BangumiRelationsService.Valid(identity, result.Snapshot))
                throw new InvalidDataException("Bangumi 返回的作品编号或关联列表无效。");
            Result = result;
            State = result.Snapshot.Items.IsEmpty ? BangumiRelatedState.Empty : BangumiRelatedState.Ready;
            Message = ResultMessage(result);
        }
        catch (OperationCanceledException)
        {
            if (Owns()) { State = BangumiRelatedState.Cancelled; Message = CancelledMessage(); }
        }
        catch (Exception e)
        {
            if (Accept())
            {
                State = BangumiRelatedState.Failed;
                Message = (e is BangumiRelationsException ? e.Message : "Bangumi 关联读取未完成，请稍后重试。")
                    + (HasResult ? " 已保留上次结果。" : "");
            }
        }
        finally
        {
            if (ReferenceEquals(operation, request)) operation = null;
            if (Owns())
            {
                // A library identity can change before its next visual notification.
                // Reject that result and leave a retryable state instead of hanging busy.
                if (State == BangumiRelatedState.Loading)
                    InvalidateContext();
                Notify();
            }
        }
    }

    private static string ResultMessage(BangumiRelationsLoadResult result)
    {
        var snapshot = result.Snapshot;
        var parts = new List<string>();
        if (snapshot.Items.IsEmpty)
            parts.Add(snapshot.UnsupportedCount > 0 ? "这四类中暂无可公开读取的关联作品。" : "Bangumi 暂无可公开读取的关联作品。");
        parts.Add((result.FromCache ? result.IsStale ? "较早缓存" : "缓存" : "已更新")
            + $" · {snapshot.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (snapshot.Truncated) parts.Add("关联较多，当前展示部分结果。");
        if (snapshot.UnsupportedCount > 0) parts.Add($"另有 {snapshot.UnsupportedCount} 项其他类型。");
        if (!string.IsNullOrWhiteSpace(result.Warning)) parts.Add(result.Warning);
        return string.Join(" ", parts);
    }

    private string CancelledMessage() => HasResult ? "已取消刷新，保留上次结果。" : "已取消读取，可重新加载。";
    private void InvalidateContext()
    {
        StopRequest(); Identity = null; Result = null; expanded = false;
        State = BangumiRelatedState.Cancelled;
        missingReason = Message = "作品关联已变化，请重新展开后读取。";
    }
    public void Cancel()
    {
        if (disposed || !IsBusy) return;
        StopRequest(); State = BangumiRelatedState.Cancelled; Message = CancelledMessage(); Notify();
    }

    public void Deactivate()
    {
        if (disposed) return;
        active = false; expanded = false; Cancel(); StopRequest(); Notify();
    }

    private void StopRequest() { generation++; operation?.Cancel(); operation = null; }
    private void Notify() => Changed(string.Empty);
    public void Dispose()
    {
        if (disposed) return;
        StopRequest();
        if (IsBusy) { State = BangumiRelatedState.Cancelled; Message = CancelledMessage(); }
        disposed = true; active = expanded = false;
    }
}
