using Microsoft.Win32;
using System.Text.Json;
using System.Security.Cryptography;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsSessionRecoveryTokens : ISessionRecoveryTokens
{
    private readonly string root;
    private readonly WindowsFilesystemIdentity filesystem = new();
    public WindowsSessionRecoveryTokens(string explicitSyntheticDirectory) => root = IsolatedLibrarySource.ValidateDirectory(explicitSyntheticDirectory);
    public WindowsSessionRecoveryTokens(NativeConfiguration configuration) => root = NativeProfileAccess.Validate(configuration);
    public FilesystemIdentity ProfileIdentity => filesystem.Inspect(root).Observation?.Identity ?? throw new IOException("无法核对恢复资料目录身份。");
    public uint? BootId
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", false);
                return key?.GetValueKind("BootId") == RegistryValueKind.DWord && key.GetValue("BootId") is int value ? unchecked((uint)value) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        }
    }
    public string Seal(SessionRecoveryIdentity value)
    {
        if (value.Version != 1 || value.ProfileIdentity != ProfileIdentity || value.BootId != BootId) throw new InvalidDataException("恢复身份已失效。");
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            if (plain.Length > 20000) throw new InvalidDataException("恢复身份过大。");
            var encoded = Convert.ToBase64String(WindowsDataProtection.Protect(plain, true));
            return encoded.Length <= 32768 ? encoded : throw new InvalidDataException("恢复身份过大。");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public SessionRecoveryIdentity? Open(string value)
    {
        if (value.Length > 32768) return null;
        byte[]? plain = null;
        try
        {
            plain = WindowsDataProtection.Protect(Convert.FromBase64String(value), false);
            if (plain.Length > 20000) return null;
            var result = JsonSerializer.Deserialize<SessionRecoveryIdentity>(plain);
            return result is { Version: 1 } && result.ProfileIdentity == ProfileIdentity ? result : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or IOException or UnauthorizedAccessException) { return null; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
}
