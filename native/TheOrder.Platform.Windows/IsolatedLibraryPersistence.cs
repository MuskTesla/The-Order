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
            leases.AddRange(PinDirectoryChain(root));
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
    internal static List<IDisposable> PinDirectoryChain(string root)
    {
        var handles = new List<IDisposable>();
        try
        {
            for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            {
                var handle = CreateFile(directory.FullName, 0, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("无法固定合成目录。", new Win32Exception()); }
                handles.Add(handle);
            }
            return handles;
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
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
    private Task<PersistenceReceipt> WriteAtomic(string name, ReadOnlyMemory<byte> bytes, PersistenceTarget kind)
        => WindowsAtomicFile.WriteAsync(Path.Combine(root, name), bytes, stage => Reach(kind, stage));
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
}
