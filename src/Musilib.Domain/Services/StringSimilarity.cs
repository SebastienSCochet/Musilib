namespace Musilib.Domain.Services;

/// <summary>Mesures de similarité de chaînes (0..1).</summary>
public static class StringSimilarity
{
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Ratio basé sur la distance de Levenshtein, après normalisation.</summary>
    public static double Ratio(string? a, string? b)
    {
        var na = TextNormalizer.Normalize(a);
        var nb = TextNormalizer.Normalize(b);
        return RatioNormalized(na, nb);
    }

    public static double RatioNormalized(string na, string nb)
    {
        if (na.Length == 0 && nb.Length == 0) return 1;
        if (na.Length == 0 || nb.Length == 0) return 0;
        var dist = Levenshtein(na, nb);
        return 1.0 - (double)dist / Math.Max(na.Length, nb.Length);
    }

    /// <summary>Similarité par ensembles de mots (insensible à l'ordre), combinée au ratio global.</summary>
    public static double TokenSetRatio(string? a, string? b)
    {
        var ta = new SortedSet<string>(TextNormalizer.Tokens(a), StringComparer.Ordinal);
        var tb = new SortedSet<string>(TextNormalizer.Tokens(b), StringComparer.Ordinal);
        if (ta.Count == 0 && tb.Count == 0) return 1;
        if (ta.Count == 0 || tb.Count == 0) return 0;

        var inter = new SortedSet<string>(ta, StringComparer.Ordinal);
        inter.IntersectWith(tb);
        var diffA = new SortedSet<string>(ta, StringComparer.Ordinal);
        diffA.ExceptWith(tb);
        var diffB = new SortedSet<string>(tb, StringComparer.Ordinal);
        diffB.ExceptWith(ta);

        var sInter = string.Join(' ', inter);
        var sA = (sInter + " " + string.Join(' ', diffA)).Trim();
        var sB = (sInter + " " + string.Join(' ', diffB)).Trim();

        var r1 = inter.Count == 0 ? 0 : RatioNormalized(sInter, sA);
        var r2 = inter.Count == 0 ? 0 : RatioNormalized(sInter, sB);
        var r3 = RatioNormalized(sA, sB);
        var sorted = RatioNormalized(string.Join(' ', ta), string.Join(' ', tb));
        return Math.Max(Math.Max(r1 * 0.95, r2 * 0.95), Math.Max(r3, sorted));
    }
}
