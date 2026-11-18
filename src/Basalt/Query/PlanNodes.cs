using Basalt.Catalog;
using Basalt.Index;
using Basalt.Storage;
using Basalt.Values;

namespace Basalt.Query;

/// <summary>
/// A node of a query plan. Execution is "pull" style (the Volcano model): each node is an
/// iterator that asks its child for rows one at a time, written here as C# iterator methods.
/// </summary>
internal abstract class PlanNode
{
    public abstract RowSchema Schema { get; }
    public abstract IEnumerable<Value[]> Execute();
    /// <summary>One line for EXPLAIN.</summary>
    public abstract string Describe();
    public virtual IReadOnlyList<PlanNode> Children => [];

    public void Explain(List<string> lines, int depth = 0)
    {
        lines.Add(new string(' ', depth * 2) + (depth > 0 ? "-> " : "") + Describe());
        foreach (var c in Children) c.Explain(lines, depth + 1);
    }
}

/// <summary>A node that reads one table and can report each row's physical address (for UPDATE/DELETE).</summary>
internal abstract class AccessNode : PlanNode
{
    protected AccessNode(Pager pager, TableInfo table, string alias)
    {
        Pager = pager;
        Table = table;
        Alias = alias;
        Heap = new TableHeap(pager, table.HeapFirstPage);
        Schema = new RowSchema(table.Columns.Select(c => new ColumnBinding(alias, c.Name, c.Type)).ToList());
    }

    protected Pager Pager { get; }
    protected TableHeap Heap { get; }
    public TableInfo Table { get; }
    public string Alias { get; }
    public override RowSchema Schema { get; }

    public abstract IEnumerable<(RowId Id, Value[] Row)> ExecuteWithIds();

    public override IEnumerable<Value[]> Execute()
    {
        foreach (var (_, row) in ExecuteWithIds()) yield return row;
    }

    protected Value[]? Fetch(RowId id)
    {
        var bytes = Heap.TryGet(id);
        if (bytes == null) throw new CorruptionException($"index of {Table.Name} points at missing row {id}");
        return RowSerializer.Deserialize(Table.Types, bytes);
    }

    protected string TableLabel => Alias.Equals(Table.Name, StringComparison.OrdinalIgnoreCase) ? Table.Name : $"{Table.Name} AS {Alias}";
}

internal sealed class SeqScanNode(Pager pager, TableInfo table, string alias) : AccessNode(pager, table, alias)
{
    public override IEnumerable<(RowId, Value[])> ExecuteWithIds()
    {
        foreach (var (id, bytes) in Heap.Scan())
            yield return (id, RowSerializer.Deserialize(Table.Types, bytes));
    }

    public override string Describe() => $"Full table scan on {TableLabel}";
}

/// <summary>Point lookup: column = constant, using the primary key or a secondary index.</summary>
internal sealed class IndexLookupNode(Pager pager, TableInfo table, string alias, IndexInfo index, Value key)
    : AccessNode(pager, table, alias)
{
    public IndexInfo Index { get; } = index;

    public override IEnumerable<(RowId, Value[])> ExecuteWithIds()
    {
        var tree = new BTree(Pager, Index.RootPage);
        var type = Table.Types[Index.Column];
        var encoded = KeyEncoder.Encode(key, type);
        if (Index.IsPrimary)
        {
            var rid = tree.Find(encoded);
            if (rid != null)
            {
                var id = RowId.ReadFrom(rid);
                yield return (id, Fetch(id)!);
            }
            yield break;
        }
        foreach (var (k, _) in tree.Scan(encoded, KeyEncoder.PadHigh(encoded, RowId.EncodedSize)))
        {
            var id = RowId.ReadFrom(k.AsSpan(k.Length - RowId.EncodedSize));
            yield return (id, Fetch(id)!);
        }
    }

    public override string Describe() =>
        $"Index lookup on {TableLabel} using {Index.Name} ({Table.Columns[Index.Column].Name} = {key.ToSqlLiteral()})";
}

