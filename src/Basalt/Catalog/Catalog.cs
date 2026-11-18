using System.Text;
using Basalt.Index;
using Basalt.Storage;
using Basalt.Values;

namespace Basalt.Catalog;

public sealed record ColumnInfo(string Name, DataType Type, bool NotNull, bool IsPrimaryKey);

/// <summary>An index over one column. The primary key index maps key -> row id; a secondary
/// index stores (value, row id) as its key so duplicate values are allowed.</summary>
public sealed record IndexInfo(string Name, string Table, int Column, int RootPage, bool IsPrimary)
{
    /// <summary>Bytes appended after the encoded value in each key (the row id for secondary indexes).</summary>
    public int SuffixLength => IsPrimary ? 0 : RowId.EncodedSize;
}

public sealed class TableInfo
{
    public TableInfo(string name, IReadOnlyList<ColumnInfo> columns, int heapFirstPage)
    {
        Name = name;
        Columns = columns;
        HeapFirstPage = heapFirstPage;
        Types = columns.Select(c => c.Type).ToArray();
        PrimaryKeyColumn = columns.ToList().FindIndex(c => c.IsPrimaryKey);
    }

    public string Name { get; }
    public IReadOnlyList<ColumnInfo> Columns { get; }
    public IReadOnlyList<DataType> Types { get; }
    public int HeapFirstPage { get; }
    public int PrimaryKeyColumn { get; }
    public List<IndexInfo> Indexes { get; } = new();

    public IndexInfo? PrimaryIndex => Indexes.FirstOrDefault(i => i.IsPrimary);

    public int ColumnIndex(string name) =>
        Columns.ToList().FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The schema: tables, their columns and indexes, and where each one's pages start.
/// It is serialized into the catalog page chain, so DDL goes through the same WAL and rollback
/// path as data: a CREATE TABLE inside a rolled-back transaction disappears too.
/// </summary>
internal sealed class SchemaCatalog
{
    private const int CatalogVersion = 1;
    private readonly Pager _pager;
    private readonly Dictionary<string, TableInfo> _tables = new(StringComparer.OrdinalIgnoreCase);

    private SchemaCatalog(Pager pager) => _pager = pager;

    public IEnumerable<TableInfo> Tables => _tables.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public static SchemaCatalog Load(Pager pager)
    {
        var catalog = new SchemaCatalog(pager);
        catalog.Reload();
        return catalog;
    }

    /// <summary>Re-reads the catalog from its pages (after a rollback).</summary>
    public void Reload()
    {
        _tables.Clear();
        var blob = CatalogPages.ReadBlob(_pager, _pager.CatalogRoot);
        if (blob.Length == 0) return;
        using var reader = new BinaryReader(new MemoryStream(blob), Encoding.UTF8);
        int version = reader.ReadInt32();
        if (version != CatalogVersion) throw new CorruptionException($"unknown catalog version {version}");
        int tableCount = reader.ReadInt32();
        for (int t = 0; t < tableCount; t++)
        {
            string name = reader.ReadString();
            int heap = reader.ReadInt32();
            int colCount = reader.ReadInt32();
            var cols = new List<ColumnInfo>(colCount);
            for (int c = 0; c < colCount; c++)
            {
                string cname = reader.ReadString();
                var type = (DataType)reader.ReadByte();
                byte flags = reader.ReadByte();
                cols.Add(new ColumnInfo(cname, type, (flags & 1) != 0, (flags & 2) != 0));
            }
            var table = new TableInfo(name, cols, heap);
            int idxCount = reader.ReadInt32();
            for (int i = 0; i < idxCount; i++)
            {
                string iname = reader.ReadString();
                int column = reader.ReadInt32();
                int root = reader.ReadInt32();
                bool primary = reader.ReadBoolean();
                table.Indexes.Add(new IndexInfo(iname, name, column, root, primary));
            }
            _tables.Add(name, table);
        }
    }

    private void Save()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(CatalogVersion);
            w.Write(_tables.Count);
            foreach (var t in Tables)
            {
                w.Write(t.Name);
                w.Write(t.HeapFirstPage);
                w.Write(t.Columns.Count);
                foreach (var c in t.Columns)
                {
                    w.Write(c.Name);
                    w.Write((byte)c.Type);
                    w.Write((byte)((c.NotNull ? 1 : 0) | (c.IsPrimaryKey ? 2 : 0)));
                }
                w.Write(t.Indexes.Count);
                foreach (var i in t.Indexes)
                {
                    w.Write(i.Name);
                    w.Write(i.Column);
                    w.Write(i.RootPage);
                    w.Write(i.IsPrimary);
                }
            }
        }
        CatalogPages.WriteBlob(_pager, _pager.CatalogRoot, ms.ToArray());
    }

    public TableInfo? TryGetTable(string name) => _tables.GetValueOrDefault(name);

    public TableInfo GetTable(string name) =>
        TryGetTable(name) ?? throw new BasaltException($"no such table: {name}");

    public IndexInfo? FindIndex(string name) =>
        _tables.Values.SelectMany(t => t.Indexes).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates the heap (and primary key index) for a new table. Must run in a write transaction.</summary>
    public TableInfo CreateTable(string name, IReadOnlyList<ColumnInfo> columns)
    {
        if (_tables.ContainsKey(name)) throw new BasaltException($"table {name} already exists");
        if (columns.Count == 0) throw new BasaltException("a table needs at least one column");
        if (columns.Count > 1000) throw new BasaltException("too many columns");
        var dup = columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new BasaltException($"column {dup.Key} is defined twice");
        if (columns.Count(c => c.IsPrimaryKey) > 1) throw new BasaltException("only single-column primary keys are supported");

        var heap = TableHeap.Create(_pager);
        var table = new TableInfo(name, columns, heap.FirstPage);
        int pk = table.PrimaryKeyColumn;
        if (pk >= 0)
        {
            var tree = BTree.Create(_pager);
            table.Indexes.Add(new IndexInfo($"pk_{name}", name, pk, tree.RootPage, IsPrimary: true));
        }
        _tables.Add(name, table);
        Save();
        return table;
    }

    /// <summary>Registers a new, empty secondary index; the caller fills it.</summary>
    public IndexInfo CreateIndex(string indexName, TableInfo table, int column)
    {
        if (FindIndex(indexName) != null) throw new BasaltException($"index {indexName} already exists");
        var tree = BTree.Create(_pager);
        var index = new IndexInfo(indexName, table.Name, column, tree.RootPage, IsPrimary: false);
        table.Indexes.Add(index);
        Save();
        return index;
    }
}
