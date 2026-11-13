using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Domain.Services;
using Musilib.Tests.Helpers;

namespace Musilib.Tests.Domain;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("  Hello,   World!! ", "hello world")]
    [InlineData("Simon & Garfunkel", "simon and garfunkel")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_removes_accents_punctuation_and_case(string? input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Theory]
    [InlineData("Song (feat. Someone)", "song")]
    [InlineData("Song [Official Video]", "song")]
    [InlineData("Song - Remastered 2011", "song")]
    [InlineData("Song ft. Other Artist", "song")]
    [InlineData("Song - Radio Edit", "song")]
    public void CoreTitle_strips_annotations(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.CoreTitle(input));

    [Fact]
    public void VersionTags_detects_live_and_remix()
    {
        Assert.Contains("live", TextNormalizer.VersionTags("Song (Live at Wembley)"));
        Assert.Contains("remix", TextNormalizer.VersionTags("Song - Artist Remix"));
        Assert.Empty(TextNormalizer.VersionTags("Song - Remastered 2011"));
    }
}

public class YouTubeTitleParserTests
{
    [Theory]
    [InlineData("Neon Harbor - Midnight Static (Official Video)", "Neon", "Neon Harbor", "Midnight Static")]
    [InlineData("Lumière Rouge - Velours [Clip Officiel]", null, "Lumière Rouge", "Velours")]
    [InlineData("Amaya Sol – Sol de Medianoche (Video Oficial) HD", null, "Amaya Sol", "Sol de Medianoche")]
    [InlineData("Paper Comets | Kites | 4K", null, "Paper Comets", "Kites")]
    public void Parses_artist_and_title_from_separators(string title, string? channel, string expectedArtist, string expectedTitle)
    {
        var parsed = YouTubeTitleParser.Parse(title, channel);
        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedArtist, parsed.Artists[0]);
        Assert.False(parsed.ArtistFromChannel);
    }

    [Theory]
    [InlineData("Sakura Drive", "Kaito Mori - Topic", "Kaito Mori")]
    [InlineData("Mirror Talk", "ClaraVeyVEVO", "ClaraVey")]
    public void Uses_channel_when_no_separator(string title, string channel, string expectedArtist)
    {
        var parsed = YouTubeTitleParser.Parse(title, channel);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(expectedArtist, parsed.Artists.Single());
        Assert.True(parsed.ArtistFromChannel);
    }

    [Fact]
    public void Extracts_featured_artists_from_title_and_artist_part()
    {
        var a = YouTubeTitleParser.Parse("Mila Okafor ft. Kaito Mori - Gold Dust (Official Music Video)", null);
        Assert.Equal("Gold Dust", a.Title);
        Assert.Equal(new[] { "Mila Okafor", "Kaito Mori" }, a.Artists);

        var b = YouTubeTitleParser.Parse("Amaya Sol - Mar Abierto ft. Orbit & Ash", null);
        Assert.Equal("Mar Abierto", b.Title);
        Assert.Equal(new[] { "Amaya Sol", "Orbit & Ash" }, b.Artists);
    }

    [Fact]
    public void Keeps_ampersand_in_artist_names()
    {
        var parsed = YouTubeTitleParser.Parse("Simon & Garfunkel - The Boxer", null);
        Assert.Equal("Simon & Garfunkel", parsed.Artists.Single());
    }

    [Fact]
    public void Splits_comma_and_x_collaborations()
    {
        var parsed = YouTubeTitleParser.Parse("Artist A, Artist B x Artist C - Title", null);
        Assert.Equal(new[] { "Artist A", "Artist B", "Artist C" }, parsed.Artists);
    }

    [Fact]
    public void Parses_quoted_titles()
    {
        var parsed = YouTubeTitleParser.Parse("Northbound Echo \"True North\" live at the Lighthouse", null);
        Assert.Equal("Northbound Echo", parsed.Artists.Single());
        Assert.Equal("True North", parsed.Title);
    }
}

public class StringSimilarityTests
{
    [Fact]
    public void Levenshtein_distance_is_correct()
    {
        Assert.Equal(3, StringSimilarity.Levenshtein("kitten", "sitting"));
        Assert.Equal(0, StringSimilarity.Levenshtein("abc", "abc"));
        Assert.Equal(3, StringSimilarity.Levenshtein("", "abc"));
    }

    [Fact]
    public void TokenSetRatio_ignores_word_order()
    {
        Assert.True(StringSimilarity.TokenSetRatio("Static Midnight", "Midnight Static") > 0.99);
        Assert.True(StringSimilarity.TokenSetRatio("hello world", "goodbye moon") < 0.5);
    }
}

