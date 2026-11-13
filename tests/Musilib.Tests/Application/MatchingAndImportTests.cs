using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Musilib.Application.Abstractions;
using Musilib.Application.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Tests.Helpers;

namespace Musilib.Tests.Application;

public class MatchingServiceTests
{
    private static MatchingService Create(TempDatabase db, ISpotifyClient spotify) =>
        new(spotify, db.Tracks, db.Cache, new TestSettingsService(), db.History, NullLogger<MatchingService>.Instance);

    [Fact]
    public async Task Auto_matches_and_persists_candidates()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (_, tracks) = await db.SeedPlaylistAsync(1);
        var spotify = new Mock<ISpotifyClient>();
        spotify.Setup(s => s.SearchTracksAsync(It.IsAny<string>(), It.IsAny<int>(), 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Candidates.Make("sp1", "Song 0", "Artist 0", 200_000, isrc: "I1")]);

        var decision = await Create(db, spotify.Object).MatchTrackAsync(tracks[0], false, CancellationToken.None);

        Assert.Equal(MatchStatus.AutoMatched, decision.Status);
        var saved = await db.Tracks.GetAsync(tracks[0].Id);
        Assert.Equal("spotify:track:sp1", saved!.SpotifyUri);
        Assert.Equal("I1", saved.Metadata.Isrc);
        Assert.Single(await db.Tracks.GetCandidatesAsync(tracks[0].Id));
    }

    [Fact]
    public async Task Search_cache_prevents_duplicate_api_calls()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (_, tracks) = await db.SeedPlaylistAsync(1);
        var spotify = new Mock<ISpotifyClient>();
        spotify.Setup(s => s.SearchTracksAsync(It.IsAny<string>(), It.IsAny<int>(), 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Candidates.Make("sp1", "Song 0", "Artist 0", 200_000)]);
        var service = Create(db, spotify.Object);

        await service.MatchTrackAsync(tracks[0], true, CancellationToken.None);
        var callsAfterFirst = spotify.Invocations.Count;
        await service.MatchTrackAsync(tracks[0], true, CancellationToken.None);

        Assert.Equal(callsAfterFirst, spotify.Invocations.Count);
        Assert.True(await db.Cache.CountAsync() >= 1);
    }

    [Fact]
    public async Task Ambiguous_results_are_never_silently_validated()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (_, tracks) = await db.SeedPlaylistAsync(1);
        var spotify = new Mock<ISpotifyClient>();
        spotify.Setup(s => s.SearchTracksAsync(It.IsAny<string>(), It.IsAny<int>(), 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Candidates.Make("a", "Song 0", "Artist 0", 200_000, isrc: "A"),
                Candidates.Make("b", "Song 0", "Artist 0", 205_000, isrc: "B")
            ]);

        var decision = await Create(db, spotify.Object).MatchTrackAsync(tracks[0], false, CancellationToken.None);

        Assert.Equal(MatchStatus.Ambiguous, decision.Status);
        var saved = await db.Tracks.GetAsync(tracks[0].Id);
        Assert.Null(saved!.SpotifyUri);
        Assert.False(saved.HasSpotifyMatch);
        Assert.Equal(2, (await db.Tracks.GetCandidatesAsync(saved.Id)).Count);
    }

    [Fact]
    public async Task Unavailable_videos_do_not_call_the_api()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (_, tracks) = await db.SeedPlaylistAsync(1, (t, _) => { t.Availability = SourceAvailability.Deleted; t.SourceTitle = "Deleted video"; });
        var spotify = new Mock<ISpotifyClient>(MockBehavior.Strict);

        var decision = await Create(db, spotify.Object).MatchTrackAsync(tracks[0], false, CancellationToken.None);

        Assert.Equal(MatchStatus.NoMatch, decision.Status);
        Assert.Contains("supprimée", (await db.Tracks.GetAsync(tracks[0].Id))!.LastError);
    }

    [Fact]
    public async Task Manual_selection_confirms_and_records_history()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (_, tracks) = await db.SeedPlaylistAsync(1);
        var service = Create(db, Mock.Of<ISpotifyClient>());
        var candidate = Candidates.Make("x", "Song 0 (Live)", "Artist 0", 260_000);

        await service.SelectCandidateAsync(tracks[0], candidate, applyMetadata: false, CancellationToken.None);

        var saved = await db.Tracks.GetAsync(tracks[0].Id);
        Assert.Equal(MatchStatus.Confirmed, saved!.MatchStatus);
        Assert.Equal("Song 0", saved.Metadata.Title);
        Assert.Single(await db.History.GetRecentAsync(10, HistoryKind.Match));
    }

    [Fact]
    public void Search_queries_go_from_precise_to_broad()
    {
        var track = new Track { Metadata = new TrackMetadata { Title = "Midnight Static (Official Video)", Artists = ["Neon Harbor"] } };
        var queries = MatchingService.BuildSearchQueries(track);
        Assert.Equal("track:\"midnight static\" artist:\"neon harbor\"", queries[0]);
        Assert.Equal("neon harbor midnight static", queries[1]);

        track.CustomSearchQuery = "custom query";
        Assert.Equal(["custom query"], MatchingService.BuildSearchQueries(track));
    }
}

