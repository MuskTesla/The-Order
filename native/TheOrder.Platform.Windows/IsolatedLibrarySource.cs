using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class IsolatedLibrarySource : IIsolatedLibrarySource
{
    public const string MarkerName = ".the-order-synthetic";
    public const string MarkerValue = "the-order-native-phase1-synthetic-v1";

    public Stream OpenRead(string? explicitDirectory)
        => OpenChecked(Path.Combine(ValidateDirectory(explicitDirectory), "library.json"));

    internal static string ValidateDirectory(string? explicitDirectory)
    {
        string root;
        try { root = ProfilePathGuard.Validate(explicitDirectory); }
        catch (NativeProfileException) { throw new DataIsolationException("资料路径命名空间或范围不符合隔离要求，已在访问前拒绝。"); }
        ProfilePathGuard.BeforeFilesystem();
        for (DirectoryInfo? current = new(root); current is not null; current = current.Parent)
        {
            if (!current.Exists) throw new DirectoryNotFoundException();
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new DataIsolationException("合成资料目录不能经过链接或重解析点。");
        }
        var markerPath = Path.Combine(root, MarkerName);
        if (!File.Exists(markerPath)) throw new DataIsolationException("缺少合成资料标记，已拒绝读取。");
        using (var marker = OpenChecked(markerPath))
        {
            if (marker.Length > 128) throw new DataIsolationException("合成资料标记无效。");
            using var text = new StreamReader(marker, Encoding.UTF8, true, 128);
            if (text.ReadToEnd().Trim() != MarkerValue) throw new DataIsolationException("缺少有效合成资料标记，已拒绝读取。");
        }
        return root;
    }

    internal static FileStream OpenChecked(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new DataIsolationException("拒绝读取链接文件。");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var final = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, final, final.Capacity, 0);
            if (length == 0 || length >= final.Capacity || !GetFileInformationByHandle(stream.SafeFileHandle, out var info)) throw new IOException("无法验证文件身份。", new Win32Exception());
            var actual = final.ToString();
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
            else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (!actual.Equals(path, StringComparison.OrdinalIgnoreCase) || info.NumberOfLinks != 1) throw new DataIsolationException("文件真实路径或链接数量不符合隔离要求。");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
}
