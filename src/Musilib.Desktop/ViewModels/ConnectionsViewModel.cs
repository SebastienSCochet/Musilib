using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Desktop.Converters;
using Musilib.Desktop.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Infrastructure;

namespace Musilib.Desktop.ViewModels;

public sealed partial class ConnectionsViewModel : PageViewModel
{
    private readonly ConnectionRegistry _connections;
    private readonly ISettingsService _settings;
    private readonly ISecureStore _secrets;
    private readonly IYouTubeClient _youTube;
    private readonly ISpotifyClient _spotify;
    private readonly IHistoryRepository _history;
    private readonly DialogService _dialogs;
    private CancellationTokenSource? _signInCts;

    public ConnectionsViewModel(ConnectionRegistry connections, ISettingsService settings, ISecureStore secrets, IYouTubeClient youTube,
        ISpotifyClient spotify, IHistoryRepository history, DialogService dialogs, AppMode mode, NotificationService notifications,
        ILogger<ConnectionsViewModel> logger)
        : base(notifications, logger)
    {
        _connections = connections;
        _settings = settings;
        _secrets = secrets;
        _youTube = youTube;
        _spotify = spotify;
        _history = history;
        _dialogs = dialogs;
        IsDemo = mode.IsDemo;
    }

    public override string Title => "Connexions";
    public bool IsDemo { get; }
    public bool IsNotDemo => !IsDemo;

    // --- YouTube ---
    [ObservableProperty] private string? _youTubeClientId;
    [ObservableProperty] private string? _youTubeClientSecret;
    [ObservableProperty] private string? _youTubeApiKey;
    [ObservableProperty] private bool _youTubeConnected;
    [ObservableProperty] private bool _youTubeConfigured;
    [ObservableProperty] private string _youTubeStatus = "Non connecté";
    [ObservableProperty] private string? _youTubeAccount;
    [ObservableProperty] private bool _youTubeHasSecret;
    [ObservableProperty] private bool _youTubeHasApiKey;
    [ObservableProperty] private bool _youTubeBusy;

    // --- Spotify ---
    [ObservableProperty] private string? _spotifyClientId;
    [ObservableProperty] private int _spotifyRedirectPort = 5543;
    [ObservableProperty] private bool _spotifyConnected;
    [ObservableProperty] private bool _spotifyConfigured;
    [ObservableProperty] private string _spotifyStatus = "Non connecté";
    [ObservableProperty] private string? _spotifyAccount;
    [ObservableProperty] private bool _spotifyBusy;

    public string SpotifyRedirectUri => $"http://127.0.0.1:{SpotifyRedirectPort}/callback";
    partial void OnSpotifyRedirectPortChanged(int value) => OnPropertyChanged(nameof(SpotifyRedirectUri));

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var s = _settings.Current;
        YouTubeClientId = s.YouTube.ClientId;
        SpotifyClientId = s.Spotify.ClientId;
        SpotifyRedirectPort = s.Spotify.RedirectPort;
        YouTubeClientSecret = null;
        YouTubeApiKey = null;
        YouTubeHasSecret = !string.IsNullOrEmpty(await _secrets.GetAsync(SecretKeys.GoogleClientSecret));
        YouTubeHasApiKey = !string.IsNullOrEmpty(await _secrets.GetAsync(SecretKeys.YouTubeApiKey));
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var yt = await _connections.YouTube.GetStatusAsync(CancellationToken.None);
        YouTubeConnected = yt.IsConnected;
        YouTubeConfigured = yt.IsConfigured;
        YouTubeAccount = yt.AccountName;
        YouTubeStatus = yt.IsConnected ? $"Connecté{(yt.AccountName is null ? string.Empty : " · " + yt.AccountName)}"
            : yt.IsConfigured ? "Configuré, non connecté" : YouTubeHasApiKey ? "Clé API seule (playlists publiques)" : "Non configuré";

