using System.Buffers.Binary;
using Basalt.Index;
using Basalt.Storage;
using Basalt.Values;

namespace Basalt.Tests;

public class BTreeTests
{
    private static byte[] Key(long k)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, k ^ long.MinValue);
        return b;
    }

    private static byte[] Val(long v) => BitConverter.GetBytes(v);

    /// <summary>100-byte key whose first 8 bytes carry the order, so nodes hold few cells and the tree gets tall.</summary>
    private static byte[] WideKey(long k)
    {
        var b = new byte[100];
        BinaryPrimitives.WriteInt64BigEndian(b, k ^ long.MinValue);
        return b;
    }

    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }

    [Fact]
    public void Insert_find_and_split_across_many_pages()
    {
        using var dir = new TempDir();
        using var pager = Pager.Open(dir.File("t.db"), cachePages: 64);
        pager.BeginTransaction();
        var tree = BTree.Create(pager);
        for (long i = 0; i < 20_000; i++) Assert.True(tree.Insert(WideKey(i * 7 % 20_000), Val(i)));
        pager.Commit();

        Assert.True(tree.Height() >= 3, "20k entries should need at least 3 levels");
        for (long i = 0; i < 20_000; i += 97)
            Assert.NotNull(tree.Find(WideKey(i)));
        Assert.Null(tree.Find(WideKey(20_001)));

        var (problems, _, entries) = tree.Check();
        Assert.Empty(problems);
        Assert.Equal(20_000, entries);
        Assert.Equal(Enumerable.Range(0, 20_000).Select(i => (long)i), tree.Scan().Select(e => BinaryPrimitives.ReadInt64BigEndian(e.Key) ^ long.MinValue));
        // Range scan over the middle.
        Assert.Equal(1001, tree.Scan(WideKey(5000), WideKey(6000)).Count());
    }

    [Fact]
    public void Duplicate_insert_is_rejected()
    {
        using var dir = new TempDir();
        using var pager = Pager.Open(dir.File("t.db"));
        pager.BeginTransaction();
        var tree = BTree.Create(pager);
        Assert.True(tree.Insert(Key(5), Val(1)));
        Assert.False(tree.Insert(Key(5), Val(2)));
        Assert.Equal(Val(1), tree.Find(Key(5)));
        pager.Commit();
    }

    /// <summary>
    /// Random inserts, deletes and range scans checked against SortedDictionary after every batch,
    /// with variable-length keys (so nodes hold different numbers of cells) and a small cache
    /// (so pages are evicted and re-read constantly).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Randomized_against_sorted_dictionary(int seed)
    {
        using var dir = new TempDir();
        using var pager = Pager.Open(dir.File("t.db"), cachePages: 32);
        var rng = new Random(seed);
        var reference = new SortedDictionary<byte[], byte[]>(ByteComparer.Instance);
        pager.BeginTransaction();
        var tree = BTree.Create(pager);
        pager.Commit();

        for (int batch = 0; batch < 40; batch++)
        {
            pager.BeginTransaction();
            for (int op = 0; op < 400; op++)
            {
                var key = new byte[rng.Next(1, 200)];
                rng.NextBytes(key);
                if (reference.Count > 0 && rng.Next(3) == 0)
                {
                    // Delete an existing key (picked by index) or a random missing one.
                    var victim = rng.Next(2) == 0 ? reference.Keys.ElementAt(rng.Next(reference.Count)) : key;
                    Assert.Equal(reference.Remove(victim), tree.Delete(victim));
                }
                else
                {
                    var value = new byte[rng.Next(0, 16)];
                    rng.NextBytes(value);
                    bool fresh = !reference.ContainsKey(key);
                    Assert.Equal(fresh, tree.Insert(key, value));
                    if (fresh) reference[key] = value;
                }
            }
            pager.Commit();

            // Full contents in order.
            var all = tree.Scan().ToList();
            Assert.Equal(reference.Count, all.Count);
            Assert.True(reference.Keys.Zip(all, (a, b) => a.AsSpan().SequenceEqual(b.Key)).All(x => x));
            Assert.True(reference.Values.Zip(all, (a, b) => a.AsSpan().SequenceEqual(b.Value)).All(x => x));

            // A random range scan.
            var lo = new byte[rng.Next(1, 4)];
            var hi = new byte[rng.Next(1, 4)];
            rng.NextBytes(lo);
            rng.NextBytes(hi);
            if (ByteComparer.Instance.Compare(lo, hi) > 0) (lo, hi) = (hi, lo);
            var expected = reference.Keys.Where(k => ByteComparer.Instance.Compare(k, lo) >= 0 && ByteComparer.Instance.Compare(k, hi) <= 0).ToList();
            var got = tree.Scan(lo, hi).Select(e => e.Key).ToList();
            Assert.Equal(expected.Count, got.Count);
            Assert.True(expected.Zip(got, (a, b) => a.AsSpan().SequenceEqual(b)).All(x => x));

            var (problems, _, entries) = tree.Check();
            Assert.Empty(problems);
            Assert.Equal(reference.Count, entries);
        }
    }

    [Fact]
    public void Survives_reopen()
    {
        using var dir = new TempDir();
        string path = dir.File("t.db");
        int root;
        using (var pager = Pager.Open(path))
        {
            pager.BeginTransaction();
            var tree = BTree.Create(pager);
            root = tree.RootPage;
            for (long i = 0; i < 5000; i++) tree.Insert(Key(i), Val(i * 2));
            pager.Commit();
        }
        using (var pager = Pager.Open(path))
        {
            var tree = new BTree(pager, root);
            Assert.Equal(Val(2468), tree.Find(Key(1234)));
            Assert.Equal(5000, tree.Scan().Count());
        }
    }

    [Fact]
    public void Rejects_oversized_keys()
    {
        using var dir = new TempDir();
        using var pager = Pager.Open(dir.File("t.db"));
        pager.BeginTransaction();
        var tree = BTree.Create(pager);
        Assert.Throws<ConstraintException>(() => tree.Insert(new byte[BTreeNode.MaxKeySize + 1], []));
        Assert.True(tree.Insert(new byte[BTreeNode.MaxKeySize], []));
        pager.Rollback();
    }
}

