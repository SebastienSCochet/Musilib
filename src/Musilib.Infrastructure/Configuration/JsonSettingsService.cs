using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;
using Musilib.Application.Settings;

namespace Musilib.Infrastructure.Configuration;

/// <summary>
/// Paramètres utilisateur : valeurs par défaut issues d'appsettings.json (section "Musilib"), surchargées
/// par le fichier utilisateur %APPDATA%\Musilib\settings.json. Aucun secret n'est stocké ici.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ILogger<JsonSettingsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonSettingsService(IConfiguration configuration, string settingsPath, ILogger<JsonSettingsService> logger)
    {
        _logger = logger;
        SettingsPath = settingsPath;
        Current = Load(configuration);
    }

    public AppSettings Current { get; private set; }
    public string SettingsPath { get; }
    public event EventHandler<AppSettings>? Changed;

    private AppSettings Load(IConfiguration configuration)
    {
        var settings = new AppSettings();
        configuration.GetSection("Musilib").Bind(settings);
        if (File.Exists(SettingsPath))
        {
            try
            {
                var user = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options);
                if (user is not null) settings = user;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Fichier de paramètres invalide, valeurs par défaut utilisées");
                File.Copy(SettingsPath, SettingsPath + ".invalid", overwrite: true);
            }
        }
        Normalize(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Normalize(settings);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temp = SettingsPath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, Options), ct).ConfigureAwait(false);
            File.Move(temp, SettingsPath, overwrite: true);
            Current = settings;
        }
        finally { _lock.Release(); }
        Changed?.Invoke(this, settings);
    }

    private static void Normalize(AppSettings s)
    {
        s.Processing.MaxConcurrentJobs = Math.Clamp(s.Processing.MaxConcurrentJobs, 1, 8);
        s.Processing.MaxConcurrentRequests = Math.Clamp(s.Processing.MaxConcurrentRequests, 1, 16);
        s.Processing.MaxRetries = Math.Clamp(s.Processing.MaxRetries, 0, 10);
        s.Processing.MaxRetryAfterSeconds = Math.Clamp(s.Processing.MaxRetryAfterSeconds, 1, 3600);
        s.Processing.SearchCacheDays = Math.Clamp(s.Processing.SearchCacheDays, 0, 365);
        s.Matching.SearchLimit = Math.Clamp(s.Matching.SearchLimit, 1, 50);
        s.Matching.MinimumThreshold = Math.Clamp(s.Matching.MinimumThreshold, 0.1, 0.95);
        s.Matching.AutoAcceptThreshold = Math.Clamp(s.Matching.AutoAcceptThreshold, s.Matching.MinimumThreshold, 1.0);
        s.Matching.AmbiguityGap = Math.Clamp(s.Matching.AmbiguityGap, 0, 0.5);
        s.Spotify.RedirectPort = Math.Clamp(s.Spotify.RedirectPort, 1024, 65535);
        s.Export.BitrateKbps = Math.Clamp(s.Export.BitrateKbps, 64, 320);
        s.YouTube.ClientId = s.YouTube.ClientId?.Trim();
        s.Spotify.ClientId = s.Spotify.ClientId?.Trim();
    }
}
