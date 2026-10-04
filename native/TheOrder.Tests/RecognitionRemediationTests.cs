using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;
using static TheOrder.Tests.RecognitionReviewTests;

namespace TheOrder.Tests;

public sealed class RecognitionRemediationTests
{
    private sealed class Preferences : IUiPreferencesStore
    {
        public Task<ClientPreferences> LoadAsync(CancellationToken c) => Task.FromResult(new ClientPreferences());
        public Task SaveAsync(ClientPreferences p, CancellationToken c) => Task.CompletedTask;
    }
    private sealed class Covers : IRemoteCoverRegistry { public string? Register(string? url, string revision) => url; }
    private static WorkspaceViewModel Workspace(Lab lab) => new(lab.Offline, new Preferences(), new(lab.Profile.Root), new LegacyClientTextRules());
    private static RecognitionViewModel Vm(Lab lab, WorkspaceViewModel workspace) => new(lab.Offline, lab.Queue, lab.Reviews, lab.Catalog, new Covers(), lab.Credentials.AuthContext, workspace);
    private static WindowsRecognitionEvidence Reader() => new(new LocalEvidenceParser(), RecognitionFingerprint.Evidence, new ExecutableEvidenceParser());
    private sealed class WriteProbe : IPersistenceProbe
    {
        public int Calls;
        public Task ReachAsync(PersistenceTarget target, PersistenceStage stage) { Calls++; return Task.CompletedTask; }
    }

