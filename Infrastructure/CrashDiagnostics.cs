using System.IO.Compression;
using System.Text;
using UniversalPresence.Configuration;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Writes a small diagnostic bundle when the app crashes, so a non-technical user can
/// attach one file instead of copying logs by hand. Secrets are never included.
/// </summary>
public static class CrashDiagnostics
{
    public static string Write(Exception? exception, Log? log, AppOptions? options)
    {
        var directory = Path.Combine(AppOptions.LocalDataDirectory, "crashes");
        Directory.CreateDirectory(directory);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var bundlePath = Path.Combine(directory, $"crash-{stamp}.zip");

        try
        {
            using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
            AddText(archive, "version.txt", typeof(CrashDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown");
            AddText(archive, "environment.txt", BuildEnvironment(exception));
            AddText(archive, "config.redacted.json", Redact(options));
            AddLog(archive, log);
        }
        catch
        {
            // Diagnostics must never make a crash worse.
        }

        return bundlePath;
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    private static void AddLog(ZipArchive archive, Log? log)
    {
        try
        {
            if (log is null || !File.Exists(log.FilePath))
            {
                return;
            }

            var entry = archive.CreateEntry("presence.log", CompressionLevel.Optimal);

            // La app mantiene el registro abierto en escritura con FileShare.ReadWrite;
            // File.OpenRead pide FileShare.Read y falla con «el archivo está en uso».
            using var input = new FileStream(
                log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = entry.Open();
            input.CopyTo(output);
        }
        catch
        {
            // ignore
        }
    }

    private static string BuildEnvironment(Exception? exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($".NET: {Environment.Version}");
        builder.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
        builder.AppendLine($"Working directory: {Environment.CurrentDirectory}");
        builder.AppendLine($"Process path: {Environment.ProcessPath}");
        builder.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");

        if (exception is not null)
        {
            builder.AppendLine();
            builder.AppendLine(exception.ToString());
        }

        return builder.ToString();
    }

    private static string Redact(AppOptions? options)
    {
        if (options is null)
        {
            return "{}";
        }

        try
        {
            // Serialize a shallow copy with secrets cleared.
            var clone = new AppOptions
            {
                // ConfigPath has a private setter; reflection-free clone keeps the path only for context.
                DiscordClientId = options.DiscordClientId,
                DiscordAccessToken = "[redacted]",
                DiscordRefreshToken = "[redacted]",
                DiscordClientSecret = "[redacted]",
                LastFmApiKey = "[redacted]",
                LastFmSecret = "[redacted]",
                LastFmSessionKey = "[redacted]",
                LastFmUser = options.LastFmUser,
                Language = options.Language,
                LogLevel = options.LogLevel,
            };
            return System.Text.Json.JsonSerializer.Serialize(clone, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return "{}";
        }
    }
}