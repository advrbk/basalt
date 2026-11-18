using System.Buffers;

namespace Basalt.Storage;

/// <summary>Counters exposed for the REPL's <c>.stats</c> command and the benchmarks.</summary>
public sealed record StorageStats(
    int PageCount, int CachedPages, long CacheHits, long CacheMisses, long Evictions,
    long DataFileReads, long DataFileWrites, long WalBytes, long WalBytesWritten, long WalSyncs,
    long Commits, long Checkpoints);

/// <summary>
/// The storage engine's front door. Everything above it (heap files, B+trees, the catalog)
/// asks the pager for pages by id and never touches files directly.
///
/// Durability rules it enforces:
/// <list type="bullet">
/// <item>No steal: pages changed by the running transaction stay pinned in memory until it ends.</item>
/// <item>Write-ahead: at commit, full images of every changed page go to the WAL and are fsynced
///       before commit returns. The data file is written later (eviction or checkpoint).</item>
/// <item>Checkpoint: write all dirty pages to the data file, fsync it, then empty the WAL.</item>
/// <item>Recovery: on open, replay the page images of every committed transaction in the WAL.</item>
/// </list>
/// Rollback is cheap because the pager keeps a copy of each page as it was before the
/// transaction first changed it.
/// </summary>
internal sealed class Pager : IDisposable
{
    public const int PageSize = 4096;

    private readonly DiskManager _disk;
    private readonly WriteAheadLog _wal;
    private readonly BufferPool _pool;

    // Before-images: page id -> bytes before the transaction (or statement) first changed it.
    // null means the page was allocated by the transaction (or statement).
    private Dictionary<int, byte[]?>? _txnBefore;
    private Dictionary<int, byte[]?>? _stmtBefore;
    private static readonly byte[] SameAsTransaction = new byte[1];

    private long _lastTxnId;
    private long _commits;
    private long _checkpoints;
    private bool _poisoned;
    private bool _disposed;

    public RecoveryInfo LastRecovery { get; }
    public bool SyncOnCommit { get; set; } = true;
    public long CheckpointThresholdBytes { get; set; } = 4 * 1024 * 1024;
    public bool InWriteTransaction => _txnBefore != null;
    public int CatalogRoot { get; private set; }

    /// <summary>Raised after a checkpoint finishes (used by tests and the crash worker's stats).</summary>
    public event Action? CheckpointCompleted;

    private Pager(string path, int cachePages)
    {
        _disk = new DiskManager(path);
        try
        {
            _wal = new WriteAheadLog(path + "-wal");
        }
        catch
        {
            _disk.Dispose();
            throw;
        }
        _pool = new BufferPool(_disk, cachePages, _wal.SyncIfNeeded);

        try
        {
            // 1. Redo committed work found in the WAL straight into the data file.
            LastRecovery = _wal.Recover((pageId, image) => _disk.Write(pageId, image), out _lastTxnId);
            if (LastRecovery.PagesRedone > 0) _disk.Sync();
            _wal.Reset();

            // 2. Create or validate the file.
            if (_disk.Length == 0)
            {
                InitializeNewDatabase();
            }
            else
            {
                var header = Read(0);
                HeaderPage.Validate(header);
                CatalogRoot = HeaderPage.GetCatalogRoot(header);
            }
        }
        catch
        {
            _wal.Dispose();
            _disk.Dispose();
            throw;
        }
    }

    public static Pager Open(string path, int cachePages = 2048) => new(path, cachePages);

    public int PageCount => HeaderPage.GetPageCount(Read(0));

    private void InitializeNewDatabase()
    {
        BeginTransaction();
        var headerFrame = _pool.CreateForTransaction(0);
        _txnBefore![0] = null;
        HeaderPage.Initialize(headerFrame.Data, catalogRoot: 1);
        int catalog = Allocate();
        CatalogPages.Initialize(Write(catalog));
        CatalogRoot = catalog;
        Commit();
        Checkpoint();
    }

    // ---- page access -------------------------------------------------------------------

    /// <summary>Returns a page for reading. Callers must not modify the returned bytes.</summary>
    public byte[] Read(int pageId)
    {
        CheckOpen();
        return _pool.Get(pageId).Data;
    }

    /// <summary>
    /// Returns a page for modification inside the running write transaction.
    /// The first call for a page records its before-image and pins it in memory.
    /// </summary>
    public byte[] Write(int pageId)
    {
        CheckOpen();
        if (_txnBefore == null) throw new InvalidOperationException("page write outside a write transaction");
        var frame = _pool.Get(pageId);
        bool firstTouch = !frame.InTransaction;
        if (firstTouch)
        {
            _txnBefore[pageId] = (byte[])frame.Data.Clone();
            _pool.Pin(frame);
        }
        if (_stmtBefore != null && !_stmtBefore.ContainsKey(pageId))
        {
            // Statement images are short-lived, so they come from a pool instead of the GC heap.
            // If the transaction image was taken just now, the statement can share it.
            if (firstTouch) _stmtBefore[pageId] = SameAsTransaction;
            else
            {
                var copy = ArrayPool<byte>.Shared.Rent(PageSize);
                frame.Data.CopyTo(copy, 0);
                _stmtBefore[pageId] = copy;
            }
        }
        return frame.Data;
    }

    /// <summary>Allocates a new zero-filled page at the end of the file and returns its id.</summary>
    public int Allocate()
    {
        var header = Write(0);
        int id = HeaderPage.GetPageCount(header);
        HeaderPage.SetPageCount(header, id + 1);
        _pool.CreateForTransaction(id);
        _txnBefore![id] = null;
        if (_stmtBefore != null) _stmtBefore[id] = null;
        return id;
    }

