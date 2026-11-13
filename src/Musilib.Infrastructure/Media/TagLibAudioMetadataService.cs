using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using TagLib;

namespace Musilib.Infrastructure.Media;

/// <summary>Lecture/écriture des tags (ID3v2.3 pour le MP3, Vorbis Comments, MP4, RIFF...) via TagLibSharp.</summary>
public sealed class TagLibAudioMetadataService : IAudioMetadataService
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma", ".aiff", ".aif", ".ape", ".wv"
    };

    static TagLibAudioMetadataService()
    {
        // ID3v2.3 : compatibilité maximale (Explorateur Windows, lecteurs anciens).
        TagLib.Id3v2.Tag.DefaultVersion = 3;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
    }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public Task<AudioFileInfo?> ReadAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!System.IO.File.Exists(path) || !IsSupported(path)) return null;
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var metadata = new TrackMetadata
            {
                Title = tag.Title ?? string.Empty,
                Artists = (tag.Performers ?? Array.Empty<string>()).SelectMany(SplitMulti).ToList(),
                AlbumArtists = (tag.AlbumArtists ?? Array.Empty<string>()).SelectMany(SplitMulti).ToList(),
                Album = tag.Album,
                ReleaseDate = tag.Year > 0 ? tag.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                Genre = tag.FirstGenre,
                TrackNumber = tag.Track > 0 ? (int)tag.Track : null,
                DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null,
                Comment = tag.Comment,
                Isrc = tag.ISRC,
                DurationMs = (long)file.Properties.Duration.TotalMilliseconds
            };
            var info = new FileInfo(path);
            return new AudioFileInfo(path, metadata, metadata.DurationMs, info.Extension.TrimStart('.').ToUpperInvariant(), info.Length);
        }
        catch (Exception ex) when (ex is CorruptFileException or UnsupportedFormatException)
        {
            return null;
        }
    }, ct);

    public Task WriteAsync(string path, TrackMetadata metadata, byte[]? cover, string? coverMimeType, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = metadata.Title;
        tag.Performers = metadata.Artists.ToArray();
        tag.AlbumArtists = metadata.AlbumArtists.Count > 0 ? metadata.AlbumArtists.ToArray() : metadata.Artists.Take(1).ToArray();
        tag.Album = metadata.Album;
        tag.Year = (uint)(metadata.Year ?? 0);
        tag.Track = (uint)(metadata.TrackNumber ?? 0);
        tag.Disc = (uint)(metadata.DiscNumber ?? 0);
        tag.Genres = string.IsNullOrWhiteSpace(metadata.Genre) ? Array.Empty<string>() : new[] { metadata.Genre };
        tag.Comment = metadata.Comment;
        tag.ISRC = metadata.Isrc;
        if (cover is { Length: > 0 })
        {
            var picture = new Picture(new ByteVector(cover))
            {
                Type = PictureType.FrontCover,
                MimeType = coverMimeType ?? "image/jpeg",
                Description = "Cover"
            };
            tag.Pictures = new IPicture[] { picture };
        }
        file.Save();
    }, ct);

    private static IEnumerable<string> SplitMulti(string value) =>
        value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
