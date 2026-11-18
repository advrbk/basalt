using System.Diagnostics;
using System.Globalization;
using Basalt;
using Microsoft.Data.Sqlite;

// Stopwatch benchmarks for Basalt, with the same workloads on SQLite for context.
//   dotnet run -c Release --project bench/Basalt.Bench -- [--rows 100000] [--lookups 20000] [--no-sqlite]

int rows = 100_000, lookups = 20_000, scanLookups = 50, ranges = 2_000, autocommits = 300;
bool sqlite = true;
string dir = Path.Combine(Environment.CurrentDirectory, "bench-data");
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rows": rows = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--lookups": lookups = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--no-sqlite": sqlite = false; break;
        case "--dir": dir = Path.GetFullPath(args[++i]); break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}
Directory.CreateDirectory(dir);

var workloads = new List<string>();
var basalt = new Dictionary<string, string>();
var lite = new Dictionary<string, string>();

Console.WriteLine($"rows={rows:N0} lookups={lookups:N0} unindexed lookups={scanLookups} range scans={ranges:N0} autocommit inserts={autocommits}");
Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical CPUs, {RuntimeInfo()}");

// Warm up the JIT on a small run, then measure.
RunBasalt(Path.Combine(dir, "warmup.db"), 20_000, 10_000, 5, 1000, 5, null);
RunBasalt(Path.Combine(dir, "basalt.db"), rows, lookups, scanLookups, ranges, autocommits, basalt);
if (sqlite)
{
    RunSqlite(Path.Combine(dir, "warmup.sqlite"), 20_000, 10_000, 5, 1000, 5, null);
    RunSqlite(Path.Combine(dir, "sqlite.sqlite"), rows, lookups, scanLookups, ranges, autocommits, lite);
}

Console.WriteLine();
Console.WriteLine(sqlite ? "| Workload | Basalt | SQLite |" : "| Workload | Basalt |");
Console.WriteLine(sqlite ? "|---|---:|---:|" : "|---|---:|");
foreach (var w in workloads)
    Console.WriteLine(sqlite ? $"| {w} | {basalt[w]} | {lite.GetValueOrDefault(w, "-")} |" : $"| {w} | {basalt[w]} |");
return 0;

void Record(Dictionary<string, string>? into, string name, string value)
{
    if (into == null) return;
    if (!workloads.Contains(name)) workloads.Add(name);
    into[name] = value;
    Console.WriteLine($"  {name,-52} {value}");
}

static string PerSecond(double count, TimeSpan t) => $"{count / t.TotalSeconds:N0}/s";

void RunBasalt(string path, int n, int nLookups, int nScans, int nRanges, int nAuto, Dictionary<string, string>? into)
{
    foreach (var f in new[] { path, path + "-wal" }) if (File.Exists(f)) File.Delete(f);
    if (into != null) Console.WriteLine("Basalt");
    var rng = new Random(1);
    var codes = Shuffle(n, rng);
    using var db = BasaltDatabase.Open(path, new BasaltOptions { CachePages = 8192 });
    db.Execute("CREATE TABLE items (id INT PRIMARY KEY, code INT NOT NULL, category INT NOT NULL, name TEXT NOT NULL, price REAL)");
    db.Execute("CREATE TABLE events (id INT PRIMARY KEY, note TEXT)");

    var sw = Stopwatch.StartNew();
    db.Execute("BEGIN");
    var ins = db.Prepare("INSERT INTO items VALUES (?, ?, ?, ?, ?)");
    for (int i = 0; i < n; i++) ins.Execute(i, codes[i], i % 50, $"item-{i}", (i % 1000) / 10.0);
    db.Execute("COMMIT");
    Record(into, $"Insert {n:N0} rows in one transaction", PerSecond(n, sw.Elapsed));

    var auto = db.Prepare("INSERT INTO events VALUES (?, 'x')");
    sw.Restart();
    for (int i = 0; i < nAuto; i++) auto.Execute(i);
    Record(into, "Autocommit inserts (one fsync each)", PerSecond(nAuto, sw.Elapsed));

    var byId = db.Prepare("SELECT name FROM items WHERE id = ?");
    sw.Restart();
    for (int i = 0; i < nLookups; i++) Check(byId.Execute(rng.Next(n)).Rows.Count == 1);
    Record(into, "Point lookup by primary key", PerSecond(nLookups, sw.Elapsed));

    var byCode = db.Prepare("SELECT name FROM items WHERE code = ?");
    sw.Restart();
    for (int i = 0; i < nScans; i++) Check(byCode.Execute(rng.Next(n)).Rows.Count == 1);
    Record(into, "Point lookup on a column without an index (full scan)", PerSecond(nScans, sw.Elapsed));

    sw.Restart();
    db.Execute("CREATE INDEX idx_code ON items (code)");
    Record(into, $"CREATE INDEX on {n:N0} rows", $"{sw.Elapsed.TotalMilliseconds:N0} ms");

    sw.Restart();
    for (int i = 0; i < nLookups; i++) Check(byCode.Execute(rng.Next(n)).Rows.Count == 1);
    Record(into, "Point lookup on the same column with a secondary index", PerSecond(nLookups, sw.Elapsed));

    var range = db.Prepare("SELECT id, price FROM items WHERE id >= ? AND id < ?");
    sw.Restart();
    long seen = 0;
    for (int i = 0; i < nRanges; i++)
    {
        int start = rng.Next(n - 100);
        seen += range.Execute(start, start + 100).Rows.Count;
    }
    Check(seen == 100L * nRanges);
    Record(into, "Range scan of 100 rows on the primary key", $"{PerSecond(nRanges, sw.Elapsed)} ({PerSecond(seen, sw.Elapsed)} rows)");

    sw.Restart();
    var groups = db.Execute("SELECT category, COUNT(*), AVG(price) FROM items GROUP BY category");
    Check(groups.Rows.Count == 50);
    Record(into, $"Full scan + GROUP BY over {n:N0} rows", $"{sw.Elapsed.TotalMilliseconds:N0} ms");
}

