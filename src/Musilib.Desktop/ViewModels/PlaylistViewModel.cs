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

/// <summary>Écran « Import et analyse » : morceaux d'une playlist, statut des correspondances, lancement des recherches.</summary>
public sealed partial class PlaylistViewModel : PageViewModel
{
    private readonly ITrackRepository _tracks;
    private readonly IJobQueue _queue;
    private readonly TrackEditingService _editing;
    private readonly NavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly List<TrackRowViewModel> _all = new();
    private DateTimeOffset _lastLiveReload = DateTimeOffset.MinValue;
    private bool _active;

    public PlaylistViewModel(ITrackRepository tracks, IJobQueue queue, TrackEditingService editing, AppState state,
        NavigationService navigation, DialogService dialogs, NotificationService notifications, ILogger<PlaylistViewModel> logger)
        : base(notifications, logger)
    {
        _tracks = tracks;
        _queue = queue;
        _editing = editing;
        State = state;
        _navigation = navigation;
        _dialogs = dialogs;
        state.JobUpdated += OnJobUpdated;
    }

    public override string Title => "Import & analyse";
    public override bool UsesPlaylist => true;
    public AppState State { get; }
    public ObservableCollection<TrackRowViewModel> Rows { get; } = new();

    [ObservableProperty] private Playlist? _playlist;
    [ObservableProperty] private string _filterKey = "all";
    [ObservableProperty] private string? _search;
    [ObservableProperty] private JobViewModel? _activeJob;
    [ObservableProperty] private PlaylistStats _stats = new();

    public bool HasPlaylist => Playlist is not null;
    public bool HasNoPlaylist => Playlist is null;
    public bool HasActiveJob => ActiveJob is not null;
    public string Header => Playlist?.Title ?? "Aucune playlist";
    public string SubHeader => Playlist is null ? string.Empty :
        $"{Stats.Total} morceaux · {Playlist.Owner ?? "YouTube"} · synchronisée {Formatters.Relative(Playlist.LastSyncedAt ?? Playlist.ImportedAt)}";
    public double MatchedPercent => Stats.MatchedRatio * 100;
    public string MatchedText => $"{Stats.Matched} / {Stats.Total} associés ({Stats.MatchedRatio:P0})";
    public string CountAll => Stats.Total.ToString();
    public string CountReview => Stats.Ambiguous.ToString();
    public string CountMatched => Stats.Matched.ToString();
    public string CountMissing => (Stats.NoMatch + Stats.Errors).ToString();
    public string CountPending => Stats.Pending.ToString();
    public string CountUnavailable => Stats.Unavailable.ToString();
    public string CountExcluded => Stats.Excluded.ToString();
    public bool IsEmptyResult => HasPlaylist && Rows.Count == 0;

