using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record InstallationInput(string Id, string Folder, string? Executable, string? WorkingDirectory, ImmutableArray<string> Arguments, bool Preferred);
public sealed record InstallationRow(Installation Installation, PreflightStatus Status, string StatusText, bool Preferred)
{
    public string PreferredText => Preferred ? "首选安装" : "";
    public override string ToString() => $"{Installation.Folder} · {StatusText} · {PreferredText}";
}
public sealed record InstallationView(InstallationDetails Details, ImmutableArray<InstallationRow> Rows);
public interface IOfflineFilesClient
{
    Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken cancellation);
    Task<ClientResult> ImportAsync(ImmutableArray<ScanCandidate> candidates, CancellationToken cancellation);
    Task<InstallationView> InstallationsAsync(string id, CancellationToken cancellation);
    Task<ClientResult> SaveAsync(InstallationDetails details, InstallationInput input, CancellationToken cancellation);
    Task<ClientResult> RemoveAsync(InstallationDetails details, string id, CancellationToken cancellation);
    Task<ClientResult> PreferAsync(InstallationDetails details, string id, CancellationToken cancellation);
    Task<ClientResult> CoverAsync(GameDetails details, string? path, CancellationToken cancellation);
}
public sealed class OfflineFilesClient(OfflineLibraryClient client, IFilesystemIdentity identity, BoundedScanner scanner, IRasterCodec raster) : IOfflineFilesClient
{
    private readonly InstallationPreflightService preflight = new(identity);
    public async Task<ClientResult> CoverAsync(GameDetails details, string? path, CancellationToken cancellation)
    {
        try
        {
            var library = client.Required();
            if (details.CoverToken is null) throw new InvalidDataException("请重新打开详情后修改封面。");
            var cover = path is null ? null : await raster.NormalizePhotoAsync(path, cancellation).ConfigureAwait(false);
            return OfflineLibraryClient.Present(await library.SaveLocalCoverAsync(details.CoverToken, cover, cancellation).ConfigureAwait(false));
        }
        catch (Exception e) when (OfflineLibraryClient.Expected(e) || e is System.Runtime.InteropServices.COMException)
        { return e is System.Runtime.InteropServices.COMException ? new(ClientOutcome.ValidationFailure, "无法解码此图片，原封面保持不变。") : OfflineLibraryClient.Failure(e); }
    }
    public Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken cancellation)
        => scanner.ScanAsync(root, TransactionalLibrary.Existing(client.Required().Snapshot.Entries), progress: progress, cancellation: cancellation);
    public async Task<InstallationView> InstallationsAsync(string id, CancellationToken cancellation)
    {
        var details = await client.Required().InstallationsAsync(id, cancellation).ConfigureAwait(false);
        return await Task.Run(() => new InstallationView(details, [.. details.Items.Select(i =>
        {
            cancellation.ThrowIfCancellationRequested(); var probe = identity.Inspect(i.Folder);
            var status = probe.IsValid && probe.Observation!.ViaReparsePoint ? "目录别名 · 可用" : probe.Status switch
            { PreflightStatus.Valid => "可用", PreflightStatus.NotFound or PreflightStatus.BrokenLink => "路径不可用，记录保留", PreflightStatus.AccessDenied => "无权访问，记录保留", _ => "无法确认，记录保留" };
            return new InstallationRow(i, probe.Status, status, details.Preferred == i.Id);
        })]), cancellation).ConfigureAwait(false);
    }
    public async Task<ClientResult> ImportAsync(ImmutableArray<ScanCandidate> candidates, CancellationToken cancellation)
    {
        try
        {
            var library = client.Required();
            var prepared = await Task.Run(() => candidates.Select(c =>
            {
                cancellation.ThrowIfCancellationRequested(); preflight.Revalidate(c.Folder, reviewedCandidate: true);
                if (c.Executables.Length is < 1 or > 200) throw new InvalidDataException("候选必须含有 1 至 200 个游戏 exe。");
                var executables = c.Executables.Select(p => { var probe = identity.Inspect(p, false); if (!probe.IsValid) { preflight.Revalidate(c.Folder, reviewedCandidate: true); throw new InvalidDataException(probe.Message); } return probe.Observation!; }).ToImmutableArray();
                return new LocalImportItem(c.Name, Prepare(new(Guid.NewGuid().ToString(), c.Folder.StoredPath, c.Executables.Length == 1 ? c.Executables[0] : null, null, [], true), cancellation, c.Folder), executables);
            }).ToImmutableArray(), cancellation).ConfigureAwait(false);
            var result = await library.ImportLocalAsync(prepared, preflight, cancellation).ConfigureAwait(false);
            var presented = OfflineLibraryClient.Present(result);
            return presented with { Message = $"{presented.Message} 新增 {result.Value.Added} 个，重复跳过 {result.Value.Skipped} 个。" };
        }
        catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); }
    }
    public async Task<ClientResult> SaveAsync(InstallationDetails details, InstallationInput input, CancellationToken cancellation)
    {
        try
        {
            var library = client.Required(); var prepared = await Task.Run(() => Prepare(input, cancellation), cancellation).ConfigureAwait(false);
            return OfflineLibraryClient.Present(await library.SaveInstallationAsync(details.Token, prepared, input.Preferred, preflight, cancellation).ConfigureAwait(false));
        }
        catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); }
    }
    public Task<ClientResult> RemoveAsync(InstallationDetails details, string id, CancellationToken cancellation)
        => Present(() => client.Required().RemoveInstallationAsync(details.Token, id, cancellation));
    public Task<ClientResult> PreferAsync(InstallationDetails details, string id, CancellationToken cancellation)
        => Present(() => client.Required().PreferInstallationAsync(details.Token, id, cancellation));
    private static async Task<ClientResult> Present(Func<Task<CommitResult<LibraryEntry>>> action)
    { try { return OfflineLibraryClient.Present(await action().ConfigureAwait(false)); } catch (Exception e) when (OfflineLibraryClient.Expected(e)) { return OfflineLibraryClient.Failure(e); } }
    private PreparedInstallation Prepare(InstallationInput input, CancellationToken cancellation, PathObservation? reviewedCandidate = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var folder = identity.Inspect(input.Folder);
        if (reviewedCandidate is not null && (!folder.IsValid || !InstallationPreflightService.SameObservation(reviewedCandidate, folder.Observation!)))
            throw new CandidateStaleException();
        if (!folder.IsValid) throw new InvalidDataException(folder.Message);
        PathObservation? executable = null, working = null;
        if (!string.IsNullOrEmpty(input.Executable))
        {
            var probe = identity.Inspect(input.Executable, false);
            if (!probe.IsValid || probe.Observation!.IsDirectory || !BoundedScanner.IsGameExecutable(Path.GetFileName(input.Executable)) || !InstallationPreflightService.Within(probe.Observation.FinalPath, folder.Observation!.FinalPath))
                throw new InvalidDataException("启动文件必须是安装目录内的有效游戏 exe；不会执行该文件。");
            executable = probe.Observation;
        }
        if (!string.IsNullOrEmpty(input.WorkingDirectory))
        {
            var probe = identity.Inspect(input.WorkingDirectory);
            if (!probe.IsValid || probe.Observation!.Identity != folder.Observation!.Identity && !InstallationPreflightService.Within(probe.Observation.FinalPath, folder.Observation.FinalPath))
                throw new InvalidDataException("工作目录必须位于安装目录内且可读取。");
            working = probe.Observation;
        }
        if (input.Arguments.IsDefault || input.Arguments.Length > 100 || input.Arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')))
            throw new InvalidDataException("参数须为不超过 100 项的字符串数组，每项最多 8192 字符。");
        return new(new(input.Id, input.Folder, executable?.StoredPath, input.Arguments, working?.StoredPath), folder.Observation!, executable, working, reviewedCandidate);
    }
}
