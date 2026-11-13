using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Musilib.Application.Abstractions;
using Musilib.Application.Errors;
using Musilib.Application.Services;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Infrastructure.Media;
using Musilib.Tests.Helpers;

namespace Musilib.Tests.Application;

public class TransferServiceTests
{
    private sealed class RecordingSpotify : ISpotifyClient
    {
        public List<(string Playlist, List<string> Uris)> Adds { get; } = new();
        public int CreateCalls;
        public HashSet<string> RejectedUris { get; } = new();
        public List<string> Existing { get; } = new();
        public int FailAfterAdds = int.MaxValue;

        public Task<IReadOnlyList<SpotifyCandidate>> SearchTracksAsync(string q, int l, int o, CancellationToken ct) => throw new NotSupportedException();
        public Task<SpotifyCandidate?> GetTrackAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SpotifyUser> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult(new SpotifyUser("me", "Me", null));
        public Task<IReadOnlyList<SpotifyPlaylistInfo>> GetMyPlaylistsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SpotifyPlaylistInfo>>([]);

        public Task<SpotifyPlaylistInfo> CreatePlaylistAsync(string name, string? description, bool isPublic, CancellationToken ct)
        {
            CreateCalls++;
            return Task.FromResult(new SpotifyPlaylistInfo("new-pl", name, null, 0, "me", "Me", false, isPublic, "https://open.spotify.com/playlist/new-pl"));
        }

        public Task AddItemsAsync(string playlistId, IReadOnlyList<string> uris, CancellationToken ct)
        {
            Assert.True(uris.Count <= ISpotifyClient.MaxItemsPerAddRequest);
            if (Adds.Count >= FailAfterAdds) throw new HttpRequestException("network down");
            if (uris.Any(RejectedUris.Contains)) throw new ApiException("Spotify", "Invalid base62 id", HttpStatusCode.BadRequest);
            Adds.Add((playlistId, uris.ToList()));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetPlaylistItemUrisAsync(string playlistId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Existing);
    }

    private static async Task<(TempDatabase Db, Playlist Playlist, List<Track> Tracks)> SeedAsync(int count)
    {
        var db = await TempDatabase.CreateAsync();
        var (p, tracks) = await db.SeedPlaylistAsync(count, (t, i) =>
        {
            t.SpotifyUri = $"spotify:track:{i:D4}";
            t.SpotifyTrackId = $"{i:D4}";
            t.MatchStatus = i % 2 == 0 ? MatchStatus.AutoMatched : MatchStatus.Confirmed;
        });
        return (db, p, tracks);
    }

