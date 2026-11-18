using UniversalPresence.Cli;
using UniversalPresence.Configuration;
using UniversalPresence.Core;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;

namespace UniversalPresence.Diagnostics;

/// <summary>Headless modes used to prove what Windows actually reports.</summary>
public static class DiagnosticsRunner
{
    public static async Task<int> RunAsync(AppOptions options, Log log, CliOptions cli)
    {
        if (cli.ListSessions)
        {
            return await RunListAsync(options, log).ConfigureAwait(false);
        }

        return await RunTestAsync(options, log, cli).ConfigureAwait(false);
    }

    private static void PrintBanner(AppOptions options, CliOptions cli, Log log)
    {
        Console.WriteLine();
        Console.WriteLine("Universal Presence — SMTC diagnostics (no player whitelist)");
        Console.WriteLine(new string('=', 88));
        Console.WriteLine($"  Configuration    : {options.ConfigPath}");
        Console.WriteLine($"  Covers            : {options.Artwork.ResolvedCacheDirectory}");
        Console.WriteLine($"  Registro         : {log.FilePath}");
        Console.WriteLine($"  Sondeo           : cada {options.PollIntervalMs} ms");
        Console.WriteLine($"  Tipo de actividad: {options.ActivityType}");
        Console.WriteLine($"  Portada          : {DescribeArtworkMode(options)}");

        if (cli.DryRun)
        {
            Console.WriteLine("  Discord          : DESACTIVADO (--dry-run)");
        }
        else if (string.IsNullOrWhiteSpace(options.DiscordClientId))
        {
            Console.WriteLine("  Discord          : sin configurar (solo se imprime el JSON)");
        }
        else
        {
            Console.WriteLine($"  Discord          : client id {Mask(options.DiscordClientId)}");
        }

        Console.WriteLine(new string('=', 88));
        Console.WriteLine();
    }

    private static async Task<int> RunTestAsync(AppOptions options, Log log, CliOptions cli)
    {
        PrintBanner(options, cli, log);

        using var engine = new PresenceEngine(options, log);
        var lastSignature = "";

        void PrintState()
        {
            var snapshot = engine.LastSnapshot;
            var activity = engine.CurrentActivity;
            var sessions = engine.Sessions;

            var signature = string.Join(
                "|",
                snapshot?.ActivityKey ?? "<none>",
                engine.Paused,
                engine.DiscordState,
                string.Join(",", sessions.Select(s => $"{s.AppUserModelId}:{s.Status}:{s.Title}")));

            if (signature == lastSignature)
            {
                return;
            }

            lastSignature = signature;

            Console.WriteLine();
            Console.WriteLine($"── {DateTimeOffset.Now:HH:mm:ss} ─────────────────────────────────────────────────────────────");

            if (engine.Paused)
            {
                Console.WriteLine("  Application paused: nothing is published to Discord.");
            }

            if (snapshot is null)
            {
                Console.WriteLine("  No SMTC session is active right now.");
                Console.WriteLine("  Abre un reproductor y dale a play (foobar2000 + Gatefold, Spotify, etc.).");
            }
            else
            {
                Console.WriteLine($"  Reproductor : {snapshot.PlayerName}   [AUMID: {Value(snapshot.AppUserModelId)}]");
                Console.WriteLine($"  Origen      : {snapshot.PlayerNameSource}" +
                                  (string.IsNullOrWhiteSpace(snapshot.PlayerExecutable) ? "" : $" · {snapshot.PlayerExecutable}"));
                Console.WriteLine($"  Status      : {snapshot.Status}" + (snapshot.IsCurrentSession ? " (current Windows session)" : ""));
                Console.WriteLine($"  Title       : {Value(snapshot.Title)}");
                Console.WriteLine($"  Artista     : {Value(snapshot.Artist)}");
                Console.WriteLine($"  Álbum       : {Value(snapshot.Album)}");
                Console.WriteLine($"  Tiempo      : {FormatTime(snapshot.Position)} / {FormatTime(snapshot.Duration)}");
                Console.WriteLine($"  Cover       : {(snapshot.Artwork is null
                    ? "no cover (the app icon will be used on Discord)"
                    : $"{snapshot.Artwork.ByteCount / 1024.0:0.0} KB {snapshot.Artwork.ContentType} → {snapshot.Artwork.FilePath}")}");
                Console.WriteLine($"  Discord cover: {snapshot.PublicArtworkUrl ?? "(no public URL; app icon)"}");
                Console.WriteLine($"  Formato     : start = ahora - posición (ms) · end = ahora + (duración - posición)");
            }

            Console.WriteLine($"  Sesiones    : {sessions.Count} detectada(s) por Windows");
            foreach (var session in sessions.OrderByDescending(s => s.Status))
            {
                Console.WriteLine($"      • {Pad(session.AppUserModelId.Length == 0 ? "(sin AUMID)" : session.AppUserModelId, 34)} " +
                                  $"{Pad(session.Status.ToString(), 8)} {session.TrackSummary}");
            }

            Console.WriteLine($"  Discord     : {DescribeDiscord(engine)}");

            if (engine.ImageProxyHint is { Length: > 0 } imageHint)
            {
                Console.WriteLine($"  Note        : {imageHint} (Discord will show a placeholder for images).");
            }

            Console.WriteLine($"  Activity    : {(activity is null ? "(no activity: nothing is sent)" : activity.ToJson())}");
        }

