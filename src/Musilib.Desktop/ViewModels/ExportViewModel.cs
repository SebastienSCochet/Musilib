using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Jobs;
using Musilib.Application.Services;
using Musilib.Desktop.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Desktop.ViewModels;

public sealed partial class ExportRowViewModel : ObservableObject
{
    private readonly ExportViewModel _owner;

    public ExportRowViewModel(Track track, ExportViewModel owner)
    {
        Track = track;
        _owner = owner;
    }

    public Track Track { get; private set; }

    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private BadgeInfo _action = new("—", "muted");
    [ObservableProperty] private string? _target;

    public int Number => Track.Position + 1;
    public string Title => Track.Metadata.Title;
    public string Artists => Track.Metadata.ArtistsDisplay;
    public string? Cover => Track.Metadata.CoverLocalPath ?? Track.Metadata.CoverUrl ?? Track.SourceThumbnailUrl;
    public string FallbackText => $"{Track.Metadata.PrimaryArtist} {Track.Metadata.Title}";
    public bool HasLocalFile => Track.HasLocalFile;
    public bool HasNoLocalFile => !Track.HasLocalFile;
    public string LocalFile => Track.HasLocalFile ? Path.GetFileName(Track.LocalFilePath!) : "Aucun fichier associé";
    public string? LocalFilePath => Track.LocalFilePath;

    public void Update(Track track)
    {
        Track = track;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand] private Task Associate() => _owner.AssociateAsync(this);
    [RelayCommand] private Task Unlink() => _owner.UnlinkAsync(this);
}

/// <summary>
/// Export local de fichiers audio possédés par l'utilisateur, organisés et taggués.
/// Aucun audio n'est extrait de YouTube ou Spotify.
/// </summary>
public sealed partial class ExportViewModel : PageViewModel
{
    private readonly ITrackRepository _tracks;
    private readonly ExportService _export;
    private readonly LocalLibraryService _library;
    private readonly TrackEditingService _editing;
    private readonly IAudioMetadataService _audio;
    private readonly ISettingsService _settings;
    private readonly IJobQueue _queue;
    private readonly DialogService _dialogs;
    private readonly AppMode _mode;
    private readonly IAppPaths _paths;
    private Guid? _jobId;

    public ExportViewModel(ITrackRepository tracks, ExportService export, LocalLibraryService library, TrackEditingService editing,
        IAudioMetadataService audio, ISettingsService settings, IJobQueue queue, AppState state, DialogService dialogs, AppMode mode,
        IAppPaths paths, NotificationService notifications, ILogger<ExportViewModel> logger)
        : base(notifications, logger)
    {
        _tracks = tracks;
        _export = export;
        _library = library;
        _editing = editing;
        _audio = audio;
        _settings = settings;
        _queue = queue;
        State = state;
        _dialogs = dialogs;
        _mode = mode;
        _paths = paths;
        state.JobUpdated += OnJobUpdated;
    }

    public override string Title => "Export local";
    public override bool UsesPlaylist => true;
    public AppState State { get; }
    public ObservableCollection<ExportRowViewModel> Rows { get; } = new();

    public IReadOnlyList<OptionItem<ExportOrganization>> Organizations { get; } =
    [
        new(ExportOrganization.None, "Tous les fichiers dans le dossier"),
        new(ExportOrganization.Artist, "Artiste\\"),
        new(ExportOrganization.ArtistAlbum, "Artiste\\Album\\"),
        new(ExportOrganization.Album, "Album\\")
    ];

