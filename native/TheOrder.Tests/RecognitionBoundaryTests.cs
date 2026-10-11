using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Recognition;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class RecognitionBoundaryTests
{
    internal const string Clock = "2026-10-02T00:00:00.000Z";
    internal static LibraryEntry Entry(string name = "星空の航路", string? folder = null) => new("a", name, false, null,
        folder is null ? [] : [new("i", folder, Path.Combine(folder, "Game.exe"), [])], folder is null ? null : "i", "planned", null, false, [], [], "", 0, 0, [], Clock, null, null, null, new(false, null));
    internal static WorkMetadata Work(string id = "200", string title = "星空の航路") => new(id, title, title, [], null, null, null, null, "", null, null, [], [], [], [], [], null, null, Clock);
    internal static RecognitionEvidence Evidence(params LocalTitleEvidence[] titles) => new([.. titles], [], [], [], [], false, []);
    internal static byte[] Gzip(string json) { using var output = new MemoryStream(); using (var zip = new GZipStream(output, CompressionLevel.Fastest, true)) zip.Write(Encoding.UTF8.GetBytes(json)); return output.ToArray(); }
    [Fact] public async Task DistributedIndexLoadsLazilyAndRemainsBounded()
    {
        int opens = 0;
        var index = new TitleIndex(() => { opens++; return File.OpenRead(Path.Combine(ReferenceSources.Repository(), "data/title-index.json.gz")); });
        Assert.Equal(0, opens); Assert.Equal(0, index.RecordCount);
        await index.FindAsync([], default);
        Assert.Equal(56064, index.RecordCount); Assert.Equal(97038, index.TitleCount); Assert.Equal(1, opens);
        await index.FindAsync(["invalid unmatched synthetic title"], default); Assert.Equal(1, opens);
    }
    [Fact] public async Task CancelledIndexLoadCanRetryWithoutPublishingPartialRows()
    {
        var bytes = Gzip("{\"version\":1,\"records\":[{\"names\":[\"作品甲\"],\"references\":[\"vndb:v1\"]}]}");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var index = new TitleIndex(() => new MemoryStream(bytes));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.FindAsync(["作品甲"], cancelled.Token)); Assert.Equal(0, index.RecordCount);
        Assert.Single(await index.FindAsync(["作品甲"], default));
    }
    [Fact] public async Task IndexCollisionDoesNotAuthorizeReferenceBridge()
    {
        var bytes = Gzip("{\"version\":1,\"records\":[{\"names\":[\"星空の航路\"],\"references\":[\"vndb:v1\"]},{\"names\":[\"星空の航路\"],\"references\":[\"vndb:v2\"]}]}");
        var identity = await RecognitionIdentityBuilder.BuildAsync(Entry(), Evidence(new LocalTitleEvidence("星空の航路", "folder")), new TitleIndex(() => new MemoryStream(bytes)), default);
        Assert.Equal(2, identity.LocalCandidates.Length); Assert.Empty(identity.Evidence.IndexProofs); Assert.Empty(identity.References);
    }
    [Fact] public async Task ExpansionBombIsRejectedAndNeverPublished()
    {
        var bytes = Gzip(new string(' ', 32 * 1024 * 1024 + 1)); var index = new TitleIndex(() => new MemoryStream(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => index.FindAsync([], default)); Assert.Equal(0, index.RecordCount);
    }
    [Fact] public async Task IndexCapsCandidateFanout()
    {
        var bytes = Gzip(JsonSerializer.Serialize(new { version = 1, records = Enumerable.Range(1, 1000).Select(i => new { names = new[] { "作品甲" }, references = new[] { "vndb:v" + (i <= 128 ? 1 : 2) } }) }));
        var index = new TitleIndex(() => new MemoryStream(bytes)); var result = await index.FindAsync(["作品甲"], default);
        Assert.Equal(128, result.Length); Assert.Equal(Enumerable.Range(0, 128), result.Select(c => c.Row));
        var identity = await RecognitionIdentityBuilder.BuildAsync(Entry("作品甲"), Evidence(new LocalTitleEvidence("作品甲", "folder")), index, default);
        Assert.Empty(identity.Evidence.IndexProofs); Assert.Empty(identity.References); Assert.NotEmpty(identity.Evidence.Warnings);
    }
    [Fact] public async Task SnapshotRejectsAbaAndNewRequestsButPermitsPersonalNotes()
    {
        var codec = new LegacyLibraryCodec(); using var profile = new TransactionProfile(codec.Encode([Entry()], Clock));
        using var library = profile.Open(); var a = await library.CaptureRecognitionAsync("a");
        await library.UpdateAsync("a", new() { Notes = "keep latest", Rating = 8 }); Assert.True(await library.IsRecognitionCurrentAsync(a));
        var token = await library.BeginRequestAsync("a"); Assert.False(await library.IsRecognitionCurrentAsync(a));
        await library.FinishRequestAsync(token);
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.BeginRecognitionCommitAsync(a));
        var b = await library.CaptureRecognitionAsync("a"); await library.DeleteAsync("a"); await library.ImportAsync(codec.Encode([Entry()], Clock));
        Assert.False(await library.IsRecognitionCurrentAsync(b));
    }
    [Fact] public async Task RecognitionCommitIsAtomicWithSnapshotValidationAndPreservesPersonalData()
    {
        var codec = new LegacyLibraryCodec(); using var profile = new TransactionProfile(codec.Encode([Entry()], Clock)); using var library = profile.Open();
        var snapshot = await library.CaptureRecognitionAsync("a"); await library.UpdateAsync("a", new() { Notes = "later note", Favorite = true });
        var token = await library.BeginRecognitionCommitAsync(snapshot); await library.CommitLinkAsync("a", Work(), token, preserveName: true);
        Assert.Equal("later note", library.Snapshot.Entries[0].Notes); Assert.True(library.Snapshot.Entries[0].Favorite);
        await Assert.ThrowsAsync<StaleLibraryRequestException>(() => library.BeginRecognitionCommitAsync(snapshot));
    }
    [Theory][InlineData("vndb:001")][InlineData("steam:480")][InlineData("steam:v123")][InlineData("vndb:r42")]
    public void InvalidExternalReferencesNeverGainAuthority(string input) => Assert.Null(LocalEvidenceParser.NormalizeReference(input));
    [Fact] public void LegacySidecarIsWeakEvidenceAndNeverCopiesPersonalFields()
    {
        var result = new LocalEvidenceParser().Parse("sidecar", "meta.json", Encoding.UTF8.GetBytes("{\"Game\":{\"Name\":{\"Value\":\"星空の航路\"},\"Ids\":[\"123\",\"456\",\"vndb:v123,bgm:456,steam:480\",null,null,null,null,\"789\"],\"Comment\":\"private-note\"}}"), false);
        Assert.Equal(new[] { "vndb:v123", "bangumi:456", "steam:789" }, result.References.Select(r => r.Value));
        Assert.All(result.Titles, t => Assert.Equal("sidecar", t.Source)); Assert.DoesNotContain("private-note", JsonSerializer.Serialize(result));
    }
    [Theory][InlineData(65001)][InlineData(932)][InlineData(1200)][InlineData(1201)]
    public void BoundedManualEncodingPreservesJapaneseTitle(int page)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(page); string header = page == 932 ? "<meta charset='Shift_JIS'>" : "";
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(header + "<title>星空の航路 - WEB Manual</title>")];
        var result = new LocalEvidenceParser().Parse("manual", "manual.html", bytes, false);
        Assert.Single(result.Titles); Assert.Equal("星空の航路", result.Titles[0].Value);
    }
    [Fact] public void ScriptsLinksAndGenericHeadingsNeverBecomeTitles()
    {
        var result = new LocalEvidenceParser().Parse("manual", "manual.html", Encoding.UTF8.GetBytes("<script><title>诱导执行命令</title></script><a href='x'>其他作品</a><h1>インストール方法</h1><title>Manual</title>"), false);
        Assert.Empty(result.Titles);
    }
    [Fact] public async Task LocalFilesAreBoundedAndChangesInvalidateEvidence()
    {
        using var directory = new TransactionProfile(); string folder = Path.Combine(directory.Root, "星空の航路"); Directory.CreateDirectory(folder);
        var reader = new WindowsRecognitionEvidence(new LocalEvidenceParser(), RecognitionFingerprint.Evidence, new ExecutableEvidenceParser());
        File.WriteAllText(Path.Combine(folder, "game.ini"), "Title=星空の航路\nBrand=青空软件");
        var first = await reader.ReadAsync(Entry(folder: folder), false, default);
        Assert.Contains(first.Evidence.Titles, t => t.Source == "config-title" && t.Value == "星空の航路");
        Assert.Equal(first.Fingerprint, (await reader.ReadAsync(Entry(folder: folder), false, default)).Fingerprint);
        File.WriteAllText(Path.Combine(folder, "game.ini"), "Title=星空の航路 FD");
        var changed = await reader.ReadAsync(Entry(folder: folder), false, default); Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        await File.WriteAllBytesAsync(Path.Combine(folder, "game.ini"), new byte[128 * 1024 + 1]);
        Assert.DoesNotContain((await reader.ReadAsync(Entry(folder: folder), false, default)).Evidence.Titles, t => t.Source == "config-title");
    }
}
