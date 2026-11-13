using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Jobs;
using Musilib.Desktop.Services;
using Musilib.Domain.Enums;
using Musilib.Infrastructure;
using Musilib.Infrastructure.Demo;

namespace Musilib.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly IDatabaseInitializer _database;
    private readonly IJobQueue _queue;
    private readonly IJobRepository _jobs;
    private readonly DemoDataSeeder _seeder;
    private readonly ConnectionRegistry _connections;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dictionary<PageKey, PageViewModel> _pages;
    private readonly Stack<(PageKey Key, object? Parameter)> _history = new();
    private PageKey? _currentKey;

    public MainWindowViewModel(
        IServiceProvider services,
        IDatabaseInitializer database,
        IJobQueue queue,
        IJobRepository jobs,
        DemoDataSeeder seeder,
        ConnectionRegistry connections,
        AppState state,
        AppMode mode,
        NotificationService notifications,
        DialogService dialogs,
        NavigationService navigation,
        PlayerViewModel player,
        ILogger<MainWindowViewModel> logger,
        DashboardViewModel dashboard,
        ConnectionsViewModel connectionsPage,
        LibraryViewModel library,
        PlaylistViewModel playlist,
        MatchReviewViewModel matchReview,
        TrackEditorViewModel editor,
        ExportViewModel export,
        TransferViewModel transfer,
        QueueViewModel queuePage,
        HistoryViewModel history,
        SettingsViewModel settings)
    {
        _services = services;
        _database = database;
        _queue = queue;
        _jobs = jobs;
        _seeder = seeder;
        _connections = connections;
        _logger = logger;
        State = state;
        IsDemo = mode.IsDemo;
        Notifications = notifications;
        Dialogs = dialogs;
        Player = player;

        _pages = new Dictionary<PageKey, PageViewModel>
        {
            [PageKey.Dashboard] = dashboard,
            [PageKey.Connections] = connectionsPage,
            [PageKey.Library] = library,
            [PageKey.Playlist] = playlist,
            [PageKey.MatchReview] = matchReview,
            [PageKey.Editor] = editor,
            [PageKey.Export] = export,
            [PageKey.Transfer] = transfer,
            [PageKey.Queue] = queuePage,
            [PageKey.History] = history,
            [PageKey.Settings] = settings
        };

        Sections =
        [
            new NavSection("GÉNÉRAL", [
                Nav(PageKey.Dashboard, "Tableau de bord", "Icon.Dashboard"),
                Nav(PageKey.Connections, "Connexions", "Icon.Link"),
                Nav(PageKey.Library, "Bibliothèque", "Icon.Library")
            ]),
            new NavSection("PLAYLIST", [
                Nav(PageKey.Playlist, "Import & analyse", "Icon.List"),
                Nav(PageKey.MatchReview, "Correspondances", "Icon.Compare"),
                Nav(PageKey.Editor, "Métadonnées & lecture", "Icon.Edit"),
                Nav(PageKey.Export, "Export local", "Icon.Download"),
                Nav(PageKey.Transfer, "Transfert Spotify", "Icon.Send")
            ]),
            new NavSection("ACTIVITÉ", [
                Nav(PageKey.Queue, "File d'attente", "Icon.Layers"),
                Nav(PageKey.History, "Historique", "Icon.Clock"),
                Nav(PageKey.Settings, "Paramètres", "Icon.Settings")
            ])
        ];

        navigation.NavigationRequested += (_, r) => _ = NavigateAsync(r.Page, r.Parameter);
        state.PlaylistSelectionChanged += async (_, _) =>
        {
            if (CurrentPage is not null) await CurrentPage.OnPlaylistChangedAsync();
        };
        state.JobUpdated += (_, _) => _ = RefreshQueueBadgeAsync();
    }

    public AppState State { get; }
    public bool IsDemo { get; }
    public NotificationService Notifications { get; }
    public DialogService Dialogs { get; }
    public PlayerViewModel Player { get; }
    public IReadOnlyList<NavSection> Sections { get; }
    public string Version => $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3)}";

    [ObservableProperty]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    private bool _isInitializing = true;

    [ObservableProperty]
    private string _initText = "Initialisation…";

    [ObservableProperty]
    private string? _fatalError;

    [ObservableProperty]
    private bool _youTubeConnected;

    [ObservableProperty]
    private bool _spotifyConnected;

    [ObservableProperty]
    private bool _canGoBack;

    private NavItemViewModel Nav(PageKey key, string title, string icon) =>
        new(key, title, icon, item => _ = NavigateAsync(item.Key, null));

    public async Task InitializeAsync()
    {
        try
        {
            InitText = "Préparation de la base de données…";
            await _database.InitializeAsync();
            if (IsDemo)
            {
                InitText = "Préparation des données de démonstration…";
                await _seeder.SeedAsync(CancellationToken.None);
            }
            InitText = "Reprise des traitements…";
            await _queue.StartAsync();
            await State.RefreshPlaylistsAsync();
            await RefreshConnectionsAsync();
            await RefreshQueueBadgeAsync();
            await NavigateAsync(PageKey.Dashboard, null);
            IsInitializing = false;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Échec de l'initialisation");
            FatalError = ex.Message;
        }
    }

    public async Task RefreshConnectionsAsync()
    {
        try
        {
            YouTubeConnected = (await _connections.YouTube.GetStatusAsync(CancellationToken.None)).IsConnected;
            SpotifyConnected = (await _connections.Spotify.GetStatusAsync(CancellationToken.None)).IsConnected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lecture de l'état des connexions impossible");
        }
    }

    private async Task RefreshQueueBadgeAsync()
    {
        var active = await _jobs.GetByStatusAsync([JobStatus.Queued, JobStatus.Running]);
        var item = Sections.SelectMany(s => s.Items).First(i => i.Key == PageKey.Queue);
        item.BadgeText = active.Count > 0 ? active.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public async Task NavigateAsync(PageKey key, object? parameter, bool pushHistory = true)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        if (pushHistory && _currentKey is { } previous && (previous != key || parameter is not null))
        {
            _history.Push((previous, null));
            CanGoBack = true;
        }
        CurrentPage?.OnNavigatedFrom();
        _currentKey = key;
        CurrentPage = page;
        foreach (var item in Sections.SelectMany(s => s.Items)) item.IsActive = item.Key == key;
        if (key == PageKey.Connections) _ = RefreshConnectionsAsync();
        try
        {
            await page.OnNavigatedToAsync(parameter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur au chargement de l'écran {Page}", key);
            Notifications.Error("Chargement impossible", ex.Message);
        }
    }

    [RelayCommand]
    private async Task GoBack()
    {
        if (_history.Count == 0) return;
        var (key, parameter) = _history.Pop();
        CanGoBack = _history.Count > 0;
        await NavigateAsync(key, parameter, pushHistory: false);
    }

    [RelayCommand]
    private void OpenLogs()
    {
        var paths = (IAppPaths?)_services.GetService(typeof(IAppPaths));
        if (paths is not null) DialogService.OpenFolder(paths.LogDirectory);
    }

    public async Task ShutdownAsync()
    {
        try
        {
            await _queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Arrêt du moteur de traitement incomplet");
        }
    }

    public static void Restart(bool? demo = null)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (demo == true) psi.ArgumentList.Add("--demo");
        Process.Start(psi);
    }
}
