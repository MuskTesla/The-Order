using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.WinUI.Presentation;

namespace TheOrder.WinUI.Startup;

internal static class Composition
{
    public static NativeHost Create()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables("THE_ORDER_NATIVE_").Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(new NativeConfiguration(configuration["DATA_DIR"]));
        services.AddSingleton<INativeSettings, SessionSettings>();
        services.AddSingleton<IIsolatedLibrarySource, IsolatedLibrarySource>();
        services.AddSingleton<IReadOnlyLibrary, LegacyLibraryReader>();
        services.AddSingleton<ErrorState>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<MainWindow>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        // Resolution is confined to this composition root. Views and view models receive constructors.
        return new(provider, provider.GetRequiredService<MainWindow>(), provider.GetRequiredService<ILogger<NativeHost>>());
    }
}

internal sealed class NativeHost(ServiceProvider provider, MainWindow window, ILogger<NativeHost> logger) : IAsyncDisposable
{
    public MainWindow Window { get; } = window;
    public void LogFatal(Exception exception) => logger.LogCritical("Native client terminated: {ExceptionType}", exception.GetType().Name);
    public async ValueTask DisposeAsync() { Window.CancelOperations(); await provider.DisposeAsync(); }
}
