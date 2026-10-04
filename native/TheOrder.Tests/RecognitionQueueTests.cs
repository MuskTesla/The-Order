using System.Collections.Concurrent;
using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Recognition;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class RecognitionQueueTests
{
    private sealed class PausedEngine : IRecognitionEngine
    {
        public readonly ConcurrentDictionary<Guid, TaskCompletionSource> Started = new();
        public readonly ConcurrentDictionary<Guid, TaskCompletionSource> Release = new();
        public int Active, Maximum;
        public bool IgnoreCancellation;
        public async Task<RecognitionResult> RecognizeAsync(RecognitionEntrySnapshot input, Guid generation, bool online, bool includePe, Action<RecognitionJobState> progress, CancellationToken cancellation)
        {
            int active = Interlocked.Increment(ref Active); Maximum = Math.Max(Maximum, active);
            try
            {
                progress(RecognitionJobState.Matching);
                var release = Release.GetOrAdd(generation, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                Started.GetOrAdd(generation, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                await release.Task.WaitAsync(IgnoreCancellation ? default : cancellation);
                var evidence = Evidence(new LocalTitleEvidence(input.Entry.Name, "folder"));
                return new(generation, input, new(evidence, [], [], "local"), new(evidence, [], [], []), [], TitleMatching.AlgorithmVersion, RecognitionFingerprint.Identity(input.Entry), true, "offline", null);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public Task Wait(Guid id) => Started.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Complete(Guid id) => Release.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
    private static byte[] Document(params LibraryEntry[] entries) => new LegacyLibraryCodec().Encode([.. entries], Clock);
    [Fact] public async Task OlderRecognitionCannotReplaceNewerGenerationEvenIfItIgnoresCancellation()
    {
        using var profile = new TransactionProfile(Document(Entry(folder: @"C:\synthetic\game"))); using var library = profile.Open(); var engine = new PausedEngine { IgnoreCancellation = true };
        await using var queue = new RecognitionQueue(library, engine);
        Guid a = await queue.EnqueueAsync("a", RecognitionMode.Reidentify, false); await engine.Wait(a);
        Guid b = await queue.EnqueueAsync("a", RecognitionMode.Reidentify, false); await engine.Wait(b); engine.Complete(b);
        engine.Complete(a); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<StaleLibraryRequestException>(() => queue.RequireCurrentResult(a)); Assert.Equal(b, queue.RequireCurrentResult(b).Generation);
    }
    [Theory][InlineData("title")][InlineData("delete-recreate")][InlineData("installation")][InlineData("relation")][InlineData("new-request")]
    public async Task ChangesDuringRecognitionInvalidateResult(string change)
    {
        var entry = Entry(folder: @"C:\synthetic\game"); using var profile = new TransactionProfile(Document(entry)); using var library = profile.Open(); var engine = new PausedEngine();
        await using var queue = new RecognitionQueue(library, engine); var id = await queue.EnqueueAsync("a", RecognitionMode.Reidentify, false); await engine.Wait(id);
        switch (change)
        {
            case "title": await library.UpdateAsync("a", new() { Name = "changed" }); break;
            case "delete-recreate": await library.DeleteAsync("a"); await library.ImportAsync(Document(entry)); break;
            case "installation": await library.RemoveInstallationAsync("a", "i"); break;
            case "relation": await library.LinkAsync("a", Work()); break;
            case "new-request": await library.BeginRequestAsync("a"); break;
        }
        engine.Complete(id); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecognitionJobState.Stale, queue.Snapshot.Single().State); Assert.Throws<StaleLibraryRequestException>(() => queue.RequireCurrentResult(id));
    }
    [Fact] public async Task WorkerCountIsBoundedAndQueuedCancellationCannotProduceReview()
    {
        var entries = Enumerable.Range(0, 12).Select(i => Entry(folder: @"C:\synthetic\game" + i) with { Id = "entry" + i }).ToArray();
        using var profile = new TransactionProfile(Document(entries)); using var library = profile.Open(); var engine = new PausedEngine();
        await using var queue = new RecognitionQueue(library, engine); var ids = new List<Guid>();
        foreach (var entry in entries) ids.Add(await queue.EnqueueAsync(entry.Id, RecognitionMode.Reidentify, false));
        await engine.Wait(ids[0]); await engine.Wait(ids[1]); Assert.Equal(2, engine.Active); queue.CancelAll();
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, engine.Maximum);
        Assert.All(queue.Snapshot, j => { Assert.Equal(RecognitionJobState.Cancelled, j.State); Assert.Null(j.Result); });
    }
    [Fact] public async Task ShutdownCancelsActiveAndQueuedWorkWithoutUnhandledObservers()
    {
        using var profile = new TransactionProfile(Document(Entry(folder: @"C:\synthetic\game"))); using var library = profile.Open(); var engine = new PausedEngine();
        var queue = new RecognitionQueue(library, engine); queue.Changed += () => throw new InvalidOperationException("UI subscriber failure");
        var id = await queue.EnqueueAsync("a", RecognitionMode.Reidentify, false); await engine.Wait(id);
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, engine.Active); Assert.True(queue.ObserverFailed);
    }
    [Fact] public async Task WishlistsAreExcludedFromLocalRecognition()
    {
        using var profile = new TransactionProfile(Document(Entry())); using var library = profile.Open(); var engine = new PausedEngine();
        await using var queue = new RecognitionQueue(library, engine);
        await Assert.ThrowsAsync<InvalidDataException>(() => queue.EnqueueAsync("a", RecognitionMode.Reidentify, false)); Assert.Empty(engine.Started);
    }
}
