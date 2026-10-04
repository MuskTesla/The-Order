using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TheOrder.Infrastructure.Legacy;

// String.toLowerCase uses Unicode full lowercase + contextual final sigma, not .NET
// invariant simple lowercase. The generated reference snapshot avoids machine NLS drift.
internal static class LegacyPathCase
{
    private sealed record CaseData(Dictionary<int, string> Lower, int[][] Cased, int[][] Ignorable);
    private static readonly CaseData Data = Load();
    private static CaseData Load()
    {
        using var stream = typeof(LegacyPathCase).Assembly.GetManifestResourceStream("TheOrder.Infrastructure.Legacy.LegacyUnicodeData.json")
            ?? throw new InvalidOperationException("Missing legacy path case data.");
        using var document = JsonDocument.Parse(stream); var root = document.RootElement;
        int[][] Ranges(string name) => root.GetProperty(name).EnumerateArray().Select(r => r.EnumerateArray().Select(n => n.GetInt32()).ToArray()).ToArray();
        return new(root.GetProperty("lower").EnumerateObject().ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetString()!), Ranges("cased"), Ranges("ignorable"));
    }
    public static string Lower(string value)
    {
        var scalars = new List<int>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) scalars.Add(char.ConvertToUtf32(value[i], value[++i]));
            else scalars.Add(value[i]); // Preserve lone UTF-16 code units exactly, like JavaScript.
        }
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < scalars.Count; i++)
        {
            var cp = scalars[i];
            if (cp == 0x03a3 && CasedBeyond(i, -1) && !CasedBeyond(i, 1)) result.Append('\u03c2');
            else if (Data.Lower.TryGetValue(cp, out var lower)) result.Append(lower);
            else if (cp <= char.MaxValue) result.Append((char)cp);
            else result.Append(char.ConvertFromUtf32(cp));
        }
        return result.ToString();

        bool CasedBeyond(int index, int step)
        {
            for (index += step; index >= 0 && index < scalars.Count; index += step)
            {
                var cp = scalars[index];
                if (InRanges(Data.Ignorable, cp)) continue;
                return InRanges(Data.Cased, cp);
            }
            return false;
        }
    }
    private static bool InRanges(int[][] ranges, int cp)
    {
        int left = 0, right = ranges.Length - 1;
        while (left <= right)
        {
            var middle = (left + right) / 2; var range = ranges[middle];
            if (cp < range[0]) right = middle - 1;
            else if (cp > range[1]) left = middle + 1;
            else return true;
        }
        return false;
    }
}
