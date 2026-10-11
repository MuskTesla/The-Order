using System.Runtime.InteropServices;

namespace TheOrder.Infrastructure.Recognition;

// Bounded retrieval spellings, never new local identity facts. A subtitle-free query may find
// a parent/other chapter; the untouched full evidence and final release checks still decide.
internal static class LocalQueryVariants
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        [Out] char[]? destination, int destinationLength, nint version, nint reserved, nint sortHandle);

    internal static string[] Chinese(string value)
    {
        if (!OperatingSystem.IsWindows() || value.Length is < 2 or > 500
            || !value.Any(c => c is >= '\u3400' and <= '\u9fff')
            || value.Any(c => c is >= '\u3040' and <= '\u30ff')) return [];
        var variants = new List<string>(2);
        foreach (var (flags, locale) in new[] { (0x02000000u, "zh-CN"), (0x04000000u, "zh-TW") })
        {
            try
            {
                // Unicode NLS mapping avoids StrConv's ANSI-codepage conversion failures. Use
                // explicit locales, bounded output, no persistent/global locale changes.
                int length = LCMapStringEx(locale, flags, value, value.Length, null, 0, 0, 0, 0);
                if (length is < 1 or > 1000) continue;
                var buffer = new char[length];
                int written = LCMapStringEx(locale, flags, value, value.Length, buffer, length, 0, 0, 0);
                if (written != length) continue;
                var spelling = new string(buffer);
                if (spelling != value && !variants.Contains(spelling, StringComparer.Ordinal)) variants.Add(spelling);
            }
            catch (Exception e) when (e is ArgumentException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
            { } // An unavailable opposite-script capability must not discard a successful spelling.
        }
        return [.. variants];
    }

    internal static string[] Retrieval(string value)
    {
        if (value.Length is < 2 or > 500) return [];
        var head = TitleMatching.Rx(@"\s*(?:~|～|〜|:|：|—|–|--)\s*").Split(value, 2)[0].Trim();
        var key = TitleMatching.IndexKey(head);
        bool specific = key.All(char.IsAscii) ? key.Length >= 8 : key.EnumerateRunes().Count() >= 4;
        return [.. Chinese(value), .. head != value && specific && TitleMatching.Compare(head, head).Score > 0 ? new[] { head } : []];
    }

    // Only a non-machine *Chinese* official title can corroborate Chinese script spelling.
    // Conversion is neither a translation nor a Japanese romanization/identity shortcut.
    internal static bool ChineseExact(string local, string official, string? language, string[]? spellings = null)
    {
        if (language is not ("zh" or "zh-Hans" or "zh-Hant" or "zh-CN" or "zh-TW")) return false;
        if (TitleMatching.IndexKey(local).EnumerateRunes().Count() < 4 || TitleMatching.IndexKey(official).EnumerateRunes().Count() < 4
            || TitleMatching.Compare(local, official).Conflict is not null) return false;
        return (spellings ?? Chinese(local)).Any(v => TitleMatching.Compare(v, official) is { Exact: true, Distinctive: true, Version: "same", Conflict: null });
    }
}