/// <summary>Range scan over an index between optional bounds. Exact bound handling is left to the
/// filter above it; the scan only has to return a superset, in index order.</summary>
internal sealed class IndexRangeNode(Pager pager, TableInfo table, string alias, IndexInfo index,
    Value? low, bool lowInclusive, Value? high, bool highInclusive) : AccessNode(pager, table, alias)
{
    public IndexInfo Index { get; } = index;

    public override IEnumerable<(RowId, Value[])> ExecuteWithIds()
    {
        var tree = new BTree(Pager, Index.RootPage);
        var type = Table.Types[Index.Column];
        // Start at the low value, or just past NULLs (encoded as 00) when there is no low bound.
        byte[] from = low is { } lo ? KeyEncoder.Encode(lo, type) : [0x01];
        byte[]? to = high is { } hi ? KeyEncoder.PadHigh(KeyEncoder.Encode(hi, type), Index.SuffixLength) : null;
        foreach (var (k, v) in tree.Scan(from, to))
        {
            var id = Index.IsPrimary ? RowId.ReadFrom(v) : RowId.ReadFrom(k.AsSpan(k.Length - RowId.EncodedSize));
            yield return (id, Fetch(id)!);
        }
    }

    public override string Describe()
    {
        string col = Table.Columns[Index.Column].Name;
        var parts = new List<string>();
        if (low is { } lo) parts.Add($"{col} {(lowInclusive ? ">=" : ">")} {lo.ToSqlLiteral()}");
        if (high is { } hi) parts.Add($"{col} {(highInclusive ? "<=" : "<")} {hi.ToSqlLiteral()}");
        return $"Index range scan on {TableLabel} using {Index.Name} ({string.Join(" AND ", parts)})";
    }
}

internal sealed class FilterNode(PlanNode child, BoundExpr predicate, string text) : PlanNode
{
    public override RowSchema Schema => child.Schema;
    public override IReadOnlyList<PlanNode> Children => [child];
    public BoundExpr Predicate => predicate;

    public override IEnumerable<Value[]> Execute()
    {
        foreach (var row in child.Execute())
            if (Passes(predicate, row)) yield return row;
    }

    public static bool Passes(BoundExpr predicate, Value[] row)
    {
        var v = predicate.Eval(row);
        if (v.IsNull) return false;
        if (v.Kind != ValueKind.Bool) throw new BasaltException($"WHERE condition must be BOOL, got {Value.KindName(v.Kind)}");
        return v.AsBool;
    }

    public override string Describe() => $"Filter {text}";
}

/// <summary>For each outer row, look the join key up in the inner table's index.</summary>
internal sealed class IndexJoinNode(PlanNode outer, Pager pager, TableInfo inner, string innerAlias, IndexInfo index,
    BoundExpr outerKey, string conditionText) : PlanNode
{
    private readonly RowSchema _schema = RowSchema.Concat(outer.Schema,
        new RowSchema(inner.Columns.Select(c => new ColumnBinding(innerAlias, c.Name, c.Type)).ToList()));

    public override RowSchema Schema => _schema;
    public override IReadOnlyList<PlanNode> Children => [outer];

    public override IEnumerable<Value[]> Execute()
    {
        var type = inner.Types[index.Column];
        foreach (var row in outer.Execute())
        {
            var key = outerKey.Eval(row);
            if (key.IsNull) continue;
            if (!Planner.TryCoerceKey(key, type, out var k)) continue;
            var lookup = new IndexLookupNode(pager, inner, innerAlias, index, k);
            foreach (var match in lookup.Execute())
            {
                var combined = new Value[row.Length + match.Length];
                row.CopyTo(combined, 0);
                match.CopyTo(combined, row.Length);
                yield return combined;
            }
        }
    }

    public override string Describe() =>
        $"Index nested-loop join with {inner.Name}{(innerAlias.Equals(inner.Name, StringComparison.OrdinalIgnoreCase) ? "" : " AS " + innerAlias)} using {index.Name} on {conditionText}";
}

/// <summary>Builds a hash table on the inner input's join key, then streams the outer input through it.</summary>
internal sealed class HashJoinNode(PlanNode outer, PlanNode inner, BoundExpr outerKey, BoundExpr innerKey, string conditionText) : PlanNode
{
    private readonly RowSchema _schema = RowSchema.Concat(outer.Schema, inner.Schema);

    public override RowSchema Schema => _schema;
    public override IReadOnlyList<PlanNode> Children => [outer, inner];