public class MatchScorerTests
{
    private static MatchQuery Query(string title = "Midnight Static", string artist = "Neon Harbor", long? duration = 214_000) =>
        new(title, [artist], duration);

    [Fact]
    public void Perfect_match_scores_near_one()
    {
        var c = Candidates.Make("1", "Midnight Static", "Neon Harbor", 214_000);
        MatchScorer.Score(Query(), c);
        Assert.True(c.Score > 0.97, $"score {c.Score}");
        Assert.Equal(1, c.DurationScore);
    }

    [Fact]
    public void Duration_difference_lowers_score()
    {
        var close = Candidates.Make("1", "Midnight Static", "Neon Harbor", 216_000);
        var far = Candidates.Make("2", "Midnight Static", "Neon Harbor", 300_000);
        MatchScorer.Score(Query(), close);
        MatchScorer.Score(Query(), far);
        Assert.True(close.Score > far.Score);
        Assert.Equal(0, far.DurationScore);
    }

    [Fact]
    public void Different_version_is_penalised()
    {
        var studio = Candidates.Make("1", "Midnight Static", "Neon Harbor", 214_000);
        var live = Candidates.Make("2", "Midnight Static - Live", "Neon Harbor", 214_000);
        MatchScorer.Score(Query(), studio);
        MatchScorer.Score(Query(), live);
        Assert.True(studio.TitleScore > live.TitleScore);
    }

    [Fact]
    public void Unrelated_title_cannot_score_high()
    {
        var c = Candidates.Make("1", "Completely Different Song", "Neon Harbor", 214_000);
        MatchScorer.Score(Query(), c);
        Assert.True(c.Score <= 0.45);
    }

    [Fact]
    public void Missing_duration_reweights_title_and_artist()
    {
        var c = Candidates.Make("1", "Midnight Static", "Neon Harbor", 214_000);
        MatchScorer.Score(Query(duration: null), c);
        Assert.True(c.Score > 0.97);
    }

    [Fact]
    public void Matching_isrc_is_strong_evidence()
    {
        var c = Candidates.Make("1", "Midnight Static (2024 Mix)", "N. Harbor", 230_000, isrc: "FRABC2400001");
        MatchScorer.Score(new MatchQuery("Midnight Static", ["Neon Harbor"], 214_000, "FRABC2400001"), c);
        Assert.True(c.Score >= 0.98);
    }

    [Fact]
    public void Decide_returns_auto_for_single_strong_match()
    {
        var decision = MatchScorer.Decide(Query(), [
            Candidates.Make("1", "Midnight Static", "Neon Harbor", 214_000),
            Candidates.Make("2", "Something Else", "Other", 180_000)
        ]);
        Assert.Equal(MatchStatus.AutoMatched, decision.Status);
        Assert.Equal("1", decision.Best!.SpotifyId);
    }

    [Fact]
    public void Decide_treats_single_and_album_versions_as_equivalent()
    {
        var decision = MatchScorer.Decide(Query(), [
            Candidates.Make("album", "Midnight Static", "Neon Harbor", 214_000, isrc: "X1", album: "Coastal"),
            Candidates.Make("single", "Midnight Static", "Neon Harbor", 214_500, isrc: "X1", album: "Midnight Static")
        ]);
        Assert.Equal(MatchStatus.AutoMatched, decision.Status);
    }

    [Fact]
    public void Decide_never_auto_validates_close_rivals()
    {
        // Deux enregistrements différents, scores très proches : ambigu.
        var decision = MatchScorer.Decide(Query("Bloom", "Static Gardens", 260_000), [
            Candidates.Make("a", "Bloom", "Static Gardens", 268_000, isrc: "A"),
            Candidates.Make("b", "Bloom", "Static Gardens", 255_000, isrc: "B")
        ]);
        Assert.Equal(MatchStatus.Ambiguous, decision.Status);
    }

    [Fact]
    public void Decide_intermediate_score_is_ambiguous()
    {
        var decision = MatchScorer.Decide(Query(), [Candidates.Make("1", "Midnight Static", "Harbor Lights Tribute Band", 219_000)]);
        Assert.Equal(MatchStatus.Ambiguous, decision.Status);
    }

    [Fact]
    public void Decide_returns_nomatch_without_results_or_low_scores()
    {
        Assert.Equal(MatchStatus.NoMatch, MatchScorer.Decide(Query(), []).Status);
        Assert.Equal(MatchStatus.NoMatch, MatchScorer.Decide(Query(), [Candidates.Make("1", "Totally Unrelated", "Nobody", 100_000)]).Status);
    }

