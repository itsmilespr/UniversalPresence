using UniversalPresence.Configuration;
using UniversalPresence.Core;
using UniversalPresence.Media;
using Xunit;

namespace UniversalPresence.Tests;

public class ActivityFactoryTests
{
    private static MediaSnapshot Track() => new()
    {
        AppUserModelId = "test",
        PlayerName = "TestPlayer",
        Title = "Song",
        Artist = "Various Artists",
        Status = PlaybackState.Playing,
        CapturedAt = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMinutes(3),
        Position = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void UsesTheFullArtistListWhenAvailable()
    {
        var factory = new ActivityFactory(new AppOptions { Language = "en" });

        var activity = factory.Create(Track() with
        {
            ArtistDisplay = "Post Malone, Jelly Roll",
        });

        Assert.Equal("Post Malone, Jelly Roll", activity?.State);
    }

    [Fact]
    public void CoverOverrideWinsOverTheExtractedExternalUrl()
    {
        var options = new AppOptions { Language = "en" };
        options.Artwork.Mode = "ExternalUrl";
        options.Artwork.ExternalUrlBase = "https://example.com/covers";

        var factory = new ActivityFactory(options);

        var activity = factory.Create(Track() with
        {
            Artwork = new ArtworkInfo("/tmp/cover.jpg", "cover.jpg", "image/jpeg", "hash", 100),
            ArtworkDecision = "override",
            PublicArtworkUrl = "https://example.com/covers/override-1234.jpg",
        });

        Assert.Equal("https://example.com/covers/override-1234.jpg", activity?.Assets?.LargeImage);
    }
}
