using System.Diagnostics;

namespace Basalt.Cli;

/// <summary>A scripted tour: schema, bulk load, queries, EXPLAIN before/after an index, rollback, crash + recovery.</summary>
internal static class Demo
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "demo.db");
        foreach (var f in new[] { path, path + "-wal" }) if (File.Exists(f)) File.Delete(f);

        var output = Console.Out;
        Step($"1. Create a database at {Path.GetFullPath(path)}");
        var db = BasaltDatabase.Open(path);
        var repl = new Repl(db, TextReader.Null, output, interactive: false);
        Sql(repl, """
            CREATE TABLE customers (id INT PRIMARY KEY, name TEXT NOT NULL, city TEXT);
            CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT NOT NULL, amount REAL, paid BOOL);
            """);

        Step("2. Load 2,000 customers and 50,000 orders in one transaction (prepared INSERT with ? parameters)");
        string[] cities = ["Lisbon", "Oslo", "Austin", "Kyoto", "Lagos", "Lima", "Perth", "Quebec"];
        var rng = new Random(42);
        var sw = Stopwatch.StartNew();
        db.Execute("BEGIN");
        var insCustomer = db.Prepare("INSERT INTO customers VALUES (?, ?, ?)");
        for (int i = 1; i <= 2000; i++) insCustomer.Execute(i, $"customer-{i:D4}", cities[rng.Next(cities.Length)]);
        var insOrder = db.Prepare("INSERT INTO orders VALUES (?, ?, ?, ?)");
        for (int i = 1; i <= 50_000; i++)
            insOrder.Execute(i, rng.Next(1, 2001), Math.Round(rng.NextDouble() * 500, 2), rng.Next(4) != 0);
        db.Execute("COMMIT");
        sw.Stop();
        output.WriteLine($"52,000 rows committed in {sw.Elapsed.TotalMilliseconds:0} ms ({52_000 / sw.Elapsed.TotalSeconds:N0} rows/s)");

        Step("3. Point lookup on the primary key");
        Sql(repl, "SELECT * FROM customers WHERE id = 42;");
        Sql(repl, "EXPLAIN SELECT * FROM customers WHERE id = 42;");

        Step("4. Filtering on a column with no index is a full scan ...");
        Sql(repl, "EXPLAIN SELECT COUNT(*) FROM orders WHERE customer_id = 42;");
        Sql(repl, "SELECT COUNT(*) AS orders_for_42 FROM orders WHERE customer_id = 42;");

        Step("   ... so add an index and the planner switches to it");
        Sql(repl, "CREATE INDEX idx_orders_customer ON orders (customer_id);");
        Sql(repl, "EXPLAIN SELECT COUNT(*) FROM orders WHERE customer_id = 42;");
        Sql(repl, "SELECT COUNT(*) AS orders_for_42 FROM orders WHERE customer_id = 42;");

        Step("5. Range scan on the primary key");
        Sql(repl, "SELECT id, customer_id, amount FROM orders WHERE id >= 1000 AND id < 1005 ORDER BY id;");

        Step("6. Join + GROUP BY + ORDER BY + LIMIT: top cities by paid revenue");
        Sql(repl, """
            SELECT c.city, COUNT(*) AS orders, ROUND(SUM(o.amount), 2) AS revenue, ROUND(AVG(o.amount), 2) AS avg_order
            FROM orders o JOIN customers c ON o.customer_id = c.id
            WHERE o.paid = TRUE
            GROUP BY c.city ORDER BY revenue DESC LIMIT 3;
            """);
        Sql(repl, "EXPLAIN SELECT c.city, SUM(o.amount) FROM orders o JOIN customers c ON o.customer_id = c.id GROUP BY c.city;");

        Step("7. ROLLBACK undoes everything since BEGIN");
        Sql(repl, "BEGIN; DELETE FROM orders WHERE amount < 250; SELECT COUNT(*) AS inside_txn FROM orders;");
        Sql(repl, "ROLLBACK; SELECT COUNT(*) AS after_rollback FROM orders;");

        Step("8. A failing statement inside a transaction is undone on its own; the transaction continues");
        Sql(repl, "BEGIN; INSERT INTO customers VALUES (5001, 'new-a', 'Oslo');");
        Sql(repl, "INSERT INTO customers VALUES (5002, 'new-b', 'Oslo'), (42, 'duplicate', 'Oslo');");
        Sql(repl, "COMMIT; SELECT id, name FROM customers WHERE id > 5000;");

        Step("9. Crash: commit one transaction, leave another uncommitted, then kill the engine without a checkpoint");
        Sql(repl, "UPDATE customers SET city = 'Reykjavik' WHERE id <= 10;");
        Sql(repl, "BEGIN; DELETE FROM customers WHERE id > 1000; SELECT COUNT(*) AS uncommitted_view FROM customers;");
        var before = db.GetStats();
        output.WriteLine($"WAL holds {before.WalBytes:N0} bytes of committed page images not yet in the data file");
        db.SimulateCrash();
        output.WriteLine("engine stopped: buffer pool dropped, no checkpoint, files closed");

        Step("10. Reopen: recovery replays committed work from the WAL and drops the uncommitted transaction");
        db = BasaltDatabase.Open(path);
        repl = new Repl(db, TextReader.Null, output, interactive: false);
        var r = db.Recovery;
        output.WriteLine($"recovery redid {r.CommittedTransactions} committed transaction(s) ({r.PagesRedone} page images)");
        Sql(repl, "SELECT COUNT(*) AS customers FROM customers;");
        Sql(repl, "SELECT COUNT(*) AS in_reykjavik FROM customers WHERE city = 'Reykjavik';");
        var report = db.CheckIntegrity();
        output.WriteLine($"integrity check: {report}");
        db.Dispose();

        Step("Done. Try it yourself:  dotnet run --project src/Basalt.Cli -- " + Path.GetFullPath(path));
        output.WriteLine("Headline test: dotnet run -c Release --project src/Basalt.CrashTest -- --cycles 500");
        return report.Ok ? 0 : 1;
    }

    private static void Step(string text)
    {
        Console.WriteLine();
        Console.WriteLine(text);
        Console.WriteLine(new string('=', Math.Min(text.Length, 100)));
    }

    private static void Sql(Repl repl, string sql)
    {
        foreach (var line in sql.Trim().Split('\n')) Console.WriteLine("sql> " + line.TrimEnd());
        repl.RunSql(sql);
    }
}
