namespace UniversalPresence.Infrastructure;

/// <summary>Per-session mutex so the tray icon is only created once.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;

    public SingleInstance(string name)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            IsFirst = createdNew;
        }
        catch (AbandonedMutexException)
        {
            IsFirst = true;
        }
        catch
        {
            IsFirst = true;
        }
    }

    public bool IsFirst { get; }

    public void Dispose()
    {
        try
        {
            if (IsFirst)
            {
                _mutex?.ReleaseMutex();
            }

            _mutex?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
