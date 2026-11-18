using System.Diagnostics;
using System.Text;

namespace Basalt.Cli;

/// <summary>Interactive shell: statements end with ';', commands start with '.'.</summary>
internal sealed class Repl(BasaltDatabase db, TextReader input, TextWriter output, bool interactive)
{
    private bool _timer = true;

    public int Run()
    {
        if (interactive)
        {
            output.WriteLine($"Basalt {typeof(BasaltDatabase).Assembly.GetName().Version?.ToString(3)} - {db.Path}");
            var r = db.Recovery;
            if (r.CommittedTransactions > 0 || r.HadIncompleteTail)
                output.WriteLine($"recovery: redid {r.CommittedTransactions} committed transaction(s), {r.PagesRedone} page(s); discarded {r.DiscardedBytes} byte(s) of incomplete log");
            output.WriteLine("Type SQL ending with ';', or .help for commands.");
        }

        var buffer = new StringBuilder();
        int errors = 0;
        while (true)
        {
            if (interactive) output.Write(buffer.Length == 0 ? (db.InTransaction ? "basalt*> " : "basalt> ") : "   ...> ");
            var line = input.ReadLine();
            if (line == null) break;

            if (buffer.Length == 0 && line.TrimStart().StartsWith('.'))
            {
                if (!Command(line.Trim())) break;
                continue;
            }

            buffer.AppendLine(line);
            if (!EndsStatement(buffer.ToString())) continue;
            string sql = buffer.ToString();
            buffer.Clear();
            if (!RunSql(sql)) errors++;
        }

        if (buffer.ToString().Trim().Length > 0 && !RunSql(buffer.ToString())) errors++;
        if (db.InTransaction)
        {
            output.WriteLine("note: open transaction rolled back on exit");
            db.Execute("ROLLBACK");
        }
        return errors == 0 ? 0 : 1;
    }

    /// <summary>True when the text ends with a ';' that is not inside a string literal or comment.</summary>
    private static bool EndsStatement(string text)
    {
        bool inString = false, lastWasSemicolon = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\'') inString = false;
                continue;
            }
            if (c == '\'') { inString = true; lastWasSemicolon = false; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == ';') lastWasSemicolon = true;
            else if (!char.IsWhiteSpace(c)) lastWasSemicolon = false;
        }
        return !inString && lastWasSemicolon;
    }

    public bool RunSql(string sql)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var results = db.ExecuteScript(sql);
            sw.Stop();
            foreach (var result in results) Print(result);
            if (_timer && results.Count > 0)
            {
                var last = results[^1];
                string what = last.HasRows ? $"{last.Rows.Count} row{(last.Rows.Count == 1 ? "" : "s")}" : last.Message ?? "ok";
                output.WriteLine(last.HasRows ? $"({what}, {sw.Elapsed.TotalMilliseconds:0.00} ms)" : $"{what} ({sw.Elapsed.TotalMilliseconds:0.00} ms)");
            }
            else if (results.Count > 0 && !results[^1].HasRows)
            {
                output.WriteLine(results[^1].Message);
            }
            return true;
        }
        catch (BasaltException ex)
        {
            output.WriteLine($"error: {ex.Message}");
            return false;
        }
    }

    private void Print(QueryResult result)
    {
        if (!result.HasRows) return;
        if (result.Columns is ["plan"]) output.Write(TablePrinter.RenderPlan(result));
        else output.Write(TablePrinter.Render(result));
    }

    private bool Command(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case ".quit":
            case ".exit":
                return false;
            case ".help":
                output.WriteLine("""
                    SQL:  CREATE TABLE / CREATE INDEX / INSERT / SELECT / UPDATE / DELETE / EXPLAIN
                          BEGIN / COMMIT / ROLLBACK      (statements end with ';')
                    .tables            list tables
                    .schema [table]    show columns and indexes
                    .check             run the integrity checker
                    .checkpoint        copy the WAL into the data file and empty it
                    .stats             buffer pool and I/O counters
                    .timer on|off      show statement timing
                    .quit              exit
                    """);
                return true;
            case ".tables":
                foreach (var t in db.Tables) output.WriteLine(t.Name);
                return true;
            case ".schema":
                foreach (var t in db.Tables)
                {
                    if (parts.Length > 1 && !t.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) continue;
                    var cols = t.Columns.Select(c =>
                        $"{c.Name} {c.Type.ToString().ToUpperInvariant()}{(c.IsPrimaryKey ? " PRIMARY KEY" : c.NotNull ? " NOT NULL" : "")}");
                    output.WriteLine($"CREATE TABLE {t.Name} ({string.Join(", ", cols)});");
                    foreach (var ix in t.Indexes.Where(i => !i.IsPrimary))
                        output.WriteLine($"CREATE INDEX {ix.Name} ON {t.Name} ({t.Columns[ix.Column].Name});");
                }
                return true;
            case ".check":
                output.WriteLine(db.CheckIntegrity().ToString());
                return true;
            case ".checkpoint":
                try
                {
                    db.Checkpoint();
                    output.WriteLine("checkpoint done");
                }
                catch (Exception ex) when (ex is BasaltException or InvalidOperationException)
                {
                    output.WriteLine($"error: {ex.Message}");
                }
                return true;
            case ".stats":
                var s = db.GetStats();
                output.WriteLine($"pages in file      {s.PageCount}");
                output.WriteLine($"pages cached       {s.CachedPages}");
                output.WriteLine($"cache hits/misses  {s.CacheHits}/{s.CacheMisses}  evictions {s.Evictions}");
                output.WriteLine($"data file reads    {s.DataFileReads}  writes {s.DataFileWrites}");
                output.WriteLine($"WAL size           {s.WalBytes} bytes, {s.WalBytesWritten} written, {s.WalSyncs} fsyncs");
                output.WriteLine($"commits            {s.Commits}  checkpoints {s.Checkpoints}");
                return true;
            case ".timer":
                _timer = parts.Length < 2 || parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                return true;
            default:
                output.WriteLine($"unknown command {parts[0]}; try .help");
                return true;
        }
    }
}