    public override IEnumerable<Value[]> Execute()
    {
        var table = new Dictionary<Value, List<Value[]>>();
        foreach (var row in inner.Execute())
        {
            var k = innerKey.Eval(row);
            if (k.IsNull) continue;
            if (!table.TryGetValue(k, out var list)) table[k] = list = new List<Value[]>();
            list.Add(row);
        }
        foreach (var row in outer.Execute())
        {
            var k = outerKey.Eval(row);
            if (k.IsNull || !table.TryGetValue(k, out var matches)) continue;
            // Value equality treats 1 and 1.0 as equal (as SQL = does) and never matches TEXT with INT.
            foreach (var m in matches)
            {
                var combined = new Value[row.Length + m.Length];
                row.CopyTo(combined, 0);
                m.CopyTo(combined, row.Length);
                yield return combined;
            }
        }
    }

    public override string Describe() => $"Hash join on {conditionText}";
}

/// <summary>Fallback join for ON conditions without a usable equality: every pair is tested.</summary>
internal sealed class NestedLoopJoinNode(PlanNode outer, PlanNode inner, BoundExpr? condition, string conditionText) : PlanNode
{
    private readonly RowSchema _schema = RowSchema.Concat(outer.Schema, inner.Schema);

    public override RowSchema Schema => _schema;
    public override IReadOnlyList<PlanNode> Children => [outer, inner];

    public override IEnumerable<Value[]> Execute()
    {
        var innerRows = inner.Execute().ToList();
        foreach (var row in outer.Execute())
        {
            foreach (var m in innerRows)
            {
                var combined = new Value[row.Length + m.Length];
                row.CopyTo(combined, 0);
                m.CopyTo(combined, row.Length);
                if (condition == null || FilterNode.Passes(condition, combined)) yield return combined;
            }
        }
    }

    public override string Describe() => $"Nested-loop join on {conditionText}";
}

internal sealed record AggregateSpec(string Function, BoundExpr? Argument, string Text);

