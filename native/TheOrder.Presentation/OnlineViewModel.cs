using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Presentation;

public enum OnlineState { Idle, Loading, Loaded, Empty, Offline, Unauthorized, RateLimited, Error, Refreshing }
public sealed record OnlineCard(string Id, string Title, string? CoverKey, string Subtitle);
public sealed class OnlineViewModel(IOnlineCatalog catalog, ICredentialSettings credentials, IRemoteCoverRegistry covers, OnlineLibraryClient library, WorkspaceViewModel workspace) : ObservableState, IAsyncDisposable
{
    private string query = "";
    private string? cursor;
    private readonly HashSet<string> usedCursors = [];
    private CancellationTokenSource? searchStop, detailStop, mutationStop, connectionStop;
    private readonly CancellationTokenSource lifetime = new();
    private long searchGeneration, detailGeneration;
    private readonly HashSet<Task> pending = [];
    private bool disposed, settingsBusy;
    public string Query { get => query; set { if (!Set(ref query, value)) return; CancelSearch(); Items = []; cursor = null; usedCursors.Clear(); SearchState = OnlineState.Idle; SearchMessage = "输入作品名后搜索。"; Notify(); } }
    public OnlineState SearchState { get; private set; }
    public OnlineState DetailState { get; private set; }
    public OnlineState ConnectionState { get; private set; }
    public string SearchMessage { get; private set; } = "输入作品名后搜索。";
    public string DetailMessage { get; private set; } = "选择作品查看详情。";
    public string CredentialMessage { get; private set; } = "仅用于隔离开发资料；尚未检查密钥。";
    public string ConnectionMessage { get; private set; } = "尚未测试连接。";
    public IReadOnlyList<OnlineCard> Items { get; private set; } = Array.Empty<OnlineCard>();
    public WorkMetadata? Details { get; private set; }
    public string? DetailCoverKey { get; private set; }
    public bool SearchBusy => SearchState is OnlineState.Loading or OnlineState.Refreshing;
    public bool DetailBusy => DetailState is OnlineState.Loading or OnlineState.Refreshing;
    public bool CanLoadMore => !SearchBusy && cursor is not null && Items.Count < 2000;
    public bool HasCredential { get; private set; }
    public bool SettingsBusy => settingsBusy;
    public string DetailTitle => Details?.Title ?? "作品详情";
    public string DetailDescription => Details?.Description ?? "";
    public string DetailFacts => Details is { } work ? $"目录编号 {work.Id}\n{work.OriginalTitle}\n{string.Join(" · ", work.Developers)}\n{work.ReleaseDate}\n{string.Join(" · ", work.Tags)}\n{string.Join(" · ", work.Ratings.Select(r => $"{r.Source} {r.Score}/{r.Scale}"))}\n{string.Join(" · ", work.Characters.Select(c => c.Name))}" : "";
    private Task Track(Func<Task> action)
    {
        if (disposed) return Task.CompletedTask;
        var task = action(); lock (pending) pending.Add(task); _ = Observe(task); return task;
    }
    private async Task Observe(Task task) { try { await task; } catch { /* Event handlers display controlled errors; disposal must still drain. */ } finally { lock (pending) pending.Remove(task); } }
    public Task SearchAsync(bool more = false) => Track(() => SearchCore(more));
    private async Task SearchCore(bool more)
    {
        if (SearchBusy || more && !CanLoadMore) return;
        var requested = Query.Trim(); if (requested.Length == 0) return;
        var next = more ? cursor : null;
        searchStop?.Cancel(); searchStop?.Dispose(); searchStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = searchStop.Token; var generation = ++searchGeneration;
        if (!more) { Items = []; cursor = null; usedCursors.Clear(); }
        SearchState = more ? OnlineState.Refreshing : OnlineState.Loading; SearchMessage = "正在搜索在线目录…"; Notify();
        try
        {
            var page = await catalog.SearchAsync(requested, next, cancellation);
            if (disposed || generation != searchGeneration || cancellation.IsCancellationRequested) return;
            var seen = Items.Select(i => i.Id).ToHashSet();
            Items = Items.Concat(page.Items.Where(w => seen.Add(w.Id)).Select(w => new OnlineCard(w.Id, w.Title, covers.Register(w.CoverUrl, w.FetchedAt), string.Join(" · ", w.Developers)))).Take(2000).ToArray();
            if (next is not null) usedCursors.Add(next);
            cursor = page.NextCursor is { } candidate && !usedCursors.Contains(candidate) ? candidate : null;
            SearchState = Items.Count == 0 ? OnlineState.Empty : OnlineState.Loaded;
            SearchMessage = Items.Count == 0 ? "本次搜索没有结果。" : $"已加载 {Items.Count} 部作品。";
            if (page.NextCursor is not null && cursor is null) SearchMessage += " 分页标记重复，已停止加载。";
            if (Items.Count >= 2000) { cursor = null; SearchMessage += " 已达到结果上限，请缩小搜索范围。"; }
        }
        catch (OperationCanceledException) { if (generation == searchGeneration) { SearchState = OnlineState.Idle; SearchMessage = "搜索已取消。"; } }
        catch (OnlineException e) { if (generation == searchGeneration) { SearchState = State(e); SearchMessage = e.Message; } }
        catch (Exception) { if (generation == searchGeneration) { SearchState = OnlineState.Error; SearchMessage = "在线搜索未完成，请重试。"; } }
        finally { if (generation == searchGeneration) Notify(); }
    }
    public void CancelSearch() { searchGeneration++; searchStop?.Cancel(); SearchState = Items.Count > 0 ? OnlineState.Loaded : OnlineState.Idle; SearchMessage = "搜索已取消。"; Notify(); }
    public Task OpenDetailsAsync(string id, bool refresh = false) => Track(() => DetailsCore(id, refresh));
    private async Task DetailsCore(string id, bool refresh)
    {
        detailStop?.Cancel(); detailStop?.Dispose(); detailStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = detailStop.Token; var generation = ++detailGeneration;
        Details = null; DetailCoverKey = null; DetailState = refresh ? OnlineState.Refreshing : OnlineState.Loading; DetailMessage = "正在读取作品详情…"; Notify();
        try
        {
            var result = await catalog.DetailsAsync(id, refresh, cancellation);
            if (disposed || generation != detailGeneration || cancellation.IsCancellationRequested) return;
            Details = result; DetailCoverKey = covers.Register(result.CoverUrl, result.FetchedAt); DetailState = OnlineState.Loaded; DetailMessage = "在线作品 · 未自动添加到书架";
        }
        catch (OperationCanceledException) { if (generation == detailGeneration) { DetailState = OnlineState.Idle; DetailMessage = "详情读取已取消。"; } }
        catch (OnlineException e) { if (generation == detailGeneration) { DetailState = State(e); DetailMessage = e.Message; } }
        catch (Exception) { if (generation == detailGeneration) { DetailState = OnlineState.Error; DetailMessage = "作品详情未能读取，请重试。"; } }
        finally { if (generation == detailGeneration) Notify(); }
    }
    public void Leave() { CancelSearch(); detailGeneration++; detailStop?.Cancel(); mutationStop?.Cancel(); connectionStop?.Cancel(); if (DetailBusy) { DetailState = OnlineState.Idle; DetailMessage = "详情读取已取消。"; } Notify(); }
    public Task AddWishlistAsync() => Details is { } work ? Track(() => Mutate(c => library.AddAsync(work.Id, c))) : Task.CompletedTask;
    public Task RefreshEntryAsync(string id) => Track(() => Mutate(c => library.RefreshAsync(id, c), id));
    public Task RemoveWishlistAsync(string id) => Track(() => Mutate(c => library.RemoveWishlistAsync(id, c)));
    private async Task Mutate(Func<CancellationToken, Task<ClientResult>> action, string? detailId = null)
    {
        if (mutationStop is not null) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); mutationStop = stop;
        try {
        await workspace.ExecuteFileMutationAsync(async cancellation =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
            try { return await action(linked.Token); }
            catch (OnlineException e) { return new(ClientOutcome.ValidationFailure, e.Message); }
        }, detailId);
        } finally { mutationStop = null; }
    }
    public Task CheckCredentialAsync() => Track(() => Settings(async () => { HasCredential = await credentials.HasCredentialAsync(lifetime.Token); CredentialMessage = HasCredential ? "已保存隔离开发密钥。" : "尚未保存隔离开发密钥。"; }));
    public Task SaveCredentialAsync(string secret) => Track(() => Settings(async () =>
    { Leave(); catalog.ClearCache(); await credentials.SaveAsync(secret, lifetime.Token); HasCredential = true; CredentialMessage = "密钥已使用 Windows 账户保护保存。"; ConnectionState = OnlineState.Idle; ConnectionMessage = "密钥已更改，请测试连接。"; }));
    public Task ClearCredentialAsync() => Track(() => Settings(async () =>
    {
        Leave(); catalog.ClearCache(); await credentials.ClearAsync(lifetime.Token);
        HasCredential = false; CredentialMessage = "隔离开发密钥已移除。";
        ConnectionState = OnlineState.Idle; ConnectionMessage = "尚未保存密钥，无法测试连接。";
        Details = null; DetailCoverKey = null; DetailState = OnlineState.Idle; DetailMessage = "选择作品查看详情。";
        Items = []; cursor = null; usedCursors.Clear(); SearchState = OnlineState.Idle; SearchMessage = "输入作品名后搜索。";
    }));
    public Task TestConnectionAsync() => Track(() => Settings(async () =>
    { using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); connectionStop = stop; try { ConnectionState = OnlineState.Loading; ConnectionMessage = "正在验证目录访问权限…"; Notify(); await catalog.TestConnectionAsync(stop.Token); ConnectionState = OnlineState.Loaded; ConnectionMessage = "连接成功：目录接口有效，密钥已获读取授权。"; } finally { connectionStop = null; } }));
    private async Task Settings(Func<Task> action)
    {
        if (settingsBusy) return; settingsBusy = true; Notify();
        try { await action(); }
        catch (OperationCanceledException) { ConnectionState = OnlineState.Idle; ConnectionMessage = "操作已取消。"; }
        catch (OnlineException e) { ConnectionState = State(e); ConnectionMessage = e.Message; }
        catch (Exception) { ConnectionState = OnlineState.Error; ConnectionMessage = "设置操作未完成，请重试。"; }
        finally { settingsBusy = false; Notify(); }
    }
    private static OnlineState State(OnlineException e) => e.Failure switch { OnlineFailure.Offline => OnlineState.Offline,
        OnlineFailure.NoCredential or OnlineFailure.InvalidCredential or OnlineFailure.Unauthorized or OnlineFailure.Forbidden => OnlineState.Unauthorized,
        OnlineFailure.RateLimited => OnlineState.RateLimited, _ => OnlineState.Error };
    private void Notify() { foreach (var property in new[] { nameof(Items), nameof(SearchState), nameof(SearchMessage), nameof(SearchBusy), nameof(CanLoadMore), nameof(Details), nameof(DetailCoverKey), nameof(DetailState), nameof(DetailMessage), nameof(DetailTitle), nameof(DetailDescription), nameof(DetailFacts), nameof(DetailBusy), nameof(CredentialMessage), nameof(HasCredential), nameof(ConnectionMessage), nameof(ConnectionState), nameof(SettingsBusy) }) Changed(property); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; Leave(); lifetime.Cancel(); workspace.CancelPendingOperation();
        Task[] tasks; lock (pending) tasks = pending.ToArray();
        await Task.WhenAll(tasks); searchStop?.Dispose(); detailStop?.Dispose(); lifetime.Dispose();
    }
}
