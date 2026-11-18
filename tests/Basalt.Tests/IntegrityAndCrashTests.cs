using Basalt.CrashTest;
using Basalt.Storage;

namespace Basalt.Tests;

public class IntegrityTests
{
    [Fact]
    public void Healthy_database_passes()
    {
        using var dir = new TempDir();
        using var db = BasaltDatabase.Open(dir.File("i.db"));
        db.Execute("CREATE TABLE t (id INT PRIMARY KEY, v TEXT)");
        db.Execute("CREATE INDEX iv ON t (v)");
        for (int i = 0; i < 3000; i++) db.Execute("INSERT INTO t VALUES (?, ?)", i, "v" + (i % 13));
        db.Execute("DELETE FROM t WHERE id % 3 = 0");
        var report = db.CheckIntegrity();
        Assert.True(report.Ok, report.ToString());
        Assert.Equal(2000, report.Rows);
        Assert.Equal(2, report.Indexes);
        Assert.Equal(4000, report.IndexEntries);
    }

    [Fact]
    public void Detects_a_damaged_heap_page_on_disk()
    {
        using var dir = new TempDir();
        string path = dir.File("i.db");
        int heapPage;
        using (var db = BasaltDatabase.Open(path))
        {
            db.Execute("CREATE TABLE t (id INT PRIMARY KEY, v TEXT)");
            for (int i = 0; i < 200; i++) db.Execute("INSERT INTO t VALUES (?, ?)", i, "row " + i);
            heapPage = db.Tables.Single().HeapFirstPage;
        }
        // Point slot 0 of the first heap page outside the page.
        using (var f = new FileStream(path, FileMode.Open))
        {
            f.Position = (long)heapPage * Pager.PageSize + SlottedPage.HeaderSize;
            f.Write([0xFF, 0x0F]);
        }
        using var reopened = BasaltDatabase.Open(path);
        var report = reopened.CheckIntegrity();
        Assert.False(report.Ok);
        Assert.Contains(report.Problems, p => p.Contains("table t"));
    }

    [Fact]
    public void Detects_an_index_entry_without_a_row()
    {
        using var dir = new TempDir();
        string path = dir.File("i.db");
        using var db = BasaltDatabase.Open(path);
        db.Execute("CREATE TABLE t (id INT PRIMARY KEY, v TEXT)");
        db.Execute("INSERT INTO t VALUES (1, 'a'), (2, 'b')");
        // Delete the row behind the engine's back, leaving the primary key entry dangling.
        var table = db.Tables.Single();
        db.Pager.BeginTransaction();
        new TableHeap(db.Pager, table.HeapFirstPage).Delete(new RowId(table.HeapFirstPage, 0));
        db.Pager.Commit();
        var report = db.CheckIntegrity();
        Assert.False(report.Ok);
        Assert.Contains(report.Problems, p => p.Contains("missing row"));
    }
}

/// <summary>A short run of the real kill-and-verify harness; the full run is the Basalt.CrashTest program.</summary>
public class CrashHarnessSmokeTests
{
    [Fact]
    public void Killing_the_worker_repeatedly_loses_no_acknowledged_commit()
    {
        using var dir = new TempDir();
        string workerDll = Path.Combine(AppContext.BaseDirectory, "Basalt.CrashTest.dll");
        Assert.True(File.Exists(workerDll), "Basalt.CrashTest.dll should be copied next to the tests");
        var log = new StringWriter();
        var options = new HarnessOptions
        {
            Cycles = 12,
            Seed = 777,
            Directory = dir.Path,
            MinKillMs = 30,
            MaxKillMs = 250,
            EarlyKillShare = 0.5, // half the cycles also kill a restarted worker while it starts up and recovers
            WorkerFile = "dotnet",
            WorkerPrefixArgs = { workerDll },
            Log = log,
        };
        var result = Harness.Run(options);
        Assert.True(result.Failures == 0, string.Join("\n", result.Problems) + "\n" + log);
        Assert.Equal(12, result.CyclesRun);
        Assert.True(result.EarlyKills > 0, "some cycles should kill a worker right after a restart");
        Assert.Equal(12 + result.EarlyKills, result.Kills);
        Assert.Equal(0, result.LostCommits);
        Assert.True(result.AckedCommits > 0, "the worker should have committed something");
    }
}
