using System.Text;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Domain.Services;

namespace Musilib.Application.Services;

public sealed class ExportOptions
{
    public Guid PlaylistId { get; set; }
    public List<Guid> TrackIds { get; set; } = new();
    public string DestinationFolder { get; set; } = string.Empty;
    public ExportOrganization Organization { get; set; } = ExportOrganization.ArtistAlbum;
    public ConflictResolution Conflict { get; set; } = ConflictResolution.Skip;
    public bool WriteTags { get; set; } = true;
    public bool EmbedCover { get; set; } = true;
    public bool WriteM3u { get; set; } = true;
    /// <summary>Extension cible (mp3, flac, m4a, ogg, opus) ou vide pour conserver le format.</summary>
    public string ConvertTo { get; set; } = string.Empty;
    public int BitrateKbps { get; set; } = 320;
    public string? PlaylistName { get; set; }
}

public enum ExportItemAction
{
    Copy,
    Convert,
    Duplicate,
    Conflict,
    NoSource,
    SourceMissing,
    Excluded
}

public sealed record ExportPlanItem(Track Track, string? SourcePath, string? TargetPath, ExportItemAction Action, string Note);

public sealed record ExportItemResult(Guid TrackId, JobItemStatus Status, string? TargetPath, string Message);

/// <summary>
/// Export de fichiers audio possédés par l'utilisateur vers un dossier organisé, avec renommage
/// « Artiste - Titre », écriture des tags et de la pochette. Le fichier original n'est jamais modifié.
/// Aucun audio n'est téléchargé depuis YouTube ou Spotify : leurs API ne le permettent pas.
/// </summary>
public sealed class ExportService
{
    private readonly IAudioMetadataService _audio;
    private readonly IAudioConverter _converter;
    private readonly IImageDownloader _images;
    private readonly ILogger<ExportService> _logger;

    public ExportService(IAudioMetadataService audio, IAudioConverter converter, IImageDownloader images, ILogger<ExportService> logger)
    {
        _audio = audio;
        _converter = converter;
        _images = images;
        _logger = logger;
    }

    public bool ConverterAvailable => _converter.IsAvailable;

