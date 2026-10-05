using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Current-user/machine DPAPI binding: this local observation is not a portable launch permission.
public sealed class IsolatedLaunchPositionStore(NativeConfiguration configuration) : ILaunchPositionStore
{
    private const int Maximum = 16 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Envelope(int Version, ImmutableArray<LaunchPosition> Positions);
    private const string FileName = "native-launch-positions.dpapi";
    public Task<ImmutableArray<LaunchPosition>> ReadAsync(CancellationToken cancellation) => Run(root =>
    {
        try
        {
            using var stream = IsolatedLibrarySource.OpenChecked(Path.Combine(root, FileName));
            if (stream.Length is < 1 or > Maximum) throw new InvalidDataException("启动位置核对记录大小无效。");
            var encrypted = new byte[(int)stream.Length]; stream.ReadExactly(encrypted);
            var plain = WindowsDataProtection.Protect(encrypted, false);
            var envelope = JsonSerializer.Deserialize<Envelope>(plain) ?? throw new InvalidDataException("启动位置记录无效。");
            if (envelope.Version != 1) throw new InvalidDataException("启动位置记录版本不支持。");
            Validate(envelope.Positions); return envelope.Positions;
        }
        catch (FileNotFoundException) { return []; }
        catch (Exception error) when (error is JsonException or CryptographicException)
        { throw new InvalidDataException("本机启动位置记录损坏或不属于当前用户，未自动覆盖。", error); }
    }, cancellation);
    public Task WriteAsync(ImmutableArray<LaunchPosition> positions, CancellationToken cancellation) => Run(root =>
    {
        Validate(positions);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, positions));
        if (plain.Length > Maximum - 1024) throw new InvalidDataException("启动位置记录容量不足。");
        var bytes = WindowsDataProtection.Protect(plain, true);
        if (bytes.Length > Maximum) throw new InvalidDataException("启动位置记录容量不足。");
        var target = Path.Combine(root, FileName); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        void Check() { try { using var existing = IsolatedLibrarySource.OpenChecked(target); } catch (FileNotFoundException) { } }
        Check();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            Check(); cancellation.ThrowIfCancellationRequested(); File.Move(temporary, target, true); return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancellation);
    private static void Validate(ImmutableArray<LaunchPosition> positions)
    {
        if (positions.IsDefault || positions.Length > 10000) throw new InvalidDataException("启动位置记录容量或结构无效。");
        var keys = new HashSet<(string, string)>();
        foreach (var position in positions)
        {
            if (position is null || string.IsNullOrWhiteSpace(position.GameId) || string.IsNullOrWhiteSpace(position.InstallationId)
                || !keys.Add((position.GameId, position.InstallationId))) throw new InvalidDataException("启动位置记录身份无效。");
            foreach (var path in new[] { position.FolderObservation, position.ExecutableObservation, position.WorkingObservation })
                if (path is null || path.Identity is null || string.IsNullOrEmpty(path.Identity.Volume) || string.IsNullOrEmpty(path.Identity.FileId)
                    || string.IsNullOrEmpty(path.StoredPath) || string.IsNullOrEmpty(path.FinalPath)
                    || !Path.IsPathFullyQualified(path.StoredPath) || !Path.IsPathFullyQualified(path.FinalPath) || path.ReparsePaths.IsDefault)
                    throw new InvalidDataException("启动位置观察无效。");
            if (!position.FolderObservation.IsDirectory || position.ExecutableObservation.IsDirectory || !position.WorkingObservation.IsDirectory
                || position.Folder != position.FolderObservation.StoredPath || position.Executable != position.ExecutableObservation.StoredPath
                || position.WorkingDirectory != position.WorkingObservation.StoredPath) throw new InvalidDataException("启动位置与观察不一致。");
        }
    }
    private async Task<T> Run<T>(Func<string, T> action, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var root = IsolatedLibrarySource.ValidateDirectory(configuration.DataDirectory);
                var pins = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { IsolatedLibrarySource.ValidateDirectory(root); cancellation.ThrowIfCancellationRequested(); return action(root); }
                finally { foreach (var pin in pins) pin.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
