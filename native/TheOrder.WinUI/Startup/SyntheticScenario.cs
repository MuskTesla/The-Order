using System.Collections.Immutable;
using TheOrder.Application;

namespace TheOrder.WinUI.Startup;

// Explicit diagnostic composition only. Real isolated services still validate every file access
// and every mutation uses the production transaction kernel. No timer sleeps or dispatcher waits.
public sealed class SyntheticScenario(bool holdPreferences, bool holdImages = false)
{
    private TaskCompletionSource? details, batch, query;
    private readonly int uiThreadId = Environment.CurrentManagedThreadId;
    private readonly TaskCompletionSource? preferences = holdPreferences ? NewGate() : null;
    private TaskCompletionSource? scan;
    private readonly TaskCompletionSource? image = holdImages ? NewGate() : null;
    private bool failReload, failRefreshAfterCommit, failQuery;
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Status { get; private set; } = holdPreferences ? "preferences-held" : "idle";
    public void DelayDetails() { details = NewGate(); Status = "details-armed"; }
    public void DelayBatch() { batch = NewGate(); Status = "batch-armed"; }
    public void DelayQuery() { query = NewGate(); Status = "query-armed"; }
    public void DelayScan() { scan = NewGate(); Status = "scan-armed"; }
    public void FailNextReload() { failReload = true; Status = "reload-failure-armed"; }
    public void FailNextCommittedRefresh() { failRefreshAfterCommit = true; Status = "refresh-failure-armed"; }
    public void Release() { details?.TrySetResult(); batch?.TrySetResult(); activePreprocessing?.TrySetResult(); query?.TrySetResult(); activeQuery?.TrySetResult(); preferences?.TrySetResult(); scan?.TrySetResult(); image?.TrySetResult(); Status = "released"; }
    internal IOfflineLibraryClient Wrap(IOfflineLibraryClient inner) => new Client(inner, this);
    internal IUiPreferencesStore Wrap(IUiPreferencesStore inner) => new Preferences(inner, this);
    internal IClientTextRules Wrap(IClientTextRules inner) => new TextRules(inner, this);
    internal IOfflineFilesClient Wrap(IOfflineFilesClient inner) => new Files(inner, this);
    internal IImageSourceProvider Wrap(IImageSourceProvider inner) => new Images(inner, this);
    private sealed class Images(IImageSourceProvider inner, SyntheticScenario owner) : IImageSourceProvider
    {
        public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellation)
        { if (owner.image is { } held && !held.Task.IsCompleted) { owner.Status = "images-held"; await held.Task.WaitAsync(cancellation); } return await inner.ReadAsync(key, cancellation); }
    }
    private sealed class Files(IOfflineFilesClient inner, SyntheticScenario owner) : IOfflineFilesClient
    {
        public async Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken cancellation)
        {
            if (owner.scan is { } held)
            {
                owner.Status = "scan-held";
                try { await held.Task.WaitAsync(cancellation); }
                finally { if (ReferenceEquals(owner.scan, held)) owner.scan = null; }
            }
            return await inner.ScanAsync(root, progress, cancellation);
        }
        public Task<ClientResult> ImportAsync(ImmutableArray<ScanCandidate> candidates, CancellationToken cancellation) => inner.ImportAsync(candidates, cancellation);
        public Task<InstallationView> InstallationsAsync(string id, CancellationToken cancellation) => inner.InstallationsAsync(id, cancellation);
        public Task<ClientResult> SaveAsync(InstallationDetails details, InstallationInput input, CancellationToken cancellation) => inner.SaveAsync(details, input, cancellation);
        public Task<ClientResult> RemoveAsync(InstallationDetails details, string id, CancellationToken cancellation) => inner.RemoveAsync(details, id, cancellation);
        public Task<ClientResult> PreferAsync(InstallationDetails details, string id, CancellationToken cancellation) => inner.PreferAsync(details, id, cancellation);
        public Task<ClientResult> CoverAsync(GameDetails details, string? path, CancellationToken cancellation) => inner.CoverAsync(details, path, cancellation);
    }
    private sealed class TextRules(IClientTextRules inner, SyntheticScenario owner) : IClientTextRules
    {
        public string NormalizeSearch(string value) => inner.NormalizeSearch(value);
        public ImmutableArray<string> QueryTerms(string value) => inner.QueryTerms(value);
        public int CompareNames(string a, string b) => inner.CompareNames(a, b);
        public int CompareIds(string a, string b) => inner.CompareIds(a, b);
        public ImmutableArray<string> SplitLabels(string value)
        {
            if (Interlocked.Exchange(ref owner.batch, null) is { } held)
            {
                if (Environment.CurrentManagedThreadId == owner.uiThreadId)
                    throw new InvalidOperationException("Synthetic preprocessing gate must never block the UI thread.");
                // This synchronous interface runs inside the tracked Task.Run preprocessing.
                // Retain the release handle while held; bounded diagnostic wait, never production.
                owner.activePreprocessing = held;
                owner.Status = "batch-preprocessing-held";
                try { if (!held.Task.Wait(TimeSpan.FromMinutes(2))) throw new TimeoutException("Synthetic preprocessing gate expired."); }
                finally { owner.activePreprocessing = null; }
            }
            return inner.SplitLabels(value);
        }
    }
    private TaskCompletionSource? activePreprocessing, activeQuery;
    private sealed class Preferences(IUiPreferencesStore inner, SyntheticScenario owner) : IUiPreferencesStore
    {
        public async Task<ClientPreferences> LoadAsync(CancellationToken cancellation)
        { if (owner.preferences is { } held) await held.Task.WaitAsync(cancellation); return await inner.LoadAsync(cancellation); }
        public Task SaveAsync(ClientPreferences value, CancellationToken cancellation) => inner.SaveAsync(value, cancellation);
    }
    private sealed class Client(IOfflineLibraryClient inner, SyntheticScenario owner) : IOfflineLibraryClient
    {
        private bool loadFailed;
        public LibraryCapability Capability => loadFailed ? new(false, false, "合成验收：受控读取失败") : inner.Capability;
        public async Task<ClientResult> LoadAsync(CancellationToken cancellation)
        {
            if (owner.failReload) { owner.failReload = false; loadFailed = true; owner.Status = "reload-failed"; return new(ClientOutcome.LoadFailure, "合成验收：受控读取失败，原文件未修改。"); }
            loadFailed = false; return await inner.LoadAsync(cancellation);
        }
        public async Task<LibraryQueryResult> QueryAsync(LibraryQuery query, CancellationToken cancellation)
        {
            if (owner.failQuery) { owner.failQuery = false; owner.Status = "refresh-failed"; throw new IOException("Synthetic refresh fault"); }
            if (Interlocked.Exchange(ref owner.query, null) is { } held)
            {
                owner.activeQuery = held;
                owner.Status = "query-held";
                try { await held.Task.WaitAsync(cancellation); }
                finally { owner.activeQuery = null; }
            }
            return await inner.QueryAsync(query, cancellation);
        }
        public async Task<GameDetails> DetailsAsync(string id, CancellationToken cancellation)
        {
            // Capture before delaying to exercise an actual stale projection completion.
            var value = await inner.DetailsAsync(id, cancellation);
            if (owner.details is { } held) { owner.Status = "details-held"; await held.Task.WaitAsync(cancellation); if (ReferenceEquals(owner.details, held)) owner.details = null; }
            return value;
        }
        public async Task<ClientResult> SaveAsync(GameDetails details, PersonalInput input, CancellationToken cancellation)
        { var result = await inner.SaveAsync(details, input, cancellation); Committed(result); return result; }
        public async Task<ClientResult> BatchAsync(ImmutableArray<string> ids, BulkPersonalPatch patch, CancellationToken cancellation)
        {
            var result = await inner.BatchAsync(ids, patch, cancellation); Committed(result); return result;
        }
        private void Committed(ClientResult result)
        { if (result.IsCommitted && owner.failRefreshAfterCommit) { owner.failRefreshAfterCommit = false; owner.failQuery = true; } }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
