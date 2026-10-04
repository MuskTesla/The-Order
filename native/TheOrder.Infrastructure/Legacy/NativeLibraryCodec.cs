using System.Collections.Immutable;
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
        var normalized = legacy.Decode(document);
        var original = LegacyLibraryReader.Parse(document).ToDictionary(e => e.Id.Trim(LegacyLibraryCodec.Whitespace));
        return [.. normalized.Select(e => e with { Installations = [.. e.Installations.Select((i, index) => i with
        { Folder = original[e.Id].Installations[index].Folder, Executable = original[e.Id].Installations[index].Executable,
            WorkingDirectory = original[e.Id].Installations[index].WorkingDirectory })] })];
    }
    public LibraryDocument Inspect(ReadOnlyMemory<byte> document, string exportedAt)
    {
        ImmutableArray<LibraryEntry> entries;
        try { entries = Decode(document); } catch (InvalidDataException e) { return new(LibraryAccess.InvalidInput, [], null, e.Message); }
        try { return new(LibraryAccess.ValidWritable, entries, Encode(entries, exportedAt)); }
        catch (LibraryWriteLimitException e) { return new(LibraryAccess.ValidReadOnly, entries, null, e.Message); }
    }
    public byte[] Encode(ImmutableArray<LibraryEntry> entries, string exportedAt) => legacy.Encode(entries, exportedAt);
    public string InstallationKey(string path) => legacy.InstallationKey(path);
    public string EvidenceIdentity(LibraryEntry entry) => legacy.EvidenceIdentity(entry);
    public bool ProtectsName(LibraryEntry entry) => legacy.ProtectsName(entry);
    public WorkMetadata NormalizeWork(WorkMetadata work) => legacy.NormalizeWork(work);
}
