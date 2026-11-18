namespace UniversalPresence.Infrastructure;

/// <summary>
/// Wakes the presence loop as soon as an SMTC event arrives, instead of waiting for
/// the next poll tick. Events are coalesced; missed signals are covered by the poll.
/// </summary>
public sealed class ChangeSignal
{
    private TaskCompletionSource<bool> _pending = NewSource();

    public void Set()
    {
        var pending = _pending;
        pending.TrySetResult(true);
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pending = _pending;
        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(pending.Task, delay).ConfigureAwait(false);

        if (completed == pending.Task)
        {
            Interlocked.CompareExchange(ref _pending, NewSource(), pending);
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static TaskCompletionSource<bool> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
