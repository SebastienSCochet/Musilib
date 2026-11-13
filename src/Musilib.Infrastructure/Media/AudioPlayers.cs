using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using NAudio.Wave;
using PlaybackState = Musilib.Application.Abstractions.PlaybackState;

namespace Musilib.Infrastructure.Media;

/// <summary>
/// Lecteur audio Windows (Media Foundation) : lit les extraits fournis par Spotify (preview_url, lorsque
/// disponibles) ou les fichiers locaux de l'utilisateur. Aucune lecture du flux complet Spotify/YouTube.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NAudioPlayer : IAudioPlayer
{
    private readonly ILogger<NAudioPlayer> _logger;
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    private float _volume = 0.8f;
    private PlaybackState _state = PlaybackState.Stopped;

    public NAudioPlayer(ILogger<NAudioPlayer> logger) => _logger = logger;

    public bool IsSupported => true;
    public PlaybackState State => _state;
    public TimeSpan Position { get { lock (_gate) return _reader?.CurrentTime ?? TimeSpan.Zero; } }
    public TimeSpan Duration { get { lock (_gate) return _reader?.TotalTime ?? TimeSpan.Zero; } }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_gate) { if (_output is not null) _output.Volume = _volume; }
        }
    }

    public event EventHandler? StateChanged;

    public async Task PlayAsync(string source, CancellationToken ct = default)
    {
        Stop();
        SetState(PlaybackState.Loading);
        try
        {
            var reader = await Task.Run(() => new MediaFoundationReader(source), ct).ConfigureAwait(false);
            var output = new WaveOutEvent { DesiredLatency = 200 };
            output.Init(reader);
            output.Volume = _volume;
            output.PlaybackStopped += OnStopped;
            lock (_gate)
            {
                _reader = reader;
                _output = output;
            }
            output.Play();
            SetState(PlaybackState.Playing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Lecture impossible : {Message}", ex.Message);
            Cleanup();
            SetState(PlaybackState.Error);
            throw new InvalidOperationException("Lecture impossible : " + ex.Message, ex);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _output)) return;
        Cleanup();
        SetState(e.Exception is null ? PlaybackState.Stopped : PlaybackState.Error);
    }

    public void Pause()
    {
        lock (_gate) _output?.Pause();
        if (_output is not null) SetState(PlaybackState.Paused);
    }

    public void Resume()
    {
        lock (_gate) _output?.Play();
        if (_output is not null) SetState(PlaybackState.Playing);
    }

    public void Stop()
    {
        Cleanup();
        SetState(PlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_reader is null) return;
            var p = position < TimeSpan.Zero ? TimeSpan.Zero : position > _reader.TotalTime ? _reader.TotalTime : position;
            _reader.CurrentTime = p;
        }
    }

    private void Cleanup()
    {
        WaveOutEvent? output;
        MediaFoundationReader? reader;
        lock (_gate)
        {
            output = _output;
            reader = _reader;
            _output = null;
            _reader = null;
        }
        if (output is not null)
        {
            output.PlaybackStopped -= OnStopped;
            try { output.Stop(); } catch (InvalidOperationException) { }
            output.Dispose();
        }
        reader?.Dispose();
    }

    private void SetState(PlaybackState state)
    {
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Cleanup();
}

/// <summary>Lecteur de repli pour les plateformes non prises en charge.</summary>
public sealed class UnsupportedAudioPlayer : IAudioPlayer
{
    public bool IsSupported => false;
    public PlaybackState State => PlaybackState.Stopped;
    public TimeSpan Position => TimeSpan.Zero;
    public TimeSpan Duration => TimeSpan.Zero;
    public float Volume { get; set; } = 0.8f;
#pragma warning disable CS0067
    public event EventHandler? StateChanged;
#pragma warning restore CS0067
    public Task PlayAsync(string source, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("La lecture audio n'est disponible que sous Windows.");
    public void Pause() { }
    public void Resume() { }
    public void Stop() { }
    public void Seek(TimeSpan position) { }
    public void Dispose() { }
}
