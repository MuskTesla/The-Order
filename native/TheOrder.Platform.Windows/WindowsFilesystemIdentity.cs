using System.ComponentModel;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsFilesystemIdentity : IFilesystemIdentity
{
    private const uint DirectoryFlag = 0x02000000, OpenReparse = 0x00200000, ReparseAttribute = 0x400;
    private const uint Symlink = 0xA000000C, MountPoint = 0xA0000003;
    public PathPreflight Inspect(string storedPath, bool requireDirectory = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(storedPath) || !Path.IsPathFullyQualified(storedPath) || storedPath.StartsWith(@"\\.\", StringComparison.Ordinal)
                || storedPath.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
                return Failure(PreflightStatus.OtherIOError, "请选择完整的本地或 UNC 路径。");
            var display = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storedPath));
            // Inspect every lexical ancestor without following its final reparse point. Unknown filters fail closed.
            bool alias = false; uint tag = 0;
            var reparsePaths = ImmutableArray.CreateBuilder<string>();
            var ancestors = new Stack<string>();
            for (string? part = display; part is not null; part = Path.GetDirectoryName(part))
            {
                if (ancestors.Count >= 512) return Failure(PreflightStatus.OtherIOError, "目录层级过多。");
                ancestors.Push(part);
            }
            while (ancestors.TryPop(out var part))
            {
                using var entry = Open(part, true);
                if (entry.IsInvalid) return NativeFailure(Marshal.GetLastWin32Error(), alias);
                if (!GetFileInformationByHandleEx(entry, 9, out AttributeTag info, (uint)Marshal.SizeOf<AttributeTag>()))
                    return Failure(PreflightStatus.IdentityUnavailable, "无法检查目录的重解析信息。", Marshal.GetLastWin32Error());
                if ((info.Attributes & ReparseAttribute) == 0) continue;
                alias = true; tag = info.Tag;
                reparsePaths.Add(part);
                if (tag is not (Symlink or MountPoint)) return Failure(PreflightStatus.UnsupportedReparseCase, "此重解析类型暂不支持，请选择普通目录、junction 或目录符号链接。");
            }
            using var handle = Open(display, false, requireDirectory);
            if (handle.IsInvalid) return NativeFailure(Marshal.GetLastWin32Error(), alias);
            if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag attributes, (uint)Marshal.SizeOf<AttributeTag>()))
                return NativeFailure(Marshal.GetLastWin32Error(), alias);
            bool directory = (attributes.Attributes & 0x10) != 0;
            if ((attributes.Attributes & ReparseAttribute) != 0 && attributes.Tag is not (Symlink or MountPoint))
                return Failure(PreflightStatus.UnsupportedReparseCase, "最终目标属于不支持的重解析类型。");
            if (requireDirectory && !directory) return Failure(PreflightStatus.FileInsteadOfDirectory, "所选路径是文件，请选择文件夹。");
            if (!GetFileInformationByHandleEx(handle, 18, out FileIdInfo identity, (uint)Marshal.SizeOf<FileIdInfo>()))
                return Failure(PreflightStatus.IdentityUnavailable, "此文件系统不能提供可靠的 128 位文件身份，未允许写入安装关系。", Marshal.GetLastWin32Error());
            var final = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
            if (length == 0 || length >= final.Capacity) return Failure(PreflightStatus.IdentityUnavailable, "无法取得句柄的最终路径。", Marshal.GetLastWin32Error());
            // FILE_ID_INFO is deliberately not narrowed to 64 bits (ReFS needs all 128).
            var key = new FilesystemIdentity(identity.Volume.ToString("X16"), $"{identity.Low:X16}{identity.High:X16}");
            return new(PreflightStatus.Valid, new(storedPath, display, final.ToString(), key, directory, alias, tag) { ReparsePaths = reparsePaths.ToImmutable() }, "可用");
        }
        catch (UnauthorizedAccessException) { return Failure(PreflightStatus.AccessDenied, "无权访问此目录，请选择可读取的目录。"); }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        { return Failure(PreflightStatus.OtherIOError, "路径不可用或无法读取。"); }
    }
    private static SafeFileHandle Open(string path, bool noFollow, bool listDirectory = false) => CreateFile(path, listDirectory ? 0x81u : 0x80u, 7, IntPtr.Zero, 3, DirectoryFlag | (noFollow ? OpenReparse : 0), IntPtr.Zero);
    private static PathPreflight Failure(PreflightStatus status, string message, int error = 0) => new(status, null, message, error);
    private static PathPreflight NativeFailure(int error, bool alias) => error switch
    {
        2 or 3 => Failure(alias ? PreflightStatus.BrokenLink : PreflightStatus.NotFound, alias ? "链接目标不存在。" : "目录不存在。", error),
        5 => Failure(PreflightStatus.AccessDenied, "无权访问此目录，请选择可读取的目录。", error),
        1921 or 4392 or 4393 or 4394 => Failure(PreflightStatus.UnsupportedReparseCase, "链接循环或重解析目标不可用。", error),
        _ => Failure(PreflightStatus.OtherIOError, "无法读取路径：" + new Win32Exception(error).Message, error)
    };
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, Tag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
