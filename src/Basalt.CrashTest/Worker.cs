namespace Basalt.CrashTest;

/// <summary>
/// The process that gets killed. It opens the database (running recovery), then commits
/// transactions forever, printing "ACK n" only after COMMIT for transaction n has returned,
/// which is after the WAL fsync. Each ACK is a promise the harness later checks.
/// Output: "READY" once the database is open, "RESUME n" (last committed transaction), then "ACK n" lines.
/// </summary>
internal static class Worker
{
    public static BasaltOptions Options => new()
    {
        // Small cache and small checkpoint threshold so kills also land during evictions and checkpoints.
        CachePages = 64,
        CheckpointThresholdBytes = 256 * 1024,
    };

    public static int Run(string path, ulong seed)
    {
        using var db = BasaltDatabase.Open(path, Options);
        EnsureSchema(db);
        Console.Out.WriteLine("READY");

        long txn = db.Execute("SELECT last_txn FROM meta WHERE k = 1").Scalar().AsInt + 1;
        // The last committed transaction after recovery. The harness needs it when it kills two
        // workers in a row, because the first one may have committed a transaction without its ACK.
        Console.Out.WriteLine($"RESUME {txn - 1}");
        var insertLog = db.Prepare("INSERT INTO log VALUES (?, ?, ?)");
        var debit = db.Prepare("UPDATE accounts SET balance = balance - ? WHERE id = ?");
        var credit = db.Prepare("UPDATE accounts SET balance = balance + ? WHERE id = ?");
        var rename = db.Prepare("UPDATE accounts SET owner = ? WHERE id = ?");
        var deleteTxn = db.Prepare("DELETE FROM log WHERE txn = ?");
        var rewrite = db.Prepare("UPDATE log SET note = ? WHERE txn = ?");
        var setMeta = db.Prepare("UPDATE meta SET last_txn = ? WHERE k = 1");

        while (true)
        {
            var s = Workload.Generate(seed, txn);
            db.Execute("BEGIN");
            foreach (var (id, note) in s.Inserts) insertLog.Execute(id, txn, note);
            foreach (var (from, to, amount) in s.Transfers)
            {
                debit.Execute(amount, from);
                credit.Execute(amount, to);
            }
            if (s.Rename is { } r) rename.Execute(r.Owner, r.Account);
            if (s.DeleteTxn is { } d) deleteTxn.Execute(d);
            if (s.Rewrite is { } w) rewrite.Execute(w.Note, w.OfTxn);
            setMeta.Execute(txn);

            if (s.RollsBack)
            {
                db.Execute("ROLLBACK");
            }
            else
            {
                db.Execute("COMMIT");
                Console.Out.WriteLine($"ACK {txn}");
            }
            txn++;
        }
    }

    private static void EnsureSchema(BasaltDatabase db)
    {
        if (db.Tables.Any(t => t.Name == "meta")) return;
        // Schema and seed rows are one transaction, so a kill here leaves either nothing or everything.
        db.Execute("BEGIN");
        db.Execute("CREATE TABLE accounts (id INT PRIMARY KEY, owner TEXT NOT NULL, balance INT NOT NULL)");
        db.Execute("CREATE INDEX idx_accounts_owner ON accounts (owner)");
        db.Execute("CREATE TABLE log (id INT PRIMARY KEY, txn INT NOT NULL, note TEXT)");
        db.Execute("CREATE INDEX idx_log_txn ON log (txn)");
        db.Execute("CREATE TABLE meta (k INT PRIMARY KEY, last_txn INT NOT NULL)");
        var ins = db.Prepare("INSERT INTO accounts VALUES (?, ?, ?)");
        for (int a = 1; a <= Workload.Accounts; a++) ins.Execute(a, Workload.InitialOwner(a), Workload.InitialBalance);
        db.Execute("INSERT INTO meta VALUES (1, 0)");
        db.Execute("COMMIT");
    }
}
