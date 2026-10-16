using TheOrder.Application;
using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record InstallationEditToken(Guid Epoch, string GameId, Guid Generation, ImmutableArray<Installation> Original, string? Preferred);
public sealed record InstallationDetails(InstallationEditToken Token, ImmutableArray<Installation> Items, string? Preferred);
public sealed record PreparedInstallation(Installation Value, PathObservation Folder, PathObservation? Executable, PathObservation? WorkingDirectory,
    PathObservation? ReviewedCandidate = null);
public sealed record LocalImportItem(string Name, PreparedInstallation Installation, ImmutableArray<PathObservation> Executables);

public sealed partial class TransactionalLibrary
{
    public Task<InstallationDetails> InstallationsAsync(string id, CancellationToken cancellation = default) => Enqueue(() =>
    {
        var entry = Find(current.Entries, id);
        return Task.FromResult(new InstallationDetails(new(epoch, id, lifecycles[id].Generation, entry.Installations, entry.PreferredInstallationId), entry.Installations, entry.PreferredInstallationId));
    }, cancellation);
    private void AssertInstallations(InstallationEditToken token, LibraryEntry entry)
    {
        if (token.Epoch != epoch || token.Generation != lifecycles[entry.Id].Generation || token.Preferred != entry.PreferredInstallationId || token.Original.Length != entry.Installations.Length
            || !token.Original.Zip(entry.Installations).All(p => p.First.Id == p.Second.Id && p.First.Folder == p.Second.Folder && p.First.Executable == p.Second.Executable && p.First.WorkingDirectory == p.Second.WorkingDirectory && p.First.Arguments.SequenceEqual(p.Second.Arguments)))
            throw new StaleLibraryRequestException();
    }
    public Task<CommitResult<LibraryEntry>> SaveInstallationAsync(InstallationEditToken token, PreparedInstallation prepared,
        bool makePreferred, InstallationPreflightService preflight, CancellationToken cancellation = default)
        => Mutate(games =>
        {
            var entry = Find(games, token.GameId); AssertInstallations(token, entry);
            var duplicate = preflight.Check(prepared.Value.Folder, Existing(games, prepared.Value.Id, token.GameId), cancellation);
            if (!duplicate.CanAttach) throw new InvalidDataException(duplicate.Path.Message);
            var old = entry.Installations.FirstOrDefault(i => i.Id == prepared.Value.Id);
            if (old is not null) prepared = prepared with { Value = prepared.Value with { SaveFolder = old.SaveFolder, SteamAppId = old.SteamAppId, Compatibility = old.Compatibility } };
            var items = old is null ? entry.Installations.Add(prepared.Value) : entry.Installations.Replace(old, prepared.Value);
            var preferred = makePreferred || !items.Any(i => i.Id == entry.PreferredInstallationId && i.Executable is not null) ? prepared.Value.Id : entry.PreferredInstallationId;
            var changed = entry with { Installations = items, PreferredInstallationId = preferred };
            return (games.Replace(entry, changed), changed);
        }, cancellation, precommit: () =>
        {
            Revalidate(prepared, preflight);
            var check = preflight.Check(prepared.Value.Folder, Existing(current.Entries, prepared.Value.Id, token.GameId), cancellation);
            if (!check.CanAttach) throw new InvalidDataException(check.Path.Message);
        });
    public Task<CommitResult<LibraryEntry>> RemoveInstallationAsync(InstallationEditToken token, string installationId, CancellationToken cancellation = default)
        => Mutate(games =>
        {
            var entry = Find(games, token.GameId); AssertInstallations(token, entry);
            var old = entry.Installations.FirstOrDefault(i => i.Id == installationId) ?? throw new InvalidDataException(UiText.Get("安装不存在。"));
            var changed = entry with { Installations = entry.Installations.Remove(old), PreferredInstallationId = entry.PreferredInstallationId == installationId ? null : entry.PreferredInstallationId };
            return (games.Replace(entry, changed), changed);
        }, cancellation);
    public Task<CommitResult<LibraryEntry>> PreferInstallationAsync(InstallationEditToken token, string installationId, CancellationToken cancellation = default)
        => Mutate(games =>
        {
            var entry = Find(games, token.GameId); AssertInstallations(token, entry);
            if (!entry.Installations.Any(i => i.Id == installationId)) throw new InvalidDataException(UiText.Get("安装不存在。"));
            var changed = entry with { PreferredInstallationId = installationId }; return (games.Replace(entry, changed), changed);
        }, cancellation);
    public Task<CommitResult<ImportCount>> ImportLocalAsync(ImmutableArray<LocalImportItem> items, InstallationPreflightService preflight, CancellationToken cancellation = default)
    {
        var accepted = new List<LocalImportItem>();
        return Mutate(games =>
        {
            if (items.Length is < 1 or > 2000) throw new InvalidDataException(UiText.Get("请选择 1 至 2000 个候选。"));
            var builder = games.ToBuilder(); int skipped = 0;
            var observed = preflight.Observe(Existing(games), cancellation).ToList();
            foreach (var item in items)
            {
                cancellation.ThrowIfCancellationRequested(); ValidateCandidate(item, preflight); Revalidate(item.Installation, preflight);
                var check = preflight.Check(item.Installation.Value.Folder, observed, cancellation);
                if (check.Overlap) throw new InvalidDataException(check.Path.Message);
                if (check.Duplicate is not null) { skipped++; continue; }
                if (!check.CanAttach) throw new InvalidDataException(check.Path.Message);
                var location = item.Installation.Value;
                builder.Add(new(newId(), item.Name, false, null, [location], location.Id, "planned", null, false, [], [], "", 0, 0, [], now(), null, null, null, new(false, null)));
                accepted.Add(item);
                observed.Add(new(new("candidate", item.Name, location.Id, location.Folder), check.Path));
            }
            return (builder.ToImmutable(), new ImportCount(accepted.Count, skipped));
        }, cancellation, precommit: () =>
        {
            var observed = preflight.Observe(Existing(current.Entries), cancellation).ToList();
            foreach (var item in accepted)
            {
                cancellation.ThrowIfCancellationRequested(); ValidateCandidate(item, preflight); Revalidate(item.Installation, preflight);
                var check = preflight.Check(item.Installation.Value.Folder, observed, cancellation);
                if (!check.CanAttach) throw new InvalidDataException(check.Path.Message);
                observed.Add(new(new("candidate", item.Name, item.Installation.Value.Id, item.Installation.Value.Folder), check.Path));
            }
        });
    }
    private static void ValidateCandidate(LocalImportItem item, InstallationPreflightService preflight)
    {
        if (item.Installation.ReviewedCandidate is not { } original || !InstallationPreflightService.SameObservation(original, item.Installation.Folder))
            throw new CandidateStaleException();
        preflight.Revalidate(original, reviewedCandidate: true);
        if (item.Executables.IsDefault || item.Executables.Length is < 1 or > 200) throw new InvalidDataException(UiText.Get("候选必须含有 1 至 200 个游戏 exe 或启动 cmd。"));
        var expected = item.Executables.Length == 1 ? item.Executables[0].StoredPath : null;
        if (item.Installation.Value.Executable is not null && item.Installation.Value.Executable != expected)
            throw new InvalidDataException(UiText.Get("导入候选中的启动程序必须是唯一候选文件；也可留空，在首次启动时选择。"));
        foreach (var executable in item.Executables)
        {
            if (executable.IsDirectory || !(BoundedScanner.IsGameExecutable(Path.GetFileName(executable.StoredPath)) || BoundedScanner.IsLauncherScript(Path.GetFileName(executable.StoredPath))) || !InstallationPreflightService.Within(executable.FinalPath, item.Installation.Folder.FinalPath)
                || !string.Equals(Path.GetDirectoryName(executable.FinalPath), item.Installation.Folder.FinalPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(UiText.Get("候选启动文件必须位于游戏根目录。"));
            preflight.Revalidate(executable);
        }
    }
    public static ImmutableArray<ExistingInstallation> Existing(ImmutableArray<LibraryEntry> games, string? exceptInstall = null, string? exceptGame = null)
        => [.. games.SelectMany(g => g.Installations.Where(i => i.Id != exceptInstall || g.Id != exceptGame).Select(i => new ExistingInstallation(g.Id, g.Name, i.Id, i.Folder)))];
    private static void Revalidate(PreparedInstallation item, InstallationPreflightService preflight)
    {
        if (item.Value.Folder != item.Folder.StoredPath || !item.Folder.IsDirectory || item.Value.Executable != item.Executable?.StoredPath
            || item.Value.WorkingDirectory != item.WorkingDirectory?.StoredPath || item.Executable?.IsDirectory == true
            || item.Executable is not null && (!LaunchFileDiscovery.IsLaunchFile(Path.GetFileName(item.Value.Executable!)) || !InstallationPreflightService.Within(item.Executable.FinalPath, item.Folder.FinalPath))
            || item.WorkingDirectory is not null && (!item.WorkingDirectory.IsDirectory || item.WorkingDirectory.Identity != item.Folder.Identity && !InstallationPreflightService.Within(item.WorkingDirectory.FinalPath, item.Folder.FinalPath)))
            throw new InvalidDataException(UiText.Get("安装检查结果与待保存路径不一致。"));
        preflight.Revalidate(item.Folder);
        if (item.Executable is not null) preflight.Revalidate(item.Executable);
        if (item.WorkingDirectory is not null) preflight.Revalidate(item.WorkingDirectory);
    }
}
