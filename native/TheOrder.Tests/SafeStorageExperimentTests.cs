using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

public sealed class SafeStorageExperimentTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task ElectronSyntheticSecretRequiresProfileEnvelope(int iteration)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "package.json"))) root = root.Parent;
        Assert.NotNull(root);
        var profile = Path.Combine(Path.GetTempPath(), "the-order-secret-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(Path.Combine(root!.FullName, "node_modules/electron/dist/electron.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root.FullName
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "native/scripts/safe-storage-probe.cjs"));
        start.Environment.Remove("ELECTRON_RUN_AS_NODE"); start.Environment.Remove("NODE_OPTIONS");
        start.Environment["THE_ORDER_PROBE_PROFILE"] = profile;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { if (!child.HasExited) child.Kill(true); throw; }
        Assert.Equal(0, child.ExitCode);
        using var reply = JsonDocument.Parse(await stdout);
        Assert.True(reply.RootElement.GetProperty("roundtrip").GetBoolean());
        var ciphertext = Convert.FromBase64String(reply.RootElement.GetProperty("ciphertext").GetString()!);
        byte[]? plaintext = null, masterKey = null, protectedKey = null;
        try
        {
            Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser));
            Assert.Equal("v10", System.Text.Encoding.ASCII.GetString(ciphertext, 0, 3));
            // This exact temporary profile was created above by this test. Never accept a profile argument.
            using var state = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(profile, "Local State")));
            protectedKey = Convert.FromBase64String(state.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
            Assert.Equal("DPAPI", System.Text.Encoding.ASCII.GetString(protectedKey, 0, 5));
            masterKey = ProtectedData.Unprotect(protectedKey[5..], null, DataProtectionScope.CurrentUser);
            Assert.Equal(32, masterKey.Length);
            plaintext = new byte[ciphertext.Length - 3 - 12 - 16];
            using var aes = new AesGcm(masterKey, 16);
            aes.Decrypt(ciphertext.AsSpan(3, 12), ciphertext.AsSpan(15, plaintext.Length), ciphertext.AsSpan(ciphertext.Length - 16), plaintext);
            Assert.Equal(reply.RootElement.GetProperty("digest").GetString(), Convert.ToHexStringLower(SHA256.HashData(plaintext)));
            var newProtected = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            var roundtrip = ProtectedData.Unprotect(newProtected, null, DataProtectionScope.CurrentUser);
            try { Assert.True(CryptographicOperations.FixedTimeEquals(plaintext, roundtrip)); } finally { CryptographicOperations.ZeroMemory(roundtrip); }
            ciphertext[^1] ^= 1;
            Assert.Throws<AuthenticationTagMismatchException>(() => aes.Decrypt(ciphertext.AsSpan(3, 12), ciphertext.AsSpan(15, plaintext.Length), ciphertext.AsSpan(ciphertext.Length - 16), plaintext));
            output.WriteLine("Probe {0}, Electron {1}: raw DPAPI rejected; synthetic profile DPAPI key + v10 AES-GCM verified, re-protection verified, tampering rejected; no plaintext persisted.", iteration, reply.RootElement.GetProperty("electron").GetString());
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (masterKey is not null) CryptographicOperations.ZeroMemory(masterKey);
            if (protectedKey is not null) CryptographicOperations.ZeroMemory(protectedKey);
            CryptographicOperations.ZeroMemory(ciphertext);
            // Profile contains only this probe's synthetic Electron files; retain for diagnostics, never copy into source.
        }
        await stderr;
    }
}
