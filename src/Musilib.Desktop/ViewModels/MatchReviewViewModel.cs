using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Services;
using Musilib.Desktop.Converters;
using Musilib.Desktop.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Desktop.ViewModels;

/// <summary>Écran de comparaison des correspondances Spotify et de sélection manuelle.</summary>
public sealed partial class MatchReviewViewModel : PageViewModel
{
    private readonly ITrackRepository _tracks;
    private readonly MatchingService _matching;
    private readonly TrackEditingService _editing;
    private readonly NavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly List<TrackRowViewModel> _all = new();
    private Guid? _pendingSelection;

    public MatchReviewViewModel(ITrackRepository tracks, MatchingService matching, TrackEditingService editing, AppState state,
        PlayerViewModel player, NavigationService navigation, DialogService dialogs, NotificationService notifications,
        ILogger<MatchReviewViewModel> logger)
        : base(notifications, logger)
    {
        _tracks = tracks;
        _matching = matching;
        _editing = editing;
        State = state;
        Player = player;
        _navigation = navigation;
        _dialogs = dialogs;
    }

    public override string Title => "Correspondances";
    public override bool UsesPlaylist => true;
    public AppState State { get; }
    public PlayerViewModel Player { get; }

    public ObservableCollection<TrackRowViewModel> Queue { get; } = new();
    public ObservableCollection<CandidateViewModel> Candidates { get; } = new();

    [ObservableProperty] private TrackRowViewModel? _selected;
    [ObservableProperty] private bool _onlyToReview = true;
    [ObservableProperty] private string? _manualQuery;
    [ObservableProperty] private bool _applyMetadata = true;
    [ObservableProperty] private bool _autoAdvance = true;
    [ObservableProperty] private bool _candidatesLoading;

    public bool HasSelection => Selected is not null;
    public bool HasNoSelection => Selected is null;
    public bool HasCandidates => Candidates.Count > 0;
    public bool NoCandidates => Selected is not null && Candidates.Count == 0 && !CandidatesLoading;
    public bool IsQueueEmpty => Queue.Count == 0;
    public string QueueTitle => OnlyToReview ? $"À vérifier ({Queue.Count})" : $"Tous les morceaux ({Queue.Count})";

    // Informations source
    public string SourceTitle => Selected?.Track.SourceTitle ?? string.Empty;
    public string SourceChannel => Selected?.Track.SourceChannel ?? "—";
    public string SourceDuration => Formatters.Duration(Selected?.Track.SourceDurationMs);
    public string? SourceThumb => Selected?.Track.SourceThumbnailUrl;
    public string ParsedTitle => Selected?.Track.Metadata.Title ?? string.Empty;
    public string ParsedArtists => Selected?.Track.Metadata.ArtistsDisplay ?? string.Empty;
    public string? StatusReason => Selected?.Track.LastError;
    public bool HasStatusReason => !string.IsNullOrWhiteSpace(StatusReason);
    public bool CanConfirm => Selected?.Track.MatchStatus == MatchStatus.AutoMatched;
    public BadgeInfo? Badge => Selected is null ? null : BadgeInfo.ForTrack(Selected.Track);

    partial void OnCandidatesLoadingChanged(bool value) => OnPropertyChanged(nameof(NoCandidates));

    partial void OnOnlyToReviewChanged(bool value) => ApplyQueueFilter(Selected?.Track.Id);

