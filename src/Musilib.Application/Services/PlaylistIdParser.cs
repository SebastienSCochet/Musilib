using System.Text.RegularExpressions;

namespace Musilib.Application.Services;

/// <summary>Extrait un identifiant de playlist YouTube depuis une URL ou un identifiant brut.</summary>
public static partial class PlaylistIdParser
{
    public static bool TryParse(string? input, out string playlistId)
    {
        playlistId = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();

        var m = ListParam().Match(text);
        if (m.Success)
        {
            playlistId = m.Groups["id"].Value;
            return true;
        }

        if (RawId().IsMatch(text))
        {
            playlistId = text;
            return true;
        }
        return false;
    }

    [GeneratedRegex(@"[?&]list=(?<id>[A-Za-z0-9_-]{2,64})")]
    private static partial Regex ListParam();

    [GeneratedRegex(@"^(PL|UU|LL|FL|OL|RD|OLAK5uy_)[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex RawId();
}
