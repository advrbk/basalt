using System.Diagnostics;

namespace Basalt.CrashTest;

internal sealed class HarnessOptions
{
    public int Cycles { get; set; } = 300;
    public ulong Seed { get; set; } = 12345;
    public string Directory { get; set; } = Path.Combine(Environment.CurrentDirectory, "crash-data");
    /// <summary>Kill the worker this many ms after it reports READY (picked at random in the range).</summary>
    public int MinKillMs { get; set; } = 20;
    public int MaxKillMs { get; set; } = 400;
    /// <summary>
    /// Share of cycles with a second, blind kill soon after process start. In those cycles a first
    /// worker is killed normally, so the WAL still holds committed work that nobody has recovered,
    /// then a second worker is started and killed 0-150 ms later, which can land during its recovery.
    /// </summary>
    public double EarlyKillShare { get; set; } = 0.1;
    /// <summary>Program + leading arguments that start the worker; "worker &lt;db&gt; &lt;seed&gt;" is appended.</summary>
    public string WorkerFile { get; set; } = "";
    public List<string> WorkerPrefixArgs { get; set; } = new();
    public TextWriter Log { get; set; } = Console.Out;
    public bool Verbose { get; set; }
}

internal sealed record HarnessResult(
    int CyclesRun, int Kills, int Failures, long LostCommits, long AckedCommits, long LastTxn, int CommittedButUnacked,
    int EarlyKills, int EarlyKillsBeforeReady, int RecoveriesWithWork, long RowsAtEnd, long PagesAtEnd, TimeSpan Elapsed,
    List<string> Problems);

/// <summary>
/// Kill-and-verify loop:
/// 1. start the worker process on the database,
/// 2. kill it (TerminateProcess) at a random moment (in some cycles twice in a row, the second time
///    blindly right after start, so the kill can interrupt the worker's own recovery),
/// 3. reopen the database in this process (this runs recovery),
/// 4. check that every acknowledged commit is there, nothing uncommitted is, the data equals the
///    model's prediction exactly, and the integrity checker finds nothing wrong,
/// 5. repeat on the same file.
/// </summary>
internal static class Harness
{
    public static HarnessResult Run(HarnessOptions o)
    {
        System.IO.Directory.CreateDirectory(o.Directory);
        string db = Path.Combine(o.Directory, "crash.db");
        foreach (var f in new[] { db, db + "-wal" }) if (File.Exists(f)) File.Delete(f);

        var rng = new Random((int)(o.Seed & 0x7FFFFFFF));
        var model = new Model();
        var problems = new List<string>();
        long ackedTotal = 0, lastAck = 0;
        int unacked = 0, early = 0, earlyBeforeReady = 0, recoveries = 0, failures = 0, cyclesRun = 0, kills = 0;
        long lost = 0;
        long rows = 0, pages = 0;
        var sw = Stopwatch.StartNew();

        for (int cycle = 1; cycle <= o.Cycles; cycle++)
        {
            bool earlyKill = rng.NextDouble() < o.EarlyKillShare;
            int delay = rng.Next(o.MinKillMs, o.MaxKillMs + 1);
            var runs = new List<WorkerRun> { RunWorkerAndKill(o, db, earlyKill: false, delay) };
            if (earlyKill && !runs[0].ExitedEarly)
            {
                // The first kill left committed work in the WAL; this worker has to recover it.
                delay = rng.Next(0, 150);
                runs.Add(RunWorkerAndKill(o, db, earlyKill: true, delay));
            }
            cyclesRun++;
            kills += runs.Count;

            string? runProblem = null;
            foreach (var run in runs)
            {
                if (run.ExitedEarly) { runProblem = $"worker exited by itself: {run.Stderr}"; break; }
                if (run.Resume >= 0 && run.Resume < lastAck)
                {
                    runProblem = $"LOST COMMITS: transaction {lastAck} was acknowledged but a restarted worker found only up to {run.Resume}";
                    break;
                }
                if (run.Resume > lastAck)
                {
                    // The previous worker committed this durably but was killed before printing the ACK.
                    unacked++;
                    ackedTotal += CountCommitted(o.Seed, lastAck, run.Resume);
                    lastAck = run.Resume;
                }
                if (run.LastAck > lastAck)
                {
                    ackedTotal += CountCommitted(o.Seed, lastAck, run.LastAck);
                    lastAck = run.LastAck;
                }
            }
            if (runs.Count == 2)
            {
                early++;
                if (!runs[1].SawReady) earlyBeforeReady++;
            }
            if (runProblem != null)
            {
                problems.Add($"cycle {cycle}: {runProblem}");
                failures++;
                break;
            }

            var cycleProblems = Verify(o, db, model, lastAck, out long found, out var recovery, out rows, out pages);
            if (found < lastAck) lost += CountCommitted(o.Seed, found, lastAck);
            if (recovery.CommittedTransactions > 0) recoveries++;
            if (found > lastAck)
            {
                // Committed and fsynced, but killed before it could print ACK. Allowed, and now promised.
                unacked++;
                ackedTotal += CountCommitted(o.Seed, lastAck, found);
                lastAck = found;
            }

            if (o.Verbose || cycle % 25 == 0 || cycleProblems.Count > 0)
                o.Log.WriteLine($"cycle {cycle,4}: kill after {delay,3} ms{(earlyKill ? " (early)" : "")}, last acked txn {lastAck}, " +
                                $"recovery redid {recovery.CommittedTransactions} txn(s), {rows} log rows, {(cycleProblems.Count == 0 ? "ok" : "FAILED")}");

            if (cycleProblems.Count > 0)
            {
                failures++;
                problems.AddRange(cycleProblems.Select(p => $"cycle {cycle}: {p}"));
                SaveEvidence(db, o.Directory, cycle);
                break;
            }
        }

        return new HarnessResult(cyclesRun, kills, failures, lost, ackedTotal, lastAck, unacked, early, earlyBeforeReady,
            recoveries, rows, pages, sw.Elapsed, problems);
    }