    // Synthetic, non-executable resource containers. The layout is the same independent fixture
    // format used by the existing Electron PE tests, not another implementation of recognition.
    private static byte[] Block(string key, byte[] value, int type, params byte[][] children)
    {
        static int Align(int n) => (n + 3) & ~3;
        var keyBytes = Encoding.Unicode.GetBytes(key + "\0"); int start = Align(6 + keyBytes.Length), size = start + value.Length;
        var offsets = children.Select(c => { int at = Align(size); size = at + c.Length; return at; }).ToArray();
        var bytes = new byte[size]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)size));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)(type == 1 ? value.Length / 2 : value.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)type); keyBytes.CopyTo(bytes, 6); value.CopyTo(bytes, start);
        for (int i = 0; i < children.Length; i++) children[i].CopyTo(bytes, offsets[i]); return bytes;
    }
    internal static byte[] Pe(params (string Key, string Value)[] fields)
    {
        var payload = Block("VS_VERSION_INFO", new byte[52], 0, Block("StringFileInfo", [], 1,
            Block("041104b0", [], 1, fields.Select(f => Block(f.Key, Encoding.Unicode.GetBytes(f.Value + "\0"), 1)).ToArray())));
        int raw = (0x100 + payload.Length + 511) & ~511; var bytes = new byte[0x400 + raw];
        void U16(int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        bytes[0] = 77; bytes[1] = 90; U32(0x3c, 0x80); U32(0x80, 0x4550); U16(0x84, 0x14c); U16(0x86, 1); U16(0x94, 0xe0); U16(0x98, 0x10b);
        U32(0x98 + 92, 16); U32(0x98 + 112, 0x1000); U32(0x98 + 116, (uint)(0x100 + payload.Length));
        U32(0x178 + 8, (uint)(0x100 + payload.Length)); U32(0x178 + 12, 0x1000); U32(0x178 + 16, (uint)raw); U32(0x178 + 20, 0x400);
        foreach (int at in new[] { 0x400, 0x420, 0x440 }) U16(at + 14, 1);
        U32(0x410, 16); U32(0x414, 0x80000020); U32(0x430, 1); U32(0x434, 0x80000040); U32(0x450, 1041); U32(0x454, 0x60);
        U32(0x460, 0x1100); U32(0x464, (uint)payload.Length); payload.CopyTo(bytes, 0x500); return bytes;
    }
    [Theory]
    [InlineData("FileDescription", "")][InlineData("OriginalFilename", "")]
    [InlineData("FileDescription", "TVP(KIRIKIRI)")][InlineData("FileDescription", "星空の航路 - start menu")]
    [InlineData("ProductName", "")][InlineData("CompanyName", "星空の航路")]
    public async Task PeSemanticMutationRejectsOldApplyWithUnchangedPhysicalIdentity(string field, string product)
    {
        using var files = new TransactionProfile(); string folder = Path.Combine(files.Root, "game"); Directory.CreateDirectory(folder);
        var fields = new List<(string, string)>(); if (product.Length > 0) fields.Add(("ProductName", product));
        fields.Add((field, "星空の航路" + (field == "OriginalFilename" ? ".exe" : "")));
        string exe = Path.Combine(folder, "Game.exe"); File.WriteAllBytes(exe, Pe([.. fields]));
        var physical = new WindowsFilesystemIdentity().Inspect(exe, false).Observation!.Identity;
        var probe = new WriteProbe();
        await using var lab = new Lab(entry: Entry("game", folder), reader: Reader(), probe: probe); await lab.Init();
        var id = await lab.Queue.EnqueueAsync("a", RecognitionMode.Reidentify, true, true); await lab.Queue.WhenIdleAsync();
        var original = lab.Queue.RequireCurrentResult(id); Assert.NotEmpty(original.Candidates);
        fields[^1] = (field, "月夜の旅路" + (field == "OriginalFilename" ? ".exe" : ""));
        File.WriteAllBytes(exe, Pe([.. fields])); Assert.Equal(physical, new WindowsFilesystemIdentity().Inspect(exe, false).Observation!.Identity);
        var before = File.ReadAllBytes(Path.Combine(lab.Profile.Root, "library.json"));
        var decision = await lab.Reviews.ApplyAsync(id, "200", default);
        Assert.False(decision.IsCommitted); Assert.Null(lab.Library.Snapshot.Entries[0].Metadata);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(lab.Profile.Root, "library.json")));
        Assert.Equal(0, probe.Calls);
        Assert.Equal(RecognitionReviewStatus.Stale, Assert.Single(lab.Reviews.Snapshot).Status);
    }
    [Fact] public void PeDigestIgnoresUnconsumedMetadataAndOrderingButDistinguishesMissingEmpty()
    {
        var parser = new ExecutableEvidenceParser(); ExecutableEvidence Parse(params (string, string)[] values) => parser.Read(new MemoryStream(Pe(values)), "Game.exe", default);
        var a = Parse(("ProductName", "星空の航路"), ("CompanyName", "青空软件"), ("LegalCopyright", "one"));
        var b = Parse(("LegalCopyright", "two"), ("CompanyName", "青空软件"), ("ProductName", "星空の航路"));
        Assert.Equal(a.EvidenceDigest, b.EvidenceDigest); Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(Parse(("ProductName", "星空の航路")).EvidenceDigest, Parse(("ProductName", "星空の航路"), ("FileDescription", "")).EvidenceDigest);
        var engine = Parse(("ProductName", "TVP(KIRIKIRI)"), ("FileDescription", "星空の航路"));
        var changed = Parse(("ProductName", "TVP(KIRIKIRI)"), ("FileDescription", "月夜の旅路"));
        Assert.Equal("", engine.Fingerprint); Assert.Equal(engine.Fingerprint, changed.Fingerprint); Assert.NotEqual(engine.EvidenceDigest, changed.EvidenceDigest);
    }
    [Fact] public void PeResourceEnumerationOrderAndCultureDoNotChangeDigest()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/recognition-evidence-oracle.json")));
        var bytes = Convert.FromBase64String(oracle.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "languages").GetProperty("bytes").GetString()!);
        var reversed = bytes.ToArray(); bytes.AsSpan(0x450, 8).CopyTo(reversed.AsSpan(0x458)); bytes.AsSpan(0x458, 8).CopyTo(reversed.AsSpan(0x450));
        var parser = new ExecutableEvidenceParser(); var before = parser.Read(new MemoryStream(bytes), "Game.exe", default);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try { System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR"); Assert.Equal(before.EvidenceDigest, parser.Read(new MemoryStream(reversed), "Game.exe", default).EvidenceDigest); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
    [Theory][InlineData(false)][InlineData(null)][InlineData(true)]
    public async Task ReidentifySeparatesProviderNameFromPersonalEvidence(bool? custom)
    {
        using var files = new TransactionProfile(); string folder = Path.Combine(files.Root, "game"); Directory.CreateDirectory(folder);
        var entry = Entry(folder: folder) with { NameIsCustom = custom, Metadata = Work(), Notes = "personal", Favorite = true };
        await using var lab = new Lab(entry: entry, reader: Reader()); await lab.Init(); var id = await lab.Recognize(); var result = lab.Queue.RequireCurrentResult(id);
        Assert.Equal(entry.Name, result.Input.Entry.Name); Assert.Equal("personal", result.Input.Entry.Notes);
        Assert.Equal(custom == true, result.Local.Evidence.Titles.Any(t => t.Source == "custom-name"));
        Assert.Equal(custom == true, result.Candidates.Any(c => c.HighConfidence));
        if (custom == true) { Assert.True((await lab.Reviews.ApplyAsync(id, "200", default)).IsCommitted); Assert.Equal("personal", lab.Library.Snapshot.Entries[0].Notes); Assert.True(lab.Library.Snapshot.Entries[0].Favorite); }
    }
    [Fact] public async Task CandidateSelectionClearsLoadedDetailsAndRejectsAbaLateResponse()
    {
        await using var lab = new Lab(); lab.Catalog.Works = [Work(), Work("201")]; await lab.Init(); var id = await lab.Recognize();
        await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace); vm.Select(id);
        await vm.OpenDetailsAsync("200"); Assert.Equal("200", vm.DisplayedCandidateId);
        var responses = new List<TaskCompletionSource<WorkMetadata>>();
        lab.Catalog.Detail = (_, _) => { var t = new TaskCompletionSource<WorkMetadata>(); responses.Add(t); return t.Task; };
        vm.SelectCandidate("201"); Assert.Null(vm.DisplayedCandidateId); Assert.Null(vm.RemoteCoverKey); Assert.DoesNotContain("编号：200", vm.RemoteFacts);
        vm.SelectCandidate("200"); vm.SelectCandidate("201"); Assert.Equal(3, responses.Count);
        responses[0].SetResult(Work("201", "old first")); responses[1].SetResult(Work("200", "old second"));
        Assert.DoesNotContain("old", vm.RemoteFacts); Assert.Null(vm.DisplayedCandidateId);
        responses[2].SetResult(Work("201", "new selected")); Assert.Equal("201", vm.DisplayedCandidateId); Assert.Contains("new selected", vm.RemoteFacts);
        lab.Catalog.Detail = null; await vm.ApplyAsync(); Assert.Equal("201", lab.Library.Snapshot.Entries[0].Metadata?.Id);
    }
    [Fact] public async Task SlowCandidateAIsDiscardedAfterSelectingBOrAnotherReview()
    {
        await using var lab = new Lab(); lab.Catalog.Works = [Work(), Work("201")]; await lab.Init(); var id = await lab.Recognize();
        await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace); vm.Select(id);
        var a = new TaskCompletionSource<WorkMetadata>(); var b = new TaskCompletionSource<WorkMetadata>();
        lab.Catalog.Detail = (key, _) => key == "200" ? a.Task : b.Task;
        var old = vm.OpenDetailsAsync("200"); vm.SelectCandidate("201"); a.SetResult(Work("200", "late A")); await old;
        Assert.DoesNotContain("late A", vm.RemoteFacts); Assert.Null(vm.DisplayedCandidateId);
        vm.Select(Guid.NewGuid()); b.SetResult(Work("201", "late B")); Assert.DoesNotContain("late B", vm.RemoteFacts);
    }
    private static RecognitionReview History(int n) => new(new Guid(n, 0, 0, new byte[8]), "a", "history", DateTimeOffset.UnixEpoch.AddSeconds(n),
        RecognitionReviewStatus.Stale, "old-algorithm", null, [new("200", "title", "original", .9, "reason")]);
    [Theory][InlineData("null")][InlineData("score")][InlineData("id")][InlineData("text")][InlineData("count")][InlineData("result-null")][InlineData("nan")][InlineData("infinity")]
    public async Task CorruptHistoryIsPreservedThroughStorageAndPresentation(string fault)
    {
        var json = JsonNode.Parse(new RecognitionReviewCodec().Write([History(1)]))!;
        var candidates = json["reviews"]![0]!["historicalCandidates"]!;
        switch (fault)
        {
            case "null": case "result-null": candidates[0] = null; break;
            case "score": candidates[0]!["confidence"] = 999; break;
            case "nan": candidates[0]!["confidence"] = "NaN"; break;
            case "infinity": candidates[0]!["confidence"] = "Infinity"; break;
            case "id": candidates[0]!["id"] = null; break;
            case "text": candidates[0]!["title"] = new string('x', 2001); break;
            case "count": for (int i = 0; i < 5; i++) candidates.AsArray().Add(candidates[0]!.DeepClone()); break;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(json); var store = new MemoryStore { Bytes = bytes };
        await using var lab = new Lab(store); await lab.Init(); await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace);
        vm.Refresh(); vm.Select(History(1).Id); Assert.NotNull(vm.PersistenceError); Assert.False(vm.HasSelection); Assert.True(lab.Offline.Capability.Loaded);
        await Assert.ThrowsAsync<IOException>(() => lab.Reviews.FlushAsync()); Assert.Equal(bytes, store.Bytes); Assert.Equal(0, store.Writes);
        await lab.Reviews.DisposeAsync(); Assert.Contains("无法安全读取", vm.PersistenceError);
    }
    [Theory][InlineData("evidence")][InlineData("veto")][InlineData("infinity")][InlineData("conflicts")]
    public async Task InvalidLiveCandidateStructureCannotRestore(string fault)
    {
        await using var lab = new Lab(); await lab.Init(); await lab.Recognize(); var json = JsonNode.Parse(lab.Store.Bytes!)!;
        var candidate = json["reviews"]![0]!["result"]!["candidates"]![0]!;
        if (fault == "evidence") candidate["evidence"]![0] = null;
        if (fault == "veto") { candidate["vetoed"] = true; candidate["highConfidence"] = true; }
        if (fault == "infinity") candidate["confidence"] = "Infinity";
        if (fault == "conflicts") { candidate["versionNeedsConfirmation"] = true; candidate["vetoed"] = true; candidate["highConfidence"] = false; candidate["conflicts"] = null; }
        Assert.Throws<InvalidDataException>(() => new RecognitionReviewCodec().Read(new(JsonSerializer.SerializeToUtf8Bytes(json), null)));
    }
    [Fact] public async Task ValidHistoryRestoresAndCanBeSelectedWithoutAuthority()
    {
        var store = new MemoryStore { Bytes = new RecognitionReviewCodec().Write([History(1)]) };
        await using var lab = new Lab(store); await lab.Init(); await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace);
        vm.Select(History(1).Id); Assert.True(vm.HasSelection); Assert.Single(vm.Candidates); Assert.False(vm.CanApply); Assert.Null(vm.PersistenceError);
    }
    [Theory][InlineData(9999)][InlineData(10000)]
    public async Task HistoricalBudgetRetiresBeforeAdmissionAndRestart(int count)
    {
        var codec = new RecognitionReviewCodec(); var history = Enumerable.Range(1, count).Select(History).ToImmutableArray();
        await using var lab = new Lab(); await lab.Init(); await lab.Recognize(); var active = Assert.Single(lab.Reviews.Snapshot);
        var admitted = codec.PrepareAdmission(history.Add(active)); Assert.Contains(active, admitted); Assert.Equal(257, admitted.Length);
        Assert.DoesNotContain(admitted, r => r.Id == History(1).Id); Assert.Contains(admitted, r => r.Id == History(count).Id);
        var store = new MemoryStore { Bytes = codec.Write(history) };
        await using var restarted = new Lab(store); await restarted.Init(); Assert.Equal(256, restarted.Reviews.Snapshot.Length);
        Assert.Equal(ClientOutcome.Success, (await restarted.Reviews.IgnoreAsync(restarted.Reviews.Snapshot[0].Id, default)).Outcome);
    }
    [Fact] public async Task CapacityRefusalKeepsActiveReviewAndIgnoreThenAdmissionWorks()
    {
        await using var lab = new Lab(codec: new RecognitionReviewCodec(admissionEntries: 1)); await lab.Init(); var first = await lab.Recognize();
        await lab.Library.ImportAsync(new LegacyLibraryCodec().Encode([Entry(folder: @"C:\synthetic\other") with { Id = "b" }], Clock));
        var before = lab.Reviews.Snapshot; var persisted = lab.Store.Bytes!.ToArray();
        var second = await lab.Queue.EnqueueAsync("b", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync(); await lab.Reviews.FlushAsync();
        Assert.Equal(before.ToArray(), lab.Reviews.Snapshot.ToArray()); Assert.Equal(persisted, lab.Store.Bytes); Assert.Contains("ReviewCapacity", lab.Reviews.CapacityError);
        Assert.Throws<StaleLibraryRequestException>(() => lab.Queue.RequireCurrentResult(second));
        Assert.Equal(ClientOutcome.Success, (await lab.Reviews.IgnoreAsync(first, default)).Outcome);
        await lab.Queue.EnqueueAsync("b", RecognitionMode.Reidentify, true); await lab.Queue.WhenIdleAsync(); Assert.Single(lab.Reviews.Snapshot);
    }
    [Fact] public async Task RepeatedReidentifyRetiresHistoryAndApplyWorksNearBudget()
    {
        await using var lab = new Lab(codec: new RecognitionReviewCodec(admissionEntries: 4, retainedHistory: 3)); await lab.Init(); Guid latest = default;
        for (int i = 0; i < 15; i++) { latest = await lab.Recognize(); Assert.InRange(lab.Reviews.Snapshot.Length, 1, 4); }
        Assert.True((await lab.Reviews.ApplyAsync(latest, "200", default)).IsCommitted); Assert.Equal("200", lab.Library.Snapshot.Entries[0].Metadata?.Id);
    }
    [Fact] public async Task ByteBudgetRetiresHistoryBeforeCountAndRefusesUnretirableActive()
    {
        await using var lab = new Lab(); await lab.Init(); await lab.Recognize(); var active = Assert.Single(lab.Reviews.Snapshot); var codec = new RecognitionReviewCodec();
        int activeBytes = codec.Write([active]).Length;
        var limited = new RecognitionReviewCodec(admissionBytes: activeBytes + 10);
        Assert.Equal(new[] { active }, limited.PrepareAdmission([History(1), active]).ToArray());
        Assert.Throws<ReviewCapacityException>(() => new RecognitionReviewCodec(admissionBytes: activeBytes - 1).PrepareAdmission([active]));
    }
    private sealed class AdjustableBudget : IRecognitionReviewCodec
    {
        private readonly RecognitionReviewCodec codec = new();
        public int Bytes = 16 * 1024 * 1024;
        public ImmutableArray<RecognitionReview> Read(RecognitionReviewDocuments documents) => codec.Read(documents);
        public byte[] Write(ImmutableArray<RecognitionReview> reviews) => codec.Write(reviews);
        public ImmutableArray<RecognitionReview> PrepareAdmission(ImmutableArray<RecognitionReview> reviews) =>
            new RecognitionReviewCodec(admissionBytes: Bytes).PrepareAdmission(reviews);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task StatusDiagnosticGrowthIsBudgetedBeforePublication(bool manual)
    {
        var codec = new AdjustableBudget(); await using var lab = new Lab(codec: codec); await lab.Init(); await lab.Recognize();
        codec.Bytes = codec.Write(lab.Reviews.Snapshot).Length;
        if (manual) Assert.True((await lab.Reviews.ManualLinkAsync("a", "300", default)).IsCommitted);
        else { await lab.Library.UpdateAsync("a", new() { Name = "changed" }); await lab.Reviews.RefreshValidityAsync(default); }
        await lab.Reviews.FlushAsync();
        Assert.Empty(lab.Reviews.Snapshot); Assert.InRange(lab.Store.Bytes!.Length, 1, codec.Bytes); Assert.Null(lab.Reviews.PersistenceError);
    }
    [Fact] public async Task RestartLifecycleDiagnosticIsBudgetedBeforePublication()
    {
        var codec = new AdjustableBudget(); var initial = codec.Write([History(1) with { HistoricalFingerprint = new string('f', 64) }]);
        codec.Bytes = codec.Write(codec.Read(new(initial, null))).Length;
        var store = new MemoryStore { Bytes = initial }; await using var lab = new Lab(store, codec: codec); await lab.Init(); await lab.Reviews.FlushAsync();
        Assert.Empty(lab.Reviews.Snapshot); Assert.InRange(store.Bytes!.Length, 1, codec.Bytes); Assert.Null(lab.Reviews.PersistenceError);
    }
    [Theory][InlineData("星空の航路 FD", "星空の航路", true)][InlineData("航路 第2章", "航路 第1章", false)]
    public async Task UncertainVersionAndConfirmedConflictHaveDistinctPresentation(string local, string remote, bool uncertain)
    {
        await using var lab = new Lab(entry: Entry(local, @"C:\synthetic\game")); lab.Catalog.Works = [Work(title: remote)]; await lab.Init(); var id = await lab.Recognize();
        var candidate = Assert.Single(lab.Queue.RequireCurrentResult(id).Candidates); Assert.True(candidate.Vetoed); Assert.Equal(uncertain, candidate.VersionNeedsConfirmation);
        await using var workspace = Workspace(lab); await using var vm = Vm(lab, workspace); vm.Select(id);
        Assert.Contains(uncertain ? "版本信息不足" : "身份冲突", Assert.Single(vm.Candidates).Summary);
        Assert.Equal(ClientOutcome.ValidationFailure, (await lab.Reviews.ApplyAsync(id, "200", default)).Outcome);
    }
    private sealed class PublicationSource(IRecognitionLibrarySource inner) : IRecognitionLibrarySource
    {
        private int checks;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RecognitionEntrySnapshot> CaptureRecognitionAsync(string id, CancellationToken c) => inner.CaptureRecognitionAsync(id, c);
        public async Task<bool> IsRecognitionCurrentAsync(RecognitionEntrySnapshot input, CancellationToken c)
        {
            if (Interlocked.Increment(ref checks) == 2) { Entered.TrySetResult(); await Release.Task; }
            return await inner.IsRecognitionCurrentAsync(input, c);
        }
    }
    private sealed class ObservedEngine(IRecognitionEngine inner) : IRecognitionEngine
    {
        public RecognitionResult? Produced;
        public async Task<RecognitionResult> RecognizeAsync(RecognitionEntrySnapshot input, Guid id, bool online, bool pe, Action<RecognitionJobState> progress, CancellationToken c)
            => Produced = await inner.RecognizeAsync(input, id, online, pe, progress, c);
    }
    [Theory][InlineData("clear")][InlineData("replace")][InlineData("lifecycle")][InlineData("generation")]
    public async Task LateAuthChangePreservesLocalButNeverOldRecognitionAuthority(string change)
    {
        await using var lab = new Lab(); await lab.Init();
        var index = new TitleIndex(() => new MemoryStream(Gzip("{\"version\":1,\"records\":[{\"names\":[\"星空の航路\"],\"references\":[\"vndb:v123\"]}]}")));
        var engine = new ObservedEngine(new RecognitionEngine(index, lab.Reader, new ReviewSafeRanking(), lab.Catalog, lab.Credentials.AuthContext));
        var source = new PublicationSource(lab.Offline); await using var queue = new RecognitionQueue(source, engine, lab.Credentials.AuthContext);
        var id = await queue.EnqueueAsync("a", RecognitionMode.Reidentify, true); await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var local = engine.Produced!.Identity; Assert.Single(local.LocalCandidates);
        if (change == "replace") await lab.Credentials.SaveAsync("synthetic-replacement", default); else await lab.Credentials.ClearAsync(default);
        if (change == "lifecycle") await lab.Library.UpdateAsync("a", new() { Name = "new title" });
        Guid? newer = change == "generation" ? await queue.EnqueueAsync("a", RecognitionMode.Reidentify, false) : null;
        source.Release.SetResult(); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (change is "generation" or "lifecycle") Assert.Throws<StaleLibraryRequestException>(() => queue.RequireCurrentResult(id));
        else
        {
            var result = queue.RequireCurrentResult(id); Assert.Same(local, result.Identity); Assert.Single(result.Identity.LocalCandidates);
            Assert.Empty(result.Candidates); Assert.Null(result.AuthGeneration); Assert.True(result.EnrichmentUnavailable);
        }
        if (newer is { } current) Assert.Equal(current, queue.RequireCurrentResult(current).Generation);
    }
    [Fact] public async Task AlreadyPublishedReviewRetainsLocalStateAfterCredentialRemoval()
    {
        await using var lab = new Lab(); await lab.Init(); var id = await lab.Recognize(); var before = lab.Queue.RequireCurrentResult(id);
        await lab.Credentials.ClearAsync(default); await lab.Reviews.RefreshValidityAsync(default);
        var result = lab.Queue.RequireCurrentResult(id); Assert.Same(before.Identity, result.Identity); Assert.Empty(result.Candidates);
        Assert.Equal(RecognitionReviewStatus.NeedsReview, Assert.Single(lab.Reviews.Snapshot).Status);
        Assert.False((await lab.Reviews.ApplyAsync(id, "200", default)).IsCommitted);
    }
    [Theory][InlineData("normal")][InlineData("corrupt")][InlineData("capacity")]
    public async Task RemediationProfileGeneratorUsesProductionLibraryAndReviewCodecs(string mode)
    {
        using var profile = new TransactionProfile(); string output = Path.Combine(profile.Root, "generated");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node") {
            WorkingDirectory = ReferenceSources.Repository(), UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "native/scripts/generate-recognition-remediation-profile.mjs", output, mode }
        })!;
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        Assert.Equal(5, new LegacyLibraryCodec().Decode(File.ReadAllBytes(Path.Combine(output, "library.json"))).Length);
        var storage = new IsolatedRecognitionReviewStorage(new(output)); var documents = await storage.ReadAsync(default); var codec = new RecognitionReviewCodec();
        if (mode == "corrupt") Assert.Throws<InvalidDataException>(() => codec.Read(documents));
        else Assert.Equal(mode == "capacity" ? 256 : 0, codec.PrepareAdmission(codec.Read(documents)).Length);
    }
}
