using System.Text;
using System.Text.RegularExpressions;

namespace TheOrder.Infrastructure.Legacy;

// Only the legacy naming-protection predicate, not the matching/scoring engine.
internal static class LegacyTitleProtection
{
    private const RegexOptions Flags = RegexOptions.CultureInvariant | RegexOptions.ECMAScript;
    private const string Packaging = @"^(?:(?:pc|windows|win(?:32|64)|x(?:86|64)|chs|cht|cn|jp|en)|(?:简体|繁体|官方)?中文(?:版)?|官中(?:版)?|汉化(?:硬盘|整合)?版?|(?:日文|英文|日语|英语|简中|繁中)版?|免安装(?:版)?|硬盘版|绿色版|直装版|解压即玩)$";
    private const string Suffix = @"(?:[\s._-]+(?:chs|cht|x64|x86|win32|win64)|[\s._-]*(?:(?:简体|繁体|官方)中文(?:版)?|官中(?:版)?|汉化(?:硬盘|整合)?版|免安装(?:版)?|硬盘版|绿色版|直装版|解压即玩))$";
    public static bool HasVersion(string value)
    {
        if (value.Length > 500) return false;
        // .NET normalization rejects lone surrogates; JS keeps those code units. They are separators here.
        value = Regex.Replace(value, @"[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]", " ");
        var s = Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"[\x00-\x1f\x7f]", " ").Trim(LegacyLibraryCodec.Whitespace);
        s = Regex.Replace(s, @"\[([^\[\]]*)\]|【([^【】]*)】|\(([^()]*)\)", m =>
        {
            var text = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
            var tokens = Regex.Split(text.Trim(), @"[\s,+/|・]+").Where(t => t.Length > 0).ToArray();
            return tokens.Length > 0 && tokens.All(t => Regex.IsMatch(t, Packaging, Flags | RegexOptions.IgnoreCase)) ? " " : m.Value;
        });
        string old;
        do { old = s; s = Regex.Replace(s, Suffix, "", Flags | RegexOptions.IgnoreCase).Trim(LegacyLibraryCodec.Whitespace); } while (s != old);
        // Accent folding applies to Latin only, preserving Japanese voiced marks.
        s = Regex.Replace(s.Normalize(NormalizationForm.FormD), @"([A-Za-z\u00c0-\u024f])\p{M}+", "$1").Normalize(NormalizationForm.FormC);
        s = LegacyPathCase.Lower(s);
        s = Regex.Replace(s, @"[^\p{L}\p{N}\p{M}]+", " ").Trim();
        return Regex.IsMatch(s, @"\b(?:remake(?:\s+for\s+(?:fhd|qhd|hd))?(?:\s+edition)?|remaster(?:ed)?(?:\s+edition)?|re\s*boot(?:\s+edition)?|reflection\s*blue(?:\s+edition)?|sana(?:\s+edition)?|(?:qhd|fhd|hd|4k)(?:\s+edition)?|r\s*18(?:\s+edition)?|all\s*ages(?:\s+edition)?|(?:complete|ultimate|deluxe|definitive|extended|renewal|special|perfect)\s+edition|(?:fd|fan\s*disc)(?:\s+edition)?|after(?:\s*story)?|new\s*(?:episode|chapter))\b|全年[齢龄齡](?:版)?|リメイク(?:版)?|重[製制]版|リマスター(?:版)?|ファンディスク|アフターストーリー|新[章篇編编]|\b\d+(?:st|nd|rd|th)?\s+anniversary(?:\s+edition)?\b|\d+周年(?:記念|纪念|紀念)?(?:版)?|\bv\s*\d+(?:\s+\d+)*\b|\b(?:episodes?|ep|chapters?|chap|volumes?|vol|part|act)\s*(?:\d+|viii|vii|iii|vi|iv|ii|ix|one|two|three|four|five|six|seven|eight|nine|ten|[ivx])\b|第[0-9〇零一二三四五六七八九十百壱壹弐貳贰参參拾]+(?:章|話|话|巻|卷|部|作)", Flags);
    }
}
