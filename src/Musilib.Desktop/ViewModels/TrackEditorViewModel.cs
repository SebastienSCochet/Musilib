using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>Édition des métadonnées (titre, artistes, album, date, pochette...) et lecture audio.</summary>
public sealed partial class TrackEditorViewModel : PageViewModel
{
    private readonly ITrackRepository _tracks;
    private readonly TrackEditingService _editing;
    private readonly MatchingService _matching;
    private readonly ISpotifyClient _spotify;
    private readonly IAudioMetadataService _audio;
    private readonly DialogService _dialogs;
    private readonly NavigationService _navigation;
    private readonly List<TrackRowViewModel> _all = new();
    private Guid? _pendingSelection;
    private bool _loadingForm;

    public TrackEditorViewModel(ITrackRepository tracks, TrackEditingService editing, MatchingService matching, ISpotifyClient spotify,
        IAudioMetadataService audio, AppState state, PlayerViewModel player, DialogService dialogs, NavigationService navigation,
        NotificationService notifications, ILogger<TrackEditorViewModel> logger)
        : base(notifications, logger)
    {
        _tracks = tracks;
        _editing = editing;
        _matching = matching;
        _spotify = spotify;
        _audio = audio;
        State = state;
        Player = player;
        _dialogs = dialogs;
        _navigation = navigation;
    }

    public override string Title => "Métadonnées & lecture";
    public override bool UsesPlaylist => true;
    public AppState State { get; }
    public PlayerViewModel Player { get; }
    public ObservableCollection<TrackRowViewModel> Tracks { get; } = new();

    [ObservableProperty] private TrackRowViewModel? _selected;
    [ObservableProperty] private string? _search;

    // Formulaire
    [ObservableProperty] private string? _editTitle;
    [ObservableProperty] private string? _editArtists;
    [ObservableProperty] private string? _editAlbum;
    [ObservableProperty] private string? _editAlbumArtists;
    [ObservableProperty] private string? _editReleaseDate;
    [ObservableProperty] private string? _editGenre;
    [ObservableProperty] private string? _editTrackNumber;
    [ObservableProperty] private string? _editDiscNumber;
    [ObservableProperty] private string? _editIsrc;
    [ObservableProperty] private string? _editComment;
    [ObservableProperty] private string? _editCoverUrl;
    [ObservableProperty] private string? _editCoverLocalPath;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string? _validationError;

