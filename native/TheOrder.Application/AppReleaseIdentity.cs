using System.Reflection;

namespace TheOrder.Application;

// Product identity comes from the same version properties as the Windows binary.
// A prerelease never participates in the stable install/update channel.
public static class AppReleaseIdentity
{
    public static string Version { get; } = typeof(AppReleaseIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static bool IsBeta => Version.Contains('-', StringComparison.Ordinal);
    public static string Product => IsBeta ? "The Order Beta" : "The Order";
}
