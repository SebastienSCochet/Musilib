using System.Text.RegularExpressions;

namespace Musilib.Domain.Services;

public sealed record ParsedTitle(string Title, IReadOnlyList<string> Artists, bool ArtistFromChannel);

/// <summary>
/// Extrait artiste(s) et titre à partir du titre d'une vidéo YouTube et du nom de la chaîne.
/// Gère "Artiste - Titre (Official Video)", les chaînes "Artiste - Topic", "ArtisteVEVO", les "feat.".
/// </summary>
public static partial class YouTubeTitleParser
{
    private static readonly string[] Separators = [" - ", " – ", " — ", " ~ ", " | ", " -- "];

    public static ParsedTitle Parse(string? videoTitle, string? channelTitle)
    {
        var raw = (videoTitle ?? string.Empty).Trim();
        var cleaned = CleanNoise(raw);

        string? artistPart = null;
        string titlePart = cleaned;
        foreach (var sep in Separators)
        {
            var idx = cleaned.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                artistPart = cleaned[..idx].Trim();
                titlePart = cleaned[(idx + sep.Length)..].Trim();
                break;
            }
        }

        // Format "Artiste « Titre »" ou 'Artiste "Titre"'
        if (artistPart is null)
        {
            var quoted = QuotedTitle().Match(cleaned);
            if (quoted.Success && quoted.Groups["artist"].Value.Trim().Length > 0)
            {
                artistPart = quoted.Groups["artist"].Value.Trim();
                titlePart = quoted.Groups["title"].Value.Trim();
            }
        }

        var artists = new List<string>();
        var fromChannel = false;
        if (!string.IsNullOrWhiteSpace(artistPart))
        {
            artists.AddRange(SplitArtists(StripFeat(artistPart, out var featInArtist)));
            artists.AddRange(featInArtist);
        }
        else
        {
            var channelArtist = CleanChannel(channelTitle);
            if (!string.IsNullOrWhiteSpace(channelArtist))
            {
                artists.Add(channelArtist);
                fromChannel = true;
            }
        }

        titlePart = StripFeat(titlePart, out var featInTitle);
        foreach (var f in featInTitle)
        {
            if (!artists.Contains(f, StringComparer.OrdinalIgnoreCase)) artists.Add(f);
        }

        titlePart = titlePart.Trim().Trim('"', '\'', '«', '»', '“', '”').Trim();
        if (titlePart.Length == 0) titlePart = raw;

        return new ParsedTitle(titlePart, artists.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), fromChannel);
    }

    /// <summary>Supprime les mentions inutiles : (Official Video), [Lyrics], HD, 4K, Clip officiel...</summary>
    public static string CleanNoise(string title)
    {
        var t = NoiseBracket().Replace(title, " ");
        t = NoiseBare().Replace(t, " ");
        t = Regex.Replace(t, @"\s{2,}", " ");
        return t.Trim().TrimEnd('-', '|', '–', '—').Trim();
    }

    public static string? CleanChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;
        var c = channel.Trim();
        c = Regex.Replace(c, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"\s*-\s*Thème$", "", RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"VEVO$", "", RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"\s*(Official|Officiel|Music|TV)$", "", RegexOptions.IgnoreCase);
        return c.Trim();
    }

    public static IEnumerable<string> SplitArtists(string artists) =>
        ArtistSplit().Split(artists)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0);

    private static string StripFeat(string text, out List<string> featured)
    {
        featured = new List<string>();
        var m = FeatPattern().Match(text);
        if (!m.Success) return text;
        var feat = m.Groups["feat"].Value.Trim().TrimEnd(')', ']').Trim();
        featured.AddRange(SplitArtists(feat));
        return (text[..m.Index] + (m.Groups["close"].Success ? string.Empty : string.Empty)).Trim();
    }

    [GeneratedRegex(@"[\(\[](?:[^\)\]]*?)(official|officiel|lyrics?|paroles|audio|video|vid[ée]o|visuali[sz]er|clip|hd|hq|4k|mv|m/v|music video)(?:[^\)\]]*?)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBracket();

    [GeneratedRegex(@"\b(official\s+(music\s+)?video|official\s+audio|lyric\s+video|clip\s+officiel|\bHD\b|\b4K\b)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBare();

    [GeneratedRegex(@"\s*[\(\[]?\b(?:feat\.?|ft\.?|featuring)\s+(?<feat>[^\)\]]+)(?<close>[\)\]])?.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatPattern();

    // "&" et "and" ne séparent pas : de nombreux noms d'artistes les contiennent (« Simon & Garfunkel »).
    [GeneratedRegex(@"\s*(?:,|\s+x\s+|\s+vs\.?\s+|\s/\s)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplit();

    [GeneratedRegex(@"^(?<artist>[^""«“]+?)\s*[""«“](?<title>[^""»”]+)[""»”]")]
    private static partial Regex QuotedTitle();
}