    public bool HasSelection => Selected is not null;
    public bool HasNoSelection => Selected is null;
    public string? CoverPreview => EditCoverLocalPath ?? EditCoverUrl ?? Selected?.Track.SourceThumbnailUrl;
    public string CoverFallback => $"{EditArtists} {EditTitle}";
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);
    public string OriginText => Selected?.Track.MetadataOrigin switch
    {
        MetadataOrigin.Manual => "Modifiées manuellement",
        MetadataOrigin.Spotify => "Issues de Spotify",
        _ => "Déduites du titre YouTube"
    };
    public bool HasPreview => !string.IsNullOrWhiteSpace(Selected?.Track.PreviewUrl);
    public bool HasLocalFile => Selected?.Track.HasLocalFile == true;
    public string LocalFile => Selected?.Track.LocalFilePath ?? "Aucun fichier audio associé";
    public bool HasSpotify => !string.IsNullOrEmpty(Selected?.Track.SpotifyTrackId);
    public string SpotifyText => Selected?.Track.HasSpotifyMatch == true ? $"Associé à Spotify ({Selected.ScoreText})" : "Aucune correspondance validée";
    public string SourceDuration => Formatters.Duration(Selected?.Track.SourceDurationMs);
    public string SourceTitle => Selected?.Track.SourceTitle ?? string.Empty;

    partial void OnSearchChanged(string? value) => ApplyFilter(Selected?.Track.Id);
    partial void OnEditCoverUrlChanged(string? value) { OnPropertyChanged(nameof(CoverPreview)); MarkDirty(); }
    partial void OnEditCoverLocalPathChanged(string? value) { OnPropertyChanged(nameof(CoverPreview)); MarkDirty(); }
    partial void OnEditTitleChanged(string? value) { OnPropertyChanged(nameof(CoverFallback)); MarkDirty(); }
    partial void OnEditArtistsChanged(string? value) { OnPropertyChanged(nameof(CoverFallback)); MarkDirty(); }
    partial void OnEditAlbumChanged(string? value) => MarkDirty();
    partial void OnEditAlbumArtistsChanged(string? value) => MarkDirty();
    partial void OnEditReleaseDateChanged(string? value) => MarkDirty();
    partial void OnEditGenreChanged(string? value) => MarkDirty();
    partial void OnEditTrackNumberChanged(string? value) => MarkDirty();
    partial void OnEditDiscNumberChanged(string? value) => MarkDirty();
    partial void OnEditIsrcChanged(string? value) => MarkDirty();
    partial void OnEditCommentChanged(string? value) => MarkDirty();
    partial void OnValidationErrorChanged(string? value) => OnPropertyChanged(nameof(HasValidationError));

    private void MarkDirty()
    {
        if (!_loadingForm) IsDirty = true;
    }

    partial void OnSelectedChanged(TrackRowViewModel? value) => LoadForm();

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is Guid trackId)
        {
            var track = await _tracks.GetAsync(trackId);
            if (track is not null)
            {
                State.SelectPlaylist(track.PlaylistId);
                _pendingSelection = trackId;
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
        if (State.SelectedPlaylist is { } p)
        {
            var tracks = await _tracks.GetByPlaylistAsync(p.Id);
            _all.AddRange(tracks.Select(t => new TrackRowViewModel(t)));
        }
        ApplyFilter(_pendingSelection ?? Selected?.Track.Id);
        _pendingSelection = null;
    }

    private void ApplyFilter(Guid? select)
    {
        var q = Search?.Trim();
        Tracks.Clear();
        foreach (var r in _all.Where(r => string.IsNullOrEmpty(q) || r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                                                  || r.Artists.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            Tracks.Add(r);
        Selected = Tracks.FirstOrDefault(r => r.Track.Id == select) ?? Tracks.FirstOrDefault();
    }

    private void LoadForm()
    {
        _loadingForm = true;
        var m = Selected?.Track.Metadata;
        EditTitle = m?.Title;
        EditArtists = m is null ? null : string.Join(", ", m.Artists);
        EditAlbum = m?.Album;
        EditAlbumArtists = m is null ? null : string.Join(", ", m.AlbumArtists);
        EditReleaseDate = m?.ReleaseDate;
        EditGenre = m?.Genre;
        EditTrackNumber = m?.TrackNumber?.ToString(CultureInfo.InvariantCulture);
        EditDiscNumber = m?.DiscNumber?.ToString(CultureInfo.InvariantCulture);
        EditIsrc = m?.Isrc;
        EditComment = m?.Comment;
        EditCoverUrl = m?.CoverUrl;
        EditCoverLocalPath = m?.CoverLocalPath;
        ValidationError = null;
        IsDirty = false;
        _loadingForm = false;
        foreach (var name in new[] { nameof(HasSelection), nameof(HasNoSelection), nameof(CoverPreview), nameof(OriginText), nameof(HasPreview),
                     nameof(HasLocalFile), nameof(LocalFile), nameof(HasSpotify), nameof(SpotifyText), nameof(SourceDuration), nameof(SourceTitle) })
            OnPropertyChanged(name);
    }

    private static List<string> SplitList(string? value) =>
        (value ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static int? ParseInt(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0) return n;
        throw new ArgumentException($"{label} doit être un nombre entier positif.");
    }

    [RelayCommand]
    private Task Save() => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null) return;
        ValidationError = null;
        try
        {
            var metadata = row.Track.Metadata.Clone();
            metadata.Title = EditTitle?.Trim() ?? string.Empty;
            metadata.Artists = SplitList(EditArtists);
            metadata.Album = string.IsNullOrWhiteSpace(EditAlbum) ? null : EditAlbum.Trim();
            metadata.AlbumArtists = SplitList(EditAlbumArtists);
            metadata.ReleaseDate = string.IsNullOrWhiteSpace(EditReleaseDate) ? null : EditReleaseDate.Trim();
            metadata.Genre = string.IsNullOrWhiteSpace(EditGenre) ? null : EditGenre.Trim();
            metadata.TrackNumber = ParseInt(EditTrackNumber, "Le numéro de piste");
            metadata.DiscNumber = ParseInt(EditDiscNumber, "Le numéro de disque");
            metadata.Isrc = string.IsNullOrWhiteSpace(EditIsrc) ? null : EditIsrc.Trim().ToUpperInvariant();
            metadata.Comment = string.IsNullOrWhiteSpace(EditComment) ? null : EditComment.Trim();
            metadata.CoverUrl = string.IsNullOrWhiteSpace(EditCoverUrl) ? null : EditCoverUrl.Trim();
            metadata.CoverLocalPath = string.IsNullOrWhiteSpace(EditCoverLocalPath) ? null : EditCoverLocalPath;
            await _editing.SaveMetadataAsync(row.Track, metadata, CancellationToken.None);
        }
        catch (ArgumentException ex)
        {
            ValidationError = ex.Message.Split(" (Parameter")[0];
            throw;
        }
        row.Update(row.Track);
        LoadForm();
    }, successTitle: "Métadonnées enregistrées");

    [RelayCommand]
    private void Revert() => LoadForm();

    [RelayCommand]
    private Task RestoreSpotify() => RunAsync(async () =>
    {
        var row = Selected;
        if (row?.Track.SpotifyTrackId is not { } id) return;
        if (!await _dialogs.ConfirmAsync("Rétablir les données Spotify ?", "Vos modifications manuelles seront remplacées par les métadonnées de la correspondance Spotify.", "Rétablir")) return;
        var candidates = await _matching.GetCandidatesAsync(row.Track, CancellationToken.None);
        var candidate = candidates.FirstOrDefault(c => c.SpotifyId == id) ?? await _spotify.GetTrackAsync(id, CancellationToken.None)
            ?? throw new InvalidOperationException("Morceau Spotify introuvable.");
        candidate.Score = row.Track.MatchScore ?? candidate.Score;
        await _editing.RevertToSpotifyAsync(row.Track, candidate, CancellationToken.None);
        row.Update(row.Track);
        LoadForm();
    }, successTitle: "Métadonnées Spotify rétablies");

    [RelayCommand]
    private Task ChooseCover() => RunAsync(async () =>
    {
        var file = await _dialogs.PickFileAsync("Choisir une pochette", "Images", ["*.jpg", "*.jpeg", "*.png", "*.webp"]);
        if (file is not null) EditCoverLocalPath = file;
    });

    [RelayCommand]
    private void ClearCustomCover() => EditCoverLocalPath = null;

    [RelayCommand]
    private Task AssociateFile() => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null) return;
        var patterns = _audio.SupportedExtensions.Select(e => "*" + e).ToList();
        var file = await _dialogs.PickFileAsync("Associer un fichier audio que vous possédez", "Fichiers audio", patterns);
        if (file is null) return;
        await _editing.AssociateLocalFileAsync(row.Track, file, CancellationToken.None);
        row.Update(row.Track);
        LoadForm();
        Notifications.Success("Fichier associé", Path.GetFileName(file));
    });

    [RelayCommand]
    private Task RemoveFile() => RunAsync(async () =>
    {
        var row = Selected;
        if (row is null) return;
        await _editing.AssociateLocalFileAsync(row.Track, null, CancellationToken.None);
        row.Update(row.Track);
        LoadForm();
    });

    [RelayCommand]
    private Task ImportTagsFromFile() => RunAsync(async () =>
    {
        var row = Selected;
        if (row?.Track.LocalFilePath is not { } path) return;
        var info = await _audio.ReadAsync(path);
        if (info is null) throw new InvalidOperationException("Impossible de lire les tags de ce fichier.");
        if (!string.IsNullOrWhiteSpace(info.Metadata.Title)) EditTitle = info.Metadata.Title;
        if (info.Metadata.Artists.Count > 0) EditArtists = string.Join(", ", info.Metadata.Artists);
        if (!string.IsNullOrWhiteSpace(info.Metadata.Album)) EditAlbum = info.Metadata.Album;
        if (!string.IsNullOrWhiteSpace(info.Metadata.ReleaseDate)) EditReleaseDate = info.Metadata.ReleaseDate;
        if (!string.IsNullOrWhiteSpace(info.Metadata.Genre)) EditGenre = info.Metadata.Genre;
        Notifications.Show("Tags importés", "Vérifiez puis enregistrez.");
    });

    [RelayCommand]
    private Task PlayPreview()
    {
        var t = Selected?.Track;
        if (t?.PreviewUrl is null) return Task.CompletedTask;
        return Player.PlayAsync(t.PreviewUrl, t.Metadata.Title, t.Metadata.ArtistsDisplay, CoverPreview, "Extrait Spotify");
    }

    [RelayCommand]
    private Task PlayLocal()
    {
        var t = Selected?.Track;
        if (t?.LocalFilePath is null) return Task.CompletedTask;
        return Player.PlayAsync(t.LocalFilePath, t.Metadata.Title, t.Metadata.ArtistsDisplay, CoverPreview, "Fichier local");
    }

    [RelayCommand]
    private void OpenSpotify()
    {
        if (Selected?.Track.SpotifyUrl is { } url) _dialogs.OpenUrl(url);
    }

    [RelayCommand]
    private void OpenYouTube()
    {
        if (Selected?.Track is { Availability: SourceAvailability.Available } t) _dialogs.OpenUrl($"https://www.youtube.com/watch?v={t.SourceVideoId}");
    }

    [RelayCommand]
    private void Compare()
    {
        if (Selected is not null) _navigation.NavigateTo(PageKey.MatchReview, Selected.Track.Id);
    }
}
