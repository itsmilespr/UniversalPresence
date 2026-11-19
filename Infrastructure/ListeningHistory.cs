using System.Text;
using System.Text.Json;
using UniversalPresence.Configuration;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Append-only listening history (JSON Lines) so the user can review what they heard.
/// </summary>
public sealed class ListeningHistoryService
{
    private readonly string _path;
    private readonly object _gate = new();

    public ListeningHistoryService()
    {
        _path = System.IO.Path.Combine(AppOptions.LocalDataDirectory, "history.jsonl");
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!); } catch { }
    }

    public void Record(string title, string artist, string album, string player, DateTimeOffset at)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                at = at.ToString("O"),
                title,
                artist,
                album,
                player,
            });
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Historial nunca debe tumbar la app.
        }
    }

    public string FilePath => _path;

    public IReadOnlyList<HistoryEntry> ReadAll()
    {
        var result = new List<HistoryEntry>();
        try
        {
            if (!File.Exists(_path)) return result;
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    result.Add(new HistoryEntry(
                        Get(root, "at"),
                        Get(root, "title"),
                        Get(root, "artist"),
                        Get(root, "album"),
                        Get(root, "player")));
                }
                catch
                {
                    // Skip malformed lines instead of failing the whole export.
                }
            }
        }
        catch
        {
            // best effort
        }

        return result;
    }

    public string ExportCsv(string path)
    {
        var entries = ReadAll();
        var builder = new StringBuilder();
        builder.AppendLine("at,title,artist,album,player");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join(",",
                Csv(entry.At), Csv(entry.Title), Csv(entry.Artist), Csv(entry.Album), Csv(entry.Player)));
        }
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    public string Summary()
    {
        var entries = ReadAll();
        if (entries.Count == 0) return "No listening history yet.";

        var topArtists = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Artist))
            .GroupBy(e => e.Artist, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        var topTracks = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Title))
            .GroupBy(e => $"{e.Title} — {e.Artist}", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        return $"Entries: {entries.Count}\nTop artists: {string.Join(", ", topArtists)}\nTop tracks: {string.Join(", ", topTracks)}";
    }

    private static string Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"") }\"";

    public sealed record HistoryEntry(string At, string Title, string Artist, string Album, string Player);
}