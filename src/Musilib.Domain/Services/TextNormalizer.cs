using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Musilib.Domain.Services;

/// <summary>Normalisation de chaînes pour la comparaison (casse, accents, ponctuation, mentions de version).</summary>
public static partial class TextNormalizer
{
    private static readonly string[] VersionKeywords =
    [
        "live", "remix", "acoustic", "acoustique", "instrumental", "karaoke", "cover", "sped up", "slowed",
        "nightcore", "edit", "version", "demo", "mix", "reprise", "unplugged", "orchestral", "piano"
    ];

    /// <summary>Minuscule, sans accents, ponctuation remplacée par des espaces, espaces compactés.</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var text = RemoveDiacritics(input).ToLowerInvariant();
        text = text.Replace("&", " and ").Replace("+", " and ");
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return MultiSpace().Replace(sb.ToString(), " ").Trim();
    }

    public static string RemoveDiacritics(string input)
    {
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Titre "cœur" : supprime les mentions entre parenthèses/crochets, les "feat.", "- Remastered 2011", etc.
    /// </summary>
    public static string CoreTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = Bracketed().Replace(title, " ");
        t = FeatTail().Replace(t, " ");
        t = DashSuffix().Replace(t, " ");
        return Normalize(t);
    }

    /// <summary>Mots-clés de version présents (live, remix...) servant à pénaliser les versions différentes.</summary>
    public static IReadOnlySet<string> VersionTags(string? title)
    {
        var norm = " " + Normalize(title) + " ";
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in VersionKeywords)
        {
            if (norm.Contains(" " + k + " ", StringComparison.Ordinal)) set.Add(k);
        }
        // "remaster" n'est pas considéré comme une version différente.
        return set;
    }

    public static IReadOnlyList<string> Tokens(string? input) =>
        Normalize(input).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"[\(\[\{【][^\)\]\}】]*[\)\]\}】]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\b(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatTail();

    [GeneratedRegex(@"\s[-–—]\s.*\b(remaster(ed)?|version|edit|mix|mono|stereo|single|radio)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashSuffix();
}
