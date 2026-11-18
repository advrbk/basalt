using Basalt.Catalog;
using Basalt.Index;
using Basalt.Query;
using Basalt.Storage;
using Basalt.Values;

namespace Basalt;

/// <summary>Result of <see cref="BasaltDatabase.CheckIntegrity"/>.</summary>
public sealed record IntegrityReport(bool Ok, IReadOnlyList<string> Problems, int Pages, long Rows, int Indexes, long IndexEntries)
{
    public override string ToString() => Ok
        ? $"ok: {Pages} pages, {Rows} rows, {Indexes} {(Indexes == 1 ? "index" : "indexes")} with {IndexEntries} entries"
        : $"{Problems.Count} problem(s): " + string.Join("; ", Problems.Take(10));
}

/// <summary>
/// Walks every structure in the file and cross-checks them:
/// <list type="bullet">
/// <item>every page 1..N-1 belongs to exactly one structure (catalog, a heap, an index): nothing leaked or shared;</item>
/// <item>heap pages pass the slotted-page checks and every row decodes against its schema (and NOT NULL holds);</item>
/// <item>every B+tree passes its ordering / depth / leaf-chain checks;</item>
/// <item>every index has exactly one entry per row, and each entry points at a row whose column value matches the key.</item>
/// </list>
/// </summary>
internal static class IntegrityChecker
{
    public static IntegrityReport Check(Pager pager, SchemaCatalog catalog)
    {
        var problems = new List<string>();
        int pageCount;
        try
        {
            var header = pager.Read(0);
            HeaderPage.Validate(header);
            pageCount = HeaderPage.GetPageCount(header);
        }
        catch (BasaltException ex)
        {
            return new IntegrityReport(false, [ex.Message], 0, 0, 0, 0);
        }

        var owner = new string?[pageCount];
        owner[0] = "header";
        void Claim(int page, string who)
        {
            if (page <= 0 || page >= pageCount) { problems.Add($"{who} references page {page}, outside the file (0..{pageCount - 1})"); return; }
            if (owner[page] != null) problems.Add($"page {page} is used by both {owner[page]} and {who}");
            else owner[page] = who;
        }

        long totalRows = 0, totalEntries = 0;
        int indexCount = 0;
        try
        {
            foreach (var p in CatalogPages.ChainPages(pager, pager.CatalogRoot)) Claim(p, "catalog");

            foreach (var table in catalog.Tables)
            {
                var heap = new TableHeap(pager, table.HeapFirstPage);
                var rows = new Dictionary<RowId, Value[]>();
                var chain = heap.ChainPages();
                foreach (var p in chain)
                {
                    Claim(p, $"table {table.Name}");
                    var page = pager.Read(p);
                    var err = SlottedPage.Validate(page);
                    if (err != null) { problems.Add($"table {table.Name} page {p}: {err}"); continue; }

                    // Rows are only decoded from pages whose slot directory is sound.
                    for (int slot = 0; slot < SlottedPage.SlotCount(page); slot++)
                    {
                        if (!SlottedPage.IsLive(page, slot)) continue;
                        var id = new RowId(p, (ushort)slot);
                        try
                        {
                            var row = RowSerializer.Deserialize(table.Types, SlottedPage.GetRow(page, slot));
                            for (int c = 0; c < row.Length; c++)
                                if (row[c].IsNull && table.Columns[c].NotNull)
                                    problems.Add($"table {table.Name} row {id}: NULL in NOT NULL column {table.Columns[c].Name}");
                            rows.Add(id, row);
                        }
                        catch (BasaltException ex)
                        {
                            problems.Add($"table {table.Name} row {id}: {ex.Message}");
                        }
                    }
                }
                int last = SlottedPage.GetLast(pager.Read(table.HeapFirstPage));
                if (last != chain[^1]) problems.Add($"table {table.Name}: last-page pointer is {last}, chain ends at {chain[^1]}");
                totalRows += rows.Count;

                foreach (var ix in table.Indexes)
                {
                    indexCount++;
                    var tree = new BTree(pager, ix.RootPage);
                    var (treeProblems, treePages, entries) = tree.Check();
                    problems.AddRange(treeProblems.Select(p => $"index {ix.Name}: {p}"));
                    foreach (var p in treePages) Claim(p, $"index {ix.Name}");
                    if (treeProblems.Count > 0) continue;
                    totalEntries += entries;

                    var seen = new HashSet<RowId>();
                    foreach (var (key, value) in tree.Scan())
                    {
                        var rid = ix.IsPrimary ? RowId.ReadFrom(value) : RowId.ReadFrom(key.AsSpan(key.Length - RowId.EncodedSize));
                        if (!rows.TryGetValue(rid, out var row)) { problems.Add($"index {ix.Name}: entry points at missing row {rid}"); continue; }
                        if (!seen.Add(rid)) problems.Add($"index {ix.Name}: row {rid} is indexed twice");
                        if (!Executor.IndexKey(table, ix, row, rid).AsSpan().SequenceEqual(key))
                            problems.Add($"index {ix.Name}: key for row {rid} does not match the row's value {row[ix.Column]}");
                    }
                    if (seen.Count != rows.Count)
                        problems.Add($"index {ix.Name} has {seen.Count} entries but table {table.Name} has {rows.Count} rows");
                }
            }
        }
        catch (BasaltException ex)
        {
            problems.Add(ex.Message);
        }

        for (int p = 1; p < pageCount; p++)
            if (owner[p] == null) problems.Add($"page {p} is not used by anything (leaked)");

        return new IntegrityReport(problems.Count == 0, problems, pageCount, totalRows, indexCount, totalEntries);
    }
}
