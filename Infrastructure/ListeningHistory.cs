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
    private readonly int _retentionDays;
    private readonly int _maxEntries;

    public ListeningHistoryService(int retentionDays = 90, int maxEntries = 5000)
    {
        _path = System.IO.Path.Combine(AppOptions.LocalDataDirectory, "history.jsonl");
        _retentionDays = retentionDays < 0 ? 0 : retentionDays;
        _maxEntries = maxEntries < 0 ? 0 : maxEntries;
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
                TrimIfNeeded(at);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Historial nunca debe tumbar la app.
        }
    }

    private void TrimIfNeeded(DateTimeOffset now)
    {
        if (!File.Exists(_path)) return;
        if (_retentionDays == 0 && _maxEntries == 0) return;

        try
        {
            var cutoff = _retentionDays > 0 ? now.AddDays(-_retentionDays) : DateTimeOffset.MinValue;
            var lines = File.ReadAllLines(_path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var kept = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                DateTimeOffset at = DateTimeOffset.MinValue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("at", out var el) && el.ValueKind == JsonValueKind.String)
                    {
                        DateTimeOffset.TryParse(el.GetString(), out at);
                    }
                }
                catch { }

                if (at == DateTimeOffset.MinValue || at >= cutoff)
                {
                    kept.Add(line);
                }
            }

            if (_maxEntries > 0 && kept.Count > _maxEntries)
            {
                kept = kept.Skip(kept.Count - _maxEntries).ToList();
            }

            if (kept.Count != lines.Count)
            {
                File.WriteAllLines(_path, kept);
            }
        }
        catch
        {
            // best effort
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