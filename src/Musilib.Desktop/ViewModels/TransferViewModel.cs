using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Common;
using Musilib.Application.Jobs;
using Musilib.Application.Services;
using Musilib.Desktop.Converters;
using Musilib.Desktop.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Desktop.ViewModels;

public sealed partial class TransferRowViewModel : ObservableObject
{
    private readonly TransferViewModel _owner;

    public TransferRowViewModel(TransferPlanItem item, TransferViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isChecked = item.State == TransferItemState.Ready;
    }

    public TransferPlanItem Item { get; }
    public Track Track => Item.Track;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _owner.OnSelectionChanged();

    public bool IsReady => Item.State == TransferItemState.Ready;
    public bool CanToggle => IsReady;
    public int Number => Track.Position + 1;
    public string SourceTitle => Track.SourceTitle;
    public string Title => Track.Metadata.Title;
    public string Artists => Track.Metadata.ArtistsDisplay;
    public string Album => Track.Metadata.Album ?? "—";
    public string Duration => Formatters.Duration(Track.Metadata.DurationMs ?? Track.SourceDurationMs);
    public string? Cover => Track.Metadata.CoverUrl ?? Track.SourceThumbnailUrl;
    public string FallbackText => $"{Track.Metadata.PrimaryArtist} {Track.Metadata.Title}";
    public string Note => Item.Note;
    public BadgeInfo Badge => Item.State switch
    {
        TransferItemState.Ready => Track.MatchStatus == MatchStatus.Confirmed ? new("Prêt · confirmé", "success") : new("Prêt · auto", "accent"),
        TransferItemState.NeedsReview => new("À valider", "warning"),
        TransferItemState.NoMatch => new("Sans correspondance", "danger"),
        TransferItemState.Excluded => new("Exclu", "muted"),
        _ => new("Indisponible", "muted")
    };

    [RelayCommand] private void Choose() => _owner.Choose(this);
}

public sealed partial class TransferViewModel : PageViewModel
{
    private readonly ITrackRepository _tracks;
    private readonly ISpotifyClient _spotify;
    private readonly IJobQueue _queue;
    private readonly IJobRepository _jobs;
    private readonly NavigationService _navigation;
    private readonly DialogService _dialogs;
    private Guid? _jobId;

    public TransferViewModel(ITrackRepository tracks, ISpotifyClient spotify, IJobQueue queue, IJobRepository jobs, AppState state,
        NavigationService navigation, DialogService dialogs, NotificationService notifications, ILogger<TransferViewModel> logger)
        : base(notifications, logger)
    {
        _tracks = tracks;
        _spotify = spotify;
        _queue = queue;
        _jobs = jobs;
        State = state;
        _navigation = navigation;
        _dialogs = dialogs;
        state.JobUpdated += OnJobUpdated;
    }

    public override string Title => "Transfert Spotify";
    public override bool UsesPlaylist => true;
    public AppState State { get; }
    public ObservableCollection<TransferRowViewModel> Rows { get; } = new();
    public ObservableCollection<SpotifyPlaylistInfo> ExistingPlaylists { get; } = new();
    public ObservableCollection<TransferLine> ReportAdded { get; } = new();
    public ObservableCollection<TransferLine> ReportSkipped { get; } = new();
    public ObservableCollection<TransferLine> ReportFailed { get; } = new();

    [ObservableProperty] private bool _createNew = true;
    [ObservableProperty] private string? _newName;
    [ObservableProperty] private string? _newDescription;
    [ObservableProperty] private bool _isPublic;
    [ObservableProperty] private SpotifyPlaylistInfo? _selectedExisting;
    [ObservableProperty] private bool _skipExisting = true;
    [ObservableProperty] private bool _playlistsLoaded;
    [ObservableProperty] private string _filterKey = "all";
    [ObservableProperty] private JobViewModel? _activeJob;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string? _reportTitle;
    [ObservableProperty] private string? _reportUrl;
    [ObservableProperty] private TransferPlan? _plan;

    public bool UseExisting
    {
        get => !CreateNew;
        set => CreateNew = !value;
    }

    partial void OnCreateNewChanged(bool value)
    {
        OnPropertyChanged(nameof(UseExisting));
        if (!value && !PlaylistsLoaded) _ = LoadSpotifyPlaylistsAsync();
    }

    partial void OnFilterKeyChanged(string value) => ApplyFilter();
    partial void OnActiveJobChanged(JobViewModel? value) => OnPropertyChanged(nameof(HasActiveJob));
    partial void OnPlanChanged(TransferPlan? value) => RaiseCounts();

    private readonly List<TransferRowViewModel> _all = new();

