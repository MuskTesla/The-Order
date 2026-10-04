using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public enum PersistenceStage { BeforeBackup, BeforeTempWrite, DuringTempWrite, AfterTempWrite, BeforeReplace, Replace, AfterReplace }
public enum PersistenceTarget { Backup, Main }
public interface IPersistenceProbe
{
    Task ReachAsync(PersistenceTarget target, PersistenceStage stage);
}

// Synthetic profiles only. No UI registration and no implicit environment/profile fallback.
public sealed class IsolatedLibraryPersistence : ILibraryPersistence
{
    private readonly string root;
    private readonly IPersistenceProbe? probe;
    private readonly List<IDisposable> leases = [];
    private bool disposed;
    public IsolatedLibraryPersistence(string explicitDirectory, IPersistenceProbe? probe = null)
    {
        root = IsolatedLibrarySource.ValidateDirectory(explicitDirectory); this.probe = probe;
        try
        {
            // Pin the directory chain against rename/junction substitution for the lease lifetime.
            for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            {
                var handle = CreateFile(directory.FullName, 0, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("无法固定合成目录。", new Win32Exception()); }
                leases.Add(handle);
            }
            IsolatedLibrarySource.ValidateDirectory(root);
            leases.Add(IsolatedLibrarySource.OpenChecked(Path.Combine(root, IsolatedLibrarySource.MarkerName)));
            var lockPath = Path.Combine(root, ".the-order-writer.lock");
            // OpenOrCreate does not truncate an existing file. Check links before opening and hold
            // exclusive sharing afterwards. No data bytes are ever written to this lock file.
            CheckExisting(lockPath);
            leases.Add(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch { Dispose(); throw; }
    }
    public byte[]? ReadMain() => Read("library.json");
    public byte[]? ReadBackup() => Read("library.json.bak");
    private byte[]? Read(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            using var stream = IsolatedLibrarySource.OpenChecked(Path.Combine(root, name));
            if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("游戏库超过 32 MiB。");
            using var buffer = new MemoryStream(); stream.CopyTo(buffer);
            if (buffer.Length > 32 * 1024 * 1024) throw new InvalidDataException("游戏库超过 32 MiB。");
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
    }
    public async Task<PersistenceReceipt> SaveAsync(ReadOnlyMemory<byte> previous, ReadOnlyMemory<byte> candidate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        IsolatedLibrarySource.ValidateDirectory(root);
        await Reach(PersistenceTarget.Backup, PersistenceStage.BeforeBackup).ConfigureAwait(false);
        var backup = await WriteAtomic("library.json.bak", previous, PersistenceTarget.Backup).ConfigureAwait(false);
        var main = await WriteAtomic("library.json", candidate, PersistenceTarget.Main).ConfigureAwait(false);
        return new(main.Warning ?? backup.Warning);
    }
    private async Task<PersistenceReceipt> WriteAtomic(string name, ReadOnlyMemory<byte> bytes, PersistenceTarget kind)
    {
        var target = Path.Combine(root, name); var temp = Path.Combine(root, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool committed = false;
        try
        {
            CheckExisting(target);
            await Reach(kind, PersistenceStage.BeforeTempWrite).ConfigureAwait(false);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var split = bytes.Length / 2;
                await file.WriteAsync(bytes[..split]).ConfigureAwait(false);
                await Reach(kind, PersistenceStage.DuringTempWrite).ConfigureAwait(false);
                await file.WriteAsync(bytes[split..]).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            await Reach(kind, PersistenceStage.AfterTempWrite).ConfigureAwait(false);
            await Reach(kind, PersistenceStage.BeforeReplace).ConfigureAwait(false);
            CheckExisting(target);
            await Reach(kind, PersistenceStage.Replace).ConfigureAwait(false);
            // Same-directory MoveFileEx REPLACE_EXISTING | WRITE_THROUGH, one atomic namespace switch.
            // No target is ever truncated or deleted first. The backup is the canonical lastGood.
            if (!MoveFileEx(temp, target, 0x1 | 0x8)) throw new IOException("原子替换失败。", new Win32Exception());
            committed = true;
            await Reach(kind, PersistenceStage.AfterReplace).ConfigureAwait(false);
            return new();
        }
        catch (Exception) when (committed)
        {
            // The primary namespace switch has already succeeded. Never report it as rolled back.
            // Backup-only warnings are safe to continue: its new contents equal lastGood.
            return new("替换已完成；提交后检查发生 I/O 错误。");
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private Task Reach(PersistenceTarget target, PersistenceStage stage) => probe?.ReachAsync(target, stage) ?? Task.CompletedTask;
    private static void CheckExisting(string path)
    {
        try { using var checkedFile = IsolatedLibrarySource.OpenChecked(path); }
        catch (FileNotFoundException) { }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string target, uint flags);
}
