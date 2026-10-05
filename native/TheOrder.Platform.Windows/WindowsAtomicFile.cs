using System.ComponentModel;
using System.Runtime.InteropServices;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Shared Phase 2 atomic replacement primitive; callers retain transaction and ownership policy.
internal static class WindowsAtomicFile
{
    public static async Task<PersistenceReceipt> WriteAsync(string target, ReadOnlyMemory<byte> bytes, Func<PersistenceStage, Task>? probe = null)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool committed = false;
        try
        {
            CheckExisting(target);
            await Reach(probe, PersistenceStage.BeforeTempWrite).ConfigureAwait(false);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var split = bytes.Length / 2;
                await file.WriteAsync(bytes[..split]).ConfigureAwait(false);
                await Reach(probe, PersistenceStage.DuringTempWrite).ConfigureAwait(false);
                await file.WriteAsync(bytes[split..]).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            await Reach(probe, PersistenceStage.AfterTempWrite).ConfigureAwait(false);
            await Reach(probe, PersistenceStage.BeforeReplace).ConfigureAwait(false);
            CheckExisting(target);
            await Reach(probe, PersistenceStage.Replace).ConfigureAwait(false);
            // Same-directory MoveFileEx REPLACE_EXISTING | WRITE_THROUGH, one atomic namespace switch.
            // No target is ever truncated or deleted first. The backup is the canonical lastGood.
            if (!MoveFileEx(temp, target, 0x1 | 0x8)) throw new IOException("原子替换失败。", new Win32Exception());
            committed = true;
            await Reach(probe, PersistenceStage.AfterReplace).ConfigureAwait(false);
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
    private static Task Reach(Func<PersistenceStage, Task>? probe, PersistenceStage stage) => probe?.Invoke(stage) ?? Task.CompletedTask;
    internal static void CheckExisting(string path)
    {
        try { using var file = IsolatedLibrarySource.OpenChecked(path); }
        catch (FileNotFoundException) { }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string target, uint flags);
}