using Basalt.Storage;

namespace Basalt.Tests;

/// <summary>
/// Crash recovery. SimulateCrash() closes the files without writing cached pages or checkpointing,
/// which leaves the data file and WAL exactly as a killed process would.
/// </summary>
public class RecoveryTests
{
    private static BasaltOptions NoAutoCheckpoint => new() { CheckpointThresholdBytes = long.MaxValue };

    private static BasaltDatabase Fresh(string path)
    {
        var db = BasaltDatabase.Open(path, NoAutoCheckpoint);
        db.Execute("CREATE TABLE t (id INT PRIMARY KEY, v TEXT NOT NULL)");
        db.Execute("CREATE INDEX idx_v ON t (v)");
        return db;
    }

    [Fact]
    public void Committed_work_survives_a_crash_before_any_checkpoint()
    {
        using var dir = new TempDir();
        string path = dir.File("r.db");
        var db = Fresh(path);
        for (int i = 0; i < 300; i++) db.Execute("INSERT INTO t VALUES (?, ?)", i, $"value {i}");
        db.Execute("UPDATE t SET v = 'changed' WHERE id < 10");
        db.Execute("DELETE FROM t WHERE id >= 290");
        db.SimulateCrash();

        using var reopened = BasaltDatabase.Open(path);
        Assert.True(reopened.Recovery.CommittedTransactions >= 300);
        Assert.Equal(290, reopened.Execute("SELECT COUNT(*) FROM t").Long());
        Assert.Equal(10, reopened.Execute("SELECT COUNT(*) FROM t WHERE v = 'changed'").Long());
        Assert.True(reopened.CheckIntegrity().Ok, reopened.CheckIntegrity().ToString());
    }

    [Fact]
    public void Uncommitted_transaction_is_discarded()
    {
        using var dir = new TempDir();
        string path = dir.File("r.db");
        var db = Fresh(path);
        db.Execute("INSERT INTO t VALUES (1, 'kept')");
        db.Execute("BEGIN");
        for (int i = 2; i < 2000; i++) db.Execute("INSERT INTO t VALUES (?, 'lost')", i);
        db.Execute("CREATE TABLE ghost (x INT)");
        db.SimulateCrash();

        using var reopened = BasaltDatabase.Open(path);
        Assert.Equal(["kept"], reopened.Execute("SELECT v FROM t").Strings());
        Assert.DoesNotContain(reopened.Tables, t => t.Name == "ghost");
        Assert.True(reopened.CheckIntegrity().Ok);
    }

    /// <summary>
    /// Cut the WAL at many points (a torn write at power loss can leave any prefix). Recovery must
    /// produce exactly the state after some number of complete commits, never a partial one.
    /// </summary>
    [Fact]
    public void Truncated_wal_recovers_to_a_prefix_of_commits()
    {
        using var dir = new TempDir();
        string path = dir.File("r.db");
        var db = Fresh(path);
        db.Checkpoint();
        // Each transaction inserts 5 rows, so any valid state has a row count divisible by 5.
        for (int t = 0; t < 30; t++)
        {
            db.Execute("BEGIN");
            for (int j = 0; j < 5; j++) db.Execute("INSERT INTO t VALUES (?, ?)", t * 5 + j, new string('x', 50 + t));
            db.Execute("COMMIT");
        }
        db.SimulateCrash();

        byte[] data = File.ReadAllBytes(path);
        byte[] wal = File.ReadAllBytes(path + "-wal");
        var rng = new Random(3);
        var cuts = Enumerable.Range(0, 25).Select(_ => rng.Next(0, wal.Length)).Append(wal.Length).Append(0).Append(31).ToList();
        long previous = -1;
        foreach (int cut in cuts.Order())
        {
            File.WriteAllBytes(path, data);
            File.WriteAllBytes(path + "-wal", wal[..cut]);
            using var r = BasaltDatabase.Open(path);
            long count = r.Execute("SELECT COUNT(*) FROM t").Long();
            Assert.Equal(0, count % 5);
            Assert.Equal(count / 5, r.Recovery.CommittedTransactions);
            Assert.True(count >= previous, "a longer log can never recover less");
            previous = count;
            var ids = r.Execute("SELECT id FROM t ORDER BY id").Longs();
            Assert.Equal(Enumerable.Range(0, (int)count).Select(i => (long)i), ids);
            Assert.True(r.CheckIntegrity().Ok);
        }
        Assert.Equal(150, previous);
    }

