using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsScanFilesystem : IScanFilesystem
{
    public IEnumerable<DirectoryItem> Enumerate(string directory)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            yield return new(item.FullName, item.Name, (item.Attributes & FileAttributes.Directory) != 0, (item.Attributes & FileAttributes.ReparsePoint) != 0);
    }
    public bool IsForbiddenRoot(PathObservation root)
    {
        var final = root.FinalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + root.FinalPath[8..] : root.FinalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? root.FinalPath[4..] : root.FinalPath;
        var forbidden = new[] { Path.GetPathRoot(final), Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) };
        return forbidden.Any(p => !string.IsNullOrEmpty(p) && string.Equals(Path.TrimEndingDirectorySeparator(p), Path.TrimEndingDirectorySeparator(final), StringComparison.OrdinalIgnoreCase));
    }
}
