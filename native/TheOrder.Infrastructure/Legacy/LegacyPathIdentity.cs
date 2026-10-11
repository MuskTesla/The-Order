namespace TheOrder.Infrastructure.Legacy;

// Lexical Windows identity for legacy library records, never a file-access authorization.
// Mirrors library.ts absolutePath (trim/isAbsolute/normalize) + pathKey (resolve/lowercase).
// Unlike filesystem canonicalization, Node preserves trailing dots/spaces in path segments.
public static class LegacyPathIdentity
{
    public static string Create(string storedPath, string? currentDirectory = null)
    {
        var path = storedPath.Trim(EcmaWhitespace).Replace('/', '\\');
        var (device, start, absolute) = Root(path);
        if (!absolute) throw new InvalidDataException("旧版安装路径必须为绝对路径。");
        if (device.Length == 0)
        {
            var context = (currentDirectory ?? Environment.CurrentDirectory).Replace('/', '\\');
            (device, _, _) = Root(context);
            if (device.Length == 0) throw new InvalidDataException("无法解析旧版安装路径的当前驱动器。");
        }
        var segments = new List<string>();
        foreach (var part in path[start..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (segments.Count != 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(part);
        }
        return LegacyPathCase.Lower(device + "\\" + string.Join('\\', segments));
    }

    public static bool Contains(string rootKey, string targetKey) => targetKey == rootKey ||
        targetKey.StartsWith(rootKey.TrimEnd('\\') + "\\", StringComparison.Ordinal);

    // path.win32.normalize retains case, root-relative paths and a trailing separator.
    public static string Normalize(string storedPath)
    {
        var path = storedPath.Trim(EcmaWhitespace).Replace('/', '\\');
        var (device, start, absolute) = Root(path);
        if (!absolute) throw new InvalidDataException("旧版安装路径必须为绝对路径。");
        var parts = new List<string>();
        foreach (var part in path[start..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part != ".") parts.Add(part);
        return device + "\\" + string.Join('\\', parts) + (parts.Count > 0 && path.EndsWith('\\') ? "\\" : "");
    }

    private static (string Device, int Start, bool Absolute) Root(string path)
    {
        if (path.Length == 0) return ("", 0, false);
        if (path[0] == '\\')
        {
            // Exactly two leading separators can introduce a UNC server/share.
            if (path.Length > 2 && path[1] == '\\' && path[2] != '\\')
            {
                var serverEnd = path.IndexOf('\\', 2);
                if (serverEnd > 2)
                {
                    var shareStart = serverEnd;
                    while (shareStart < path.Length && path[shareStart] == '\\') shareStart++;
                    var shareEnd = path.IndexOf('\\', shareStart);
                    if (shareEnd < 0) shareEnd = path.Length;
                    if (shareEnd > shareStart) return (path[..serverEnd] + "\\" + path[shareStart..shareEnd], shareEnd, true);
                }
            }
            return ("", 1, true);
        }
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\') return (path[..2], 3, true);
        return ("", 0, false);
    }

    private static readonly char[] EcmaWhitespace = ['\u0009', '\u000a', '\u000b', '\u000c', '\u000d', '\u0020', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff'];
}
