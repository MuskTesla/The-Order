using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheOrder.Infrastructure.Legacy;

// Legacy JavaScript strings are UTF-16 code units, including unpaired surrogates.
// Keep this exception to normal JSON string handling inside the read-only legacy boundary.
internal static class LegacyJson
{
    public static string String(JsonElement value)
    {
        var raw = value.GetRawText();
        return Decode(raw.AsSpan(1, raw.Length - 2));
    }

    private static string Decode(ReadOnlySpan<char> raw)
    {
        var result = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\') { result.Append(raw[i]); continue; }
            var escape = raw[++i]; // JsonDocument/Utf8JsonReader already checked escape syntax.
            if (escape == 'u')
            {
                result.Append((char)ushort.Parse(raw.Slice(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 4;
            }
            else result.Append(escape switch
            {
                '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f',
                'n' => '\n', 'r' => '\r', 't' => '\t', _ => throw new JsonException("Invalid legacy string escape.")
            });
        }
        return result.ToString();
    }

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new StringConverter(), new IntegerConverter<int>(), new IntegerConverter<long>() }
    };

    private sealed class StringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var value = JsonDocument.ParseValue(ref reader);
            return String(value.RootElement);
        }
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => throw new NotSupportedException("Legacy compatibility converters are read-only.");
    }

    // JavaScript parses all JSON numbers as doubles before checking integer constraints.
    // Do not accept numeric strings or truncate fractional values. Bounds are validated first.
    private sealed class IntegerConverter<T> : JsonConverter<T> where T : System.Numerics.IBinaryInteger<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = reader.GetDouble();
            if (!double.IsFinite(value) || Math.Truncate(value) != value) throw new JsonException("Expected an integral legacy number.");
            try { return T.CreateChecked(value); }
            catch (OverflowException ex) { throw new JsonException("Legacy integer is out of range.", ex); }
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => throw new NotSupportedException("Legacy compatibility converters are read-only.");
    }
}
