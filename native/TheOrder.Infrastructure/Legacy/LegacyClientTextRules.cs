using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Legacy;

public sealed class LegacyClientTextRules : IClientTextRules
{
    private static readonly CompareInfo Names = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
    private static readonly CompareInfo Ids = CultureInfo.GetCultureInfo("en-US").CompareInfo;
    public string NormalizeSearch(string value)
    {
        // JS normalization retains lone UTF-16 surrogates; its L/N filter then removes them.
        var valid = new StringBuilder();
        foreach (var rune in value.EnumerateRunes()) if (rune != Rune.ReplacementChar) valid.Append(rune.ToString());
        var normalized = LegacyPathCase.Lower(valid.ToString().Normalize(NormalizationForm.FormKC));
        var result = new StringBuilder();
        foreach (var rune in normalized.EnumerateRunes()) if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) result.Append(rune.ToString());
        return result.ToString();
    }
    public ImmutableArray<string> QueryTerms(string value) => [.. value.Split(LegacyLibraryCodec.Whitespace, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeSearch).Where(s => s.Length > 0)];
    public ImmutableArray<string> SplitLabels(string value) => [.. value.Split([',', '，', '\n']).Select(s => s.Trim(LegacyLibraryCodec.Whitespace)).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];
    public int CompareNames(string a, string b) => Names.Compare(a, b, CompareOptions.None);
    public int CompareIds(string a, string b) => Ids.Compare(a, b, CompareOptions.None);
}
