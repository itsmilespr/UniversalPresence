using System.Drawing;
using System.Text.Json;
using UniversalPresence.Configuration;
using UniversalPresence.Core;
using UniversalPresence.Infrastructure;
using UniversalPresence.Localization;
using UniversalPresence.Media;

namespace UniversalPresence.Diagnostics;

public static class SelfTestRunner
{
    public static int Run()
    {
        var failures = new List<string>();

        void Check(string name, bool condition)
        {
            if (!condition) failures.Add(name);
        }

        // Localization fallback.
        Check("PresenceText fallback", PresenceText.For("zz") == PresenceText.English);
        Check("UiText fallback", UiText.For("zz") == UiText.English);

        // Config roundtrip + secret protection.
        var tempDir = Path.Combine(Path.GetTempPath(), "UniversalPresenceSelfTest");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        try
        {
            if (File.Exists(configPath)) File.Delete(configPath);
            var secrets = Path.Combine(tempDir, "secrets.dpapi.json");
            if (File.Exists(secrets)) File.Delete(secrets);

            var options = new AppOptions { ConfigPath = configPath, DiscordAccessToken = "secret-token", LastFmSecret = "secret-lastfm" };
            options.Save();
            var savedJson = File.ReadAllText(configPath);
            Check("Config does not contain raw secret", !savedJson.Contains("secret-token", StringComparison.Ordinal));
            Check("Secrets file created", File.Exists(secrets));

            var loaded = AppOptions.Load(new UniversalPresence.Cli.CliOptions { ConfigPath = configPath });
            Check("Secret roundtrip", loaded.DiscordAccessToken == "secret-token" && loaded.LastFmSecret == "secret-lastfm");
        }
        catch (Exception ex)
        {
            failures.Add("Config roundtrip: " + ex.Message);
        }

        // Activity factory smoke test.
        try
        {
            var options = new AppOptions { Language = "en" };
            var factory = new ActivityFactory(options);
            var snapshot = new MediaSnapshot
            {
                AppUserModelId = "test",
                PlayerName = "TestPlayer",
                Title = "Song",
                Artist = "Artist",
                Album = "Album",
                Status = PlaybackState.Playing,
                Duration = TimeSpan.FromMinutes(3),
                Position = TimeSpan.FromSeconds(30),
                CapturedAt = DateTimeOffset.UtcNow,
            };
            var activity = factory.Create(snapshot);
            Check("Activity created", activity is not null);
            Check("Activity details", activity?.Details == "Song");
            Check("Activity state", activity?.State == "Artist");
            Check("Activity buttons", activity?.Buttons?.Count == 2);
        }
        catch (Exception ex)
        {
            failures.Add("Activity factory: " + ex.Message);
        }

        // Button customization.
        try
        {
            var options = new AppOptions { Language = "en", Button1Label = "Play", Button1Url = "https://example.com/play", Button2Label = "", Button2Url = "" };
            var factory = new ActivityFactory(options);
            var snapshot = new MediaSnapshot
            {
                AppUserModelId = "test",
                PlayerName = "TestPlayer",
                Title = "Song",
                Artist = "Artist",
                Status = PlaybackState.Playing,
                CapturedAt = DateTimeOffset.UtcNow,
            };
            var activity = factory.Create(snapshot);
            Check("Button customization count", activity?.Buttons?.Count == 2);
            Check("Button customization label", activity?.Buttons?[0].Label == "Play");
            Check("Button customization url", activity?.Buttons?[0].Url == "https://example.com/play");
        }
        catch (Exception ex)
        {
            failures.Add("Button customization: " + ex.Message);
        }

        // Icon-only mode.
        try
        {
            var options = new AppOptions { Language = "en" };
            options.Artwork.IconOnly = true;
            var factory = new ActivityFactory(options);
            var snapshot = new MediaSnapshot
            {
                AppUserModelId = "test",
                PlayerName = "TestPlayer",
                Title = "Song",
                Artist = "Artist",
                Status = PlaybackState.Playing,
                CapturedAt = DateTimeOffset.UtcNow,
                Artwork = new ArtworkInfo("/tmp/x.jpg", "x.jpg", "image/jpeg", "hash", 100),
            };
            var activity = factory.Create(snapshot);
            Check("Icon-only activity created", activity is not null);
            Check("Icon-only clears large image", activity is null || string.IsNullOrWhiteSpace(activity.Assets?.LargeImage));
        }
        catch (Exception ex)
        {
            failures.Add("Icon-only: " + ex.Message);
        }

        // Templates.
        try
        {
            var options = new AppOptions { Language = "en", DetailsTemplate = "{title} by {artist}" };
            var factory = new ActivityFactory(options);
            var snapshot = new MediaSnapshot
            {
                AppUserModelId = "test",
                PlayerName = "TestPlayer",
                Title = "Song",
                Artist = "Artist",
                Status = PlaybackState.Playing,
                CapturedAt = DateTimeOffset.UtcNow,
            };
            var activity = factory.Create(snapshot);
            Check("Template applied", activity?.Details == "Song by Artist");
        }
        catch (Exception ex)
        {
            failures.Add("Templates: " + ex.Message);
        }

        // Textos nuevos: todas las claves existen y ningún idioma se queda a medias.
        try
        {
            var english = UiStrings.For("en");
            var keys = UiStrings.Keys.ToList();
            Check("UiStrings keys present", keys.Count >= 60);
            Check("UiStrings English non-empty",
                keys.All(key => !string.IsNullOrWhiteSpace(english[key]) && english[key] != key));

            // Antes solo se vigilaba el español, así que portugués, francés, alemán, italiano y
            // chino se quedaron con textos en inglés al añadir funciones. Ahora el autotest
            // exige que los siete idiomas estén completos.
            foreach (var code in new[] { "es", "pt", "fr", "de", "it", "zh" })
            {
                Check(
                    $"UiStrings {code} translation complete",
                    keys.All(key => UiStrings.HasTranslation(code, key)));
            }

            Check("UiStrings unknown key falls back", english["__nope__"] == "__nope__");
        }
        catch (Exception ex)
        {
            failures.Add("UiStrings: " + ex.Message);
        }

        // Normalización de las opciones nuevas.
        try
        {
            var options = new AppOptions
            {
                IdleSeconds = 1,
                UpdateChannel = "BETA",
                CoverOverrides = new Dictionary<string, string>
                {
                    ["  Spotify.exe  "] = "https://example.com/a.png",
                    ["   "] = "https://example.com/ignored.png",
                    ["vlc.exe"] = "   ",
                },
            };
            options.Normalize();

            Check("IdleSeconds clamped", options.IdleSeconds == 30);
            Check("UpdateChannel normalized", options.UpdateChannel == "beta");
            Check("CoverOverrides cleaned",
                options.CoverOverrides.Count == 1 && options.CoverOverrides.ContainsKey("Spotify.exe"));
            Check("CoverOverride by player name (case-insensitive)",
                options.ResolveCoverOverride(null, "spotify.exe") == "https://example.com/a.png");
            Check("CoverOverride miss returns null",
                options.ResolveCoverOverride("other.app", "Other Player") is null);
        }
        catch (Exception ex)
        {
            failures.Add("Options normalize: " + ex.Message);
        }

        // Historial de escucha: duración, agregados y CSV.
        try
        {
            var historyPath = Path.Combine(tempDir, "history-test.jsonl");
            var csvPath = Path.Combine(tempDir, "history-test.csv");
            if (File.Exists(historyPath)) File.Delete(historyPath);

            var history = new ListeningHistoryService(90, 1000, historyPath);
            history.Record("Song A", "Artist A", "Album", "Player", DateTimeOffset.Now, 60_000);
            history.Record("Song A", "Artist A", "Album", "Player", DateTimeOffset.Now, 60_000);
            history.Record("Song B", "Artist B", "Album", "Player", DateTimeOffset.Now);

            var stats = history.Compute(0);
            Check("History plays", stats.Plays == 3);
            Check("History hours", Math.Abs(stats.Hours - (120_000 / 3_600_000.0)) < 0.0001);
            Check("History artists", stats.Artists == 2);
            Check("History top artist", stats.TopArtists.Count == 2 && stats.TopArtists[0].Count == 2);
            Check("History per-day window", stats.PerDay.Count == 7 && stats.PerDay[^1].Count == 3);
            Check("History range filter", history.Compute(0).Plays >= history.Compute(1).Plays);

            history.ExportCsv(csvPath);
            var csv = File.ReadAllText(csvPath);
            Check("History CSV header", csv.StartsWith("at,title,artist,album,player,durationMs", StringComparison.Ordinal));
            Check("History CSV rows", csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 4);
        }
        catch (Exception ex)
        {
            failures.Add("History: " + ex.Message);
        }

        // Normalización compartida: acentos precompuestos, emparejamiento y claves.
        try
        {
            Check("TextMatch strips accents", TextMatch.Normalize("Álvaro Díaz") == "alvaro diaz");
            Check("TextMatch similar reordered case", TextMatch.Similar("RAMONA FLOWERS", "Ramona Flowers"));
            Check("TextMatch similar edition suffix", TextMatch.Similar("Himno (Remastered)", "Himno"));
            Check("TextMatch rejects unrelated", !TextMatch.Similar("Song One", "Other Title"));
            Check("TextMatch ignores partial words", !TextMatch.Similar("Someone", "One"));
            Check("TextMatch empty safe", !TextMatch.Similar("", "Song") && TextMatch.Normalize(null) == "");
            Check(
                "TextMatch track key is stable",
                TextMatch.TrackKey("Álvaro Díaz", "Himno") == TextMatch.TrackKey("alvaro  diaz", "HIMNO"));
        }
        catch (Exception ex)
        {
            failures.Add("TextMatch: " + ex.Message);
        }

        // Portada por canción y textos por reproductor.
        try
        {
            var options = new AppOptions { Language = "en" };
            options.TrackCoverOverrides[TextMatch.TrackKey("Álvaro Díaz", "Himno")] = "https://example.com/himno.png";
            options.TrackCoverOverrides[TextMatch.TitleKey("Solo")] = "https://example.com/solo.png";
            options.PlayerTemplates["TestPlayer"] = PresenceTemplate.Create("P: {title}", "", "");
            options.Normalize();

            Check(
                "Track override by artist and title",
                options.ResolveTrackCoverOverride("alvaro diaz", "HIMNO") == "https://example.com/himno.png");
            Check(
                "Track override by title only",
                options.ResolveTrackCoverOverride("Someone Else", "solo") == "https://example.com/solo.png");
            Check("Track override miss", options.ResolveTrackCoverOverride("Nobody", "Nothing") is null);
            Check(
                "Track override forgiving with featured artists",
                options.ResolveTrackCoverOverride("Álvaro Díaz & Someone", "Himno (Remastered)")
                == "https://example.com/himno.png");

            var factory = new ActivityFactory(options);
            var snapshot = new MediaSnapshot
            {
                AppUserModelId = "test",
                PlayerName = "TestPlayer",
                Title = "Song",
                Artist = "Artist",
                Status = PlaybackState.Playing,
                CapturedAt = DateTimeOffset.UtcNow,
            };
            var activity = factory.Create(snapshot);
            Check("Player template applied", activity?.Details == "P: Song");
            Check("Player template empty field falls back", activity?.State == "Artist");

            var other = factory.Create(snapshot with
            {
                AppUserModelId = "other",
                PlayerName = "OtherPlayer",
            });
            Check("Player template does not leak", other?.Details == "Song");
        }
        catch (Exception ex)
        {
            failures.Add("Overrides and templates: " + ex.Message);
        }

        // Exportación JSON y resumen semanal del historial.
        try
        {
            var jsonPath = Path.Combine(tempDir, "history-test.json");
            if (File.Exists(jsonPath)) File.Delete(jsonPath);

            // El archivo de origen se reinicia: el autotest debe dar el mismo resultado
            // tantas veces como se ejecute.
            var weeklyPath = Path.Combine(tempDir, "history-weekly.jsonl");
            if (File.Exists(weeklyPath)) File.Delete(weeklyPath);

            var history = new ListeningHistoryService(90, 1000, weeklyPath);
            history.Record("Song A", "Artist A", "Album", "Player", DateTimeOffset.Now, 90_000);
            history.Record("Song A", "Artist A", "Album", "Player", DateTimeOffset.Now, 30_000);

            history.ExportJson(jsonPath);
            using (var document = JsonDocument.Parse(File.ReadAllText(jsonPath)))
            {
                Check("History JSON is an array", document.RootElement.ValueKind == JsonValueKind.Array);
                Check("History JSON keeps entries", document.RootElement.GetArrayLength() == 2);
                Check(
                    "History JSON keeps duration",
                    document.RootElement[0].TryGetProperty("durationMs", out var duration)
                    && duration.GetInt64() > 0);
            }

            var weekly = history.ComputeWeekly();
            Check("Weekly plays", weekly.Plays == 2);
            Check("Weekly hours", Math.Abs(weekly.Hours - (120_000 / 3_600_000.0)) < 0.0001);
            Check("Weekly active days", weekly.ActiveDays == 1);
            Check("Weekly top artist", weekly.TopArtist == "Artist A");
        }
        catch (Exception ex)
        {
            failures.Add("History export: " + ex.Message);
        }

        // Optimización de carátulas antes de subir.
        try
        {
            using var big = new Bitmap(1600, 1600);
            using (var graphics = Graphics.FromImage(big))
            {
                graphics.Clear(Color.CadetBlue);
            }

            using var source = new MemoryStream();
            big.Save(source, System.Drawing.Imaging.ImageFormat.Jpeg);
            var originalBytes = source.ToArray();

            var prepared = ImageOptimizer.Prepare(originalBytes, "image/jpeg", "cover.jpg");
            Check("Image optimizer changes big covers", prepared.Changed);
            Check("Image optimizer shrinks", prepared.Bytes.Length < originalBytes.Length);

            using (var check = Image.FromStream(new MemoryStream(prepared.Bytes)))
            {
                Check("Image optimizer caps the longest side", Math.Max(check.Width, check.Height) <= ImageOptimizer.MaxSide);
            }

            using var small = new Bitmap(80, 80);
            using var smallStream = new MemoryStream();
            small.Save(smallStream, System.Drawing.Imaging.ImageFormat.Jpeg);
            var smallBytes = smallStream.ToArray();
            var untouched = ImageOptimizer.Prepare(smallBytes, "image/jpeg", "tiny.jpg");
            Check("Image optimizer keeps small covers", !untouched.Changed && untouched.Bytes.Length == smallBytes.Length);
        }
        catch (Exception ex)
        {
            failures.Add("Image optimizer: " + ex.Message);
        }

        // Detección de inactividad/bloqueo: nunca debe lanzar y debe ser coherente.
        try
        {
            var idle = UserIdle.IdleTime;
            var locked = UserIdle.IsSessionLocked();

            Check("Idle time sane", idle >= TimeSpan.Zero && idle < TimeSpan.FromDays(60));
            Check("Away without thresholds", !UserIdle.IsAway(0) || locked);
            Check("Away with huge threshold", locked || !UserIdle.IsAway(86_400));
        }
        catch (Exception ex)
        {
            failures.Add("UserIdle: " + ex.Message);
        }

        // Interfaz: los formularios nuevos se construyen sin mostrar (detecta errores de
        // enlace, diccionarios incompletos y controles nulos antes de llegar al usuario).
        try
        {
            var options = new AppOptions { Language = "es" };

            using (var stats = new UniversalPresence.Tray.StatsForm(options))
            {
                Check("StatsForm built", stats.Text.Length > 0);
            }

            using (var wizard = new UniversalPresence.Tray.FirstRunForm(options, discordRunning: true))
            {
                Check("FirstRunForm built", wizard.Text.Length > 0);
            }

            using (var settings = new UniversalPresence.Tray.SettingsForm(options, () => { }))
            {
                Check("SettingsForm built", settings.Text.Length > 0);

                // La pestaña Avanzado y el botón de Last.fm deben existir de verdad, no solo compilar.
                var names = new HashSet<string>(StringComparer.Ordinal);
                void Walk(Control control)
                {
                    if (!string.IsNullOrEmpty(control.Name)) names.Add(control.Name);
                    foreach (Control child in control.Controls) Walk(child);
                }
                Walk(settings);

                Check("Advanced tab present", names.Contains("ClearWhenIdleCheck"));
                Check("Idle seconds box present", names.Contains("IdleSecondsBox"));
                Check("Update channel box present", names.Contains("UpdateChannelBox"));
                Check("Auto rollback check present", names.Contains("AutoRollbackCheck"));
                Check("Cover overrides list present", names.Contains("OverridesList"));
                Check("Track overrides list present", names.Contains("TrackOverridesList"));
                Check("Player templates list present", names.Contains("PlayerTemplatesList"));
                Check("Upload optimization check present", names.Contains("OptimizeUploadsCheck"));
                Check("Fallback host check present", names.Contains("FallbackHostCheck"));
                Check("Stats button present", names.Contains("OpenStatsButton"));
                Check("Log viewer button present", names.Contains("OpenLogButton"));
                Check("Restore button present", names.Contains("RestoreBackupButton"));
                Check("Last.fm connect button present", names.Contains("LastFmConnectButton"));
            }

            using (var viewer = new UniversalPresence.Tray.LogViewerForm(options, Log.ForReading()))
            {
                Check("LogViewerForm built", viewer.Text.Length > 0);
            }
        }
        catch (Exception ex)
        {
            failures.Add("Forms: " + ex.GetType().Name + ": " + ex.Message);
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("Self-test: PASS");
            return 0;
        }

        Console.Error.WriteLine("Self-test: FAIL");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine(" - " + failure);
        }
        return 1;
    }
}