    public async Task<IReadOnlyList<ExportPlanItem>> PlanAsync(IEnumerable<Track> tracks, ExportOptions options, CancellationToken ct)
    {
        var plan = new List<ExportPlanItem>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks.OrderBy(t => t.Position))
        {
            ct.ThrowIfCancellationRequested();
            if (track.Excluded) { plan.Add(new(track, null, null, ExportItemAction.Excluded, "Exclu")); continue; }
            if (!track.HasLocalFile) { plan.Add(new(track, null, null, ExportItemAction.NoSource, "Aucun fichier audio local associé")); continue; }
            if (!File.Exists(track.LocalFilePath)) { plan.Add(new(track, track.LocalFilePath, null, ExportItemAction.SourceMissing, "Fichier source introuvable")); continue; }

            var source = Path.GetFullPath(track.LocalFilePath!);
            var convert = NeedsConversion(source, options);
            var ext = convert ? "." + options.ConvertTo.Trim('.').ToLowerInvariant() : Path.GetExtension(source).ToLowerInvariant();
            var target = BuildTargetPath(track.Metadata, options, ext);

            if (!sources.Add(source))
            {
                plan.Add(new(track, source, target, ExportItemAction.Duplicate, "Même fichier source qu'un autre morceau de la sélection"));
                continue;
            }

            // Collision de noms à l'intérieur de l'export : suffixe automatique.
            if (reserved.Contains(target))
                target = FileNameSanitizer.MakeUnique(target, p => reserved.Contains(p) || File.Exists(p));

            var action = convert ? ExportItemAction.Convert : ExportItemAction.Copy;
            var note = convert ? $"Conversion en {options.ConvertTo.ToUpperInvariant()}" : "Copie (original préservé)";
            if (File.Exists(target))
            {
                if (await IsDuplicateAsync(target, track.Metadata, ct).ConfigureAwait(false))
                {
                    action = ExportItemAction.Duplicate;
                    note = "Déjà présent à la destination";
                }
                else
                {
                    action = ExportItemAction.Conflict;
                    note = options.Conflict switch
                    {
                        ConflictResolution.Overwrite => "Conflit : le fichier existant sera remplacé",
                        ConflictResolution.KeepBoth => "Conflit : un nouveau nom sera utilisé",
                        _ => "Conflit : ignoré"
                    };
                    if (options.Conflict == ConflictResolution.KeepBoth)
                        target = FileNameSanitizer.MakeUnique(target, p => reserved.Contains(p) || File.Exists(p));
                }
            }
            reserved.Add(target);
            plan.Add(new(track, source, target, action, note));
        }
        return plan;
    }

    public bool NeedsConversion(string source, ExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConvertTo)) return false;
        var target = "." + options.ConvertTo.Trim('.').ToLowerInvariant();
        return !string.Equals(Path.GetExtension(source), target, StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildTargetPath(TrackMetadata metadata, ExportOptions options, string extension)
    {
        var dir = Path.Combine(options.DestinationFolder, FileNameSanitizer.BuildRelativeDirectory(metadata, options.Organization));
        return Path.GetFullPath(Path.Combine(dir, FileNameSanitizer.BuildFileName(metadata) + extension));
    }

    private async Task<bool> IsDuplicateAsync(string existingPath, TrackMetadata metadata, CancellationToken ct)
    {
        try
        {
            var info = await _audio.ReadAsync(existingPath, ct).ConfigureAwait(false);
            if (info is null) return false;
            var sameTitle = TextNormalizer.Normalize(info.Metadata.Title) == TextNormalizer.Normalize(metadata.Title);
            var sameArtist = TextNormalizer.Normalize(info.Metadata.ArtistsDisplay) == TextNormalizer.Normalize(metadata.ArtistsDisplay);
            var sameIsrc = !string.IsNullOrEmpty(metadata.Isrc) && string.Equals(info.Metadata.Isrc, metadata.Isrc, StringComparison.OrdinalIgnoreCase);
            return sameIsrc || (sameTitle && sameArtist);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lecture des tags impossible pour la détection de doublon");
            return false;
        }
    }

    public async Task<ExportItemResult> ExportItemAsync(ExportPlanItem item, ExportOptions options, CancellationToken ct)
    {
        var id = item.Track.Id;
        switch (item.Action)
        {
            case ExportItemAction.Excluded:
            case ExportItemAction.NoSource:
                return new(id, JobItemStatus.Skipped, null, item.Note);
            case ExportItemAction.SourceMissing:
                return new(id, JobItemStatus.Failed, null, item.Note);
            case ExportItemAction.Duplicate when options.Conflict != ConflictResolution.Overwrite || item.SourcePath is null:
                return new(id, JobItemStatus.Skipped, item.TargetPath, "Doublon ignoré");
            case ExportItemAction.Conflict when options.Conflict == ConflictResolution.Skip:
                return new(id, JobItemStatus.Skipped, item.TargetPath, "Conflit de nom : ignoré");
        }

        var target = item.TargetPath!;
        var source = item.SourcePath!;
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);

        var convert = item.Action == ExportItemAction.Convert || (NeedsConversion(source, options) && item.Action != ExportItemAction.Copy);
        var message = "Exporté";
        if (convert && !_converter.IsAvailable)
        {
            // Conversion impossible : on conserve le format original plutôt que d'échouer.
            target = Path.ChangeExtension(target, Path.GetExtension(source));
            convert = false;
            message = "Exporté au format d'origine (FFmpeg introuvable)";
        }

        var temp = Path.Combine(dir, $".musilib-{Guid.NewGuid():N}{Path.GetExtension(target)}");
        try
        {
            if (convert)
            {
                await _converter.ConvertAsync(source, temp, options.ConvertTo, options.BitrateKbps, ct).ConfigureAwait(false);
                message = $"Converti en {options.ConvertTo.ToUpperInvariant()}";
            }
            else
            {
                await CopyFileAsync(source, temp, ct).ConfigureAwait(false);
            }

            if (options.WriteTags && _audio.IsSupported(temp))
            {
                byte[]? cover = null;
                string? mime = null;
                if (options.EmbedCover)
                {
                    (cover, mime) = await LoadCoverAsync(item.Track.Metadata, ct).ConfigureAwait(false);
                }
                await _audio.WriteAsync(temp, item.Track.Metadata, cover, mime, ct).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
            return new(id, JobItemStatus.Completed, target, message);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { /* nettoyage best effort */ }
            }
        }
    }

    private async Task<(byte[]? Data, string? Mime)> LoadCoverAsync(TrackMetadata metadata, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(metadata.CoverLocalPath) && File.Exists(metadata.CoverLocalPath))
            {
                var bytes = await File.ReadAllBytesAsync(metadata.CoverLocalPath, ct).ConfigureAwait(false);
                var mime = Path.GetExtension(metadata.CoverLocalPath).ToLowerInvariant() is ".png" ? "image/png" : "image/jpeg";
                return (bytes, mime);
            }
            if (!string.IsNullOrWhiteSpace(metadata.CoverUrl))
            {
                var result = await _images.GetAsync(metadata.CoverUrl, ct).ConfigureAwait(false);
                if (result is { } r) return (r.Data, r.MimeType);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Pochette indisponible : {Message}", ex.Message);
        }
        return (null, null);
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        const int buffer = 1 << 16;
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer, useAsync: true);
        await using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer, useAsync: true);
        await src.CopyToAsync(dst, buffer, ct).ConfigureAwait(false);
    }

    /// <summary>Écrit une playlist M3U8 (chemins relatifs, ordre d'origine).</summary>
    public static async Task<string> WriteM3uAsync(string folder, string playlistName, IEnumerable<(Track Track, string Path)> entries, CancellationToken ct)
    {
        var file = Path.Combine(folder, FileNameSanitizer.Sanitize(playlistName, "Playlist") + ".m3u8");
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine($"#PLAYLIST:{playlistName}");
        foreach (var (track, path) in entries.OrderBy(e => e.Track.Position))
        {
            var seconds = (track.Metadata.DurationMs ?? track.SourceDurationMs ?? 0) / 1000;
            sb.AppendLine($"#EXTINF:{seconds},{track.Metadata.ArtistsDisplay} - {track.Metadata.Title}");
            sb.AppendLine(Path.GetRelativePath(folder, path).Replace('\\', '/'));
        }
        await File.WriteAllTextAsync(file, sb.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return file;
    }
}
