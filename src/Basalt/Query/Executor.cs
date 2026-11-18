using Basalt.Catalog;
using Basalt.Index;
using Basalt.Sql;
using Basalt.Storage;
using Basalt.Values;

namespace Basalt.Query;

/// <summary>
/// Runs one parsed statement against the storage engine. Transactions and locking are handled by
/// the caller (<see cref="BasaltSession"/>); this class only reads and writes.
/// </summary>
internal sealed class Executor(Pager pager, SchemaCatalog catalog, IReadOnlyList<Value> parameters)
{
    private readonly Planner _planner = new(pager, catalog, parameters);

    public static bool IsReadOnly(Statement s) => s is SelectStmt or ExplainStmt;

    public QueryResult Execute(Statement statement) => statement switch
    {
        SelectStmt s => Select(s),
        ExplainStmt e => Explain(e.Inner),
        InsertStmt i => Insert(i),
        UpdateStmt u => Update(u),
        DeleteStmt d => Delete(d),
        CreateTableStmt c => CreateTable(c),
        CreateIndexStmt c => CreateIndex(c),
        _ => throw new BasaltException($"cannot execute {statement.GetType().Name} here"),
    };

    // ---- reads ---------------------------------------------------------------------------

    private QueryResult Select(SelectStmt s)
    {
        var plan = _planner.PlanSelect(s);
        var rows = plan.Root.Execute().ToList();
        return QueryResult.ForRows(plan.ColumnNames, rows);
    }

    private QueryResult Explain(Statement inner)
    {
        var lines = new List<string>();
        switch (inner)
        {
            case SelectStmt s:
                _planner.PlanSelect(s).Root.Explain(lines);
                break;
            case UpdateStmt u:
            {
                var table = catalog.GetTable(u.Table);
                lines.Add($"Update {table.Name} set {string.Join(", ", u.Assignments.Select(a => a.Column))}");
                TargetPlan(table, u.Where).Explain(lines, 1);
                break;
            }
            case DeleteStmt d:
            {
                var table = catalog.GetTable(d.Table);
                lines.Add($"Delete from {table.Name}");
                TargetPlan(table, d.Where).Explain(lines, 1);
                break;
            }
        }
        return QueryResult.ForRows(["plan"], lines.Select(l => new[] { Value.FromText(l) }).ToList());
    }

    /// <summary>Access path + filter that finds the rows an UPDATE or DELETE touches.</summary>
    private PlanNode TargetPlan(TableInfo table, Expr? where)
    {
        var access = _planner.PlanAccess(table, table.Name, Binder.SplitAnd(where));
        return where == null ? access : new FilterNode(access, Binder.Bind(where, access.Schema, parameters), Binder.Substitute(where, parameters).ToString());
    }

    private List<(RowId Id, Value[] Row)> FindTargets(TableInfo table, Expr? where)
    {
        if (where != null && Binder.ContainsAggregate(where)) throw new BasaltException("aggregates are not allowed in WHERE");
        var access = _planner.PlanAccess(table, table.Name, Binder.SplitAnd(where));
        var predicate = where == null ? null : Binder.Bind(where, access.Schema, parameters);
        // Materialise first, then modify: a row moved by an UPDATE must not be visited twice.
        return access.ExecuteWithIds().Where(r => predicate == null || FilterNode.Passes(predicate, r.Row)).ToList();
    }

    // ---- writes --------------------------------------------------------------------------

    private QueryResult CreateTable(CreateTableStmt c)
    {
        if (catalog.TryGetTable(c.Table) != null)
        {
            if (c.IfNotExists) return QueryResult.ForMessage($"table {c.Table} already exists", 0);
            throw new BasaltException($"table {c.Table} already exists");
        }
        catalog.CreateTable(c.Table, c.Columns.Select(d => new ColumnInfo(d.Name, d.Type, d.NotNull || d.PrimaryKey, d.PrimaryKey)).ToList());
        return QueryResult.ForMessage($"created table {c.Table}", 0);
    }

    private QueryResult CreateIndex(CreateIndexStmt c)
    {
        if (catalog.FindIndex(c.Index) != null)
        {
            if (c.IfNotExists) return QueryResult.ForMessage($"index {c.Index} already exists", 0);
            throw new BasaltException($"index {c.Index} already exists");
        }
        var table = catalog.GetTable(c.Table);
        int column = table.ColumnIndex(c.Column);
        if (column < 0) throw new BasaltException($"no such column: {c.Table}.{c.Column}");

        var index = catalog.CreateIndex(c.Index, table, column);
        var tree = new BTree(pager, index.RootPage);
        var heap = new TableHeap(pager, table.HeapFirstPage);
        int n = 0;
        foreach (var (id, bytes) in heap.Scan())
        {
            var row = RowSerializer.Deserialize(table.Types, bytes);
            tree.Insert(KeyEncoder.EncodeWithRowId(row[column], table.Types[column], id), []);
            n++;
        }
        return QueryResult.ForMessage($"created index {c.Index} on {table.Name}({table.Columns[column].Name}), {n} entries", 0);
    }

    private QueryResult Insert(InsertStmt s)
    {
        var table = catalog.GetTable(s.Table);
        int[] targets;
        if (s.Columns == null)
        {
            targets = Enumerable.Range(0, table.Columns.Count).ToArray();
        }
        else
        {
            targets = s.Columns.Select(name =>
            {
                int i = table.ColumnIndex(name);
                return i >= 0 ? i : throw new BasaltException($"no such column: {table.Name}.{name}");
            }).ToArray();
            if (targets.Distinct().Count() != targets.Length) throw new BasaltException("a column is listed twice in INSERT");
        }

        int count = 0;
        foreach (var exprs in s.Rows)
        {
            if (exprs.Count != targets.Length)
                throw new BasaltException($"INSERT has {exprs.Count} values for {targets.Length} columns");
            var values = new Value[table.Columns.Count];
            for (int i = 0; i < targets.Length; i++)
                values[targets[i]] = Binder.EvalConstant(exprs[i], parameters);
            for (int i = 0; i < values.Length; i++)
                values[i] = Coerce(values[i], table, i);
            InsertRow(table, values);
            count++;
        }
        return QueryResult.ForMessage($"inserted {count} row{(count == 1 ? "" : "s")}", count);
    }

