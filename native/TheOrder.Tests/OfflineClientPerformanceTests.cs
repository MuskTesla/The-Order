using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Transaction remediation")]
public sealed class OfflineClientPerformanceTests(ITestOutputHelper output)
{
    private const string Clock = "2026-10-04T00:00:00.000Z";
    private static readonly LegacyLibraryCodec Codec = new();
    private static byte[] Library(int count) => Codec.Encode([.. Enumerable.Range(0, count).Select(i =>
        new LibraryEntry($"g-{i:D5}", $"合成作品 {i:D5}", false, null, [], null, i % 2 == 0 ? "playing" : "planned", i % 11,
            i % 3 == 0, [$"分类 {i % 9}"], [$"标签 {i % 13}"], "合成笔记", i * 60, 0, [], Clock, null, null, null, new(false, null)))], Clock);

    // These are Application timings, not XAML first-paint, scrolling or visual virtualization evidence.
    [Theory, Trait("Category", "Performance")]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task LargeOfflineProjectionQueryAndSingleDetail(int count)
    {
        using var p = new TransactionProfile(Library(count));
        await using var client = new OfflineLibraryClient(new IsolatedLibraryFactory(Codec, new(p.Root)), new LegacyClientTextRules());
        var clock = Stopwatch.StartNew(); Assert.Equal(ClientOutcome.Success, (await client.LoadAsync(default)).Outcome);
        var load = clock.Elapsed.TotalMilliseconds; clock.Restart();
        Assert.Equal(count, (await client.QueryAsync(new(), default)).Items.Length);
        var index = clock.Elapsed.TotalMilliseconds;
        foreach (var (name, query) in new[] { ("search", new LibraryQuery("合成 000")), ("sort", new LibraryQuery(Sort: "rating")), ("filter", new LibraryQuery(Status: "playing", Favorites: true)) })
        {
            var runs = new List<double>();
            for (var round = 0; round < 4; round++)
            {
                clock.Restart(); var result = await client.QueryAsync(query, default); var elapsed = clock.Elapsed.TotalMilliseconds;
                Assert.NotEmpty(result.Items); if (round > 0) runs.Add(elapsed);
                if (name == "filter") Assert.All(result.Items, item => { Assert.True(item.Favorite); Assert.Equal("playing", item.Status); });
                if (name == "sort") Assert.Equal(10, result.Items[0].Rating);
            }
            runs.Sort(); output.WriteLine($"APPLICATION count={count} operation={name} warmup=1 measured=3 medianMs={runs[1]:F2} minMs={runs[0]:F2} maxMs={runs[2]:F2}");
        }
        var details = await client.DetailsAsync($"g-{count - 1:D5}", default); Assert.Equal($"合成作品 {count - 1:D5}", details.Personal.Name);
        output.WriteLine($"APPLICATION count={count} loadMs={load:F1} firstIndexMs={index:F1}; not UI evidence");
    }

    [Fact]
    public async Task ProjectionWorkRunsOffUiAndPublishedStateReturnsToUi()
    {
        using var p = new TransactionProfile(Library(1000));
        await SingleThread.Run(async () =>
        {
            var ui = Environment.CurrentManagedThreadId;
            var text = new CheckedText(ui);
            await using var client = new OfflineLibraryClient(new IsolatedLibraryFactory(Codec, new(p.Root)), text);
            await using var vm = new WorkspaceViewModel(client, new IsolatedUiPreferencesStore(new(p.Root)), new(p.Root), text);
            var notifications = 0;
            vm.PropertyChanged += (_, _) => { Assert.Equal(ui, Environment.CurrentManagedThreadId); notifications++; };
            await vm.InitializeAsync(); await vm.SetQueryAsync(new("合成", Sort: "name"));
            await vm.NavigateAsync(WorkspacePage.Details, "g-00000", () => Task.FromResult(UnsavedChoice.Stay));
            vm.Draft!.Notes = "changed"; Assert.True((await vm.SaveAsync()).IsCommitted);
            Assert.True(text.Calls > 1000); Assert.True(notifications > 0);
        }).WaitAsync(TimeSpan.FromSeconds(30));
    }
    [Fact]
    public async Task LargeValidReadOnlyFlowsThroughProductionClientAndPresentation()
    {
        var small = Codec.Decode(Library(2));
        var entries = small.Select(e => e with { Sessions = [.. Enumerable.Range(0, 100000).Select(i => new PlaySession("s-" + i, Clock, Clock, 1))], TotalPlaySeconds = 100000 }).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = "kun-gal-manager", version = 1, exportedAt = Clock, games = entries.Select(LegacyLibraryCodec.Project) }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var p = new TransactionProfile(bytes);
        await using var client = new OfflineLibraryClient(new IsolatedLibraryFactory(Codec, new(p.Root)), new LegacyClientTextRules());
        await using var vm = new WorkspaceViewModel(client, new IsolatedUiPreferencesStore(new(p.Root)), new(p.Root), new LegacyClientTextRules());
        await vm.InitializeAsync(); Assert.True(vm.IsLoaded); Assert.True(vm.IsReadOnly); Assert.False(vm.CanEdit); Assert.Equal(2, vm.Items.Count);
        await vm.NavigateAsync(WorkspacePage.Details, "g-00000", () => Task.FromResult(UnsavedChoice.Stay)); vm.Draft!.Notes = "rejected";
        Assert.False(vm.CanSave); Assert.Equal(ClientOutcome.ReadOnly, (await vm.SaveAsync()).Outcome);
        Assert.Equal(ClientOutcome.ReadOnly, (await client.BatchAsync(["g-00000"], new() { Favorite = new(true, true) }, default)).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(p.Main)); Assert.False(File.Exists(p.Backup));
    }
    private sealed class CheckedText(int ui) : IClientTextRules
    {
        private readonly LegacyClientTextRules inner = new(); public int Calls;
        private void Check() { Assert.NotEqual(ui, Environment.CurrentManagedThreadId); Assert.Null(SynchronizationContext.Current); Calls++; }
        public string NormalizeSearch(string value) { Check(); return inner.NormalizeSearch(value); }
        public ImmutableArray<string> QueryTerms(string value) { Check(); return inner.QueryTerms(value); }
        public ImmutableArray<string> SplitLabels(string value) { Check(); return inner.SplitLabels(value); }
        public int CompareNames(string a, string b) { Check(); return inner.CompareNames(a, b); }
        public int CompareIds(string a, string b) { Check(); return inner.CompareIds(a, b); }
    }
    private sealed class SingleThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
        public static Task Run(Func<Task> action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                var context = new SingleThread(); SetSynchronizationContext(context);
                context.Post(async _ => { try { await action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } finally { context.queue.CompleteAdding(); } }, null);
                foreach (var (callback, state) in context.queue.GetConsumingEnumerable()) callback(state);
                context.queue.Dispose();
            }) { IsBackground = true, Name = "Offline presentation test UI" }.Start();
            return done.Task;
        }
    }
}
