using System.Reflection;

namespace TheOrder.Application;

// Product identity comes from the same version properties as the Windows binary.
// Channel is independent from installation, profile and application identity.
public static class AppReleaseIdentity
{
    public static string Version { get; } = typeof(AppReleaseIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static bool IsBeta => Version.Contains('-', StringComparison.Ordinal);
    public static string Product => "The Order";
}
