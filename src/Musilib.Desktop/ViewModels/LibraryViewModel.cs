using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Jobs;
using Musilib.Application.Services;
using Musilib.Desktop.Converters;
using Musilib.Desktop.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Desktop.ViewModels;

public sealed partial class LibraryPlaylistViewModel : ObservableObject
{
    private readonly LibraryViewModel _owner;

    public LibraryPlaylistViewModel(Playlist playlist, LibraryViewModel owner)
    {
        Playlist = playlist;
        _owner = owner;
    }

    public Playlist Playlist { get; }
    public string Title => Playlist.Title;
    public string? Cover => Playlist.ThumbnailUrl;
    public string Owner => Playlist.Owner ?? "YouTube";
    public string Count => $"{Playlist.Stats.Total} morceaux";
    public string Synced => $"Synchronisée {Formatters.Relative(Playlist.LastSyncedAt ?? Playlist.ImportedAt)}";
    public double MatchedPercent => Playlist.Stats.MatchedRatio * 100;
    public string MatchedText => $"{Playlist.Stats.Matched} associés";
    public string ReviewText => $"{Playlist.Stats.Ambiguous} à vérifier";
    public bool HasReview => Playlist.Stats.Ambiguous > 0;
    public string MissingText => $"{Playlist.Stats.NoMatch} introuvables";
    public bool HasMissing => Playlist.Stats.NoMatch > 0;
    public bool HasPending => Playlist.Stats.Pending > 0;
    public string PendingText => $"{Playlist.Stats.Pending} en attente";

    [RelayCommand] private void Open() => _owner.Open(this);
    [RelayCommand] private Task Sync() => _owner.SyncAsync(this);
    [RelayCommand] private Task Delete() => _owner.DeleteAsync(this);
}

public sealed partial class RemotePlaylistViewModel : ObservableObject
{
    private readonly LibraryViewModel _owner;

    public RemotePlaylistViewModel(RemotePlaylist remote, bool imported, LibraryViewModel owner)
    {
        Remote = remote;
        _isImported = imported;
        _owner = owner;
    }

    public RemotePlaylist Remote { get; }
    public string Title => Remote.Title;
    public string? Cover => Remote.ThumbnailUrl;
    public string Count => $"{Remote.ItemCount} vidéos";
    public string? Description => string.IsNullOrWhiteSpace(Remote.Description) ? null : Remote.Description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportLabel))]
    private bool _isImported;

    [ObservableProperty]
    private bool _isQueued;

    public string ImportLabel => IsImported ? "Resynchroniser" : "Importer";

    [RelayCommand] private Task Import() => _owner.ImportRemoteAsync(this);
}

public sealed partial class LibraryViewModel : PageViewModel
{
    private readonly PlaylistImportService _import;
    private readonly IPlaylistRepository _playlists;
    private readonly IJobQueue _queue;
    private readonly AppState _state;
    private readonly NavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly IHistoryRepository _history;

    public LibraryViewModel(PlaylistImportService import, IPlaylistRepository playlists, IJobQueue queue, AppState state,
        NavigationService navigation, DialogService dialogs, IHistoryRepository history, NotificationService notifications,
        ILogger<LibraryViewModel> logger)
        : base(notifications, logger)
    {
        _import = import;
        _playlists = playlists;
        _queue = queue;
        _state = state;
        _navigation = navigation;
        _dialogs = dialogs;
        _history = history;
        state.JobUpdated += (_, job) =>
        {
            if (_active && job.Type == JobType.Import && job.IsTerminal) _ = LoadLocalAsync();
        };
    }

    private bool _active;

    public override string Title => "Bibliothèque";

    public ObservableCollection<LibraryPlaylistViewModel> LocalPlaylists { get; } = new();
    public ObservableCollection<RemotePlaylistViewModel> RemotePlaylists { get; } = new();

    [ObservableProperty] private string? _importUrl;
    [ObservableProperty] private bool _autoMatch = true;
    [ObservableProperty] private bool _remoteLoaded;
    [ObservableProperty] private bool _remoteLoading;
    [ObservableProperty] private string? _remoteError;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string? _filter;

