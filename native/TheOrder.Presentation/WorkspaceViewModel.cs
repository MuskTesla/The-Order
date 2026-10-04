using System.Collections.Immutable;
using System.ComponentModel;
using TheOrder.Application;

namespace TheOrder.Presentation;

public enum WorkspacePage { Library, Details, Settings }
public enum UnsavedChoice { Stay, Save, Discard }

// No JSON, file paths used for access, network, native API or repository implementation here.
// Await continuations return to the caller's UI context; Application owns background work.
public sealed class WorkspaceViewModel(IOfflineLibraryClient client, IUiPreferencesStore preferencesStore, NativeConfiguration configuration, IClientTextRules textRules) : ObservableState, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? searchCancellation, detailsCancellation;
    private long searchGeneration, detailsGeneration;
    private Task<ClientResult>? saving;
    private Task preferenceSaves = Task.CompletedTask;
    private bool loading, searching, detailsLoading, preferencesWritable = true, disposed;
    private ImmutableArray<GameSummary> items = [];
    private IReadOnlyList<GameSummary> itemView = Array.Empty<GameSummary>();
    private ImmutableArray<string> categories = [], tags = [];
    private PersonalDraft? draft;
    private ClientResult notice = new(ClientOutcome.Success, "");
    private ClientPreferences preferences = new();
    private LibraryQuery query = new();
    private WorkspacePage page;
    private string? selectedId;
    private int total;
    public IReadOnlyList<GameSummary> Items => itemView;
    public IReadOnlyList<string> Categories => categories;
    public IReadOnlyList<string> Tags => tags;
    public ClientPreferences Preferences => preferences;
    public IReadOnlyList<SavedFilter> SavedFilters => preferences.Filters;
    public LibraryQuery Query => query;
    public string Layout => preferences.Layout;
    public NativeTheme Theme => preferences.Theme;
    public WorkspacePage Page => page;
    public string? SelectedId => selectedId;
    public PersonalDraft? Draft => draft;
    public bool IsDirty => draft?.IsDirty == true;
    public bool IsLoading => loading;
    public bool IsSearching => searching;
    public bool IsDetailsLoading => detailsLoading;
    public bool IsSaving { get; private set; }
    public bool CanEdit => client.Capability.CanEdit && !loading && !IsSaving && !disposed;
    public bool CanSave => CanEdit && IsDirty;
    public bool CanReload => !loading && !IsSaving;
    public bool IsReadOnly => client.Capability.Loaded && !client.Capability.CanEdit;
    public string CapabilityMessage => client.Capability.Message;
    public bool IsLoaded => client.Capability.Loaded;
    public bool IsEmpty => IsLoaded && !loading && !searching && items.IsEmpty;
    public string EmptyText => total == 0 ? "书架还没有作品。可使用开发脚本生成隔离资料。" : "没有符合当前条件的作品。试试清除筛选。";
    public string CountText => IsLoaded ? $"{items.Length:N0} / {total:N0} 部作品" : "尚未加载书架";
    public ClientResult Notice => notice;
    public bool HasNotice => notice.Message.Length != 0;
    public string DataDirectory => configuration.DataDirectory ?? "未指定；不会访问正式书架";
    public double LastQueryMilliseconds { get; private set; }
    public event Action? ThemeChanged;

    private void StateChanged()
    {
        foreach (var name in new[] { nameof(CanEdit), nameof(CanSave), nameof(CanReload), nameof(IsReadOnly), nameof(IsLoaded), nameof(CapabilityMessage), nameof(IsEmpty), nameof(EmptyText), nameof(CountText), nameof(IsDirty) }) Changed(name);
    }
    private void Report(ClientResult result) { notice = result; Changed(nameof(Notice)); Changed(nameof(HasNotice)); }
    public void ReportUnexpected() => Report(new(ClientOutcome.LoadFailure, "操作未能完成。当前草稿已保留，请检查开发日志或重新读取。"));
    public async Task InitializeAsync()
    {
        await ReloadAsync();
        if (!IsLoaded || disposed) return;
        try { preferences = await preferencesStore.LoadAsync(lifetime.Token); preferencesWritable = true; }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or DataIsolationException or ArgumentException)
        { preferencesWritable = false; Report(new(ClientOutcome.ValidationFailure, "界面偏好无法读取，旧文件未覆盖。外观更改仅本次有效。")); }
        if (disposed) return;
        Changed(nameof(Preferences)); Changed(nameof(SavedFilters)); Changed(nameof(Layout)); Changed(nameof(Theme)); ThemeChanged?.Invoke();
        await SetQueryAsync(query with { Sort = preferences.Sort });
    }
    public async Task<bool> ReloadAsync()
    {
        if (IsDirty || IsSaving || loading || disposed) return false;
        loading = true; Changed(nameof(IsLoading)); StateChanged();
        searchCancellation?.Cancel(); detailsCancellation?.Cancel(); ++searchGeneration; ++detailsGeneration;
        try
        {
            var result = await client.LoadAsync(lifetime.Token);
            if (disposed) return false;
            Report(result); SetDraft(null);
            if (!client.Capability.Loaded) { items = []; itemView = Array.Empty<GameSummary>(); Changed(nameof(Items)); total = 0; return false; }
            await SearchAsync();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { loading = false; Changed(nameof(IsLoading)); StateChanged(); }
    }
    public async Task SetQueryAsync(LibraryQuery value)
    {
        query = value; Changed(nameof(Query));
        if (preferences.Sort != value.Sort)
        {
            preferences = preferences with { Sort = value.Sort };
            _ = QueuePreferencesSave();
        }
        await SearchAsync();
    }
    public async Task SearchAsync()
    {
        if (!client.Capability.Loaded || disposed) return;
        searchCancellation?.Cancel(); searchCancellation?.Dispose();
        searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = searchCancellation.Token; var generation = ++searchGeneration; var requested = query;
        searching = true; Changed(nameof(IsSearching)); StateChanged();
        try
        {
            await Task.Delay(120, cancellation);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = await client.QueryAsync(requested, cancellation);
            if (disposed || generation != searchGeneration || cancellation.IsCancellationRequested) return;
            LastQueryMilliseconds = timer.Elapsed.TotalMilliseconds;
            items = result.Items; itemView = items; total = result.Total; categories = result.Categories; tags = result.Tags;
            Changed(nameof(Items)); Changed(nameof(Categories)); Changed(nameof(Tags)); Changed(nameof(LastQueryMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (generation == searchGeneration && !disposed) ReportUnexpected(); }
        finally { if (generation == searchGeneration) { searching = false; Changed(nameof(IsSearching)); StateChanged(); } }
    }
    public async Task<bool> ResolveUnsavedAsync(Func<Task<UnsavedChoice>> choose)
    {
        if (saving is { IsCompleted: false }) await saving;
        if (!IsDirty) return true;
        return await choose() switch { UnsavedChoice.Discard => Discard(), UnsavedChoice.Save => (await SaveAsync()).IsCommitted, _ => false };
    }
    private bool Discard() { CancelDraft(); return true; }
    public void CancelDraft() { if (!IsSaving && draft is not null) SetDraft(new(draft.Source)); }
    public async Task<bool> NavigateAsync(WorkspacePage destination, string? id, Func<Task<UnsavedChoice>> choose)
    {
        if (disposed || !await ResolveUnsavedAsync(choose)) return false;
        detailsCancellation?.Cancel(); detailsCancellation?.Dispose();
        detailsCancellation = null;
        var generation = ++detailsGeneration;
        if (destination != WorkspacePage.Details)
        {
            page = destination; Changed(nameof(Page)); return true;
        }
        if (id is null || !IsLoaded) return false;
        detailsCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellation = detailsCancellation.Token;
        detailsLoading = true; Changed(nameof(IsDetailsLoading));
        try
        {
            var details = await client.DetailsAsync(id, cancellation);
            if (disposed || generation != detailsGeneration || cancellation.IsCancellationRequested) return false;
            selectedId = id; SetDraft(new(details)); page = WorkspacePage.Details;
            Changed(nameof(SelectedId)); Changed(nameof(Page)); return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) { if (generation == detailsGeneration && !disposed) ReportUnexpected(); return false; }
        finally { if (generation == detailsGeneration) { detailsLoading = false; Changed(nameof(IsDetailsLoading)); } }
    }
    private void SetDraft(PersonalDraft? value)
    {
        if (draft is not null) draft.PropertyChanged -= DraftChanged;
        draft = value;
        if (draft is not null) draft.PropertyChanged += DraftChanged;
        Changed(nameof(Draft)); StateChanged();
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(PersonalDraft.IsDirty)) StateChanged(); }
    public Task<ClientResult> SaveAsync() => saving is { IsCompleted: false } ? saving : saving = SaveCoreAsync();
    private async Task<ClientResult> SaveCoreAsync()
    {
        if (!CanSave || draft is null) return new(ClientOutcome.ReadOnly, IsReadOnly ? CapabilityMessage : "没有可保存的修改。");
        var captured = draft;
        IsSaving = true; Changed(nameof(IsSaving)); StateChanged();
        try
        {
            var result = await client.SaveAsync(captured.Source, captured.Input, lifetime.Token);
            if (disposed) return result;
            Report(result);
            if (result.IsCommitted)
            {
                // A refresh failure after commit must never turn this into a retryable save failure.
                try { SetDraft(new(await client.DetailsAsync(captured.Source.Id, lifetime.Token))); await SearchAsync(); }
                catch (OperationCanceledException) { }
                catch (Exception) { SetDraft(null); Report(new(ClientOutcome.CommittedWithWarning, "修改已经保存，但刷新显示失败。请重新读取，不要重复保存。")); }
            }
            return result;
        }
        catch (OperationCanceledException) { var result = new ClientResult(ClientOutcome.Cancelled, "提交前已取消，草稿已保留。"); Report(result); return result; }
        catch (Exception) { ReportUnexpected(); return notice; }
        finally { IsSaving = false; Changed(nameof(IsSaving)); StateChanged(); }
    }
    public Task<ClientResult> BatchAsync(ImmutableArray<string> ids, BulkPersonalPatch patch) =>
        saving is { IsCompleted: false } ? saving : saving = BatchCoreAsync(ids, patch);
    private async Task<ClientResult> BatchCoreAsync(ImmutableArray<string> ids, BulkPersonalPatch patch)
    {
        if (!CanEdit || IsDirty) return new(ClientOutcome.ReadOnly, "当前不能批量编辑，请先处理详情草稿。");
        IsSaving = true; Changed(nameof(IsSaving)); StateChanged();
        try { var result = await client.BatchAsync(ids, patch, lifetime.Token); if (!disposed) { Report(result); if (result.IsCommitted) await SearchAsync(); } return result; }
        catch (OperationCanceledException) { var result = new ClientResult(ClientOutcome.Cancelled, "批量修改在提交前已取消。"); Report(result); return result; }
        catch (Exception) { ReportUnexpected(); return notice; }
        finally { IsSaving = false; Changed(nameof(IsSaving)); StateChanged(); }
    }
    public async Task ApplyBatchAsync(ImmutableArray<string> ids, string status, string favorite, string categoriesText, string tagsText, bool remove)
    {
        var labels = await Task.Run(() => (Categories: textRules.SplitLabels(categoriesText), Tags: textRules.SplitLabels(tagsText)), lifetime.Token);
        await BatchAsync(ids, new()
        {
            Status = new(status != "all", status), Favorite = new(favorite != "keep", favorite == "yes"),
            AddCategories = remove ? [] : labels.Categories, RemoveCategories = remove ? labels.Categories : [],
            AddTags = remove ? [] : labels.Tags, RemoveTags = remove ? labels.Tags : []
        });
    }
    public async Task ChangeAppearanceAsync(NativeTheme theme, string layout)
    {
        preferences = preferences with { Theme = theme, Layout = layout };
        Changed(nameof(Theme)); Changed(nameof(Layout)); Changed(nameof(Preferences)); ThemeChanged?.Invoke();
        await QueuePreferencesSave();
    }
    public async Task SaveFilterAsync(string name)
    {
        var next = preferences with { SavedFilters = preferences.Filters.Add(new(Guid.NewGuid().ToString("N"), name.Trim(), query)) };
        try { PreferenceValidation.Validate(next); preferences = next; Changed(nameof(SavedFilters)); await QueuePreferencesSave(); }
        catch (InvalidDataException e) { Report(new(ClientOutcome.ValidationFailure, e.Message)); }
    }
    public async Task RemoveFilterAsync(string id)
    { preferences = preferences with { SavedFilters = [.. preferences.Filters.Where(f => f.Id != id)] }; Changed(nameof(SavedFilters)); await QueuePreferencesSave(); }
    private Task QueuePreferencesSave()
    {
        if (!preferencesWritable || disposed || !IsLoaded) return Task.CompletedTask;
        var captured = preferences;
        return preferenceSaves = PersistAfterAsync(preferenceSaves, captured);
    }
    private async Task PersistAfterAsync(Task previous, ClientPreferences captured)
    { await previous; await PersistAsync(captured); }
    private async Task PersistAsync(ClientPreferences captured)
    {
        try { await preferencesStore.SaveAsync(captured, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!disposed) Report(new(ClientOutcome.IoFailure, "界面偏好未能保存；书架数据不受影响。")); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        searchCancellation?.Cancel(); detailsCancellation?.Cancel();
        if (saving is not null) await saving;
        await preferenceSaves;
        lifetime.Cancel();
        if (draft is not null) draft.PropertyChanged -= DraftChanged;
        searchCancellation?.Dispose(); detailsCancellation?.Dispose(); lifetime.Dispose();
    }
}
