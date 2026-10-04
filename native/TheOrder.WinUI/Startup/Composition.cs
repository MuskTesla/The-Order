using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Platform.Windows;
using TheOrder.Presentation;

namespace TheOrder.WinUI.Startup;

internal static class Composition
{
    public static NativeHost Create()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables("THE_ORDER_NATIVE_").Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        var nativeConfiguration = new NativeConfiguration(configuration["DATA_DIR"], configuration["DIAGNOSTICS"] == "1");
        services.AddSingleton(nativeConfiguration);
        services.AddSingleton<ILibraryCodec, LegacyLibraryCodec>();
        services.AddSingleton<ITransactionalLibraryFactory, IsolatedLibraryFactory>();
        if (nativeConfiguration.Diagnostics)
        {
            services.AddSingleton(new SyntheticScenario(configuration["HOLD_PREFERENCES"] == "1"));
            services.AddSingleton<IClientTextRules>(p => p.GetRequiredService<SyntheticScenario>().Wrap(new LegacyClientTextRules()));
            services.AddSingleton<IUiPreferencesStore>(p => p.GetRequiredService<SyntheticScenario>().Wrap(new IsolatedUiPreferencesStore(nativeConfiguration)));
            services.AddSingleton<IOfflineLibraryClient>(p => p.GetRequiredService<SyntheticScenario>().Wrap(new OfflineLibraryClient(p.GetRequiredService<ITransactionalLibraryFactory>(), p.GetRequiredService<IClientTextRules>())));
        }
        else
        {
            services.AddSingleton<IClientTextRules, LegacyClientTextRules>();
            services.AddSingleton<IUiPreferencesStore, IsolatedUiPreferencesStore>();
            services.AddSingleton<IOfflineLibraryClient, OfflineLibraryClient>();
        }
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
    public async ValueTask DisposeAsync() { await Window.StopAsync(); await provider.DisposeAsync(); }
}
