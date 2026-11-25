using System.Net;
using UniversalPresence.Media;
using Xunit;

namespace UniversalPresence.Tests;

public class TrackArtistLookupServiceTests
{
    [Fact]
    public void JoinsMainAndFeaturedArtists()
    {
        var result = TrackArtistLookupService.JoinArtists(
            new[] { "Post Malone", "Young Thug" },
            "Post Malone");

        Assert.Equal("Post Malone, Young Thug", result);
    }

    [Fact]
    public void RejectsListWithoutTheReportedArtist()
    {
        var result = TrackArtistLookupService.JoinArtists(
            new[] { "Someone Else", "Young Thug" },
            "Post Malone");

        Assert.Null(result);
    }

    [Fact]
    public void DeduplicatesIgnoringCaseAndAccents()
    {
        var result = TrackArtistLookupService.JoinArtists(
            new[] { "Álvaro Díaz", "alvaro diaz", "Sen Senra" },
            "Álvaro Díaz");

        Assert.Equal("Álvaro Díaz, Sen Senra", result);
    }

    [Fact]
    public void ReturnsNullForASingleArtist()
    {
        Assert.Null(TrackArtistLookupService.JoinArtists(new[] { "Post Malone" }, "Post Malone"));
    }

    [Fact]
    public void AcceptsTheLineupWhenSpotifyReportsAGenericAlbumArtist()
    {
        var result = TrackArtistLookupService.JoinArtists(
            new[] { "Post Malone", "Jelly Roll" },
            "Various Artists");

        Assert.Equal("Post Malone, Jelly Roll", result);
    }

    [Fact]
    public void ReplacesAGenericAlbumArtistEvenWithASingleContributor()
    {
        var result = TrackArtistLookupService.JoinArtists(
            new[] { "Post Malone" },
            "Various Artists");

        Assert.Equal("Post Malone", result);
    }

    [Fact]
    public async Task ResolvesTheFullContributorListFromDeezer()
    {
        using var http = new HttpClient(new StubHandler(
            """{"data":[{"id":7,"title":"Goodbyes","artist":{"name":"Post Malone"}}]}""",
            """{"contributors":[{"name":"Post Malone","role":"Main"},{"name":"Young Thug","role":"Featured"}]}"""));

        var service = new TrackArtistLookupService(null!, http);
        var result = await service.ResolveAsync(
            "Goodbyes (feat. Young Thug)",
            "Post Malone",
            CancellationToken.None);

        Assert.Equal("Post Malone, Young Thug", result);
    }

    [Fact]
    public async Task KeepsTheOriginalArtistWhenDeezerHasOnlyOne()
    {
        using var http = new HttpClient(new StubHandler(
            """{"data":[{"id":7,"title":"Goodbyes","artist":{"name":"Post Malone"}}]}""",
            """{"contributors":[{"name":"Post Malone","role":"Main"}]}"""));

        var service = new TrackArtistLookupService(null!, http);
        var result = await service.ResolveAsync(
            "Goodbyes (feat. Young Thug)",
            "Post Malone",
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RetriesAfterATransientDeezerFailure()
    {
        using var http = new HttpClient(new FlakyHandler());
        var service = new TrackArtistLookupService(null!, http);

        var failed = await service.ResolveAsync("Goodbyes", "Post Malone", CancellationToken.None);
        var recovered = await service.ResolveAsync("Goodbyes", "Post Malone", CancellationToken.None);

        Assert.Null(failed);
        Assert.Equal("Post Malone, Young Thug", recovered);
    }

    private sealed class FlakyHandler : HttpMessageHandler
    {
        private int _searches;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/track/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(
                    """{"contributors":[{"name":"Post Malone","role":"Main"},{"name":"Young Thug","role":"Featured"}]}"""));
            }

            _searches++;

            return _searches == 1
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : Task.FromResult(Json(
                    """{"data":[{"id":7,"title":"Goodbyes","artist":{"name":"Post Malone"}}]}"""));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler(string searchJson, string trackJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.StartsWith("/track/", StringComparison.Ordinal)
                ? trackJson
                : searchJson;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
