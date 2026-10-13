using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record RecentGameActivation(Guid ProfileId, Guid StoreId, string GameId, string Kind, string Root)
{
    public static RecentGameActivation? Parse(string[] args)
    {
        if (args.Length != 10 || args[0] != "--jump-game" || args[2] != "--jump-profile" || args[4] != "--jump-store" || args[6] != "--jump-kind" || args[8] != "--jump-root"
            || !ValidGameId(args[1]) || !Guid.TryParseExact(args[3], "D", out var profile) || !Guid.TryParseExact(args[5], "D", out var store)
            || !Enum.TryParse<NativeProfileKind>(args[7], false, out var kind) || !Enum.IsDefined(kind) || !Path.IsPathFullyQualified(args[9]) || args[9].Length > 32768 || args[9].Contains('\0')) return null;
        return new(profile, store, args[1], args[7], args[9]);
    }
    // Imported legacy bookshelves may use non-GUID IDs. They remain opaque lookup keys,
    // never executable names or command lines; activation must also match profile/store.
    private static bool ValidGameId(string id) => id.Length is > 0 and <= 256 && !id.Any(char.IsControl);
    public string[] Arguments => ["--jump-game", GameId, "--jump-profile", ProfileId.ToString("D"), "--jump-store", StoreId.ToString("D"), "--jump-kind", Kind, "--jump-root", Root];
    public bool Matches(Guid profile, Guid store, string kind, string root) => ProfileId == profile && StoreId == store && Kind == kind && string.Equals(Path.GetFullPath(Root).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static ImmutableArray<LibraryEntry> Recent(IEnumerable<LibraryEntry> entries) => [.. entries.Where(e => ValidGameId(e.Id) && e.LastPlayedAt is not null && e.Installations.Any(i => i.Executable is not null))
        .OrderByDescending(e => DateTimeOffset.TryParse(e.LastPlayedAt, out var time) ? time : DateTimeOffset.MinValue).Take(5)];
}
