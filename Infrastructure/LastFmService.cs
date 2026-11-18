using System.Security.Cryptography;
using System.Text;
using UniversalPresence.Configuration;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Scrobbling mínimo a Last.fm. Desactivado si falta API key/usuario/session key.
/// </summary>
public sealed class LastFmService
{
    private readonly AppOptions _options;
    private readonly Log _log;

    public LastFmService(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.LastFmApiKey)
        && !string.IsNullOrWhiteSpace(_options.LastFmSecret)
        && !string.IsNullOrWhiteSpace(_options.LastFmUser)
        && !string.IsNullOrWhiteSpace(_options.LastFmSessionKey);

    public async Task ScrobbleAsync(string title, string artist, string album, DateTimeOffset startedAt, CancellationToken ct)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return;
        try
        {
            var apiSig = Md5($"album{album}api_key{_options.LastFmApiKey}artist{artist}methodtrack.scrobble" +
                             $"sk{_options.LastFmSessionKey}timestamp{startedAt.ToUnixTimeSeconds()}track{title}{_options.LastFmSecret}");
            var body = $"method=track.scrobble&track={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}" +
                       $"&album={Uri.EscapeDataString(album)}&timestamp={startedAt.ToUnixTimeSeconds()}" +
                       $"&api_key={_options.LastFmApiKey}&sk={_options.LastFmSessionKey}&api_sig={apiSig}";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var res = await http.PostAsync("https://ws.audioscrobbler.com/2.0/", new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"), ct);
            if (!res.IsSuccessStatusCode) _log.Debug($"Last.fm scrobble failed: {res.StatusCode}");
        }
        catch (Exception ex)
        {
            _log.Debug($"Last.fm scrobble error: {ex.Message}");
        }
    }

    private static string Md5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}