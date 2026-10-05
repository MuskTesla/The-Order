using System.Collections.Immutable;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Legacy;

// Native persistence retains the user's spelling; the Electron oracle codec still models its
// historical path normalization. Both share the same complete schema validation and limits.
public sealed class NativeLibraryCodec : ILibraryCodec
{
    private readonly LegacyLibraryCodec legacy = new();
    public ImmutableArray<LibraryEntry> Decode(ReadOnlyMemory<byte> document)
    {
        try { return DecodeCore(document); }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { throw new InvalidDataException("原生 session JSON 无效。", e); }
    }
    private ImmutableArray<LibraryEntry> DecodeCore(ReadOnlyMemory<byte> document)
    {
        var normalized = legacy.Decode(document);
        var original = LegacyLibraryReader.Parse(document).ToDictionary(e => e.Id.Trim(LegacyLibraryCodec.Whitespace));
        using var json = JsonDocument.Parse(document);
        var sessions = new Dictionary<(string, string), NativeSession>();
        foreach (var game in json.RootElement.GetProperty("games").EnumerateArray())
            foreach (var session in game.GetProperty("sessions").EnumerateArray())
                if (session.TryGetProperty("native", out var n))
                    sessions.Add((LegacyJson.String(game.GetProperty("id")).Trim(LegacyLibraryCodec.Whitespace), LegacyJson.String(session.GetProperty("id")).Trim(LegacyLibraryCodec.Whitespace)),
                        NativeSessionCodec.Read(n, session.GetProperty("seconds").GetInt64()));
        var nativeIds = new Dictionary<string, NativeSession>(StringComparer.Ordinal);
        foreach (var (key, value) in sessions)
        {
            if (!Guid.TryParseExact(key.Item2, "D", out _) || !value.IsTerminal && value.GameId != key.Item1)
                throw new InvalidDataException("原生 session binding 无效。");
            // Historical aliases can contain the same terminal invocation before an explicit merge.
            // New creation is globally unique; conflicting/active duplicates can never be admitted.
            if (nativeIds.TryGetValue(key.Item2, out var previous) && (!value.IsTerminal || previous != value))
                throw new InvalidDataException("冲突的 native SessionId。");
            nativeIds[key.Item2] = value;
        }
        return [.. normalized.Select(e => e with { Installations = [.. e.Installations.Select((i, index) => i with
        { Folder = original[e.Id].Installations[index].Folder, Executable = original[e.Id].Installations[index].Executable,
            WorkingDirectory = original[e.Id].Installations[index].WorkingDirectory })],
            Sessions = [.. e.Sessions.Select(s => sessions.TryGetValue((e.Id, s.Id), out var n) ? s with { Native = n } : s)] })];
    }
    public LibraryDocument Inspect(ReadOnlyMemory<byte> document, string exportedAt)
    {
        ImmutableArray<LibraryEntry> entries;
        try { entries = Decode(document); }
        catch (UnsupportedSessionSchemaException) { return new(LibraryAccess.ValidReadOnly, legacy.Decode(document), null, "UnsupportedSessionSchema：当前版本只能读取，不能覆盖未来 Session 字段。"); }
        catch (InvalidDataException e) { return new(LibraryAccess.InvalidInput, [], null, e.Message); }
        try { return new(LibraryAccess.ValidWritable, entries, Encode(entries, exportedAt)); }
        catch (LibraryWriteLimitException e) { return new(LibraryAccess.ValidReadOnly, entries, null, e.Message); }
    }
    public byte[] Encode(ImmutableArray<LibraryEntry> entries, string exportedAt)
    {
        var bytes = legacy.Encode(entries, exportedAt);
        if (!entries.Any(e => e.Sessions.Any(s => s.Native is not null))) return bytes;
        var games = entries.Select(e =>
        {
            var projected = LegacyLibraryCodec.Project(e);
            projected["sessions"] = e.Sessions.Select(s =>
            {
                var record = new Dictionary<string, object?> { ["id"] = s.Id, ["startedAt"] = s.StartedAt, ["endedAt"] = s.EndedAt, ["seconds"] = s.Seconds };
                if (s.Native is { } n)
                {
                    n.Validate();
                    if (n.Accumulated100ns / NativeSession.TicksPerSecond != s.Seconds) throw new InvalidDataException("Session marker 不一致。");
                    record["native"] = n;
                }
                return record;
            });
            return projected;
        });
        bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = "kun-gal-manager", version = 1, exportedAt, games }, NativeSessionCodec.Options);
        if (bytes.Length > LegacyLibraryReader.MaximumBytes) throw new LibraryWriteLimitException();
        return bytes;
    }
    public string InstallationKey(string path) => legacy.InstallationKey(path);
    public string EvidenceIdentity(LibraryEntry entry) => legacy.EvidenceIdentity(entry);
    public bool ProtectsName(LibraryEntry entry) => legacy.ProtectsName(entry);
    public WorkMetadata NormalizeWork(WorkMetadata work) => legacy.NormalizeWork(work);
}