    partial void OnSelectedChanged(TrackRowViewModel? value)
    {
        foreach (var name in new[] { nameof(HasSelection), nameof(HasNoSelection), nameof(SourceTitle), nameof(SourceChannel), nameof(SourceDuration),
                     nameof(SourceThumb), nameof(ParsedTitle), nameof(ParsedArtists), nameof(StatusReason), nameof(HasStatusReason),
                     nameof(CanConfirm), nameof(Badge), nameof(NoCandidates) })
            OnPropertyChanged(name);
        ManualQuery = value is null ? null : value.Track.CustomSearchQuery ?? $"{value.Track.Metadata.PrimaryArtist} {value.Track.Metadata.Title}".Trim();
        _ = LoadCandidatesAsync();
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is Guid trackId)
        {
            var track = await _tracks.GetAsync(trackId);
            if (track is not null)
            {
                State.SelectPlaylist(track.PlaylistId);
                _pendingSelection = trackId;
                if (track.MatchStatus is MatchStatus.AutoMatched or MatchStatus.Confirmed) OnlyToReview = false;
            }
        }
        await LoadAsync();
    }

    public override Task OnPlaylistChangedAsync()
    {
        _pendingSelection = null;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        _all.Clear();
        if (State.SelectedPlaylist is { } playlist)
        {
            var tracks = await _tracks.GetByPlaylistAsync(playlist.Id);
            _all.AddRange(tracks.Select(t => new TrackRowViewModel(t)));
        }
        ApplyQueueFilter(_pendingSelection);
        _pendingSelection = null;
    }

    private static bool NeedsReview(Track t) => !t.Excluded && t.MatchStatus is MatchStatus.Ambiguous or MatchStatus.NoMatch or MatchStatus.Error or MatchStatus.Pending;

    private void ApplyQueueFilter(Guid? select)
    {
        Queue.Clear();
        foreach (var r in _all.Where(r => !OnlyToReview || NeedsReview(r.Track) || r.Track.Id == select)) Queue.Add(r);
        OnPropertyChanged(nameof(IsQueueEmpty));
        OnPropertyChanged(nameof(QueueTitle));
        Selected = Queue.FirstOrDefault(r => r.Track.Id == select) ?? Queue.FirstOrDefault();
    }

    private async Task LoadCandidatesAsync()
    {
        Candidates.Clear();
        OnPropertyChanged(nameof(HasCandidates));
        var row = Selected;
        if (row is null) return;
        CandidatesLoading = true;
        try
        {
            var list = await _matching.GetCandidatesAsync(row.Track, CancellationToken.None);
            if (row != Selected) return;
            SetCandidates(row, list);
        }
        finally
        {
            CandidatesLoading = false;
        }
    }

    private void SetCandidates(TrackRowViewModel row, IReadOnlyList<SpotifyCandidate> list)
    {
        Candidates.Clear();
        var bestId = list.OrderByDescending(c => c.Score).FirstOrDefault()?.SpotifyId;
        foreach (var c in list.OrderByDescending(c => c.Score))
        {
            Candidates.Add(new CandidateViewModel(c, row.Track.SourceDurationMs ?? row.Track.Metadata.DurationMs, c.SpotifyId == bestId,
                c.SpotifyId == row.Track.SpotifyTrackId, SelectCandidateAsync, PlayCandidateAsync));
        }
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(NoCandidates));
    }

    private void RefreshSelected(Track track)
    {
        Selected?.Update(track);
        foreach (var name in new[] { nameof(StatusReason), nameof(HasStatusReason), nameof(CanConfirm), nameof(Badge), nameof(ParsedTitle), nameof(ParsedArtists) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private Task ManualSearch() => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null || string.IsNullOrWhiteSpace(ManualQuery)) return;
        CandidatesLoading = true;
        await _matching.ManualSearchAsync(row.Track, ManualQuery.Trim(), CancellationToken.None);
        var list = await _matching.GetCandidatesAsync(row.Track, CancellationToken.None);
        SetCandidates(row, list);
        CandidatesLoading = false;
        if (list.Count == 0) Notifications.Show("Aucun résultat", "Essayez une requête plus courte (artiste + titre).");
    }, "Recherche…");

    [RelayCommand]
    private Task Rematch() => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null) return;
        await _editing.SetCustomSearchQueryAsync(row.Track, null, CancellationToken.None);
        var decision = await _matching.MatchTrackAsync(row.Track, force: true, CancellationToken.None);
        RefreshSelected(row.Track);
        SetCandidates(row, decision.Ranked.Take(10).ToList());
        Notifications.Show("Recherche relancée", decision.Reason);
    }, "Recherche…");

    private Task SelectCandidateAsync(CandidateViewModel candidate) => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null) return;
        await _matching.SelectCandidateAsync(row.Track, candidate.Candidate, ApplyMetadata, CancellationToken.None);
        foreach (var c in Candidates) c.IsCurrent = c == candidate;
        RefreshSelected(row.Track);
        Notifications.Success("Correspondance validée", $"{candidate.Artists} - {candidate.Title}");
        if (AutoAdvance) Advance(row);
    });

    [RelayCommand]
    private Task Confirm() => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null) return;
        await _matching.ConfirmAsync(row.Track, CancellationToken.None);
        RefreshSelected(row.Track);
        if (AutoAdvance) Advance(row);
    });

    [RelayCommand]
    private Task Reject() => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null) return;
        await _matching.RejectAsync(row.Track, CancellationToken.None);
        foreach (var c in Candidates) c.IsCurrent = false;
        RefreshSelected(row.Track);
        if (AutoAdvance) Advance(row);
    });

    [RelayCommand]
    private Task Exclude() => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null) return;
        await _editing.SetExcludedAsync(row.Track, !row.Track.Excluded, CancellationToken.None);
        RefreshSelected(row.Track);
        if (AutoAdvance && row.Track.Excluded) Advance(row);
    });

    private void Advance(TrackRowViewModel current)
    {
        var index = Queue.IndexOf(current);
        if (OnlyToReview && !NeedsReview(current.Track))
        {
            Queue.Remove(current);
            OnPropertyChanged(nameof(IsQueueEmpty));
            OnPropertyChanged(nameof(QueueTitle));
            Selected = Queue.Count == 0 ? null : Queue[Math.Clamp(index, 0, Queue.Count - 1)];
            if (Queue.Count == 0) Notifications.Success("Tout est vérifié", "Plus aucune correspondance en attente pour cette playlist.");
        }
        else if (index >= 0 && index < Queue.Count - 1)
        {
            Selected = Queue[index + 1];
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (Selected is null) return;
        var i = Queue.IndexOf(Selected);
        if (i < Queue.Count - 1) Selected = Queue[i + 1];
    }

    [RelayCommand]
    private void Previous()
    {
        if (Selected is null) return;
        var i = Queue.IndexOf(Selected);
        if (i > 0) Selected = Queue[i - 1];
    }

    private async Task PlayCandidateAsync(CandidateViewModel candidate)
    {
        if (!candidate.HasPreview)
        {
            Notifications.Show("Aucun extrait disponible", "Spotify ne fournit pas d'extrait audio pour ce titre.");
            return;
        }
        foreach (var c in Candidates) c.IsPlaying = c == candidate;
        await Player.PlayAsync(candidate.Candidate.PreviewUrl!, candidate.Title, candidate.Artists, candidate.Cover, "Extrait Spotify");
    }

    [RelayCommand]
    private void OpenYouTube()
    {
        if (Selected is { } row && !string.IsNullOrEmpty(row.Track.SourceVideoId) && Selected.Track.Availability == SourceAvailability.Available)
            _dialogs.OpenUrl($"https://www.youtube.com/watch?v={row.Track.SourceVideoId}");
    }

    [RelayCommand]
    private void OpenInSpotify(CandidateViewModel? candidate)
    {
        if (candidate?.Candidate.ExternalUrl is { } url) _dialogs.OpenUrl(url);
    }

    [RelayCommand]
    private void EditMetadata()
    {
        if (Selected is not null) _navigation.NavigateTo(PageKey.Editor, Selected.Track.Id);
    }
}