    public IReadOnlyList<OptionItem<ConflictResolution>> Conflicts { get; } =
    [
        new(ConflictResolution.Skip, "Ignorer le morceau"),
        new(ConflictResolution.KeepBoth, "Conserver les deux (renommer)"),
        new(ConflictResolution.Overwrite, "Remplacer le fichier existant")
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

    [ObservableProperty] private string? _destination;
    [ObservableProperty] private OptionItem<ExportOrganization>? _organization;
    [ObservableProperty] private OptionItem<ConflictResolution>? _conflict;
    [ObservableProperty] private OptionItem<string>? _format;
    [ObservableProperty] private bool _writeTags = true;
    [ObservableProperty] private bool _embedCover = true;
    [ObservableProperty] private bool _writeM3u = true;
    [ObservableProperty] private int _bitrate = 320;
    [ObservableProperty] private bool _planReady;
    [ObservableProperty] private string? _planSummary;
    [ObservableProperty] private JobViewModel? _activeJob;
    [ObservableProperty] private string? _lastResult;
    [ObservableProperty] private string? _lastM3u;
    [ObservableProperty] private string? _scanSummary;

    public bool HasPlaylist => State.SelectedPlaylist is not null;
    public bool HasNoPlaylist => State.SelectedPlaylist is null;
    public bool FfmpegAvailable => _export.ConverterAvailable;
    public bool FfmpegMissing => !_export.ConverterAvailable;
    public bool IsConverting => !string.IsNullOrEmpty(Format?.Value);
    public bool HasActiveJob => ActiveJob is not null;
    public bool HasLastResult => !string.IsNullOrEmpty(LastResult);
    public bool HasScanSummary => !string.IsNullOrEmpty(ScanSummary);
    public int LocalCount => Rows.Count(r => r.HasLocalFile && !r.Track.Excluded);
    public string LocalSummary => $"{LocalCount} / {Rows.Count} morceaux ont un fichier audio local associé";
    public bool IsDemo => _mode.IsDemo;

    partial void OnFormatChanged(OptionItem<string>? value) { OnPropertyChanged(nameof(IsConverting)); PlanReady = false; }
    partial void OnDestinationChanged(string? value) => PlanReady = false;
    partial void OnOrganizationChanged(OptionItem<ExportOrganization>? value) => PlanReady = false;
    partial void OnConflictChanged(OptionItem<ConflictResolution>? value) => PlanReady = false;
    partial void OnActiveJobChanged(JobViewModel? value) => OnPropertyChanged(nameof(HasActiveJob));
    partial void OnLastResultChanged(string? value) => OnPropertyChanged(nameof(HasLastResult));
    partial void OnScanSummaryChanged(string? value) => OnPropertyChanged(nameof(HasScanSummary));

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var e = _settings.Current.Export;
        Destination ??= string.IsNullOrWhiteSpace(e.DefaultFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Musilib")
            : e.DefaultFolder;
        Organization ??= Organizations.First(o => o.Value == e.Organization);
        Conflict ??= Conflicts.First(o => o.Value == e.ConflictResolution);
        Format ??= Formats.FirstOrDefault(f => f.Value == e.ConvertTo) ?? Formats[0];
        WriteTags = e.WriteTags;
        EmbedCover = e.EmbedCover;
        WriteM3u = e.WriteM3u;
        Bitrate = e.BitrateKbps;
        OnPropertyChanged(nameof(FfmpegAvailable));
        OnPropertyChanged(nameof(FfmpegMissing));
        await LoadAsync();
    }

    public override Task OnPlaylistChangedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        OnPropertyChanged(nameof(HasPlaylist));
        OnPropertyChanged(nameof(HasNoPlaylist));
        Rows.Clear();
        PlanReady = false;
        if (State.SelectedPlaylist is { } p)
        {
            foreach (var t in await _tracks.GetByPlaylistAsync(p.Id))
                Rows.Add(new ExportRowViewModel(t, this) { IsChecked = t.HasLocalFile && !t.Excluded });
        }
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(LocalCount));
        OnPropertyChanged(nameof(LocalSummary));
    }

    private ExportOptions BuildOptions() => new()
    {
        PlaylistId = State.SelectedPlaylist!.Id,
        TrackIds = Rows.Where(r => r.IsChecked).Select(r => r.Track.Id).ToList(),
        DestinationFolder = Destination ?? string.Empty,
        Organization = Organization?.Value ?? ExportOrganization.ArtistAlbum,
        Conflict = Conflict?.Value ?? ConflictResolution.Skip,
        WriteTags = WriteTags,
        EmbedCover = EmbedCover,
        WriteM3u = WriteM3u,
        ConvertTo = Format?.Value ?? string.Empty,
        BitrateKbps = Bitrate,
        PlaylistName = State.SelectedPlaylist!.Title
    };

    [RelayCommand]
    private Task PickDestination() => RunAsync(async () =>
    {
        var folder = await _dialogs.PickFolderAsync("Dossier de destination", Destination);
        if (folder is not null) Destination = folder;
    });

    [RelayCommand]
    private Task ScanFolder() => RunAsync(async () =>
    {
        if (State.SelectedPlaylist is null) return;
        var start = _mode.IsDemo ? Path.Combine(_paths.DataDirectory, "demo-music") : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var folder = await _dialogs.PickFolderAsync("Dossier contenant vos fichiers audio", start);
        if (folder is null) return;
        var result = await _library.ScanAndAssociateAsync(folder, State.SelectedPlaylist.Id, replaceExisting: false, null, CancellationToken.None);
        ScanSummary = $"{result.FilesScanned} fichiers analysés · {result.Associated} nouvelles associations · {result.AlreadyAssociated} déjà associés · {result.Unmatched.Count} fichiers sans correspondance";
        await LoadAsync();
        Notifications.Success("Analyse du dossier terminée", $"{result.Associated} fichier(s) associé(s).");
    }, "Analyse des fichiers…");

    internal Task AssociateAsync(ExportRowViewModel row) => RunAsync(async () =>
    {
        var patterns = _audio.SupportedExtensions.Select(e => "*" + e).ToList();
        var file = await _dialogs.PickFileAsync($"Fichier audio pour « {row.Title} »", "Fichiers audio", patterns);
        if (file is null) return;
        await _editing.AssociateLocalFileAsync(row.Track, file, CancellationToken.None);
        row.Update(row.Track);
        row.IsChecked = true;
        PlanReady = false;
        RaiseCounts();
    });

    internal Task UnlinkAsync(ExportRowViewModel row) => RunAsync(async () =>
    {
        await _editing.AssociateLocalFileAsync(row.Track, null, CancellationToken.None);
        row.Update(row.Track);
        row.IsChecked = false;
        PlanReady = false;
        RaiseCounts();
    });

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Rows) r.IsChecked = r.HasLocalFile && !r.Track.Excluded;
        PlanReady = false;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Rows) r.IsChecked = false;
        PlanReady = false;
    }

    [RelayCommand]
    private Task Preview() => RunAsync(async () =>
    {
        if (State.SelectedPlaylist is null) return;
        if (string.IsNullOrWhiteSpace(Destination)) throw new ArgumentException("Choisissez un dossier de destination.");
        var options = BuildOptions();
        var selected = Rows.Where(r => r.IsChecked).Select(r => r.Track).ToList();
        var plan = await _export.PlanAsync(selected, options, CancellationToken.None);
        var byId = plan.ToDictionary(p => p.Track.Id);
        foreach (var row in Rows)
        {
            if (!byId.TryGetValue(row.Track.Id, out var item))
            {
                row.Action = new BadgeInfo("Non sélectionné", "muted");
                row.Target = null;
                continue;
            }
            row.Action = item.Action switch
            {
                ExportItemAction.Copy => new BadgeInfo("Copie", "success"),
                ExportItemAction.Convert => new BadgeInfo("Conversion", "info"),
                ExportItemAction.Duplicate => new BadgeInfo("Doublon", "muted"),
                ExportItemAction.Conflict => new BadgeInfo("Conflit", "warning"),
                ExportItemAction.SourceMissing => new BadgeInfo("Source introuvable", "danger"),
                ExportItemAction.Excluded => new BadgeInfo("Exclu", "muted"),
                _ => new BadgeInfo("Aucun fichier", "muted")
            };
            row.Target = item.TargetPath is null ? item.Note : $"{Path.GetRelativePath(options.DestinationFolder, item.TargetPath)} — {item.Note}";
        }
        var ready = plan.Count(p => p.Action is ExportItemAction.Copy or ExportItemAction.Convert);
        var conflicts = plan.Count(p => p.Action == ExportItemAction.Conflict);
        var duplicates = plan.Count(p => p.Action == ExportItemAction.Duplicate);
        PlanSummary = $"{ready} à exporter · {conflicts} conflit(s) · {duplicates} doublon(s) · {plan.Count(p => p.Action is ExportItemAction.NoSource or ExportItemAction.SourceMissing)} sans fichier";
        PlanReady = true;
    }, "Préparation…");

    [RelayCommand]
    private Task Export() => RunAsync(async () =>
    {
        if (State.SelectedPlaylist is null) return;
        if (!PlanReady) await Preview();
        if (!PlanReady) return;
        var options = BuildOptions();
        if (options.TrackIds.Count == 0) throw new ArgumentException("Aucun morceau sélectionné.");
        if (!await _dialogs.ConfirmAsync("Lancer l'export ?",
                $"{options.TrackIds.Count} morceau(x) vers {options.DestinationFolder}. Les fichiers d'origine ne sont jamais modifiés.",
                "Exporter", details: PlanSummary is null ? null : [PlanSummary])) return;
        var job = await _queue.EnqueueAsync(JobType.Export, options.PlaylistId, $"Export · {State.SelectedPlaylist.Title}", options);
        _jobId = job.Id;
        ActiveJob = new JobViewModel(job);
        LastResult = null;
        Notifications.Success("Export lancé");
    });

    private void OnJobUpdated(object? sender, Job job)
    {
        if (job.Id != _jobId) return;
        if (ActiveJob is null) ActiveJob = new JobViewModel(job);
        else ActiveJob.Update(job);
        if (job.IsTerminal || job.Status == JobStatus.Paused)
        {
            LastResult = job.Message;
            var state = Application.Common.JsonDefaults.Deserialize<ExportJobState>(job.State);
            LastM3u = state?.M3uPath;
            ActiveJob = null;
            _jobId = null;
            PlanReady = false;
            if (job.Status is JobStatus.Completed) Notifications.Success("Export terminé", job.Message);
            else if (job.Status is JobStatus.PartiallyCompleted) Notifications.Warning("Export partiel", job.Message);
            else Notifications.Error("Export interrompu", job.Message);
        }
    }

    [RelayCommand]
    private void OpenDestination()
    {
        if (!string.IsNullOrWhiteSpace(Destination))
        {
            Directory.CreateDirectory(Destination);
            DialogService.OpenFolder(Destination);
        }
    }
}
