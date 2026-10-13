namespace TheOrder.Application;

public sealed record ValveValue(string? Text, IReadOnlyDictionary<string, ValveValue>? Children)
{
    public string? String(string key) => Children?.GetValueOrDefault(key)?.Text;
}
public static class ValveKeyValues
{
    public static IReadOnlyDictionary<string, ValveValue> Parse(string source)
    {
        if (source.Length > 1_048_576 || source.Contains('\0')) throw new InvalidDataException("Steam 清单过大或无效。");
        int offset = 0, count = 0; return Object(0, false);
        Dictionary<string, ValveValue> Object(int depth, bool nested)
        {
            if (depth > 16) throw new InvalidDataException("Steam 清单嵌套过深。");
            var result = new Dictionary<string, ValveValue>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var key = Token(); if (key is null) { if (nested) throw new InvalidDataException("Steam 清单不完整。"); return result; }
                if (key == "}") { if (!nested) throw new InvalidDataException("Steam 清单无效。"); return result; }
                if (key == "{") throw new InvalidDataException("Steam 清单键无效。");
                var value = Token() ?? throw new InvalidDataException("Steam 清单缺少值。");
                if (value == "}") throw new InvalidDataException("Steam 清单缺少值。");
                if (!result.TryAdd(key, value == "{" ? new(null, Object(depth + 1, true)) : new(value, null))) throw new InvalidDataException("Steam 清单含重复字段。");
            }
        }
        string? Token()
        {
            if (++count > 50000) throw new InvalidDataException("Steam 清单项目过多。");
            while (offset < source.Length)
            {
                if (char.IsWhiteSpace(source[offset])) { offset++; continue; }
                if (source[offset] == '/' && offset + 1 < source.Length && source[offset + 1] == '/') { while (offset < source.Length && source[offset] != '\n') offset++; continue; }
                break;
            }
            if (offset == source.Length) return null;
            char c = source[offset++]; if (c is '{' or '}') return c.ToString();
            if (c != '"') { int start = offset - 1; while (offset < source.Length && !char.IsWhiteSpace(source[offset]) && source[offset] is not ('{' or '}')) offset++; return source[start..offset]; }
            var text = new System.Text.StringBuilder();
            while (offset < source.Length)
            {
                c = source[offset++]; if (c == '"') return text.ToString();
                if (c == '\\' && offset < source.Length && source[offset] is '"' or '\\') c = source[offset++];
                text.Append(c); if (text.Length > 32768) throw new InvalidDataException("Steam 清单字段过长。");
            }
            throw new InvalidDataException("Steam 清单字符串不完整。");
        }
    }
}
