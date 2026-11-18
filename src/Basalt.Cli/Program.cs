using Basalt;
using Basalt.Cli;

// Usage:
//   basalt [file.db]              interactive shell (default file: basalt.db)
//   basalt file.db -c "SQL;"      run SQL and exit
//   basalt file.db < script.sql   run a script from stdin
//   basalt demo [dir]             scripted tour (default dir: ./demo-data)

if (args.Length > 0 && args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: basalt [file.db] [-c \"SQL\"]  |  basalt demo [dir]");
    return 0;
}

if (args.Length > 0 && args[0].Equals("demo", StringComparison.OrdinalIgnoreCase))
    return Demo.Run(args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "demo-data"));

string path = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "basalt.db";
int c = Array.IndexOf(args, "-c");

try
{
    using var db = BasaltDatabase.Open(path);
    if (c >= 0 && c + 1 < args.Length)
        return new Repl(db, TextReader.Null, Console.Out, interactive: false).RunSql(args[c + 1]) ? 0 : 1;

    bool interactive = !Console.IsInputRedirected;
    return new Repl(db, Console.In, Console.Out, interactive).Run();
}
catch (BasaltException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (IOException ex)
{
    Console.Error.WriteLine($"cannot open {path}: {ex.Message}");
    return 2;
}
