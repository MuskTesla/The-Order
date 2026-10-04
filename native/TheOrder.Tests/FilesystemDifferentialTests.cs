using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class FilesystemDifferentialTests
{
    private static JsonDocument Oracle() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "filesystem-oracle.json")));
    [Fact] public void ReferenceSourceHashesAreCurrent()
    {
        using var oracle = Oracle(); var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "package.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        foreach (var property in oracle.RootElement.GetProperty("hashes").EnumerateObject())
            Assert.Equal(property.Value.GetString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(directory.FullName, property.Name)).Replace("\r\n", "\n")))).ToLowerInvariant());
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void CoverPriorityMatchesElectron(int index)
    {
        using var oracle = Oracle(); var vector = oracle.RootElement.GetProperty("covers")[index];
        var dto = JsonSerializer.Deserialize<LegacyGameDto>(vector.GetProperty("game"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var game = LegacyMapping.Map(dto, false);
        Assert.Equal(vector.GetProperty("selected").GetString(), CoverSources.Selected(game, vector.GetProperty("includeExplicit").GetBoolean()));
    }
    [Theory][InlineData("depth")][InlineData("directories")][InlineData("entries")][InlineData("candidates")]
    public async Task ScannerActualBoundsMatchElectron(string mode)
    {
        using var oracle = Oracle(); var expected = oracle.RootElement.GetProperty("limits").EnumerateArray().Single(v => v.GetProperty("name").GetString() == mode);
        var filesystem = new VirtualTree(mode); var result = await new BoundedScanner(filesystem, filesystem).ScanAsync(VirtualTree.Root, []);
        Assert.Equal(expected.GetProperty("accepted").GetBoolean(), result.Status is ScanStatus.Completed or ScanStatus.NoCandidates);
        if (expected.TryGetProperty("count", out var count)) Assert.Equal(count.GetInt32(), result.Candidates.Length);
    }
    [Fact] public async Task RealScanImportDuplicateAttachPreferredAndArgsMatchElectron()
    {
        using var oracle = Oracle(); using var profile = new TransactionProfile(); var identity = new WindowsFilesystemIdentity();
        var root = Path.Combine(profile.Root, "games"); Directory.CreateDirectory(root);
        string Make(string name, params string[] files) { var p = Path.Combine(root, name); Directory.CreateDirectory(p); foreach (var f in files) File.WriteAllText(Path.Combine(p, f), "synthetic-not-executable"); return p; }
        Make("A", "A.exe", "launcher.exe", "setup.exe"); Make("B", "game.exe"); Make("工具", "uninstall.exe"); Make("补丁", "patch.exe");
        var scan = await new BoundedScanner(identity, new WindowsScanFilesystem()).ScanAsync(root, []);
        Assert.Equal(oracle.RootElement.GetProperty("scans").GetArrayLength(), scan.Candidates.Length);
        foreach (var c in scan.Candidates)
        {
            var reference = oracle.RootElement.GetProperty("scans").EnumerateArray().Single(v => v.GetProperty("name").GetString() == c.Name);
            Assert.Equal(reference.GetProperty("executables").EnumerateArray().Select(v => v.GetString()), c.Executables.Select(Path.GetFileName));
        }
        var codec = new NativeLibraryCodec(); File.WriteAllBytes(profile.Main, codec.Encode([], "2026-10-04T00:00:00.000Z"));
        await using var library = TransactionalLibrary.Open(codec, new IsolatedLibraryPersistence(profile.Root)); var preflight = new InstallationPreflightService(identity);
        PreparedInstallation Prepare(string folder, string id, string? executable) => new(new(id, folder, executable, []), identity.Inspect(folder).Observation!, executable is null ? null : identity.Inspect(executable, false).Observation, null);
        var inputs = scan.Candidates.Select(c => new LocalImportItem(c.Name, Prepare(c.Folder.StoredPath, c.Id, c.Executables.Length == 1 ? c.Executables[0] : null), [.. c.Executables.Select(e => identity.Inspect(e, false).Observation!)])).ToImmutableArray();
        var operations = oracle.RootElement.GetProperty("operations");
        for (var i = 0; i < 2; i++) { var result = (await library.ImportLocalAsync(inputs, preflight)).Value; Assert.Equal(operations[i].GetProperty("result").GetProperty("added").GetInt32(), result.Added); Assert.Equal(operations[i].GetProperty("result").GetProperty("skipped").GetInt32(), result.Skipped); }
        var game = library.Snapshot.Entries.Single(g => g.Name == "A"); var extra = Make("extra", "game.exe");
        var added = (await library.SaveInstallationAsync((await library.InstallationsAsync(game.Id)).Token, Prepare(extra, "extra", Path.Combine(extra, "game.exe")), false, preflight)).Value;
        Assert.Equal(operations[2].GetProperty("count").GetInt32(), added.Installations.Length);
        Assert.Equal(operations[2].GetProperty("preferredIndex").GetInt32(), Array.FindIndex(added.Installations.ToArray(), i => i.Id == added.PreferredInstallationId));
        var prepared = Prepare(extra, "extra", Path.Combine(extra, "game.exe")); prepared = prepared with { Value = prepared.Value with { Arguments = ["two words", "", "--flag=中"] } };
        await library.SaveInstallationAsync((await library.InstallationsAsync(game.Id)).Token, prepared, false, preflight);
        var preferred = (await library.PreferInstallationAsync((await library.InstallationsAsync(game.Id)).Token, game.Installations[0].Id)).Value;
        Assert.Equal(operations[3].GetProperty("arguments").EnumerateArray().Select(v => v.GetString()), preferred.Installations[1].Arguments);
        Assert.Equal(operations[3].GetProperty("preferredIndex").GetInt32(), Array.FindIndex(preferred.Installations.ToArray(), i => i.Id == preferred.PreferredInstallationId));
        var token = (await library.InstallationsAsync(game.Id)).Token;
        Assert.True(operations[4].GetProperty("rejected").GetBoolean());
        await Assert.ThrowsAsync<InvalidDataException>(() => library.SaveInstallationAsync(token, Prepare(extra, "duplicate", Path.Combine(extra, "game.exe")), false, preflight));
    }
    private sealed class VirtualTree(string mode) : IFilesystemIdentity, IScanFilesystem
    {
        public const string Root = @"Z:\SyntheticOracle";
        public bool IsForbiddenRoot(PathObservation root) => false;
        public PathPreflight Inspect(string path, bool requireDirectory = true) => new(PreflightStatus.Valid, new(path, path, path, new("virtual", path), requireDirectory, false, 0), "");
        public IEnumerable<DirectoryItem> Enumerate(string directory)
        {
            DirectoryItem Item(string name, bool isDirectory) => new(Path.Combine(directory, name), name, isDirectory, false);
            if (mode == "depth") return [Item(directory.Split('\\').Length < 9 ? "child" : "game.exe", directory.Split('\\').Length < 9)];
            if (directory == Root) return Enumerable.Range(0, mode == "directories" ? 5001 : mode == "entries" ? 50001 : 2001).Select(i => Item(i.ToString(), mode != "entries"));
            return mode == "candidates" ? [Item("game.exe", false)] : [];
        }
    }
}
