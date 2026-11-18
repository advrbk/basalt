using System.Globalization;
using Basalt.CrashTest;

// Usage:
//   Basalt.CrashTest [--cycles N] [--seed S] [--dir PATH] [--min-ms A] [--max-ms B] [--verbose]
//   Basalt.CrashTest worker <db> <seed>      (started by the harness; not for direct use)

if (args.Length >= 3 && args[0] == "worker")
    return Worker.Run(args[1], ulong.Parse(args[2], CultureInfo.InvariantCulture));

var o = new HarnessOptions();
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--cycles": o.Cycles = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--seed": o.Seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--dir": o.Directory = Path.GetFullPath(Next()); break;
        case "--min-ms": o.MinKillMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--max-ms": o.MaxKillMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--verbose": o.Verbose = true; break;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]}");
            return 2;
    }
}

// Start the worker the same way this process was started (apphost exe, or "dotnet Basalt.CrashTest.dll").
string self = Environment.ProcessPath!;
if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
{
    o.WorkerFile = self;
    o.WorkerPrefixArgs.Add(typeof(Harness).Assembly.Location);
}
else
{
    o.WorkerFile = self;
}

Console.WriteLine($"Basalt crash test: {o.Cycles} kill cycles, seed {o.Seed}, kill {o.MinKillMs}-{o.MaxKillMs} ms after READY, database in {o.Directory}");
var result = Harness.Run(o);

Console.WriteLine();
Console.WriteLine("Summary");
Console.WriteLine("-------");
Console.WriteLine($"kill cycles run                    {result.CyclesRun}");
Console.WriteLine($"worker processes killed            {result.Kills}");
Console.WriteLine($"acknowledged commits               {result.AckedCommits}");
Console.WriteLine($"last committed transaction         {result.LastTxn}");
Console.WriteLine($"commits durable but killed pre-ACK {result.CommittedButUnacked}");
Console.WriteLine($"blind kills right after a restart  {result.EarlyKills} ({result.EarlyKillsBeforeReady} before the worker had opened the database)");
Console.WriteLine($"reopens where recovery redid work  {result.RecoveriesWithWork}");
Console.WriteLine($"log rows / pages at the end        {result.RowsAtEnd} / {result.PagesAtEnd}");
Console.WriteLine($"elapsed                            {result.Elapsed.TotalSeconds:0.0} s");
Console.WriteLine($"lost acknowledged commits          {result.LostCommits}");
if (result.Failures > 0)
{
    Console.WriteLine("FAILED:");
    foreach (var p in result.Problems) Console.WriteLine("  " + p);
    return 1;
}
Console.WriteLine("PASSED: every acknowledged commit survived, nothing uncommitted appeared, integrity checks clean.");
return 0;
