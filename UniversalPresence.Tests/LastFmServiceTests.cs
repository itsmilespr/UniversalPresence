using UniversalPresence.Infrastructure;
using Xunit;

namespace UniversalPresence.Tests;

public class LastFmServiceTests
{
    [Fact]
    public void RequiresHalfOfTheTrackLength()
    {
        var duration = TimeSpan.FromMinutes(3);

        Assert.False(LastFmService.MeetsScrobbleRule(duration, TimeSpan.FromSeconds(89)));
        Assert.True(LastFmService.MeetsScrobbleRule(duration, TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void CapsTheThresholdAtFourMinutes()
    {
        var duration = TimeSpan.FromMinutes(20);

        Assert.False(LastFmService.MeetsScrobbleRule(duration, TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(59)));
        Assert.True(LastFmService.MeetsScrobbleRule(duration, TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void RejectsTracksOfThirtySecondsOrLess()
    {
        var duration = TimeSpan.FromSeconds(30);

        Assert.False(LastFmService.MeetsScrobbleRule(duration, duration));
    }

    [Fact]
    public void RequiresFourMinutesWhenThePlayerReportsNoDuration()
    {
        Assert.False(LastFmService.MeetsScrobbleRule(TimeSpan.Zero, TimeSpan.FromMinutes(3)));
        Assert.True(LastFmService.MeetsScrobbleRule(TimeSpan.Zero, TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void RejectsATrackThatWasNotPlayedAtAll()
    {
        Assert.False(LastFmService.MeetsScrobbleRule(TimeSpan.FromMinutes(3), TimeSpan.Zero));
    }
}
