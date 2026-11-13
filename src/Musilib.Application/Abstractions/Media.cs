using Musilib.Domain.Entities;

namespace Musilib.Application.Abstractions;

public sealed record AudioFileInfo(string Path, TrackMetadata Metadata, long? DurationMs, string Format, long SizeBytes);

/// <summary>Lecture/écriture des métadonnées audio (ID3, Vorbis, MP4...).</summary>
public interface IAudioMetadataService
{
    IReadOnlyCollection<string> SupportedExtensions { get; }
    bool IsSupported(string path);
    Task<AudioFileInfo?> ReadAsync(string path, CancellationToken ct = default);
    Task WriteAsync(string path, TrackMetadata metadata, byte[]? cover, string? coverMimeType, CancellationToken ct = default);
}

/// <summary>Conversion audio via FFmpeg (optionnelle).</summary>
public interface IAudioConverter
{
    bool IsAvailable { get; }
    Task ConvertAsync(string sourcePath, string targetPath, string targetFormat, int bitrateKbps, CancellationToken ct = default);
}

/// <summary>Téléchargement d'images (pochettes) avec cache disque.</summary>
public interface IImageDownloader
{
    Task<(byte[] Data, string MimeType)?> GetAsync(string url, CancellationToken ct = default);
    /// <summary>Chemin local mis en cache de l'image (téléchargée si besoin).</summary>
    Task<string?> GetCachedPathAsync(string url, CancellationToken ct = default);
}

public enum PlaybackState
{
    Stopped,
    Loading,
    Playing,
    Paused,
    Error
}

/// <summary>Lecteur audio (extraits Spotify autorisés ou fichiers locaux de l'utilisateur).</summary>
public interface IAudioPlayer : IDisposable
{
    bool IsSupported { get; }
    PlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    float Volume { get; set; }
    event EventHandler? StateChanged;
    Task PlayAsync(string source, CancellationToken ct = default);
    void Pause();
    void Resume();
    void Stop();
    void Seek(TimeSpan position);
}
