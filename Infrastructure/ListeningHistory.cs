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

    public ListeningHistoryService(int retentionDays = 90, int maxEntries = 5000, string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.Combine(AppOptions.LocalDataDirectory, "history.jsonl")
            : path;
        _retentionDays = retentionDays < 0 ? 0 : retentionDays;
        _maxEntries = maxEntries < 0 ? 0 : maxEntries;
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!); } catch { }
    }

    public void Record(
        string title,
        string artist,
        string album,
        string player,
        DateTimeOffset at,
        long durationMs = 0)
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
                durationMs = durationMs > 0 ? durationMs : 0,
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
                        Get(root, "player"),
                        GetLong(root, "durationMs")));
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

    /// <summary>Aggregated statistics for the dashboard (0 days = all time).</summary>
    public HistoryStats Compute(int days = 0)
    {
        var entries = ReadAll();
        var cutoff = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days) : DateTimeOffset.MinValue;

        var filtered = new List<HistoryEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (cutoff == DateTimeOffset.MinValue)
            {
                filtered.Add(entry);
                continue;
            }

            if (DateTimeOffset.TryParse(entry.At, out var at) && at >= cutoff)
            {
                filtered.Add(entry);
            }
        }

        var hours = filtered.Sum(entry => entry.DurationMs) / 3_600_000.0;

        var topArtists = filtered
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Artist))
            .GroupBy(entry => entry.Artist, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(10)
            .Select(group => new CountedItem(group.Key, group.Count()))
            .ToList();

        var topTracks = filtered
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Title))
            .GroupBy(
                entry => string.IsNullOrWhiteSpace(entry.Artist)
                    ? entry.Title
                    : $"{entry.Title} — {entry.Artist}",
                StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(10)
            .Select(group => new CountedItem(group.Key, group.Count()))
            .ToList();

        var perDay = new List<DayCount>();
        for (var offset = 6; offset >= 0; offset--)
        {
            var day = DateOnly.FromDateTime(DateTime.Now.AddDays(-offset));
            var count = filtered.Count(entry =>
                DateTimeOffset.TryParse(entry.At, out var at)
                && DateOnly.FromDateTime(at.LocalDateTime) == day);
            perDay.Add(new DayCount(day, count));
        }

        return new HistoryStats(
            filtered.Count,
            hours,
            filtered.Select(entry => entry.Artist).Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).Count(),
            filtered.Select(entry => $"{entry.Title}|{entry.Artist}").Distinct(StringComparer.CurrentCultureIgnoreCase).Count(),
            topArtists,
            topTracks,
            perDay);
    }

    public string ExportCsv(string path)
    {
        var entries = ReadAll();
        var builder = new StringBuilder();
        builder.AppendLine("at,title,artist,album,player,durationMs");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join(",",
                Csv(entry.At), Csv(entry.Title), Csv(entry.Artist), Csv(entry.Album), Csv(entry.Player),
                entry.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    /// <summary>
    /// Exporta el historial completo en JSON (mismo contenido que el CSV, pero legible por
    /// otros programas y con los tipos correctos). Se usa para compartir el historial o
    /// analizarlo fuera de la app.
    /// </summary>
    public string ExportJson(string path)
    {
        var entries = ReadAll()
            .Select(entry => new
            {
                at = entry.At,
                title = entry.Title,
                artist = entry.Artist,
                album = entry.Album,
                player = entry.Player,
                durationMs = entry.DurationMs,
            })
            .ToList();

        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        return path;
    }

    /// <summary>
    /// Resumen de los últimos siete días, comparado con los siete anteriores. Es lo que
    /// responde a «¿cuánto he escuchado esta semana?» sin tener que leer toda la tabla.
    /// </summary>
    public WeeklySummary ComputeWeekly()
    {
        var entries = ReadAll();
        var now = DateTimeOffset.Now;
        var weekStart = now.AddDays(-7);
        var previousStart = now.AddDays(-14);

        var week = new List<HistoryEntry>();
        var previous = 0;

        foreach (var entry in entries)
        {
            if (!DateTimeOffset.TryParse(entry.At, out var at))
            {
                continue;
            }

            if (at >= weekStart)
            {
                week.Add(entry);
            }
            else if (at >= previousStart)
            {
                previous++;
            }
        }

        var topArtist = week
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Artist))
            .GroupBy(entry => entry.Artist, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "";

        var topTrack = week
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Title))
            .GroupBy(
                entry => string.IsNullOrWhiteSpace(entry.Artist)
                    ? entry.Title
                    : $"{entry.Title} — {entry.Artist}",
                StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "";

        var activeDays = week
            .Select(entry => DateTimeOffset.TryParse(entry.At, out var at)
                ? DateOnly.FromDateTime(at.LocalDateTime)
                : (DateOnly?)null)
            .Where(day => day is not null)
            .Distinct()
            .Count();

        return new WeeklySummary(
            week.Count,
            week.Sum(entry => entry.DurationMs) / 3_600_000.0,
            previous,
            topArtist,
            topTrack,
            activeDays);
    }

    public string Summary()
    {
        var stats = Compute();
        if (stats.Plays == 0) return "No listening history yet.";

        var topArtists = stats.TopArtists.Take(5).Select(item => $"{item.Name} ({item.Count})").ToList();
        var topTracks = stats.TopTracks.Take(5).Select(item => $"{item.Name} ({item.Count})").ToList();

        return $"Entries: {stats.Plays}\nHours: {stats.Hours:0.0}\nArtists: {stats.Artists}\n" +
               $"Top artists: {string.Join(", ", topArtists)}\nTop tracks: {string.Join(", ", topTracks)}";
    }

    private static string Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static long GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var value)
            ? value
            : 0;

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"") }\"";

    public sealed record HistoryEntry(string At, string Title, string Artist, string Album, string Player, long DurationMs = 0);

    public sealed record CountedItem(string Name, int Count);

    public sealed record DayCount(DateOnly Day, int Count);

    public sealed record WeeklySummary(
        int Plays,
        double Hours,
        int PreviousPlays,
        string TopArtist,
        string TopTrack,
        int ActiveDays)
    {
        public int ChangePercent => PreviousPlays == 0
            ? (Plays == 0 ? 0 : 100)
            : (int)Math.Round((Plays - PreviousPlays) * 100.0 / PreviousPlays);
    }

    public sealed record HistoryStats(
        int Plays,
        double Hours,
        int Artists,
        int Tracks,
        IReadOnlyList<CountedItem> TopArtists,
        IReadOnlyList<CountedItem> TopTracks,
        IReadOnlyList<DayCount> PerDay);
}
