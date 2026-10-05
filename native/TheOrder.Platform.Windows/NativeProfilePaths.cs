using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed record NativeProfileRequest(NativeProfileKind Kind, string Root, bool ExplicitProductionAuthorization = false,
    Guid? ExpectedProfileId = null, Guid? ExpectedStoreId = null);

public static class NativeProfilePaths
{
    // Merely computing these strings never probes, creates, enumerates or reads formal profiles.
    public static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KunGalManager");
    public static string ProductionRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheOrder", "Profiles", "Production");
    public static string DevelopmentRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheOrder", "Profiles", "Development");
    public static NativeProfileRequest Select(NativeProfileKind buildKind, string? explicitKind, string? explicitRoot)
    {
        var kind = buildKind;
        if (explicitKind is not null && (!Enum.TryParse(explicitKind, false, out kind) || !Enum.IsDefined(kind)))
            throw new NativeProfileException("InvalidProfileKind");
        if (kind == NativeProfileKind.Test && string.IsNullOrWhiteSpace(explicitRoot)) throw new NativeProfileException("TestRequiresExplicitRoot");
        return new(kind, explicitRoot ?? (kind == NativeProfileKind.Production ? ProductionRoot : DevelopmentRoot));
    }
    public static string Validate(NativeProfileRequest request)
    {
        if (!Enum.IsDefined(request.Kind) || !Path.IsPathFullyQualified(request.Root) || request.Root.StartsWith(@"\\", StringComparison.Ordinal)
            || request.Root.Contains('~') || request.Root.IndexOf(':', 2) >= 0) throw new NativeProfileException("InvalidProfilePath");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Root));
        if (root.Split('\\', '/').Any(part => part.EndsWith('.') || part.EndsWith(' '))) throw new NativeProfileException("AmbiguousProfilePath");
        if (Overlaps(root, LegacyRoot)) throw new NativeProfileException("LegacyProfileForbidden");
        if (Overlaps(root, ProductionRoot) && (request.Kind != NativeProfileKind.Production || !request.ExplicitProductionAuthorization
            || !root.Equals(ProductionRoot, StringComparison.OrdinalIgnoreCase))) throw new NativeProfileException("ProductionAuthorizationRequired");
        if (Overlaps(root, DevelopmentRoot) && (request.Kind != NativeProfileKind.Development || !root.Equals(DevelopmentRoot, StringComparison.OrdinalIgnoreCase)))
            throw new NativeProfileException("WrongProfileScope");
        CheckChain(root); return root;
    }
    internal static void CheckChain(string root)
    {
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
        {
            // GetAttributes also detects dangling links; Directory.Exists alone does not.
            try { if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0) throw new NativeProfileException("ProfileLinkForbidden"); }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
        }
    }
    internal static List<IDisposable> CreateAndPinRoot(NativeProfileRequest request)
    {
        var root = Validate(request);
        var missing = new Stack<string>(); var ancestor = new DirectoryInfo(root);
        while (!ancestor.Exists) { missing.Push(ancestor.FullName); ancestor = ancestor.Parent ?? throw new NativeProfileException("InvalidProfileRoot"); }
        var held = IsolatedLibraryPersistence.PinDirectoryChain(ancestor.FullName);
        try
        {
            Validate(request);
            while (missing.TryPop(out var next))
            {
                Directory.CreateDirectory(next); CheckChain(next);
                held.AddRange(IsolatedLibraryPersistence.PinDirectoryChain(next)); CheckChain(next);
            }
            Validate(request); return held;
        }
        catch { foreach (var handle in held) handle.Dispose(); throw; }
    }
    internal static bool Overlaps(string a, string b) => Within(a, b) || Within(b, a);
    private static bool Within(string a, string b) => b.Equals(a, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

internal static class NativeProfileAccess
{
    public static string Validate(NativeConfiguration configuration)
    {
        if (configuration.Profile is not { } profile) return IsolatedLibrarySource.ValidateDirectory(configuration.DataDirectory);
        profile.ValidateAccess();
        if (!string.Equals(profile.Root, configuration.DataDirectory, StringComparison.OrdinalIgnoreCase)) throw new NativeProfileException("ProfileConfigurationMismatch");
        return profile.Root;
    }
}