/// <summary>GROUP BY with hash grouping. Output row = group key values followed by aggregate results.</summary>
internal sealed class AggregateNode(PlanNode child, IReadOnlyList<BoundExpr> groupKeys, IReadOnlyList<string> groupText,
    IReadOnlyList<AggregateSpec> aggregates, RowSchema schema) : PlanNode
{
    public override RowSchema Schema => schema;
    public override IReadOnlyList<PlanNode> Children => [child];

    public override IEnumerable<Value[]> Execute()
    {
        var groups = new Dictionary<GroupKey, Accumulator[]>();
        var order = new List<(GroupKey, Accumulator[])>();
        foreach (var row in child.Execute())
        {
            var keyValues = new Value[groupKeys.Count];
            for (int i = 0; i < keyValues.Length; i++) keyValues[i] = groupKeys[i].Eval(row);
            var key = new GroupKey(keyValues);
            if (!groups.TryGetValue(key, out var accs))
            {
                accs = aggregates.Select(a => new Accumulator(a.Function)).ToArray();
                groups.Add(key, accs);
                order.Add((key, accs));
            }
            for (int i = 0; i < accs.Length; i++)
                accs[i].Add(aggregates[i].Argument == null ? Value.True : aggregates[i].Argument!.Eval(row));
        }

        // Without GROUP BY, an aggregate over zero rows still returns one row (COUNT = 0, SUM = NULL).
        if (groupKeys.Count == 0 && order.Count == 0)
            order.Add((new GroupKey([]), aggregates.Select(a => new Accumulator(a.Function)).ToArray()));

        foreach (var (key, accs) in order)
        {
            var output = new Value[key.Values.Length + accs.Length];
            key.Values.CopyTo(output, 0);
            for (int i = 0; i < accs.Length; i++) output[key.Values.Length + i] = accs[i].Result();
            yield return output;
        }
    }

    public override string Describe()
    {
        string aggs = string.Join(", ", aggregates.Select(a => a.Text));
        return groupKeys.Count == 0 ? $"Aggregate {aggs}" : $"Hash aggregate {aggs} group by {string.Join(", ", groupText)}";
    }

    private readonly struct GroupKey(Value[] values) : IEquatable<GroupKey>
    {
        public Value[] Values { get; } = values;

        public bool Equals(GroupKey other)
        {
            if (Values.Length != other.Values.Length) return false;
            for (int i = 0; i < Values.Length; i++)
                if (!Values[i].Equals(other.Values[i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is GroupKey g && Equals(g);

        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (var v in Values) h.Add(v);
            return h.ToHashCode();
        }
    }

    private sealed class Accumulator(string function)
    {
        private long _count;
        private long _intSum;
        private double _realSum;
        private bool _sawReal;
        private Value _best = Value.Null;

        public void Add(Value v)
        {
            if (v.IsNull) return;
            switch (function)
            {
                case "COUNT":
                    _count++;
                    break;
                case "SUM":
                case "AVG":
                    if (!v.IsNumeric) throw new BasaltException($"{function} needs numbers, got {Value.KindName(v.Kind)}");
                    _count++;
                    if (v.Kind == ValueKind.Real) _sawReal = true;
                    _realSum += v.AsReal;
                    if (v.Kind == ValueKind.Int && !_sawReal)
                    {
                        try { _intSum = checked(_intSum + v.AsInt); }
                        catch (OverflowException) { _sawReal = true; }
                    }
                    break;
                case "MIN":
                    if (_best.IsNull || BinaryOpExpr.Compare(v, _best) < 0) _best = v;
                    break;
                case "MAX":
                    if (_best.IsNull || BinaryOpExpr.Compare(v, _best) > 0) _best = v;
                    break;
            }
        }

        public Value Result() => function switch
        {
            "COUNT" => Value.FromInt(_count),
            "SUM" => _count == 0 ? Value.Null : _sawReal ? Value.FromReal(_realSum) : Value.FromInt(_intSum),
            "AVG" => _count == 0 ? Value.Null : Value.FromReal(_realSum / _count),
            _ => _best,
        };
    }
}

internal sealed class SortNode(PlanNode child, IReadOnlyList<(BoundExpr Key, bool Descending)> keys, string text) : PlanNode
{
    public override RowSchema Schema => child.Schema;
    public override IReadOnlyList<PlanNode> Children => [child];

    public override IEnumerable<Value[]> Execute()
    {
        // Evaluate sort keys once per row, then do a stable sort.
        var rows = child.Execute().Select((r, i) => (Row: r, Keys: keys.Select(k => k.Key.Eval(r)).ToArray(), Seq: i)).ToList();
        rows.Sort((a, b) =>
        {
            for (int i = 0; i < keys.Count; i++)
            {
                int c = a.Keys[i].CompareTo(b.Keys[i]);
                if (c != 0) return keys[i].Descending ? -c : c;
            }
            return a.Seq.CompareTo(b.Seq);
        });
        foreach (var r in rows) yield return r.Row;
    }

    public override string Describe() => $"Sort by {text}";
}

internal sealed class LimitNode(PlanNode child, long limit, long offset) : PlanNode
{
    public override RowSchema Schema => child.Schema;
    public override IReadOnlyList<PlanNode> Children => [child];

    public override IEnumerable<Value[]> Execute()
    {
        long skipped = 0, taken = 0;
        if (limit <= 0) yield break;
        foreach (var row in child.Execute())
        {
            if (skipped < offset) { skipped++; continue; }
            yield return row;
            if (++taken >= limit) yield break;
        }
    }

    public override string Describe() => offset > 0 ? $"Limit {limit} offset {offset}" : $"Limit {limit}";
}

internal sealed class ProjectNode(PlanNode child, IReadOnlyList<BoundExpr> exprs, RowSchema schema, string text) : PlanNode
{
    public override RowSchema Schema => schema;
    public override IReadOnlyList<PlanNode> Children => [child];

    public override IEnumerable<Value[]> Execute()
    {
        foreach (var row in child.Execute())
        {
            var output = new Value[exprs.Count];
            for (int i = 0; i < output.Length; i++) output[i] = exprs[i].Eval(row);
            yield return output;
        }
    }

    public override string Describe() => $"Project {text}";
}

/// <summary>SELECT without FROM produces one empty row.</summary>
internal sealed class SingleRowNode : PlanNode
{
    public override RowSchema Schema { get; } = new([]);
    public override IEnumerable<Value[]> Execute() { yield return []; }
    public override string Describe() => "Single row";
}
