using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Recognition;
using Xunit;

namespace TheOrder.Tests;

public sealed class RecognitionEvidenceDifferentialTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/recognition-evidence-oracle.json")));
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()!, c.GetRawText() }).ToArray();
    }
    [Theory][MemberData(nameof(Cases))]
    public void MatchesActualElectronEvidence(string id, string json)
    {
        _ = id; using var doc = JsonDocument.Parse(json); var row = doc.RootElement; var expected = row.GetProperty("expected");
        var bytes = Convert.FromBase64String(row.GetProperty("bytes").GetString()!); string kind = row.GetProperty("kind").GetString()!;
        ParsedLocalEvidence actual; string fingerprint = "";
        if (kind == "pe")
        {
            using var stream = new MemoryStream(bytes);
            try { var parsed = new ExecutableEvidenceParser().Read(stream, row.GetProperty("name").GetString()!, default); actual = parsed.Evidence; fingerprint = parsed.Fingerprint; }
            catch (Exception e) when (e is InvalidDataException or System.Text.DecoderFallbackException) { Assert.NotEmpty(expected.GetProperty("warnings").EnumerateArray()); Assert.Empty(expected.GetProperty("titles").EnumerateArray()); return; }
            Assert.Equal(expected.GetProperty("executableFingerprints").EnumerateArray().Select(x => x.GetString()), fingerprint.Length == 0 ? [] : new[] { fingerprint });
        }
        else actual = new LocalEvidenceParser().Parse(kind, row.GetProperty("name").GetString()!, bytes, false);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("titles"), JsonSerializer.SerializeToElement(actual.Titles, Options)), json + "\nActual " + JsonSerializer.Serialize(actual, Options));
        Assert.Equal(expected.GetProperty("companies").EnumerateArray().Select(c => c.GetString()), actual.Companies);
        if (expected.GetProperty("warnings").GetArrayLength() > 0) Assert.NotEmpty(actual.Warnings);
    }
    [Fact] public void OracleTracksCurrentEvidenceSources()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/recognition-evidence-oracle.json")));
        foreach (var source in doc.RootElement.GetProperty("sources").EnumerateObject()) Assert.Equal(source.Value.GetString(), ReferenceSources.Hash(File.ReadAllText(Path.Combine(ReferenceSources.Repository(), source.Name)).Replace("\r\n", "\n")));
    }
    [Fact] public void ProductionIdentityMatchesAllSixLegacyVectorsIncludingLoneSurrogates()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/fingerprints.json")));
        foreach (var vector in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var game = vector.GetProperty("game"); string String(JsonElement value) => LegacyJson.String(value);
            var entry = RecognitionBoundaryTests.Entry() with {
                Name = String(game.GetProperty("name")), NameIsCustom = game.TryGetProperty("nameIsCustom", out var custom) && custom.ValueKind != JsonValueKind.Null ? custom.GetBoolean() : null,
                Metadata = game.GetProperty("metadata").ValueKind == JsonValueKind.Null ? null : RecognitionBoundaryTests.Work(String(game.GetProperty("metadata").GetProperty("id"))),
                PreferredInstallationId = game.GetProperty("preferredInstallationId").ValueKind == JsonValueKind.Null ? null : String(game.GetProperty("preferredInstallationId")),
                Installations = [.. game.GetProperty("installations").EnumerateArray().Select(i => new Installation(String(i.GetProperty("id")), String(i.GetProperty("folderPath")), i.GetProperty("executablePath").ValueKind == JsonValueKind.Null ? null : String(i.GetProperty("executablePath")), []))]
            };
            Assert.Equal(vector.GetProperty("serialized").GetString(), RecognitionFingerprint.SerializeIdentity(entry));
            Assert.Equal(vector.GetProperty("identity").GetString(), RecognitionFingerprint.Identity(entry));
        }
    }
}
