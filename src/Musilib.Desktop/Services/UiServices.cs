using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musilib.Application.Abstractions;
using Musilib.Application.Jobs;
using Musilib.Domain.Entities;

namespace Musilib.Desktop.Services;

public enum PageKey
{
    Dashboard,
    Connections,
    Library,
    Playlist,
    MatchReview,
    Editor,
    Export,
    Transfer,
    Queue,
    History,
    Settings
}

public sealed record NavigationRequest(PageKey Page, object? Parameter);

/// <summary>Navigation entre écrans (découplée du ViewModel principal).</summary>
public sealed class NavigationService
{
    public event EventHandler<NavigationRequest>? NavigationRequested;

    public void NavigateTo(PageKey page, object? parameter = null) =>
        NavigationRequested?.Invoke(this, new NavigationRequest(page, parameter));
}

/// <summary>État partagé : playlists, playlist courante et notifications de traitements (thread UI).</summary>
public sealed partial class AppState : ObservableObject
{
    private readonly IPlaylistRepository _playlists;
    private bool _refreshing;
    private Guid? _lastNotifiedPlaylist;

    public AppState(IPlaylistRepository playlists, IJobQueue queue)
    {
        _playlists = playlists;
        queue.JobChanged += (_, job) => Dispatcher.UIThread.Post(() => OnJobChanged(job));
    }

    public ObservableCollection<Playlist> Playlists { get; } = new();

    [ObservableProperty]
    private Playlist? _selectedPlaylist;

    /// <summary>Changement effectif de playlist (identifiant différent).</summary>
    public event EventHandler<Playlist?>? PlaylistSelectionChanged;

    /// <summary>Mise à jour d'un traitement (déjà sur le thread UI).</summary>
    public event EventHandler<Job>? JobUpdated;

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        if (_refreshing) return;
        if (value?.Id == _lastNotifiedPlaylist) return;
        _lastNotifiedPlaylist = value?.Id;
        PlaylistSelectionChanged?.Invoke(this, value);
    }

    public void SelectPlaylist(Guid id)
    {
        var p = Playlists.FirstOrDefault(x => x.Id == id);
        if (p is not null) SelectedPlaylist = p;
    }

    public async Task RefreshPlaylistsAsync()
    {
        var list = await _playlists.GetAllAsync();
        var selectedId = SelectedPlaylist?.Id ?? _lastNotifiedPlaylist;
        _refreshing = true;
        try
        {
            Playlists.Clear();
            foreach (var p in list) Playlists.Add(p);
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == selectedId) ?? Playlists.FirstOrDefault();
        }
        finally
        {
            _refreshing = false;
        }
        if (SelectedPlaylist?.Id != _lastNotifiedPlaylist)
        {
            _lastNotifiedPlaylist = SelectedPlaylist?.Id;
            PlaylistSelectionChanged?.Invoke(this, SelectedPlaylist);
        }
    }

    private void OnJobChanged(Job job)
    {
        JobUpdated?.Invoke(this, job);
        if (job.IsTerminal || job.Status == Domain.Enums.JobStatus.Paused)
        {
            _ = RefreshPlaylistsAsync();
        }
    }
}

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

public sealed partial class ToastViewModel : ObservableObject
{
    public ToastViewModel(string title, string? message, ToastKind kind, Action<ToastViewModel> close)
    {
        Title = title;
        Message = message;
        Kind = kind;
        CloseCommand = new RelayCommand(() => close(this));
    }

    public string Title { get; }
    public string? Message { get; }
    public ToastKind Kind { get; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IsSuccess => Kind == ToastKind.Success;
    public bool IsWarning => Kind == ToastKind.Warning;
    public bool IsError => Kind == ToastKind.Error;
    public bool IsInfo => Kind == ToastKind.Info;
    public string IconKey => Kind switch
    {
        ToastKind.Success => "Icon.CheckCircle",
        ToastKind.Warning => "Icon.Alert",
        ToastKind.Error => "Icon.XCircle",
        _ => "Icon.Info"
    };
    public IRelayCommand CloseCommand { get; }
}

/// <summary>Notifications éphémères (feedback visuel de chaque opération).</summary>
public sealed class NotificationService
{
    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    public void Show(string title, string? message = null, ToastKind kind = ToastKind.Info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastViewModel(title, message, kind, t => Toasts.Remove(t));
            Toasts.Add(toast);
            while (Toasts.Count > 4) Toasts.RemoveAt(0);
            var delay = kind is ToastKind.Error or ToastKind.Warning ? 7000 : 4500;
            DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromMilliseconds(delay));
        });
    }

    public void Success(string title, string? message = null) => Show(title, message, ToastKind.Success);
    public void Warning(string title, string? message = null) => Show(title, message, ToastKind.Warning);
    public void Error(string title, string? message = null) => Show(title, message, ToastKind.Error);
}

public sealed partial class ConfirmDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public ConfirmDialogViewModel(string title, string message, string confirmText, bool isDanger, IReadOnlyList<string>? details)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsDanger = isDanger;
        Details = details ?? Array.Empty<string>();
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public bool IsDanger { get; }
    public bool IsNotDanger => !IsDanger;
    public IReadOnlyList<string> Details { get; }
    public bool HasDetails => Details.Count > 0;
    public Task<bool> Result => _tcs.Task;

    [RelayCommand]
    private void Confirm() => _tcs.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _tcs.TrySetResult(false);
}

/// <summary>Dialogues : confirmation (superposition intégrée), sélecteurs de fichiers/dossiers, presse-papiers.</summary>
public sealed partial class DialogService : ObservableObject
{
    private readonly IBrowserLauncher _browser;

    public DialogService(IBrowserLauncher browser) => _browser = browser;

    public TopLevel? Owner { get; set; }

    [ObservableProperty]
    private ConfirmDialogViewModel? _current;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirmer", bool danger = false, IReadOnlyList<string>? details = null)
    {
        var vm = new ConfirmDialogViewModel(title, message, confirmText, danger, details);
        Current = vm;
        try { return await vm.Result; }
        finally { Current = null; }
    }

    public async Task<string?> PickFolderAsync(string title, string? startPath = null)
    {
        if (Owner is null) return null;
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
            start = await Owner.StorageProvider.TryGetFolderFromPathAsync(startPath);
        var result = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns)
    {
        if (Owner is null) return null;
        var result = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns.ToList() }]
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task CopyAsync(string text)
    {
        if (Owner?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public void OpenUrl(string url) => _browser.Open(url);

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path) && File.Exists(path))
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }
            path = Path.GetDirectoryName(path) ?? path;
        }
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

/// <summary>Chargement asynchrone des images avec cache mémoire (références faibles) et décodage redimensionné.</summary>
public sealed class ImageLoader
{
    private readonly IImageDownloader _downloader;
    private readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _cache = new();

    public ImageLoader(IImageDownloader downloader) => _downloader = downloader;

    public async Task<Bitmap?> LoadAsync(string source, int decodeWidth)
    {
        var key = decodeWidth + "|" + source;
        if (_cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached)) return cached;

        var path = File.Exists(source) ? source : await _downloader.GetCachedPathAsync(source).ConfigureAwait(false);
        if (path is null) return null;
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception)
            {
                return null;
            }
        }).ConfigureAwait(false);
        if (bitmap is not null) _cache[key] = new WeakReference<Bitmap>(bitmap);
        return bitmap;
    }
}
