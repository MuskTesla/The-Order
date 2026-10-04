using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;

namespace TheOrder.Infrastructure.Recognition;

// Pure, bounded parsing. The platform collector supplies bytes, never executable modules.
public sealed class LocalEvidenceParser : ILocalEvidenceParser
{
    internal static Regex Rx(string pattern, bool ignoreCase = false) => TitleMatching.Rx(pattern, ignoreCase);
    private const string TitleKey = @"^(?:game_?title|product_?name|title|ゲームタイトル|タイトル|游戏名称|游戏标题|遊戲名稱)$";
    private const string CompanyKey = @"^(?:brand|maker|company|publisher|developer|メーカー|ブランド|会社名|厂商|品牌|开发商)$";
    private const string Generic = @"^(?:readme|manual|usermanual|webmanual|onlinehelp|help|index|contents|tableofcontents|introduction|installation|systemrequirements|ゲーム|ゲームタイトル|タイトル|製品|本製品|ゲームマニュアル|マニュアル|説明書|取扱説明書|ユーザーマニュアル|ウェブマニュアル|webマニュアル|お読みください|はじめに|目次|ゲームについて|本製品について|製品について|游戏|游戏名称|游戏说明|说明书|使用说明|目録|目錄|目录|注意事項|ご注意|動作環境|起動方法|インストール方法|アンインストール方法|操作方法|game|gameexe|application|siglusengine|siglus|kirikiri|tvp|bgi|ethornell|reallive)$";
    private const string Boilerplate = @"^(?:インストール|アンインストール|ゲームの(?:起動|終了|操作|削除)|操作説明|操作方法|著作権|免責|トラブル|サポート|修正履歴|更新履歴|動作環境|ご利用|ご注意|注意事項|版权|免责声明|安装方法|运行环境|操作说明)";
    static LocalEvidenceParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static string Clean(string value) => Rx(@"\s+").Replace(Rx(@"[\u0000-\u001f\u007f]").Replace(value.TrimStart('\ufeff'), " "), " ").Trim();
    private static string Compact(string value) => string.Concat(LegacyPathCase.Lower(value.Normalize(NormalizationForm.FormKC))
        .EnumerateRunes().Where(r => Rune.IsLetter(r) || Rune.IsNumber(r)).Select(r => r.ToString()));
    private static bool Meaningful(string value)
    {
        var key = Compact(value);
        return value.Length <= 180 && key.Length >= 3 && key.Any(char.IsLetter) && !Rx(Generic).IsMatch(key)
            && !Rx(Boilerplate).IsMatch(value) && !Rx(@"[<>\uFFFD]|https?://|(?:^|\s)[A-Za-z]:[\\/]", true).IsMatch(value);
    }
    public ParsedLocalEvidence Parse(string kind, string name, ReadOnlyMemory<byte> bytes, bool truncated)
    {
        if (bytes.Length > (kind == "sidecar" ? 1024 * 1024 : 128 * 1024)) throw new InvalidDataException("本地线索超过解析上限。");
        var titles = new List<LocalTitleEvidence>(); var references = new List<ReferenceEvidence>(); var companies = new List<string>();
        void Title(string value, string source) { value = Clean(value); if (titles.Count < 16 && Meaningful(value) && !titles.Any(t => Compact(t.Value) == Compact(value))) titles.Add(new(value, source)); }
        void Company(string value) { value = Rx("^[\"'「『]|[\"'」』]$").Replace(Clean(value), "").Trim(); if (companies.Count < 8 && value.Length <= 100 && Meaningful(value) && !companies.Contains(value, StringComparer.Ordinal)) companies.Add(value); }
        void Reference(string value, string source) { var normalized = NormalizeReference(value); if (normalized is not null && references.Count < 8 && !references.Any(r => r.Value == normalized)) references.Add(new(normalized, source)); }
        try
        {
            if (kind == "sidecar")
            {
                using var json = JsonDocument.Parse(Decode(bytes.Span, false, "sidecar"), new() { MaxDepth = 16 });
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new([], [], [], []);
                if (root.TryGetProperty("Game", out var game) && game.ValueKind == JsonValueKind.Object) root = game;
                foreach (string key in new[] { "Name", "OriginalName", "ChineseName", "CnName" })
                    if (root.TryGetProperty(key, out var v)) { if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Value", out var wrapped)) v = wrapped; if (v.ValueKind == JsonValueKind.String) Title(LegacyJson.String(v), "sidecar"); }
                if (root.TryGetProperty("Ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var (index, source) in new[] { (0, "vndb"), (1, "bangumi"), (7, "steam") })
                        if (index < ids.GetArrayLength() && ids[index].ValueKind is JsonValueKind.String or JsonValueKind.Number) Reference(source + ":" + (ids[index].ValueKind == JsonValueKind.String ? ids[index].GetString() : ids[index].GetRawText()), "sidecar");
                    if (ids.GetArrayLength() > 2 && ids[2].ValueKind == JsonValueKind.String)
                        foreach (var reference in ids[2].GetString()!.Split(',').Take(30)) Reference(reference, "sidecar");
                }
            }
            else
            {
                var text = Decode(bytes.Span, truncated, kind);
                if (text.Contains('\0')) return new([], [], [], []);
                if (kind == "readme-heading")
                {
                    string heading = string.Join(' ', Rx(@"\r?\n").Split(text).Take(24));
                    return new([new(heading[..Math.Min(3000, heading.Length)], "readme-heading")], [], [], []);
                }
                if (kind == "steam")
                {
                    if (name.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase)) Reference("steam:" + text.Trim(), "steam-config");
                    else foreach (Match match in Rx(@"(?:^|\r?\n)\s*AppId\s*=\s*(\d+)\s*(?:[;#][^\r\n]*)?(?=\r?\n|$)", true).Matches(text)) Reference("steam:" + match.Groups[1].Value, "steam-config");
                }
                else if (kind == "config")
                {
                    foreach (var line in Rx(@"\r?\n").Split(text))
                    {
                        if (line.Length > 512) continue;
                        var match = Rx("^\\s*#?\\s*([^=;#\\r\\n]{1,32})\\s*=\\s*(?:\"([^\"\\r\\n]*)\"|'([^'\\r\\n]*)'|([^;#\\r\\n]*))\\s*(?:[;#].*)?$").Match(line);
                        if (!match.Success) continue;
                        string key = match.Groups[1].Value.Trim(), value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                        if (Rx(TitleKey, true).IsMatch(key)) Title(value, "config-title");
                        if (Rx(CompanyKey, true).IsMatch(key)) Company(value);
                    }
                }
                else
                {
                    var safe = Rx(@"<!--[\s\S]*?(?:-->|$)").Replace(text, " ");
                    safe = Rx(@"<(script|style|svg|template|iframe|object|noscript)\b[^>]*>[\s\S]*?(?:</\1\s*>|$)", true).Replace(safe, " ");
                    var headings = Rx(@"\.html?$", true).IsMatch(name)
                        ? Rx(@"<(title|h[1-3])\b[^>]*>([\s\S]*?)</\1\s*>", true).Matches(safe).Take(12).Select(m => EntityText(Rx(@"<[^>]*>").Replace(m.Groups[2].Value, " "))).ToArray()
                        : Rx(@"\r?\n").Split(safe).Take(8).Where(line => Rx(@"[『「《][^』」》]+[』」》]|^(?:ゲームタイトル|タイトル|游戏名称|title)\s*[:：]", true).IsMatch(line.Trim())).ToArray();
                    foreach (var heading in headings) foreach (var value in Heading(heading)) Title(value, "manual-title");
                    var plain = EntityText(Rx(@"[\t ]+").Replace(Rx(@"<[^>]*>").Replace(safe, " "), " "));
                    foreach (Match match in Rx(@"(?:弊社製品|本製品|ゲームタイトル|游戏名称)\s*[:：]?\s*[「『《]([^」』》\r\n]{3,180})[」』》]").Matches(plain)) Title(match.Groups[1].Value, "manual-title");
                    foreach (var line in headings.Concat(Rx(@"\r?\n").Split(plain)).Take(300))
                    {
                        var labelled = Rx(@"^\s*(?:メーカー|ブランド|開発元|厂商|品牌|開發商|developer|publisher|brand)\s*[:：]\s*([^\r\n]{3,100})\s*$", true).Match(line);
                        if (labelled.Success) Company(labelled.Groups[1].Value);
                        var branded = Rx(@"^\s*([^『「\r\n]{3,60})[『「]([^』」\r\n]{3,180})[』」]について\s*$").Match(line);
                        if (branded.Success && titles.Any(t => Compact(t.Value) == Compact(branded.Groups[2].Value))) Company(branded.Groups[1].Value);
                    }
                }
            }
            return new([.. titles], [.. references], [.. companies], []);
        }
        catch (Exception e) when (e is DecoderFallbackException or JsonException or ArgumentException or RegexMatchTimeoutException)
        { return new([], [], [], ["部分说明标题或配置无法安全读取。"]); }
    }
    private static IEnumerable<string> Heading(string input)
    {
        string text = Clean(input);
        if (text.Length > 500 || Rx(@"^(?:メーカー|ブランド|開発元|厂商|品牌|開發商|developer|publisher|brand)\s*[:：]", true).IsMatch(text)) return [];
        var quoted = Rx(@"[『「《]([^』」》\r\n]{3,180})[』」》]").Matches(text).Select(m => Clean(m.Groups[1].Value)).ToArray();
        if (quoted.Length > 0) return quoted.Where(Meaningful);
        text = Rx(@"^(?:お読みくださ[いイ]|お読み下さい|はじめにお読みください|read\s*me|(?:ゲーム)?タイトル|游戏名称|title\s*[:：])\s*[:：\-–—]?\s*", true).Replace(text, "");
        text = Rx(@"\s*(?:[-–—|:：]\s*)?(?:(?:web|online|ウェブ|オンライン)?\s*(?:マニュアル|manual|説明書|取扱説明書)(?:へのリンク)?|について|開始メニュー|スタートメニュー)\s*$", true).Replace(text, "").Trim();
        return Meaningful(text) ? [text] : [];
    }
    private static string EntityText(string value) => Rx(@"&(#x[0-9a-f]{1,6}|#[0-9]{1,7}|amp|quot|apos|lt|gt|nbsp);", true).Replace(value, m =>
    {
        var entity = m.Groups[1].Value.ToLowerInvariant();
        if (!entity.StartsWith('#')) return entity switch { "amp" => "&", "quot" => "\"", "apos" => "'", "lt" => "<", "gt" => ">", "nbsp" => " ", _ => m.Value };
        int code = int.Parse(entity[(entity.StartsWith("#x", StringComparison.Ordinal) ? 2 : 1)..], entity.StartsWith("#x", StringComparison.Ordinal) ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture);
        return code >= 32 && Rune.IsValid(code) ? char.ConvertFromUtf32(code) : "";
    });
    public static string? NormalizeReference(string value)
    {
        var match = Rx(@"^(vndb|bangumi|bgm|steam)\s*:\s*(v?[1-9]\d{0,11})$", true).Match(value.Trim());
        if (!match.Success) return null;
        string source = match.Groups[1].Value.ToLowerInvariant(), id = match.Groups[2].Value.ToLowerInvariant();
        if (source == "bgm") source = "bangumi";
        if (source != "vndb" && id.StartsWith('v')) return null;
        if (source == "vndb") id = "v" + id.TrimStart('v');
        return id.TrimStart('v', '0').Length == 0 || source == "steam" && id == "480" ? null : source + ":" + id;
    }
    internal static string Decode(ReadOnlySpan<byte> bytes, bool truncated, string kind)
    {
        static string Read(ReadOnlySpan<byte> bytes, int codePage, bool truncated)
        {
            var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var decoder = encoding.GetDecoder(); var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            decoder.Convert(bytes, chars, !truncated, out _, out int count, out _);
            return new string(chars, 0, count).TrimStart('\ufeff');
        }
        if (bytes.StartsWith(new byte[] { 255, 254 })) return Read(bytes[2..], 1200, truncated);
        if (bytes.StartsWith(new byte[] { 254, 255 }) && kind != "steam") return Read(bytes[2..], 1201, truncated);
        if (kind == "steam") return Read(bytes, 65001, truncated);
        string declaration = Rx("charset\\s*=\\s*[\"']?\\s*([a-z0-9_-]+)", true).Match(Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 4096)])).Groups[1].Value.ToLowerInvariant();
        int declared = declaration switch { "utf-8" or "utf8" => 65001, "shift_jis" or "shift-jis" or "sjis" or "windows-31j" => 932, "gbk" or "gb2312" or "gb18030" => 54936, _ => 0 };
        if (declared != 0) return Read(bytes, declared, truncated);
        try { return Read(bytes, 65001, truncated); } catch (DecoderFallbackException) { }
        if (kind == "sidecar") return Read(bytes, 54936, truncated);
        var alternatives = new List<string>();
        foreach (int page in new[] { 932, 54936 }) try { alternatives.Add(Read(bytes, page, truncated)); } catch (DecoderFallbackException) { }
        if (alternatives.Count == 0) throw new DecoderFallbackException();
        static double Score(string s) => Rx(@"ゲーム|タイトル|メーカー|ブランド|マニュアル|游戏|标题|名称|厂商|品牌").Count(s) * 20
            + Rx(@"[\u3040-\u30ff]").Count(s) * 2 + Rx(@"[\u4e00-\u9fff]").Count(s) * .5 - Rx(@"[\uff61-\uff9f]").Count(s) * 4;
        return alternatives.OrderByDescending(Score).First();
    }
}
