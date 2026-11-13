using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Desktop.Converters;
using Musilib.Desktop.Services;

namespace Musilib.Desktop.ViewModels;

/// <summary>
/// Lecteur global (barre inférieure) : extraits fournis par Spotify lorsqu'ils existent, ou fichiers
/// audio locaux de l'utilisateur. Musilib ne lit jamais le flux complet d'un service de streaming.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IAudioPlayer _player;
    private readonly NotificationService _notifications;
    private readonly ILogger<PlayerViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private bool _updatingPosition;

    public PlayerViewModel(IAudioPlayer player, NotificationService notifications, ILogger<PlayerViewModel> logger)
    {
        _player = player;
        _notifications = notifications;
        _logger = logger;
        _volume = player.Volume * 100;
        _player.StateChanged += (_, _) => Dispatcher.UIThread.Post(SyncState);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
    }

    public bool IsSupported => _player.IsSupported;

    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _nowPlayingTitle;
    [ObservableProperty] private string? _nowPlayingSubtitle;
    [ObservableProperty] private string? _nowPlayingCover;
    [ObservableProperty] private string? _sourceLabel;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private double _volume;
    [ObservableProperty] private string? _currentSource;

    partial void OnVolumeChanged(double value) => _player.Volume = (float)(value / 100.0);

    partial void OnPositionChanged(double value)
    {
        if (_updatingPosition) return;
        _player.Seek(TimeSpan.FromSeconds(value));
    }

    public async Task PlayAsync(string source, string title, string? subtitle, string? cover, string sourceLabel)
    {
        if (!_player.IsSupported)
        {
            _notifications.Warning("Lecture indisponible", "La lecture audio n'est prise en charge que sous Windows.");
            return;
        }
        if (CurrentSource == source && HasMedia)
        {
            Toggle();
            return;
        }
        CurrentSource = source;
        NowPlayingTitle = title;
        NowPlayingSubtitle = subtitle;
        NowPlayingCover = cover;
        SourceLabel = sourceLabel;
        HasMedia = true;
        IsLoading = true;
        try
        {
            await _player.PlayAsync(source);
            _timer.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lecture impossible : {Message}", ex.Message);
            _notifications.Error("Lecture impossible", ex.Message);
            HasMedia = false;
            CurrentSource = null;
        }
        finally
        {
            IsLoading = false;
            SyncState();
        }
    }

    [RelayCommand]
    private void Toggle()
    {
        if (_player.State == PlaybackState.Playing) _player.Pause();
        else if (_player.State == PlaybackState.Paused) _player.Resume();
        else if (CurrentSource is not null) _ = PlayAsync(CurrentSource, NowPlayingTitle ?? string.Empty, NowPlayingSubtitle, NowPlayingCover, SourceLabel ?? string.Empty);
    }

    [RelayCommand]
    public void Stop()
    {
        _player.Stop();
        _timer.Stop();
        HasMedia = false;
        CurrentSource = null;
        SyncState();
    }

    private void SyncState()
    {
        IsPlaying = _player.State == PlaybackState.Playing;
        if (_player.State is PlaybackState.Stopped or PlaybackState.Error && !IsLoading)
        {
            _timer.Stop();
            _updatingPosition = true;
            Position = 0;
            PositionText = "0:00";
            _updatingPosition = false;
        }
    }

    private void Tick()
    {
        _updatingPosition = true;
        var d = _player.Duration.TotalSeconds;
        Duration = d > 0 ? d : 1;
        Position = Math.Min(_player.Position.TotalSeconds, Duration);
        PositionText = Formatters.Duration((long)_player.Position.TotalMilliseconds) is "—" ? "0:00" : Formatters.Duration((long)_player.Position.TotalMilliseconds);
        DurationText = d > 0 ? Formatters.Duration((long)(d * 1000)) : "0:00";
        _updatingPosition = false;
    }

    public void Dispose()
    {
        _timer.Stop();
        _player.Dispose();
    }
}
