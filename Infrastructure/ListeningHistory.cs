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
}