using Musilib.Domain.Enums;
using Musilib.Domain.Services;

namespace Musilib.Application.Settings;

/// <summary>
/// Paramètres utilisateur (non secrets), persistés en JSON dans le profil utilisateur.
/// Les secrets (client secret Google, clé API, jetons) sont stockés via <see cref="Abstractions.ISecureStore"/>.
/// </summary>
public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public YouTubeSettings YouTube { get; set; } = new();
    public SpotifySettings Spotify { get; set; } = new();
    public ProcessingSettings Processing { get; set; } = new();
    public MatchingSettings Matching { get; set; } = new();
    public ExportSettings Export { get; set; } = new();

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
    }
}

public sealed class GeneralSettings
{
    public bool DemoMode { get; set; }
    public string Theme { get; set; } = "Dark";
}

public sealed class YouTubeSettings
{
    /// <summary>Identifiant client OAuth "Application de bureau" (Google Cloud Console).</summary>
    public string? ClientId { get; set; }
    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
    public string RevocationEndpoint { get; set; } = "https://oauth2.googleapis.com/revoke";
    public string ApiBaseUrl { get; set; } = "https://www.googleapis.com/youtube/v3/";
    /// <summary>Portée minimale : lecture seule.</summary>
    public string Scope { get; set; } = "https://www.googleapis.com/auth/youtube.readonly";
}

public sealed class SpotifySettings
{
    /// <summary>Client ID de l'application Spotify (flux PKCE : aucun secret requis).</summary>
    public string? ClientId { get; set; }
    /// <summary>Port de redirection loopback ; l'URI http://127.0.0.1:{port}/callback doit être déclarée dans le Dashboard Spotify.</summary>
    public int RedirectPort { get; set; } = 5543;
    public string AuthorizationEndpoint { get; set; } = "https://accounts.spotify.com/authorize";
    public string TokenEndpoint { get; set; } = "https://accounts.spotify.com/api/token";
    public string ApiBaseUrl { get; set; } = "https://api.spotify.com/v1/";
    /// <summary>Portées minimales : lecture des playlists privées, modification des playlists.</summary>
    public string Scope { get; set; } = "playlist-read-private playlist-read-collaborative playlist-modify-private playlist-modify-public";
    /// <summary>Marché utilisé pour la recherche ("from_token" = pays du compte).</summary>
    public string Market { get; set; } = "from_token";

    public string RedirectUri => $"http://127.0.0.1:{RedirectPort}/callback";
}

public sealed class ProcessingSettings
{
    /// <summary>Nombre de traitements exécutés simultanément.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;
    /// <summary>Nombre de requêtes simultanées par traitement (recherches Spotify, exports).</summary>
    public int MaxConcurrentRequests { get; set; } = 4;
    public int MaxRetries { get; set; } = 5;
    /// <summary>Attente maximale acceptée pour un Retry-After avant d'abandonner la requête.</summary>
    public int MaxRetryAfterSeconds { get; set; } = 120;
    public bool AutoResumeJobs { get; set; } = true;
    public int SearchCacheDays { get; set; } = 30;
}

public sealed class MatchingSettings
{
    public double AutoAcceptThreshold { get; set; } = 0.85;
    public double MinimumThreshold { get; set; } = 0.50;
    public double AmbiguityGap { get; set; } = 0.07;
    /// <summary>Nombre de résultats demandés par recherche (10 max pour les applications Spotify en mode développement).</summary>
    public int SearchLimit { get; set; } = 10;
    /// <summary>Applique automatiquement les métadonnées Spotify lors d'une correspondance fiable.</summary>
    public bool ApplySpotifyMetadata { get; set; } = true;

    public MatchThresholds ToThresholds() => new(AutoAcceptThreshold, MinimumThreshold, AmbiguityGap);
}

public sealed class ExportSettings
{
    public string? DefaultFolder { get; set; }
    public ExportOrganization Organization { get; set; } = ExportOrganization.ArtistAlbum;
    public ConflictResolution ConflictResolution { get; set; } = ConflictResolution.Skip;
    public bool WriteTags { get; set; } = true;
    public bool EmbedCover { get; set; } = true;
    public bool WriteM3u { get; set; } = true;
    /// <summary>Chemin de ffmpeg.exe (facultatif ; sinon recherché dans le PATH).</summary>
    public string? FfmpegPath { get; set; }
    /// <summary>Format cible si conversion souhaitée ("" = conserver le format d'origine).</summary>
    public string ConvertTo { get; set; } = string.Empty;
    public int BitrateKbps { get; set; } = 320;
}
