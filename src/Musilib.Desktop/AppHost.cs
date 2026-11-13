using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Musilib.Application;
using Musilib.Desktop.Services;
using Musilib.Desktop.ViewModels;
using Musilib.Infrastructure;
using Musilib.Infrastructure.Configuration;
using Musilib.Infrastructure.Logging;
using Serilog;
using Serilog.Events;

namespace Musilib.Desktop;

/// <summary>Composition root : configuration, journalisation et injection de dépendances.</summary>
public static class AppHost
{
    public static IServiceProvider Build(string[] args, out bool demoMode)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("MUSILIB_")
            .Build();

        var paths = new AppPaths();

        // Le mode démo détermine les fournisseurs injectés : il est lu avant la construction du conteneur.
        var preSettings = new JsonSettingsService(configuration, Path.Combine(paths.SettingsDirectory, "settings.json"),
            NullLogger<JsonSettingsService>.Instance);
        demoMode = preSettings.Current.General.DemoMode || args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.With(new RedactionEnricher())
            .WriteTo.File(Path.Combine(paths.LogDirectory, "musilib-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("Démarrage de Musilib Manager {Version} (démo : {Demo})", typeof(AppHost).Assembly.GetName().Version, demoMode);

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddSerilog(Log.Logger, dispose: false));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMusilibApplication();
        services.AddMusilibInfrastructure(configuration, paths, demoMode);
        services.AddMusilibDesktop(demoMode);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static IServiceCollection AddMusilibDesktop(this IServiceCollection services, bool demoMode)
    {
        services.AddSingleton(new AppMode(demoMode));
        services.AddSingleton<AppState>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ImageLoader>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<PlayerViewModel>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ConnectionsViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<PlaylistViewModel>();
        services.AddSingleton<MatchReviewViewModel>();
        services.AddSingleton<TrackEditorViewModel>();
        services.AddSingleton<ExportViewModel>();
        services.AddSingleton<TransferViewModel>();
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        return services;
    }
}

public sealed record AppMode(bool IsDemo);
