using System.Security.Cryptography;
using System.Text;
using Musilib.Domain.Enums;

namespace Musilib.Infrastructure.Demo;

/// <summary>Données entièrement fictives (artistes, titres, albums) utilisées par le mode démonstration.</summary>
public static class DemoCatalog
{
    public sealed record Song(string Key, string Artist, string Title, string Album, string ReleaseDate, int DurationSec,
        string? Featuring = null, bool HasSingle = false, bool HasLive = false, bool HasRemix = false, bool OnSpotify = true, string? Genre = null);

    public sealed record VideoEntry(string SongKey, string VideoTitle, string Channel, int DurationOffsetSec = 0,
        SourceAvailability Availability = SourceAvailability.Available);

    public sealed record DemoPlaylist(string Id, string Title, string Description, IReadOnlyList<VideoEntry> Items);

    public static readonly IReadOnlyList<Song> Songs =
    [
        new("nh-static", "Neon Harbor", "Midnight Static", "Coastal Frequencies", "2024-03-15", 214, HasSingle: true, Genre: "Synthwave"),
        new("nh-glass", "Neon Harbor", "Glass Horizon", "Coastal Frequencies", "2024-03-15", 241, HasLive: true, Genre: "Synthwave"),
        new("nh-tide", "Neon Harbor", "Low Tide Lights", "Coastal Frequencies", "2024-03-15", 198, Genre: "Synthwave"),
        new("lr-velours", "Lumière Rouge", "Velours", "Cinéma Intérieur", "2023-10-06", 187, HasSingle: true, HasRemix: true, Genre: "Pop"),
        new("lr-paris", "Lumière Rouge", "Paris à l'envers", "Cinéma Intérieur", "2023-10-06", 203, Genre: "Pop"),
        new("lr-encore", "Lumière Rouge", "Encore une nuit", "Cinéma Intérieur", "2023-10-06", 226, Featuring: "Clara Vey", Genre: "Pop"),
        new("vs-orbit", "The Velvet Satellites", "Orbit Season", "Gravity Club", "2022-06-24", 232, HasLive: true, Genre: "Indie Rock"),
        new("vs-wires", "The Velvet Satellites", "Copper Wires", "Gravity Club", "2022-06-24", 205, Genre: "Indie Rock"),
        new("vs-home", "The Velvet Satellites", "Signal Home", "Gravity Club", "2022-06-24", 259, Genre: "Indie Rock"),
        new("km-sakura", "Kaito Mori", "Sakura Drive", "Night Bloom", "2025-04-02", 176, HasSingle: true, Genre: "Lo-fi"),
        new("km-rain", "Kaito Mori", "Rain on Ginza", "Night Bloom", "2025-04-02", 164, Genre: "Lo-fi"),
        new("as-sol", "Amaya Sol", "Sol de Medianoche", "Brisa", "2024-07-19", 199, HasRemix: true, Genre: "Latin Pop"),
        new("as-mar", "Amaya Sol", "Mar Abierto", "Brisa", "2024-07-19", 211, Featuring: "Orbit & Ash", Genre: "Latin Pop"),
        new("ne-north", "Northbound Echo", "True North", "Compass", "2021-11-12", 247, HasLive: true, Genre: "Folk"),
        new("ne-pines", "Northbound Echo", "Under the Pines", "Compass", "2021-11-12", 221, Genre: "Folk"),
        new("cv-mirror", "Clara Vey", "Mirror Talk", "Paper Moons", "2025-01-31", 193, HasSingle: true, Genre: "Pop"),
        new("cv-quiet", "Clara Vey", "Quiet Hours", "Paper Moons", "2025-01-31", 236, Genre: "Pop"),
        new("sg-bloom", "Static Gardens", "Bloom", "Overgrown", "2023-05-05", 268, Genre: "Electronic"),
        new("sg-ivy", "Static Gardens", "Ivy Circuit", "Overgrown", "2023-05-05", 302, HasRemix: true, Genre: "Electronic"),
        new("nb-aube", "Les Nuits Blanches", "L'aube électrique", "Insomnies", "2022-09-30", 218, Genre: "Électro-pop"),
        new("nb-metro", "Les Nuits Blanches", "Dernier métro", "Insomnies", "2022-09-30", 196, Genre: "Électro-pop"),
        new("oa-ember", "Orbit & Ash", "Ember", "Afterglow", "2024-11-08", 184, HasSingle: true, Genre: "Alt R&B"),
        new("oa-falling", "Orbit & Ash", "Falling Slowly Upward", "Afterglow", "2024-11-08", 229, Genre: "Alt R&B"),
        new("mo-gold", "Mila Okafor", "Gold Dust", "Lagos Nights", "2025-06-13", 201, Featuring: "Kaito Mori", Genre: "Afrobeats"),
        new("mo-heat", "Mila Okafor", "Heatwave", "Lagos Nights", "2025-06-13", 188, HasRemix: true, Genre: "Afrobeats"),
        new("pc-kites", "Paper Comets", "Kites", "Small Hours", "2020-02-21", 207, Genre: "Indie Pop"),
        // Morceau homonyme d'un autre artiste : crée une ambiguïté.
        new("pc-bloom", "Paper Comets", "Bloom", "Small Hours", "2020-02-21", 255, Genre: "Indie Pop"),
        // Absents de Spotify : aucune correspondance possible.
        new("xx-garage", "The Basement Tapes Club", "Garage Demo #4", "Demos", "2019-01-01", 172, OnSpotify: false),
        new("xx-cover", "Lucas Ferreira", "Midnight Static (acoustic cover)", "Covers", "2025-02-02", 190, OnSpotify: false)
    ];

