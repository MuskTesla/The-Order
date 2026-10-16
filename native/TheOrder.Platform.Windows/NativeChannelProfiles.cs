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
        var old = Path.Combine(beta, NativeProfileStore.ManifestName); WindowsAtomicFile.CheckExisting(old);
        if (!File.Exists(old)) return request;
        var useLegacy = chooseLegacy();
        if (useLegacy is null) throw new NativeProfileException("ChannelProfileChoiceCanceled");
        if (!useLegacy.Value) return request;
        using var input = IsolatedLibrarySource.OpenChecked(old);
        if (input.Length > 16384) throw new NativeProfileException("InvalidLegacyBetaProfile");
        using var json = JsonDocument.Parse(input); var value = json.RootElement;
        if (value.GetProperty("Version").GetInt32() != 1 || value.GetProperty("ProfileKind").GetString() != "Beta" || value.GetProperty("State").GetString() != "Ready")
            throw new NativeProfileException("InvalidLegacyBetaProfile");
        return new(NativeProfileKind.Beta, beta, false, value.GetProperty("ProfileId").GetGuid(), value.GetProperty("StoreId").GetGuid());
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
