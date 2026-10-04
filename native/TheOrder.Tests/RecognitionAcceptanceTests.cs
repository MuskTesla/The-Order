using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;
using static TheOrder.Tests.OnlineTransportTests;
using static TheOrder.Tests.RecognitionReviewTests;

namespace TheOrder.Tests;

public sealed class RecognitionAcceptanceTests
{
    [Theory]
    [InlineData("local", "evidence")][InlineData("identity", "evidence")][InlineData("identity", "queries")]
    [InlineData("input", "entry")][InlineData("local", "observations")]
    public async Task NestedCorruptionFailsClosed(string parent, string property)
    {
        await using var lab = new Lab(); await lab.Init(); await lab.Recognize();
        var json = JsonNode.Parse(lab.Store.Bytes!)!; json["reviews"]![0]!["result"]![parent]![property] = null;
        Assert.Throws<InvalidDataException>(() => new RecognitionReviewCodec().Read(new(JsonSerializer.SerializeToUtf8Bytes(json), null)));
    }
    [Fact] public async Task CancelDuringFinalEvidenceCheckPreventsAnyCommit()
    {
        await using var lab = new Lab(); await lab.Init(); var id = await lab.Recognize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lab.Reader.Pause = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        var apply = lab.Reviews.ApplyAsync(id, "200", default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lab.Queue.Cancel(id); Assert.False((await apply.WaitAsync(TimeSpan.FromSeconds(5))).IsCommitted);
        Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ManualCommitUsesProductionCanonicalVerification(bool merge)
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(
            merge && request.RequestUri!.AbsolutePath.EndsWith("/200", StringComparison.Ordinal)
            ? Json("{\"object\":\"work\",\"code\":\"ENTITY_MERGED\",\"current_id\":\"201\"}",404)
            : Json("{\"object\":\"work\",\"id\":\"201\",\"display_name\":\"星空の航路\"}"))));
        var catalog = new NextMoeCatalog(new(http, new Credential(), new("https://api.nextmoe.dev/v2/")));
        await using var lab = new Lab(catalog: catalog); await lab.Init();
        var decision = await lab.Reviews.ManualLinkAsync("a", "200", default);
        Assert.Equal(merge, decision.IsCommitted); Assert.Equal(merge ? "201" : null, lab.Library.Snapshot.Entries[0].Metadata?.Id);
    }
    [Theory][InlineData("星空の航路", "星空の航路", false)][InlineData("航路 第2章", "航路 第1章", true)]
    [InlineData("星空の航路 FD", "星空の航路", true)]
    public async Task GoldenCorpusRunsThroughEngineQueueAndReview(string local, string remote, bool veto)
    {
        await using var lab = new Lab(entry: Entry(local, @"C:\synthetic\game")); lab.Catalog.Works = [Work(title:remote)];
        await lab.Init(); var id = await lab.Recognize(); var review = Assert.Single(lab.Reviews.Snapshot);
        Assert.Equal(id, review.Id); Assert.Equal(RecognitionReviewStatus.NeedsReview, review.Status);
        var candidate = Assert.Single(review.Result!.Candidates); Assert.Equal("200", candidate.Work.Id); Assert.Equal(veto, candidate.Vetoed);
        Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
    }
    [Fact] public void OrderingAndVetoInvariantsAcrossInputPermutations()
    {
        var ranker = new EvidenceRanking(); var evidence = Evidence(new LocalTitleEvidence("星空の航路 第2章", "folder"));
        var works = new[] { Work("20", "星空の航路 第2章"), Work("10", "星空の航路 第2章"), Work("30", "星空の航路 第1章") };
        var expected = ranker.Rank(evidence, works);
        foreach (var order in new[] { works.Reverse(), works.Skip(1).Concat(works.Take(1)), works.AsEnumerable() })
            Assert.Equal(expected.Select(c=>c.Work.Id), ranker.Rank(evidence,order).Select(c=>c.Work.Id));
        var expanded = ranker.Rank(evidence, works.Append(Work("99", "全然別の作品")));
        Assert.Equal(expected[0].Confidence, expanded[0].Confidence); Assert.Equal(expected[0].Evidence.ToArray(), expanded[0].Evidence.ToArray());
        Assert.All(expanded.Where(c=>c.Vetoed), c=>Assert.False(c.HighConfidence));
    }
    [Fact] public async Task GeneratedUiProfileSatisfiesProductionCodec()
    {
        using var profile = new TransactionProfile();
        string destination = Path.Combine(profile.Root, "generated");
        var start = new ProcessStartInfo("node") { WorkingDirectory = ReferenceSources.Repository(), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("native/scripts/generate-recognition-profile.mjs"); start.ArgumentList.Add(destination);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode == 0, await error); Assert.Contains("\"count\":7", await output);
        var entries = new NativeLibraryCodec().Decode(await File.ReadAllBytesAsync(Path.Combine(destination, "library.json")));
        Assert.Equal(7, entries.Length); Assert.All(entries, e => { Assert.Null(e.Metadata); Assert.Single(e.Installations); });
    }
    [Fact] public async Task RecognitionBaseline()
    {
        if (Environment.GetEnvironmentVariable("THE_ORDER_RECOGNITION_BASELINE") != "1") return;
        var output = new List<object>();
        foreach (bool online in new[] { false, true }) foreach (int count in new[] { 1, 100, 1000 })
        {
            using var profile = new TransactionProfile(); string folder = Path.Combine(profile.Root,"星空の航路"); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder,"game.ini"),"Title=星空の航路");
            var index = new TitleIndex(()=>File.OpenRead(Path.Combine(ReferenceSources.Repository(),"data/title-index.json.gz")));
            int active=0, maximum=0, requests=0;
            using var http=new HttpClient(new Handler(async (request,c) => {
                int current=Interlocked.Increment(ref active); Interlocked.Increment(ref requests); int old;
                do { old=maximum; if(old>=current)break; } while(Interlocked.CompareExchange(ref maximum,current,old)!=old);
                try { await Task.Delay(1,c); string body=request.RequestUri!.AbsolutePath.EndsWith("/search",StringComparison.Ordinal)
                    ? "{\"object\":\"list\",\"items\":[{\"object\":\"search_result\",\"target_object\":\"work\",\"id\":\"200\"}]}"
                    : request.RequestUri.Query.Contains("ids=",StringComparison.Ordinal) ? "{\"object\":\"list\",\"items\":[{\"object\":\"work\",\"id\":\"200\",\"display_name\":\"星空の航路\"}]}"
                    : request.RequestUri.AbsolutePath.EndsWith("/releases",StringComparison.Ordinal) ? "{\"object\":\"list\",\"items\":[]}"
                    : "{\"object\":\"work\",\"id\":\"200\",\"display_name\":\"星空の航路\"}";
                    return Json(body); } finally { Interlocked.Decrement(ref active); }
            }));
            var secret=new Credential(); var credentials=new AuthenticatedCredentials(secret,secret);
            var catalog=new NextMoeCatalog(new(http,credentials,new("https://api.nextmoe.dev/v2/")));
            var reader=new WindowsRecognitionEvidence(new LocalEvidenceParser(),RecognitionFingerprint.Evidence,new ExecutableEvidenceParser());
            var source=new BenchmarkSource(folder); var engine=new RecognitionEngine(index,reader,new ReviewSafeRanking(),catalog,credentials.AuthContext);
            await using var queue=new RecognitionQueue(source,engine,credentials.AuthContext);
            int workers=0; queue.Changed+=()=>InterlockedExtensionsMax(ref workers,queue.Progress.Processing);
            var clock=Stopwatch.StartNew(); for(int i=0;i<count;i++)await queue.EnqueueAsync(i.ToString(),RecognitionMode.Reidentify,online);
            await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(3)); clock.Stop();
            Assert.Equal(count,queue.Progress.NeedsReview); Assert.Equal(0,queue.Progress.Failed); Assert.InRange(workers,1,2); Assert.InRange(maximum,0,2);
            using var process=Process.GetCurrentProcess();
            output.Add(new { count, online, elapsedMs=clock.Elapsed.TotalMilliseconds, entriesPerSecond=count/clock.Elapsed.TotalSeconds, workingSetBytes=process.WorkingSet64, maximumJobs=workers, maximumHttp=maximum, requests, indexLoadMs=index.LoadMilliseconds, index.RecordCount, index.TitleCount });
        }
        string destination=Path.Combine(ReferenceSources.Repository(),".artifacts/native-phase4b/recognition-baseline.json");
        await File.WriteAllTextAsync(destination,JsonSerializer.Serialize(new {label="Phase 4B baseline; synthetic IO and HTTP; not SLA; sequential runs in shared test process", results=output},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void InterlockedExtensionsMax(ref int location,int value) { int old; do { old=location; if(old>=value)return; }while(Interlocked.CompareExchange(ref location,value,old)!=old); }
    private sealed class BenchmarkSource(string folder): IRecognitionLibrarySource
    {
        private readonly RecognitionLifecycle life=new(Guid.NewGuid(),Guid.NewGuid(),0,0);
        public Task<RecognitionEntrySnapshot> CaptureRecognitionAsync(string id,CancellationToken cancellation)=>Task.FromResult(new RecognitionEntrySnapshot(Entry(folder:folder) with{Id=id},life));
        public Task<bool> IsRecognitionCurrentAsync(RecognitionEntrySnapshot snapshot,CancellationToken cancellation)=>Task.FromResult(snapshot.Lifecycle==life);
    }
}
