namespace Basalt.Engine;

/// <summary>
/// Readers-writer lock for the whole database: many readers at once, or one writer.
/// It alternates fairly: a waiting writer stops new readers from jumping ahead of it, and when a
/// writer finishes, the readers that were already waiting get in before the next writer.
/// Without the second rule a busy writer can starve readers; without the first, readers starve writers.
/// It is not tied to a thread (unlike ReaderWriterLockSlim), because a session's BEGIN and
/// COMMIT may run on different threads, for example after an await.
/// </summary>
internal sealed class DatabaseLock
{
    private readonly object _gate = new();
    private int _readers;
    private bool _writer;
    private int _writersWaiting;
    private int _readersWaiting;
    private int _readerTurn; // readers still owed a turn after the last writer left

    public void EnterRead(TimeSpan timeout)
    {
        lock (_gate)
        {
            var deadline = DateTime.UtcNow + timeout;
            _readersWaiting++;
            try
            {
                while (_writer || (_writersWaiting > 0 && _readerTurn == 0))
                {
                    if (!Wait(deadline))
                    {
                        if (_readerTurn > 0) _readerTurn--;
                        throw new BasaltException("database is locked (a write transaction is running)");
                    }
                }
                if (_readerTurn > 0) _readerTurn--;
                _readers++;
            }
            finally
            {
                _readersWaiting--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void ExitRead()
    {
        lock (_gate)
        {
            if (--_readers == 0) Monitor.PulseAll(_gate);
        }
    }

    public void EnterWrite(TimeSpan timeout)
    {
        lock (_gate)
        {
            var deadline = DateTime.UtcNow + timeout;
            _writersWaiting++;
            try
            {
                while (_writer || _readers > 0 || _readerTurn > 0)
                {
                    if (!Wait(deadline))
                        throw new BasaltException(_writer
                            ? "database is locked (another write transaction is running)"
                            : "database is locked (readers are active)");
                }
                _writer = true;
            }
            finally
            {
                _writersWaiting--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void ExitWrite()
    {
        lock (_gate)
        {
            _writer = false;
            _readerTurn = _readersWaiting; // everyone already queued for reading goes next
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Waits for a pulse or the deadline. Returns false once the deadline has passed; callers re-check their condition.</summary>
    private bool Wait(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return false;
        Monitor.Wait(_gate, remaining);
        return true;
    }
}
