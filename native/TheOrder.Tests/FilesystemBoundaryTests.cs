using System.Collections.Immutable;
using System.Diagnostics;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class FilesystemBoundaryTests
{
    private static readonly NativeLibraryCodec Codec = new();
    private const string Time = "2026-10-04T00:00:00.000Z";

    [Theory]
    [InlineData("junction-outside")][InlineData("symlink-outside")][InlineData("junction-inside")]
    [InlineData("deleted")][InlineData("ordinary-replacement")][InlineData("loop")]
    public async Task QueuedDirectoryReplacementIsNeverEnumerated(string kind)
    {
        using var lab = new Lab(); var root = lab.Directory("scan"); var child = lab.Directory("scan/queued");
        var outside = lab.Game("outside", "outside-marker.exe"); var inside = lab.Game("scan/inside", "inside.exe");
        var fs = new HookFilesystem(root, () =>
        {
            lab.MoveAside(child);
            switch (kind)
            {
                case "junction-outside": lab.Link(child, outside); break;
                case "symlink-outside": lab.Link(child, outside, true); break;
                case "junction-inside": lab.Link(child, inside); break;
                case "loop": lab.Link(child, root); break;
                case "ordinary-replacement": System.IO.Directory.CreateDirectory(child); File.WriteAllText(Path.Combine(child, "replacement-marker.exe"), "synthetic"); break;
            }
        });
        var result = await new BoundedScanner(new WindowsFilesystemIdentity(), fs).ScanAsync(root, []);
        Assert.DoesNotContain(child, fs.Enumerated);
        Assert.DoesNotContain(outside, fs.Enumerated);
        Assert.DoesNotContain(result.Candidates, c => c.Folder.StoredPath == child || c.Executables.Any(e => e.Contains("outside-marker") || e.Contains("replacement-marker")));
        Assert.True(result.IsPartial); Assert.NotEmpty(result.Warnings); Assert.InRange(result.Progress.Directories, 1, 5);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ChangedRootStopsInsteadOfAdoptingNewAuthorization(bool junction)
    {
        using var lab = new Lab(); var root = lab.Directory("scan"); lab.Directory("scan/queued"); var outside = lab.Game("outside", "outside-marker.exe");
        var fs = new HookFilesystem(root, () => { lab.MoveAside(root); if (junction) lab.Link(root, outside); else System.IO.Directory.CreateDirectory(root); });
        var result = await new BoundedScanner(new WindowsFilesystemIdentity(), fs).ScanAsync(root, []);
        Assert.Equal(ScanStatus.Failed, result.Status); Assert.Contains("根目录", result.Message);
        Assert.Single(fs.Enumerated); Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ExplicitRootAliasRemainsSupportedButChildLinksAreSkipped()
    {
        using var lab = new Lab(); var target = lab.Directory("target"); lab.Game("target/game", "game.exe");
        var outside = lab.Game("outside", "outside-marker.exe"); lab.Link(Path.Combine(target, "link"), outside);
        var root = Path.Combine(lab.Root, "selected-alias"); lab.Link(root, target);
        var result = await new BoundedScanner(new WindowsFilesystemIdentity(), new WindowsScanFilesystem()).ScanAsync(root, []);
        var candidate = Assert.Single(result.Candidates); Assert.Equal(Path.Combine(root, "game"), candidate.Folder.StoredPath);
        Assert.True(candidate.Folder.ViaReparsePoint); Assert.DoesNotContain("outside-marker", string.Join("|", candidate.Executables));
    }

    [Theory][InlineData(PreflightStatus.AccessDenied)][InlineData(PreflightStatus.NotFound)]
    [InlineData(PreflightStatus.IdentityUnavailable)][InlineData(PreflightStatus.OtherIOError)]
    public async Task UnavailableExistingIdentityIsIndeterminateAndRetryReevaluates(PreflightStatus status)
    {
        using var lab = new Lab(); var target = lab.Game("game", "game.exe"); var alias = Path.Combine(lab.Root, "alias"); lab.Link(alias, target);
        var unavailable = true;
        var identity = new HookIdentity((p, _) => p == alias && unavailable ? new(status, null, "synthetic unavailable") : null);
        var service = new InstallationPreflightService(identity); ImmutableArray<ExistingInstallation> existing = [new("old", "old", "i", alias)];
        var result = await service.CheckAsync(target, existing);
        Assert.Equal(DuplicateStatus.Indeterminate, result.DuplicateStatus); Assert.False(result.CanAttach);
        Assert.Equal(alias, Assert.Single(result.Unresolved).Installation.Folder); Assert.Contains(alias, result.Path.Message);
        unavailable = false;
        Assert.Equal(DuplicateStatus.ConfirmedDuplicate, (await service.CheckAsync(target, existing)).DuplicateStatus);
        var unique = lab.Game("different", "game.exe");
        Assert.Equal(DuplicateStatus.ConfirmedUnique, (await service.CheckAsync(unique, existing)).DuplicateStatus);
    }

    [Fact]
    public void SameStoredPathWithUnavailableObservationIsConfirmedDuplicate()
    {
        using var lab = new Lab(); var path = lab.Game("game", "game.exe");
        var result = new InstallationPreflightService(new WindowsFilesystemIdentity()).Check(path,
            new[] { new ObservedInstallation(new("old", "old", "i", path), new(PreflightStatus.AccessDenied, null, "denied")) }, default);
        Assert.Equal(DuplicateStatus.ConfirmedDuplicate, result.DuplicateStatus); Assert.False(result.CanAttach);
    }

    [Fact]
    public async Task RealUnavailableAliasIsIndeterminateWithoutBlockingDirectTargetAccess()
    {
        using var lab = new Lab(); var target = lab.Game("target", "game.exe"); var alias = Path.Combine(lab.Root, "alias"); lab.Link(alias, target);
        var identity = new WindowsFilesystemIdentity(); var service = new InstallationPreflightService(identity);
        var original = identity.Inspect(target).Observation!; lab.MoveAside(target);
        Assert.Equal(original.Identity, identity.Inspect(target + "-old").Observation!.Identity);
        Assert.Equal(PreflightStatus.BrokenLink, identity.Inspect(alias).Status);
        var result = await service.CheckAsync(target + "-old", [new("old", "old", "i", alias)]);
        Assert.Equal(DuplicateStatus.Indeterminate, result.DuplicateStatus); Assert.False(result.CanAttach);
        lab.Move(target + "-old", target);
        Assert.Equal(DuplicateStatus.ConfirmedDuplicate, (await service.CheckAsync(target, [new("old", "old", "i", alias)])).DuplicateStatus);
    }

    [Fact]
    public void WindowsEnumerationBoundaryRejectsReplacementAfterObservation()
    {
        using var lab = new Lab(); var root = lab.Directory("scan"); var child = lab.Directory("scan/queued"); var outside = lab.Game("outside", "outside-marker.exe");
        var identity = new WindowsFilesystemIdentity(); var rootObservation = identity.Inspect(root).Observation!; var childObservation = identity.Inspect(child).Observation!;
        lab.MoveAside(child); lab.Link(child, outside);
        Assert.Throws<InvalidDataException>(() => new WindowsScanFilesystem().Enumerate(childObservation, rootObservation).ToArray());
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task UnknownHistoryRejectsWholeBatchAndPreservesHistory(bool physicallyMissing)
    {
        using var lab = new Lab(); var old = lab.Directory("old"); var a = lab.Game("a", "game.exe"); var b = lab.Game("b", "game.exe");
        if (physicallyMissing) lab.MoveAside(old);
        var entry = new LibraryEntry("old", "historical", false, null, [new("install", old, null, [])], "install", "planned", null, false, [], [], "keep", 0, 0, [], Time, null, null, null, new(false, null));
        var persistence = new CountingPersistence(Codec.Encode([entry], Time));
        var identity = new HookIdentity((p, _) => p == old && !physicallyMissing ? new(PreflightStatus.AccessDenied, null, "denied") : null);
        await using var client = Client(persistence); await client.LoadAsync(default);
        var files = Files(client, identity); var candidates = ImmutableArray.Create(Candidate(a), Candidate(b));
        var before = persistence.Bytes.ToArray(); var result = await files.ImportAsync(candidates, default);
        Assert.Equal(ClientOutcome.ValidationFailure, result.Outcome); Assert.Contains("无法确认是否重复", result.Message);
        Assert.Equal(0, persistence.Writes); Assert.Equal(before, persistence.Bytes);
        Assert.Equal(old, Assert.Single(Codec.Decode(persistence.Bytes)).Installations[0].Folder);
    }

    [Theory][InlineData("ordinary")][InlineData("junction")][InlineData("removed")][InlineData("renamed")]
    public async Task PreprocessingCannotReplaceReviewedCandidate(string change)
    {
        using var lab = new Lab(); var root = lab.Game("game", "game.exe"); var replacement = lab.Game("replacement", "game.exe");
        var original = Candidate(root); var originalId = original.Folder.Identity; bool changed = false;
        // Execute the change after the first folder revalidation, at the next executable inspection.
        var identity = new HookIdentity((p, directory) =>
        {
            if (!directory && !changed)
            {
                changed = true; lab.MoveAside(root);
                if (change == "ordinary") { System.IO.Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "game.exe"), "synthetic B"); }
                if (change == "junction") lab.Link(root, replacement);
                if (change == "renamed") Assert.Equal(originalId, new WindowsFilesystemIdentity().Inspect(root + "-old").Observation!.Identity);
            }
            return null;
        });
        var persistence = new CountingPersistence(Codec.Encode([], Time)); await using var client = Client(persistence); await client.LoadAsync(default);
        var result = await Files(client, identity).ImportAsync([original], default);
        Assert.True(changed); Assert.Equal(ClientOutcome.CandidateStale, result.Outcome); Assert.Equal(0, persistence.Writes); Assert.Empty(Codec.Decode(persistence.Bytes));
    }

    [Fact]
    public async Task FinalPrecommitRejectsOneChangedCandidateAmongOneHundredWithoutWrites()
    {
        using var lab = new Lab(); var candidates = Enumerable.Range(0, 100).Select(i => Candidate(lab.Game("g" + i, "game.exe"))).ToImmutableArray();
        var last = candidates[^1].Folder.StoredPath; var calls = 0;
        // Last folder calls: initial validation, Prepare, original validation, mutation revalidation,
        // duplicate check, then final precommit original validation. Change on that final production check.
        var identity = new HookIdentity((p, directory) => { if (directory && p == last && ++calls == 6) { lab.MoveAside(last); System.IO.Directory.CreateDirectory(last); File.WriteAllText(Path.Combine(last, "game.exe"), "replacement"); } return null; });
        var persistence = new CountingPersistence(Codec.Encode([], Time)); await using var client = Client(persistence); await client.LoadAsync(default);
        var result = await Files(client, identity).ImportAsync(candidates, default);
        Assert.True(calls >= 6); Assert.Equal(ClientOutcome.CandidateStale, result.Outcome); Assert.Equal(0, persistence.Writes); Assert.Empty(Codec.Decode(persistence.Bytes));
    }

    [Fact]
    public async Task UnchangedReviewedCandidateImportsOnceThroughOfflineClient()
    {
        using var lab = new Lab(); var path = lab.Game("game", "game.exe"); var candidate = Candidate(path);
        var persistence = new CountingPersistence(Codec.Encode([], Time)); await using var client = Client(persistence); await client.LoadAsync(default);
        var files = Files(client, new WindowsFilesystemIdentity()); var scanned = await files.ScanAsync(path, null, default);
        Assert.Equal(candidate.Folder.Identity, Assert.Single(scanned.Candidates).Folder.Identity);
        var result = await files.ImportAsync(scanned.Candidates, default);
        Assert.True(result.IsCommitted); Assert.Equal(1, persistence.Writes);
        var imported = Assert.Single(Codec.Decode(persistence.Bytes));
        Assert.Equal(candidate.Folder.StoredPath, imported.Installations[0].Folder);
        Assert.Equal(imported.Id, Assert.Single(result.ImportedGameIds));
        var duplicate = await files.ImportAsync(scanned.Candidates, default);
        Assert.True(duplicate.IsCommitted); Assert.Empty(duplicate.ImportedGameIds);
        Assert.Single(Codec.Decode(persistence.Bytes));
    }

    [Theory][InlineData(false, false)][InlineData(true, false)][InlineData(true, true)]
    public async Task DepthBoundaryReportsPartialWithoutTraversingDeeper(bool deeper, bool candidate)
    {
        using var lab = new Lab(); var root = lab.Directory("scan"); lab.Directory("scan/boundary");
        if (deeper) lab.Game("scan/boundary/deeper", "hidden.exe");
        if (candidate) lab.Game("scan/found", "game.exe");
        var fs = new HookFilesystem(root, () => { });
        var result = await new BoundedScanner(new WindowsFilesystemIdentity(), fs).ScanAsync(root, [], new(MaxDepth: 1));
        Assert.Equal(deeper, result.IsPartial); Assert.Equal(deeper ? ScanTruncation.DepthLimit : ScanTruncation.None, result.Truncation);
        Assert.Equal(candidate ? 1 : 0, result.Candidates.Length);
        Assert.DoesNotContain(Path.Combine(root, "boundary", "deeper"), fs.Enumerated);
        if (deeper) { Assert.Contains("深度限制", result.Message); Assert.NotEqual("未发现候选游戏。", result.Message); }
    }

    private static ScanCandidate Candidate(string root)
    {
        var observation = new WindowsFilesystemIdentity().Inspect(root).Observation!;
        return new(Guid.NewGuid().ToString(), Path.GetFileName(root), observation, [Path.Combine(root, "game.exe")], "synthetic", new(new(PreflightStatus.Valid, observation, "available")));
    }
    private static OfflineFilesClient Files(OfflineLibraryClient client, IFilesystemIdentity identity) => new(client, identity, new(identity, new WindowsScanFilesystem()), new WindowsRasterCodec());
    private static OfflineLibraryClient Client(CountingPersistence persistence) => new(new Factory(persistence), new LegacyClientTextRules());
    private sealed class Factory(CountingPersistence persistence) : ITransactionalLibraryFactory
    { public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation) => Task.FromResult(TransactionalLibrary.Open(Codec, persistence)); }
    private sealed class CountingPersistence(byte[] bytes) : ILibraryPersistence
    {
        public byte[] Bytes { get; private set; } = bytes; public int Writes { get; private set; }
        public byte[]? ReadMain() => Bytes; public byte[]? ReadBackup() => null;
        public Task<PersistenceReceipt> SaveAsync(ReadOnlyMemory<byte> previous, ReadOnlyMemory<byte> candidate) { Writes++; Bytes = candidate.ToArray(); return Task.FromResult(new PersistenceReceipt()); }
        public void Dispose() { }
    }
    private sealed class HookIdentity(Func<string, bool, PathPreflight?> before) : IFilesystemIdentity
    { private readonly WindowsFilesystemIdentity inner = new(); public PathPreflight Inspect(string path, bool requireDirectory = true) => before(path, requireDirectory) ?? inner.Inspect(path, requireDirectory); }
    private sealed class HookFilesystem(string root, Action afterRoot) : IScanFilesystem
    {
        private readonly WindowsScanFilesystem inner = new(); private bool changed;
        public List<string> Enumerated { get; } = [];
        public bool IsForbiddenRoot(PathObservation observation) => inner.IsForbiddenRoot(observation);
        public IEnumerable<DirectoryItem> Enumerate(string path)
        {
            Enumerated.Add(path);
            foreach (var entry in inner.Enumerate(path)) yield return entry;
            if (path == root && !changed) { changed = true; afterRoot(); }
        }
    }
    private sealed class Lab : IDisposable
    {
        private readonly TransactionProfile profile = new(); private readonly List<string> links = [];
        public string Root => profile.Root;
        public string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        public string Game(string name, string exe) { var path = Directory(name); File.WriteAllText(Path.Combine(path, exe), "synthetic text; never executable"); return path; }
        private void Check(string path) { Assert.StartsWith(Root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); }
        public void MoveAside(string path) { Check(path); Check(path + "-old"); System.IO.Directory.Move(path, path + "-old"); }
        public void Move(string from, string to) { Check(from); Check(to); System.IO.Directory.Move(from, to); }
        public void Link(string path, string target, bool symbolic = false)
        {
            Check(path); Check(target);
            if (symbolic) System.IO.Directory.CreateSymbolicLink(path, target);
            else
            {
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
                start.ArgumentList.Add($"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{path.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
                using var process = Process.Start(start)!; Assert.True(process.WaitForExit(15000)); Assert.Equal(0, process.ExitCode);
            }
            links.Add(path);
        }
        public void Dispose() { foreach (var path in links) { Check(path); System.IO.Directory.Delete(path); } profile.Dispose(); }
    }
}
