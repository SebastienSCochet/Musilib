using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Services;
using Musilib.Domain.Enums;
using Musilib.Infrastructure.Auth;
using Musilib.Infrastructure.Configuration;
using Musilib.Infrastructure.Demo;
using Musilib.Infrastructure.Http;
using Musilib.Infrastructure.Media;
using Musilib.Infrastructure.Persistence;
using Musilib.Infrastructure.Security;
using Musilib.Infrastructure.Spotify;
using Musilib.Infrastructure.YouTube;

namespace Musilib.Infrastructure;

/// <summary>Services d'authentification par fournisseur.</summary>
public sealed record ConnectionRegistry(IAuthService YouTube, IAuthService Spotify)
{
    public IAuthService Get(ExternalService provider) => provider == ExternalService.YouTube ? YouTube : Spotify;
}

public static class DependencyInjection
{
    public const string UserAgent = "MusilibManager/1.0 (+desktop)";

    public static IServiceCollection AddMusilibInfrastructure(this IServiceCollection services, IConfiguration configuration, AppPaths paths, bool demo)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsService>(sp => new JsonSettingsService(configuration,
            Path.Combine(paths.SettingsDirectory, "settings.json"), sp.GetRequiredService<ILogger<JsonSettingsService>>()));
        services.AddSingleton<ISecureStore>(_ => new ProtectedFileStore(paths.SecretsDirectory));
        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();

        // Persistance
        services.AddSingleton(_ => new SqliteConnectionFactory(paths.DatabasePath(demo)));
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<ITrackRepository, TrackRepository>();
        services.AddSingleton<IPlaylistRepository, PlaylistRepository>();
        services.AddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<ISearchCache, SearchCacheRepository>();

        // Médias
        services.AddSingleton<IAudioMetadataService, TagLibAudioMetadataService>();
        services.AddSingleton<IAudioConverter, FfmpegAudioConverter>();
        services.AddSingleton<IImageDownloader, CachedImageDownloader>();
        services.AddSingleton<IAudioPlayer>(sp => OperatingSystem.IsWindows()
            ? new NAudioPlayer(sp.GetRequiredService<ILogger<NAudioPlayer>>())
            : new UnsupportedAudioPlayer());

        services.AddSingleton(new PlaylistSourceContext(demo ? PlaylistSource.Demo : PlaylistSource.YouTube));

        // HTTP
        services.AddHttpClient("oauth", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        });
        services.AddHttpClient(CachedImageDownloader.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        });
        services.AddHttpClient(YouTubeApiClient.HttpClientName, (sp, c) =>
            {
                c.BaseAddress = new Uri(sp.GetRequiredService<ISettingsService>().Current.YouTube.ApiBaseUrl);
                c.Timeout = TimeSpan.FromSeconds(60);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler(sp => new YouTubeAuthHandler(sp.GetRequiredService<ConnectionRegistry>().YouTube, sp.GetRequiredService<ISecureStore>()))
            .AddHttpMessageHandler(sp => CreateRetryHandler(sp, "YouTube"));
        services.AddHttpClient(SpotifyApiClient.HttpClientName, (sp, c) =>
            {
                c.BaseAddress = new Uri(sp.GetRequiredService<ISettingsService>().Current.Spotify.ApiBaseUrl);
                c.Timeout = TimeSpan.FromSeconds(60);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler(sp => new BearerTokenHandler(sp.GetRequiredService<ConnectionRegistry>().Spotify))
            .AddHttpMessageHandler(sp => CreateRetryHandler(sp, "Spotify"));

        if (demo)
        {
            services.AddSingleton<IYouTubeClient, DemoYouTubeClient>();
            services.AddSingleton<DemoSpotifyClient>();
            services.AddSingleton<ISpotifyClient>(sp => sp.GetRequiredService<DemoSpotifyClient>());
            services.AddSingleton(new ConnectionRegistry(new DemoAuthService(ExternalService.YouTube), new DemoAuthService(ExternalService.Spotify)));
        }
        else
        {
            services.AddSingleton<GoogleAuthService>();
            services.AddSingleton<SpotifyAuthService>();
            services.AddSingleton(sp => new ConnectionRegistry(sp.GetRequiredService<GoogleAuthService>(), sp.GetRequiredService<SpotifyAuthService>()));
            services.AddSingleton<IYouTubeClient, YouTubeApiClient>();
            services.AddSingleton<ISpotifyClient, SpotifyApiClient>();
        }
        services.AddSingleton<DemoDataSeeder>();
        return services;
    }

    private static RetryHandler CreateRetryHandler(IServiceProvider sp, string provider)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger($"Musilib.Http.{provider}");
        return new RetryHandler(() => settings.Current.Processing, logger);
    }
}
