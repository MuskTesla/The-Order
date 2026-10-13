using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Optional companion, bound to the existing profile/store lease; no legacy component migration.
public sealed class WindowsNextMoeAuthStore(NativeConfiguration configuration) : INextMoeAuthStore
{
    private sealed record Envelope(int Schema, Guid ProfileId, Guid StoreId, NextMoeAuthRecord Record);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim serial = new(1, 1);
    public async Task<NextMoeAuthRecord?> LoadAsync(CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var profile = configuration.Profile as NativeProfileStore ?? throw new OnlineException(OnlineFailure.CredentialStorage);
            profile.ValidateAccess(); var root = NativeProfileAccess.Validate(configuration);
            var path = Path.Combine(root, "native-nextmoe-auth.dpapi");
            using var stream = OpenOptional(path); if (stream is null) return null;
            if (stream.Length is <= 0 or > 131072) throw new OnlineException(OnlineFailure.CredentialStorageCorrupt);
            var encrypted = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(encrypted, cancellation).ConfigureAwait(false);
            var plain = WindowsDataProtection.Protect(encrypted, false);
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(plain, Json) ?? throw new JsonException();
                if (envelope.Schema != 1 || envelope.ProfileId != profile.Manifest.ProfileId || envelope.StoreId != profile.Manifest.StoreId
                    || envelope.Record is null) throw new JsonException();
                NextMoeAuthValidation.Validate(envelope.Record);
                return envelope.Record;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException)
        { throw new OnlineException(OnlineFailure.CredentialStorageCorrupt); }
        finally { serial.Release(); }
    }
    public async Task SaveAsync(NextMoeAuthRecord record, CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            NextMoeAuthValidation.Validate(record);
            cancellation.ThrowIfCancellationRequested();
            var profile = configuration.Profile as NativeProfileStore ?? throw new OnlineException(OnlineFailure.CredentialStorage);
            profile.ValidateAccess(); var root = NativeProfileAccess.Validate(configuration);
            var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, profile.Manifest.ProfileId, profile.Manifest.StoreId, record), Json);
                byte[] encrypted;
                try { if (bytes.Length > 65536) throw new OnlineException(OnlineFailure.CredentialStorage); encrypted = WindowsDataProtection.Protect(bytes, true); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                profile.ValidateAccess(); cancellation.ThrowIfCancellationRequested();
                var receipt = await WindowsAtomicFile.WriteAsync(Path.Combine(root, "native-nextmoe-auth.dpapi"), encrypted).ConfigureAwait(false);
                if (receipt.Warning is not null) throw new OnlineException(OnlineFailure.CredentialStorage);
            }
            finally { foreach (var handle in held) handle.Dispose(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { throw new OnlineException(OnlineFailure.CredentialStorage); }
        finally { serial.Release(); }
    }
    private static Stream? OpenOptional(string path) { try { return IsolatedLibrarySource.OpenChecked(path); } catch (FileNotFoundException) { return null; } }
}
