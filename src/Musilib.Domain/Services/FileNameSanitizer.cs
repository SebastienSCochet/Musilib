using System.Text;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Domain.Services;

/// <summary>Construction de noms de fichiers valides sous Windows.</summary>
public static class FileNameSanitizer
{
    public const int MaxSegmentLength = 120;

    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Nettoie un segment de chemin (nom de fichier ou dossier, sans extension).</summary>
    public static string Sanitize(string? name, string fallback = "Inconnu")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 32 || Array.IndexOf(Invalid, c) >= 0)
            {
                sb.Append(c == ':' || c == '/' || c == '\\' || c == '|' ? '-' : c == '"' ? '\'' : '_');
            }
            else sb.Append(c);
        }
        var result = sb.ToString();
        while (result.Contains("  ", StringComparison.Ordinal)) result = result.Replace("  ", " ", StringComparison.Ordinal);
        result = result.Trim().TrimEnd('.', ' ');
        if (result.Length > MaxSegmentLength) result = result[..MaxSegmentLength].TrimEnd('.', ' ');
        if (result.Length == 0) return fallback;
        var stem = result.Split('.')[0];
        if (Reserved.Contains(stem)) result = "_" + result;
        return result;
    }

    /// <summary>Nom au format « Artiste - Titre » (sans extension).</summary>
    public static string BuildFileName(TrackMetadata metadata)
    {
        var artist = metadata.Artists.Count > 0 ? string.Join(", ", metadata.Artists) : "Artiste inconnu";
        var title = string.IsNullOrWhiteSpace(metadata.Title) ? "Titre inconnu" : metadata.Title;
        return Sanitize($"{artist} - {title}");
    }

    /// <summary>Sous-dossier relatif selon l'organisation choisie.</summary>
    public static string BuildRelativeDirectory(TrackMetadata metadata, ExportOrganization organization)
    {
        var artist = Sanitize(metadata.AlbumArtists.FirstOrDefault() ?? metadata.Artists.FirstOrDefault(), "Artiste inconnu");
        var album = Sanitize(metadata.Album, "Album inconnu");
        return organization switch
        {
            ExportOrganization.Artist => artist,
            ExportOrganization.ArtistAlbum => Path.Combine(artist, album),
            ExportOrganization.Album => album,
            _ => string.Empty
        };
    }

    /// <summary>Renvoie un chemin libre en ajoutant " (2)", " (3)"... si nécessaire.</summary>
    public static string MakeUnique(string path, Func<string, bool> exists)
    {
        if (!exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        throw new IOException($"Impossible de trouver un nom libre pour {path}");
    }
}
