namespace UniversalPresence.Media;

public enum PlaybackState
{
    Closed = 0,
    Stopped = 1,
    Paused = 2,
    Playing = 3,
}

/// <summary>Immutable view of one SMTC session at a point in time.</summary>
public sealed record MediaSnapshot
{
    /// <summary>Raw AppUserModelId reported by Windows. Never used to filter.</summary>
    public string AppUserModelId { get; init; } = "";

    /// <summary>Human readable player name (process / registered app / AUMID fallback).</summary>
    public string PlayerName { get; init; } = "";

    /// <summary>Executable path when it could be resolved.</summary>
    public string? PlayerExecutable { get; init; }

    /// <summary>How the player name was obtained: override | appinfo | process | aumid | empty.</summary>
    public string PlayerNameSource { get; init; } = "";

    public string Title { get; init; } = "";

    public string Artist { get; init; } = "";

    public string Album { get; init; } = "";

    public string AlbumArtist { get; init; } = "";

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    public PlaybackState Status { get; init; }

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset SourceUpdatedAt { get; init; }

    public bool IsCurrentSession { get; init; }

    public ArtworkInfo? Artwork { get; init; }

    /// <summary>Public HTTPS cover URL (hosted SMTC cover or resolved catalog cover).</summary>
    public string? PublicArtworkUrl { get; init; }

    /// <summary>Public HTTPS URL of the program's icon (uploaded cover).</summary>
    public string? ProgramIconUrl { get; init; }

    /// <summary>Consecutive play count for this track (1 on first play, increments on repeat).</summary>
    public int PlayCount { get; init; } = 1;

    public bool IsActive => Status is PlaybackState.Playing or PlaybackState.Paused;

    /// <summary>Stable key for activity de-duplication (position is intentionally excluded).</summary>
    public string ActivityKey => string.Join(
        '|',
        AppUserModelId,
        PlayerName,
        Title,
        Artist,
        Album,
        Status.ToString(),
        (long)Duration.TotalSeconds);

    public string TrackSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title))
            {
                return PlayerName;
            }

            return string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} — {Artist}";
        }
    }
}

public sealed record ArtworkInfo(string FilePath, string FileName, string ContentType, string Hash, int ByteCount);