void RunSqlite(string path, int n, int nLookups, int nScans, int nRanges, int nAuto, Dictionary<string, string>? into)
{
    SqliteConnection.ClearAllPools();
    foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    if (into != null) Console.WriteLine("SQLite");
    var rng = new Random(1);
    var codes = Shuffle(n, rng);
    using var c = new SqliteConnection($"Data Source={path};Pooling=False");
    c.Open();
    Exec(c, "PRAGMA journal_mode=WAL");
    Exec(c, "PRAGMA synchronous=FULL"); // fsync on every commit, like Basalt
    Exec(c, "CREATE TABLE items (id INTEGER PRIMARY KEY, code INTEGER NOT NULL, category INTEGER NOT NULL, name TEXT NOT NULL, price REAL)");
    Exec(c, "CREATE TABLE events (id INTEGER PRIMARY KEY, note TEXT)");

    var sw = Stopwatch.StartNew();
    using (var tx = c.BeginTransaction())
    {
        using var ins = Cmd(c, "INSERT INTO items VALUES ($a, $b, $c, $d, $e)", "$a", "$b", "$c", "$d", "$e");
        ins.Transaction = tx;
        for (int i = 0; i < n; i++)
        {
            ins.Parameters[0].Value = i;
            ins.Parameters[1].Value = codes[i];
            ins.Parameters[2].Value = i % 50;
            ins.Parameters[3].Value = $"item-{i}";
            ins.Parameters[4].Value = (i % 1000) / 10.0;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }
    Record(into, $"Insert {n:N0} rows in one transaction", PerSecond(n, sw.Elapsed));

    using (var auto = Cmd(c, "INSERT INTO events VALUES ($a, 'x')", "$a"))
    {
        sw.Restart();
        for (int i = 0; i < nAuto; i++)
        {
            auto.Parameters[0].Value = i;
            auto.ExecuteNonQuery();
        }
        Record(into, "Autocommit inserts (one fsync each)", PerSecond(nAuto, sw.Elapsed));
    }

    using var byId = Cmd(c, "SELECT name FROM items WHERE id = $a", "$a");
    sw.Restart();
    for (int i = 0; i < nLookups; i++) Check(CountRows(byId, rng.Next(n)) == 1);
    Record(into, "Point lookup by primary key", PerSecond(nLookups, sw.Elapsed));

    using var byCode = Cmd(c, "SELECT name FROM items WHERE code = $a", "$a");
    sw.Restart();
    for (int i = 0; i < nScans; i++) Check(CountRows(byCode, rng.Next(n)) == 1);
    Record(into, "Point lookup on a column without an index (full scan)", PerSecond(nScans, sw.Elapsed));

    sw.Restart();
    Exec(c, "CREATE INDEX idx_code ON items (code)");
    Record(into, $"CREATE INDEX on {n:N0} rows", $"{sw.Elapsed.TotalMilliseconds:N0} ms");

    using var byCode2 = Cmd(c, "SELECT name FROM items WHERE code = $a", "$a");
    sw.Restart();
    for (int i = 0; i < nLookups; i++) Check(CountRows(byCode2, rng.Next(n)) == 1);
    Record(into, "Point lookup on the same column with a secondary index", PerSecond(nLookups, sw.Elapsed));

    using var range = Cmd(c, "SELECT id, price FROM items WHERE id >= $a AND id < $b", "$a", "$b");
    sw.Restart();
    long seen = 0;
    for (int i = 0; i < nRanges; i++)
    {
        int start = rng.Next(n - 100);
        range.Parameters[0].Value = start;
        range.Parameters[1].Value = start + 100;
        using var r = range.ExecuteReader();
        while (r.Read()) { _ = r.GetInt64(0); _ = r.GetDouble(1); seen++; }
    }
    Check(seen == 100L * nRanges);
    Record(into, "Range scan of 100 rows on the primary key", $"{PerSecond(nRanges, sw.Elapsed)} ({PerSecond(seen, sw.Elapsed)} rows)");

    sw.Restart();
    using (var g = Cmd(c, "SELECT category, COUNT(*), AVG(price) FROM items GROUP BY category"))
    {
        int groups = 0;
        using var r = g.ExecuteReader();
        while (r.Read()) groups++;
        Check(groups == 50);
    }
    Record(into, $"Full scan + GROUP BY over {n:N0} rows", $"{sw.Elapsed.TotalMilliseconds:N0} ms");
}

static int CountRows(SqliteCommand cmd, int arg)
{
    cmd.Parameters[0].Value = arg;
    using var r = cmd.ExecuteReader();
    int count = 0;
    while (r.Read()) { _ = r.GetString(0); count++; }
    return count;
}

static SqliteCommand Cmd(SqliteConnection c, string sql, params string[] names)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, 0));
    cmd.Prepare();
    return cmd;
}

static void Exec(SqliteConnection c, string sql)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static int[] Shuffle(int n, Random rng)
{
    var a = Enumerable.Range(0, n).ToArray();
    rng.Shuffle(a);
    return a;
}

static void Check(bool ok)
{
    if (!ok) throw new InvalidOperationException("benchmark result check failed");
}

static string RuntimeInfo() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