    partial void OnPlaylistChanged(Playlist? value)
    {
        OnPropertyChanged(nameof(HasPlaylist));
        OnPropertyChanged(nameof(HasNoPlaylist));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(SubHeader));
    }

    partial void OnStatsChanged(PlaylistStats value)
    {
        foreach (var name in new[] { nameof(SubHeader), nameof(MatchedPercent), nameof(MatchedText), nameof(CountAll), nameof(CountReview),
                     nameof(CountMatched), nameof(CountMissing), nameof(CountPending), nameof(CountUnavailable), nameof(CountExcluded) })
            OnPropertyChanged(name);
    }

    partial void OnActiveJobChanged(JobViewModel? value) => OnPropertyChanged(nameof(HasActiveJob));
    partial void OnSearchChanged(string? value) => ApplyFilter();
    partial void OnFilterKeyChanged(string value) => ApplyFilter();

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _active = true;
        if (parameter is Guid id) State.SelectPlaylist(id);
        await LoadAsync();
    }

    public override void OnNavigatedFrom() => _active = false;
    public override Task OnPlaylistChangedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        Playlist = State.SelectedPlaylist;
        ActiveJob = null;
        if (Playlist is null)
        {
            _all.Clear();
            Rows.Clear();
            Stats = new PlaylistStats();
            return;
        }
        await ReloadTracksAsync();
    }

    private async Task ReloadTracksAsync()
    {
        if (Playlist is null) return;
        var tracks = await _tracks.GetByPlaylistAsync(Playlist.Id);
        Stats = await _tracks.GetStatsAsync(Playlist.Id);
        var byId = _all.ToDictionary(r => r.Track.Id);
        _all.Clear();
        foreach (var t in tracks)
        {
            if (byId.TryGetValue(t.Id, out var row)) { row.Update(t); _all.Add(row); }
            else _all.Add(new TrackRowViewModel(t, Compare, Edit, ToggleExcludeAsync));
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = Search?.Trim();
        var filtered = _all.Where(r => FilterKey switch
        {
            "review" => !r.Track.Excluded && r.Track.MatchStatus == MatchStatus.Ambiguous,
            "matched" => !r.Track.Excluded && r.Track.HasSpotifyMatch,
            "missing" => !r.Track.Excluded && r.Track.MatchStatus is MatchStatus.NoMatch or MatchStatus.Error,
            "pending" => !r.Track.Excluded && r.Track.MatchStatus == MatchStatus.Pending,
            "unavailable" => !r.Track.IsAvailable,
            "excluded" => r.Track.Excluded,
            _ => true
        }).Where(r => string.IsNullOrEmpty(q)
            || r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.Artists.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.SourceTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();

        // Mise à jour minimale pour conserver la position de défilement.
        if (filtered.Count == Rows.Count && filtered.SequenceEqual(Rows)) { OnPropertyChanged(nameof(IsEmptyResult)); return; }
        Rows.Clear();
        foreach (var r in filtered) Rows.Add(r);
        OnPropertyChanged(nameof(IsEmptyResult));
    }

    private void OnJobUpdated(object? sender, Job job)
    {
        if (!_active || Playlist is null || job.PlaylistId != Playlist.Id) return;
        if (job.Type is JobType.Match or JobType.Import)
        {
            if (job.IsTerminal || job.Status == JobStatus.Paused)
            {
                ActiveJob = null;
                _ = ReloadTracksAsync();
            }
            else
            {
                if (ActiveJob?.Id == job.Id) ActiveJob.Update(job);
                else ActiveJob = new JobViewModel(job, pause: j => _queue.PauseAsync(j.Id), cancel: j => _queue.CancelAsync(j.Id));
                if (DateTimeOffset.UtcNow - _lastLiveReload > TimeSpan.FromSeconds(1.5))
                {
                    _lastLiveReload = DateTimeOffset.UtcNow;
                    _ = ReloadTracksAsync();
                }
            }
        }
    }

    [RelayCommand]
    private void SetFilter(string key) => FilterKey = key;

    [RelayCommand]
    private Task Analyze() => RunAsync(async () =>
    {
        if (Playlist is null) return;
        await _queue.EnqueueAsync(JobType.Match, Playlist.Id, $"Recherche Spotify · {Playlist.Title}", new MatchJobPayload { PlaylistId = Playlist.Id });
    }, successTitle: "Analyse lancée", successMessage: "Les morceaux en attente vont être recherchés sur Spotify.");

    [RelayCommand]
    private Task ReanalyzeAll() => RunAsync(async () =>
    {
        if (Playlist is null) return;
        if (!await _dialogs.ConfirmAsync("Relancer toutes les recherches ?",
                "Les correspondances automatiques, ambiguës et introuvables seront recalculées. Les correspondances confirmées manuellement sont conservées. Les recherches déjà en cache n'appellent pas l'API.",
                "Relancer")) return;
        await _queue.EnqueueAsync(JobType.Match, Playlist.Id, $"Nouvelle analyse · {Playlist.Title}", new MatchJobPayload { PlaylistId = Playlist.Id, Force = true });
        Notifications.Success("Nouvelle analyse lancée");
    });

    [RelayCommand]
    private Task Sync() => RunAsync(async () =>
    {
        if (Playlist is null) return;
        await _queue.EnqueueAsync(JobType.Import, null, $"Synchronisation · {Playlist.Title}",
            new ImportJobPayload { ExternalPlaylistId = Playlist.ExternalId, AutoMatch = true });
    }, successTitle: "Synchronisation lancée");

    [RelayCommand] private void GoReview() => _navigation.NavigateTo(PageKey.MatchReview);
    [RelayCommand] private void GoTransfer() => _navigation.NavigateTo(PageKey.Transfer);
    [RelayCommand] private void GoExport() => _navigation.NavigateTo(PageKey.Export);
    [RelayCommand] private void GoLibrary() => _navigation.NavigateTo(PageKey.Library);

    private void Compare(TrackRowViewModel row) => _navigation.NavigateTo(PageKey.MatchReview, row.Track.Id);
    private void Edit(TrackRowViewModel row) => _navigation.NavigateTo(PageKey.Editor, row.Track.Id);

    private Task ToggleExcludeAsync(TrackRowViewModel row) => RunAsync(async () =>
    {
        await _editing.SetExcludedAsync(row.Track, !row.Track.Excluded, CancellationToken.None);
        row.Update(row.Track);
        Stats = await _tracks.GetStatsAsync(Playlist!.Id);
        ApplyFilter();
    });
}