        var sp = await _connections.Spotify.GetStatusAsync(CancellationToken.None);
        SpotifyConnected = sp.IsConnected;
        SpotifyConfigured = sp.IsConfigured;
        SpotifyAccount = sp.AccountName;
        SpotifyStatus = sp.IsConnected ? $"Connecté{(sp.AccountName is null ? string.Empty : " · " + sp.AccountName)}"
            : sp.IsConfigured ? "Configuré, non connecté" : "Non configuré";
    }

    [RelayCommand]
    private Task SaveYouTube() => RunAsync(async () =>
    {
        var settings = _settings.Current.Clone();
        settings.YouTube.ClientId = YouTubeClientId?.Trim();
        await _settings.SaveAsync(settings);
        if (!string.IsNullOrWhiteSpace(YouTubeClientSecret))
            await _secrets.SetAsync(SecretKeys.GoogleClientSecret, YouTubeClientSecret.Trim());
        if (!string.IsNullOrWhiteSpace(YouTubeApiKey))
            await _secrets.SetAsync(SecretKeys.YouTubeApiKey, YouTubeApiKey.Trim());
        YouTubeClientSecret = null;
        YouTubeApiKey = null;
        YouTubeHasSecret = !string.IsNullOrEmpty(await _secrets.GetAsync(SecretKeys.GoogleClientSecret));
        YouTubeHasApiKey = !string.IsNullOrEmpty(await _secrets.GetAsync(SecretKeys.YouTubeApiKey));
        await RefreshStatusAsync();
    }, successTitle: "Configuration YouTube enregistrée", successMessage: "Les secrets sont chiffrés avec votre compte Windows.");

    [RelayCommand]
    private Task ClearYouTubeSecrets() => RunAsync(async () =>
    {
        await _secrets.DeleteAsync(SecretKeys.GoogleClientSecret);
        await _secrets.DeleteAsync(SecretKeys.YouTubeApiKey);
        YouTubeHasSecret = false;
        YouTubeHasApiKey = false;
        await RefreshStatusAsync();
    }, successTitle: "Secrets YouTube supprimés");

    [RelayCommand]
    private async Task ConnectYouTube()
    {
        YouTubeBusy = true;
        _signInCts = new CancellationTokenSource();
        await RunAsync(async () =>
        {
            if (!(await _connections.YouTube.GetStatusAsync(CancellationToken.None)).IsConfigured)
                throw new Musilib.Application.Errors.ConfigurationMissingException("Renseignez et enregistrez les identifiants Google (ID client et code secret) avant de vous connecter.");
            Notifications.Show("Autorisation YouTube", "Terminez la connexion dans votre navigateur.");
            await _connections.YouTube.SignInAsync(_signInCts.Token);
            var channel = await _youTube.GetChannelTitleAsync(CancellationToken.None);
            await _connections.YouTube.SetAccountNameAsync(channel, CancellationToken.None);
            await _history.AddAsync(new HistoryEntry { Kind = HistoryKind.Connection, Title = "Connexion YouTube", Details = channel });
            await RefreshStatusAsync();
        }, successTitle: "YouTube connecté");
        YouTubeBusy = false;
    }

    [RelayCommand]
    private Task DisconnectYouTube() => RunAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Déconnecter YouTube ?", "Le jeton d'accès sera révoqué et supprimé de cet ordinateur.", "Déconnecter", danger: true)) return;
        await _connections.YouTube.SignOutAsync(CancellationToken.None);
        await _history.AddAsync(new HistoryEntry { Kind = HistoryKind.Connection, Title = "Déconnexion YouTube" });
        await RefreshStatusAsync();
    });

    [RelayCommand]
    private Task TestYouTube() => RunAsync(async () =>
    {
        var playlists = await _youTube.GetMyPlaylistsAsync(CancellationToken.None);
        Notifications.Success("Connexion YouTube opérationnelle", $"{playlists.Count} playlist(s) accessible(s).");
    }, "Test en cours…");

    [RelayCommand]
    private Task SaveSpotify() => RunAsync(async () =>
    {
        var settings = _settings.Current.Clone();
        settings.Spotify.ClientId = SpotifyClientId?.Trim();
        settings.Spotify.RedirectPort = SpotifyRedirectPort;
        await _settings.SaveAsync(settings);
        await RefreshStatusAsync();
    }, successTitle: "Configuration Spotify enregistrée");

    [RelayCommand]
    private async Task ConnectSpotify()
    {
        SpotifyBusy = true;
        _signInCts = new CancellationTokenSource();
        await RunAsync(async () =>
        {
            if (!(await _connections.Spotify.GetStatusAsync(CancellationToken.None)).IsConfigured)
                throw new Musilib.Application.Errors.ConfigurationMissingException("Renseignez et enregistrez les identifiants Spotify (Client ID) avant de vous connecter.");
            Notifications.Show("Autorisation Spotify", "Terminez la connexion dans votre navigateur.");
            await _connections.Spotify.SignInAsync(_signInCts.Token);
            var me = await _spotify.GetCurrentUserAsync(CancellationToken.None);
            await _connections.Spotify.SetAccountNameAsync(me.DisplayName, CancellationToken.None);
            await _history.AddAsync(new HistoryEntry { Kind = HistoryKind.Connection, Title = "Connexion Spotify", Details = me.DisplayName });
            await RefreshStatusAsync();
        }, successTitle: "Spotify connecté");
        SpotifyBusy = false;
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    [RelayCommand]
    private Task DisconnectSpotify() => RunAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Déconnecter Spotify ?", "Les jetons seront supprimés de cet ordinateur. Vous pourrez aussi retirer l'accès depuis votre compte Spotify.", "Déconnecter", danger: true)) return;
        await _connections.Spotify.SignOutAsync(CancellationToken.None);
        await _history.AddAsync(new HistoryEntry { Kind = HistoryKind.Connection, Title = "Déconnexion Spotify" });
        await RefreshStatusAsync();
    });

    [RelayCommand]
    private Task TestSpotify() => RunAsync(async () =>
    {
        var me = await _spotify.GetCurrentUserAsync(CancellationToken.None);
        var playlists = await _spotify.GetMyPlaylistsAsync(CancellationToken.None);
        Notifications.Success("Connexion Spotify opérationnelle", $"{me.DisplayName} · {playlists.Count} playlist(s).");
    }, "Test en cours…");

    [RelayCommand] private Task CopyRedirectUri() => _dialogs.CopyAsync(SpotifyRedirectUri);
    [RelayCommand] private void OpenGoogleConsole() => _dialogs.OpenUrl("https://console.cloud.google.com/apis/credentials");
    [RelayCommand] private void OpenSpotifyDashboard() => _dialogs.OpenUrl("https://developer.spotify.com/dashboard");
}
