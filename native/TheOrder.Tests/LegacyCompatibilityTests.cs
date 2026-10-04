using System.Text;
using System.Text.Json.Nodes;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class LegacyCompatibilityTests
{
    private static string Fixture(string name = "normal") => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    private static byte[] Bytes(string name = "normal") => File.ReadAllBytes(Path.Combine(Fixture(name), "library.json"));
    private static JsonObject Document() => JsonNode.Parse(Bytes())!.AsObject();
    private static byte[] Encode(JsonNode json) => Encoding.UTF8.GetBytes(json.ToJsonString());

    [Fact] public void MapsAllImportantSemantics()
    {
        var games = LegacyLibraryReader.Parse(Bytes()); Assert.Equal(3, games.Length);
        var game = games[0]; Assert.Equal("我的自定义名称 · 星海", game.Name); Assert.True(game.NameIsCustom);
        Assert.Equal(0d, game.PersonalRating); Assert.Null(games[1].PersonalRating);
        Assert.Null(games[1].NameIsCustom); Assert.False(games[2].NameIsCustom);
        Assert.Equal("18446744073709551616", game.Metadata!.Id);
        Assert.Equal(2, game.Installations.Length); Assert.Equal("空 格", game.Installations[0].Arguments[1]);
        Assert.Null(game.Installations[1].Executable); Assert.Equal("install-a", game.PreferredInstallationId);
        Assert.Equal(7200, game.TotalPlaySeconds); Assert.Equal(600, game.Sessions.Sum(s => s.Seconds));
        Assert.False(game.CoverDisplay.IsSpecified); Assert.True(games[1].CoverDisplay.IsSpecified); Assert.Null(games[1].CoverDisplay.Mode);
        Assert.Equal("crop", games[2].CoverDisplay.Mode); Assert.Null(game.Metadata.Covers); Assert.Null(game.Metadata.TitleEvidence);
        Assert.StartsWith("data:image/jpeg;base64,", games[1].LocalCover!.DataUrl);
        Assert.Equal(4, games[1].LocalCover!.Width); Assert.Null(games[1].Metadata);
        Assert.Equal("虚构笔记", game.Notes); Assert.Equal("合成资料", Assert.Single(game.Categories)); Assert.Equal(3, game.PlayCount);
    }
    [Fact] public void EmptyIsSuccess() => Assert.Empty(LegacyLibraryReader.Parse(Bytes("empty")));

    [Theory]
    [InlineData("format")][InlineData("version")][InlineData("name")][InlineData("id")]
    [InlineData("favorite")][InlineData("status")][InlineData("personalRating")][InlineData("nameIsCustom")]
    [InlineData("preferredInstallationId")][InlineData("totalPlaySeconds")][InlineData("sessions")]
    [InlineData("metadata")][InlineData("localCover")][InlineData("nonPortraitCoverDisplay")]
    public void RejectsInvalidFields(string field)
    {
        var doc = Document(); var game = doc["games"]![0]!;
        switch (field)
        {
            case "format": doc[field] = "other"; break;
            case "version": doc[field] = 2; break;
            case "name": game.AsObject().Remove(field); break;
            case "id": game[field] = ""; break;
            case "favorite": game[field] = "true"; break;
            case "status": game[field] = "unknown"; break;
            case "personalRating": game[field] = 11; break;
            case "nameIsCustom": game[field] = null; break;
            case "preferredInstallationId": game[field] = "absent"; break;
            case "totalPlaySeconds": game[field] = 10; break;
            case "sessions": game[field]![0]!["endedAt"] = "2000-01-01T00:00:00Z"; break;
            case "metadata": game[field]!["id"] = 123; break;
            case "localCover": doc["games"]![1]![field]!["dataUrl"] = "data:image/jpeg;base64,broken"; break;
            default: game[field] = "invalid"; break;
        }
        Assert.Throws<InvalidDataException>(() => LegacyLibraryReader.Parse(Encode(doc)));
    }
    [Theory][InlineData("{bad}")][InlineData("{\"games\":[")][InlineData("")]
    public void RejectsCorruptJson(string value) => Assert.ThrowsAny<System.Text.Json.JsonException>(() => LegacyLibraryReader.Parse(Encoding.UTF8.GetBytes(value)));
    [Fact] public void RejectsDuplicateGameIds()
    {
        var d = Document(); d["games"]![1]!["id"] = d["games"]![0]!["id"]!.GetValue<string>();
        Assert.Throws<InvalidDataException>(() => LegacyLibraryReader.Parse(Encode(d)));
    }
    [Fact] public void RejectsDuplicatePathsAndEscapingExecutable()
    {
        var d = Document(); var installs = d["games"]![0]!["installations"]!;
        installs[1]!["folderPath"] = installs[0]!["folderPath"]!.GetValue<string>().ToUpperInvariant();
        Assert.Throws<InvalidDataException>(() => LegacyLibraryReader.Parse(Encode(d)));
        d = Document(); d["games"]![0]!["installations"]![0]!["executablePath"] = "C:\\outside\\game.exe";
        Assert.Throws<InvalidDataException>(() => LegacyLibraryReader.Parse(Encode(d)));
    }
    [Fact] public async Task ReaderDoesNotModifyAnyFixture()
    {
        var before = Bytes();
        var result = await new LegacyLibraryReader(new IsolatedLibrarySource(), new(Fixture())).LoadAsync(default);
        Assert.True(result.IsSuccess); Assert.Equal(before, Bytes());
    }
    [Fact] public async Task CorruptionIsNeverAnEmptySuccess()
    {
        var before = Bytes("corrupt");
        var result = await new LegacyLibraryReader(new IsolatedLibrarySource(), new(Fixture("corrupt"))).LoadAsync(default);
        Assert.False(result.IsSuccess); Assert.Equal(LibraryReadStatus.InvalidData, result.Status); Assert.NotNull(result.Error);
        Assert.Equal(before, Bytes("corrupt"));
    }
    [Theory][InlineData(null)][InlineData("relative")]
    public async Task MissingConfigurationFailsClosed(string? directory)
    {
        var result = await new LegacyLibraryReader(new IsolatedLibrarySource(), new(directory)).LoadAsync(default);
        Assert.Equal(LibraryReadStatus.ConfigurationError, result.Status);
    }
    [Fact] public void FormalPathRejectedBeforeAnyRead()
    {
        var formal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KunGalManager");
        var source = new IsolatedLibrarySource();
        Assert.Throws<DataIsolationException>(() => source.OpenRead(formal));
        Assert.Throws<DataIsolationException>(() => source.OpenRead(Path.Combine(formal, "native")));
        Assert.Throws<DataIsolationException>(() => source.OpenRead(Path.GetDirectoryName(formal)));
    }
    [Fact] public async Task CancellationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var reader = new LegacyLibraryReader(new IsolatedLibrarySource(), new(Fixture()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.LoadAsync(cancellation.Token));
    }
    [Fact] public void BoundedInput() => Assert.Throws<InvalidDataException>(() => LegacyLibraryReader.Parse(new byte[LegacyLibraryReader.MaximumBytes + 1]));
}