    [Fact]
    public async Task Sends_in_chunks_of_100_preserving_original_order()
    {
        var (db, p, tracks) = await SeedAsync(250);
        await using var _ = db;
        var spotify = new RecordingSpotify();
        var state = new TransferState();
        var service = new TransferService(spotify, db.Tracks, NullLogger<TransferService>.Instance);

        await service.ExecuteAsync(new TransferRequest { PlaylistId = p.Id, NewPlaylistName = "Out" }, state, _ => Task.CompletedTask, (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(1, spotify.CreateCalls);
        Assert.Equal([100, 100, 50], spotify.Adds.Select(a => a.Uris.Count));
        var sent = spotify.Adds.SelectMany(a => a.Uris).ToList();
        Assert.Equal(tracks.OrderBy(t => t.Position).Select(t => t.SpotifyUri), sent);
        Assert.Equal(250, state.Added.Count);
        Assert.Empty(state.Failed);
    }

    [Fact]
    public async Task Ambiguous_excluded_and_unmatched_tracks_are_skipped_with_reasons()
    {
        var (db, p, tracks) = await SeedAsync(4);
        await using var _ = db;
        tracks[0].MatchStatus = MatchStatus.Ambiguous;
        tracks[1].Excluded = true;
        tracks[2].ClearMatch(MatchStatus.NoMatch);
        await db.Tracks.UpsertManyAsync(tracks);
        var spotify = new RecordingSpotify();
        var state = new TransferState();

        await new TransferService(spotify, db.Tracks, NullLogger<TransferService>.Instance)
            .ExecuteAsync(new TransferRequest { PlaylistId = p.Id, NewPlaylistName = "Out" }, state, _ => Task.CompletedTask, (_, _, _) => { }, CancellationToken.None);

        Assert.Single(state.Added);
        Assert.Equal(3, state.Skipped.Count);
        Assert.Contains(state.Skipped, s => s.Reason == "Correspondance non validée");
        Assert.Contains(state.Skipped, s => s.Reason == "Exclu");
    }

    [Fact]
    public async Task Rejected_item_is_isolated_and_others_are_added()
    {
        var (db, p, _) = await SeedAsync(5);
        await using var __ = db;
        var spotify = new RecordingSpotify();
        spotify.RejectedUris.Add("spotify:track:0002");
        var state = new TransferState();

        await new TransferService(spotify, db.Tracks, NullLogger<TransferService>.Instance)
            .ExecuteAsync(new TransferRequest { PlaylistId = p.Id, NewPlaylistName = "Out" }, state, _ => Task.CompletedTask, (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(4, state.Added.Count);
        Assert.Single(state.Failed);
        Assert.Equal(["spotify:track:0000", "spotify:track:0001", "spotify:track:0003", "spotify:track:0004"], spotify.Adds.SelectMany(a => a.Uris));
    }

    [Fact]
    public async Task Resume_after_interruption_does_not_recreate_playlist_nor_duplicate_items()
    {
        var (db, p, _) = await SeedAsync(230);
        await using var __ = db;
        var spotify = new RecordingSpotify { FailAfterAdds = 1 };
        var state = new TransferState();
        var saved = 0;
        var service = new TransferService(spotify, db.Tracks, NullLogger<TransferService>.Instance);
        var request = new TransferRequest { PlaylistId = p.Id, NewPlaylistName = "Out" };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.ExecuteAsync(request, state, _ => { saved++; return Task.CompletedTask; }, (_, _, _) => { }, CancellationToken.None));
        Assert.Equal(100, state.Added.Count);
        Assert.True(saved >= 2);

        // Persistance JSON de l'état (comme dans la table Jobs), puis reprise.
        var restored = Musilib.Application.Common.JsonDefaults.Deserialize<TransferState>(Musilib.Application.Common.JsonDefaults.Serialize(state))!;
        spotify.FailAfterAdds = int.MaxValue;
        await service.ExecuteAsync(request, restored, _ => Task.CompletedTask, (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(1, spotify.CreateCalls);
        Assert.Equal(230, restored.Added.Count);
        Assert.Equal(230, spotify.Adds.SelectMany(a => a.Uris).Distinct().Count());
        Assert.Equal(230, spotify.Adds.Sum(a => a.Uris.Count));
    }

    [Fact]
    public async Task Existing_playlist_skips_items_already_present()
    {
        var (db, p, _) = await SeedAsync(3);
        await using var __ = db;
        var spotify = new RecordingSpotify();
        spotify.Existing.Add("spotify:track:0001");
        var state = new TransferState();

        await new TransferService(spotify, db.Tracks, NullLogger<TransferService>.Instance).ExecuteAsync(
            new TransferRequest { PlaylistId = p.Id, ExistingPlaylistId = "existing", ExistingPlaylistName = "Mine", SkipExisting = true },
            state, _ => Task.CompletedTask, (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(0, spotify.CreateCalls);
        Assert.All(spotify.Adds, a => Assert.Equal("existing", a.Playlist));
        Assert.Equal(2, state.Added.Count);
        Assert.Contains(state.Skipped, s => s.Reason == "Déjà présent dans la playlist");
    }

    [Fact]
    public void BuildPlan_classifies_every_track()
    {
        var plan = TransferService.BuildPlan([
            new Track { Position = 0, MatchStatus = MatchStatus.Confirmed, SpotifyUri = "u" },
            new Track { Position = 1, MatchStatus = MatchStatus.Ambiguous },
            new Track { Position = 2, MatchStatus = MatchStatus.NoMatch },
            new Track { Position = 3, Excluded = true },
            new Track { Position = 4, Availability = SourceAvailability.Private, MatchStatus = MatchStatus.NoMatch },
            new Track { Position = 5, MatchStatus = MatchStatus.Pending }
        ]);
        Assert.Equal(1, plan.Ready);
        Assert.Equal(2, plan.NeedsReview);
        Assert.Equal(1, plan.NoMatch);
        Assert.Equal(1, plan.Excluded);
        Assert.Equal(1, plan.Unavailable);
    }
}

public class ExportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "musilib-export-tests", Guid.NewGuid().ToString("N"));
    private readonly TagLibAudioMetadataService _audio = new();

    private ExportService CreateService(IAudioConverter? converter = null)
    {
        var images = new Mock<IImageDownloader>();
        images.Setup(i => i.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0 }, "image/png"));
        var conv = converter ?? Mock.Of<IAudioConverter>(c => c.IsAvailable == false);
        return new ExportService(_audio, conv, images.Object, NullLogger<ExportService>.Instance);
    }

    private Track TrackWithFile(int position, string artist, string title, string? album = "Album")
    {
        var source = WavFactory.Create(Path.Combine(_root, "source", $"src{position}-{Guid.NewGuid():N}.wav"));
        return new Track
        {
            Position = position,
            LocalFilePath = source,
            Metadata = new TrackMetadata { Title = title, Artists = [artist], Album = album, ReleaseDate = "2024-03-15", Isrc = "FRABC24000" + position, CoverUrl = "https://img/cover.png" }
        };
    }

    private ExportOptions Options(ConflictResolution conflict = ConflictResolution.Skip, ExportOrganization org = ExportOrganization.ArtistAlbum) => new()
    {
        DestinationFolder = Path.Combine(_root, "out"),
        Organization = org,
        Conflict = conflict,
        WriteTags = true,
        EmbedCover = true
    };

    [Fact]
    public async Task Exports_with_artist_title_name_tags_and_preserves_original()
    {
        var service = CreateService();
        var track = TrackWithFile(0, "AC/DC", "Back: In Black?");
        var originalBytes = await File.ReadAllBytesAsync(track.LocalFilePath!);
        var options = Options();

        var plan = await service.PlanAsync([track], options, CancellationToken.None);
        var result = await service.ExportItemAsync(plan[0], options, CancellationToken.None);

        Assert.Equal(JobItemStatus.Completed, result.Status);
        Assert.Equal(Path.Combine(options.DestinationFolder, "AC-DC", "Album", "AC-DC - Back- In Black_.wav"), result.TargetPath);
        Assert.True(File.Exists(result.TargetPath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(track.LocalFilePath!));

        var info = await _audio.ReadAsync(result.TargetPath!);
        Assert.Equal("Back: In Black?", info!.Metadata.Title);
        Assert.Equal(["AC/DC"], info.Metadata.Artists);
        Assert.Equal("2024", info.Metadata.ReleaseDate);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(result.TargetPath!)!, ".musilib-*"));
    }

    [Fact]
    public async Task Detects_duplicates_and_resolves_conflicts()
    {
        var service = CreateService();
        var options = Options(ConflictResolution.KeepBoth, ExportOrganization.None);
        var first = TrackWithFile(0, "Artist", "Song");
        var plan = await service.PlanAsync([first], options, CancellationToken.None);
        await service.ExportItemAsync(plan[0], options, CancellationToken.None);

        // Même morceau (mêmes tags) : doublon ignoré.
        var again = await service.PlanAsync([TrackWithFile(1, "Artist", "Song")], options, CancellationToken.None);
        Assert.Equal(ExportItemAction.Duplicate, again[0].Action);
        Assert.Equal(JobItemStatus.Skipped, (await service.ExportItemAsync(again[0], options, CancellationToken.None)).Status);

        // Fichier différent portant le même nom : conflit → nouveau nom.
        var target = Path.Combine(options.DestinationFolder, "Other - Name.wav");
        WavFactory.Create(target);
        var conflict = await service.PlanAsync([TrackWithFile(2, "Other", "Name")], options, CancellationToken.None);
        Assert.Equal(ExportItemAction.Conflict, conflict[0].Action);
        var result = await service.ExportItemAsync(conflict[0], options, CancellationToken.None);
        Assert.Equal(Path.Combine(options.DestinationFolder, "Other - Name (2).wav"), result.TargetPath);
    }

    [Fact]
    public async Task Skip_mode_does_not_touch_existing_files()
    {
        var service = CreateService();
        var options = Options(ConflictResolution.Skip, ExportOrganization.None);
        var target = Path.Combine(options.DestinationFolder, "X - Y.wav");
        WavFactory.Create(target);
        var before = File.GetLastWriteTimeUtc(target);

        var plan = await service.PlanAsync([TrackWithFile(0, "X", "Y")], options, CancellationToken.None);
        var result = await service.ExportItemAsync(plan[0], options, CancellationToken.None);

        Assert.Equal(JobItemStatus.Skipped, result.Status);
        Assert.Equal(before, File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public async Task Name_collisions_inside_one_export_get_unique_names()
    {
        var service = CreateService();
        var options = Options(org: ExportOrganization.None);
        var plan = await service.PlanAsync([TrackWithFile(0, "Same", "Song"), TrackWithFile(1, "Same", "Song")], options, CancellationToken.None);
        Assert.NotEqual(plan[0].TargetPath, plan[1].TargetPath);
        Assert.EndsWith("Same - Song (2).wav", plan[1].TargetPath);
    }

    [Fact]
    public async Task Tracks_without_files_are_skipped_and_missing_sources_fail()
    {
        var service = CreateService();
        var options = Options();
        var plan = await service.PlanAsync([
            new Track { Position = 0, Metadata = new TrackMetadata { Title = "A", Artists = ["B"] } },
            new Track { Position = 1, LocalFilePath = Path.Combine(_root, "missing.mp3"), Metadata = new TrackMetadata { Title = "C", Artists = ["D"] } }
        ], options, CancellationToken.None);

        Assert.Equal(ExportItemAction.NoSource, plan[0].Action);
        Assert.Equal(ExportItemAction.SourceMissing, plan[1].Action);
        Assert.Equal(JobItemStatus.Skipped, (await service.ExportItemAsync(plan[0], options, CancellationToken.None)).Status);
        Assert.Equal(JobItemStatus.Failed, (await service.ExportItemAsync(plan[1], options, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Conversion_falls_back_to_original_format_without_ffmpeg()
    {
        var service = CreateService(Mock.Of<IAudioConverter>(c => c.IsAvailable == false));
        var options = Options(org: ExportOrganization.None);
        options.ConvertTo = "mp3";
        var plan = await service.PlanAsync([TrackWithFile(0, "A", "B")], options, CancellationToken.None);
        Assert.Equal(ExportItemAction.Convert, plan[0].Action);

        var result = await service.ExportItemAsync(plan[0], options, CancellationToken.None);
        Assert.Equal(JobItemStatus.Completed, result.Status);
        Assert.EndsWith(".wav", result.TargetPath);
        Assert.Contains("FFmpeg", result.Message);
    }

    [Fact]
    public async Task Writes_m3u_with_relative_paths_in_order()
    {
        var folder = Path.Combine(_root, "m3u");
        Directory.CreateDirectory(folder);
        var t1 = new Track { Position = 1, Metadata = new TrackMetadata { Title = "Second", Artists = ["B"], DurationMs = 61_000 } };
        var t0 = new Track { Position = 0, Metadata = new TrackMetadata { Title = "First", Artists = ["A"], DurationMs = 120_000 } };
        var file = await ExportService.WriteM3uAsync(folder, "My: List", [(t1, Path.Combine(folder, "B", "b.mp3")), (t0, Path.Combine(folder, "a.mp3"))], CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(file);
        Assert.EndsWith("My- List.m3u8", file);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#EXTINF:120,A - First", lines[2]);
        Assert.Equal("a.mp3", lines[3]);
        Assert.Equal("B/b.mp3", lines[5]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class FfmpegArgumentsTests
{
    [Theory]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("flac", "flac")]
    [InlineData("m4a", "aac")]
    [InlineData("opus", "libopus")]
    public void Builds_codec_arguments(string format, string codec)
    {
        var args = FfmpegAudioConverter.BuildArguments("in.wav", "out." + format, format, 256);
        Assert.Contains(codec, args);
        Assert.Equal("out." + format, args[^1]);
        Assert.Contains("-map_metadata", args);
    }

    [Fact]
    public void Unknown_format_is_rejected() =>
        Assert.Throws<NotSupportedException>(() => FfmpegAudioConverter.BuildArguments("a", "b", "xyz", 128));
}
