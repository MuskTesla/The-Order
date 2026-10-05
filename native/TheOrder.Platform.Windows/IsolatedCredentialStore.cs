using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Current-user DPAPI, separate from Electron safeStorage and settings.json. No plaintext file or secret-bearing errors.
public sealed class IsolatedCredentialStore(NativeConfiguration configuration) : ICredentialSettings, ICredentialProvider
{
    private readonly SemaphoreSlim queue = new(1, 1);
    public async Task<bool> HasCredentialAsync(CancellationToken cancellation) => await ReadAsync(cancellation).ConfigureAwait(false) is not null;
    public Task<string?> ReadAsync(CancellationToken cancellation) => Run<string?>(root =>
    {
        try
        {
            using var input = IsolatedLibrarySource.OpenChecked(Path.Combine(root, "native-online-key.dpapi"));
            if (input.Length is <= 0 or > 16384) throw new OnlineException(OnlineFailure.CredentialStorageCorrupt);
            var encrypted = new byte[(int)input.Length]; input.ReadExactly(encrypted); var plain = WindowsDataProtection.Protect(encrypted, false);
            try
            {
                var secret = new UTF8Encoding(false, true).GetString(plain);
                if (secret.Length is < 1 or > 4096 || secret.Any(c => c < 0x21 || c > 0x7e)) throw new OnlineException(OnlineFailure.CredentialStorageCorrupt);
                return secret;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception e) when (e is CryptographicException or DecoderFallbackException) { throw new OnlineException(OnlineFailure.CredentialStorageCorrupt); }
    }, cancellation);
    public Task SaveAsync(string secret, CancellationToken cancellation)
    {
        var normalized = secret.Trim();
        if (normalized.Length is < 1 or > 4096 || normalized.Any(c => c < 0x21 || c > 0x7e)) throw new OnlineException(OnlineFailure.InvalidCredential);
        return Run(root =>
        {
            var plain = Encoding.UTF8.GetBytes(normalized); byte[] encrypted;
            try { encrypted = WindowsDataProtection.Protect(plain, true); } finally { CryptographicOperations.ZeroMemory(plain); }
            var target = Path.Combine(root, "native-online-key.dpapi"); var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            void Check() { try { using var prior = IsolatedLibrarySource.OpenChecked(target); } catch (FileNotFoundException) { } }
            Check();
            try { using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(encrypted); output.Flush(true); } Check(); File.Move(temp, target, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return true;
        }, cancellation);
    }
    public Task ClearAsync(CancellationToken cancellation) => Run(root =>
    {
        var target = Path.Combine(root, "native-online-key.dpapi");
        try { using (IsolatedLibrarySource.OpenChecked(target)) { } File.Delete(target); } catch (FileNotFoundException) { }
        return true;
    }, cancellation);
    private async Task<T> Run<T>(Func<string, T> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var root = IsolatedLibrarySource.ValidateDirectory(configuration.DataDirectory);
                var held = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { IsolatedLibrarySource.ValidateDirectory(root); cancellation.ThrowIfCancellationRequested(); return action(root); }
                finally { foreach (var handle in held) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DataIsolationException or CryptographicException) { throw new OnlineException(OnlineFailure.CredentialStorage); }
        finally { queue.Release(); }
    }
}
