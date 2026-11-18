using System.Buffers.Binary;

namespace Basalt.Storage;

/// <summary>
/// Stores one byte blob (the serialized catalog) across a linked list of pages.
/// Layout: type | 3 reserved | next page (4) | blob length (4, first page only) | bytes used here (4) | data.
/// When the blob shrinks, extra pages stay linked so they are never orphaned.
/// </summary>
internal static class CatalogPages
{
    private const int DataStart = 16;
    private const int Capacity = Pager.PageSize - DataStart;

    public static void Initialize(Span<byte> page)
    {
        page.Clear();
        page[0] = (byte)PageType.Catalog;
    }

    public static int GetNext(ReadOnlySpan<byte> page) => BinaryPrimitives.ReadInt32LittleEndian(page.Slice(4));
    private static void SetNext(Span<byte> page, int next) => BinaryPrimitives.WriteInt32LittleEndian(page.Slice(4), next);

    public static byte[] ReadBlob(Pager pager, int firstPage)
    {
        var first = pager.Read(firstPage);
        if (first[0] != (byte)PageType.Catalog) throw new CorruptionException($"page {firstPage} is not a catalog page");
        int total = BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(8));
        var blob = new byte[total];
        int off = 0, pageId = firstPage, hops = 0;
        while (off < total)
        {
            var page = pager.Read(pageId);
            if (page[0] != (byte)PageType.Catalog) throw new CorruptionException($"page {pageId} is not a catalog page");
            int used = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(12));
            if (used < 0 || used > Capacity || off + used > total) throw new CorruptionException("catalog chain is damaged");
            page.AsSpan(DataStart, used).CopyTo(blob.AsSpan(off));
            off += used;
            if (off < total)
            {
                pageId = GetNext(page);
                if (pageId == 0 || ++hops > 100_000) throw new CorruptionException("catalog chain ends early");
            }
        }
        return blob;
    }

    public static void WriteBlob(Pager pager, int firstPage, ReadOnlySpan<byte> blob)
    {
        int pageId = firstPage, off = 0;
        while (true)
        {
            var page = pager.Write(pageId);
            int n = Math.Min(Capacity, blob.Length - off);
            if (pageId == firstPage) BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(8), blob.Length);
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(12), n);
            blob.Slice(off, n).CopyTo(page.AsSpan(DataStart));
            off += n;
            if (off >= blob.Length) return;

            int next = GetNext(page);
            if (next == 0)
            {
                next = pager.Allocate();
                Initialize(pager.Write(next));
                SetNext(pager.Write(pageId), next);
            }
            pageId = next;
        }
    }

    /// <summary>All page ids in the chain (used by the integrity checker).</summary>
    public static List<int> ChainPages(Pager pager, int firstPage)
    {
        var pages = new List<int>();
        var seen = new HashSet<int>();
        int id = firstPage;
        while (id != 0)
        {
            if (!seen.Add(id)) throw new CorruptionException("catalog chain has a cycle");
            pages.Add(id);
            id = GetNext(pager.Read(id));
        }
        return pages;
    }
}