    [Fact]
    public void Decide_respects_custom_thresholds()
    {
        // Durée décalée de 4 s : score ≈ 0.98, fiable par défaut mais pas avec un seuil de 0.99.
        var candidates = new[] { Candidates.Make("1", "Midnight Static", "Neon Harbor", 218_000) };
        Assert.Equal(MatchStatus.AutoMatched, MatchScorer.Decide(Query(), candidates).Status);
        var strict = MatchScorer.Decide(Query(), candidates, new MatchThresholds(AutoAccept: 0.99));
        Assert.Equal(MatchStatus.Ambiguous, strict.Status);
    }
}

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("AC/DC: Back in Black?", "AC-DC- Back in Black_")]
    [InlineData("What \"is\" <this>|", "What 'is' _this_-")]
    [InlineData("trailing dots...  ", "trailing dots")]
    [InlineData("CON", "_CON")]
    [InlineData("lpt1.txt", "_lpt1.txt")]
    [InlineData("   ", "Inconnu")]
    public void Sanitize_produces_valid_windows_names(string input, string expected) =>
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

    [Fact]
    public void Sanitize_truncates_long_names()
    {
        var result = FileNameSanitizer.Sanitize(new string('a', 500));
        Assert.Equal(FileNameSanitizer.MaxSegmentLength, result.Length);
    }

    [Fact]
    public void BuildFileName_uses_artist_dash_title()
    {
        var m = new TrackMetadata { Title = "Gold Dust", Artists = ["Mila Okafor", "Kaito Mori"] };
        Assert.Equal("Mila Okafor, Kaito Mori - Gold Dust", FileNameSanitizer.BuildFileName(m));
    }

    [Fact]
    public void BuildRelativeDirectory_follows_organization()
    {
        var m = new TrackMetadata { Title = "T", Artists = ["Art"], Album = "Alb: 1" };
        Assert.Equal(string.Empty, FileNameSanitizer.BuildRelativeDirectory(m, ExportOrganization.None));
        Assert.Equal("Art", FileNameSanitizer.BuildRelativeDirectory(m, ExportOrganization.Artist));
        Assert.Equal(Path.Combine("Art", "Alb- 1"), FileNameSanitizer.BuildRelativeDirectory(m, ExportOrganization.ArtistAlbum));
        Assert.Equal("Alb- 1", FileNameSanitizer.BuildRelativeDirectory(m, ExportOrganization.Album));
    }

    [Fact]
    public void MakeUnique_appends_counter()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("d", "a.mp3"), Path.Combine("d", "a (2).mp3") };
        Assert.Equal(Path.Combine("d", "a (3).mp3"), FileNameSanitizer.MakeUnique(Path.Combine("d", "a.mp3"), existing.Contains));
        Assert.Equal(Path.Combine("d", "b.mp3"), FileNameSanitizer.MakeUnique(Path.Combine("d", "b.mp3"), existing.Contains));
    }
}

public class TrackTests
{
    [Fact]
    public void ApplyCandidate_overwrites_metadata_unless_edited()
    {
        var c = Candidates.Make("id", "Spotify Title", "Spotify Artist", 200_000, isrc: "ISRC1");
        var track = new Track { Metadata = new TrackMetadata { Title = "Yt", Artists = ["A"] } };
        track.ApplyCandidate(c, MatchStatus.AutoMatched, overwriteMetadata: true);
        Assert.Equal("Spotify Title", track.Metadata.Title);
        Assert.Equal(MetadataOrigin.Spotify, track.MetadataOrigin);
        Assert.True(track.HasSpotifyMatch);

        var edited = new Track { MetadataEdited = true, Metadata = new TrackMetadata { Title = "Mine", Artists = ["Me"] } };
        edited.ApplyCandidate(c, MatchStatus.Confirmed, overwriteMetadata: true);
        Assert.Equal("Mine", edited.Metadata.Title);
        Assert.Equal("spotify:track:id", edited.SpotifyUri);
    }

    [Fact]
    public void Ambiguous_track_is_not_considered_matched()
    {
        var track = new Track { SpotifyUri = "spotify:track:x", MatchStatus = MatchStatus.Ambiguous };
        Assert.False(track.HasSpotifyMatch);
    }

    [Fact]
    public void Year_is_parsed_from_release_date()
    {
        Assert.Equal(2021, new TrackMetadata { ReleaseDate = "2021-11-12" }.Year);
        Assert.Null(new TrackMetadata { ReleaseDate = "n/a" }.Year);
    }
}
