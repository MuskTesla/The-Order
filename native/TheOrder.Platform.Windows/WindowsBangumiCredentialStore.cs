using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// A separate optional DPAPI companion, bound to this profile/store. No schema change
// to library.json, no secret in UI preferences, and no cross-provider credential reuse.
public sealed class WindowsBangumiCredentialStore(NativeConfiguration configuration,
    Func<PersistenceStage, Task>? persistenceProbe = null) : IBangumiCredentialStore
{
    public const string FileName = "native-bangumi-key.dpapi";
    private readonly SemaphoreSlim serial = new(1);
    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private sealed record Envelope(int Version, Guid ProfileId, Guid StoreId, string? Token);
    private sealed record OAuthEnvelope(int Version, Guid ProfileId, Guid StoreId, string? Token, string? RefreshToken,
        DateTimeOffset? Expires, string? Service, string? AccountId);
    public async Task<string?> ReadAsync(CancellationToken cancellation) => (await ReadCredentialAsync(cancellation).ConfigureAwait(false))?.WithSecrets((token, _) => token);
    public Task<BangumiCredential?> ReadCredentialAsync(CancellationToken cancellation) => Run((target, profile) => Task.FromResult(Read(target, profile)), cancellation);
    public Task SaveAsync(string token, CancellationToken cancellation) => Write(BangumiCredential.Manual(token), cancellation);
    public Task SaveCredentialAsync(BangumiCredential value, CancellationToken cancellation) => Write(value, cancellation);
    public Task ClearAsync(CancellationToken cancellation) => Write(null, cancellation);
    private Task Write(BangumiCredential? credential, CancellationToken cancellation) => Run(async (target, profile) =>
    {
        // Unknown, corrupted, foreign, or future formats are not overwritten.
        _ = Read(target, profile);
        // Manual tokens and cleared values retain the older compatible format.
        var plain = credential?.IsOAuth == true
            ? credential.WithSecrets((token, refresh) => JsonSerializer.SerializeToUtf8Bytes(new OAuthEnvelope(2,
                profile.Manifest.ProfileId, profile.Manifest.StoreId, token, refresh, credential.Expires, credential.Service?.AbsoluteUri, credential.AccountId), Options))
            : JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, profile.Manifest.ProfileId, profile.Manifest.StoreId,
                credential?.WithSecrets((token, _) => token)), Options);
        byte[] encrypted;
        try { encrypted = WindowsDataProtection.Protect(plain, true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        cancellation.ThrowIfCancellationRequested();
        await WindowsAtomicFile.WriteAsync(target, encrypted, async stage =>
        {
            if (persistenceProbe is not null) await persistenceProbe(stage).ConfigureAwait(false);
            if (stage is PersistenceStage.BeforeTempWrite or PersistenceStage.BeforeReplace)
            {
                cancellation.ThrowIfCancellationRequested(); profile.ValidateAccess();
                if (stage == PersistenceStage.BeforeReplace) _ = Read(target, profile);
            }
        }).ConfigureAwait(false);
        return true;
    }, cancellation);
    private static BangumiCredential? Read(string target, NativeProfileStore profile)
    {
        try
        {
            using var stream = IsolatedLibrarySource.OpenChecked(target);
            if (stream.Length is < 1 or > 32768) throw Error();
            var encrypted = new byte[(int)stream.Length]; stream.ReadExactly(encrypted);
            var plain = WindowsDataProtection.Protect(encrypted, false);
            try
            {
                using var document = JsonDocument.Parse(plain);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("Version", out var version)
                    || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)) throw Error();
                if (number == 1)
                {
                    var old = JsonSerializer.Deserialize<Envelope>(plain, Options);
                    if (old is null || old.ProfileId != profile.Manifest.ProfileId || old.StoreId != profile.Manifest.StoreId) throw Error();
                    return old.Token is null ? null : BangumiCredential.Manual(old.Token);
                }
                if (number != 2) throw Error();
                var value = JsonSerializer.Deserialize<OAuthEnvelope>(plain, Options);
                if (value is null || value.ProfileId != profile.Manifest.ProfileId || value.StoreId != profile.Manifest.StoreId) throw Error();
                if (value.RefreshToken is null)
                {
                    if (value.Expires is not null || value.Service is not null || value.AccountId is not null) throw Error();
                    return value.Token is null ? null : BangumiCredential.Manual(value.Token);
                }
                if (value.Token is null || value.Expires is null || value.AccountId is null
                    || !Uri.TryCreate(value.Service, UriKind.Absolute, out var service)) throw Error();
                return BangumiCredential.OAuth(value.Token, value.RefreshToken, value.Expires.Value, service, value.AccountId);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (FileNotFoundException) { return null; }
    }
    private async Task<T> Run<T>(Func<string, NativeProfileStore, Task<T>> action, CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                if (configuration.Profile is not NativeProfileStore profile) throw Error();
                var root = NativeProfileAccess.Validate(configuration);
                var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try
                {
                    profile.ValidateAccess(); cancellation.ThrowIfCancellationRequested();
                    return await action(Path.Combine(root, FileName), profile).ConfigureAwait(false);
                }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DataIsolationException
            or NativeProfileException or CryptographicException or JsonException)
        { throw Error(); }
        finally { serial.Release(); }
    }
    private static BangumiAuthorizationException Error() => new(BangumiAuthorizationFailure.Storage);
}
