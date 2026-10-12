using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class LegacyRemediationTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/compatibility-vectors.json")));
    public static IEnumerable<object[]> StringCases() => Cases("strings");
    public static IEnumerable<object[]> PathCases() => Cases("paths");
    public static IEnumerable<object[]> NumberCases() => Cases("numbers");
    private static IEnumerable<object[]> Cases(string group)
    {
        using var doc = Vectors();
        return doc.RootElement.GetProperty(group).EnumerateArray().Select(v => new object[] { v.GetProperty("label").GetString()! }).ToArray();
    }
    private static JsonElement Case(JsonDocument doc, string group, string label) => doc.RootElement.GetProperty(group).EnumerateArray().Single(v => v.GetProperty("label").GetString() == label);

    [Fact] public void ReferenceVectorsAreCurrentAndComplete()
    {
        using var doc = Vectors(); var root = doc.RootElement;
        Assert.Equal("the-order-legacy-reference", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        using var package = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ReferenceSources.Repository(), "package.json")));
        Assert.Equal(package.RootElement.GetProperty("devDependencies").GetProperty("electron").GetString(), root.GetProperty("electron").GetString());
        foreach (var (group, count) in new[] { ("strings", 7), ("paths", 29), ("numbers", 7) })
        {
            var labels = root.GetProperty(group).EnumerateArray().Select(v => v.GetProperty("label").GetString()).ToArray();
            Assert.Equal(count, labels.Length); Assert.Equal(count, labels.Distinct().Count());
        }
        var source = File.ReadAllText(Path.Combine(ReferenceSources.Repository(), "electron/library.ts")).Replace("\r\n", "\n");
        Assert.True(root.GetProperty("sourceSha256").GetString() == ReferenceSources.Hash(source), "Electron library reference changed. Inspect and regenerate native/scripts/generate-compatibility-vectors.mjs.");
        using var stream = typeof(LegacyPathIdentity).Assembly.GetManifestResourceStream("TheOrder.Infrastructure.Legacy.LegacyUnicodeData.json");
        Assert.NotNull(stream);
        using var text = new StreamReader(stream);
        Assert.Equal(root.GetProperty("caseDataSha256").GetString(), ReferenceSources.Hash(text.ReadToEnd().Replace("\r\n", "\n")));
    }

    [Theory][MemberData(nameof(StringCases))]
    public async Task ProductionReaderPreservesJavaScriptUtf16(string label)
    {
        using var vectors = Vectors(); var vector = Case(vectors, "strings", label);
        using var profile = new SyntheticLegacyProfile(vector.GetProperty("json").GetString()!);
        var result = await profile.Read();
        Assert.True(result.IsSuccess, result.Error); Assert.Equal(3, result.Entries.Length);
        var expected = vector.GetProperty("utf16").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var game = result.Entries[0];
        foreach (var value in new[] { game.Name, game.Notes, game.Metadata!.Title, Assert.Single(game.Categories), Assert.Single(game.Installations[0].Arguments) })
            Assert.Equal(expected, value.Select(c => (int)c).ToArray());
        Assert.Equal("synthetic-b", result.Entries[1].Id); Assert.Equal("synthetic-c", result.Entries[2].Id);
    }

    [Theory][MemberData(nameof(PathCases))]
    public async Task PathIdentityAndProductionValidationMatchElectron(string label)
    {
        using var vectors = Vectors(); var vector = Case(vectors, "paths", label);
        var paths = new[] { vector.GetProperty("left").GetString()!, vector.GetProperty("right").GetString()! };
        var keys = vector.GetProperty("keys").EnumerateArray().ToArray();
        for (int i = 0; i < paths.Length; i++)
        {
            if (keys[i].ValueKind == JsonValueKind.Null) Assert.Throws<InvalidDataException>(() => LegacyPathIdentity.Create(paths[i]));
            else Assert.Equal(keys[i].GetString(), LegacyPathIdentity.Create(paths[i], vectors.RootElement.GetProperty("contextRoot").GetString()));
        }
        using var profile = new SyntheticLegacyProfile(vector.GetProperty("json").GetString()!);
        var result = await profile.Read(); var accepted = vector.GetProperty("accepted").GetBoolean();
        Assert.Equal(accepted ? LibraryReadStatus.Loaded : LibraryReadStatus.InvalidData, result.Status);
        if (accepted) Assert.Equal(paths, result.Entries[0].Installations.Select(i => i.Folder).ToArray());
        else Assert.NotNull(result.Error);
    }

    [Theory][MemberData(nameof(NumberCases))]
    public async Task ProductionNumberLexemesMatchElectron(string label)
    {
        using var vectors = Vectors(); var vector = Case(vectors, "numbers", label);
        using var profile = new SyntheticLegacyProfile(vector.GetProperty("json").GetString()!);
        var result = await profile.Read();
        Assert.Equal(vector.GetProperty("accepted").GetBoolean() ? LibraryReadStatus.Loaded : LibraryReadStatus.InvalidData, result.Status);
        if (result.IsSuccess)
        {
            Assert.Equal(3, result.Entries.Length);
            Assert.Equal(4, result.Entries[1].LocalCover!.Width); Assert.Equal(3, result.Entries[1].LocalCover!.Height);
            var cover = Assert.Single(result.Entries[0].Metadata!.Covers!.Value);
            Assert.Equal(4, cover.Width); Assert.Equal(3, cover.Height); Assert.Equal(9007199254740991L, cover.Votes);
        }
    }
}

internal static class ReferenceSources
{
    public static string FixtureOutput(string project)
    {
        var repo = Repository();
        var relative = Path.GetRelativePath(Path.Combine(repo, "native", "TheOrder.Tests", "bin"), AppContext.BaseDirectory);
        return Path.GetFullPath(Path.Combine(repo, "native", project, "bin", relative));
    }
    public static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "electron/metadata-matching.ts"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Reference tests must run from this repository.");
    }
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

internal sealed class SyntheticLegacyProfile : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "the-order-remediation-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] original;
    public SyntheticLegacyProfile(string json)
    {
        original = Encoding.UTF8.GetBytes(json);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, IsolatedLibrarySource.MarkerName), IsolatedLibrarySource.MarkerValue);
        File.WriteAllBytes(Path.Combine(directory, "library.json"), original);
    }
    public async Task<LibraryReadResult> Read()
    {
        var result = await new LegacyLibraryReader(new IsolatedLibrarySource(), new(directory)).LoadAsync(default);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, "library.json")));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
        return result;
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(full).StartsWith("the-order-remediation-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected synthetic directory");
        Directory.Delete(full, true);
    }
}