public class KeyEncoderTests
{
    [Fact]
    public void Byte_order_matches_value_order()
    {
        var rng = new Random(7);
        var ints = Enumerable.Range(0, 500).Select(_ => Value.FromInt(rng.NextInt64(long.MinValue, long.MaxValue)))
            .Concat([Value.FromInt(0), Value.FromInt(-1), Value.FromInt(long.MinValue), Value.FromInt(long.MaxValue)]);
        AssertOrderPreserved(ints, DataType.Int);

        var reals = Enumerable.Range(0, 500).Select(_ => Value.FromReal((rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-5, 12))))
            .Concat([Value.FromReal(0), Value.FromReal(-0.0), Value.FromReal(double.MaxValue), Value.FromReal(double.NegativeInfinity)]);
        AssertOrderPreserved(reals, DataType.Real);

        string[] texts = ["", "a", "a\0", "a\0b", "ab", "b", "Z", "Ã©", "ï¿½", "ðŸ˜€", "î€€", "aa", "a b"];
        AssertOrderPreserved(texts.Select(Value.FromText), DataType.Text);

        AssertOrderPreserved([Value.False, Value.True], DataType.Bool);
    }

    [Fact]
    public void Null_sorts_first()
    {
        var n = KeyEncoder.Encode(Value.Null, DataType.Int);
        var v = KeyEncoder.Encode(Value.FromInt(long.MinValue), DataType.Int);
        Assert.True(n.AsSpan().SequenceCompareTo(v) < 0);
    }

    private static void AssertOrderPreserved(IEnumerable<Value> values, DataType type)
    {
        var list = values.ToList();
        var byValue = list.OrderBy(v => v).ToList();
        var byBytes = list.OrderBy(v => KeyEncoder.Encode(v, type), Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))).ToList();
        for (int i = 0; i < list.Count; i++)
            Assert.True(byValue[i].CompareTo(byBytes[i]) == 0, $"{type}: position {i}: {byValue[i]} vs {byBytes[i]}");
    }
}
