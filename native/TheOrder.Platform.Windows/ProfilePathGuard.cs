using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Pure namespace/authorization boundary. Filesystem identity/reparse checks run only after this returns.
internal static class ProfilePathGuard
{
    internal sealed record KnownRoots(string Legacy, string Production, string Development, string? Beta = null);
    internal sealed record AccessProbe(KnownRoots Roots, Action BeforeFilesystem);
    // Internal, execution-context-local test seam; never configured by environment/command line.
    internal static readonly AsyncLocal<AccessProbe?> Probe = new();

    internal static string Validate(string? path, NativeProfileKind? kind = null, bool productionAuthorized = false)
    {
        if (kind is { } value && !Enum.IsDefined(value)) throw new NativeProfileException("InvalidProfileKind");
        var root = Canonicalize(path);
        var roots = Probe.Value?.Roots ?? new(NativeProfilePaths.LegacyRoot, NativeProfilePaths.ProductionRoot, NativeProfilePaths.DevelopmentRoot);
        var production = Canonicalize(roots.Production); var development = Canonicalize(roots.Development);
        if (NativeProfilePaths.Overlaps(root, Canonicalize(roots.Legacy))) throw new NativeProfileException("LegacyProfileForbidden");
        if (NativeProfilePaths.Overlaps(root, production) && (kind != NativeProfileKind.Production || !productionAuthorized
            || !root.Equals(production, StringComparison.OrdinalIgnoreCase))) throw new NativeProfileException("ProductionAuthorizationRequired");
        if (NativeProfilePaths.Overlaps(root, development) && (kind != NativeProfileKind.Development
            || !root.Equals(development, StringComparison.OrdinalIgnoreCase))) throw new NativeProfileException("WrongProfileScope");
        var beta = Canonicalize(roots.Beta ?? NativeProfilePaths.BetaRoot);
        if (NativeProfilePaths.Overlaps(root, beta) && (kind != NativeProfileKind.Beta
            || !root.Equals(beta, StringComparison.OrdinalIgnoreCase))) throw new NativeProfileException("WrongProfileScope");
        return root;
    }

    internal static string Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new NativeProfileException("InvalidProfilePath");
        var dos = path.Replace('/', '\\');
        if (dos.StartsWith(@"\\?\", StringComparison.Ordinal)) dos = dos[4..];
        // Only local absolute DOS paths (ordinary or extended). Reject UNC, device, NT, relative and ADS forms.
        if (dos.Length < 3 || !char.IsAsciiLetter(dos[0]) || dos[1] != ':' || dos[2] != '\\'
            || dos.IndexOf(':', 2) >= 0 || dos.IndexOfAny(['~', '\0', '*', '?', '"', '<', '>', '|']) >= 0
            || dos.Any(char.IsControl)) throw new NativeProfileException("InvalidProfilePath");
        foreach (var part in dos[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") continue;
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (part.EndsWith('.') || part.EndsWith(' ') || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'))
                throw new NativeProfileException("AmbiguousProfilePath");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dos));
    }

    internal static void BeforeFilesystem() => Probe.Value?.BeforeFilesystem();
}