        engine.Updated += PrintState;
        engine.Start();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        if (cli.Seconds > 0)
        {
            cancellation.CancelAfter(TimeSpan.FromSeconds(cli.Seconds));
            Console.WriteLine($"Test mode: will end on its own in {cli.Seconds} s (Ctrl+C to exit earlier).");
        }
        else
        {
            Console.WriteLine("Listening for session changes… Ctrl+C to exit.");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        Console.WriteLine();
        Console.WriteLine("Cerrando y limpiando la presencia de Discord…");
        await engine.StopAsync().ConfigureAwait(false);
        Console.WriteLine("Listo.");
        return 0;
    }

    private static async Task<int> RunListAsync(AppOptions options, Log log)
    {
        Console.WriteLine("Universal Presence — sesiones SMTC que Windows expone ahora mismo");
        Console.WriteLine(new string('=', 88));

        using var monitor = new SmtcMonitor(options, log);

        if (!await monitor.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false))
        {
            Console.WriteLine("No se pudo obtener el gestor SMTC de Windows.");
            return 2;
        }

        await Task.Delay(500).ConfigureAwait(false);
        var sessions = await monitor.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);

        if (sessions.Count == 0)
        {
            Console.WriteLine("No hay ninguna sesión SMTC. Abre un reproductor y dale a reproducir.");
            return 0;
        }

        foreach (var session in sessions.OrderByDescending(s => s.IsCurrentSession).ThenByDescending(s => s.Status))
        {
            Console.WriteLine();
            Console.WriteLine($"  AUMID        : {Value(session.AppUserModelId)}");
            Console.WriteLine($"  Reproductor  : {session.PlayerName} (origen: {session.PlayerNameSource})");
            Console.WriteLine($"  Ejecutable   : {Value(session.PlayerExecutable)}");
            Console.WriteLine($"  Estado       : {session.Status}" + (session.IsCurrentSession ? " · sesión actual" : ""));
            Console.WriteLine($"  Título       : {Value(session.Title)}");
            Console.WriteLine($"  Artista      : {Value(session.Artist)}");
            Console.WriteLine($"  Álbum        : {Value(session.Album)}");
            Console.WriteLine($"  Tiempo       : {FormatTime(session.Position)} / {FormatTime(session.Duration)}");
            Console.WriteLine($"  Carátula     : {(session.Artwork is null ? "sin carátula" : $"{session.Artwork.ByteCount / 1024.0:0.0} KB {session.Artwork.ContentType}")}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{sessions.Count} sesión(es) publicable(s). Sin lista blanca; " +
            $"IgnoredPlayers tiene {options.IgnoredPlayers.Count} entrada(s).");
        return 0;
    }

    private static string DescribeArtworkMode(AppOptions options)
    {
        if (options.Artwork.IsDisabled)
        {
            return "desactivada (solo icono de la app)";
        }

        if (options.Artwork.UsesExternalUrl)
        {
            return $"carátula SMTC desde {options.Artwork.ExternalUrlBase}";
        }

        if (options.Artwork.UsesUpload)
        {
            var host = string.Equals(options.Artwork.UploadMode, "Imgur", StringComparison.OrdinalIgnoreCase)
                ? "Imgur"
                : "Catbox";

            var key = string.Equals(host, "Imgur", StringComparison.OrdinalIgnoreCase)
                && options.Artwork.UploadApiKey.Length == 0
                    ? " (sin UploadApiKey)"
                    : "";

            return $"subida de la carátula SMTC a {host}{key}";
        }

        return options.Artwork.UsesLookup
            ? "búsqueda pública por artista/título (iTunes + Deezer)"
            : "asset de Discord";
    }

    private static string DescribeDiscord(PresenceEngine engine) => engine.DiscordState switch
    {
        DiscordConnectionState.Connected => "conectado (actividad publicada)",
        DiscordConnectionState.Connecting => "conectando…",
        DiscordConnectionState.Disabled => "sin configurar / desactivado",
        _ => "desconectado (se reintenta automáticamente)",
    };

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "(vacío)" : value;

    private static string Pad(string value, int width)
    {
        if (value.Length > width)
        {
            return value[..(width - 1)] + "…";
        }

        return value.PadRight(width);
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "0:00";
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private static string Mask(string clientId)
    {
        if (clientId.Length <= 6)
        {
            return clientId;
        }

        return clientId[..3] + new string('•', Math.Min(8, clientId.Length - 6)) + clientId[^3..];
    }
}
