using System.Buffers.Binary;

namespace Basalt.Storage;

/// <summary>
/// A heap page holding variable-length rows. The slot directory grows forward from the header and
/// row bytes grow backward from the end of the page:
/// <code>
/// [header 16][slot 0][slot 1]...[slot n-1] -> free space <- [row n-1]...[row 1][row 0]
/// header: type | slot count (2) | content start (2) | next page (4) | last page (4, first page only)
/// slot:   offset (2) | length (2); offset 0 marks an empty slot
/// </code>
/// A row's address is (page, slot). Rows can move inside the page when it is compacted, but the
/// slot number never changes, so indexes that point at (page, slot) stay valid.
/// </summary>
internal static class SlottedPage
{
    public const int HeaderSize = 16;
    public const int SlotSize = 4;
    /// <summary>Largest row that fits in an empty page.</summary>
    public const int MaxRowSize = Pager.PageSize - HeaderSize - SlotSize;

    public static void Initialize(Span<byte> page)
    {
        page.Clear();
        page[0] = (byte)PageType.Heap;
        SetContentStart(page, Pager.PageSize);
    }

    public static int SlotCount(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(1));
    private static void SetSlotCount(Span<byte> p, int n) => BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(1), (ushort)n);

    // Content start is stored as a ushort; 0 stands for "PageSize" (empty page) since 4096 does not fit.
    private static int ContentStart(ReadOnlySpan<byte> p)
    {
        int v = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(3));
        return v == 0 ? Pager.PageSize : v;
    }
    private static void SetContentStart(Span<byte> p, int v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(3), (ushort)(v == Pager.PageSize ? 0 : v));

    public static int GetNext(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadInt32LittleEndian(p.Slice(5));
    public static void SetNext(Span<byte> p, int next) => BinaryPrimitives.WriteInt32LittleEndian(p.Slice(5), next);
    public static int GetLast(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadInt32LittleEndian(p.Slice(9));
    public static void SetLast(Span<byte> p, int last) => BinaryPrimitives.WriteInt32LittleEndian(p.Slice(9), last);

    private static int SlotOffset(ReadOnlySpan<byte> p, int slot) =>
        BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(HeaderSize + slot * SlotSize));
    private static int SlotLength(ReadOnlySpan<byte> p, int slot) =>
        BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(HeaderSize + slot * SlotSize + 2));
    private static void SetSlot(Span<byte> p, int slot, int offset, int length)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(HeaderSize + slot * SlotSize), (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(HeaderSize + slot * SlotSize + 2), (ushort)length);
    }

    public static bool IsLive(ReadOnlySpan<byte> p, int slot) =>
        slot >= 0 && slot < SlotCount(p) && SlotOffset(p, slot) != 0;

    /// <summary>The bytes of a live row.</summary>
    public static ReadOnlySpan<byte> GetRow(ReadOnlySpan<byte> p, int slot)
    {
        if (!IsLive(p, slot)) throw new BasaltException($"row slot {slot} is empty");
        int off = SlotOffset(p, slot), len = SlotLength(p, slot);
        if (off < HeaderSize || off + len > Pager.PageSize) throw new CorruptionException($"row slot {slot} points outside its page");
        return p.Slice(off, len);
    }

    private static int LiveBytes(ReadOnlySpan<byte> p, int exceptSlot = -1)
    {
        int n = SlotCount(p), total = 0;
        for (int i = 0; i < n; i++)
            if (i != exceptSlot && SlotOffset(p, i) != 0) total += SlotLength(p, i);
        return total;
    }

    private static int FreeSlot(ReadOnlySpan<byte> p)
    {
        int n = SlotCount(p);
        for (int i = 0; i < n; i++)
            if (SlotOffset(p, i) == 0) return i;
        return -1;
    }

    /// <summary>Inserts a row and returns its slot, or -1 if the page is too full.</summary>
    public static int Insert(Span<byte> p, ReadOnlySpan<byte> row)
    {
        int slot = FreeSlot(p);
        int slotCount = SlotCount(p);
        int dirEnd = HeaderSize + (slot < 0 ? slotCount + 1 : slotCount) * SlotSize;

        if (ContentStart(p) - dirEnd < row.Length)
        {
            // Not enough contiguous space: would compaction free enough?
            if (Pager.PageSize - dirEnd - LiveBytes(p) < row.Length) return -1;
            Compact(p);
        }

        if (slot < 0)
        {
            slot = slotCount;
            SetSlotCount(p, slotCount + 1);
        }
        int start = ContentStart(p) - row.Length;
        row.CopyTo(p.Slice(start));
        SetContentStart(p, start);
        SetSlot(p, slot, start, row.Length);
        return slot;
    }

    /// <summary>Removes a row. Its space is reclaimed by the next compaction.</summary>
    public static void Delete(Span<byte> p, int slot)
    {
        if (!IsLive(p, slot)) throw new BasaltException($"row slot {slot} is empty");
        SetSlot(p, slot, 0, 0);
        // Trailing empty slots can be dropped; slots in the middle must stay so others keep their numbers.
        int n = SlotCount(p);
        while (n > 0 && SlotOffset(p, n - 1) == 0) n--;
        SetSlotCount(p, n);
        if (n == 0) SetContentStart(p, Pager.PageSize);
    }

    /// <summary>Replaces a row in place (same slot). Returns false if the new version does not fit here.</summary>
    public static bool Update(Span<byte> p, int slot, ReadOnlySpan<byte> row)
    {
        if (!IsLive(p, slot)) throw new BasaltException($"row slot {slot} is empty");
        int oldLen = SlotLength(p, slot);
        if (row.Length <= oldLen)
        {
            int off = SlotOffset(p, slot);
            row.CopyTo(p.Slice(off));
            SetSlot(p, slot, off, row.Length);
            return true;
        }

        int dirEnd = HeaderSize + SlotCount(p) * SlotSize;
        if (Pager.PageSize - dirEnd - LiveBytes(p, exceptSlot: slot) < row.Length) return false;

        // Free the old copy, compact, then write the new copy into the same slot.
        SetSlot(p, slot, 0, 0);
        Compact(p);
        int start = ContentStart(p) - row.Length;
        row.CopyTo(p.Slice(start));
        SetContentStart(p, start);
        SetSlot(p, slot, start, row.Length);
        return true;
    }

    /// <summary>Packs live rows against the end of the page, keeping slot numbers.</summary>
    private static void Compact(Span<byte> p)
    {
        int n = SlotCount(p);
        Span<byte> scratch = stackalloc byte[Pager.PageSize];
        int end = Pager.PageSize;
        for (int i = 0; i < n; i++)
        {
            int off = SlotOffset(p, i);
            if (off == 0) continue;
            int len = SlotLength(p, i);
            end -= len;
            p.Slice(off, len).CopyTo(scratch.Slice(end));
            SetSlot(p, i, end, len);
        }
        scratch.Slice(end).CopyTo(p.Slice(end));
        SetContentStart(p, end);
    }

    /// <summary>Structural check used by the integrity checker. Returns null if fine.</summary>
    public static string? Validate(ReadOnlySpan<byte> p)
    {
        if (p[0] != (byte)PageType.Heap) return "not a heap page";
        int n = SlotCount(p);
        int dirEnd = HeaderSize + n * SlotSize;
        int contentStart = ContentStart(p);
        if (dirEnd > contentStart) return "slot directory overlaps row data";
        var ranges = new List<(int Start, int End)>();
        for (int i = 0; i < n; i++)
        {
            int off = SlotOffset(p, i);
            if (off == 0) continue;
            int len = SlotLength(p, i);
            if (off < contentStart || off + len > Pager.PageSize) return $"slot {i} points outside the row area";
            ranges.Add((off, off + len));
        }
        ranges.Sort();
        for (int i = 1; i < ranges.Count; i++)
            if (ranges[i].Start < ranges[i - 1].End) return "two rows overlap";
        if (n > 0 && SlotOffset(p, n - 1) == 0) return "trailing empty slot was not trimmed";
        return null;
    }
}