    private void InsertRow(TableInfo table, Value[] values)
    {
        var pk = table.PrimaryIndex;
        byte[]? pkKey = null;
        if (pk != null)
        {
            pkKey = KeyEncoder.Encode(values[pk.Column], table.Types[pk.Column]);
            if (new BTree(pager, pk.RootPage).Find(pkKey) != null)
                throw new ConstraintException($"duplicate primary key {values[pk.Column].ToSqlLiteral()} in table {table.Name}");
        }

        var rid = new TableHeap(pager, table.HeapFirstPage).Insert(RowSerializer.Serialize(table.Types, values));
        foreach (var index in table.Indexes)
        {
            var tree = new BTree(pager, index.RootPage);
            if (index.IsPrimary) tree.Insert(pkKey!, rid.ToBytes());
            else tree.Insert(KeyEncoder.EncodeWithRowId(values[index.Column], table.Types[index.Column], rid), []);
        }
    }

    private QueryResult Update(UpdateStmt s)
    {
        var table = catalog.GetTable(s.Table);
        var schema = new SeqScanNode(pager, table, table.Name).Schema;
        var sets = s.Assignments.Select(a =>
        {
            int i = table.ColumnIndex(a.Column);
            if (i < 0) throw new BasaltException($"no such column: {table.Name}.{a.Column}");
            if (Binder.ContainsAggregate(a.Value)) throw new BasaltException("aggregates are not allowed in SET");
            return (Column: i, Expr: Binder.Bind(a.Value, schema, parameters));
        }).ToList();
        if (sets.Select(x => x.Column).Distinct().Count() != sets.Count) throw new BasaltException("a column is assigned twice in UPDATE");

        var heap = new TableHeap(pager, table.HeapFirstPage);
        var targets = FindTargets(table, s.Where);
        foreach (var (rid, old) in targets)
        {
            var updated = (Value[])old.Clone();
            foreach (var (col, expr) in sets) updated[col] = Coerce(expr.Eval(old), table, col); // all SETs see the old row

            // Remove old index entries, move/update the row, then add the new entries.
            var oldKeys = table.Indexes.Select(ix => IndexKey(table, ix, old, rid)).ToList();
            var newRid = heap.Update(rid, RowSerializer.Serialize(table.Types, updated));
            for (int k = 0; k < table.Indexes.Count; k++)
            {
                var ix = table.Indexes[k];
                var newKey = IndexKey(table, ix, updated, newRid);
                if (newRid == rid && newKey.AsSpan().SequenceEqual(oldKeys[k])) continue;
                var tree = new BTree(pager, ix.RootPage);
                tree.Delete(oldKeys[k]);
                if (!tree.Insert(newKey, ix.IsPrimary ? newRid.ToBytes() : []))
                    throw new ConstraintException($"duplicate primary key {updated[ix.Column].ToSqlLiteral()} in table {table.Name}");
            }
        }
        return QueryResult.ForMessage($"updated {targets.Count} row{(targets.Count == 1 ? "" : "s")}", targets.Count);
    }

    private QueryResult Delete(DeleteStmt s)
    {
        var table = catalog.GetTable(s.Table);
        var heap = new TableHeap(pager, table.HeapFirstPage);
        var targets = FindTargets(table, s.Where);
        foreach (var (rid, row) in targets)
        {
            foreach (var ix in table.Indexes)
                new BTree(pager, ix.RootPage).Delete(IndexKey(table, ix, row, rid));
            heap.Delete(rid);
        }
        return QueryResult.ForMessage($"deleted {targets.Count} row{(targets.Count == 1 ? "" : "s")}", targets.Count);
    }

    internal static byte[] IndexKey(TableInfo table, IndexInfo ix, Value[] row, RowId rid) =>
        ix.IsPrimary
            ? KeyEncoder.Encode(row[ix.Column], table.Types[ix.Column])
            : KeyEncoder.EncodeWithRowId(row[ix.Column], table.Types[ix.Column], rid);

    /// <summary>Checks NOT NULL and converts a value to the column type (INT to REAL, 0/1 to BOOL).</summary>
    internal static Value Coerce(Value v, TableInfo table, int column)
    {
        var col = table.Columns[column];
        if (v.IsNull)
        {
            if (col.NotNull) throw new ConstraintException($"NOT NULL constraint failed: {table.Name}.{col.Name}");
            return v;
        }
        switch (col.Type)
        {
            case DataType.Int:
                if (v.Kind == ValueKind.Int) return v;
                if (v.Kind == ValueKind.Real && Planner.TryCoerceKey(v, DataType.Int, out var asInt)) return asInt;
                break;
            case DataType.Real:
                if (v.IsNumeric) return Value.FromReal(v.AsReal);
                break;
            case DataType.Text:
                if (v.Kind == ValueKind.Text) return v;
                break;
            case DataType.Bool:
                if (v.Kind == ValueKind.Bool) return v;
                if (v.Kind == ValueKind.Int && v.AsInt is 0 or 1) return Value.FromBool(v.AsInt == 1);
                break;
        }
        throw new ConstraintException($"column {table.Name}.{col.Name} is {col.Type.ToString().ToUpperInvariant()}, cannot store {Value.KindName(v.Kind)} value {v.ToSqlLiteral()}");
    }
}
