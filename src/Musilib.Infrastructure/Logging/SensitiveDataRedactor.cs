using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Musilib.Infrastructure.Logging;

/// <summary>Masque jetons, codes d'autorisation, secrets et clés API dans les journaux.</summary>
public static partial class SensitiveDataRedactor
{
    public const string Mask = "***";

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var s = Bearer().Replace(input, "Bearer " + Mask);
        s = KeyValue().Replace(s, m => m.Groups["k"].Value + m.Groups["sep"].Value + Mask);
        s = JsonField().Replace(s, m => $"\"{m.Groups["k"].Value}\":\"{Mask}\"");
        return s;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-\._~\+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?<k>access_token|refresh_token|client_secret|code_verifier|code|key|api_key|token)(?<sep>=)[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"""(?<k>access_token|refresh_token|client_secret|id_token|code_verifier)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonField();
}

/// <summary>Enrichisseur Serilog appliquant le masquage aux propriétés textuelles et aux exceptions.</summary>
public sealed class RedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            if (value is ScalarValue { Value: string text })
            {
                var redacted = SensitiveDataRedactor.Redact(text);
                if (!ReferenceEquals(redacted, text) && redacted != text)
                    logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(redacted)));
            }
        }
    }
}
