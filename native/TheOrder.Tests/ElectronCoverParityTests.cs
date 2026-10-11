using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class ElectronCoverParityTests
{
    private static readonly WorkCover Default = new("1", "https://images.example.com/default.png", 1200, 800, 0, "safe");
    private static readonly WorkCover Personal = new("2", "https://images.example.com/portrait.png", 600, 900, 200, "safe");
    private static WorkMetadata Artwork() => Work() with { CoverUrl = Default.Url, Covers = [Default, Personal] };
    private sealed class Catalog : IOnlineCatalog
    {
        public WorkMetadata Value = Artwork();
        public Func<CancellationToken, Task>? Before { get; set; }
        public async Task<WorkMetadata> DetailsAsync(string id, bool refresh, CancellationToken c)
        { Assert.True(refresh); if (Before is not null) await Before(c); c.ThrowIfCancellationRequested(); return Value; }
        public Task<OnlinePage> SearchAsync(string q, string? cursor, CancellationToken c) => throw new NotSupportedException();
        public Task TestConnectionAsync(CancellationToken c) => Task.CompletedTask;
        public void ClearCache() { }
    }
    private sealed class Credentials : ICredentialSettings, ICredentialProvider
    {
        public Task<bool> HasCredentialAsync(CancellationToken c) => Task.FromResult(true);
        public Task<string?> ReadAsync(CancellationToken c) => Task.FromResult<string?>("synthetic");
        public Task SaveAsync(string value, CancellationToken c) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken c) => Task.CompletedTask;
    }
    private sealed class Lab : IAsyncDisposable
    {
        public readonly TransactionProfile Profile;
        public readonly Catalog Catalog = new();
        public readonly AuthenticatedCredentials Credentials;
        public readonly OfflineLibraryClient Offline;
        public readonly OnlineCoverSelectionClient Client;
        public TransactionalLibrary Library => Offline.Required();
        public Lab(LibraryEntry? entry = null, ICoverRecommendationSource? recommendations = null)
        {
            var codec = new NativeLibraryCodec();
            Profile = new(codec.Encode([entry ?? Entry() with { Metadata = Artwork() }], Clock));
            Offline = new(new IsolatedLibraryFactory(codec, new(Profile.Root)), new LegacyClientTextRules());
            var credentials = new Credentials(); Credentials = new(credentials, credentials);
            Client = new(Offline, Catalog, Credentials.AuthContext, recommendations);
        }
        public async Task Init() => Assert.Equal(ClientOutcome.Success, (await Offline.LoadAsync(default)).Outcome);
        public async Task<OnlineCoverOptions> Options() => await Client.LoadAsync(await Library.DetailsAsync("a"), default);
        public async ValueTask DisposeAsync() { await Offline.DisposeAsync(); Profile.Dispose(); }
    }

    private sealed class Recommendations : ICoverRecommendationSource
    {
        public CoverRecommendation Value = new("https://images.example/recommended", "signature", true);
        public Task<CoverRecommendation> RecommendationAsync(string id, CancellationToken cancellation)
        { Assert.Equal("200", id); cancellation.ThrowIfCancellationRequested(); return Task.FromResult(Value); }
    }
    [Fact] public async Task AutomaticPreviewUsesDisplayedRecommendationAndKeepsCatalogDefaultSeparate()
    {
        var recommendations = new Recommendations(); await using var lab = new Lab(recommendations: recommendations); await lab.Init();
        var options = await lab.Options(); Assert.Equal(recommendations.Value, options.Recommendation); Assert.Equal(Default.Url, options.Work.CoverUrl);
        Assert.True((await lab.Client.SaveAsync(options, null, default)).IsCommitted);
        Assert.Null(lab.Library.Snapshot.Entries[0].CoverChoice);
        Assert.Equal("自动推荐封面", (await lab.Library.DetailsAsync("a")).CoverDescription);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ChangedOrUnreadyAutomaticPreviewCannotSilentlySaveDifferentRecommendation(bool pending)
    {
        var recommendations = new Recommendations(); await using var lab = new Lab(recommendations: recommendations); await lab.Init();
        var options = await lab.Options(); var bytes = File.ReadAllBytes(lab.Profile.Main);
        recommendations.Value = pending ? new(null, null, false) : recommendations.Value with { Url = "https://images.example/another" };
        Assert.False((await lab.Client.SaveAsync(options, null, default)).IsCommitted); Assert.Equal(bytes, File.ReadAllBytes(lab.Profile.Main));
        Assert.True((await lab.Client.SaveAsync(options, Default.Id, default)).IsCommitted); // pin primary even while auto is pending
        Assert.Equal(Default.Url, lab.Library.Snapshot.Entries[0].CoverChoice!.Cover.Url);
    }

    [Fact]
    public void DefaultSelectionNeverUsesPortraitVotes()
    {
        using var json = JsonDocument.Parse("""
            {"object":"work","id":"200","display_name":"作品","cover":{"id":"1","url":"https://images.example.com/default.png","width":1200,"height":800,"vote_count":0},
             "covers":[{"id":"2","url":"https://images.example.com/portrait.png","width":600,"height":900,"vote_count":200}]}
            """);
        var work = new RemoteWorkDto(json.RootElement).Map(true, DateTimeOffset.UnixEpoch);
        Assert.Equal(Default.Url, work.CoverUrl);
        Assert.Equal(Default.Url, CoverSources.Selected(Entry() with { Metadata = work }));
    }
    [Fact]
    public async Task SelectedCoverPersistsAcrossRefreshAndReopenWithoutAccountingChanges()
    {
        await using var lab = new Lab(Entry() with { Metadata = Artwork(), Notes = "保留笔记", PlayCount = 7, TotalPlaySeconds = 123 });
        await lab.Init(); var options = await lab.Options();
        Assert.True((await lab.Client.SaveAsync(options, Personal.Id, default)).IsCommitted);
        var library = lab.Library; var token = await library.BeginRequestAsync("a");
        await library.CommitLinkAsync("a", Artwork() with { Description = "刷新资料" }, token);
        var entry = library.Snapshot.Entries[0]; Assert.Equal(Personal.Url, CoverSources.Selected(entry));
        Assert.Equal("保留笔记", entry.Notes); Assert.Equal(7, entry.PlayCount); Assert.Equal(123, entry.TotalPlaySeconds);
        Assert.Empty(entry.Sessions); Assert.Null(entry.LocalCover);
        var decoded = new NativeLibraryCodec().Decode(File.ReadAllBytes(lab.Profile.Main));
        Assert.Equal(Personal.Url, CoverSources.Selected(Assert.Single(decoded)));
    }
    [Fact]
    public async Task DefaultClearsBothPersonalChoicesAndUsesActualSourceDefault()
    {
        await using var lab = new Lab(Entry() with { Metadata = Artwork(), CoverChoice = new("200", Personal) });
        await lab.Init(); Assert.True((await lab.Client.SaveAsync(await lab.Options(), null, default)).IsCommitted);
        var entry = Assert.Single(lab.Library.Snapshot.Entries);
        Assert.Null(entry.CoverChoice); Assert.Null(entry.LocalCover); Assert.Equal(Default.Url, CoverSources.Selected(entry));
    }
    [Fact]
    public async Task UnlinkedEntryDoesNotRequestRemoteCovers()
    {
        await using var lab = new Lab(Entry()); await lab.Init();
        await Assert.ThrowsAsync<InvalidDataException>(() => lab.Options());
    }
    [Theory][InlineData("missing")][InlineData("url")][InlineData("permission")][InlineData("work")][InlineData("default")]
    public async Task ChangedRemoteIdentityOrUnlistedSelectionCannotCommit(string change)
    {
        await using var lab = new Lab(); await lab.Init(); var options = await lab.Options();
        lab.Catalog.Value = change switch {
            "missing" => Artwork() with { Covers = [Default] },
            "url" => Artwork() with { Covers = [Default, Personal with { Url = "https://images.example.com/new.png" }] },
            "permission" => Artwork() with { Covers = [Default, Personal with { Sexual = "explicit" }] },
            "work" => Artwork() with { Id = "201" },
            _ => Artwork() with { CoverUrl = Personal.Url }
        };
        var bytes = File.ReadAllBytes(lab.Profile.Main);
        Assert.False((await lab.Client.SaveAsync(options, change == "default" ? null : Personal.Id, default)).IsCommitted);
        Assert.Equal(bytes, File.ReadAllBytes(lab.Profile.Main));
    }
    [Fact]
    public async Task AnArbitraryCoverIdIsNeverAccepted()
    {
        await using var lab = new Lab(); await lab.Init();
        Assert.False((await lab.Client.SaveAsync(await lab.Options(), "999", default)).IsCommitted);
        Assert.Null(lab.Library.Snapshot.Entries[0].CoverChoice);
    }
    [Theory][InlineData("auth")][InlineData("cover")][InlineData("relation")][InlineData("delete")]
    public async Task LateDialogCannotOverwriteNewAuthOrLocalIdentity(string change)
    {
        await using var lab = new Lab(); await lab.Init(); var options = await lab.Options();
        switch (change)
        {
            case "auth": await lab.Credentials.ClearAsync(default); break;
            case "cover": await lab.Library.SaveLocalCoverAsync(options.Token, null); break;
            case "relation": var token = await lab.Library.BeginRequestAsync("a"); await lab.Library.CommitLinkAsync("a", Work("201"), token); break;
            case "delete": await lab.Library.DeleteWishlistAsync("a", default); break;
        }
        // A no-op restore keeps the cover token current; a real selection supersedes it.
        if (change == "cover") await lab.Library.SaveRemoteCoverAsync(options.Token, Artwork(), Default.Id);
        var bytes = File.ReadAllBytes(lab.Profile.Main);
        Assert.False((await lab.Client.SaveAsync(options, Personal.Id, default)).IsCommitted);
        Assert.Equal(bytes, File.ReadAllBytes(lab.Profile.Main));
    }
    [Fact]
    public async Task CancellationKeepsOriginalCoverAndBytes()
    {
        await using var lab = new Lab(); await lab.Init(); var options = await lab.Options();
        using var stop = new CancellationTokenSource(); stop.Cancel(); var bytes = File.ReadAllBytes(lab.Profile.Main);
        Assert.False((await lab.Client.SaveAsync(options, Personal.Id, stop.Token)).IsCommitted);
        Assert.Equal(bytes, File.ReadAllBytes(lab.Profile.Main));
    }
    [Fact]
    public async Task CredentialChangeDuringLoadCannotPublishOldCoverOptions()
    {
        await using var lab = new Lab(); await lab.Init();
        lab.Catalog.Before = _ => lab.Credentials.ClearAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lab.Options());
        Assert.Null(lab.Library.Snapshot.Entries[0].CoverChoice);
    }
    [Fact]
    public async Task ChangingWorksRetiresOnlyRemoteChoice()
    {
        await using var lab = new Lab(Entry() with { Metadata = Artwork(), CoverChoice = new("200", Personal) }); await lab.Init();
        var token = await lab.Library.BeginRequestAsync("a"); await lab.Library.CommitLinkAsync("a", Work("201"), token);
        Assert.Null(lab.Library.Snapshot.Entries[0].CoverChoice);
    }
}
