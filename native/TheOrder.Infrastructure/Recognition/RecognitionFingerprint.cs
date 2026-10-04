using System.Security.Cryptography;
using System.Text.Json;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;

namespace TheOrder.Infrastructure.Recognition;

public static class RecognitionFingerprint
{
    public const string LegacyVersion = "electron-review-identity-v1";
    public const string EvidenceVersion = "native-local-evidence-v2";
    private static readonly JsonSerializerOptions Options = new() { Converters = { new LegacyLibraryCodec.Utf16Writer() } };
    public static string SerializeIdentity(LibraryEntry entry) => JsonSerializer.Serialize(new object?[] {
        entry.Name, entry.NameIsCustom, entry.Metadata?.Id, entry.PreferredInstallationId,
        entry.Installations.Select(i => new object?[] { i.Id, i.Folder, i.Executable }).ToArray()
    }, Options);
    public static string Identity(LibraryEntry entry) => Hash(System.Text.Encoding.UTF8.GetBytes(SerializeIdentity(entry)));
    // File identity, selected byte digest and directory observations are supplied by bounded IO.
    // This is an input comparison, never a capability authorizing a library transaction.
    public static string Evidence(IEnumerable<string> observations) => Hash(JsonSerializer.SerializeToUtf8Bytes(
        new object[] { EvidenceVersion, observations.Order(StringComparer.Ordinal).ToArray() }, Options));
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
