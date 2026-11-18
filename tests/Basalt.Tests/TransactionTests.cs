namespace Basalt.Tests;

public class TransactionTests
{
    private static BasaltDatabase Accounts(TempDir dir, int n = 10)
    {
        var db = BasaltDatabase.Open(dir.File("tx.db"));
        db.Execute("CREATE TABLE accounts (id INT PRIMARY KEY, owner TEXT, balance INT NOT NULL)");
        db.Execute("CREATE INDEX idx_owner ON accounts (owner)");
        for (int i = 1; i <= n; i++) db.Execute("INSERT INTO accounts VALUES (?, ?, 100)", i, $"o{i}");
        return db;
    }

    [Fact]
    public void Rollback_undoes_inserts_updates_deletes_and_ddl()
    {
        using var dir = new TempDir();
        using var db = Accounts(dir);
        db.Execute("BEGIN");
        db.Execute("INSERT INTO accounts VALUES (11, 'new', 5)");
        db.Execute("UPDATE accounts SET balance = 0, owner = 'zed' WHERE id <= 5");
        db.Execute("DELETE FROM accounts WHERE id > 8");
        db.Execute("CREATE TABLE scratch (x INT)");
        db.Execute("CREATE INDEX idx_balance ON accounts (balance)");
        Assert.Equal(8, db.Execute("SELECT COUNT(*) FROM accounts").Long());
        db.Execute("ROLLBACK");

        Assert.Equal(10, db.Execute("SELECT COUNT(*) FROM accounts").Long());
        Assert.Equal(1000, db.Execute("SELECT SUM(balance) FROM accounts").Long());
        Assert.Empty(db.Execute("SELECT * FROM accounts WHERE owner = 'zed'").Rows);
        Assert.DoesNotContain(db.Tables, t => t.Name == "scratch");
        Assert.Single(db.Tables.Single(t => t.Name == "accounts").Indexes, i => !i.IsPrimary);
        Assert.True(db.CheckIntegrity().Ok);
    }

    [Fact]
    public void Commit_makes_changes_visible_and_durable()
    {
        using var dir = new TempDir();
        string path;
        using (var db = Accounts(dir))
        {
            path = db.Path;
            db.Execute("BEGIN");
            db.Execute("UPDATE accounts SET balance = balance - 30 WHERE id = 1");
            db.Execute("UPDATE accounts SET balance = balance + 30 WHERE id = 2");
            db.Execute("COMMIT");
        }
        using var again = BasaltDatabase.Open(path);
        Assert.Equal([70L, 130L], again.Execute("SELECT balance FROM accounts WHERE id <= 2 ORDER BY id").Longs());
    }

    [Fact]
    public void Failed_statement_in_transaction_is_undone_alone()
    {
        using var dir = new TempDir();
        using var db = Accounts(dir);
        db.Execute("BEGIN");
        db.Execute("INSERT INTO accounts VALUES (20, 'a', 1)");
        // The second row is a duplicate: the whole statement (including row 21) is undone.
        Assert.Throws<ConstraintException>(() => db.Execute("INSERT INTO accounts VALUES (21, 'b', 1), (1, 'dup', 1)"));
        Assert.True(db.InTransaction);
        db.Execute("COMMIT");
        Assert.Equal([20L], db.Execute("SELECT id FROM accounts WHERE id > 10").Longs());
        Assert.True(db.CheckIntegrity().Ok);
    }

    [Fact]
    public void Failed_autocommit_statement_changes_nothing()
    {
        using var dir = new TempDir();
        using var db = Accounts(dir);
        Assert.Throws<ConstraintException>(() => db.Execute("UPDATE accounts SET id = 5 WHERE id = 6"));
        Assert.Throws<ConstraintException>(() => db.Execute("UPDATE accounts SET balance = NULL WHERE id > 3"));
        Assert.Equal(10, db.Execute("SELECT COUNT(*) FROM accounts").Long());
        Assert.Equal(1000, db.Execute("SELECT SUM(balance) FROM accounts").Long());
        Assert.True(db.CheckIntegrity().Ok);
    }

