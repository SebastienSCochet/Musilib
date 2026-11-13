using Musilib.Domain.Entities;
using Musilib.Domain.Enums;

namespace Musilib.Application.Abstractions;

public sealed record RemotePlaylist(
    string Id,
    string Title,
    string? Description,
    string? ThumbnailUrl,
    int ItemCount,
    string? Owner);

public sealed record RemotePlaylistItem(
    string ItemId,
    string VideoId,
    int Position,
    string Title,
    string? ChannelTitle,
    string? ThumbnailUrl,
    SourceAvailability Availability,
    long? DurationMs);

/// <summary>Accès en lecture aux playlists YouTube (YouTube Data API v3).</summary>
public interface IYouTubeClient
{
    /// <summary>Nom de la chaîne de l'utilisateur connecté.</summary>
    Task<string?> GetChannelTitleAsync(CancellationToken ct);

    /// <summary>Playlists de l'utilisateur connecté (pagination gérée).</summary>
    Task<IReadOnlyList<RemotePlaylist>> GetMyPlaylistsAsync(CancellationToken ct);

    /// <summary>Informations d'une playlist (privée si l'utilisateur y a accès, ou publique).</summary>
    Task<RemotePlaylist?> GetPlaylistAsync(string playlistId, CancellationToken ct);

    /// <summary>Tous les éléments de la playlist dans l'ordre d'origine (pagination gérée).</summary>
    IAsyncEnumerable<RemotePlaylistItem> GetPlaylistItemsAsync(string playlistId, CancellationToken ct);
}

public sealed record SpotifyUser(string Id, string DisplayName, string? ImageUrl);

public sealed record SpotifyPlaylistInfo(
    string Id,
    string Name,
    string? ImageUrl,
    int TrackCount,
    string OwnerId,
    string? OwnerName,
    bool Collaborative,
    bool IsPublic,
    string? ExternalUrl);

/// <summary>Spotify Web API : recherche et gestion de playlists.</summary>
public interface ISpotifyClient
{
    /// <summary>Taille maximale d'un lot d'ajout à une playlist (limite de l'API).</summary>
    const int MaxItemsPerAddRequest = 100;

    Task<IReadOnlyList<SpotifyCandidate>> SearchTracksAsync(string query, int limit, int offset, CancellationToken ct);
    Task<SpotifyCandidate?> GetTrackAsync(string trackId, CancellationToken ct);
    Task<SpotifyUser> GetCurrentUserAsync(CancellationToken ct);
    /// <summary>Playlists de l'utilisateur (pagination gérée).</summary>
    Task<IReadOnlyList<SpotifyPlaylistInfo>> GetMyPlaylistsAsync(CancellationToken ct);
    Task<SpotifyPlaylistInfo> CreatePlaylistAsync(string name, string? description, bool isPublic, CancellationToken ct);
    /// <summary>Ajoute au plus 100 éléments, dans l'ordre fourni.</summary>
    Task AddItemsAsync(string playlistId, IReadOnlyList<string> uris, CancellationToken ct);
    /// <summary>URIs déjà présentes dans une playlist dont l'utilisateur est propriétaire/collaborateur.</summary>
    Task<IReadOnlyList<string>> GetPlaylistItemUrisAsync(string playlistId, CancellationToken ct);
}