public class PlaylistImportServiceTests
{
    private sealed class FakeYouTube : IYouTubeClient
    {
        public List<RemotePlaylistItem> Items { get; set; } = new();
        public Task<string?> GetChannelTitleAsync(CancellationToken ct) => Task.FromResult<string?>("me");
        public Task<IReadOnlyList<RemotePlaylist>> GetMyPlaylistsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemotePlaylist>>([]);
        public Task<RemotePlaylist?> GetPlaylistAsync(string playlistId, CancellationToken ct) =>
            Task.FromResult<RemotePlaylist?>(new RemotePlaylist(playlistId, "My list", null, null, Items.Count, "me"));

        public async IAsyncEnumerable<RemotePlaylistItem> GetPlaylistItemsAsync(string playlistId, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var i in Items)
            {
                await Task.Yield();
                yield return i;
            }
        }
    }

    private static RemotePlaylistItem Item(int pos, string title, SourceAvailability a = SourceAvailability.Available) =>
        new("item" + pos, "vid" + pos, pos, title, "Channel", null, a, 180_000);

    [Fact]
    public async Task Import_preserves_order_and_availability_then_resync_keeps_user_edits()
    {
        await using var db = await TempDatabase.CreateAsync();
        var yt = new FakeYouTube { Items = [Item(0, "A - One"), Item(1, "Deleted video", SourceAvailability.Deleted), Item(2, "C - Three")] };
        var service = new PlaylistImportService(yt, db.Playlists, db.Tracks, db.History, NullLogger<PlaylistImportService>.Instance,
            new PlaylistSourceContext(PlaylistSource.YouTube));

        var first = await service.ImportAsync("PL1", null, CancellationToken.None);
        var tracks = await db.Tracks.GetByPlaylistAsync(first.Playlist.Id);
        Assert.Equal(["One", "Deleted video", "Three"], tracks.Select(t => t.Metadata.Title));
        Assert.Equal(SourceAvailability.Deleted, tracks[1].Availability);
        Assert.Equal(1, first.Unavailable);

        // L'utilisateur édite un morceau, puis la playlist change (réordonnée, un élément retiré, un ajouté).
        tracks[2].Metadata.Title = "Edited";
        tracks[2].MetadataEdited = true;
        await db.Tracks.UpdateAsync(tracks[2]);
        yt.Items = [Item(2, "C - Three"), Item(0, "A - One"), Item(3, "D - Four")];

        var second = await service.ImportAsync("PL1", null, CancellationToken.None);
        var after = await db.Tracks.GetByPlaylistAsync(second.Playlist.Id);

        Assert.Equal(first.Playlist.Id, second.Playlist.Id);
        Assert.Equal(1, second.Added);
        Assert.Equal(1, second.Removed);
        Assert.Equal(["Edited", "One", "Four", "Deleted video"], after.Select(t => t.Metadata.Title));
        Assert.True(after[3].RemovedFromSource);
    }
}

public class PlaylistIdParserTests
{
    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLabcdefghijKLMN", "PLabcdefghijKLMN")]
    [InlineData("https://youtube.com/watch?v=xyz&list=PLqwertyuiop12345&index=3", "PLqwertyuiop12345")]
    [InlineData("PL1234567890abcdef", "PL1234567890abcdef")]
    [InlineData("  OLAK5uy_abcdefghij12345  ", "OLAK5uy_abcdefghij12345")]
    public void Parses_urls_and_ids(string input, string expected)
    {
        Assert.True(PlaylistIdParser.TryParse(input, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("not a playlist")]
    public void Rejects_invalid_input(string input) => Assert.False(PlaylistIdParser.TryParse(input, out _));
}

public class TrackEditingServiceTests
{
    [Fact]
    public async Task Saving_metadata_marks_track_as_edited_and_validates_date()
    {
        await using var db = await TempDatabase.CreateAsync();
        var (_, tracks) = await db.SeedPlaylistAsync(1);
        var service = new TrackEditingService(db.Tracks, db.History, new Musilib.Infrastructure.Media.TagLibAudioMetadataService());

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveMetadataAsync(tracks[0],
            new TrackMetadata { Title = "T", Artists = ["A"], ReleaseDate = "13/2024" }, CancellationToken.None));

        await service.SaveMetadataAsync(tracks[0], new TrackMetadata { Title = " New ", Artists = [" A ", "a", "B"], ReleaseDate = "2024-05" }, CancellationToken.None);
        var saved = await db.Tracks.GetAsync(tracks[0].Id);
        Assert.True(saved!.MetadataEdited);
        Assert.Equal(MetadataOrigin.Manual, saved.MetadataOrigin);
        Assert.Equal(["A", "B"], saved.Metadata.Artists);
    }
}
