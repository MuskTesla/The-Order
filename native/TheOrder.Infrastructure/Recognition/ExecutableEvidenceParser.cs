using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;

namespace TheOrder.Infrastructure.Recognition;

// A seekable byte reader, not the Windows loader. Every offset/range and aggregate read is bounded.
public sealed class ExecutableEvidenceParser : IExecutableEvidenceParser
{
    private const int Limit = 1024 * 1024;
    private const string Helper = @"^(?:unins\d*|uninst|uninstall(?:er)?|delfile|setup|install(?:er)?|vcredist.*|vc_redist.*|dxsetup|directx.*|redist.*|unitycrashhandler.*|crashreport.*|crashpad.*|updater?|configure|configuration|config|cmvsconfig(?:32|64)?|opensavefolder|openconfigfolder|sigchk|krpatcher|sigluscounter|bootmenu|(?:r18)?dlc|steamclient.*|steam_api.*)(?:[._ -].*)?\.exe$|(?:^|[._ -])(?:r18dlc(?:installer)?|dlcinstaller|patchinstaller|uninstaller|setup|installer)(?:[._ -].*)?\.exe$|check(?:32|64)?\.exe$";
    private const string OtherHelper = @"^(?:alpharomdie|smartsteamloader|steamloader|reipatcher)(?:[._ -].*)?\.exe$|^(?:patch(?:er)?|crack(?:er)?|unlocker|keygen)(?:[._ -](?:v?\d+(?:[._-]\d+)*|x64|x86|chs|cht|cn|en|jp))*\.exe$|[._ -](?:crack|patcher|unlocker|keygen)(?:[._ -](?:v?\d+(?:[._-]\d+)*|x64|x86|chs|cht|cn|en|jp))*\.exe$";
    private const string Generic = @"^(?:game(?:exe|application|app)?|application|app|launcher|launch|start|main|engine|bootstrap|cmvs(?:config)?|bhvc|cs2|qlie|imosurume|sigchk|krpatcher|unknown|productname|filedescription|mygame|samplegame|testgame|data\d*|script\d*|resource\d*|system\d*|patch\d*|scenario\d*|movie\d*|sound\d*|voice\d*|image\d*|bgm\d*|unity(?:player|game)?|unreal(?:engine)?|kirikiri(?:z|2)?|krkr(?:z|2)?|tvp|吉里吉里(?:z|2)?|bgi|buriko(?:generalinterpreter)?|siglus(?:engine|start)?|reallive(?:engine|launcher)?|advhd|adv(?:player|engine)?|artemis(?:engine)?|ethornell|renpy(?:game)?|nscripter|onscripter|rpgmaker(?:mv|mz|vx|vxa|xp)?|wolfrpgeditor|tyranobuilder)(?:\d+)?$";
    private const string Engine = @"^(?:tvpkirikiri|tvpwin(?:32|64)|kirikiri(?:z|2)?core|吉里吉里(?:z|2)?(?:core|コア)|siglus(?:engine|visualarts|スタートアプリケーション|ゲーム実行エンジン)|(?:ethornell)?burikogeneralinterpreter|unityplayer|renpyruntime)";
    private const string Utility = @"^(?:digitalsignature(?:fileintegrity)?checktool|fileintegritychecktool|ファイル(?:破損|整合性)チェック(?:ツール)?|エンジン設定|起動設定|環境設定|alpharomdie)|(?:siglus|alpharom).*(?:一键解锁|解锁工具|破解器)";
    private const string Menu = @"(?:\s|[_\-–—])+[（(]?(?:開始メニュー|起動メニュー|スタートメニュー|(?:中文)?模组启动器|start\s*menu|launcher)[）)]?$";
    private static bool Match(string value, string pattern, bool ignoreCase = false) => TitleMatching.Rx(pattern, ignoreCase).IsMatch(value);
    private static string Compact(string value) => string.Concat(LegacyPathCase.Lower(value.Normalize(NormalizationForm.FormKC)).EnumerateRunes().Where(r => Rune.IsLetter(r) || Rune.IsNumber(r)).Select(r => r.ToString()));
    public bool IsUsefulExecutable(string name) => Match(name, @"\.exe$", true) && !Match(name, Helper, true) && !Match(name, OtherHelper, true);
    public bool IsUsefulTitle(string title)
    {
        string value = Compact(title), variant = TitleMatching.Rx(@"(?:(?:chs|cht|cn|jp|patch|crack|x64|x86|32bit|64bit))+$").Replace(TitleMatching.Rx(@"(?:v\d+)$").Replace(value, ""), "");
        return value.Length >= 3 && value.Any(char.IsLetter) && !Match(value, Generic) && !Match(variant, Generic) && !Match(value, Engine) && !Match(value, Utility) && !Match(value, @"^(?:microsoft|visualstudio|windowsapplication|todo)");
    }
    private static string Clean(string value) => LocalEvidenceParser.Clean(value.Split('\0')[0]) is var s ? s[..Math.Min(s.Length, 512)] : "";
    private static string Stem(string value) => TitleMatching.Rx(@"\.(?:exe|ain|pfs|ypf)$", true).Replace(Path.GetFileName(value.Replace('/', '\\')), "");
    private static InvalidDataException Invalid() => new("程序版本资源超出边界或格式无效。");
    private static bool Range(long offset, long length, long size) => offset >= 0 && length >= 0 && offset <= size && length <= size - offset;
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
    private sealed record Fields(List<string> Products, List<string> Descriptions, List<string> Companies, List<string> OriginalNames);
    public ExecutableEvidence Read(Stream stream, string basename, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!stream.CanRead || !stream.CanSeek) throw Invalid();
        int total = 0;
        byte[] Read(long offset, int size)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Range(offset, size, stream.Length) || size > Limit || total + size > Limit + 64 * 1024) throw Invalid();
            total += size; var bytes = new byte[size]; stream.Position = offset;
            for (int n = 0; n < size;) { cancellation.ThrowIfCancellationRequested(); int count = stream.Read(bytes, n, size - n); if (count == 0) throw Invalid(); n += count; }
            return bytes;
        }
        static ExecutableEvidence Empty() => new(new([], [], [], []), "");
        if (stream.Length < 2) return Empty();
        var dos = Read(0, (int)Math.Min(64, stream.Length)); if (dos[0] != 0x4d || dos[1] != 0x5a) return Empty();
        if (dos.Length < 64) throw Invalid();
        uint peOffset = U32(dos, 60); if (peOffset < 64 || peOffset > 64 * 1024) throw Invalid();
        var header = Read(peOffset, 24); if (U32(header, 0) != 0x4550) throw Invalid();
        int sectionCount = U16(header, 6), optionalSize = U16(header, 20);
        if (sectionCount is < 1 or > 96 || optionalSize is < 112 or > 4096) throw Invalid();
        var optional = Read(peOffset + 24L, optionalSize); int directoryStart = U16(optional, 0) switch { 0x10b => 96, 0x20b => 112, _ => -1 };
        if (directoryStart < 0 || optionalSize < directoryStart + 24) throw Invalid();
        if (U32(optional, directoryStart - 4) < 3) return Empty();
        long resourceRva = U32(optional, directoryStart + 16), resourceSize = U32(optional, directoryStart + 20);
        if (resourceRva == 0 || resourceSize == 0) return Empty();
        if (resourceSize < 16 || !Range(resourceRva, resourceSize, 0x100000000)) throw Invalid();
        var table = Read(peOffset + 24L + optionalSize, sectionCount * 40);
        long FileOffset(long rva, int length)
        {
            if (!Range(rva, length, 0x100000000)) throw Invalid(); long offset = -1;
            for (int i = 0; i < sectionCount; i++)
            {
                long address = U32(table, i * 40 + 12), rawSize = U32(table, i * 40 + 16), rawOffset = U32(table, i * 40 + 20);
                if (!Range(rva - address, length, rawSize)) continue;
                if (offset != -1) throw Invalid(); offset = rawOffset + rva - address;
            }
            if (!Range(offset, length, stream.Length)) throw Invalid(); return offset;
        }
        byte[] Relative(long offset, int size) { if (!Range(offset, size, resourceSize)) throw Invalid(); return Read(FileOffset(resourceRva + offset, size), size); }
        var visited = new HashSet<long>();
        (uint Name, uint Target)[] Directory(long offset)
        {
            if (visited.Count >= 32 || !visited.Add(offset)) throw Invalid();
            var h = Relative(offset, 16); int count = U16(h, 12) + U16(h, 14); if (count > 256) throw Invalid();
            var bytes = Relative(offset + 16, count * 8); return Enumerable.Range(0, count).Select(i => (U32(bytes, i * 8), U32(bytes, i * 8 + 4))).ToArray();
        }
        var version = Directory(0).FirstOrDefault(e => e.Name == 16); if (version.Name == 0) return Empty();
        if ((version.Target & 0x80000000) == 0) throw Invalid();
        var resources = new List<byte[]>(); int resourceBytes = 0;
        foreach (var name in Directory(version.Target & 0x7fffffff).Take(4))
        {
            if ((name.Target & 0x80000000) == 0) throw Invalid();
            foreach (var language in Directory(name.Target & 0x7fffffff).Take(4))
            {
                if ((language.Target & 0x80000000) != 0) throw Invalid();
                var data = Relative(language.Target, 16); long size = U32(data, 4);
                if (size < 8 || size > Limit || resourceBytes + size > Limit) throw Invalid();
                resourceBytes += (int)size; resources.Add(Read(FileOffset(U32(data, 0), (int)size), (int)size));
            }
        }
        var parsed = resources.Select(r => ParseVersion(r, cancellation)).ToArray();
        if (parsed.Any(f => f.OriginalNames.Any(n => Match(Path.GetFileName(n), Helper, true) || Match(Path.GetFileName(n), OtherHelper, true)))) return new(new([], [], [], []), "", true);
        var titles = new List<LocalTitleEvidence>(); var companies = new List<string>(); bool fingerprintable = false;
        void Title(string value, string source)
        {
            value = Clean(value); bool program = source is "product-name" or "file-description";
            if (program) value = TitleMatching.Rx(Menu, true).Replace(value, "").Trim();
            if (IsUsefulTitle(value) && !titles.Any(t => Compact(t.Value) == Compact(value) && (t.Source == source || program && t.Source is "product-name" or "file-description"))) titles.Add(new(value, source));
        }
        foreach (var fields in parsed)
        {
            cancellation.ThrowIfCancellationRequested();
            bool hasTitle = fields.Products.Concat(fields.Descriptions).Any(n => IsUsefulTitle(TitleMatching.Rx(Menu, true).Replace(Clean(n), "").Trim()));
            foreach (var value in fields.Products) Title(value, "product-name");
            foreach (var value in fields.Descriptions) Title(value, "file-description");
            foreach (var value in fields.OriginalNames) if (Path.GetExtension(value) is "" || Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase)) Title(Stem(value), "executable-name");
            if (hasTitle) companies.AddRange(fields.Companies.Where(c => c.Length > 0 && !Match(Compact(c), @"^(?:companyname|unknown|yourcompany|todo)", true)));
            fingerprintable |= fields.Products.Any(p => IsUsefulTitle(p) && !Match(p, Menu, true));
        }
        string hash = "";
        if (fingerprintable)
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var resource in resources) digest.AppendData(resource);
            digest.AppendData(Encoding.UTF8.GetBytes("\0" + LegacyPathCase.Lower(basename.Normalize(NormalizationForm.FormKC))));
            hash = "pe-version-sha256:" + Convert.ToHexStringLower(digest.GetHashAndReset());
        }
        return new(new([.. titles.Take(16)], [], [.. companies.Distinct(StringComparer.Ordinal).Take(8)], []), hash);
    }
    private static Fields ParseVersion(byte[] bytes, CancellationToken cancellation)
    {
        var fields = new Fields([], [], [], []); int blocks = 0;
        var targets = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["ProductName"] = fields.Products, ["FileDescription"] = fields.Descriptions, ["CompanyName"] = fields.Companies, ["OriginalFilename"] = fields.OriginalNames };
        var decoder = new UnicodeEncoding(false, false, true);
        static int Align(int value) => (value + 3) & ~3;
        int Block(int offset, int end, string[] parents)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++blocks > 1024 || parents.Length > 5 || !Range(offset, 6, end)) throw Invalid();
            int length = U16(bytes, offset), valueLength = U16(bytes, offset + 2), type = U16(bytes, offset + 4);
            if (length < 8 || !Range(offset, length, end) || type > 1) throw Invalid();
            int blockEnd = offset + length, cursor = offset + 6;
            while (cursor + 2 <= blockEnd && cursor - offset < 520 && U16(bytes, cursor) != 0) cursor += 2;
            if (cursor + 2 > blockEnd || U16(bytes, cursor) != 0) throw Invalid();
            string key = decoder.GetString(bytes, offset + 6, cursor - offset - 6);
            if (parents.Length == 0 && key != "VS_VERSION_INFO") throw Invalid();
            cursor = Align(cursor + 2); int valueBytes = valueLength * (type == 1 ? 2 : 1);
            if (valueBytes > 0 && !Range(cursor, valueBytes, blockEnd)) throw Invalid();
            if (parents.Length == 3 && parents[0] == "VS_VERSION_INFO" && parents[1] == "StringFileInfo" && Match(parents[2], @"^[0-9a-f]{8}$", true) && type == 1 && targets.TryGetValue(key, out var target))
            { string value = Clean(decoder.GetString(bytes, cursor, valueBytes)); if (value.Length > 0) target.Add(value); }
            cursor = Align(cursor + valueBytes);
            while (cursor + 6 <= blockEnd)
            {
                if (U16(bytes, cursor) == 0) { if (bytes.AsSpan(cursor, blockEnd - cursor).ContainsAnyExcept((byte)0)) throw Invalid(); break; }
                cursor = Align(Block(cursor, blockEnd, [.. parents, key]));
            }
            return blockEnd;
        }
        Block(0, bytes.Length, []); return fields;
    }
}