    public static readonly IReadOnlyList<DemoPlaylist> Playlists =
    [
        new("PLdemoRoadTrip2026xxxxxxxx", "Road Trip 2026", "Les titres pour la route cet été.",
        [
            new("nh-static", "Neon Harbor - Midnight Static (Official Video)", "Neon Harbor", 12),
            new("lr-velours", "Lumière Rouge - Velours [Clip Officiel]", "LumiereRougeVEVO", 4),
            new("vs-orbit", "The Velvet Satellites - Orbit Season (Lyrics)", "Lyrics Planet", 1),
            new("km-sakura", "Sakura Drive", "Kaito Mori - Topic"),
            new("as-sol", "Amaya Sol – Sol de Medianoche (Video Oficial) HD", "Amaya Sol", 9),
            new("ne-north", "Northbound Echo \"True North\" live at the Lighthouse", "Northbound Echo", 35),
            new("cv-mirror", "Clara Vey - Mirror Talk (Official Audio)", "Clara Vey"),
            new("sg-bloom", "Bloom", "Static Gardens - Topic"),
            new("xx-deleted", "Deleted video", "", 0, SourceAvailability.Deleted),
            new("oa-ember", "Orbit & Ash - Ember (Visualizer)", "Orbit & Ash", 2),
            new("mo-gold", "Mila Okafor ft. Kaito Mori - Gold Dust (Official Music Video)", "Mila Okafor", 21),
            new("nb-aube", "Les Nuits Blanches - L'aube électrique", "Les Nuits Blanches"),
            new("xx-private", "Private video", "", 0, SourceAvailability.Private),
            new("nh-glass", "Neon Harbor - Glass Horizon", "Neon Harbor", 1),
            new("xx-garage", "The Basement Tapes Club - Garage Demo #4", "The Basement Tapes Club"),
            new("pc-kites", "Paper Comets | Kites | 4K", "Paper Comets", 3)
        ]),
        new("PLdemoChillDuSoirxxxxxxxx", "Chill du soir", "Ambiance douce pour fin de journée.",
        [
            new("km-rain", "Kaito Mori - Rain on Ginza (lofi)", "Kaito Mori"),
            new("ne-pines", "Under the Pines", "Northbound Echo - Topic"),
            new("cv-quiet", "Clara Vey – Quiet Hours (Lyric Video)", "Clara Vey", 2),
            new("lr-encore", "Lumière Rouge feat. Clara Vey - Encore une nuit", "Lumière Rouge", 3),
            new("nh-tide", "Neon Harbor - Low Tide Lights (Official Visualizer)", "Neon Harbor"),
            new("oa-falling", "Orbit & Ash - Falling Slowly Upward", "OrbitAndAshVEVO", 5),
            new("vs-home", "The Velvet Satellites - Signal Home", "The Velvet Satellites"),
            new("xx-cover", "Midnight Static - Neon Harbor (acoustic cover by Lucas Ferreira)", "Lucas Ferreira"),
            new("pc-bloom", "Bloom", "Paper Comets", 1),
            new("as-mar", "Amaya Sol - Mar Abierto ft. Orbit & Ash", "Amaya Sol", 2)
        ]),
        new("PLdemoDecouvertesIndexxxx", "Découvertes indé", "Pépites repérées en 2025.",
        [
            new("vs-wires", "The Velvet Satellites - Copper Wires (Official Video)", "The Velvet Satellites", 15),
            new("nb-metro", "Les Nuits Blanches - Dernier Métro (clip)", "Les Nuits Blanches", 6),
            new("sg-ivy", "Static Gardens - Ivy Circuit", "Static Gardens"),
            new("lr-paris", "Lumière Rouge - Paris à l'envers", "Lumière Rouge"),
            new("mo-heat", "Mila Okafor - Heatwave", "Mila Okafor", 1),
            new("pc-kites", "Kites", "Paper Comets - Topic")
        ]),
        new("PLdemoWorkoutMixxxxxxxxxx", "Workout Mix", "Énergie maximale.",
        [
            new("mo-heat", "Mila Okafor - Heatwave (Official Video)", "Mila Okafor", 8),
            new("sg-ivy", "Static Gardens - Ivy Circuit (Remix)", "Static Gardens"),
            new("as-sol", "Amaya Sol - Sol de Medianoche", "Amaya Sol"),
            new("nh-static", "Midnight Static", "Neon Harbor - Topic"),
            new("oa-ember", "ORBIT & ASH - EMBER", "Orbit & Ash")
        ])
    ];

    public static Song? FindSong(string key) => Songs.FirstOrDefault(s => s.Key == key);

    /// <summary>Identifiant pseudo-Spotify déterministe (22 caractères base62).</summary>
    public static string SpotifyId(string seed)
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var sb = new StringBuilder(22);
        for (var i = 0; i < 22; i++) sb.Append(alphabet[hash[i] % alphabet.Length]);
        return sb.ToString();
    }

    public static string VideoId(string seed) => SpotifyId("yt:" + seed)[..11];

    public static string Isrc(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("isrc:" + key));
        var number = BitConverter.ToUInt32(hash, 0) % 100000;
        return "FRMSL25" + number.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
    }
}
