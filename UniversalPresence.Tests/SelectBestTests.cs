using UniversalPresence.Media;
using Xunit;

namespace UniversalPresence.Tests;

public class SelectBestTests
{
    private static MediaSnapshot Snap(PlaybackState status, bool current = false, DateTimeOffset? updated = null) =>
        new()
        {
            Status = status,
            IsCurrentSession = current,
            SourceUpdatedAt = updated ?? DateTimeOffset.MinValue,
            AppUserModelId = "aumid",
            Title = "title",
        };

    [Fact]
    public void PlayingBeatsPaused()
    {
        var sessions = new[]
        {
            Snap(PlaybackState.Paused),
            Snap(PlaybackState.Playing),
        };

        var best = SmtcMonitor.SelectBest(sessions);

        Assert.NotNull(best);
        Assert.Equal(PlaybackState.Playing, best!.Status);
    }

    [Fact]
    public void CurrentSessionWinsTie()
    {
        var sessions = new[]
        {
            Snap(PlaybackState.Playing, current: false),
            Snap(PlaybackState.Playing, current: true),
        };

        var best = SmtcMonitor.SelectBest(sessions);

        Assert.NotNull(best);
        Assert.True(best!.IsCurrentSession);
    }

    [Fact]
    public void MostRecentlyUpdatedWinsTie()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = new[]
        {
            Snap(PlaybackState.Playing, updated: now.AddMinutes(-1)),
            Snap(PlaybackState.Playing, updated: now),
        };

        var best = SmtcMonitor.SelectBest(sessions);

        Assert.NotNull(best);
        Assert.Equal(now, best!.SourceUpdatedAt);
    }

    [Fact]
    public void EmptyReturnsNull()
    {
        Assert.Null(SmtcMonitor.SelectBest(Array.Empty<MediaSnapshot>()));
    }
}