    [Fact]
    public void Corrupted_record_stops_replay_at_the_last_good_commit()
    {
        using var dir = new TempDir();
        string path = dir.File("r.db");
        var db = Fresh(path);
        db.Checkpoint();
        for (int i = 0; i < 20; i++) db.Execute("INSERT INTO t VALUES (?, 'x')", i);
        db.SimulateCrash();

        byte[] data = File.ReadAllBytes(path);
        byte[] wal = File.ReadAllBytes(path + "-wal");
        int middle = wal.Length / 2;
        wal[middle] ^= 0x5A; // a single flipped bit pattern in the middle of the log
        File.WriteAllBytes(path, data);
        File.WriteAllBytes(path + "-wal", wal);

        using var r = BasaltDatabase.Open(path);
        long count = r.Execute("SELECT COUNT(*) FROM t").Long();
        Assert.InRange(count, 1, 19);
        Assert.True(r.Recovery.HadIncompleteTail);
        Assert.Equal(Enumerable.Range(0, (int)count).Select(i => (long)i), r.Execute("SELECT id FROM t ORDER BY id").Longs());
        Assert.True(r.CheckIntegrity().Ok);
    }

    /// <summary>
    /// The write-ahead rule with fsync on commit turned off: before a dirty page is written to the
    /// data file (eviction or checkpoint), the WAL records describing it must be fsynced. Otherwise a
    /// power cut could leave half a transaction in the data file with no log to repair it.
    /// </summary>
    [Fact]
    public void With_sync_off_the_wal_is_fsynced_before_pages_reach_the_data_file()
    {
        using var dir = new TempDir();
        using var pager = Pager.Open(dir.File("r.db"), cachePages: 16);
        pager.SyncOnCommit = false;
        long syncsBefore = pager.GetStats().WalSyncs;
        long writesBefore = pager.GetStats().DataFileWrites;

        // Each transaction allocates and fills a page; 60 of them overflow a 16-page cache.
        for (int t = 0; t < 60; t++)
        {
            pager.BeginTransaction();
            int id = pager.Allocate();
            pager.Write(id)[100] = (byte)t;
            pager.Commit();
        }

        var stats = pager.GetStats();
        Assert.True(stats.DataFileWrites > writesBefore, "dirty pages should have been evicted to the data file");
        Assert.True(stats.WalSyncs > syncsBefore, "the WAL must be fsynced before evicted pages are written");
    }

    [Fact]
    public void Recovery_is_idempotent_when_interrupted_and_rerun()
    {
        using var dir = new TempDir();
        string path = dir.File("r.db");
        var db = Fresh(path);
        for (int i = 0; i < 100; i++) db.Execute("INSERT INTO t VALUES (?, 'v')", i);
        db.SimulateCrash();

        // Crash after recovery wrote pages to the data file but before it reset the WAL:
        // replaying the same full page images a second time must give the same result.
        byte[] wal = File.ReadAllBytes(path + "-wal");
        using (var once = BasaltDatabase.Open(path)) Assert.Equal(100, once.Execute("SELECT COUNT(*) FROM t").Long());
        File.WriteAllBytes(path + "-wal", wal);
        using var twice = BasaltDatabase.Open(path);
        Assert.Equal(100, twice.Execute("SELECT COUNT(*) FROM t").Long());
        Assert.True(twice.CheckIntegrity().Ok);
    }

    [Fact]
    public void Checkpoint_moves_data_into_the_file_and_empties_the_wal()
    {
        using var dir = new TempDir();
        string path = dir.File("r.db");
        var db = Fresh(path);
        for (int i = 0; i < 100; i++) db.Execute("INSERT INTO t VALUES (?, 'v')", i);
        Assert.True(db.GetStats().WalBytes > 32);
        db.Checkpoint();
        Assert.Equal(32, db.GetStats().WalBytes); // just the header
        db.SimulateCrash();

        File.Delete(path + "-wal"); // the data file alone must now be complete
        using var r = BasaltDatabase.Open(path);
        Assert.Equal(0, r.Recovery.CommittedTransactions);
        Assert.Equal(100, r.Execute("SELECT COUNT(*) FROM t").Long());
    }

    [Fact]
    public void Automatic_checkpoints_and_evictions_under_a_tiny_cache_lose_nothing()
    {
        using var dir = new TempDir();
        string path = dir.File("r.db");
        var options = new BasaltOptions { CachePages = 16, CheckpointThresholdBytes = 64 * 1024 };
        var db = BasaltDatabase.Open(path, options);
        db.Execute("CREATE TABLE t (id INT PRIMARY KEY, v TEXT NOT NULL)");
        db.Execute("CREATE INDEX idx_v ON t (v)");
        for (int i = 0; i < 1500; i++) db.Execute("INSERT INTO t VALUES (?, ?)", i, new string((char)('a' + i % 26), 100 + i % 50));
        Assert.True(db.GetStats().Checkpoints > 1);
        Assert.True(db.GetStats().Evictions > 0);
        db.SimulateCrash();

        using var r = BasaltDatabase.Open(path, options);
        Assert.Equal(1500, r.Execute("SELECT COUNT(*) FROM t").Long());
        Assert.True(r.CheckIntegrity().Ok, r.CheckIntegrity().ToString());
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_database()
    {
        using var dir = new TempDir();
        string path = dir.File("junk.db");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'z', 8192).ToArray());
        Assert.Throws<CorruptionException>(() => BasaltDatabase.Open(path));
    }
}
