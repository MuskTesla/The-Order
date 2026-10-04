using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Current-user DPAPI, separate from Electron safeStorage and settings.json. No plaintext file or secret-bearing errors.
public sealed class IsolatedCredentialStore(NativeConfiguration configuration) : ICredentialSettings, ICredentialProvider
{
    private readonly SemaphoreSlim queue = new(1, 1);
    public Task<bool> HasCredentialAsync(CancellationToken cancellation) => Run(root =>
    {
        try { using var stream = IsolatedLibrarySource.OpenChecked(Path.Combine(root, "native-online-key.dpapi")); return stream.Length is > 0 and <= 16384; }
        catch (FileNotFoundException) { return false; }
    }, cancellation);
    public Task<string?> ReadAsync(CancellationToken cancellation) => Run<string?>(root =>
    {
        try
        {
            using var input = IsolatedLibrarySource.OpenChecked(Path.Combine(root, "native-online-key.dpapi"));
            if (input.Length is <= 0 or > 16384) throw new OnlineException(OnlineFailure.CredentialStorage);
            var encrypted = new byte[(int)input.Length]; input.ReadExactly(encrypted); var plain = Protect(encrypted, false);
            try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (FileNotFoundException) { return null; }
    }, cancellation);
    public Task SaveAsync(string secret, CancellationToken cancellation)
    {
        var normalized = secret.Trim();
        if (normalized.Length is < 1 or > 4096 || normalized.Any(c => c < 0x21 || c > 0x7e)) throw new OnlineException(OnlineFailure.InvalidCredential);
        return Run(root =>
        {
            var plain = Encoding.UTF8.GetBytes(normalized); byte[] encrypted;
            try { encrypted = Protect(plain, true); } finally { CryptographicOperations.ZeroMemory(plain); }
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
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException();
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
