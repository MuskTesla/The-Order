using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TheOrder.Tests;

// An experiment only, not a pending-review approval service. Tokens must be reissued in a new process.
public sealed class FingerprintExperimentTests
{
    [Fact]
    public void MatchesCurrentJavaScriptSourceVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/fingerprints.json")));
        foreach (var vector in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var game = vector.GetProperty("game");
            // JsonElement.GetString rejects lone surrogates. The JS oracle contains one deliberately;
            // compare that vector through its raw JSON string tokens, preserving their UTF-16 escapes.
            var serialized = "[" + StringToken(game.GetProperty("name")) + "," +
                (game.TryGetProperty("nameIsCustom", out var custom) ? custom.GetRawText() : "null") + "," +
                (game.GetProperty("metadata").ValueKind == JsonValueKind.Null ? "null" : StringToken(game.GetProperty("metadata").GetProperty("id"))) + "," +
                NullableToken(game.GetProperty("preferredInstallationId")) + ",[" +
                string.Join(',', game.GetProperty("installations").EnumerateArray().Select(i => "[" + StringToken(i.GetProperty("id")) + "," + StringToken(i.GetProperty("folderPath")) + "," + NullableToken(i.GetProperty("executablePath")) + "]")) + "]]";
            Assert.Equal(vector.GetProperty("serialized").GetString(), serialized);
            Assert.Equal(vector.GetProperty("identity").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))));
            var hasToken = vector.TryGetProperty("token", out var token);
            var decision = "[" + StringToken(vector.GetProperty("identity")) + "," +
                (hasToken ? StringToken(token.GetProperty("epoch")) + "," + StringToken(token.GetProperty("generation")) + "," + token.GetProperty("requestId").GetRawText() : "null,null,null") + "]";
            Assert.Equal(vector.GetProperty("decisionSerialized").GetString(), decision);
            Assert.Equal(vector.GetProperty("decisionFingerprint").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(decision))));
        }
    }
    private static string NullableToken(JsonElement e) => e.ValueKind == JsonValueKind.Null ? "null" : StringToken(e);
    private static string StringToken(JsonElement e)
    {
        // Decode JSON escapes to UTF-16 without replacing unpaired surrogates.
        var raw = e.GetRawText(); var decoded = new StringBuilder();
        for (var i = 1; i < raw.Length - 1; i++)
        {
            var c = raw[i];
            if (c != '\\') { decoded.Append(c); continue; }
            c = raw[++i];
            if (c == 'u') { decoded.Append((char)Convert.ToUInt16(raw.Substring(i + 1, 4), 16)); i += 4; }
            else decoded.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => c });
        }
        var text = decoded.ToString(); var result = new StringBuilder("\"");
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { result.Append(c); result.Append(text[++i]); }
                    else if (c < 32 || char.IsSurrogate(c)) result.Append("\\u").Append(((int)c).ToString("x4"));
                    else result.Append(c);
                    break;
            }
        }
        return result.Append('"').ToString();
    }
    [Fact]
    public void DefaultDotNetSerializationIsNotJavaScriptFingerprintSerialization()
    {
        var value = "中文 <>&\u2028";
        Assert.NotEqual("[\"" + value + "\"]", JsonSerializer.Serialize(new[] { value }));
    }
}
