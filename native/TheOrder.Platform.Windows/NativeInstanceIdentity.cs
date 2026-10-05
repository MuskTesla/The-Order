using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public static class NativeInstanceIdentity
{
    // Read-only validation, before opening any writer or recovery coordinator.
    public static string ForProfile(string? directory)
    {
        var root = IsolatedLibrarySource.ValidateDirectory(directory);
        var identity = new WindowsFilesystemIdentity().Inspect(root).Observation?.Identity
            ?? throw new DataIsolationException("无法验证资料目录的物理身份。");
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User?.Value ?? throw new DataIsolationException("无法验证当前用户。");
        return "dev.local.kungalmanager.native." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "|" + identity)));
    }
}