    public bool HasLocal => LocalPlaylists.Count > 0;
    public bool HasNoLocal => LocalPlaylists.Count == 0;
    public bool HasRemoteError => !string.IsNullOrEmpty(RemoteError);

    partial void OnRemoteErrorChanged(string? value) => OnPropertyChanged(nameof(HasRemoteError));
    partial void OnFilterChanged(string? value) => _ = LoadLocalAsync();
    partial void OnSelectedTabChanged(int value)
    {
        if (value == 1 && !RemoteLoaded && !RemoteLoading) _ = LoadRemoteAsync();
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _active = true;
        await LoadLocalAsync();
    }

    public override void OnNavigatedFrom() => _active = false;

    private async Task LoadLocalAsync()
    {
        var all = await _playlists.GetAllAsync();
        LocalPlaylists.Clear();
        foreach (var p in all.Where(p => string.IsNullOrWhiteSpace(Filter) || p.Title.Contains(Filter, StringComparison.CurrentCultureIgnoreCase)))
            LocalPlaylists.Add(new LibraryPlaylistViewModel(p, this));
        OnPropertyChanged(nameof(HasLocal));
        OnPropertyChanged(nameof(HasNoLocal));
        foreach (var r in RemotePlaylists) r.IsImported = all.Any(p => p.ExternalId == r.Remote.Id);
    }

    [RelayCommand]
    private async Task LoadRemoteAsync()
    {
        RemoteLoading = true;
        RemoteError = null;
        var ok = await RunAsync(async () =>
        {
            var remote = await _import.GetRemotePlaylistsAsync(CancellationToken.None);
            var local = await _playlists.GetAllAsync();
            RemotePlaylists.Clear();
            foreach (var r in remote) RemotePlaylists.Add(new RemotePlaylistViewModel(r, local.Any(p => p.ExternalId == r.Id), this));
            RemoteLoaded = true;
        });
        if (!ok) RemoteError = "Impossible de récupérer vos playlists. Vérifiez la connexion YouTube dans l'écran Connexions.";
        RemoteLoading = false;
    }

    [RelayCommand]
    private Task ImportFromUrl() => RunAsync(async () =>
    {
        if (!PlaylistIdParser.TryParse(ImportUrl, out var id))
            throw new ArgumentException("URL ou identifiant de playlist YouTube invalide (ex. https://www.youtube.com/playlist?list=PL...).");
        await EnqueueImportAsync(id, ImportUrl!.Trim());
        ImportUrl = null;
    });

    internal Task ImportRemoteAsync(RemotePlaylistViewModel remote) => RunAsync(async () =>
    {
        await EnqueueImportAsync(remote.Remote.Id, remote.Title);
        remote.IsQueued = true;
    });

    private async Task EnqueueImportAsync(string playlistId, string label)
    {
        await _queue.EnqueueAsync(JobType.Import, null, $"Import · {label}", new ImportJobPayload { ExternalPlaylistId = playlistId, AutoMatch = AutoMatch });
        Notifications.Success("Import ajouté à la file", AutoMatch ? "La recherche Spotify démarrera automatiquement ensuite." : label);
    }

    internal void Open(LibraryPlaylistViewModel item)
    {
        _state.SelectPlaylist(item.Playlist.Id);
        _navigation.NavigateTo(PageKey.Playlist, item.Playlist.Id);
    }

    internal Task SyncAsync(LibraryPlaylistViewModel item) => RunAsync(() =>
        EnqueueImportAsync(item.Playlist.ExternalId, item.Title));

    internal Task DeleteAsync(LibraryPlaylistViewModel item) => RunAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync($"Supprimer « {item.Title} » ?",
                "La playlist, ses correspondances et ses métadonnées éditées seront supprimées de Musilib. Vos fichiers et vos comptes YouTube/Spotify ne sont pas modifiés.",
                "Supprimer", danger: true)) return;
        await _playlists.DeleteAsync(item.Playlist.Id);
        await _history.AddAsync(new HistoryEntry { Kind = HistoryKind.System, Title = $"Playlist supprimée : {item.Title}" });
        await _state.RefreshPlaylistsAsync();
        await LoadLocalAsync();
        Notifications.Success("Playlist supprimée");
    });
}
