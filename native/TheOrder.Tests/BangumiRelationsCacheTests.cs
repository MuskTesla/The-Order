using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class BangumiRelationsCacheTests
{
    private static BangumiRelationIdentity Identity(string local = "synthetic-game") => new(local, "session-1", "200", "42", new('A', 64));
    private static BangumiRelationsSnapshot Snapshot(string title = "合成关联", string subject = "42") => new(subject,
        DateTimeOffset.Parse("2026-10-13T01:00:00Z"), [new("43", BangumiRelationKind.Anime, title, "Original", "改编",
            "https://lain.bgm.tv/pic/cover/l/synthetic.jpg")]) { Truncated = true, UnsupportedCount = 2 };
    private sealed class Fixture : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "the-order-relations-test-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string CacheRoot => Path.Combine(Root, IsolatedBangumiRelationsCache.DirectoryName);
        public NativeProfileStore Profile { get; private set; }
        public Fixture(int rootLength = 0)
        {
            Root = Path.Combine(Parent, rootLength == 0 ? "profile" : new string('p', rootLength - Parent.Length - 1));
            Profile = NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec());
        }
        public IsolatedBangumiRelationsCache Store(Func<PersistenceStage, Task>? probe = null) => new(Profile.Configuration(), probe);
        public string PathFor(BangumiRelationIdentity identity) => Path.Combine(CacheRoot, identity.CacheKey + ".json");
        public void Reopen() { Profile.Dispose(); Profile = NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec()); }
        public void Dispose()
        {
            Profile.Dispose();
            var path = Path.GetFullPath(Parent); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!path.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("the-order-relations-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture escaped temporary root.");
            Directory.Delete(path, true);
        }
    }
    private static void SameSnapshot(BangumiRelationsSnapshot expected, BangumiRelationsSnapshot? actual)
    {
        Assert.NotNull(actual); Assert.Equal(expected.SubjectId, actual.SubjectId); Assert.Equal(expected.FetchedAtUtc, actual.FetchedAtUtc);
        Assert.Equal(expected.Truncated, actual.Truncated); Assert.Equal(expected.UnsupportedCount, actual.UnsupportedCount);
        Assert.Equal(expected.Items.ToArray(), actual.Items.ToArray());
    }

    [Fact]
    public async Task RoundTripAndProcessLifecycleChangeRetainPublicRelationsAndLibraryBytes()
    {
        using var fixture = new Fixture(); var identity = Identity(); var snapshot = Snapshot();
        var before = ProfileFixture.Hashes(fixture.Root);
        Assert.Null(await fixture.Store().ReadAsync(identity, default)); Assert.False(Directory.Exists(fixture.CacheRoot));
        await fixture.Store().WriteAsync(identity, snapshot, default);
        Assert.DoesNotContain("session-1", File.ReadAllText(fixture.PathFor(identity)));
        SameSnapshot(snapshot, await fixture.Store().ReadAsync(identity, default));
        fixture.Reopen();
        SameSnapshot(snapshot, await fixture.Store().ReadAsync(identity with { Lifecycle = "session-2" }, default));
        Assert.Equal(before, ProfileFixture.Hashes(fixture.Root));
    }

    [Theory]
    [InlineData(229)][InlineData(244)][InlineData(300)]
    public async Task AtomicTemporaryPathPastMaxPathStillPersistsAndReopens(int targetLength)
    {
        // A normal, supported Profile path may be short enough while the hashed
        // cache file plus same-directory GUID temporary suffix exceeds MAX_PATH.
        using var fixture = new Fixture(targetLength - IsolatedBangumiRelationsCache.DirectoryName.Length - 71);
        var identity = Identity(); var expected = Snapshot(); var path = fixture.PathFor(identity);
        Assert.Equal(targetLength, path.Length);
        Assert.True((path + "." + new string('A', 32) + ".tmp").Length >= 260);
        var before = ProfileFixture.Hashes(fixture.Root);
        await fixture.Store().WriteAsync(identity, expected, default);
        SameSnapshot(expected, await fixture.Store().ReadAsync(identity, default));
        await fixture.Store().WriteAsync(identity, Snapshot("更新后的原文・日本語 😀"), default);
        fixture.Reopen();
        SameSnapshot(Snapshot("更新后的原文・日本語 😀"), await fixture.Store().ReadAsync(identity with { Lifecycle = "restarted" }, default));
        Assert.Equal(before, ProfileFixture.Hashes(fixture.Root));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.tmp"));
    }

    [Theory]
    [InlineData("local")][InlineData("nextmoe")][InlineData("subject")][InlineData("fingerprint")]
    public async Task ChangingAnyPersistentAssociationCannotReuseOrAdoptAnotherEnvelope(string field)
    {
        using var fixture = new Fixture(); var identity = Identity(); var store = fixture.Store();
        await store.WriteAsync(identity, Snapshot(), default);
        var other = field switch
        {
            "local" => identity with { LocalGameId = "other-local" },
            "nextmoe" => identity with { NextMoeId = "201" },
            "subject" => identity with { SubjectId = "44" },
            _ => identity with { Fingerprint = new('B', 64) }
        };
        Assert.Null(await store.ReadAsync(other, default));
        File.Copy(fixture.PathFor(identity), fixture.PathFor(other)); var before = File.ReadAllBytes(fixture.PathFor(other));
        Assert.Null(await store.ReadAsync(other, default));
        await store.WriteAsync(other, Snapshot("fresh", other.SubjectId), default);
        Assert.Equal(before, File.ReadAllBytes(fixture.PathFor(other)));
        SameSnapshot(Snapshot(), await store.ReadAsync(identity, default));
    }

    [Fact]
    public async Task CopyingCacheAcrossProfilesCannotReadReplaceOrPruneTheForeignEnvelope()
    {
        using var first = new Fixture(); using var second = new Fixture(); var identity = Identity();
        await first.Store().WriteAsync(identity, Snapshot(), default); Directory.CreateDirectory(second.CacheRoot);
        File.Copy(first.PathFor(identity), second.PathFor(identity)); var before = File.ReadAllBytes(second.PathFor(identity));
        Assert.NotEqual(first.Profile.Manifest.ProfileId, second.Profile.Manifest.ProfileId);
        Assert.Null(await second.Store().ReadAsync(identity, default));
        await second.Store().WriteAsync(identity, Snapshot("new"), default);
        Assert.Equal(before, File.ReadAllBytes(second.PathFor(identity)));
    }

    [Theory]
    [InlineData("Version")][InlineData("ProfileId")][InlineData("StoreId")][InlineData("Hash")]
    [InlineData("Payload")][InlineData("Identity")][InlineData("NullNextMoe")][InlineData("FutureField")][InlineData("Malformed")]
    public async Task InvalidFutureAndDamagedEnvelopesAreMissesAndRemainByteForByte(string fault)
    {
        using var fixture = new Fixture(); var identity = Identity(); var store = fixture.Store();
        await store.WriteAsync(identity, Snapshot(), default); var path = fixture.PathFor(identity);
        var document = JsonNode.Parse(File.ReadAllBytes(path))!;
        switch (fault)
        {
            case "Version": document[fault] = 2; break;
            case "ProfileId": case "StoreId": document[fault] = Guid.NewGuid().ToString(); break;
            case "Hash": document[fault] = new string('0', 64); break;
            case "Payload": case "Identity": document[fault] = null; break;
            case "NullNextMoe": document["Identity"]!["NextMoeId"] = null; break;
            default: document[fault] = "unknown"; break;
        }
        File.WriteAllText(path, fault == "Malformed" ? "{broken" : document.ToJsonString()); var before = File.ReadAllBytes(path);
        Assert.Null(await store.ReadAsync(identity, default)); await store.WriteAsync(identity, Snapshot("fresh"), default);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.tmp"));
    }

    [Fact]
    public async Task ValidChecksumDoesNotMakeInvalidRelationPayloadAcceptable()
    {
        using var fixture = new Fixture(); var identity = Identity(); var store = fixture.Store();
        await store.WriteAsync(identity, Snapshot(), default); var path = fixture.PathFor(identity);
        var envelope = JsonNode.Parse(File.ReadAllBytes(path))!;
        var payload = JsonSerializer.SerializeToUtf8Bytes(Snapshot() with { SubjectId = "999" });
        envelope["Payload"] = Convert.ToBase64String(payload); envelope["Hash"] = Convert.ToHexString(SHA256.HashData(payload));
        File.WriteAllText(path, envelope.ToJsonString()); var before = File.ReadAllBytes(path);
        Assert.Null(await store.ReadAsync(identity, default)); await store.WriteAsync(identity, Snapshot(), default);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(PersistenceStage.BeforeTempWrite)][InlineData(PersistenceStage.DuringTempWrite)][InlineData(PersistenceStage.BeforeReplace)]
    public async Task CancellationBeforeAtomicReplacementPreservesCompletePriorFile(PersistenceStage stage)
    {
        using var fixture = new Fixture(); var identity = Identity();
        await fixture.Store().WriteAsync(identity, Snapshot(), default); var before = File.ReadAllBytes(fixture.PathFor(identity));
        using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(reached => { if (reached == stage) cancellation.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(identity, Snapshot("new"), cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.PathFor(identity))); Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.tmp"));
        SameSnapshot(Snapshot(), await fixture.Store().ReadAsync(identity, default));
    }

    [Fact]
    public async Task CancellationAfterReplacementCannotUndoACompleteCommittedValue()
    {
        using var fixture = new Fixture(); var identity = Identity(); using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(stage => { if (stage == PersistenceStage.AfterReplace) cancellation.Cancel(); return Task.CompletedTask; });
        await store.WriteAsync(identity, Snapshot("committed"), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested); SameSnapshot(Snapshot("committed"), await fixture.Store().ReadAsync(identity, default));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.tmp"));
    }

    [Fact]
    public async Task PartialWriteFailureKeepsPriorCacheAndLibraryAndCleansOwnTemporaryFile()
    {
        using var fixture = new Fixture(); var identity = Identity(); await fixture.Store().WriteAsync(identity, Snapshot(), default);
        var before = File.ReadAllBytes(fixture.PathFor(identity)); var library = ProfileFixture.Hashes(fixture.Root);
        var failing = fixture.Store(stage => stage == PersistenceStage.DuringTempWrite ? Task.FromException(new IOException("synthetic failure")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => failing.WriteAsync(identity, Snapshot("new"), default));
        Assert.Equal(before, File.ReadAllBytes(fixture.PathFor(identity))); Assert.Equal(library, ProfileFixture.Hashes(fixture.Root));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.tmp"));
    }

    [Fact]
    public async Task PreCancelledRequestCreatesNoDirectoryAndInvalidIdentityCreatesNoFile()
    {
        using var fixture = new Fixture(); var store = fixture.Store(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(Identity(), Snapshot(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(Identity(), cancellation.Token));
        await store.WriteAsync(Identity() with { SubjectId = "../library" }, Snapshot(), default);
        Assert.Null(await store.ReadAsync(Identity() with { Fingerprint = "invalid" }, default)); Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public async Task PathOnlyConfigurationCannotCreateAnUnboundCache()
    {
        using var fixture = new Fixture(); var store = new IsolatedBangumiRelationsCache(new(fixture.Root));
        await store.WriteAsync(Identity(), Snapshot(), default); Assert.Null(await store.ReadAsync(Identity(), default));
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    // Seed verified public envelopes directly so capacity tests do not repeatedly
    // flush hundreds of synthetic records. Each cloned association has its own key.
    private static void CloneEnvelope(Fixture fixture, byte[] seed, string local, DateTime time)
    {
        var identity = Identity(local); var envelope = JsonNode.Parse(seed)!;
        envelope["Identity"]!["LocalGameId"] = local;
        File.WriteAllText(fixture.PathFor(identity), envelope.ToJsonString()); File.SetLastWriteTimeUtc(fixture.PathFor(identity), time);
    }

    [Fact]
    public async Task FileBoundEvictsOldVerifiedEntriesButPreservesForeignFutureAndUnknownEvidence()
    {
        using var fixture = new Fixture(); var identity = Identity(); await fixture.Store().WriteAsync(identity, Snapshot(), default);
        var seed = File.ReadAllBytes(fixture.PathFor(identity)); File.Delete(fixture.PathFor(identity));
        for (var i = 0; i < IsolatedBangumiRelationsCache.MaximumFiles - 3; i++) CloneEnvelope(fixture, seed, "entry-" + i, DateTime.UnixEpoch.AddSeconds(i));
        var future = JsonNode.Parse(seed)!; future["Version"] = 2; File.WriteAllText(fixture.PathFor(identity), future.ToJsonString());
        var foreignIdentity = Identity("foreign"); var foreign = JsonNode.Parse(seed)!; foreign["ProfileId"] = Guid.NewGuid().ToString();
        File.WriteAllText(fixture.PathFor(foreignIdentity), foreign.ToJsonString()); File.WriteAllText(Path.Combine(fixture.CacheRoot, "evidence.txt"), "preserve");
        var futureBefore = File.ReadAllBytes(fixture.PathFor(identity)); var foreignBefore = File.ReadAllBytes(fixture.PathFor(foreignIdentity));
        await fixture.Store().WriteAsync(Identity("latest"), Snapshot("latest"), default);
        Assert.Equal(IsolatedBangumiRelationsCache.MaximumFiles, Directory.GetFiles(fixture.CacheRoot).Length);
        Assert.Null(await fixture.Store().ReadAsync(Identity("entry-0"), default)); SameSnapshot(Snapshot("latest"), await fixture.Store().ReadAsync(Identity("latest"), default));
        Assert.Equal(futureBefore, File.ReadAllBytes(fixture.PathFor(identity))); Assert.Equal(foreignBefore, File.ReadAllBytes(fixture.PathFor(foreignIdentity)));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.CacheRoot, "evidence.txt")));
    }

    [Fact]
    public async Task FloodedDirectoryIsNotSweptOrExtended()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.CacheRoot);
        for (var i = 0; i <= IsolatedBangumiRelationsCache.MaximumFiles; i++) File.WriteAllText(Path.Combine(fixture.CacheRoot, "foreign-" + i + ".json"), "evidence");
        var before = ProfileFixture.Hashes(fixture.CacheRoot); await fixture.Store().WriteAsync(Identity(), Snapshot(), default);
        Assert.Equal(before, ProfileFixture.Hashes(fixture.CacheRoot)); Assert.Null(await fixture.Store().ReadAsync(Identity(), default));
    }

    [Fact]
    public async Task TotalByteBoundReclaimsOnlyVerifiedEntries()
    {
        using var fixture = new Fixture(); var identity = Identity();
        var large = Snapshot() with { Items = Enumerable.Range(1000, BangumiRelationsService.MaxItems)
            .Select(i => new BangumiRelationItem(i.ToString(), BangumiRelationKind.Book, new('x', 1900), new('y', 1900), "", null)).ToImmutableArray() };
        await fixture.Store().WriteAsync(identity, large, default); var seed = File.ReadAllBytes(fixture.PathFor(identity));
        File.Delete(fixture.PathFor(identity));
        var copies = (int)(IsolatedBangumiRelationsCache.MaximumBytes / seed.Length) + 2;
        Assert.InRange(copies, 2, IsolatedBangumiRelationsCache.MaximumFiles - 1);
        for (var i = 0; i < copies; i++) CloneEnvelope(fixture, seed, "large-" + i, DateTime.UnixEpoch.AddSeconds(i));
        Assert.True(new DirectoryInfo(fixture.CacheRoot).EnumerateFiles().Sum(f => f.Length) > IsolatedBangumiRelationsCache.MaximumBytes);
        await fixture.Store().WriteAsync(identity, Snapshot("fresh"), default);
        Assert.True(new DirectoryInfo(fixture.CacheRoot).EnumerateFiles().Sum(f => f.Length) <= IsolatedBangumiRelationsCache.MaximumBytes);
        Assert.Null(await fixture.Store().ReadAsync(Identity("large-0"), default)); SameSnapshot(Snapshot("fresh"), await fixture.Store().ReadAsync(identity, default));
    }

    [Fact]
    public async Task OversizedPayloadAndUnownedBytePressureCannotOverwriteOrExceedTheBound()
    {
        using var fixture = new Fixture(); var identity = Identity(); await fixture.Store().WriteAsync(identity, Snapshot(), default);
        var before = File.ReadAllBytes(fixture.PathFor(identity));
        var tooLarge = Snapshot() with { Items = Enumerable.Range(1000, BangumiRelationsService.MaxItems)
            .Select(i => new BangumiRelationItem(i.ToString(), BangumiRelationKind.Book, new('文', 2000), new('字', 2000), "", null)).ToImmutableArray() };
        Assert.True(BangumiRelationsService.Valid(identity, tooLarge));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(tooLarge).Length > IsolatedBangumiRelationsCache.MaximumPayloadBytes);
        await fixture.Store().WriteAsync(identity, tooLarge, default); Assert.Equal(before, File.ReadAllBytes(fixture.PathFor(identity)));
        var unknown = Path.Combine(fixture.CacheRoot, "unowned.bin");
        using (var file = new FileStream(unknown, FileMode.CreateNew)) file.SetLength(IsolatedBangumiRelationsCache.MaximumBytes);
        await fixture.Store().WriteAsync(Identity("new"), Snapshot("new"), default);
        Assert.Equal(before, File.ReadAllBytes(fixture.PathFor(identity))); Assert.False(File.Exists(fixture.PathFor(Identity("new"))));
        Assert.Equal(IsolatedBangumiRelationsCache.MaximumBytes, new FileInfo(unknown).Length);
    }
}
