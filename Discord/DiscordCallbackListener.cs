using System.Net;
using System.Net.Sockets;
using System.Text;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Discord;

/// <summary>Respuesta que devuelve el navegador al terminar la autorización.</summary>
public sealed record DiscordCallback(bool Ok, string? Code, string? State, string? Error);

/// <summary>
/// Escucha local para el redirect de OAuth2 («http://127.0.0.1:PUERTO/callback»).
///
/// Es lo que permite conectar la cuenta sin copiar y pegar nada: el navegador vuelve a
/// esta dirección, aquí se lee el «code» y la ventana lo canjea por los tokens.
///
/// Se usa un <see cref="TcpListener"/> en lugar de HttpListener porque este último exige
/// permisos de administrador para registrar la URL en Windows.
/// </summary>
public sealed class DiscordCallbackListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Log _log;
    private bool _started;

    public DiscordCallbackListener(int port, Log log)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _log = log;
    }

    public void Start()
    {
        _listener.Start();
        _started = true;
        _log.Debug($"Escuchando la respuesta de Discord en el puerto {Port}.");
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// Espera la petición del navegador. Null si se agota el tiempo (o si el usuario cancela):
    /// en ese caso la ventana ofrece el pegado manual.
    /// </summary>
    public async Task<DiscordCallback?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return null;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;

        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            TcpClient client;

            try
            {
                client = await _listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }

            using (client)
            {
                var result = await HandleAsync(client, cancellationToken).ConfigureAwait(false);

                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private async Task<DiscordCallback?> HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 2048, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return null;
            }

            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "/";
            var query = QueryOf(target);

            if (query is null)
            {
                // favicon, peticiones sueltas del navegador: se ignoran y se sigue esperando.
                await RespondAsync(stream, "204 No Content", "text/plain; charset=utf-8", "", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            var code = Value(query, "code");
            var state = Value(query, "state");
            var error = Value(query, "error") ?? Value(query, "error_description");

            var ok = !string.IsNullOrWhiteSpace(code) && string.IsNullOrEmpty(error);

            await RespondAsync(
                    stream,
                    ok ? "200 OK" : "200 OK",
                    "text/html; charset=utf-8",
                    BuildPage(ok, error),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!ok && string.IsNullOrEmpty(error))
            {
                return null;
            }

            return new DiscordCallback(ok, code, state, error);
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo leer la respuesta de Discord: {ex.Message}");
            return null;
        }
    }

    /// <summary>Devuelve el query de una petición que traiga «code» o «error».</summary>
    private static string? QueryOf(string target)
    {
        var questionMark = target.IndexOf('?');

        if (questionMark < 0)
        {
            return null;
        }

        var query = target[(questionMark + 1)..];

        if (!query.Contains("code=", StringComparison.Ordinal)
            && !query.Contains("error=", StringComparison.Ordinal))
        {
            return null;
        }

        return query;
    }

    private static string? Value(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);

            if (split.Length == 2 && string.Equals(split[0], key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(split[1]);
            }
        }

        return null;
    }

    private static async Task RespondAsync(
        NetworkStream stream,
        string status,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);

        var header = $"HTTP/1.1 {status}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {bytes.Length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildPage(bool ok, string? error)
    {
        var title = ok ? "Cuenta conectada" : "No se pudo conectar";
        var detail = ok
            ? "You can now close this tab and return to Universal Presence. Covers and the player icon will appear on your Discord profile."
            : WebUtility.HtmlEncode(error ?? "Discord did not return an authorization code. Try again from the application.");

        return $$"""
                <!DOCTYPE html>
                <html lang="es">
                <head>
                  <meta charset="utf-8">
                  <title>{{title}} · Universal Presence</title>
                  <style>
                    body { background:#1e1f22; color:#f2f3f5; font-family:system-ui,Segoe UI,sans-serif;
                            display:flex; align-items:center; justify-content:center; height:100vh; margin:0; }
                    main { max-width:460px; text-align:center; padding:32px; }
                    h1 { font-size:22px; margin:0 0 12px; }
                    p  { color:#b5bac1; line-height:1.5; }
                  </style>
                </head>
                <body>
                  <main>
                    <h1>{{title}}</h1>
                    <p>{{detail}}</p>
                  </main>
                </body>
                </html>
                """;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore
        }
    }
}
