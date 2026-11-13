using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Services;
using Musilib.Desktop.Converters;
using Musilib.Desktop.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Desktop.ViewModels;

public sealed record StatCard(string Label, string Value, string? Hint, string IconKey, string Kind)
{
    public bool IsAccent => Kind == "accent";
    public bool IsWarning => Kind == "warning";
    public bool IsInfo => Kind == "info";
    public bool IsPurple => Kind == "purple";
    public bool IsDanger => Kind == "danger";
}

public sealed partial class PlaylistCardViewModel : ObservableObject
{
    private readonly Action<PlaylistCardViewModel> _open;

    public PlaylistCardViewModel(Playlist playlist, Action<PlaylistCardViewModel> open)
    {
        Playlist = playlist;
        _open = open;
    }

    public Playlist Playlist { get; }
    public string Title => Playlist.Title;
    public string? Cover => Playlist.ThumbnailUrl;
    public string Subtitle => $"{Playlist.Stats.Total} morceaux · {Formatters.Relative(Playlist.LastSyncedAt ?? Playlist.ImportedAt)}";
    public double MatchedPercent => Playlist.Stats.MatchedRatio * 100;
    public string MatchedText => $"{Playlist.Stats.Matched}/{Playlist.Stats.Total} associés";
    public int NeedsReview => Playlist.Stats.NeedsReview;
    public bool HasReview => Playlist.Stats.Ambiguous > 0;
    public string ReviewText => $"{Playlist.Stats.Ambiguous} à vérifier";

    [RelayCommand]
    private void Open() => _open(this);
}

public sealed record HistoryLine(HistoryEntry Entry)
{
    public string Title => Entry.Title;
    public string? Details => Entry.Details;
    public bool HasDetails => !string.IsNullOrWhiteSpace(Entry.Details);
    public string When => Formatters.Relative(Entry.Timestamp);
    public string Date => Formatters.DateTime(Entry.Timestamp);
    public bool Success => Entry.Success;
    public bool Failed => !Entry.Success;
    public string KindText => Entry.Kind switch
    {
        HistoryKind.Import => "Import",
        HistoryKind.Match => "Correspondance",
        HistoryKind.Export => "Export",
        HistoryKind.Transfer => "Transfert",
        HistoryKind.Edit => "Édition",
        HistoryKind.Connection => "Connexion",
        _ => "Système"
    };
    public string IconKey => Entry.Kind switch
    {
        HistoryKind.Import => "Icon.YouTube",
        HistoryKind.Match => "Icon.Compare",
        HistoryKind.Export => "Icon.Download",
        HistoryKind.Transfer => "Icon.Send",
        HistoryKind.Edit => "Icon.Edit",
        HistoryKind.Connection => "Icon.Link",
        _ => "Icon.Info"
    };
}

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly DashboardService _dashboard;
    private readonly NavigationService _navigation;
    private readonly AppState _state;

    public DashboardViewModel(DashboardService dashboard, NavigationService navigation, AppState state, AppMode mode,
        NotificationService notifications, ILogger<DashboardViewModel> logger)
        : base(notifications, logger)
    {
        _dashboard = dashboard;
        _navigation = navigation;
        _state = state;
        IsDemo = mode.IsDemo;
        state.JobUpdated += (_, job) =>
        {
            if (_isActive && job.IsTerminal) _ = LoadAsync();
        };
    }

    private bool _isActive;

    public override string Title => "Tableau de bord";
    public bool IsDemo { get; }

    public ObservableCollection<StatCard> Stats { get; } = new();
    public ObservableCollection<PlaylistCardViewModel> RecentPlaylists { get; } = new();
    public ObservableCollection<JobViewModel> RecentJobs { get; } = new();
    public ObservableCollection<HistoryLine> RecentHistory { get; } = new();

    [ObservableProperty]
    private string _greeting = "Bonjour";

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _hasPlaylists;

    [ObservableProperty]
    private int _reviewCount;

    public bool HasReview => ReviewCount > 0;
    public bool HasNoJobs => RecentJobs.Count == 0;
    partial void OnReviewCountChanged(int value) => OnPropertyChanged(nameof(HasReview));

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _isActive = true;
        await LoadAsync();
    }

    public override void OnNavigatedFrom() => _isActive = false;

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        var data = await _dashboard.GetAsync(CancellationToken.None);
        var hour = DateTime.Now.Hour;
        Greeting = hour < 6 ? "Bonne nuit" : hour < 18 ? "Bonjour" : "Bonsoir";
        var t = data.Totals;
        Summary = data.PlaylistCount == 0
            ? "Connectez YouTube puis importez votre première playlist pour commencer."
            : $"{data.PlaylistCount} playlist(s) · {t.Total} morceaux · {t.Matched} associés à Spotify";
        ReviewCount = t.Ambiguous;
        HasPlaylists = data.PlaylistCount > 0;

        Stats.Clear();
        Stats.Add(new StatCard("Playlists", data.PlaylistCount.ToString(), $"{t.Total} morceaux importés", "Icon.Library", "info"));
        Stats.Add(new StatCard("Associés à Spotify", t.Matched.ToString(), t.Total == 0 ? "—" : $"{t.MatchedRatio:P0} du total", "Icon.CheckCircle", "accent"));
        Stats.Add(new StatCard("À vérifier", t.Ambiguous.ToString(), $"{t.NoMatch} introuvables · {t.Errors} erreurs", "Icon.Alert", "warning"));
        Stats.Add(new StatCard("Transferts réussis", data.TransfersDone.ToString(), $"{data.ExportsDone} exports locaux", "Icon.Send", "purple"));
        Stats.Add(new StatCard("Recherches en cache", data.CachedSearches.ToString(), "appels API évités", "Icon.Pulse", "info"));
        Stats.Add(new StatCard("Indisponibles", t.Unavailable.ToString(), "vidéos supprimées/privées", "Icon.EyeOff", "danger"));

        RecentPlaylists.Clear();
        foreach (var p in data.RecentPlaylists) RecentPlaylists.Add(new PlaylistCardViewModel(p, OpenPlaylist));

        RecentJobs.Clear();
        foreach (var j in data.RecentJobs) RecentJobs.Add(new JobViewModel(j));
        OnPropertyChanged(nameof(HasNoJobs));

        RecentHistory.Clear();
        foreach (var h in data.RecentHistory) RecentHistory.Add(new HistoryLine(h));
    });

    private void OpenPlaylist(PlaylistCardViewModel card)
    {
        _state.SelectPlaylist(card.Playlist.Id);
        _navigation.NavigateTo(PageKey.Playlist, card.Playlist.Id);
    }

    [RelayCommand] private void GoLibrary() => _navigation.NavigateTo(PageKey.Library);
    [RelayCommand] private void GoConnections() => _navigation.NavigateTo(PageKey.Connections);
    [RelayCommand] private void GoReview() => _navigation.NavigateTo(PageKey.MatchReview);
    [RelayCommand] private void GoQueue() => _navigation.NavigateTo(PageKey.Queue);
    [RelayCommand] private void GoHistory() => _navigation.NavigateTo(PageKey.History);
}
