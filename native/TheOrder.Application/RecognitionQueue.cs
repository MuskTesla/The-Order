using TheOrder.Application;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace TheOrder.Application;

public sealed record RecognitionJob(Guid Id, string GameId, string Title, RecognitionMode Mode, RecognitionJobState State,
    DateTimeOffset CreatedAt, RecognitionResult? Result = null, string? Message = null);
public sealed record RecognitionProgress(int Total, int Queued, int Processing, int NeedsReview, int Completed, int Failed, int Cancelled, int Stale)
{
    public int Processed => NeedsReview + Completed + Failed + Cancelled + Stale;
}

// Own generation, cancellation and bounded scheduling. It can read library identity, never write it.
public sealed class RecognitionQueue : IAsyncDisposable
{
    public const int MaximumJobs = 1000;
    public const int WorkerCount = 2;
    private sealed record Work(Guid Id, RecognitionEntrySnapshot Input, bool Online, bool IncludePe, CancellationTokenSource Cancellation);
    private readonly IRecognitionLibrarySource library;
    private readonly IRecognitionEngine engine;
    private readonly OnlineAuthContext? auth;
    private readonly object gate = new();
    private readonly Dictionary<Guid, RecognitionJob> jobs = [];
    private readonly HashSet<Guid> settled = [];
    private readonly Dictionary<string, Work> current = new(StringComparer.Ordinal);
    private readonly Channel<Work> channel = Channel.CreateBounded<Work>(new BoundedChannelOptions(MaximumJobs) { SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task[] workers;
    private bool closed;
    private TaskCompletionSource idle = Completed();
    private int pending;
    public bool ObserverFailed { get; private set; }
    public event Action? Changed;
    public RecognitionQueue(IRecognitionLibrarySource library, IRecognitionEngine engine, OnlineAuthContext? auth = null)
    { this.library = library; this.engine = engine; this.auth = auth; workers = Enumerable.Range(0, WorkerCount).Select(_ => Task.Run(Worker)).ToArray(); }
    private static TaskCompletionSource Completed() { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }
    public ImmutableArray<RecognitionJob> Snapshot { get { lock (gate) return [.. jobs.Values.OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)]; } }
    public RecognitionProgress Progress
    {
        get
        {
            var all = Snapshot; int Count(RecognitionJobState state) => all.Count(j => j.State == state);
            return new(all.Length, Count(RecognitionJobState.Queued), all.Count(j => j.State is RecognitionJobState.CollectingEvidence or RecognitionJobState.Matching or RecognitionJobState.Enriching),
                Count(RecognitionJobState.NeedsReview), Count(RecognitionJobState.Completed), Count(RecognitionJobState.Failed), Count(RecognitionJobState.Cancelled), Count(RecognitionJobState.Stale));
        }
    }
    public Task WhenIdleAsync(CancellationToken cancellation = default) { lock (gate) return idle.Task.WaitAsync(cancellation); }
    public async Task<Guid> EnqueueAsync(string gameId, RecognitionMode mode, bool online, bool includePe = false, CancellationToken cancellation = default)
    {
        var id = Guid.NewGuid(); CancellationTokenSource lifetime;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (pending >= MaximumJobs) throw new InvalidOperationException(UiText.Get("识别队列已满，请等待当前批次完成。"));
            // Retain bounded completed history, never evict active work or silently discard valid reviews.
            if (jobs.Count >= MaximumJobs)
            {
                var retired = jobs.Values.FirstOrDefault(j => settled.Contains(j.Id) && j.State is RecognitionJobState.Completed or RecognitionJobState.Cancelled or RecognitionJobState.Failed or RecognitionJobState.Stale);
                if (retired is null) throw new InvalidOperationException(UiText.Get("待核对队列已满，请先处理已有结果。"));
                jobs.Remove(retired.Id);
                settled.Remove(retired.Id);
            }
            InvalidateLocked(gameId, UiText.Get("已重新识别，旧结果失效。"));
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, cancellation);
            jobs[id] = new(id, gameId, gameId, mode, RecognitionJobState.Queued, DateTimeOffset.UtcNow);
            // Reserve the latest generation before any await, so older capture completion cannot win.
            current[gameId] = new(id, null!, online, includePe, lifetime);
            if (pending++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Notify();
        try
        {
            var input = (await library.CaptureRecognitionAsync(gameId, lifetime.Token).ConfigureAwait(false)) with { Mode = mode };
            if (input.Entry.Installations.IsEmpty) throw new InvalidDataException(UiText.Get("愿望条目没有本地安装，不参与本地识别。"));
            var work = new Work(id, input, online, includePe, lifetime);
            lock (gate)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!IsCurrentLocked(work)) throw new OperationCanceledException(lifetime.Token);
                current[gameId] = work; jobs[id] = jobs[id] with { Title = input.Entry.Name };
            }
            await channel.Writer.WriteAsync(work, lifetime.Token).ConfigureAwait(false);
            return id;
        }
        catch (Exception e)
        {
            lock (gate)
            {
                if (jobs.TryGetValue(id, out var job) && job.State == RecognitionJobState.Queued)
                    jobs[id] = job with { State = e is OperationCanceledException ? RecognitionJobState.Cancelled : RecognitionJobState.Failed, Message = SafeMessage(e) };
                SettleLocked(id, gameId, lifetime);
            }
            Notify();
            lock (gate) CompletePendingLocked();
            if (e is OperationCanceledException) return id;
            throw;
        }
    }
    private static string SafeMessage(Exception e) => e switch { InvalidDataException or InvalidOperationException => e.Message, OperationCanceledException => UiText.Get("识别已取消。"), _ => UiText.Get("识别未完成，请重试。") };
    private bool IsCurrentLocked(Work work) => current.TryGetValue(work.Input?.Entry.Id ?? jobs[work.Id].GameId, out var currentWork) && currentWork.Id == work.Id;
    private void InvalidateLocked(string gameId, string reason)
    {
        if (!current.TryGetValue(gameId, out var older)) return;
        older.Cancellation.Cancel();
        if (jobs.TryGetValue(older.Id, out var job)) jobs[older.Id] = job with { State = RecognitionJobState.Stale, Result = null, Message = reason };
        if (settled.Contains(older.Id)) { current.Remove(gameId); older.Cancellation.Dispose(); }
    }
    public void Cancel(Guid id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job)) return;
            if (current.TryGetValue(job.GameId, out var work) && work.Id == id) work.Cancellation.Cancel();
            jobs[id] = job with { State = RecognitionJobState.Cancelled, Result = null, Message = UiText.Get("识别已取消。") };
            if (settled.Contains(id) && current.TryGetValue(job.GameId, out var finished) && finished.Id == id) { current.Remove(job.GameId); finished.Cancellation.Dispose(); }
        }
        Notify();
    }
    public void CancelAll()
    {
        lock (gate)
        {
            foreach (var work in current.Values.ToArray())
            {
                if (!jobs.TryGetValue(work.Id, out var job) || job.State is RecognitionJobState.Completed or RecognitionJobState.Failed or RecognitionJobState.Cancelled or RecognitionJobState.Stale) continue;
                work.Cancellation.Cancel(); jobs[work.Id] = job with { State = RecognitionJobState.Cancelled, Result = null, Message = UiText.Get("识别已取消。") };
                if (settled.Contains(work.Id)) { current.Remove(job.GameId); work.Cancellation.Dispose(); }
            }
        }
        Notify();
    }
    public RecognitionResult RequireCurrentResult(Guid id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || job.State != RecognitionJobState.NeedsReview || job.Result is null
                || !current.TryGetValue(job.GameId, out var work) || work.Id != id || work.Cancellation.IsCancellationRequested
                || job.Result.AuthGeneration is { } generation && auth is not null && auth.Generation != generation) throw new StaleLibraryRequestException();
            return job.Result;
        }
    }
    public CancellationToken DecisionCancellation(Guid id)
    {
        lock (gate) { _ = RequireCurrentResult(id); return current[jobs[id].GameId].Cancellation.Token; }
    }
    public void MarkResolved(Guid id, string message)
    {
        lock (gate) { if (jobs.TryGetValue(id, out var job)) { jobs[id] = job with { State = RecognitionJobState.Completed, Result = null, Message = message }; if (settled.Contains(id) && current.TryGetValue(job.GameId, out var work) && work.Id == id) { current.Remove(job.GameId); work.Cancellation.Dispose(); } } }
        Notify();
    }
    public void RejectCapacity(Guid id)
    {
        MarkStale(id, new ReviewCapacityException().Message);
    }
    public void InvalidateEnrichment(Guid id)
    {
        lock (gate)
        {
            if (jobs.TryGetValue(id, out var job) && job.State == RecognitionJobState.NeedsReview && job.Result is { } result)
                jobs[id] = job with { Result = result.WithoutEnrichment(), Message = UiText.Get("在线补充已失效，本地结果保留。") };
        }
        Notify();
    }
    public void MarkStale(Guid id, string message)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || job.State != RecognitionJobState.NeedsReview) return;
            jobs[id] = job with { State = RecognitionJobState.Stale, Result = null, Message = message };
            if (current.TryGetValue(job.GameId, out var work) && work.Id == id)
            {
                work.Cancellation.Cancel();
                if (settled.Contains(id)) { current.Remove(job.GameId); work.Cancellation.Dispose(); }
            }
        }
        Notify();
    }
    private void Update(Work work, RecognitionJobState state)
    {
        lock (gate)
        {
            work.Cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentLocked(work)) throw new OperationCanceledException(work.Cancellation.Token);
            jobs[work.Id] = jobs[work.Id] with { State = state };
        }
        Notify();
    }
    private async Task Worker()
    {
        await foreach (var work in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                work.Cancellation.Token.ThrowIfCancellationRequested();
                if (!await library.IsRecognitionCurrentAsync(work.Input, work.Cancellation.Token).ConfigureAwait(false)) throw new StaleLibraryRequestException();
                var result = await engine.RecognizeAsync(work.Input, work.Id, work.Online, work.IncludePe, s => Update(work, s), work.Cancellation.Token).ConfigureAwait(false);
                work.Cancellation.Token.ThrowIfCancellationRequested();
                if (!await library.IsRecognitionCurrentAsync(work.Input, work.Cancellation.Token).ConfigureAwait(false)) throw new StaleLibraryRequestException();
                lock (gate)
                {
                    work.Cancellation.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentLocked(work) || result.Generation != work.Id || result.Input != work.Input) throw new StaleLibraryRequestException();
                    if (result.AuthGeneration is { } epoch && auth is not null && auth.Generation != epoch) result = result.WithoutEnrichment();
                    jobs[work.Id] = jobs[work.Id] with { State = RecognitionJobState.NeedsReview, Result = result };
                }
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    if (jobs.TryGetValue(work.Id, out var job) && job.State != RecognitionJobState.Stale)
                        jobs[work.Id] = job with { State = e is StaleLibraryRequestException ? RecognitionJobState.Stale : e is OperationCanceledException ? RecognitionJobState.Cancelled : RecognitionJobState.Failed, Result = null, Message = SafeMessage(e) };
                }
            }
            finally
            {
                lock (gate) SettleLocked(work.Id, work.Input.Entry.Id, work.Cancellation);
                // Idle is a publication boundary: synchronous result observers (including
                // the review store) must see the final state before callers can flush it.
                Notify();
                lock (gate) CompletePendingLocked();
            }
        }
    }
    private void CompletePendingLocked() { if (--pending == 0) idle.TrySetResult(); }
    private void SettleLocked(Guid id, string gameId, CancellationTokenSource cancellation)
    {
        settled.Add(id);
        if (!current.TryGetValue(gameId, out var latest) || latest.Id != id) { cancellation.Dispose(); return; }
        if (!jobs.TryGetValue(id, out var job) || job.State != RecognitionJobState.NeedsReview) { current.Remove(gameId); cancellation.Dispose(); }
    }
    private void Notify()
    {
        // A presentation subscriber is not allowed to terminate a worker or strand pending work.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception) { ObserverFailed = true; }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closed) return; closed = true; shutdown.Cancel(); channel.Writer.TryComplete(); }
        await Task.WhenAll(workers).ConfigureAwait(false);
        await WhenIdleAsync().ConfigureAwait(false);
        lock (gate) { foreach (var work in current.Values) work.Cancellation.Dispose(); }
        shutdown.Dispose();
    }
}
