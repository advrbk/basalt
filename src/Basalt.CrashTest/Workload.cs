namespace Basalt.CrashTest;

/// <summary>Small deterministic RNG (SplitMix64) so the worker and the checker generate identical transactions.</summary>
internal struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Range(int minInclusive, int maxExclusive) => minInclusive + (int)(Next() % (ulong)(maxExclusive - minInclusive));
    public bool Chance(double p) => (Next() >> 11) * (1.0 / (1UL << 53)) < p;
}

/// <summary>Everything transaction number <see cref="Txn"/> does. Generated from (seed, txn) alone.</summary>
internal sealed class TxnScript
{
    public long Txn { get; init; }
    public bool RollsBack { get; init; }
    public List<(long Id, string Note)> Inserts { get; } = new();
    public List<(int From, int To, int Amount)> Transfers { get; } = new();
    public (int Account, string Owner)? Rename { get; set; }
    public long? DeleteTxn { get; set; }
    public (long OfTxn, string Note)? Rewrite { get; set; }
}

internal static class Workload
{
    public const int Accounts = 20;
    public const int InitialBalance = 1000;

    public static string InitialOwner(int account) => $"owner-{account:D2}";

    public static TxnScript Generate(ulong seed, long txn)
    {
        var rng = new SplitMix64(seed ^ (ulong)txn * 0xD1B54A32D192ED03UL);
        var s = new TxnScript { Txn = txn, RollsBack = rng.Chance(0.10) };

        int inserts = rng.Range(1, 7);
        for (int j = 0; j < inserts; j++)
            s.Inserts.Add((txn * 100 + j, Note(ref rng, rng.Range(0, 400))));

        int transfers = rng.Range(1, 4);
        for (int j = 0; j < transfers; j++)
        {
            int from = rng.Range(1, Accounts + 1), to = rng.Range(1, Accounts + 1);
            s.Transfers.Add((from, to, rng.Range(1, 60)));
        }

        if (rng.Chance(0.10)) s.Rename = (rng.Range(1, Accounts + 1), $"owner-{rng.Range(0, 1000):D3}");
        if (txn > 1 && rng.Chance(0.30)) s.DeleteTxn = rng.Range((int)Math.Max(1, txn - 300), (int)txn);
        // Long rewrites make rows grow, so some of them move to another page (index entries must follow).
        if (txn > 1 && rng.Chance(0.15)) s.Rewrite = (rng.Range((int)Math.Max(1, txn - 50), (int)txn), Note(ref rng, rng.Range(200, 1200)));
        return s;
    }

    private static string Note(ref SplitMix64 rng, int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789 ";
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = alphabet[rng.Range(0, alphabet.Length)];
        return new string(chars);
    }
}

/// <summary>The expected database state, built by applying the same scripts in memory.</summary>
internal sealed class Model
{
    public long[] Balances { get; } = new long[Workload.Accounts + 1];
    public string[] Owners { get; } = new string[Workload.Accounts + 1];
    public SortedDictionary<long, (long Txn, string Note)> Log { get; } = new();
    public long LastTxn { get; private set; }

    public Model()
    {
        for (int a = 1; a <= Workload.Accounts; a++)
        {
            Balances[a] = Workload.InitialBalance;
            Owners[a] = Workload.InitialOwner(a);
        }
    }

    /// <summary>Applies committed transactions up to and including <paramref name="txn"/>.</summary>
    public void AdvanceTo(ulong seed, long txn)
    {
        if (txn < LastTxn) throw new InvalidOperationException("model cannot go backwards");
        for (long t = LastTxn + 1; t <= txn; t++)
        {
            var s = Workload.Generate(seed, t);
            if (!s.RollsBack) Apply(s);
        }
        LastTxn = txn;
    }

    private readonly Dictionary<long, List<long>> _idsByTxn = new();

    private void Apply(TxnScript s)
    {
        foreach (var (id, note) in s.Inserts)
        {
            Log.Add(id, (s.Txn, note));
            if (!_idsByTxn.TryGetValue(s.Txn, out var ids)) _idsByTxn[s.Txn] = ids = new List<long>();
            ids.Add(id);
        }
        foreach (var (from, to, amount) in s.Transfers)
        {
            Balances[from] -= amount;
            Balances[to] += amount;
        }
        if (s.Rename is { } r) Owners[r.Account] = r.Owner;
        if (s.DeleteTxn is { } d && _idsByTxn.Remove(d, out var deleted))
            foreach (var id in deleted) Log.Remove(id);
        if (s.Rewrite is { } w && _idsByTxn.TryGetValue(w.OfTxn, out var rewritten))
            foreach (var id in rewritten) Log[id] = (w.OfTxn, w.Note);
    }

    public int CountForTxn(long txn) => _idsByTxn.TryGetValue(txn, out var ids) ? ids.Count : 0;
}
