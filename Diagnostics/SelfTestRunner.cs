using System.Text.Json;
using UniversalPresence.Configuration;
using UniversalPresence.Core;
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