    private static long CountCommitted(ulong seed, long fromExclusive, long toInclusive)
    {
        long n = 0;
        for (long t = fromExclusive + 1; t <= toInclusive; t++)
            if (!Workload.Generate(seed, t).RollsBack) n++;
        return n;
    }

    /// <summary>What one worker process reported before it was killed. Resume is -1 if it never got that far.</summary>
    private sealed record WorkerRun(long LastAck, long Resume, bool SawReady, bool ExitedEarly, string Stderr);

    private static WorkerRun RunWorkerAndKill(HarnessOptions o, string db, bool earlyKill, int delayMs)
    {
        var psi = new ProcessStartInfo(o.WorkerFile)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in o.WorkerPrefixArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("worker");
        psi.ArgumentList.Add(db);
        psi.ArgumentList.Add(o.Seed.ToString());

        long lastAck = 0, resume = -1;
        var ready = new ManualResetEventSlim(false);
        var stderr = new System.Text.StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data == "READY") ready.Set();
            else if (e.Data.StartsWith("RESUME ", StringComparison.Ordinal) && long.TryParse(e.Data.AsSpan(7), out long r))
                Interlocked.Exchange(ref resume, r);
            else if (e.Data.StartsWith("ACK ", StringComparison.Ordinal) && long.TryParse(e.Data.AsSpan(4), out long n))
                Interlocked.Exchange(ref lastAck, n);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!earlyKill && !ready.Wait(TimeSpan.FromSeconds(30)))
        {
            Kill(p);
            return new WorkerRun(lastAck, resume, false, true, "worker did not report READY within 30 s. " + stderr);
        }
        bool exited = p.WaitForExit(delayMs);
        if (!exited) Kill(p);
        p.WaitForExit(); // also drains the redirected output, so every ACK written before the kill is seen
        return new WorkerRun(Interlocked.Read(ref lastAck), Interlocked.Read(ref resume), ready.IsSet, exited, stderr.ToString());
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
    }

    private static List<string> Verify(HarnessOptions o, string path, Model model, long lastAck,
        out long found, out Storage.RecoveryInfo recovery, out long rows, out long pages)
    {
        var problems = new List<string>();
        found = 0;
        rows = 0;
        pages = 0;
        using var db = BasaltDatabase.Open(path, Worker.Options);
        recovery = db.Recovery;

        if (!db.Tables.Any(t => t.Name == "meta"))
        {
            if (lastAck > 0) problems.Add($"schema is missing but {lastAck} commits were acknowledged");
            if (db.Tables.Any()) problems.Add("half-created schema is visible");
            return problems;
        }

        found = db.Execute("SELECT last_txn FROM meta WHERE k = 1").Scalar().AsInt;
        long nextCommit = lastAck + 1;
        while (Workload.Generate(o.Seed, nextCommit).RollsBack) nextCommit++;
        if (found < lastAck)
        {
            problems.Add($"LOST COMMITS: transaction {lastAck} was acknowledged but the database only has up to {found}");
            return problems;
        }
        if (found > lastAck && found != nextCommit)
        {
            problems.Add($"database is at transaction {found}, but only {lastAck} was acknowledged and the next commit is {nextCommit}");
            return problems;
        }

        model.AdvanceTo(o.Seed, found);

        // Accounts: exact match, and money is conserved.
        var accounts = db.Execute("SELECT id, owner, balance FROM accounts ORDER BY id").Rows;
        if (accounts.Count != Workload.Accounts) problems.Add($"expected {Workload.Accounts} accounts, found {accounts.Count}");
        long total = 0;
        foreach (var a in accounts)
        {
            int id = (int)a[0].AsInt;
            total += a[2].AsInt;
            if (id < 1 || id > Workload.Accounts) { problems.Add($"unexpected account {id}"); continue; }
            if (a[1].AsText != model.Owners[id]) problems.Add($"account {id} owner is {a[1]}, expected {model.Owners[id]}");
            if (a[2].AsInt != model.Balances[id]) problems.Add($"account {id} balance is {a[2]}, expected {model.Balances[id]}");
        }
        if (total != (long)Workload.Accounts * Workload.InitialBalance) problems.Add($"total balance is {total}, money was created or destroyed");

        // Log rows: exact match with the model.
        var log = db.Execute("SELECT id, txn, note FROM log ORDER BY id").Rows;
        rows = log.Count;
        if (log.Count != model.Log.Count) problems.Add($"log has {log.Count} rows, expected {model.Log.Count}");
        using (var expected = model.Log.GetEnumerator())
        {
            foreach (var r in log)
            {
                if (!expected.MoveNext()) break;
                var (eid, (etxn, enote)) = (expected.Current.Key, expected.Current.Value);
                if (r[0].AsInt != eid || r[1].AsInt != etxn || r[2].AsText != enote)
                {
                    problems.Add($"log row mismatch: got id {r[0]} txn {r[1]}, expected id {eid} txn {etxn}");
                    break;
                }
            }
        }

        // Spot-check that the secondary indexes answer the same as the model.
        var probe = new Random((int)found);
        for (int i = 0; i < 5 && found > 0; i++)
        {
            long t = probe.NextInt64(1, found + 1);
            long got = db.Execute("SELECT COUNT(*) FROM log WHERE txn = ?", t).Scalar().AsInt;
            if (got != model.CountForTxn(t)) problems.Add($"index lookup for txn {t} returned {got} rows, expected {model.CountForTxn(t)}");
        }
        int acct = probe.Next(1, Workload.Accounts + 1);
        var byOwner = db.Execute("SELECT id FROM accounts WHERE owner = ?", model.Owners[acct]).Rows.Select(r => r[0].AsInt).ToList();
        if (!byOwner.Contains(acct)) problems.Add($"owner index does not find account {acct}");

        var report = db.CheckIntegrity();
        pages = report.Pages;
        if (!report.Ok) problems.AddRange(report.Problems.Take(10).Select(p => "integrity: " + p));
        return problems;
    }

    private static void SaveEvidence(string db, string dir, int cycle)
    {
        string target = Path.Combine(dir, $"failed-cycle-{cycle}");
        System.IO.Directory.CreateDirectory(target);
        foreach (var f in new[] { db, db + "-wal" })
            if (File.Exists(f)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
    }
}
