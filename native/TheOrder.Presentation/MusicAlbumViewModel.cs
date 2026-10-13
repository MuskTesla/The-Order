using TheOrder.Application;

namespace TheOrder.Presentation;

public enum MusicAlbumState { Idle, LoadingSource, Matching, LoadingAlbum, Choosing, Preview, Ready, Cancelled, Failed }
public sealed class MusicAlbumViewModel(IBangumiSubjectDetailsReader subjects, IMusicBrainzReader music) : ObservableState, IDisposable
{
    private CancellationTokenSource? operation;
    private long generation;
    private bool active, disposed;
    public BangumiRelationItem? Item { get; private set; }
    public BangumiSubjectDetailsResult? SourceDetails { get; private set; }
    public MusicAlbumSource? Source { get; private set; }
    public MusicBrainzMatchResult? Match { get; private set; }
    public MusicBrainzAlbumResult? Album { get; private set; }
    public MusicBrainzAlbumResult? Preview { get; private set; }
    public MusicAlbumState State { get; private set; }
    public string Message { get; private set; } = "";
    public string SelectionReason { get; private set; } = "";
    public bool IsBusy => State is MusicAlbumState.LoadingSource or MusicAlbumState.Matching or MusicAlbumState.LoadingAlbum;
    public bool CanLoad => active && !disposed && !IsBusy && Item is not null;
    public bool CanConfirm => CanLoad && State == MusicAlbumState.Preview && Source is not null && Preview is not null;
    public bool CanCancel => active && !disposed && IsBusy;
    public MusicBrainzAlbumResult? Displayed => Preview ?? Album;
    public Task OpenAsync(BangumiRelationItem item, CancellationToken cancellation = default)
    {
        if (disposed) return Task.CompletedTask;
        Stop(); active = true; Item = item; SourceDetails = null; Source = null; Match = null; Album = Preview = null; SelectionReason = "";
        State = MusicAlbumState.Idle;
        if (item.Kind != BangumiRelationKind.Music || !BangumiRelationIdentityResolver.ValidSubjectId(item.Id))
        { State = MusicAlbumState.Failed; Message = "音乐条目身份无效，请返回重试。"; Notify(); return Task.CompletedTask; }
        return ReadAsync(false, false, cancellation);
    }
    public Task RefreshAsync(CancellationToken cancellation = default) => ReadAsync(true, false, cancellation);
    public Task SearchAsync(string query, CancellationToken cancellation = default)
    {
        if (!CanLoad || Source is not { } bridge || string.IsNullOrWhiteSpace(query)) return Task.CompletedTask;
        query = query.Trim();
        if (!MusicBrainzValidation.Text(query, 2000, false)) return Task.CompletedTask;
        MusicAlbumSource search;
        if (MusicBrainzReference.TryParse(query, out var reference))
            search = bridge with { Facts = BangumiMusicFacts.Empty with { ExternalIds = [reference!] } };
        else
        {
            if (Uri.TryCreate(query, UriKind.Absolute, out _)) { Message = "请填写专辑名，或 MusicBrainz 发行版／专辑组的链接。"; Notify(); return Task.CompletedTask; }
            search = bridge with { Title = query, Facts = BangumiMusicFacts.Empty };
        }
        return Request(async (token, accepts) =>
        {
            State = MusicAlbumState.Matching; Message = "正在查找音乐发行版…"; Preview = null; Notify();
            var found = await music.MatchAsync(search, true, token);
            if (!accepts()) return;
            if (!MusicBrainzValidation.Search(found.Search)) throw new InvalidDataException();
            // Manual retrieval never changes the original Bangumi facts or bypasses confirmation.
            Match = found with { AutomaticReleaseId = null, ConfirmedReleaseId = null }; State = MusicAlbumState.Choosing;
            Message = found.Search.Candidates.Length == 0 ? "未找到发行版，可以调整名称或粘贴 MusicBrainz 条目链接。"
                : "请选择发行版，核对曲目后确认。";
        }, cancellation);
    }
    public Task ChooseEditionAsync(CancellationToken cancellation = default)
    {
        if (!CanLoad) return Task.CompletedTask;
        var group = (Preview ?? Album)?.Album.Release.ReleaseGroupId;
        if (group is null) return ReadAsync(true, true, cancellation);
        return Request(async (token, accepts) =>
        {
            State = MusicAlbumState.Matching; Message = "正在读取此专辑的其他发行版…"; Notify();
            var result = await music.EditionsAsync(group, token);
            if (!accepts()) return;
            if (!MusicBrainzValidation.Search(result) || result.Candidates.Any(x => x.ReleaseGroupId != group)) throw new InvalidDataException();
            Match = new(result, null, null, false, false, null); Preview = null; State = MusicAlbumState.Choosing;
            Message = "不同发行版可能有不同曲目或译名。请先预览，再确认。";
            if (result.Truncated) Message += " 当前显示前 25 项。";
            if (result.Notice is not null) Message += " " + result.Notice;
        }, cancellation);
    }
    private async Task ReadAsync(bool refresh, bool choose, CancellationToken cancellation)
    {
        if (!CanLoad || Item is not { } item) return;
        await Request(async (token, accepts) =>
        {
            State = MusicAlbumState.LoadingSource; Message = "正在读取音乐条目的发行信息…"; Notify();
            var details = await subjects.LoadAsync(item.Id, item.Kind, refresh, token);
            if (!accepts()) return;
            if (!BangumiSubjectDetailsService.Valid(item.Id, item.Kind, details.Details)) throw new InvalidDataException();
            SourceDetails = details; Source = MusicAlbumSource.From(details.Details);
            State = MusicAlbumState.Matching; Message = "正在查找 MusicBrainz 发行版…"; Notify();
            var match = await music.MatchAsync(Source, refresh, token);
            if (!accepts()) return;
            if (!MusicBrainzValidation.Search(match.Search)) throw new InvalidDataException();
            Match = match; Preview = null;
            var id = match.ConfirmedReleaseId ?? match.AutomaticReleaseId;
            if (!choose && id is not null)
            {
                State = MusicAlbumState.LoadingAlbum; Message = "正在读取专辑与曲目…"; Notify();
                var album = await music.LoadAlbumAsync(id, refresh, token);
                if (!accepts()) return;
                Validate(id, album);
                // Search documents are not a substitute for the final release identity and facts.
                if (match.ConfirmedReleaseId is not null || MusicBrainzMatching.Assess(Source, album.Album.Release).Strong)
                {
                    Album = album; State = MusicAlbumState.Ready;
                    SelectionReason = match.ConfirmedReleaseId is not null ? "使用已确认的发行版" : "目录编号与发行信息匹配";
                    Message = Warnings(details.Warning, match.Warning, album.Warning, album.Album.CoverWarning); return;
                }
            }
            State = MusicAlbumState.Choosing;
            Message = match.Search.Candidates.Length == 0 ? "MusicBrainz 暂未找到对应发行版，仍可查看 Bangumi 资料。"
                : "请选择具体发行版。可以先查看曲目，再确认使用。";
            if (match.Search.Truncated) Message += " 当前仅列出前 25 项，请在原站进一步核对。";
        }, cancellation);
    }
    public async Task PreviewAsync(string id, CancellationToken cancellation = default)
    {
        if (!CanLoad || Match?.Search.Candidates.Any(x => x.Id == id) != true) return;
        await Request(async (token, accepts) =>
        {
            Preview = null; State = MusicAlbumState.LoadingAlbum; Message = "正在读取所选发行版的曲目…"; Notify();
            var value = await music.LoadAlbumAsync(id, false, token);
            if (!accepts()) return; Validate(id, value);
            Preview = value; State = MusicAlbumState.Preview;
            Message = "这是发行版预览。确认后将记住此选择。";
            if (Source is not null && MusicBrainzMatching.Assess(Source, value.Album.Release).HasConflict)
                Message += " 发行信息与 Bangumi 不完全一致，请仔细核对。";
            Message = Warnings(Message, value.Warning, value.Album.CoverWarning);
        }, cancellation);
    }
    public async Task ConfirmAsync(CancellationToken cancellation = default)
    {
        if (!CanConfirm || Source is not { } source || Preview is not { } preview
            || Match?.Search.Candidates.Any(x => x.Id == preview.Album.Release.Id) != true) return;
        await Request(async (token, accepts) =>
        {
            State = MusicAlbumState.LoadingAlbum; Message = "正在保存发行版选择…"; Notify();
            await music.ConfirmAsync(source, preview.Album, token);
            if (!accepts()) return;
            Album = preview; Preview = null; State = MusicAlbumState.Ready; SelectionReason = "你确认的发行版";
            Message = Warnings(preview.Warning, preview.Album.CoverWarning);
        }, cancellation);
    }
    public void BackToEditions()
    {
        if (!CanLoad || Match is null) return;
        Preview = null; State = MusicAlbumState.Choosing; Message = "请选择具体发行版。"; Notify();
    }
    private async Task Request(Func<CancellationToken, Func<bool>, Task> action, CancellationToken cancellation)
    {
        Stop(); var version = generation; var item = Item;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation); operation = request;
        bool Owns() => !disposed && active && generation == version && Item == item;
        bool Accept() => Owns() && !request.IsCancellationRequested;
        try { await action(request.Token, Accept); }
        catch (OperationCanceledException) { if (Owns()) { State = MusicAlbumState.Cancelled; Message = "已取消读取，可重试或返回关联列表。"; } }
        catch (Exception e)
        {
            if (!Accept()) return;
            if (e is BangumiRelationsException { Failure: BangumiRelationsFailure.InvalidResponse or BangumiRelationsFailure.Unavailable })
            { Source = null; SourceDetails = null; Match = null; Album = Preview = null; }
            if (e is MusicBrainzException { Failure: MusicBrainzFailure.InvalidResponse or MusicBrainzFailure.Unavailable or MusicBrainzFailure.ResponseTooLarge })
                Album = Preview = null;
            State = MusicAlbumState.Failed;
            Message = e is MusicBrainzException or BangumiRelationsException ? e.Message
                : e is IOException ? "发行版选择未能保存，可以重试。" : "音乐资料暂时不可用，已保留 Bangumi 资料。";
        }
        finally
        {
            if (ReferenceEquals(operation, request)) operation = null;
            if (Owns())
            {
                if (IsBusy) { State = MusicAlbumState.Cancelled; Message = "已取消读取，可重试。"; }
                Notify();
            }
        }
    }
    private static void Validate(string id, MusicBrainzAlbumResult result)
    { if (!MusicBrainzValidation.Album(result.Album) || result.Album.Release.Id != id) throw new MusicBrainzException(MusicBrainzFailure.InvalidResponse, "专辑身份不一致，未显示资料。"); }
    private static string Warnings(params string?[] values) => string.Join(" ", values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
    public void Cancel()
    {
        if (!CanCancel) return; Stop(); State = MusicAlbumState.Cancelled; Message = "已取消读取，可重试或返回关联列表。"; Notify();
    }
    public void Deactivate() { Stop(); active = false; Notify(); }
    private void Stop() { generation++; operation?.Cancel(); operation = null; }
    private void Notify() => Changed(string.Empty);
    public void Dispose() { if (disposed) return; Deactivate(); disposed = true; }
}
