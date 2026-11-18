using Basalt.Catalog;
using Basalt.Engine;
using Basalt.Sql;
using Basalt.Storage;
using Basalt.Values;

namespace Basalt;

public sealed class BasaltOptions
{
    /// <summary>Buffer pool size in 4 KB pages (default 2048 = 8 MB).</summary>
    public int CachePages { get; set; } = 2048;

    /// <summary>fsync the WAL on every commit. Turning it off is faster but a power cut can lose recent commits.</summary>
    public bool SyncOnCommit { get; set; } = true;

    /// <summary>Checkpoint automatically once the WAL grows past this many bytes.</summary>
    public long CheckpointThresholdBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>How long a statement waits for the database lock before failing with "database is locked".</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// An open database file. The embedded API:
/// <code>
/// using var db = BasaltDatabase.Open("shop.db");
/// db.Execute("CREATE TABLE items (id INT PRIMARY KEY, name TEXT NOT NULL, price REAL)");
/// db.Execute("INSERT INTO items VALUES (?, ?, ?)", 1, "lamp", 19.5);
/// foreach (var row in db.Execute("SELECT name, price FROM items WHERE id = ?", 1).Rows) ...
/// </code>
/// <see cref="Execute"/> uses a built-in session, which is fine for single-threaded use.
/// For concurrent use, give each thread its own session from <see cref="OpenSession"/>.
/// </summary>
public sealed class BasaltDatabase : IDisposable
{
    private readonly BasaltSession _defaultSession;
    private readonly List<BasaltSession> _sessions = new();
    private bool _disposed;

    internal Pager Pager { get; }
    internal SchemaCatalog Catalog { get; }
    internal DatabaseLock Lock { get; } = new();
    internal BasaltOptions Options { get; }

    public string Path { get; }

    /// <summary>What crash recovery did when this database was opened.</summary>
    public RecoveryInfo Recovery => Pager.LastRecovery;

    private BasaltDatabase(string path, BasaltOptions options)
    {
        Path = path;
        Options = options;
        Pager = Pager.Open(path, options.CachePages);
        try
        {
            Pager.SyncOnCommit = options.SyncOnCommit;
            Pager.CheckpointThresholdBytes = options.CheckpointThresholdBytes;
            Catalog = SchemaCatalog.Load(Pager);
        }
        catch
        {
            Pager.Dispose();
            throw;
        }
        _defaultSession = new BasaltSession(this);
    }

    /// <summary>Opens (or creates) a database file. Runs crash recovery first if the last run did not shut down cleanly.</summary>
    public static BasaltDatabase Open(string path, BasaltOptions? options = null)
    {
        var full = System.IO.Path.GetFullPath(path);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new BasaltDatabase(full, options ?? new BasaltOptions());
            }
            catch (IOException) when (attempt < 20)
            {
                // A process that just exited can hold the file for a moment on Windows.
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>A new session (connection). Sessions are not thread safe; use one per thread.</summary>
    public BasaltSession OpenSession()
    {
        CheckOpen();
        var s = new BasaltSession(this);
        lock (_sessions) _sessions.Add(s);
        return s;
    }

    internal void Forget(BasaltSession s)
    {
        lock (_sessions) _sessions.Remove(s);
    }

    public QueryResult Execute(string sql, params object?[] args) => _defaultSession.Execute(sql, args);

    public IReadOnlyList<QueryResult> ExecuteScript(string sql) => _defaultSession.ExecuteScript(sql);

    public PreparedStatement Prepare(string sql) => _defaultSession.Prepare(sql);

    public bool InTransaction => _defaultSession.InTransaction;

    public IEnumerable<TableInfo> Tables
    {
        get
        {
            if (_defaultSession.InTransaction) return Catalog.Tables.ToList(); // already holds the write lock
            Lock.EnterRead(Options.LockTimeout);
            try { return Catalog.Tables.ToList(); }
            finally { Lock.ExitRead(); }
        }
    }

    /// <summary>Writes all committed pages to the data file and empties the WAL.</summary>
    public void Checkpoint()
    {
        CheckOpen();
        if (_defaultSession.InTransaction) throw new BasaltException("cannot checkpoint inside a transaction; COMMIT or ROLLBACK first");
        Lock.EnterWrite(Options.LockTimeout);
        try { Pager.Checkpoint(); }
        finally { Lock.ExitWrite(); }
    }

    /// <summary>Runs the full structural check (see <see cref="IntegrityChecker"/>).</summary>
    public IntegrityReport CheckIntegrity()
    {
        CheckOpen();
        if (_defaultSession.InTransaction) return IntegrityChecker.Check(Pager, Catalog);
        Lock.EnterRead(Options.LockTimeout);
        try { return IntegrityChecker.Check(Pager, Catalog); }
        finally { Lock.ExitRead(); }
    }

    public StorageStats GetStats() => Pager.GetStats();

    /// <summary>
    /// Test hook: drops the cache and closes the files without a checkpoint, like a killed process.
    /// Reopen the path to run recovery.
    /// </summary>
    internal void SimulateCrash()
    {
        if (_disposed) return;
        _disposed = true;
        Pager.SimulateCrash();
    }

    private void CheckOpen() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _defaultSession.Dispose();
        BasaltSession[] open;
        lock (_sessions) open = _sessions.ToArray();
        foreach (var s in open) s.Dispose();
        _disposed = true;
        Pager.Dispose(); // checkpoint + close
    }
}

/// <summary>A parsed statement that can run many times with different parameters.</summary>
public sealed class PreparedStatement
{
    private readonly BasaltSession _session;
    private readonly Statement _statement;

    internal PreparedStatement(BasaltSession session, Statement statement, int parameterCount, string sql)
    {
        _session = session;
        _statement = statement;
        ParameterCount = parameterCount;
        Sql = sql;
    }

    public string Sql { get; }
    public int ParameterCount { get; }

    public QueryResult Execute(params object?[] args) => _session.Execute(_statement, args.Select(Value.FromObject).ToArray());
}