    public bool HasPlaylist => State.SelectedPlaylist is not null;
    public bool HasNoPlaylist => State.SelectedPlaylist is null;
    public bool HasActiveJob => ActiveJob is not null;
    public int SelectedCount => _all.Count(r => r.IsChecked && r.IsReady);
    public string ReadyText => $"{Plan?.Ready ?? 0} prêts";
    public string ReviewText => $"{Plan?.NeedsReview ?? 0} à valider";
    public string MissingText => $"{Plan?.NoMatch ?? 0} sans correspondance";
    public string OtherText => $"{(Plan?.Excluded ?? 0) + (Plan?.Unavailable ?? 0)} exclus/indisponibles";
    public bool HasReview => (Plan?.NeedsReview ?? 0) > 0;
    public string TransferButtonText => $"Transférer {SelectedCount} morceau(x)";
    public bool CanTransfer => SelectedCount > 0 && !HasActiveJob;

    public void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TransferButtonText));
        OnPropertyChanged(nameof(CanTransfer));
    }

    private void RaiseCounts()
    {
        foreach (var n in new[] { nameof(ReadyText), nameof(ReviewText), nameof(MissingText), nameof(OtherText), nameof(HasReview) }) OnPropertyChanged(n);
        OnSelectionChanged();
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadAsync();
        await RestoreLastReportAsync();
    }

    public override async Task OnPlaylistChangedAsync()
    {
        HasReport = false;
        await LoadAsync();
        await RestoreLastReportAsync();
    }

    private async Task LoadAsync()
    {
        OnPropertyChanged(nameof(HasPlaylist));
        OnPropertyChanged(nameof(HasNoPlaylist));
        _all.Clear();
        Rows.Clear();
        Plan = null;
        if (State.SelectedPlaylist is not { } p) return;
        NewName ??= p.Title;
        if (NewName != p.Title && !string.IsNullOrEmpty(NewName) && _lastPlaylistId != p.Id) NewName = p.Title;
        _lastPlaylistId = p.Id;
        NewDescription ??= "Importée depuis YouTube avec Musilib Manager";
        var tracks = await _tracks.GetByPlaylistAsync(p.Id);
        Plan = TransferService.BuildPlan(tracks);
        foreach (var item in Plan.Items) _all.Add(new TransferRowViewModel(item, this));
        ApplyFilter();
        RaiseCounts();
    }

    private Guid? _lastPlaylistId;

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in _all.Where(r => FilterKey switch
                 {
                     "ready" => r.IsReady,
                     "review" => r.Item.State == TransferItemState.NeedsReview,
                     "missing" => r.Item.State == TransferItemState.NoMatch,
                     "other" => r.Item.State is TransferItemState.Excluded or TransferItemState.Unavailable,
                     _ => true
                 }))
            Rows.Add(r);
    }

    private async Task RestoreLastReportAsync()
    {
        if (State.SelectedPlaylist is not { } p || HasActiveJob) return;
        var recent = await _jobs.GetRecentAsync(30);
        var job = recent.FirstOrDefault(j => j.Type == JobType.Transfer && j.PlaylistId == p.Id);
        if (job is null) return;
        if (!job.IsTerminal && job.Status != JobStatus.Paused)
        {
            _jobId = job.Id;
            ActiveJob = new JobViewModel(job);
            return;
        }
        ShowReport(job);
    }

    [RelayCommand]
    private Task LoadSpotifyPlaylists() => LoadSpotifyPlaylistsAsync();

    private Task LoadSpotifyPlaylistsAsync() => RunAsync(async () =>
    {
        var me = await _spotify.GetCurrentUserAsync(CancellationToken.None);
        var all = await _spotify.GetMyPlaylistsAsync(CancellationToken.None);
        ExistingPlaylists.Clear();
        // Seules les playlists modifiables (propriétaire ou collaborative) sont proposées.
        foreach (var pl in all.Where(x => x.OwnerId == me.Id || x.Collaborative).OrderBy(x => x.Name)) ExistingPlaylists.Add(pl);
        SelectedExisting ??= ExistingPlaylists.FirstOrDefault();
        PlaylistsLoaded = true;
    }, "Chargement des playlists Spotify…");

    [RelayCommand]
    private void SetFilter(string key) => FilterKey = key;

    [RelayCommand]
    private void SelectAllReady()
    {
        foreach (var r in _all.Where(r => r.IsReady)) r.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in _all) r.IsChecked = false;
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private void ReviewPending() => _navigation.NavigateTo(PageKey.MatchReview);

    internal void Choose(TransferRowViewModel row) => _navigation.NavigateTo(PageKey.MatchReview, row.Track.Id);

    [RelayCommand]
    private Task Transfer() => RunAsync(async () =>
    {
        if (State.SelectedPlaylist is not { } p) return;
        var selected = _all.Where(r => r.IsChecked && r.IsReady).Select(r => r.Track).OrderBy(t => t.Position).ToList();
        if (selected.Count == 0) throw new ArgumentException("Aucun morceau prêt n'est sélectionné.");
        if (CreateNew && string.IsNullOrWhiteSpace(NewName)) throw new ArgumentException("Indiquez le nom de la nouvelle playlist.");
        if (!CreateNew && SelectedExisting is null) throw new ArgumentException("Choisissez une playlist Spotify existante.");

        var target = CreateNew ? $"nouvelle playlist « {NewName} » ({(IsPublic ? "publique" : "privée")})" : $"playlist existante « {SelectedExisting!.Name} »";
        var details = new List<string>
        {
            $"{selected.Count} morceau(x) seront ajoutés dans l'ordre d'origine, par lots de {ISpotifyClient.MaxItemsPerAddRequest}.",
            $"{selected.Count(t => t.MatchStatus == MatchStatus.AutoMatched)} correspondance(s) automatique(s) fiable(s), {selected.Count(t => t.MatchStatus == MatchStatus.Confirmed)} confirmée(s).",
        };
        if (Plan is { NeedsReview: > 0 } plan) details.Add($"{plan.NeedsReview} morceau(x) ambigu(s) non validé(s) ne seront pas envoyés.");
        if (!CreateNew && SkipExisting) details.Add("Les morceaux déjà présents dans la playlist seront ignorés.");
        if (!await _dialogs.ConfirmAsync("Confirmer le transfert vers Spotify", $"Destination : {target}.", "Transférer", details: details)) return;

        var request = new TransferRequest
        {
            PlaylistId = p.Id,
            ExistingPlaylistId = CreateNew ? null : SelectedExisting!.Id,
            ExistingPlaylistName = CreateNew ? null : SelectedExisting!.Name,
            NewPlaylistName = CreateNew ? NewName!.Trim() : null,
            NewPlaylistDescription = NewDescription,
            IsPublic = IsPublic,
            SkipExisting = SkipExisting,
            TrackIds = selected.Select(t => t.Id).ToList()
        };
        var job = await _queue.EnqueueAsync(JobType.Transfer, p.Id, $"Transfert · {p.Title} → Spotify", request);
        _jobId = job.Id;
        ActiveJob = new JobViewModel(job, pause: j => _queue.PauseAsync(j.Id), cancel: j => _queue.CancelAsync(j.Id));
        HasReport = false;
        OnSelectionChanged();
        Notifications.Success("Transfert lancé");
    });

    private void OnJobUpdated(object? sender, Job job)
    {
        if (job.Type != JobType.Transfer || (job.Id != _jobId && job.PlaylistId != State.SelectedPlaylist?.Id)) return;
        if (job.IsTerminal || job.Status == JobStatus.Paused)
        {
            ActiveJob = null;
            _jobId = null;
            ShowReport(job);
            OnSelectionChanged();
            if (job.Status == JobStatus.Completed) Notifications.Success("Transfert terminé", job.Message);
            else if (job.Status == JobStatus.Paused) Notifications.Warning("Transfert en pause", job.Message);
            else Notifications.Warning("Transfert incomplet", job.Message);
        }
        else
        {
            _jobId = job.Id;
            if (ActiveJob?.Id == job.Id) ActiveJob.Update(job);
            else ActiveJob = new JobViewModel(job, pause: j => _queue.PauseAsync(j.Id), cancel: j => _queue.CancelAsync(j.Id));
        }
    }

    private void ShowReport(Job job)
    {
        var state = JsonDefaults.Deserialize<TransferState>(job.State);
        ReportAdded.Clear();
        ReportSkipped.Clear();
        ReportFailed.Clear();
        if (state is null)
        {
            HasReport = false;
            return;
        }
        foreach (var l in state.Added) ReportAdded.Add(l);
        foreach (var l in state.Skipped) ReportSkipped.Add(l);
        foreach (var l in state.Failed) ReportFailed.Add(l);
        ReportTitle = $"{BadgeInfo.ForJob(job.Status).Text} · {job.Message}";
        ReportUrl = state.TargetPlaylistUrl;
        HasReport = true;
        CanResumeReport = job.CanResume && job.Status != JobStatus.Completed;
        _reportJobId = job.Id;
    }

    private Guid? _reportJobId;

    [ObservableProperty] private bool _canResumeReport;

    [RelayCommand]
    private Task ResumeTransfer() => RunAsync(async () =>
    {
        if (_reportJobId is not { } id) return;
        _jobId = id;
        await _queue.ResumeAsync(id);
        HasReport = false;
    });

    [RelayCommand]
    private void OpenReportPlaylist()
    {
        if (ReportUrl is not null) _dialogs.OpenUrl(ReportUrl);
    }
}
