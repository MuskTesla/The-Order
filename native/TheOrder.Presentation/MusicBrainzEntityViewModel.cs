using TheOrder.Application;

namespace TheOrder.Presentation;

public enum MusicBrainzEntityState { Idle, Loading, Ready, Failed, Cancelled }
public sealed class MusicBrainzEntityViewModel(IMusicBrainzEntityReader reader) : ObservableState, IDisposable
{
    private CancellationTokenSource? operation;
    private long generation;
    private bool active, disposed;
    public MusicBrainzReference? Reference { get; private set; }
    public MusicBrainzEntityResult? Result { get; private set; }
    public MusicBrainzEntityDetail? Detail => Result?.Detail;
    public MusicBrainzEntityState State { get; private set; }
    public string Message { get; private set; } = "";
    public long Version => generation;
    public bool IsBusy => State == MusicBrainzEntityState.Loading;
    public bool CanLoad => !disposed && active && Reference is not null && !IsBusy;
    public Task OpenAsync(MusicBrainzReference reference, CancellationToken cancellation = default)
    {
        if (disposed) return Task.CompletedTask;
        Stop(); active = true; Result = null; Reference = reference; State = MusicBrainzEntityState.Idle; Message = "";
        if (!MusicBrainzEntityValidation.Reference(reference))
        { Reference = null; State = MusicBrainzEntityState.Failed; Message = "音乐实体编号或类型无效。"; Notify(); return Task.CompletedTask; }
        return ReadAsync(false, cancellation);
    }
    public void Restore(MusicBrainzEntityResult result)
    {
        if (disposed) return;
        Stop(); active = true;
        if (!MusicBrainzEntityValidation.Detail(result.Detail))
        { Reference = null; Result = null; State = MusicBrainzEntityState.Failed; Message = "先前音乐资料无效，请重新打开。"; Notify(); return; }
        Reference = result.Detail.Reference; Result = result; State = MusicBrainzEntityState.Ready; Message = result.Warning ?? ""; Notify();
    }
    public Task RefreshAsync(CancellationToken cancellation = default) => ReadAsync(true, cancellation);
    private async Task ReadAsync(bool refresh, CancellationToken cancellation)
    {
        if (!CanLoad || Reference is not { } reference) return;
        Stop(); var version = generation;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation); operation = request;
        bool Owns() => !disposed && active && generation == version && Reference == reference;
        bool Accepts() => Owns() && !request.IsCancellationRequested;
        State = MusicBrainzEntityState.Loading; Message = "正在读取 MusicBrainz 专业资料…"; Notify();
        try
        {
            var result = await reader.LoadAsync(reference, refresh, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!Accepts()) return;
            if (!MusicBrainzEntityValidation.Detail(result.Detail) || result.Detail.Reference != reference)
                throw new MusicBrainzException(MusicBrainzFailure.InvalidResponse, "音乐资料与所选实体不一致。");
            Result = result; State = MusicBrainzEntityState.Ready; Message = result.Warning ?? "";
        }
        catch (OperationCanceledException)
        { if (Owns()) { State = MusicBrainzEntityState.Cancelled; Message = "已取消读取。"; } }
        catch (Exception e)
        {
            if (!Accepts()) return;
            if (e is not MusicBrainzException { Failure: MusicBrainzFailure.Offline or MusicBrainzFailure.Timeout or MusicBrainzFailure.RateLimited or MusicBrainzFailure.Server }) Result = null;
            State = MusicBrainzEntityState.Failed; Message = e is MusicBrainzException ? e.Message : "音乐资料暂时无法读取，请重试。";
        }
        finally { if (ReferenceEquals(operation, request)) operation = null; if (Owns()) Notify(); }
    }
    public void Cancel()
    {
        if (disposed || !IsBusy) return;
        Stop(); State = MusicBrainzEntityState.Cancelled; Message = "已取消读取。"; Notify();
    }
    public void Deactivate()
    { if (disposed) return; Stop(); active = false; State = MusicBrainzEntityState.Idle; Message = ""; Notify(); }
    private void Stop() { generation++; operation?.Cancel(); operation = null; }
    private void Notify() => Changed(string.Empty);
    public void Dispose() { if (disposed) return; Stop(); disposed = true; active = false; Reference = null; Result = null; }
}
