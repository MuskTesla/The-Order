using TheOrder.Application;

namespace TheOrder.Presentation;

public enum AniListDetailState { Idle, Loading, Choosing, Preview, Ready, Cancelled, Failed }
public sealed class AniListDetailsViewModel(IBangumiSubjectDetailsReader subjects, IAniListReader reader) : ObservableState, IDisposable
{
    private sealed record Entry(AniListMedia? Media, AniListResult? Result, AniListResult[] Pages,
        AniListDetailState State, string Reason, string? Origin, string? Relation, double Offset);
    private readonly Stack<Entry> history = new();
    private CancellationTokenSource? request; private long generation; private bool active, disposed;
    private AniListDetailState resumeState;
    public BangumiRelationItem? Item { get; private set; }
    public BangumiSubjectDetailsResult? Bridge { get; private set; }
    public AniListSource? Source { get; private set; }
    public AniListMedia[] Candidates { get; private set; } = [];
    public AniListMedia? Media { get; private set; }
    public AniListResult? Displayed { get; private set; }
    public AniListResult[] PeoplePages { get; private set; } = [];
    public AniListDetailState State { get; private set; }
    public string Message { get; private set; } = "";
    public string SelectionReason { get; private set; } = "";
    public string? IncomingOrigin { get; private set; }
    public string? IncomingRelation { get; private set; }
    public bool IsBridge => history.Count == 0;
    public int HistoryDepth => history.Count;
    public bool IsBusy => request is not null;
    public bool HasUnconfirmedPreview => Displayed is not null && SelectionReason.StartsWith("条目预览", StringComparison.Ordinal);
    public bool CanLoad => active && !disposed && !IsBusy;
    public bool CanConfirm => CanLoad && IsBridge && State == AniListDetailState.Preview && Source is not null && Displayed is not null && !AniListMatching.Assess(Source, Displayed.Details.Media).Conflict;
    public bool CanMore => CanLoad && State is AniListDetailState.Ready or AniListDetailState.Preview && Displayed is not null && PeoplePages.LastOrDefault()?.Details is { PeoplePage: < 10 } d && (d.MoreCharacters || d.MoreStaff);
    public double RestoreOffset { get; private set; }
    public long RestoreVersion { get; private set; }
    public Task OpenAsync(BangumiRelationItem item, CancellationToken cancellation = default)
    {
        Stop(); active = true; history.Clear(); Item = item; Bridge = null; Source = null; Candidates = []; Media = null; Displayed = null; PeoplePages = [];
        IncomingOrigin = IncomingRelation = null; SelectionReason = ""; State = AniListDetailState.Idle;
        RestoreOffset = 0; RestoreVersion++;
        return RunAsync(async token =>
        {
            if (item.Kind is not (BangumiRelationKind.Anime or BangumiRelationKind.Book)) throw new InvalidDataException("作品媒体类型不适用。");
            var bridge = await subjects.LoadAsync(item.Id, item.Kind, false, token); Require(token);
            Bridge = bridge; Source = AniListSource.From(bridge.Details); Notify();
            if (item.Kind == BangumiRelationKind.Book && bridge.Details.BookFacts is { IsSeries: false, Isbns.Length: > 0 }
                && bridge.Details.ExternalLinks.Length == 0)
            { State = AniListDetailState.Ready; Message = "当前为单本出版资料，可查看出版信息与关联系列。"; return; }
            await MatchCoreAsync(null, false, token);
        }, cancellation);
    }
    public Task SearchAsync(string? query = null) => !CanLoad || Source is null || !IsBridge ? Task.CompletedTask
        : RunAsync(async token => { Displayed = null; Media = null; PeoplePages = []; await MatchCoreAsync(query, true, token); });
    private async Task MatchCoreAsync(string? query, bool refresh, CancellationToken token)
    {
        var match = await reader.MatchAsync(Source!, query, refresh, token); Require(token); Candidates = match.Candidates;
        if (match.Binding is { } binding)
        {
            var details = await reader.LoadAsync(binding.MediaId, binding.Kind, false, 1, token); Require(token);
            if (AniListMatching.Assess(Source!, details.Details.Media).Conflict) throw new InvalidDataException("已保存的关联与当前作品证据不一致，请重新选择。");
            Display(details); State = AniListDetailState.Ready; SelectionReason = binding.Evidence; Message = details.Warning ?? "";
        }
        else
        {
            State = AniListDetailState.Choosing; Message = match.Notice ?? (Candidates.Length == 0
                ? "暂未找到对应条目，可用原文标题或 AniList 链接重试；Bangumi 资料仍可浏览。"
                : "请核对发行形式与年份，选择对应的 AniList 条目。");
        }
    }
    public Task PreviewAsync(int id) => !CanLoad || !IsBridge || Candidates.SingleOrDefault(x => x.Id == id) is not { } candidate ? Task.CompletedTask
        : RunAsync(async token =>
        {
            var result = await reader.LoadAsync(candidate.Id, candidate.Kind, false, 1, token); Require(token);
            Display(result); State = AniListDetailState.Preview; SelectionReason = "条目预览 · 尚未确认";
            Message = string.Join(" · ", AniListMatching.Assess(Source!, result.Details.Media).Evidence);
        });
    public Task ConfirmAsync() => !CanConfirm ? Task.CompletedTask : RunAsync(async token =>
    {
        await reader.ConfirmAsync(Source!, Displayed!.Details, token); Require(token);
        State = AniListDetailState.Ready; SelectionReason = "用户核对确认 · Bangumi → AniList"; Message = Displayed.Warning ?? "";
    });
    public Task ChooseAgainAsync()
    {
        if (!CanLoad || !IsBridge || Source is null) return Task.CompletedTask;
        return RunAsync(async token =>
        {
            Displayed = null; Media = null; PeoplePages = []; Candidates = []; SelectionReason = ""; Notify();
            // Bypass the saved binding, while retaining native-title fallback.
            // Keep that binding until a different preview is actually confirmed.
            var match = await reader.FindCandidatesAsync(Source, false, token); Require(token);
            Candidates = match.Candidates; State = AniListDetailState.Choosing;
            Message = match.Notice ?? (Candidates.Length > 0 ? "请选择其他条目，确认之前保留原关联。" : "暂未找到其他候选，可输入原文标题或 AniList 链接查找。");
        });
    }
    public Task ExploreAsync(AniListRelation relation, double scrollOffset = 0)
    {
        if (!CanLoad || State != AniListDetailState.Ready || Displayed is null || history.Count >= 20
            || !Displayed.Details.Relations.Contains(relation)) return Task.CompletedTask;
        history.Push(new(Media, Displayed, PeoplePages, State, SelectionReason, IncomingOrigin, IncomingRelation, scrollOffset));
        IncomingOrigin = Displayed.Details.Media.Title; IncomingRelation = relation.Relation;
        Media = relation.Media; Displayed = null; PeoplePages = []; SelectionReason = "AniList 原站作品关系";
        RestoreOffset = 0; RestoreVersion++;
        return RunAsync(async token =>
        {
            var result = await reader.LoadAsync(relation.Media.Id, relation.Media.Kind, false, 1, token); Require(token);
            Display(result); State = AniListDetailState.Ready; Message = result.Warning ?? "";
        });
    }
    public bool GoBack()
    {
        if (!active || disposed || !history.TryPop(out var entry)) return false;
        Stop(); Media = entry.Media; Displayed = entry.Result; PeoplePages = entry.Pages; State = entry.State; SelectionReason = entry.Reason;
        IncomingOrigin = entry.Origin; IncomingRelation = entry.Relation; Message = Displayed?.Warning ?? "";
        RestoreOffset = entry.Offset; RestoreVersion++; Notify(); return true;
    }
    public Task MoreAsync() => !CanMore || Media is null ? Task.CompletedTask : RunAsync(async token =>
    {
        var result = await reader.LoadAsync(Media.Id, Media.Kind, false, PeoplePages.Last().Details.PeoplePage + 1, token); Require(token);
        PeoplePages = PeoplePages.Append(result).ToArray(); State = resumeState; Message = result.Warning ?? "";
    });
    public Task RefreshAsync() => !CanLoad ? Task.CompletedTask : Media is not { } media
        ? Item is { } item ? OpenAsync(item) : Task.CompletedTask : RunAsync(async token =>
        {
            var result = await reader.LoadAsync(media.Id, media.Kind, true, 1, token); Require(token); Display(result);
            State = SelectionReason.StartsWith("条目预览", StringComparison.Ordinal) ? AniListDetailState.Preview : AniListDetailState.Ready; Message = result.Warning ?? "";
        });
    private void Display(AniListResult result) { Displayed = result; Media = result.Details.Media; PeoplePages = [result]; }
    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellation = default)
    {
        if (disposed || !active || IsBusy) return;
        Stop(); var version = generation; using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation); request = pending;
        var oldState = State; resumeState = oldState; State = AniListDetailState.Loading; Message = "正在读取 AniList 资料…"; Notify();
        bool Owns() => active && !disposed && generation == version;
        try { await action(pending.Token); if (!Owns() || pending.IsCancellationRequested) return; }
        catch (OperationCanceledException) { if (Owns()) { State = AniListDetailState.Cancelled; Message = "已取消读取，已有资料保留。"; } }
        catch (Exception e)
        {
            if (!Owns() || pending.IsCancellationRequested) return;
            if (e is AniListException { Failure: AniListFailure.Unavailable or AniListFailure.InvalidResponse or AniListFailure.ResponseTooLarge })
            { Displayed = null; PeoplePages = []; }
            State = Displayed is not null ? oldState : AniListDetailState.Failed;
            Message = e is AniListException or InvalidDataException or IOException ? e.Message : "AniList 资料暂时无法读取，Bangumi 资料仍可浏览。";
        }
        finally { if (ReferenceEquals(request, pending)) request = null; if (Owns()) Notify(); }
    }
    private void Require(CancellationToken token) { token.ThrowIfCancellationRequested(); if (!active || disposed) throw new OperationCanceledException(token); }
    public void Cancel() { if (!IsBusy) return; Stop(); State = Displayed is not null ? resumeState : AniListDetailState.Cancelled; Message = "已取消读取，已有资料保留。"; Notify(); }
    private void Stop() { generation++; request?.Cancel(); request = null; }
    public void Deactivate() { Stop(); active = false; Notify(); }
    private void Notify() => Changed(string.Empty);
    public void Dispose() { if (disposed) return; Deactivate(); disposed = true; }
}
