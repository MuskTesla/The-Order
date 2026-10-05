using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheOrder.Application;
using TheOrder.Infrastructure.Legacy;
using TheOrder.Infrastructure.Online;
using TheOrder.Infrastructure.Recognition;
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
        var nativeConfiguration = Program.Profile?.Configuration(configuration["DIAGNOSTICS"] == "1")
            ?? new NativeConfiguration(configuration["DATA_DIR"], configuration["DIAGNOSTICS"] == "1");
        services.AddSingleton(nativeConfiguration);
        services.AddSingleton<ILibraryCodec, NativeLibraryCodec>();
        services.AddSingleton<ITransactionalLibraryFactory, IsolatedLibraryFactory>();
        services.AddSingleton<OfflineLibraryClient>();
        services.AddSingleton<IFilesystemIdentity, WindowsFilesystemIdentity>();
        services.AddSingleton<ILaunchLibrary, OfflineLaunchLibrary>();
        services.AddSingleton<ILaunchPositionStore, IsolatedLaunchPositionStore>();
        services.AddSingleton<IGameProcessPlatform, WindowsGameProcesses>();
        services.AddSingleton<WindowsSessionPowerEvents>();
        services.AddSingleton(p => new AppSessionHost(async () =>
        {
            var client = p.GetRequiredService<OfflineLibraryClient>();
            await client.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (!client.Capability.Loaded) throw new IOException(client.Capability.Message);
            if (!client.Capability.CanEdit) return null;
            return client.CreateSessionEngine(p.GetRequiredService<IFilesystemIdentity>(), p.GetRequiredService<IGameProcessPlatform>(),
                p.GetRequiredService<ILaunchPositionStore>(), new WindowsSessionClock(), new WindowsSessionRecoveryTokens(nativeConfiguration), p.GetRequiredService<WindowsSessionPowerEvents>());
        }, () => p.GetRequiredService<OfflineLibraryClient>().CurrentSnapshot));
        services.AddSingleton(p => new LaunchViewModel(p.GetRequiredService<ILaunchLibrary>(), p.GetRequiredService<AppSessionHost>()));
        services.AddSingleton<IScanFilesystem, WindowsScanFilesystem>();
        services.AddSingleton<BoundedScanner>();
        services.AddSingleton<IRasterCodec, WindowsRasterCodec>();
        services.AddSingleton<OfflineFilesClient>();
        services.AddSingleton<LocalCoverSources>();
        services.AddSingleton<IsolatedCredentialStore>();
        var fixtureOnline = nativeConfiguration.Diagnostics && configuration["ONLINE_FIXTURE"] == "1";
        if (fixtureOnline)
        {
            services.AddSingleton(_ => new SyntheticOnline(configuration["ONLINE_SLOW_CONNECTION"] == "1", configuration["RECOGNITION_FIXTURE"] == "1", configuration["RECOGNITION_BOUNDARY_FIXTURE"] == "1"));
        }
        var fixtureCredentials = fixtureOnline && configuration["ONLINE_REAL_STORE"] != "1";
        services.AddSingleton(p => new AuthenticatedCredentials(
            fixtureCredentials ? p.GetRequiredService<SyntheticOnline>() : p.GetRequiredService<IsolatedCredentialStore>(),
            fixtureCredentials ? p.GetRequiredService<SyntheticOnline>() : p.GetRequiredService<IsolatedCredentialStore>()));
        services.AddSingleton<ICredentialSettings>(p => p.GetRequiredService<AuthenticatedCredentials>());
        services.AddSingleton<ICredentialProvider>(p => p.GetRequiredService<AuthenticatedCredentials>());
        services.AddSingleton<CatalogNetwork>();
        services.AddSingleton<ImageNetwork>();
        var performanceImages = nativeConfiguration.Diagnostics && Program.Profile?.Manifest.ProfileKind == NativeProfileKind.Test && configuration["PERFORMANCE_COVERS"] is not null;
        if (performanceImages) services.AddSingleton<PerformanceImageFixture>();
        services.AddSingleton(p => new CatalogTransport(fixtureOnline ? p.GetRequiredService<SyntheticOnline>().Client : p.GetRequiredService<CatalogNetwork>().Client, p.GetRequiredService<ICredentialProvider>(), new Uri("https://api.nextmoe.dev/v2/")));
        services.AddSingleton(p => new NextMoeCatalog(p.GetRequiredService<CatalogTransport>(), clock: fixtureOnline ? () => p.GetRequiredService<SyntheticOnline>().Now : null));
        services.AddSingleton<IOnlineCatalog>(p => p.GetRequiredService<NextMoeCatalog>());
        services.AddSingleton<IRecognitionCatalog>(p => p.GetRequiredService<NextMoeCatalog>());
        services.AddSingleton(p => p.GetRequiredService<AuthenticatedCredentials>().AuthContext);
        services.AddSingleton<ILocalEvidenceParser, LocalEvidenceParser>();
        services.AddSingleton<IExecutableEvidenceParser, ExecutableEvidenceParser>();
        services.AddSingleton<IRecognitionEvidenceReader>(p => new WindowsRecognitionEvidence(p.GetRequiredService<ILocalEvidenceParser>(), RecognitionFingerprint.Evidence, p.GetRequiredService<IExecutableEvidenceParser>()));
        services.AddSingleton<ITitleIndex>(_ => new TitleIndex(() => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "data", "title-index.json.gz"))));
        services.AddSingleton<IRecognitionRanker, ReviewSafeRanking>();
        services.AddSingleton<IRecognitionEngine, RecognitionEngine>();
        services.AddSingleton<IRecognitionLibrarySource>(p => p.GetRequiredService<OfflineLibraryClient>());
        services.AddSingleton<RecognitionQueue>();
        services.AddSingleton<IRecognitionReviewStorage, IsolatedRecognitionReviewStorage>();
        services.AddSingleton<IRecognitionReviewCodec, RecognitionReviewCodec>();
        services.AddSingleton(p => new RecognitionReviewService(p.GetRequiredService<OfflineLibraryClient>(), p.GetRequiredService<RecognitionQueue>(),
            p.GetRequiredService<IRecognitionReviewStorage>(), p.GetRequiredService<IRecognitionReviewCodec>(), p.GetRequiredService<IRecognitionEvidenceReader>(),
            p.GetRequiredService<IRecognitionRanker>(), p.GetRequiredService<IRecognitionCatalog>(), p.GetRequiredService<OnlineAuthContext>(), RecognitionFingerprint.Identity));
        services.AddSingleton<RecognitionViewModel>();
        services.AddSingleton(p => new RemoteCoverSource(performanceImages ? p.GetRequiredService<PerformanceImageFixture>().Client : fixtureOnline ? p.GetRequiredService<SyntheticOnline>().Client : p.GetRequiredService<ImageNetwork>().Client));
        services.AddSingleton<IRemoteCoverRegistry>(p => p.GetRequiredService<RemoteCoverSource>());
        services.AddSingleton(p => new CombinedCoverSources(p.GetRequiredService<LocalCoverSources>(), p.GetRequiredService<OfflineLibraryClient>(), p.GetRequiredService<IRemoteCoverRegistry>(), p.GetRequiredService<RemoteCoverSource>()));
        services.AddSingleton<OnlineLibraryClient>();
        services.AddSingleton<OnlineViewModel>();
        services.AddSingleton<IThumbnailDiskCache, IsolatedThumbnailCache>();
        services.AddSingleton(new ImageOptions());
        services.AddSingleton<IImagePipeline, ImagePipeline>();
        services.AddSingleton<FilesViewModel>();
        if (nativeConfiguration.Diagnostics)
        {
            services.AddSingleton(new SyntheticScenario(configuration["HOLD_PREFERENCES"] == "1", configuration["HOLD_IMAGES"] == "1"));
            services.AddSingleton<IOfflineFilesClient>(p => p.GetRequiredService<SyntheticScenario>().Wrap(p.GetRequiredService<OfflineFilesClient>()));
            services.AddSingleton<IImageSourceProvider>(p => p.GetRequiredService<SyntheticScenario>().Wrap(p.GetRequiredService<CombinedCoverSources>()));
            services.AddSingleton<IClientTextRules>(p => p.GetRequiredService<SyntheticScenario>().Wrap(new LegacyClientTextRules()));
            services.AddSingleton<IUiPreferencesStore>(p => p.GetRequiredService<SyntheticScenario>().Wrap(new IsolatedUiPreferencesStore(nativeConfiguration)));
            services.AddSingleton<IOfflineLibraryClient>(p => p.GetRequiredService<SyntheticScenario>().Wrap(p.GetRequiredService<OfflineLibraryClient>()));
        }
        else
        {
            services.AddSingleton<IOfflineFilesClient>(p => p.GetRequiredService<OfflineFilesClient>());
            services.AddSingleton<IImageSourceProvider>(p => p.GetRequiredService<CombinedCoverSources>());
            services.AddSingleton<IClientTextRules, LegacyClientTextRules>();
            services.AddSingleton<IUiPreferencesStore, IsolatedUiPreferencesStore>();
            services.AddSingleton<IOfflineLibraryClient>(p => p.GetRequiredService<OfflineLibraryClient>());
        }
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<MainWindow>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        // Resolution is confined to this composition root. Views and view models receive constructors.
        return new(provider, provider.GetRequiredService<MainWindow>(), provider.GetRequiredService<AppSessionHost>(), provider.GetRequiredService<ILogger<NativeHost>>());
    }
}

internal sealed class NativeHost(ServiceProvider provider, MainWindow window, AppSessionHost sessions, ILogger<NativeHost> logger) : IAsyncDisposable
{
    public MainWindow Window { get; } = window;
    public AppSessionHost Sessions { get; } = sessions;
    public void LogFatal(Exception exception) => logger.LogCritical("Native client terminated: {ExceptionType}", exception.GetType().Name);
    public async ValueTask DisposeAsync() { await Window.StopAsync(); await Sessions.DisposeAsync(); await provider.DisposeAsync(); }
}
