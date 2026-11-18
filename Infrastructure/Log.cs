using UniversalPresence.Configuration;

namespace UniversalPresence.Infrastructure;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>Minimal thread-safe logger: console when available, always a rolling file.</summary>
public sealed class Log : IDisposable
{
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private readonly bool _console;
    private readonly LogLevel _minLevel;

    private Log(LogLevel minLevel, bool console, StreamWriter? writer, string filePath, string directory)
    {
        _minLevel = minLevel;
        _console = console;
        _writer = writer;
        FilePath = filePath;
        Directory = directory;
    }

    public string FilePath { get; }

    public string Directory { get; }

    public static Log Create(AppOptions options, bool console)
    {
        var level = Enum.TryParse<LogLevel>(options.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Info;

        var directory = Path.Combine(AppOptions.LocalDataDirectory, "logs");
        var filePath = Path.Combine(directory, "presence.log");
        StreamWriter? writer = null;

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            Rotate(filePath);
            writer = new StreamWriter(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            // Logging must never take the app down.
        }

        return new Log(level, console, writer, filePath, directory);
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minLevel)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Tag(level),-5}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_gate)
        {
            if (_console)
            {
                Console.WriteLine(line);
            }

            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // ignore transient IO failures
            }
        }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };

    private static void Rotate(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                var backup = filePath + ".1";
                File.Delete(backup);
                File.Move(filePath, backup);
            }
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
