using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

[Collection("Native profile resource")]
public sealed class BookBibliographyStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T00:00:00Z");
    private static BookIdentity Source(string id = "100") => new("Bangumi", id, BookEntityLevel.Series);
    private static BookPublication Publication(string id = "000000001") => new(id, "合成出版记录", "合成作品", "1",
        [], [], ["合成作者"], ["合成出版社"], [], "2000-01", null, ["jpn"], false, Now);
    private static BookCacheValue Cache(string id = "000000001") => new("publication:" + id, Now, Publication: Publication(id));
    private static BookPublicationBinding Binding(string publication = "000000001", BookIdentity? source = null) =>
        new(source ?? Source(), new('A', 64), publication, Now, "用户核对合成出版记录");
    private static string FileName(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json";
    private sealed class Fixture : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "the-order-book-store-test-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Parent, "profile");
        public string CacheRoot => Path.Combine(Root, IsolatedBookBibliographyStore.CacheDirectoryName);
        public string BindingRoot => Path.Combine(Root, IsolatedBookBibliographyStore.BindingsDirectoryName);
        public NativeProfileStore Profile { get; private set; }
        public Fixture() => Profile = Open();
        private NativeProfileStore Open() => NativeProfileStore.Open(new(NativeProfileKind.Test, Root), new NativeLibraryCodec());
        public void Reopen() { Profile.Dispose(); Profile = Open(); }
        public IsolatedBookBibliographyStore Store(Func<PersistenceStage, Task>? probe = null) => new(Profile.Configuration(), probe);
        public string CachePath(string key) => Path.Combine(CacheRoot, FileName(key));
        public string BindingPath(BookIdentity source) => Path.Combine(BindingRoot, FileName("source:" + source.Key));
        public void Dispose()
        {
            Profile.Dispose();
            var path = Path.GetFullPath(Parent); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!path.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("the-order-book-store-test-", StringComparison.Ordinal)) throw new InvalidOperationException("Fixture escaped temporary root.");
            Directory.Delete(path, true);
        }
    }
    private static void SameCache(BookCacheValue expected, BookCacheValue? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }
    private static void ReplacePayload(string path, byte[] payload)
    {
        var envelope = JsonNode.Parse(File.ReadAllBytes(path))!;
        envelope["Payload"] = Convert.ToBase64String(payload); envelope["Hash"] = Convert.ToHexString(SHA256.HashData(payload));
        File.WriteAllText(path, envelope.ToJsonString());
    }

    [Fact]
    public async Task CacheAndSmallConfirmedMappingsSurviveRestartWithoutChangingLibrary()
    {
        using var fixture = new Fixture(); var store = fixture.Store(); var before = ProfileFixture.Hashes(fixture.Root);
        Assert.Null(await store.ReadAsync(Cache().Key, default)); Assert.Empty(await store.ReadAsync(Source(), default));
        Assert.False(Directory.Exists(fixture.CacheRoot)); Assert.False(Directory.Exists(fixture.BindingRoot));
        await store.WriteAsync(Cache(), default); await store.WriteAsync(Source(), [Binding(), Binding("123456789012")], default);
        var bindingEnvelope = JsonNode.Parse(File.ReadAllBytes(fixture.BindingPath(Source())))!;
        var bindingText = Encoding.UTF8.GetString(Convert.FromBase64String(bindingEnvelope["Payload"]!.GetValue<string>()));
        Assert.DoesNotContain("\"Title\":", bindingText); Assert.DoesNotContain("PublicationDate", bindingText);
        fixture.Reopen(); SameCache(Cache(), await fixture.Store().ReadAsync(Cache().Key, default));
        Assert.Equal(new[] { Binding(), Binding("123456789012") }, await fixture.Store().ReadAsync(Source(), default));
        Assert.Equal(before, ProfileFixture.Hashes(fixture.Root));
    }

    [Fact]
    public async Task ConfirmationsAreScopedByProviderIdAndEntityLevelAndRetainFingerprintEvidence()
    {
        using var fixture = new Fixture(); var store = fixture.Store(); var source = Source();
        await store.WriteAsync(source, [Binding()], default);
        Assert.Empty(await store.ReadAsync(source with { Id = "101" }, default));
        Assert.Empty(await store.ReadAsync(source with { Provider = "AniList" }, default));
        Assert.Empty(await store.ReadAsync(source with { Level = BookEntityLevel.Volume }, default));
        Assert.Equal(new string('A', 64), Assert.Single(await store.ReadAsync(source, default)).SourceFingerprint);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(source, [Binding(source: source with { Id = "101" })], default));
        Assert.Equal(Binding(), Assert.Single(await store.ReadAsync(source, default)));
    }

    [Fact]
    public async Task ForeignProfileCacheIsAMissButForeignConfirmationCannotBeReadOrOverwritten()
    {
        using var first = new Fixture(); using var second = new Fixture();
        await first.Store().WriteAsync(Cache(), default); await first.Store().WriteAsync(Source(), [Binding()], default);
        Directory.CreateDirectory(second.CacheRoot); Directory.CreateDirectory(second.BindingRoot);
        File.Copy(first.CachePath(Cache().Key), second.CachePath(Cache().Key)); File.Copy(first.BindingPath(Source()), second.BindingPath(Source()));
        var cacheBefore = File.ReadAllBytes(second.CachePath(Cache().Key)); var bindingsBefore = File.ReadAllBytes(second.BindingPath(Source()));
        Assert.Null(await second.Store().ReadAsync(Cache().Key, default));
        await Assert.ThrowsAsync<IOException>(() => second.Store().ReadAsync(Source(), default));
        await Assert.ThrowsAsync<IOException>(() => second.Store().WriteAsync(Cache(), default));
        await Assert.ThrowsAsync<IOException>(() => second.Store().WriteAsync(Source(), [], default));
        Assert.Equal(cacheBefore, File.ReadAllBytes(second.CachePath(Cache().Key)));
        Assert.Equal(bindingsBefore, File.ReadAllBytes(second.BindingPath(Source())));
    }

    [Theory]
    [InlineData("Version")][InlineData("StoreId")][InlineData("Hash")][InlineData("UnknownField")]
    [InlineData("Malformed")][InlineData("DuplicateProperty")]
    public async Task DamagedOrFutureConfirmationFailsExplicitlyAndPreservesOriginalBytes(string fault)
    {
        using var fixture = new Fixture(); var store = fixture.Store(); await store.WriteAsync(Source(), [Binding()], default);
        var path = fixture.BindingPath(Source()); var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        switch (fault)
        {
            case "Version": json[fault] = 2; break;
            case "StoreId": json[fault] = Guid.NewGuid().ToString(); break;
            case "Hash": json[fault] = new string('0', 64); break;
            default: json[fault] = "unsupported"; break;
        }
        string text = fault == "Malformed" ? "{broken" : fault == "DuplicateProperty"
            ? File.ReadAllText(path).Replace("\"Version\":1", "\"Version\":2,\"Version\":1", StringComparison.Ordinal) : json.ToJsonString();
        File.WriteAllText(path, text); var before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(Source(), default));
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Source(), [Binding("000000002")], default));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ValidHashCannotAdoptInvalidOrDuplicateNestedMappings(bool duplicateProperty)
    {
        using var fixture = new Fixture(); var store = fixture.Store(); await store.WriteAsync(Source(), [Binding()], default);
        var path = fixture.BindingPath(Source()); var envelope = JsonNode.Parse(File.ReadAllBytes(path))!;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()));
        payload = duplicateProperty ? payload.Replace("\"PublicationId\":\"000000001\"", "\"PublicationId\":\"000000002\",\"PublicationId\":\"000000001\"", StringComparison.Ordinal)
            : payload.Replace("\"PublicationId\":\"000000001\"", "\"PublicationId\":\"../library\"", StringComparison.Ordinal);
        ReplacePayload(path, Encoding.UTF8.GetBytes(payload)); var before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(Source(), default));
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Source(), [], default)); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task CacheValidationRejectsFutureEnvelopeBadHashAndMismatchedPublicationKey()
    {
        using var fixture = new Fixture(); var store = fixture.Store(); var value = Cache(); await store.WriteAsync(value, default);
        var path = fixture.CachePath(value.Key); var original = File.ReadAllBytes(path);
        foreach (var fault in new[] { "Version", "Hash", "WrongPublication" })
        {
            File.WriteAllBytes(path, original);
            if (fault == "WrongPublication") ReplacePayload(path, JsonSerializer.SerializeToUtf8Bytes(value with { Publication = Publication("000000002") }));
            else { var json = JsonNode.Parse(original)!; json[fault] = fault == "Version" ? JsonValue.Create(2) : JsonValue.Create("bad"); File.WriteAllText(path, json.ToJsonString()); }
            var before = File.ReadAllBytes(path); Assert.Null(await store.ReadAsync(value.Key, default));
            await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(value, default)); Assert.Equal(before, File.ReadAllBytes(path));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(value with { Key = "publication:000000002" }, default));
    }

    [Theory][InlineData(PersistenceStage.BeforeTempWrite)][InlineData(PersistenceStage.DuringTempWrite)][InlineData(PersistenceStage.BeforeReplace)]
    public async Task CancelBeforeReplacementRetainsPriorConfirmationAndNoTemporaryDebris(PersistenceStage stage)
    {
        using var fixture = new Fixture(); await fixture.Store().WriteAsync(Source(), [Binding()], default);
        var before = File.ReadAllBytes(fixture.BindingPath(Source())); using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(reached => { if (reached == stage) cancellation.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(Source(), [Binding("000000002")], cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.BindingPath(Source()))); Assert.Empty(Directory.GetFiles(fixture.BindingRoot, "*.tmp"));
        Assert.Equal(Binding(), Assert.Single(await fixture.Store().ReadAsync(Source(), default)));
    }

    [Theory][InlineData(PersistenceStage.Replace)][InlineData(PersistenceStage.AfterReplace)]
    public async Task CancellationOnceReplacementStartsRetainsCompleteCommittedConfirmation(PersistenceStage stage)
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(reached => { if (reached == stage) cancellation.Cancel(); return Task.CompletedTask; });
        await store.WriteAsync(Source(), [Binding()], cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested); fixture.Reopen();
        Assert.Equal(Binding(), Assert.Single(await fixture.Store().ReadAsync(Source(), default)));
    }

    [Fact]
    public async Task AtomicFailureAndUnboundConfigurationCannotReportConfirmationSuccess()
    {
        using var fixture = new Fixture(); await fixture.Store().WriteAsync(Source(), [Binding()], default);
        var before = File.ReadAllBytes(fixture.BindingPath(Source())); var main = ProfileFixture.Hashes(fixture.Root);
        var failing = fixture.Store(stage => stage == PersistenceStage.DuringTempWrite ? Task.FromException(new IOException("synthetic failure")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => failing.WriteAsync(Source(), [Binding("000000002")], default));
        Assert.Equal(before, File.ReadAllBytes(fixture.BindingPath(Source()))); Assert.Empty(Directory.GetFiles(fixture.BindingRoot, "*.tmp"));
        var unbound = new IsolatedBookBibliographyStore(new(fixture.Root));
        Assert.Null(await unbound.ReadAsync(Cache().Key, default));
        await Assert.ThrowsAsync<IOException>(() => unbound.ReadAsync(Source(), default));
        await Assert.ThrowsAsync<IOException>(() => unbound.WriteAsync(Source(), [Binding()], default));
        Assert.Equal(main, ProfileFixture.Hashes(fixture.Root));
    }

    [Fact]
    public async Task ConfirmationLimitAndDuplicateIdsDoNotReplaceExistingMappings()
    {
        using var fixture = new Fixture(); var store = fixture.Store();
        var maximum = Enumerable.Range(1, 64).Select(i => Binding(i.ToString("D9"))).ToArray();
        await store.WriteAsync(Source(), maximum, default); var before = File.ReadAllBytes(fixture.BindingPath(Source()));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(Source(), [.. maximum, Binding("000000100")], default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(Source(), [Binding(), Binding()], default));
        Assert.Equal(before, File.ReadAllBytes(fixture.BindingPath(Source())));
        await store.WriteAsync(Source(), [], default); Assert.Empty(await store.ReadAsync(Source(), default));
    }

    [Fact]
    public async Task DistinctAdaptersSharingTheProfileSerializeAtomicWrites()
    {
        using var fixture = new Fixture(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var secondEntered = false;
        var first = fixture.Store(async stage => { if (stage == PersistenceStage.BeforeReplace) { started.TrySetResult(); await release.Task; } });
        var second = fixture.Store(stage => { if (stage == PersistenceStage.BeforeTempWrite) secondEntered = true; return Task.CompletedTask; });
        var firstWrite = first.WriteAsync(Source(), [Binding()], default); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondWrite = second.WriteAsync(Source(), [Binding("000000002")], default);
        try { Assert.False(secondEntered); } finally { release.TrySetResult(); }
        await Task.WhenAll(firstWrite, secondWrite);
        Assert.True(secondEntered); Assert.Equal(Binding("000000002"), Assert.Single(await second.ReadAsync(Source(), default)));
    }

    private static void SeedCache(Fixture fixture, byte[] template, BookCacheValue value, DateTime stamp)
    {
        var envelope = JsonNode.Parse(template)!; var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        envelope["Key"] = value.Key; envelope["Payload"] = Convert.ToBase64String(payload); envelope["Hash"] = Convert.ToHexString(SHA256.HashData(payload));
        File.WriteAllText(fixture.CachePath(value.Key), envelope.ToJsonString()); File.SetLastWriteTimeUtc(fixture.CachePath(value.Key), stamp);
    }

    [Fact]
    public async Task CacheEvictionCannotDeleteConfirmedPublicationMappings()
    {
        using var fixture = new Fixture(); var store = fixture.Store(); await store.WriteAsync(Cache(), default);
        await store.WriteAsync(Source(), [Binding()], default); var bindingBefore = File.ReadAllBytes(fixture.BindingPath(Source()));
        var seed = File.ReadAllBytes(fixture.CachePath(Cache().Key));
        for (var i = 1; i <= IsolatedBookBibliographyStore.MaximumFiles; i++) SeedCache(fixture, seed, Cache(i.ToString("D9")), DateTime.UnixEpoch.AddSeconds(i));
        await store.WriteAsync(Cache("000000999"), default);
        Assert.Equal(IsolatedBookBibliographyStore.MaximumFiles, Directory.GetFiles(fixture.CacheRoot).Length);
        Assert.Null(await store.ReadAsync(Cache().Key, default)); SameCache(Cache("000000999"), await store.ReadAsync(Cache("000000999").Key, default));
        Assert.Equal(bindingBefore, File.ReadAllBytes(fixture.BindingPath(Source()))); fixture.Reopen();
        Assert.Equal(Binding(), Assert.Single(await fixture.Store().ReadAsync(Source(), default)));
    }

    [Fact]
    public async Task CacheTotalBytesAreBoundedWhileUnknownAndFutureEvidenceIsPreserved()
    {
        using var fixture = new Fixture(); var store = fixture.Store(); await store.WriteAsync(Cache(), default);
        var seed = File.ReadAllBytes(fixture.CachePath(Cache().Key)); File.Delete(fixture.CachePath(Cache().Key));
        var page = new BookSearchPage(Enumerable.Range(1, 20).Select(i => Publication(i.ToString("D9")) with
            { CreatorStatements = Enumerable.Repeat(new string('x', 1900), 16).ToArray() }).ToArray(), 20, null, Now);
        var first = new BookCacheValue(new BookSearchQuery("large-0").Key, Now, Search: page);
        SeedCache(fixture, seed, first, DateTime.UnixEpoch); var size = new FileInfo(fixture.CachePath(first.Key)).Length;
        Assert.InRange(size, 500000, IsolatedBookBibliographyStore.MaximumFileBytes);
        var count = (int)(IsolatedBookBibliographyStore.MaximumBytes / size);
        for (var i = 1; i < count; i++) SeedCache(fixture, seed, first with { Key = new BookSearchQuery("large-" + i).Key }, DateTime.UnixEpoch.AddSeconds(i));
        File.WriteAllText(Path.Combine(fixture.CacheRoot, "unknown.txt"), "preserve");
        var future = JsonNode.Parse(seed)!; future["Version"] = 2; File.WriteAllText(fixture.CachePath(Cache().Key), future.ToJsonString());
        var futureBefore = File.ReadAllBytes(fixture.CachePath(Cache().Key));
        var next = first with { Key = new BookSearchQuery("new-large").Key }; await store.WriteAsync(next, default);
        Assert.True(new DirectoryInfo(fixture.CacheRoot).EnumerateFiles().Sum(f => f.Length) <= IsolatedBookBibliographyStore.MaximumBytes);
        Assert.Null(await store.ReadAsync(first.Key, default)); SameCache(next, await store.ReadAsync(next.Key, default));
        Assert.Equal(futureBefore, File.ReadAllBytes(fixture.CachePath(Cache().Key))); Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.CacheRoot, "unknown.txt")));
    }

    [Fact]
    public async Task OversizedCacheFileAndFloodedDirectoryArePreservedWithoutSweeping()
    {
        using var fixture = new Fixture(); var store = fixture.Store(); Directory.CreateDirectory(fixture.CacheRoot);
        var path = fixture.CachePath(Cache().Key); using (var file = new FileStream(path, FileMode.CreateNew)) file.SetLength(IsolatedBookBibliographyStore.MaximumFileBytes + 1);
        Assert.Null(await store.ReadAsync(Cache().Key, default)); await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Cache(), default));
        Assert.Equal(IsolatedBookBibliographyStore.MaximumFileBytes + 1, new FileInfo(path).Length);
        for (var i = 0; i < IsolatedBookBibliographyStore.MaximumFiles; i++) File.WriteAllText(Path.Combine(fixture.CacheRoot, "unknown-" + i), "evidence");
        var before = ProfileFixture.Hashes(fixture.CacheRoot);
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Cache("000000002"), default));
        Assert.Equal(before, ProfileFixture.Hashes(fixture.CacheRoot));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ReparseDirectoryCannotRedirectCacheOrConfirmationWrites(bool confirmation)
    {
        using var fixture = new Fixture(); var target = Path.Combine(fixture.Parent, "outside-profile"); Directory.CreateDirectory(target);
        var link = confirmation ? fixture.BindingRoot : fixture.CacheRoot;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
        using var process = Process.Start(start)!; Assert.True(process.WaitForExit(15000)); Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            var store = fixture.Store();
            if (confirmation)
            {
                await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(Source(), default));
                await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Source(), [Binding()], default));
            }
            else
            {
                Assert.Null(await store.ReadAsync(Cache().Key, default));
                await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Cache(), default));
            }
            Assert.Empty(Directory.GetFiles(target));
        }
        finally { Directory.Delete(link); }
    }
}
