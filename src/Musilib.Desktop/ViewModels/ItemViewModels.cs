using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musilib.Desktop.Converters;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Desktop.ViewModels;

/// <summary>Présentation d'un état sous forme de badge (texte + classe de couleur).</summary>
public sealed record BadgeInfo(string Text, string Kind)
{
    public bool IsSuccess => Kind == "success";
    public bool IsWarning => Kind == "warning";
    public bool IsDanger => Kind == "danger";
    public bool IsInfo => Kind == "info";
    public bool IsAccent => Kind == "accent";
    public bool IsPurple => Kind == "purple";

    public static BadgeInfo ForTrack(Track t)
    {
        if (t.Excluded) return new("Exclu", "muted");
        return t.MatchStatus switch
        {
            MatchStatus.Confirmed => new("Confirmé", "success"),
            MatchStatus.AutoMatched => new("Fiable", "accent"),
            MatchStatus.Ambiguous => new("À vérifier", "warning"),
            MatchStatus.NoMatch when !t.IsAvailable => new(t.RemovedFromSource ? "Retiré" : "Indisponible", "muted"),
            MatchStatus.NoMatch => new("Introuvable", "danger"),
            MatchStatus.Error => new("Erreur", "danger"),
            _ => t.IsAvailable ? new("En attente", "info") : new("Indisponible", "muted")
        };
    }

    public static BadgeInfo ForJob(JobStatus s) => s switch
    {
        JobStatus.Queued => new("En file", "info"),
        JobStatus.Running => new("En cours", "accent"),
        JobStatus.Paused => new("En pause", "warning"),
        JobStatus.Completed => new("Terminé", "success"),
        JobStatus.PartiallyCompleted => new("Partiel", "warning"),
        JobStatus.Failed => new("Échec", "danger"),
        _ => new("Annulé", "muted")
    };
}

public sealed partial class TrackRowViewModel : ObservableObject
{
    private readonly Action<TrackRowViewModel>? _compare;
    private readonly Action<TrackRowViewModel>? _edit;
    private readonly Func<TrackRowViewModel, Task>? _toggleExclude;

    public TrackRowViewModel(Track track, Action<TrackRowViewModel>? compare = null, Action<TrackRowViewModel>? edit = null,
        Func<TrackRowViewModel, Task>? toggleExclude = null)
    {
        Track = track;
        _compare = compare;
        _edit = edit;
        _toggleExclude = toggleExclude;
    }

    public Track Track { get; private set; }

    [ObservableProperty]
    private bool _isChecked = true;

    public int Number => Track.Position + 1;
    public string Title => string.IsNullOrWhiteSpace(Track.Metadata.Title) ? Track.SourceTitle : Track.Metadata.Title;
    public string Artists => Track.Metadata.Artists.Count == 0 ? "Artiste inconnu" : Track.Metadata.ArtistsDisplay;
    public string Album => Track.Metadata.Album ?? "—";
    public string SourceTitle => Track.SourceTitle;
    public string SourceLine => string.IsNullOrWhiteSpace(Track.SourceChannel) ? Track.SourceTitle : $"{Track.SourceTitle} · {Track.SourceChannel}";
    public string Duration => Formatters.Duration(Track.Metadata.DurationMs ?? Track.SourceDurationMs);
    public string? Cover => Track.Metadata.CoverLocalPath ?? Track.Metadata.CoverUrl ?? Track.SourceThumbnailUrl;
    public string FallbackText => $"{Track.Metadata.PrimaryArtist} {Title}";
    public BadgeInfo Badge => BadgeInfo.ForTrack(Track);
    public double ScorePercent => (Track.MatchScore ?? 0) * 100;
    public string ScoreText => Track.MatchScore is { } s ? s.ToString("P0", CultureInfo.GetCultureInfo("fr-FR")) : "—";
    public bool HasScore => Track.MatchScore is not null;
    public bool IsExcluded => Track.Excluded;
    public bool HasLocalFile => Track.HasLocalFile;
    public string LocalFileName => Track.HasLocalFile ? Path.GetFileName(Track.LocalFilePath!) : "Aucun fichier";
    public bool IsEdited => Track.MetadataEdited;
    public string? Error => Track.LastError;
    public bool HasError => !string.IsNullOrWhiteSpace(Track.LastError);
    public string ExcludeLabel => Track.Excluded ? "Inclure" : "Exclure";

    [RelayCommand]
    private void Compare() => _compare?.Invoke(this);

    [RelayCommand]
    private void Edit() => _edit?.Invoke(this);

    [RelayCommand]
    private async Task ToggleExclude()
    {
        if (_toggleExclude is not null) await _toggleExclude(this);
    }

    public void Update(Track track)
    {
        Track = track;
        OnPropertyChanged(string.Empty);
    }
}

public sealed partial class CandidateViewModel : ObservableObject
{
    private readonly Func<CandidateViewModel, Task>? _select;
    private readonly Func<CandidateViewModel, Task>? _play;

    public CandidateViewModel(SpotifyCandidate candidate, long? sourceDurationMs, bool isBest, bool isCurrent,
        Func<CandidateViewModel, Task>? select, Func<CandidateViewModel, Task>? play)
    {
        Candidate = candidate;
        SourceDurationMs = sourceDurationMs;
        IsBest = isBest;
        _isCurrent = isCurrent;
        _select = select;
        _play = play;
    }

    public SpotifyCandidate Candidate { get; }
    public long? SourceDurationMs { get; }
    public bool IsBest { get; }

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isPlaying;