    [Fact]
    public void Transaction_state_errors()
    {
        using var dir = new TempDir();
        using var db = Accounts(dir);
        Assert.Throws<BasaltException>(() => db.Execute("COMMIT"));
        Assert.Throws<BasaltException>(() => db.Execute("ROLLBACK"));
        db.Execute("BEGIN");
        Assert.Throws<BasaltException>(() => db.Execute("BEGIN"));
        db.Execute("ROLLBACK");
    }

    [Fact]
    public void Second_writer_waits_then_times_out()
    {
        using var dir = new TempDir();
        string path = dir.File("lock.db");
        using var db = BasaltDatabase.Open(path, new BasaltOptions { LockTimeout = TimeSpan.FromMilliseconds(200) });
        db.Execute("CREATE TABLE t (x INT)");
        using var a = db.OpenSession();
        using var b = db.OpenSession();
        a.Execute("BEGIN");
        a.Execute("INSERT INTO t VALUES (1)");
        var ex = Assert.Throws<BasaltException>(() => b.Execute("INSERT INTO t VALUES (2)"));
        Assert.Contains("locked", ex.Message);
        // Readers also wait for the writer: they never see uncommitted rows.
        Assert.Throws<BasaltException>(() => b.Execute("SELECT COUNT(*) FROM t"));
        a.Execute("COMMIT");
        Assert.Equal(1, b.Execute("SELECT COUNT(*) FROM t").Long());
    }

    [Fact]
    public void Lock_lets_waiting_readers_in_between_back_to_back_writers()
    {
        var gate = new Basalt.Engine.DatabaseLock();
        var timeout = TimeSpan.FromSeconds(10);
        bool writerDone = false;
        int readsWhileWriting = 0;
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref writerDone))
            {
                gate.EnterRead(timeout);
                if (!Volatile.Read(ref writerDone)) Interlocked.Increment(ref readsWhileWriting);
                gate.ExitRead();
            }
        });
        reader.Start();
        Thread.Sleep(20);
        for (int i = 0; i < 2000; i++)
        {
            gate.EnterWrite(timeout);
            Thread.SpinWait(200);
            gate.ExitWrite(); // no pause before the next EnterWrite
        }
        Volatile.Write(ref writerDone, true);
        reader.Join();
        Assert.True(readsWhileWriting > 100, $"reader got in only {readsWhileWriting} times during 2000 writes");
    }

    /// <summary>
    /// Writers move money between accounts in transactions while readers sum all balances.
    /// Under the readers-writer lock every reader must see the same total.
    /// </summary>
    [Fact]
    public void Concurrent_readers_never_see_a_half_done_transfer()
    {
        using var dir = new TempDir();
        using var db = Accounts(dir, 20);
        long expected = 20 * 100;
        using var cts = new CancellationTokenSource();
        int reads = 0;
        var errors = new List<string>();

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Factory.StartNew(() =>
        {
            using var s = db.OpenSession();
            while (!cts.IsCancellationRequested)
            {
                long total = s.Execute("SELECT SUM(balance) FROM accounts").Long();
                if (total != expected) lock (errors) errors.Add($"saw total {total}");
                Interlocked.Increment(ref reads);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        var writer = Task.Factory.StartNew(() =>
        {
            // Start writing only once the readers are running, so the two really overlap.
            SpinWait.SpinUntil(() => Volatile.Read(ref reads) >= 3, TimeSpan.FromSeconds(10));
            using var s = db.OpenSession();
            var rng = new Random(5);
            for (int i = 0; i < 200; i++)
            {
                int from = rng.Next(1, 21), to = rng.Next(1, 21), amount = rng.Next(1, 20);
                s.Execute("BEGIN");
                s.Execute("UPDATE accounts SET balance = balance - ? WHERE id = ?", amount, from);
                s.Execute("UPDATE accounts SET balance = balance + ? WHERE id = ?", amount, to);
                s.Execute("COMMIT");
            }
        }, TaskCreationOptions.LongRunning);

        writer.Wait(TimeSpan.FromSeconds(60));
        cts.Cancel();
        Task.WaitAll(readers, TimeSpan.FromSeconds(30));
        Assert.Empty(errors);
        Assert.True(reads > 3, $"readers only managed {reads} reads");
        Assert.Equal(expected, db.Execute("SELECT SUM(balance) FROM accounts").Long());
    }
}
