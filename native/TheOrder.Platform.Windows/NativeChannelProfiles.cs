using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// A routing choice, not a copy/migration of a physical-root-bound profile.
// It may select only our two existing namespaces; custom roots remain Test-only.
public static class NativeChannelProfiles
{
    public const string FileName = "active-profile.json";
    private sealed record Choice(int Version, NativeProfileKind Kind, Guid ProfileId, Guid StoreId);
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    public static NativeProfileRequest Resolve(NativeProfileRequest request, string installation, Func<bool?> chooseLegacy)
    {
        // This return must precede ANY formal-directory probe during isolated testing.
        if (request.Kind != NativeProfileKind.Production || !request.ExplicitProductionAuthorization
            || !request.Root.Equals(NativeProfilePaths.ProductionRoot, StringComparison.OrdinalIgnoreCase)) return request;
        return ResolveAt(request, installation, NativeProfilePaths.ProductionRoot, NativeProfilePaths.BetaRoot, chooseLegacy);
    }
    internal static NativeProfileRequest ResolveAt(NativeProfileRequest request, string installation, string production, string beta, Func<bool?> chooseLegacy)
    {
        var target = Path.Combine(installation, FileName); WindowsAtomicFile.CheckExisting(target);
        try
        {
            using var file = IsolatedLibrarySource.OpenChecked(target);
            if (file.Length > 4096) throw new NativeProfileException("InvalidChannelProfileChoice");
            var choice = JsonSerializer.Deserialize<Choice>(file, Options);
            if (choice is null || choice.Version != 1 || choice.Kind is not (NativeProfileKind.Production or NativeProfileKind.Beta)
                || choice.ProfileId == Guid.Empty || choice.StoreId == Guid.Empty) throw new NativeProfileException("InvalidChannelProfileChoice");
            var root = choice.Kind == NativeProfileKind.Beta ? beta : production;
            if (!File.Exists(Path.Combine(root, NativeProfileStore.ManifestName))) throw new NativeProfileException("SelectedProfileMissing");
            return new(choice.Kind, root, choice.Kind == NativeProfileKind.Production, choice.ProfileId, choice.StoreId);
        }
        catch (FileNotFoundException) { }
        using var input = OpenOptionalLegacyProfile(beta);
        if (input is null) return request;
        var useLegacy = chooseLegacy();
        if (useLegacy is null) throw new NativeProfileException("ChannelProfileChoiceCanceled");
        if (!useLegacy.Value) return request;
        if (input.Length > 16384) throw new NativeProfileException("InvalidLegacyBetaProfile");
        using var json = JsonDocument.Parse(input); var value = json.RootElement;
        if (value.GetProperty("Version").GetInt32() != 1 || value.GetProperty("ProfileKind").GetString() != "Beta" || value.GetProperty("State").GetString() != "Ready")
            throw new NativeProfileException("InvalidLegacyBetaProfile");
        return new(NativeProfileKind.Beta, beta, false, value.GetProperty("ProfileId").GetGuid(), value.GetProperty("StoreId").GetGuid());
    }
    private static FileStream? OpenOptionalLegacyProfile(string root)
    {
        // Never installing beta.1 is normal. Only this optional legacy probe may
        // accept missing parents; a missing explicitly selected profile above
        // must still stop startup rather than create a different bookshelf.
        ProfilePathGuard.BeforeFilesystem();
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory.FullName); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new NativeProfileException("ProfileLinkForbidden");
            if ((attributes & FileAttributes.Directory) == 0) throw new NativeProfileException("InvalidLegacyBetaProfileDirectory");
        }
        // Hold the checked file through the user's choice. Do not swallow access
        // failures, malformed manifests, links, or missing installation paths.
        try { return IsolatedLibrarySource.OpenChecked(Path.Combine(root, NativeProfileStore.ManifestName)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public static void Remember(NativeProfileStore profile, string installation)
    {
        if (profile.Manifest.ProfileKind is not (NativeProfileKind.Production or NativeProfileKind.Beta)
            || !WindowsUpdateInstaller.ManagedDirectory(installation)) return;
        profile.ValidateAccess();
        var held = IsolatedLibraryPersistence.PinDirectoryChain(installation);
        try
        {
            var path = Path.Combine(installation, FileName);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Choice(1, profile.Manifest.ProfileKind, profile.Manifest.ProfileId, profile.Manifest.StoreId), Options);
            // Avoid rewriting the routing file on every launch, including a read-only install.
            if (File.Exists(path))
            {
                using var input = IsolatedLibrarySource.OpenChecked(path);
                if (input.Length <= 4096 && JsonSerializer.Deserialize<Choice>(input, Options) == new Choice(1, profile.Manifest.ProfileKind, profile.Manifest.ProfileId, profile.Manifest.StoreId)) return;
                throw new NativeProfileException("ChannelProfileChoiceConflict");
            }
            WindowsAtomicFile.WriteAsync(path, bytes).GetAwaiter().GetResult();
        }
        finally { foreach (var handle in held) handle.Dispose(); }
    }
}