    public string Title => Candidate.Title;
    public string Artists => Candidate.ArtistsDisplay;
    public string Album => Candidate.Album ?? "—";
    public string Year => Candidate.ReleaseDate is { Length: >= 4 } d ? d[..4] : "—";
    public string ReleaseDate => Candidate.ReleaseDate ?? "—";
    public string Isrc => Candidate.Isrc ?? "—";
    public string Duration => Formatters.Duration(Candidate.DurationMs);
    public string? Cover => Candidate.CoverUrl;
    public string FallbackText => $"{Candidate.Artists.FirstOrDefault()} {Candidate.Album}";
    public bool HasPreview => !string.IsNullOrWhiteSpace(Candidate.PreviewUrl);
    public bool IsExplicit => Candidate.Explicit;
    public double Score => Candidate.Score * 100;
    public string ScoreText => Candidate.Score.ToString("P0", CultureInfo.GetCultureInfo("fr-FR"));
    public double TitleScore => Candidate.TitleScore * 100;
    public double ArtistScore => Candidate.ArtistScore * 100;
    public double DurationScore => Candidate.DurationScore * 100;
    public bool IsHighScore => Candidate.Score >= 0.85;
    public bool IsMediumScore => Candidate.Score is >= 0.6 and < 0.85;
    public bool IsLowScore => Candidate.Score < 0.6;

    public string DurationDiff
    {
        get
        {
            if (SourceDurationMs is null or <= 0 || Candidate.DurationMs <= 0) return string.Empty;
            var diff = (Candidate.DurationMs - SourceDurationMs.Value) / 1000.0;
            if (Math.Abs(diff) < 1) return "durée identique";
            return (diff > 0 ? "+" : "−") + Math.Abs(diff).ToString("0", CultureInfo.InvariantCulture) + " s";
        }
    }

    [RelayCommand]
    private async Task Select()
    {
        if (_select is not null) await _select(this);
    }

    [RelayCommand]
    private async Task Play()
    {
        if (_play is not null) await _play(this);
    }
}

public sealed partial class JobViewModel : ObservableObject
{
    private readonly Func<JobViewModel, Task>? _pause;
    private readonly Func<JobViewModel, Task>? _resume;
    private readonly Func<JobViewModel, Task>? _cancel;
    private readonly Func<JobViewModel, Task>? _remove;

    public JobViewModel(Job job, Func<JobViewModel, Task>? pause = null, Func<JobViewModel, Task>? resume = null,
        Func<JobViewModel, Task>? cancel = null, Func<JobViewModel, Task>? remove = null)
    {
        Job = job;
        _pause = pause;
        _resume = resume;
        _cancel = cancel;
        _remove = remove;
    }

    public Job Job { get; private set; }
    public Guid Id => Job.Id;
    public string Title => Job.Title;
    public string TypeText => Job.Type switch
    {
        JobType.Import => "Import YouTube",
        JobType.Match => "Recherche Spotify",
        JobType.Export => "Export local",
        _ => "Transfert Spotify"
    };
    public string IconKey => Job.Type switch
    {
        JobType.Import => "Icon.YouTube",
        JobType.Match => "Icon.Compare",
        JobType.Export => "Icon.Download",
        _ => "Icon.Send"
    };
    public BadgeInfo Badge => BadgeInfo.ForJob(Job.Status);
    public double Progress => Job.Progress * 100;
    public bool IsIndeterminate => Job.Status == JobStatus.Running && Job.Total == 0;
    public string ProgressText => Job.Total > 0 ? $"{Job.Processed} / {Job.Total}" : Job.Status == JobStatus.Running ? "Préparation…" : string.Empty;
    public string? FailedText => Job.Failed > 0 ? $"{Job.Failed} erreur(s)" : null;
    public bool HasFailed => Job.Failed > 0;
    public string? CurrentItem => Job.CurrentItem;
    public bool HasCurrentItem => !string.IsNullOrWhiteSpace(Job.CurrentItem) && Job.Status == JobStatus.Running;
    public string? Message => Job.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Job.Message);
    public string CreatedText => Formatters.Relative(Job.CreatedAt);
    public string TimesText => Job.CompletedAt is { } done
        ? $"Terminé {Formatters.Relative(done)}" + (Job.StartedAt is { } s ? $" · durée {FormatSpan(done - s)}" : string.Empty)
        : Job.StartedAt is { } st ? $"Démarré {Formatters.Relative(st)}" : $"Créé {Formatters.Relative(Job.CreatedAt)}";
    public bool IsRunning => Job.Status == JobStatus.Running;
    public bool IsActive => Job.Status is JobStatus.Running or JobStatus.Queued;
    public bool CanPause => Job.Status is JobStatus.Running or JobStatus.Queued;
    public bool CanResume => Job.CanResume;
    public bool CanCancel => !Job.IsTerminal;

    public void Update(Job job)
    {
        Job = job;
        OnPropertyChanged(string.Empty);
    }

    private static string FormatSpan(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{Math.Max(0, t.TotalSeconds):0} s";

    [RelayCommand] private Task Pause() => _pause?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task Resume() => _resume?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task Cancel() => _cancel?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task Remove() => _remove?.Invoke(this) ?? Task.CompletedTask;
}

public sealed partial class NavItemViewModel : ObservableObject
{
    public NavItemViewModel(Services.PageKey key, string title, string iconKey, Action<NavItemViewModel> navigate)
    {
        Key = key;
        Title = title;
        IconKey = iconKey;
        NavigateCommand = new RelayCommand(() => navigate(this));
    }

    public Services.PageKey Key { get; }
    public string Title { get; }
    public string IconKey { get; }
    public IRelayCommand NavigateCommand { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private string? _badgeText;

    public bool HasBadge => !string.IsNullOrEmpty(BadgeText);
}

public sealed record NavSection(string Title, IReadOnlyList<NavItemViewModel> Items);

public sealed record OptionItem<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
