using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Presentation;
using Xunit;

namespace TheOrder.Tests;

public sealed class MusicBrainzEntityTests
{
    private const string Work = "423cadec-2e5b-4569-9855-6148de881084", Artist = "9782d6e1-9a4e-4013-b60b-3e050b03a067";
    private static readonly MusicBrainzReference WorkRef = new(MusicBrainzEntityKind.Work, Work);
    private static readonly MusicBrainzReference ArtistRef = new(MusicBrainzEntityKind.Artist, Artist);
    private static JsonObject WorkJson() => JsonNode.Parse("""
    {"id":"423cadec-2e5b-4569-9855-6148de881084","title":"汐","type":null,"languages":["zxx"],"iswcs":["T-123.456.789-0"],
    "aliases":[{"name":"Ushio","locale":"en"}],"relations":[
    {"type":"composer","direction":"backward","target-type":"artist","attributes":[],"artist":{"id":"9782d6e1-9a4e-4013-b60b-3e050b03a067","name":"戸越まごめ"}},
    {"type":"performance","direction":"backward","target-type":"recording","attributes":["live"],"begin":"2004-08","recording":{"id":"077d765e-62d5-42f6-8560-0246210b911f","title":"汐"}}]}
    """)!.AsObject();
    private static string ArtistJson => """
    {"id":"9782d6e1-9a4e-4013-b60b-3e050b03a067","name":"戸越まごめ","type":"Person","sort-name":"Togoshi, Magome","country":"JP","gender":"Male","area":{"name":"Japan"},"life-span":{"begin":"1971-03-25","end":null},"aliases":[{"name":"Magome Togoshi"}],"relations":[]}
    """;
    private static MusicBrainzEntityDetail Map(JsonObject node, MusicBrainzReference? reference = null)
    { using var document = JsonDocument.Parse(node.ToJsonString()); return MusicBrainzProvider.MapEntity(reference ?? WorkRef, document.RootElement, MusicTestData.Stamp); }
    private static MusicBrainzEntityResult Result(MusicBrainzReference? reference = null)
    {
        var detail = new MusicBrainzEntityDetail(reference ?? WorkRef, "合成作品", null, null, [], [], [], false, MusicTestData.Stamp);
        return new(detail, false, false, null);
    }

