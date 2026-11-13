using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Jobs;
using Musilib.Application.Settings;
using Musilib.Desktop.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Infrastructure.Media;

namespace Musilib.Desktop.ViewModels;

/// <summary>File d'attente : progression globale et individuelle, pause, reprise, annulation.</summary>
public sealed partial class QueueViewModel : PageViewModel
{
    private readonly IJobQueue _queue;
    private readonly IJobRepository _jobs;
    private readonly ISettingsService _settings;
    private readonly DialogService _dialogs;

    public QueueViewModel(IJobQueue queue, IJobRepository jobs, ISettingsService settings, AppState state, DialogService dialogs,
        NotificationService notifications, ILogger<QueueViewModel> logger)
        : base(notifications, logger)
    {
        _queue = queue;
        _jobs = jobs;
        _settings = settings;
        _dialogs = dialogs;
        state.JobUpdated += OnJobUpdated;
    }

    public override string Title => "File d'attente";
    public ObservableCollection<JobViewModel> Active { get; } = new();
    public ObservableCollection<JobViewModel> Finished { get; } = new();

    [ObservableProperty] private double _globalProgress;
    [ObservableProperty] private string _globalText = "Aucun traitement en cours";

    public bool HasActive => Active.Count > 0;
    public bool HasNoActive => Active.Count == 0;
    public bool HasFinished => Finished.Count > 0;
    public string ConcurrencyText => $"{_settings.Current.Processing.MaxConcurrentJobs} traitement(s) simultané(s) · {_settings.Current.Processing.MaxConcurrentRequests} requête(s) parallèle(s) par traitement · reprise automatique {(_settings.Current.Processing.AutoResumeJobs ? "activée" : "désactivée")}";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        OnPropertyChanged(nameof(ConcurrencyText));
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var recent = await _jobs.GetRecentAsync(100);
        Active.Clear();
        Finished.Clear();
        foreach (var j in recent.OrderBy(j => j.CreatedAt))
        {
            if (!j.IsTerminal) Active.Add(Create(j));
        }
        foreach (var j in recent.Where(j => j.IsTerminal)) Finished.Add(Create(j));
        Recompute();
    }

    private JobViewModel Create(Job j) => new(j,
        pause: vm => RunAsync(() => _queue.PauseAsync(vm.Id)),
        resume: vm => RunAsync(() => _queue.ResumeAsync(vm.Id)),
        cancel: vm => RunAsync(async () =>
        {
            if (await _dialogs.ConfirmAsync("Annuler le traitement ?", "Les éléments déjà traités sont conservés. Vous pourrez reprendre le traitement plus tard.", "Annuler le traitement", danger: true))
                await _queue.CancelAsync(vm.Id);
        }),
        remove: vm => RunAsync(async () =>
        {
            await _queue.RemoveAsync(vm.Id);
            Active.Remove(vm);
            Finished.Remove(vm);
            Recompute();
        }));

    private void OnJobUpdated(object? sender, Job job)
    {
        var existing = Active.FirstOrDefault(j => j.Id == job.Id) ?? Finished.FirstOrDefault(j => j.Id == job.Id);
        if (existing is null)
        {
            existing = Create(job);
            if (job.IsTerminal) Finished.Insert(0, existing); else Active.Add(existing);
        }
        else
        {
            existing.Update(job);
            if (job.IsTerminal && Active.Remove(existing)) Finished.Insert(0, existing);
            else if (!job.IsTerminal && Finished.Remove(existing)) Active.Add(existing);
        }
        Recompute();
    }

    private void Recompute()
    {
        var total = Active.Sum(j => Math.Max(j.Job.Total, 0));
        var done = Active.Sum(j => Math.Min(j.Job.Processed, Math.Max(j.Job.Total, 0)));
        GlobalProgress = total == 0 ? 0 : 100.0 * done / total;
        var running = Active.Count(j => j.IsRunning);
        GlobalText = Active.Count == 0 ? "Aucun traitement en cours"
            : $"{running} en cours · {Active.Count(j => j.Job.Status == JobStatus.Queued)} en attente · {Active.Count(j => j.Job.Status == JobStatus.Paused)} en pause · {done}/{total} éléments";
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasNoActive));
        OnPropertyChanged(nameof(HasFinished));
    }

    [RelayCommand]
    private Task ClearFinished() => RunAsync(async () =>
    {
        var n = await _queue.ClearFinishedAsync();
        Finished.Clear();
        Recompute();
        Notifications.Show($"{n} traitement(s) retiré(s) de la liste");
    });

    [RelayCommand]
    private Task ResumeAll() => RunAsync(async () =>
    {
        foreach (var j in Active.Where(j => j.CanResume).ToList()) await _queue.ResumeAsync(j.Id);
    });

    [RelayCommand]
    private Task PauseAll() => RunAsync(async () =>
    {
        foreach (var j in Active.Where(j => j.CanPause).ToList()) await _queue.PauseAsync(j.Id);
    });
}

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly IHistoryRepository _history;
    private readonly DialogService _dialogs;

    public HistoryViewModel(IHistoryRepository history, DialogService dialogs, AppState state, NotificationService notifications,
        ILogger<HistoryViewModel> logger)
        : base(notifications, logger)
    {
        _history = history;
        _dialogs = dialogs;
        state.JobUpdated += (_, job) =>
        {
            if (_active && job.IsTerminal) _ = LoadAsync();
        };
    }

    private bool _active;

    public override string Title => "Historique";
    public ObservableCollection<HistoryLine> Entries { get; } = new();

    public IReadOnlyList<OptionItem<HistoryKind?>> Kinds { get; } =
    [
        new(null, "Toutes les activités"),
        new(HistoryKind.Import, "Imports"),
        new(HistoryKind.Match, "Correspondances"),
        new(HistoryKind.Transfer, "Transferts"),
        new(HistoryKind.Export, "Exports"),
        new(HistoryKind.Edit, "Éditions"),
        new(HistoryKind.Connection, "Connexions"),
        new(HistoryKind.System, "Système")
    ];

    [ObservableProperty] private OptionItem<HistoryKind?>? _selectedKind;

    public bool IsEmpty => Entries.Count == 0;

    partial void OnSelectedKindChanged(OptionItem<HistoryKind?>? value) => _ = LoadAsync();

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _active = true;
        SelectedKind ??= Kinds[0];
        await LoadAsync();
    }

    public override void OnNavigatedFrom() => _active = false;

    private async Task LoadAsync()
    {
        var list = await _history.GetRecentAsync(500, SelectedKind?.Value);
        Entries.Clear();
        foreach (var e in list) Entries.Add(new HistoryLine(e));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private Task Clear() => RunAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Effacer l'historique ?", "Toutes les entrées de l'historique seront supprimées. Les playlists et correspondances ne sont pas affectées.", "Effacer", danger: true)) return;
        await _history.ClearAsync();
        await LoadAsync();
    }, successTitle: "Historique effacé");

    [RelayCommand]
    private Task Refresh() => LoadAsync();
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly ISearchCache _cache;
    private readonly IAppPaths _paths;
    private readonly IDatabaseInitializer _database;
    private readonly DialogService _dialogs;
    private readonly AppMode _mode;
    private readonly FfmpegAudioConverter? _ffmpeg;

    public SettingsViewModel(ISettingsService settings, ISearchCache cache, IAppPaths paths, IDatabaseInitializer database, DialogService dialogs,
        AppMode mode, IAudioConverter converter, NotificationService notifications, ILogger<SettingsViewModel> logger)
        : base(notifications, logger)
    {
        _settings = settings;
        _cache = cache;
        _paths = paths;
        _database = database;
        _dialogs = dialogs;
        _mode = mode;
        _ffmpeg = converter as FfmpegAudioConverter;
    }

    public override string Title => "Paramètres";

    public IReadOnlyList<OptionItem<ExportOrganization>> Organizations { get; } =
    [
        new(ExportOrganization.None, "Aucune"),
        new(ExportOrganization.Artist, "Par artiste"),
        new(ExportOrganization.ArtistAlbum, "Par artiste puis album"),
        new(ExportOrganization.Album, "Par album")
    ];

    public IReadOnlyList<OptionItem<ConflictResolution>> Conflicts { get; } =
    [
        new(ConflictResolution.Skip, "Ignorer"),
        new(ConflictResolution.KeepBoth, "Conserver les deux"),
        new(ConflictResolution.Overwrite, "Remplacer")
    ];

    public IReadOnlyList<OptionItem<string>> Formats { get; } =
    [
        new(string.Empty, "Conserver le format d'origine"),
        new("mp3", "MP3"),
        new("flac", "FLAC"),
        new("m4a", "M4A (AAC)"),
        new("ogg", "OGG Vorbis"),
        new("opus", "Opus")
    ];

    public IReadOnlyList<OptionItem<string>> Themes { get; } = [new("Dark", "Sombre"), new("Light", "Clair")];

    [ObservableProperty] private bool _demoMode;
    [ObservableProperty] private OptionItem<string>? _theme;
    [ObservableProperty] private decimal _maxConcurrentJobs;
    [ObservableProperty] private decimal _maxConcurrentRequests;
    [ObservableProperty] private decimal _maxRetries;
    [ObservableProperty] private decimal _maxRetryAfter;
    [ObservableProperty] private bool _autoResume;
    [ObservableProperty] private decimal _cacheDays;
    [ObservableProperty] private double _autoAccept;
    [ObservableProperty] private double _minimum;
    [ObservableProperty] private double _gap;
    [ObservableProperty] private decimal _searchLimit;
    [ObservableProperty] private bool _applySpotifyMetadata;
    [ObservableProperty] private string? _market;
    [ObservableProperty] private string? _defaultFolder;
    [ObservableProperty] private OptionItem<ExportOrganization>? _organization;
    [ObservableProperty] private OptionItem<ConflictResolution>? _conflict;
    [ObservableProperty] private OptionItem<string>? _format;
    [ObservableProperty] private decimal _bitrate;
    [ObservableProperty] private bool _writeTags;
    [ObservableProperty] private bool _embedCover;
    [ObservableProperty] private bool _writeM3u;
    [ObservableProperty] private string? _ffmpegPath;
    [ObservableProperty] private int _cacheCount;

    public string AutoAcceptText => $"{AutoAccept:P0}";
    public string MinimumText => $"{Minimum:P0}";
    public string GapText => $"{Gap:P0}";
    public string DataPath => _paths.DataDirectory;
    public string DatabasePath => _database.DatabasePath;
    public string LogPath => _paths.LogDirectory;
    public string SettingsPath => _settings.SettingsPath;
    public bool IsDemoRunning => _mode.IsDemo;
    public string FfmpegStatus => _ffmpeg?.ResolveExecutable() is { } p ? $"FFmpeg détecté : {p}" : "FFmpeg non détecté (la conversion sera désactivée)";
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    partial void OnAutoAcceptChanged(double value) => OnPropertyChanged(nameof(AutoAcceptText));
    partial void OnMinimumChanged(double value) => OnPropertyChanged(nameof(MinimumText));
    partial void OnGapChanged(double value) => OnPropertyChanged(nameof(GapText));
    partial void OnThemeChanged(OptionItem<string>? value)
    {
        if (value is not null) App.ApplyTheme(value.Value);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var s = _settings.Current;
        DemoMode = s.General.DemoMode;
        Theme = Themes.FirstOrDefault(t => t.Value == s.General.Theme) ?? Themes[0];
        MaxConcurrentJobs = s.Processing.MaxConcurrentJobs;
        MaxConcurrentRequests = s.Processing.MaxConcurrentRequests;
        MaxRetries = s.Processing.MaxRetries;
        MaxRetryAfter = s.Processing.MaxRetryAfterSeconds;
        AutoResume = s.Processing.AutoResumeJobs;
        CacheDays = s.Processing.SearchCacheDays;
        AutoAccept = s.Matching.AutoAcceptThreshold;
        Minimum = s.Matching.MinimumThreshold;
        Gap = s.Matching.AmbiguityGap;
        SearchLimit = s.Matching.SearchLimit;
        ApplySpotifyMetadata = s.Matching.ApplySpotifyMetadata;
        Market = s.Spotify.Market;
        DefaultFolder = s.Export.DefaultFolder;
        Organization = Organizations.First(o => o.Value == s.Export.Organization);
        Conflict = Conflicts.First(o => o.Value == s.Export.ConflictResolution);
        Format = Formats.FirstOrDefault(f => f.Value == s.Export.ConvertTo) ?? Formats[0];
        Bitrate = s.Export.BitrateKbps;
        WriteTags = s.Export.WriteTags;
        EmbedCover = s.Export.EmbedCover;
        WriteM3u = s.Export.WriteM3u;
        FfmpegPath = s.Export.FfmpegPath;
        CacheCount = await _cache.CountAsync();
        OnPropertyChanged(nameof(FfmpegStatus));
    }

    [RelayCommand]
    private Task Save() => RunAsync(async () =>
    {
        var s = _settings.Current.Clone();
        var demoChanged = s.General.DemoMode != DemoMode;
        s.General.DemoMode = DemoMode;
        s.General.Theme = Theme?.Value ?? "Dark";
        s.Processing.MaxConcurrentJobs = (int)MaxConcurrentJobs;
        s.Processing.MaxConcurrentRequests = (int)MaxConcurrentRequests;
        s.Processing.MaxRetries = (int)MaxRetries;
        s.Processing.MaxRetryAfterSeconds = (int)MaxRetryAfter;
        s.Processing.AutoResumeJobs = AutoResume;
        s.Processing.SearchCacheDays = (int)CacheDays;
        s.Matching.AutoAcceptThreshold = Math.Round(AutoAccept, 2);
        s.Matching.MinimumThreshold = Math.Round(Minimum, 2);
        s.Matching.AmbiguityGap = Math.Round(Gap, 2);
        s.Matching.SearchLimit = (int)SearchLimit;
        s.Matching.ApplySpotifyMetadata = ApplySpotifyMetadata;
        s.Spotify.Market = string.IsNullOrWhiteSpace(Market) ? "from_token" : Market.Trim();
        s.Export.DefaultFolder = DefaultFolder;
        s.Export.Organization = Organization?.Value ?? ExportOrganization.ArtistAlbum;
        s.Export.ConflictResolution = Conflict?.Value ?? ConflictResolution.Skip;
        s.Export.ConvertTo = Format?.Value ?? string.Empty;
        s.Export.BitrateKbps = (int)Bitrate;
        s.Export.WriteTags = WriteTags;
        s.Export.EmbedCover = EmbedCover;
        s.Export.WriteM3u = WriteM3u;
        s.Export.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim();
        await _settings.SaveAsync(s);
        OnPropertyChanged(nameof(FfmpegStatus));
        Notifications.Success("Paramètres enregistrés");

        if (demoChanged && await _dialogs.ConfirmAsync("Redémarrer l'application ?",
                DemoMode ? "Le mode démonstration utilise des données fictives et une base séparée. Un redémarrage est nécessaire."
                         : "Le mode normal utilise vos comptes et votre base réelle. Un redémarrage est nécessaire.",
                "Redémarrer maintenant"))
        {
            App.RequestRestart();
        }
    });

    [RelayCommand]
    private Task ClearCache() => RunAsync(async () =>
    {
        await _cache.ClearAsync();
        CacheCount = 0;
    }, successTitle: "Cache des recherches vidé");

    [RelayCommand]
    private Task PickFolder() => RunAsync(async () =>
    {
        var f = await _dialogs.PickFolderAsync("Dossier d'export par défaut", DefaultFolder);
        if (f is not null) DefaultFolder = f;
    });

    [RelayCommand]
    private Task PickFfmpeg() => RunAsync(async () =>
    {
        var f = await _dialogs.PickFileAsync("Localiser ffmpeg", "FFmpeg", OperatingSystem.IsWindows() ? ["ffmpeg.exe"] : ["ffmpeg"]);
        if (f is not null) FfmpegPath = f;
    });

    [RelayCommand]
    private void ResetMatching()
    {
        var d = new MatchingSettings();
        AutoAccept = d.AutoAcceptThreshold;
        Minimum = d.MinimumThreshold;
        Gap = d.AmbiguityGap;
        SearchLimit = d.SearchLimit;
    }

    [RelayCommand] private void OpenData() => DialogService.OpenFolder(_paths.DataDirectory);
    [RelayCommand] private void OpenLogs() => DialogService.OpenFolder(_paths.LogDirectory);
}