    // ---- transactions ------------------------------------------------------------------

    public void BeginTransaction()
    {
        CheckOpen();
        if (_poisoned) throw new BasaltException("the database hit an I/O error while committing; reopen it to recover");
        if (_txnBefore != null) throw new InvalidOperationException("a write transaction is already running");
        _txnBefore = new Dictionary<int, byte[]?>();
    }

    /// <summary>Makes the transaction durable: WAL append + fsync. Then the pages become evictable.</summary>
    public void Commit()
    {
        var before = _txnBefore ?? throw new InvalidOperationException("no write transaction");
        EndStatement();
        if (before.Count == 0)
        {
            _txnBefore = null;
            return;
        }

        var ids = before.Keys.OrderBy(i => i).ToArray();
        var frames = new Frame[ids.Length];
        var images = new List<(int, byte[])>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            frames[i] = _pool.TryGetResident(ids[i]) ?? throw new InvalidOperationException($"transaction page {ids[i]} is not resident");
            images.Add((ids[i], frames[i].Data));
        }

        try
        {
            _wal.AppendCommit(++_lastTxnId, images, SyncOnCommit);
        }
        catch (Exception ex)
        {
            Rollback();
            _poisoned = true;
            throw new BasaltException("commit failed while writing the WAL; the transaction was rolled back", ex);
        }

        foreach (var frame in frames) _pool.Unpin(frame);
        _txnBefore = null;
        _commits++;
        _pool.Trim();

        // The commit is durable at this point. A failed automatic checkpoint must not be reported
        // as a failed commit: the WAL still holds everything, and the next commit tries again.
        if (_wal.Length > CheckpointThresholdBytes)
        {
            try { Checkpoint(); }
            catch (IOException) { }
        }
    }

    /// <summary>Puts every changed page back the way it was when the transaction began.</summary>
    public void Rollback()
    {
        var before = _txnBefore ?? throw new InvalidOperationException("no write transaction");
        EndStatement();
        foreach (var (id, image) in before)
        {
            if (image == null)
            {
                _pool.Drop(id);
                continue;
            }
            var frame = _pool.TryGetResident(id) ?? throw new InvalidOperationException($"transaction page {id} is not resident");
            image.CopyTo(frame.Data, 0);
            // The restored bytes are the committed version; marking it dirty only risks a redundant write.
            _pool.Unpin(frame);
        }
        _txnBefore = null;
        _pool.Trim();
    }

    /// <summary>Starts a statement-level savepoint inside an explicit transaction.</summary>
    public void BeginStatement()
    {
        if (_txnBefore == null) throw new InvalidOperationException("no write transaction");
        _stmtBefore = new Dictionary<int, byte[]?>();
    }

    public void EndStatement()
    {
        if (_stmtBefore == null) return;
        ReturnStatementImages(_stmtBefore);
        _stmtBefore = null;
    }

    private static void ReturnStatementImages(Dictionary<int, byte[]?> images)
    {
        foreach (var image in images.Values)
            if (image != null && !ReferenceEquals(image, SameAsTransaction)) ArrayPool<byte>.Shared.Return(image);
    }

    /// <summary>Undoes only the current statement; the transaction stays open.</summary>
    public void RollbackStatement()
    {
        var stmt = _stmtBefore ?? throw new InvalidOperationException("no statement savepoint");
        foreach (var (id, image) in stmt)
        {
            if (image == null)
            {
                _pool.Drop(id);
                _txnBefore!.Remove(id);
                continue;
            }
            var frame = _pool.TryGetResident(id) ?? throw new InvalidOperationException($"statement page {id} is not resident");
            var source = ReferenceEquals(image, SameAsTransaction) ? _txnBefore![id]! : image;
            source.AsSpan(0, PageSize).CopyTo(frame.Data);
        }
        ReturnStatementImages(stmt);
        _stmtBefore = null;
    }

    // ---- checkpoint / shutdown ---------------------------------------------------------

    /// <summary>Copies all committed pages into the data file, fsyncs it, then empties the WAL.</summary>
    public void Checkpoint()
    {
        CheckOpen();
        if (_txnBefore != null) throw new InvalidOperationException("cannot checkpoint inside a write transaction");
        if (_poisoned) return;
        _pool.FlushDirty();
        _disk.Sync();   // the data file must be durable before the WAL that backs it is thrown away
        _wal.Reset();
        _checkpoints++;
        CheckpointCompleted?.Invoke();
    }

    public StorageStats GetStats() => new(
        PageCount, _pool.Count, _pool.Hits, _pool.Misses, _pool.Evictions,
        _disk.PageReads, _disk.PageWrites, _wal.Length, _wal.BytesWritten, _wal.Syncs,
        _commits, _checkpoints);

    /// <summary>
    /// Test hook: closes the files without writing cached pages and without a checkpoint,
    /// leaving the data file and WAL exactly as a killed process would.
    /// </summary>
    public void SimulateCrash()
    {
        if (_disposed) return;
        _pool.DiscardAll();
        _txnBefore = null;
        _stmtBefore = null;
        _disposed = true;
        _wal.Dispose();
        _disk.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (_txnBefore != null) Rollback();
            Checkpoint();
        }
        finally
        {
            _disposed = true;
            _wal.Dispose();
            _disk.Dispose();
        }
    }

    private void CheckOpen()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Pager), "the database is closed");
    }
}
