using System.Buffers.Binary;

namespace Basalt.Storage;

/// <summary>Physical address of a row: heap page + slot.</summary>
public readonly record struct RowId(int PageId, ushort Slot)
{
    public const int EncodedSize = 6;

    /// <summary>Big-endian so encoded row ids sort the same way as (page, slot).</summary>
    public void WriteTo(Span<byte> dest)
    {
        BinaryPrimitives.WriteInt32BigEndian(dest, PageId);
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(4), Slot);
    }

    public static RowId ReadFrom(ReadOnlySpan<byte> src) =>
        new(BinaryPrimitives.ReadInt32BigEndian(src), BinaryPrimitives.ReadUInt16BigEndian(src.Slice(4)));

    public byte[] ToBytes()
    {
        var b = new byte[EncodedSize];
        WriteTo(b);
        return b;
    }

    public override string ToString() => $"({PageId},{Slot})";
}

/// <summary>
/// A table's rows: an unordered chain of slotted pages. The first page also stores the id of the
/// last page, so inserts go straight to the end of the chain.
/// </summary>
internal sealed class TableHeap
{
    private readonly Pager _pager;

    public int FirstPage { get; }

    public TableHeap(Pager pager, int firstPage)
    {
        _pager = pager;
        FirstPage = firstPage;
    }

    public static TableHeap Create(Pager pager)
    {
        int id = pager.Allocate();
        var page = pager.Write(id);
        SlottedPage.Initialize(page);
        SlottedPage.SetLast(page, id);
        return new TableHeap(pager, id);
    }

    public RowId Insert(ReadOnlySpan<byte> row)
    {
        if (row.Length > SlottedPage.MaxRowSize)
            throw new ConstraintException($"row is {row.Length} bytes; the limit is {SlottedPage.MaxRowSize} bytes");

        int last = SlottedPage.GetLast(_pager.Read(FirstPage));
        if (last == 0) last = FirstPage;

        int slot = SlottedPage.Insert(_pager.Write(last), row);
        if (slot >= 0) return new RowId(last, (ushort)slot);

        // Last page is full: start a new one and link it in.
        int fresh = _pager.Allocate();
        var freshPage = _pager.Write(fresh);
        SlottedPage.Initialize(freshPage);
        slot = SlottedPage.Insert(freshPage, row);
        SlottedPage.SetNext(_pager.Write(last), fresh);
        SlottedPage.SetLast(_pager.Write(FirstPage), fresh);
        return new RowId(fresh, (ushort)slot);
    }

    public byte[]? TryGet(RowId id)
    {
        var page = _pager.Read(id.PageId);
        if (page[0] != (byte)PageType.Heap || !SlottedPage.IsLive(page, id.Slot)) return null;
        return SlottedPage.GetRow(page, id.Slot).ToArray();
    }

    public void Delete(RowId id) => SlottedPage.Delete(_pager.Write(id.PageId), id.Slot);

    /// <summary>Updates a row. If it no longer fits in its page it moves, and the new address is returned.</summary>
    public RowId Update(RowId id, ReadOnlySpan<byte> row)
    {
        if (row.Length > SlottedPage.MaxRowSize)
            throw new ConstraintException($"row is {row.Length} bytes; the limit is {SlottedPage.MaxRowSize} bytes");
        if (SlottedPage.Update(_pager.Write(id.PageId), id.Slot, row)) return id;
        SlottedPage.Delete(_pager.Write(id.PageId), id.Slot);
        return Insert(row);
    }

    /// <summary>Every live row in chain order. Rows are copied, so the caller may keep them.</summary>
    public IEnumerable<(RowId Id, byte[] Row)> Scan()
    {
        int pageId = FirstPage;
        while (pageId != 0)
        {
            var page = _pager.Read(pageId);
            int n = SlottedPage.SlotCount(page);
            var batch = new List<(RowId, byte[])>(n);
            for (int i = 0; i < n; i++)
                if (SlottedPage.IsLive(page, i))
                    batch.Add((new RowId(pageId, (ushort)i), SlottedPage.GetRow(page, i).ToArray()));
            int next = SlottedPage.GetNext(page);
            foreach (var item in batch) yield return item;
            pageId = next;
        }
    }

    /// <summary>Page ids of the chain, for the integrity checker.</summary>
    public List<int> ChainPages()
    {
        var pages = new List<int>();
        var seen = new HashSet<int>();
        int id = FirstPage;
        while (id != 0)
        {
            if (!seen.Add(id)) throw new CorruptionException($"heap chain starting at page {FirstPage} has a cycle");
            pages.Add(id);
            id = SlottedPage.GetNext(_pager.Read(id));
        }
        return pages;
    }
}
