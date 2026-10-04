using System.Collections.Immutable;
using System.Threading.Channels;
using TheOrder.Domain;

namespace TheOrder.Application;

// The explicit decision application layer owns writes. The engine and worker queue never receive it.
public sealed class RecognitionReviewService : IAsyncDisposable
{
    private readonly OfflineLibraryClient offline;
    private readonly RecognitionQueue queue;
    private readonly IRecognitionReviewStorage storage;
    private readonly IRecognitionReviewCodec codec;
    private readonly IRecognitionEvidenceReader evidence;
    private readonly IRecognitionRanker ranker;
    private readonly IRecognitionCatalog catalog;
    private readonly OnlineAuthContext auth;
    private readonly Func<LibraryEntry, string> fingerprint;
    private readonly Dictionary<Guid, RecognitionReview> reviews = [];
    private readonly object gate = new();
    private readonly SemaphoreSlim persistence = new(1, 1), decision = new(1, 1), initialization = new(1, 1);
    private readonly Channel<bool> dirty = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task pump;
    private bool loaded, storageSafe = true, disposed;
    public string? PersistenceError { get; private set; }
    public string? CapacityError { get; private set; }
    public bool Loaded => loaded;
    public event Action? Changed;
    public RecognitionReviewService(OfflineLibraryClient offline, RecognitionQueue queue, IRecognitionReviewStorage storage,
        IRecognitionReviewCodec codec, IRecognitionEvidenceReader evidence, IRecognitionRanker ranker, IRecognitionCatalog catalog,
        OnlineAuthContext auth, Func<LibraryEntry, string> fingerprint)
    {
        this.offline = offline; this.queue = queue; this.storage = storage; this.codec = codec; this.evidence = evidence;
        this.ranker = ranker; this.catalog = catalog; this.auth = auth; this.fingerprint = fingerprint;
        queue.Changed += OnQueueChanged; pump = Task.Run(PersistLoop);
    }
    public ImmutableArray<RecognitionReview> Snapshot { get { lock (gate) return [.. reviews.Values.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)]; } }
    public async Task InitializeAsync(CancellationToken cancellation)
    {
        await initialization.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (loaded) return;
            try
            {
                var restored = await Task.Run(async () => codec.Read(await storage.ReadAsync(cancellation).ConfigureAwait(false)), cancellation).ConfigureAwait(false);
                var opening = new List<RecognitionReview>();
                foreach (var review in codec.PrepareAdmission(restored))
                {
                    cancellation.ThrowIfCancellationRequested(); var value = review;
                    try
                    {
                        var current = await offline.CaptureRecognitionAsync(review.GameId, cancellation).ConfigureAwait(false);
                        var previous = review.Result?.SourceFingerprint ?? review.HistoricalFingerprint;
                        if (previous is not null && previous != fingerprint(current.Entry)) value = review with { Status = RecognitionReviewStatus.Stale, Message = "本地名称、安装或关联已变化，请重新识别。" };
                    }
                    catch (InvalidDataException) { value = review with { Status = RecognitionReviewStatus.Stale, Message = "原条目已不存在，请核对后忽略。" }; }
                    opening.Add(value);
                }
                // Lifecycle diagnostics can increase serialized size too. Publish only the final budgeted state.
                var admitted = codec.PrepareAdmission([.. opening]);
                lock (gate) foreach (var value in admitted) reviews.TryAdd(value.Id, value);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or DataIsolationException)
            { storageSafe = false; PersistenceError = "历史核对队列无法安全读取，原文件已保留；本次建议暂不持久保存。"; }
            loaded = true; OnQueueChanged();
        }
        finally { initialization.Release(); }
    }
    private void OnQueueChanged()
    {
        var rejected = new List<Guid>();
        lock (gate)
        {
            var proposed = new Dictionary<Guid, RecognitionReview>(reviews);
            foreach (var job in queue.Snapshot)
            {
                if (job.State == RecognitionJobState.NeedsReview && job.Result is { } result && (!reviews.TryGetValue(job.Id, out var prior) || prior.Status == RecognitionReviewStatus.NeedsReview))
                    proposed[job.Id] = new(job.Id, job.GameId, job.Title, job.CreatedAt, RecognitionReviewStatus.NeedsReview, result.AlgorithmVersion, result, [], Message: result.EnrichmentMessage);
                else if (job.State == RecognitionJobState.Stale && reviews.TryGetValue(job.Id, out var previous))
                    proposed[job.Id] = previous with { Status = RecognitionReviewStatus.Stale, Message = job.Message };
                else if (job.State is RecognitionJobState.Completed or RecognitionJobState.Cancelled) proposed.Remove(job.Id);
            }
            if (proposed.Count != reviews.Count || proposed.Any(p => !reviews.TryGetValue(p.Key, out var old) || old != p.Value))
            {
                try
                {
                    var admitted = codec.PrepareAdmission([.. proposed.Values]);
                    reviews.Clear(); foreach (var item in admitted) reviews.Add(item.Id, item);
                    CapacityError = null;
                }
                catch (ReviewCapacityException e)
                {
                    CapacityError = e.Message;
                    rejected.AddRange(proposed.Keys.Where(id => !reviews.ContainsKey(id)));
                }
            }
        }
        foreach (var id in rejected) queue.RejectCapacity(id);
        if (loaded && !disposed) dirty.Writer.TryWrite(true);
        Notify();
    }
    public async Task RefreshValidityAsync(CancellationToken cancellation)
    {
        foreach (var review in Snapshot.Where(r => r.Status == RecognitionReviewStatus.NeedsReview && r.Result is not null))
        {
            bool current = false;
            try { current = await offline.IsRecognitionCurrentAsync(review.Result!.Input, cancellation).ConfigureAwait(false); } catch (InvalidOperationException) { }
            if (current && review.Result!.AuthGeneration is { } epoch && epoch != auth.Generation)
            { queue.InvalidateEnrichment(review.Id); continue; }
            if (current) continue;
            const string message = "条目、请求或凭据已变化，请重新识别。";
            // OnQueueChanged stages and budgets the status/message update before publishing it.
            queue.MarkStale(review.Id, message);
        }
        if (loaded && !disposed) dirty.Writer.TryWrite(true);
        Notify();
    }
    public async Task FlushAsync(CancellationToken cancellation = default)
    {
        await persistence.WaitAsync(cancellation).ConfigureAwait(false);
        try { await SaveSnapshotAsync(Snapshot, cancellation).ConfigureAwait(false); }
        finally { persistence.Release(); }
    }
    private async Task SaveSnapshotAsync(ImmutableArray<RecognitionReview> snapshot, CancellationToken cancellation)
    {
        if (!loaded) return;
        if (!storageSafe) throw new IOException("历史队列尚未安全读取，拒绝覆盖。");
        var bytes = await Task.Run(() => codec.Write(snapshot), cancellation).ConfigureAwait(false);
        await storage.WriteAsync(bytes, cancellation).ConfigureAwait(false); PersistenceError = null;
    }
    private async Task PersistLoop()
    {
        await foreach (var notification in dirty.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Coalesce state/progress bursts. No percentage or persistence success is fabricated.
            await Task.Delay(50).ConfigureAwait(false);
            while (dirty.Reader.TryRead(out _)) { }
            try { await FlushAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or DataIsolationException)
            { if (storageSafe) PersistenceError = "核对建议未能保存，已有文件保留；请检查隔离目录后重试。"; Notify(); }
        }
    }
    private RecognitionReview Require(Guid id)
    {
        lock (gate) return reviews.TryGetValue(id, out var review) ? review : throw new StaleLibraryRequestException();
    }
    public async Task<ClientResult> IgnoreAsync(Guid id, CancellationToken cancellation)
    {
        await decision.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            _ = Require(id);
            await persistence.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                await SaveSnapshotAsync([.. Snapshot.Where(r => r.Id != id)], cancellation).ConfigureAwait(false);
                lock (gate) reviews.Remove(id); queue.MarkResolved(id, "已忽略本次建议；以后仍可重新识别。");
            }
            finally { persistence.Release(); }
            Notify(); return new(ClientOutcome.Success, "已忽略这一次核对建议。");
        }
        catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); }
        finally { decision.Release(); }
    }
    public Task<ClientResult> ApplyAsync(Guid reviewId, string candidateId, CancellationToken cancellation) => DecideAsync(reviewId, null, candidateId, cancellation);
    public Task<ClientResult> ManualLinkAsync(string gameId, string workId, CancellationToken cancellation) => DecideAsync(null, gameId, workId, cancellation);
    private async Task<ClientResult> DecideAsync(Guid? reviewId, string? manualGameId, string workId, CancellationToken cancellation)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        await decision.WaitAsync(stopping.Token).ConfigureAwait(false);
        RequestToken? transaction = null; TransactionalLibrary? library = null;
        try
        {
            if (workId.Length is < 1 or > 20 || !workId.All(c => c is >= '0' and <= '9')) throw new InvalidDataException("目录作品编号须为 1 至 20 位数字。");
            RecognitionResult? result = null;
            if (reviewId is { } id)
            {
                if (Require(id).Status != RecognitionReviewStatus.NeedsReview) throw new StaleLibraryRequestException();
                result = queue.RequireCurrentResult(id);
                var candidate = result.Candidates.FirstOrDefault(c => c.Work.Id == workId) ?? throw new StaleLibraryRequestException();
                if (candidate.Vetoed) throw new InvalidDataException(candidate.VersionNeedsConfirmation ? "版本信息不足，需要确认；不能直接应用。" : "此候选存在身份冲突，不能直接应用；请核对版本后使用手动关联。");
                if (result.SourceFingerprint != fingerprint(result.Input.Entry)) throw new StaleLibraryRequestException();
            }
            var input = result?.Input ?? await offline.CaptureRecognitionAsync(manualGameId!, stopping.Token).ConfigureAwait(false);
            var authSnapshot = await auth.CaptureAsync(stopping.Token).ConfigureAwait(false);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, authSnapshot.Changed,
                reviewId is { } rid ? queue.DecisionCancellation(rid) : default);
            var token = operation.Token;
            library = offline.Required(); transaction = await library.BeginRecognitionCommitAsync(input, token).ConfigureAwait(false);
            // The catalog verifies the exact requested/canonical identity. A legitimate remote merge
            // does not itself authorize a different local relation; this call is already explicit intent.
            var work = await catalog.DetailsAsync(workId, true, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result is not null)
            {
                var releases = await catalog.ReleasesAsync(work.Id, token).ConfigureAwait(false);
                var ranked = await Task.Run(() => ranker.Rank(result.Identity.Evidence, [work], new Dictionary<string, ImmutableArray<ReleaseIdentity>> { [work.Id] = releases }, token), token).ConfigureAwait(false);
                if (ranked.IsEmpty || ranked[0].Vetoed) throw new InvalidDataException("最新详情与本地证据存在冲突，请重新核对。");
            }
            var receipt = await library.CommitLinkAsync(input.Entry.Id, work, transaction, preserveName: true, preserveMergedName: true, cancellation: token, verifyBeforeCommit: async () =>
            {
                if (result is not null)
                {
                    _ = queue.RequireCurrentResult(reviewId!.Value);
                    var actual = await evidence.ReadAsync(input.EvidenceEntry, result.Local.IncludePe, token).ConfigureAwait(false);
                    if (actual.Fingerprint != result.Local.Fingerprint)
                    { queue.MarkStale(reviewId!.Value, "本地识别证据已变化，请重新识别。"); throw new StaleLibraryRequestException(); }
                    _ = queue.RequireCurrentResult(reviewId.Value);
                }
                auth.Accept(authSnapshot, () => { token.ThrowIfCancellationRequested(); return true; });
            }).ConfigureAwait(false);
            var presented = OfflineLibraryClient.Present(receipt);
            // The durable library result is authoritative even if review cleanup subsequently fails.
            try
            {
                if (reviewId is { } resolved) { lock (gate) reviews.Remove(resolved); queue.MarkResolved(resolved, "已保存关联。"); }
                else
                {
                    Guid[] superseded;
                    lock (gate)
                    {
                        superseded = [.. reviews.Values.Where(r => r.GameId == input.Entry.Id).Select(r => r.Id)];
                        var proposed = reviews.Values.Select(r => r.GameId == input.Entry.Id
                            ? r with { Status = RecognitionReviewStatus.Stale, Message = "已手动关联，旧建议失效。" } : r);
                        var admitted = codec.PrepareAdmission([.. proposed]);
                        reviews.Clear(); foreach (var item in admitted) reviews.Add(item.Id, item);
                    }
                    foreach (var oldId in superseded) queue.MarkStale(oldId, "已手动关联，旧建议失效。");
                }
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or DataIsolationException)
            { PersistenceError = "关联已经保存，但核对队列更新失败。"; presented = new(ClientOutcome.CommittedWithWarning, PersistenceError); }
            Notify(); return presented;
        }
        catch (OnlineException e) { return new(ClientOutcome.ValidationFailure, e.Message); }
        catch (ObjectDisposedException) { return new(ClientOutcome.Conflict, "书架已重新加载或关闭，请重新识别后决定。"); }
        catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); }
        finally
        {
            if (transaction is not null && library is not null)
                try { await library.FinishRequestAsync(transaction).ConfigureAwait(false); } catch (ObjectDisposedException) { /* Reload/shutdown already retired this epoch. */ }
            decision.Release();
        }
    }
    private void Notify() { foreach (var handler in Changed?.GetInvocationList() ?? []) try { ((Action)handler)(); } catch (Exception) { /* A failed view observer must not corrupt persisted review state. */ } }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel();
        await decision.WaitAsync().ConfigureAwait(false); decision.Release();
        queue.Changed -= OnQueueChanged; dirty.Writer.TryComplete(); await pump.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
