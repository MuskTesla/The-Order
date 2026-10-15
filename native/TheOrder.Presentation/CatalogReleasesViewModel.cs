using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Presentation;

public enum CatalogReleasesState { Idle, Ready, Loading, Cancelled, Failed }

public sealed class CatalogReleasesViewModel(ICatalogReleaseReader reader, Func<long>? authGeneration = null) : ObservableState, IDisposable
{
    public const int MaximumRows = 500;
    private CancellationTokenSource? request;
    private readonly HashSet<string> seenCursors = new(StringComparer.Ordinal);
    private long version, sourceAuth;
    private bool disposed;
    public string? WorkId { get; private set; }
    public CatalogReleaseEdition[] Items { get; private set; } = [];
    public CatalogReleaseEdition[] VisibleItems { get; private set; } = [];
    public string? KindFilter { get; private set; }
    public string? LanguageFilter { get; private set; }
    public string? PlatformFilter { get; private set; }
    public bool HasFilters => KindFilter is not null || LanguageFilter is not null || PlatformFilter is not null;
    public string[] Kinds => Items.Select(row => row.Kind).Distinct(StringComparer.Ordinal).OrderBy(CatalogReleaseFacts.KindOrder).ThenBy(value => value, StringComparer.Ordinal).ToArray();
    public string[] Languages => Items.SelectMany(row => row.Languages).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    public string[] Platforms => Items.SelectMany(row => row.Platforms).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    public string? NextCursor { get; private set; }
    public DateTimeOffset? FetchedAtUtc { get; private set; }
    public bool Expanded { get; private set; }
    public bool Loaded { get; private set; }
    public bool IsBusy => request is not null;
    public bool CanLoad => !disposed && WorkId is not null && Expanded && !IsBusy;
    public bool CanNext => CanLoad && NextCursor is not null && Items.Length < MaximumRows;
    public string Message { get; private set; } = "";
    public CatalogReleasesState State { get; private set; }
    public long Version => version;
    private long Auth => authGeneration?.Invoke() ?? 0;
    public void SetSource(string? workId)
    {
        if (disposed) return;
        workId = CatalogReleaseFacts.Id(workId) ? workId : null;
        if (WorkId == workId && sourceAuth == Auth) return;
        Stop(); WorkId = workId; sourceAuth = Auth; Expanded = false; Reset();
        State = workId is null ? CatalogReleasesState.Idle : CatalogReleasesState.Ready; Notify();
    }
    // These filters describe only the pages already loaded. They never change remote
    // queries, recognition evidence or local installation identities.
    public void SetFilters(string? kind, string? language, string? platform)
    {
        if (disposed) return;
        kind = Kinds.Contains(kind, StringComparer.Ordinal) ? kind : null;
        language = Languages.Contains(language, StringComparer.Ordinal) ? language : null;
        platform = Platforms.Contains(platform, StringComparer.Ordinal) ? platform : null;
        if (KindFilter == kind && LanguageFilter == language && PlatformFilter == platform) return;
        KindFilter = kind; LanguageFilter = language; PlatformFilter = platform; ApplyFilters(); Notify();
    }
    public void ClearFilters() => SetFilters(null, null, null);
    private void ApplyFilters()
    {
        if (!Kinds.Contains(KindFilter, StringComparer.Ordinal)) KindFilter = null;
        if (!Languages.Contains(LanguageFilter, StringComparer.Ordinal)) LanguageFilter = null;
        if (!Platforms.Contains(PlatformFilter, StringComparer.Ordinal)) PlatformFilter = null;
        VisibleItems = Items.Where(row => (KindFilter is null || row.Kind == KindFilter)
            && (LanguageFilter is null || row.Languages.Contains(LanguageFilter, StringComparer.Ordinal))
            && (PlatformFilter is null || row.Platforms.Contains(PlatformFilter, StringComparer.Ordinal)))
            .OrderBy(row => CatalogReleaseFacts.KindOrder(row.Kind)).ToArray();
    }
    public Task ExpandAsync()
    {
        if (disposed || WorkId is null) return Task.CompletedTask;
        CheckAuth(); Expanded = true; Notify();
        return Loaded || IsBusy ? Task.CompletedTask : LoadAsync(null, false, false);
    }
    public void Collapse()
    {
        if (!Expanded) return;
        Expanded = false; Cancel();
    }
    public Task RefreshAsync() { CheckAuth(); return CanLoad ? LoadAsync(null, false, true) : Task.CompletedTask; }
    public Task NextAsync() { CheckAuth(); return CanNext ? LoadAsync(NextCursor, true, false) : Task.CompletedTask; }
    private void CheckAuth()
    {
        if (sourceAuth == Auth) return;
        Stop(); sourceAuth = Auth; Reset(); State = CatalogReleasesState.Ready; Notify();
    }
    private async Task LoadAsync(string? cursor, bool append, bool refresh)
    {
        if (!CanLoad || WorkId is not { } id) return;
        var stop = new CancellationTokenSource(); request = stop; var current = ++version; var auth = Auth;
        bool Current() => !disposed && Expanded && WorkId == id && version == current && auth == Auth && !stop.IsCancellationRequested;
        State = CatalogReleasesState.Loading; Message = append ? "读取下一页发行资料…" : "读取发行资料…"; Notify();
        try
        {
            var page = await reader.LoadReleasesAsync(id, cursor, refresh, stop.Token);
            if (!Current()) return;
            if (page.WorkId != id || page.Items is null || page.Items.Length > 100 || !CatalogReleaseFacts.Cursor(page.NextCursor)
                || page.NextCursor is not null && (page.NextCursor == cursor || append && seenCursors.Contains(page.NextCursor))
                || page.Items.Any(row => !CatalogReleaseFacts.Valid(row, id))
                || page.Items.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != page.Items.Length)
                throw new InvalidDataException();
            Dictionary<string, CatalogReleaseEdition> existing = append ? Items.ToDictionary(row => row.Id, StringComparer.Ordinal) : [];
            foreach (var row in page.Items)
            {
                if (existing.TryGetValue(row.Id, out var prior) && JsonSerializer.Serialize(prior) != JsonSerializer.Serialize(row)) throw new InvalidDataException();
                existing.TryAdd(row.Id, row);
            }
            if (existing.Count > MaximumRows) throw new InvalidDataException();
            Items = existing.Values.ToArray(); NextCursor = page.NextCursor; FetchedAtUtc = page.FetchedAtUtc; Loaded = true;
            ApplyFilters();
            if (!append) seenCursors.Clear();
            if (cursor is not null) seenCursors.Add(cursor);
            if (page.NextCursor is not null) seenCursors.Add(page.NextCursor);
            State = CatalogReleasesState.Ready;
            Message = Items.Length == 0 ? "本次未收录发行记录。"
                : NextCursor is null ? $"已载入 {Items.Length} 条发行记录。"
                : Items.Length >= MaximumRows ? $"已载入 {Items.Length} 条，达到本页展示上限；原站仍有后续资料。"
                : $"已载入 {Items.Length} 条，可继续读取下一页。";
        }
        catch (OperationCanceledException)
        {
            if (Current()) { State = CatalogReleasesState.Cancelled; Message = "读取已取消，可重新读取。"; }
        }
        catch (Exception error)
        {
            if (Current())
            {
                var transient = error is OnlineException { Failure: OnlineFailure.Offline or OnlineFailure.Timeout or OnlineFailure.RateLimited or OnlineFailure.Server };
                if (!transient) Reset();
                State = CatalogReleasesState.Failed;
                Message = (transient && Items.Length > 0 ? "暂时无法更新，保留之前读取的发行资料。" : "")
                    + (error is OnlineException online ? online.Message : "发行资料未能读取，请重试。");
            }
        }
        finally
        {
            if (ReferenceEquals(request, stop))
            {
                request = null;
                if (auth != Auth) { sourceAuth = Auth; Reset(); State = CatalogReleasesState.Cancelled; Message = "目录身份已变化，请重新读取。"; }
                Notify();
            }
            stop.Dispose();
        }
    }
    public void Cancel()
    {
        if (request is not null) { Stop(); State = CatalogReleasesState.Cancelled; Message = "读取已取消，可重新读取。"; }
        Notify();
    }
    public void Deactivate() { Stop(); WorkId = null; Expanded = false; Reset(); State = CatalogReleasesState.Idle; Notify(); }
    private void Reset() { Items = []; VisibleItems = []; KindFilter = LanguageFilter = PlatformFilter = null; NextCursor = null; FetchedAtUtc = null; Loaded = false; Message = ""; seenCursors.Clear(); }
    private void Stop() { version++; var prior = request; request = null; prior?.Cancel(); }
    private void Notify() => Changed(string.Empty);
    public void Dispose() { if (disposed) return; Deactivate(); disposed = true; }
}