    [Fact] public void WorkKeepsArtistAndRecordingIdentitiesDirectionsAndPartialDates()
    {
        var detail = Map(WorkJson());
        Assert.Equal(WorkRef, detail.Reference); Assert.Equal("汐", detail.Title); Assert.Equal("Ushio", Assert.Single(detail.Aliases));
        Assert.Contains(detail.Facts, x => x.Label == "语言" && x.Value == "zxx");
        Assert.Equal(ArtistRef, detail.Relations[0].Target); Assert.Equal("backward", detail.Relations[0].Direction);
        Assert.Equal("composer", detail.Relations[0].RelationType);
        Assert.Equal(MusicBrainzEntityKind.Recording, detail.Relations[1].Target.Kind);
        Assert.Equal("2004-08", detail.Relations[1].Begin); Assert.Equal("live", Assert.Single(detail.Relations[1].Attributes));
    }
    [Fact] public void ArtistUsesOwnNameIdentityAndStructuredDates()
    {
        using var document = JsonDocument.Parse(ArtistJson);
        var detail = MusicBrainzProvider.MapEntity(ArtistRef, document.RootElement, MusicTestData.Stamp);
        Assert.Equal("戸越まごめ", detail.Title); Assert.Equal("Person", detail.EntityType);
        Assert.Contains(detail.Facts, x => x.Label == "起始日期" && x.Value == "1971-03-25");
        Assert.Contains(detail.Facts, x => x.Label == "地区" && x.Value == "Japan");
    }
    [Fact] public void RecordingKeepsLengthArtistAndIsrcWithoutClaimingWorkIdentity()
    {
        using var document = JsonDocument.Parse("""{"id":"077d765e-62d5-42f6-8560-0246210b911f","title":"汐","length":68000,"artist-credit":[{"name":"戸越まごめ"}],"isrcs":["JPKSY0400012"],"first-release-date":"2004-08-13"}""");
        var detail = MusicBrainzProvider.MapEntity(new(MusicBrainzEntityKind.Recording, MusicTestData.Recording), document.RootElement, MusicTestData.Stamp);
        Assert.Contains(detail.Facts, x => x.Label == "时长" && x.Value == "1:08");
        Assert.Contains(detail.Facts, x => x.Label == "ISRC" && x.Value == "JPKSY0400012");
        Assert.Empty(detail.Relations); Assert.Equal(MusicBrainzEntityKind.Recording, detail.Reference.Kind);
    }
    [Theory] [InlineData(MusicBrainzEntityKind.Release)] [InlineData(MusicBrainzEntityKind.ReleaseGroup)] [InlineData((MusicBrainzEntityKind)99)]
    public async Task AlbumOrInvalidKindsCannotEnterTheEntityReader(MusicBrainzEntityKind kind)
    {
        using var http = new HttpClient(new MusicTestData.Handler((_, _) => throw new Exception("Must not request")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new MusicBrainzProvider(http).LoadAsync(new(kind, Work), false, default));
    }
    [Theory] [InlineData("bad-id")] [InlineData(Artist)]
    public void WrongRootIdentityIsRejected(string id)
    { var data = WorkJson(); data["id"] = id; Assert.Throws<MusicBrainzException>(() => Map(data)); }
    [Theory] [InlineData("id")] [InlineData("title")]
    public void PartialTargetIdentityCannotBeFilledFromParent(string field)
    {
        var data = WorkJson(); var target = data["relations"]![1]!["recording"]!.AsObject(); target.Remove(field);
        Assert.Throws<MusicBrainzException>(() => Map(data));
    }
    [Fact] public void UnknownRelationTypeIsKeptRatherThanInferred()
    {
        var data = WorkJson(); data["relations"]![0]!["type"] = "unknown future relationship";
        Assert.Equal("unknown future relationship", Map(data).Relations[0].RelationType);
    }
    [Fact] public void RelationCapIsExplicitAndUnsupportedTargetsAreNotInternalLinks()
    {
        var data = WorkJson(); var rows = data["relations"]!.AsArray();
        for (var i = 0; i < 260; i++) rows.Add(rows[0]!.DeepClone());
        rows.Add(JsonNode.Parse("""{"target-type":"url","url":{"resource":"https://example.invalid"}}"""));
        var detail = Map(data); Assert.Equal(256, detail.Relations.Length); Assert.True(detail.RelationsTruncated);
    }
    [Fact] public void InvalidRelationDatesAndMissingDirectionsRejectTheSnapshot()
    {
        var data = WorkJson(); data["relations"]![0]!["begin"] = "2004-99";
        Assert.Throws<MusicBrainzException>(() => Map(data));
        data = WorkJson(); data["relations"]![0]!.AsObject().Remove("direction"); Assert.Throws<MusicBrainzException>(() => Map(data));
    }
    [Fact] public async Task RepeatedReadUsesBoundedSessionCacheAndRefreshUsesSharedTransport()
    {
        var clock = new MusicTestData.Clock();
        var handler = new MusicTestData.Handler((_, _) => Task.FromResult(MusicTestData.Response(WorkJson().ToJsonString())));
        using var http = new HttpClient(handler); var provider = new MusicBrainzProvider(http, clock.Rate(), clock: clock.GetUtcNow);
        Assert.False((await provider.LoadAsync(WorkRef, false, default)).FromCache);
        Assert.True((await provider.LoadAsync(WorkRef, false, default)).FromCache);
        Assert.False((await provider.LoadAsync(WorkRef, true, default)).FromCache);
        Assert.Equal(2, handler.Urls.Count); Assert.All(handler.Urls, x => Assert.StartsWith("https://musicbrainz.org/ws/2/work/" + Work, x));
        Assert.True(clock.GetTimestamp() >= 1100);
    }
    [Fact] public async Task MutatingReturnedArraysCannotChangeCachedEntityFactsOrRelations()
    {
        var clock = new MusicTestData.Clock();
        var handler = new MusicTestData.Handler((_, _) => Task.FromResult(MusicTestData.Response(WorkJson().ToJsonString())));
        using var http = new HttpClient(handler); var provider = new MusicBrainzProvider(http, clock.Rate(), clock: clock.GetUtcNow);
        void Corrupt(MusicBrainzEntityDetail detail)
        {
            detail.Aliases[0] = "Invented alias";
            detail.Facts[0] = new("语言", "invented language");
            detail.Relations[0] = detail.Relations[0] with { Target = new(MusicBrainzEntityKind.Artist, MusicTestData.Other), Title = "Wrong person" };
            detail.Relations[1].Attributes[0] = "invented attribute";
            Assert.True(MusicBrainzEntityValidation.Detail(detail)); // Validation cannot detect otherwise plausible forged facts.
        }
        var network = await provider.LoadAsync(WorkRef, false, default); Corrupt(network.Detail);
        var cached = await provider.LoadAsync(WorkRef, false, default);
        Assert.True(cached.FromCache); Assert.Equal("Ushio", cached.Detail.Aliases[0]);
        Assert.Equal("zxx", cached.Detail.Facts[0].Value); Assert.Equal(ArtistRef, cached.Detail.Relations[0].Target);
        Assert.Equal("live", cached.Detail.Relations[1].Attributes[0]); Corrupt(cached.Detail);
        var again = await provider.LoadAsync(WorkRef, false, default);
        Assert.Equal("Ushio", again.Detail.Aliases[0]); Assert.Equal("zxx", again.Detail.Facts[0].Value);
        Assert.Equal(ArtistRef, again.Detail.Relations[0].Target); Assert.Equal("live", again.Detail.Relations[1].Attributes[0]);
        Assert.Single(handler.Urls);
    }
    [Fact] public async Task OfflineFallbackAlsoReturnsAnIndependentSnapshot()
    {
        var clock = new MusicTestData.Clock(); bool offline = false;
        var handler = new MusicTestData.Handler((_, _) => offline ? throw new HttpRequestException() : Task.FromResult(MusicTestData.Response(WorkJson().ToJsonString())));
        using var http = new HttpClient(handler); var provider = new MusicBrainzProvider(http, clock.Rate(), clock: clock.GetUtcNow);
        await provider.LoadAsync(WorkRef, false, default); clock.Advance(TimeSpan.FromDays(2)); offline = true;
        var stale = await provider.LoadAsync(WorkRef, false, default); Assert.True(stale.IsStale);
        stale.Detail.Aliases[0] = "Invented alias"; stale.Detail.Relations[1].Attributes[0] = "fake";
        var again = await provider.LoadAsync(WorkRef, false, default);
        Assert.Equal("Ushio", again.Detail.Aliases[0]); Assert.Equal("live", again.Detail.Relations[1].Attributes[0]);
    }
    [Fact] public async Task TransientFailureCanUseOldSnapshotButNotPastFourteenDays()
    {
        var clock = new MusicTestData.Clock(); bool offline = false;
        var handler = new MusicTestData.Handler((_, _) => offline ? throw new HttpRequestException() : Task.FromResult(MusicTestData.Response(WorkJson().ToJsonString())));
        using var http = new HttpClient(handler); var provider = new MusicBrainzProvider(http, clock.Rate(), clock: clock.GetUtcNow);
        await provider.LoadAsync(WorkRef, false, default); offline = true; clock.Advance(TimeSpan.FromDays(2));
        Assert.True((await provider.LoadAsync(WorkRef, false, default)).IsStale);
        clock.Advance(TimeSpan.FromDays(13));
        await Assert.ThrowsAsync<MusicBrainzException>(() => provider.LoadAsync(WorkRef, false, default));
    }
    [Fact] public async Task PermanentDenialInvalidatesCacheAndCannotBeRevivedByLaterOfflineFailure()
    {
        var clock = new MusicTestData.Clock(); var stage = 0;
        var handler = new MusicTestData.Handler((_, _) => stage switch
        { 0 => Task.FromResult(MusicTestData.Response(WorkJson().ToJsonString())), 1 => Task.FromResult(MusicTestData.Response("{}", HttpStatusCode.NotFound)), _ => throw new HttpRequestException() });
        using var http = new HttpClient(handler); var provider = new MusicBrainzProvider(http, clock.Rate(), clock: clock.GetUtcNow);
        await provider.LoadAsync(WorkRef, false, default); stage = 1;
        await Assert.ThrowsAsync<MusicBrainzException>(() => provider.LoadAsync(WorkRef, true, default)); stage = 2;
        await Assert.ThrowsAsync<MusicBrainzException>(() => provider.LoadAsync(WorkRef, false, default));
    }
    [Fact] public async Task FutureClockCacheIsRejectedAndCancelledRequestDoesNotRead()
    {
        var clock = new MusicTestData.Clock(); bool offline = false;
        var handler = new MusicTestData.Handler((_, _) => offline ? throw new HttpRequestException() : Task.FromResult(MusicTestData.Response(WorkJson().ToJsonString())));
        using var http = new HttpClient(handler); var provider = new MusicBrainzProvider(http, clock.Rate(), clock: clock.GetUtcNow);
        await provider.LoadAsync(WorkRef, false, default); offline = true; clock.Advance(TimeSpan.FromDays(-1));
        await Assert.ThrowsAsync<MusicBrainzException>(() => provider.LoadAsync(WorkRef, false, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.LoadAsync(WorkRef, false, cancel.Token)); Assert.Equal(2, handler.Urls.Count);
    }
    [Fact] public async Task EntityCacheEvictsBeyondThirtyTwoEntries()
    {
        var clock = new MusicTestData.Clock();
        var handler = new MusicTestData.Handler((request, _) =>
        {
            var data = WorkJson(); data["id"] = request.RequestUri!.Segments.Last();
            return Task.FromResult(MusicTestData.Response(data.ToJsonString()));
        });
        using var http = new HttpClient(handler); var provider = new MusicBrainzProvider(http, clock.Rate(), clock: clock.GetUtcNow);
        await provider.LoadAsync(WorkRef, false, default);
        for (var i = 1; i <= 32; i++) await provider.LoadAsync(new(MusicBrainzEntityKind.Work, "00000000-0000-0000-0000-" + i.ToString("D12")), false, default);
        Assert.False((await provider.LoadAsync(WorkRef, false, default)).FromCache); Assert.Equal(34, handler.Urls.Count);
    }
    private sealed class Reader(Func<MusicBrainzReference, bool, CancellationToken, Task<MusicBrainzEntityResult>> read) : IMusicBrainzEntityReader
    { public Task<MusicBrainzEntityResult> LoadAsync(MusicBrainzReference reference, bool refresh, CancellationToken cancellation) => read(reference, refresh, cancellation); }
    [Fact] public async Task ViewModelPageSwitchAndRestoreRejectLateResultsWithoutAnotherRequest()
    {
        var pending = new TaskCompletionSource<MusicBrainzEntityResult>(); var count = 0;
        using var model = new MusicBrainzEntityViewModel(new Reader((reference, _, _) => ++count == 1 ? pending.Task : Task.FromResult(Result(reference))));
        var first = model.OpenAsync(WorkRef); await model.OpenAsync(ArtistRef); var version = model.Version;
        pending.SetResult(Result()); await first;
        Assert.Equal(ArtistRef, model.Reference); Assert.Equal(ArtistRef, model.Detail!.Reference);
        model.Restore(Result()); Assert.True(model.Version > version); Assert.Equal(WorkRef, model.Reference); Assert.Equal(2, count);
    }
    [Fact] public async Task ViewModelCancelAndDeactivateRejectUncooperativeReader()
    {
        var pending = new TaskCompletionSource<MusicBrainzEntityResult>();
        using var model = new MusicBrainzEntityViewModel(new Reader((_, _, _) => pending.Task));
        var first = model.OpenAsync(WorkRef); model.Cancel(); pending.SetResult(Result()); await first;
        Assert.Null(model.Detail); Assert.Equal(MusicBrainzEntityState.Cancelled, model.State);
        pending = new(); first = model.OpenAsync(WorkRef); model.Deactivate(); pending.SetResult(Result()); await first;
        Assert.Null(model.Detail); Assert.Equal(MusicBrainzEntityState.Idle, model.State); Assert.False(model.CanLoad);
    }
    [Fact] public async Task ViewModelRejectsWrongKindEvenWhenUuidMatches()
    {
        using var model = new MusicBrainzEntityViewModel(new Reader((_, _, _) => Task.FromResult(Result(new(MusicBrainzEntityKind.Artist, Work)))));
        await model.OpenAsync(WorkRef); Assert.Equal(MusicBrainzEntityState.Failed, model.State); Assert.Null(model.Detail);
    }
    [Fact] public async Task ViewModelPermanentFailureClearsPreviouslyDisplayedSnapshot()
    {
        using var model = new MusicBrainzEntityViewModel(new Reader((_, _, _) => throw new MusicBrainzException(MusicBrainzFailure.Unavailable, "unavailable")));
        model.Restore(Result()); await model.RefreshAsync(); Assert.Null(model.Detail); Assert.Equal(MusicBrainzEntityState.Failed, model.State);
    }
    [Fact] public async Task ExternalCancellationCannotLeaveModelLoadingWhenReaderIgnoresToken()
    {
        var pending = new TaskCompletionSource<MusicBrainzEntityResult>(); using var cancellation = new CancellationTokenSource();
        using var model = new MusicBrainzEntityViewModel(new Reader((_, _, _) => pending.Task));
        var task = model.OpenAsync(WorkRef, cancellation.Token); cancellation.Cancel(); pending.SetResult(Result()); await task;
        Assert.Equal(MusicBrainzEntityState.Cancelled, model.State); Assert.False(model.IsBusy); Assert.Null(model.Detail);
    }
}
