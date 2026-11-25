using System;
using UniversalPresence.Media;
using Windows.Media.Control;
using Xunit;

namespace UniversalPresence.Tests;

public class MapStatusTests
{
    [Fact]
    public void MapsPlaying()
    {
        Assert.Equal(
            PlaybackState.Playing,
            SmtcMonitor.MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
    }

    [Fact]
    public void MapsPaused()
    {
        Assert.Equal(
            PlaybackState.Paused,
            SmtcMonitor.MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
    }

    [Fact]
    public void NullMapsClosed()
    {
        Assert.Equal(PlaybackState.Closed, SmtcMonitor.MapStatus(null));
    }
}
