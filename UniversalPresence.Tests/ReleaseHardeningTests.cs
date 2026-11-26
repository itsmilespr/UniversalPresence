using UniversalPresence.Core;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;
using Xunit;

namespace UniversalPresence.Tests;

/// <summary>
/// Regresiones de la tanda de rendimiento: reparto del límite de Discord, desviación de
/// la barra, escritura atómica de la caché y recorte del historial.
/// </summary>
public sealed class ReleaseHardeningTests
{
    [Fact]
    public void ContentSendsCanUseTheWholeDiscordWindow()
    {
        var policy = DiscordPresenceService.PolicyFor(
            PublishPriority.Content,
            minUpdateIntervalMs: 4000);

        Assert.Equal(5, policy.Limit);
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.Gap);
    }

    [Fact]
    public void ProgressAndBackgroundSendsReserveSlotsForTheNextTrack()
    {
        var progress = DiscordPresenceService.PolicyFor(
            PublishPriority.Progress,
            minUpdateIntervalMs: 4000);
        var background = DiscordPresenceService.PolicyFor(
            PublishPriority.Background,
            minUpdateIntervalMs: 4000);

        Assert.Equal(3, progress.Limit);
        Assert.Equal(3, background.Limit);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), progress.Gap);
        Assert.Equal(TimeSpan.FromSeconds(4), background.Gap);
    }

    [Fact]
    public void BackgroundGapNeverDropsBelowOneSecond()
    {
        var policy = DiscordPresenceService.PolicyFor(
            PublishPriority.Background,
            minUpdateIntervalMs: 0);

        Assert.Equal(TimeSpan.FromSeconds(1), policy.Gap);
    }

    [Fact]
    public void ProgressDesyncIgnoresNormalPlaybackAndDetectsRealDrift()
    {
        var anchor = DateTimeOffset.UtcNow;

        Assert.False(PresenceEngine.IsProgressDesync(
            publishedPosition: TimeSpan.FromSeconds(10),
            anchorAt: anchor,
            actualPosition: TimeSpan.FromSeconds(15.1),
            now: anchor.AddSeconds(5),
            status: PlaybackState.Playing,
            threshold: TimeSpan.FromMilliseconds(400)));

        Assert.True(PresenceEngine.IsProgressDesync(
            publishedPosition: TimeSpan.FromSeconds(10),
            anchorAt: anchor,
            actualPosition: TimeSpan.FromSeconds(15.8),
            now: anchor.AddSeconds(5),
            status: PlaybackState.Playing,
            threshold: TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void ProgressDesyncComparesAgainstThePausedPosition()
    {
        var anchor = DateTimeOffset.UtcNow;

        Assert.False(PresenceEngine.IsProgressDesync(
            publishedPosition: TimeSpan.FromSeconds(10),
            anchorAt: anchor,
            actualPosition: TimeSpan.FromSeconds(10),
            now: anchor.AddSeconds(30),
            status: PlaybackState.Paused,
            threshold: TimeSpan.FromMilliseconds(400)));

        Assert.True(PresenceEngine.IsProgressDesync(
            publishedPosition: TimeSpan.FromSeconds(10),
            anchorAt: anchor,
            actualPosition: TimeSpan.FromSeconds(13),
            now: anchor.AddSeconds(30),
            status: PlaybackState.Paused,
            threshold: TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void PersistentCacheRoundTripsAndLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "up-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "cache.json");

        try
        {
            var cache = new PersistentCache(path, Log.ForReading());
            cache.Set("cover-a", "https://example.com/a.jpg");

            var reopened = new PersistentCache(path, Log.ForReading());

            Assert.True(reopened.TryGet("cover-a", out var url, out _));
            Assert.Equal("https://example.com/a.jpg", url);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void ListeningHistoryTrimsOnlyAfterCrossingTheLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "up-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.jsonl");

        try
        {
            var history = new ListeningHistoryService(
                retentionDays: 0,
                maxEntries: 5,
                path: path);

            for (var index = 0; index < 25; index++)
            {
                history.Record(
                    $"Track {index}",
                    "Artist",
                    "Album",
                    "Player",
                    DateTimeOffset.UtcNow);
            }

            var lines = File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            Assert.True(lines.Count < 25, $"Se esperaba un recorte, hay {lines.Count} líneas.");
            Assert.True(lines.Count <= 15, $"El recorte dejó demasiadas líneas: {lines.Count}.");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
