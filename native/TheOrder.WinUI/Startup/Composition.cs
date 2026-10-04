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
        services.AddSingleton<ILibraryCodec, NativeLibraryCodec>();
        services.AddSingleton<ITransactionalLibraryFactory, IsolatedLibraryFactory>();
        services.AddSingleton<OfflineLibraryClient>();
        services.AddSingleton<IFilesystemIdentity, WindowsFilesystemIdentity>();
        services.AddSingleton<IScanFilesystem, WindowsScanFilesystem>();
        services.AddSingleton<BoundedScanner>();
        services.AddSingleton<IRasterCodec, WindowsRasterCodec>();
        services.AddSingleton<OfflineFilesClient>();
        services.AddSingleton<LocalCoverSources>();
        services.AddSingleton<IThumbnailDiskCache, IsolatedThumbnailCache>();
        services.AddSingleton(new ImageOptions());
        services.AddSingleton<IImagePipeline, ImagePipeline>();
        services.AddSingleton<FilesViewModel>();
        if (nativeConfiguration.Diagnostics)
        {
            services.AddSingleton(new SyntheticScenario(configuration["HOLD_PREFERENCES"] == "1", configuration["HOLD_IMAGES"] == "1"));
            services.AddSingleton<IOfflineFilesClient>(p => p.GetRequiredService<SyntheticScenario>().Wrap(p.GetRequiredService<OfflineFilesClient>()));
            services.AddSingleton<IImageSourceProvider>(p => p.GetRequiredService<SyntheticScenario>().Wrap(p.GetRequiredService<LocalCoverSources>()));
            services.AddSingleton<IClientTextRules>(p => p.GetRequiredService<SyntheticScenario>().Wrap(new LegacyClientTextRules()));
            services.AddSingleton<IUiPreferencesStore>(p => p.GetRequiredService<SyntheticScenario>().Wrap(new IsolatedUiPreferencesStore(nativeConfiguration)));
            services.AddSingleton<IOfflineLibraryClient>(p => p.GetRequiredService<SyntheticScenario>().Wrap(p.GetRequiredService<OfflineLibraryClient>()));
        }
        else
        {
            services.AddSingleton<IOfflineFilesClient>(p => p.GetRequiredService<OfflineFilesClient>());
            services.AddSingleton<IImageSourceProvider>(p => p.GetRequiredService<LocalCoverSources>());
            services.AddSingleton<IClientTextRules, LegacyClientTextRules>();
            services.AddSingleton<IUiPreferencesStore, IsolatedUiPreferencesStore>();
            services.AddSingleton<IOfflineLibraryClient>(p => p.GetRequiredService<OfflineLibraryClient>());
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
