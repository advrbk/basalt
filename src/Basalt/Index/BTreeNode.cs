using System.Buffers.Binary;
using Basalt.Storage;

namespace Basalt.Index;

/// <summary>
/// Layout of one B+tree node page. Cells are variable length and kept in sorted order through a
/// cell-pointer array, the same idea as a slotted heap page:
/// <code>
/// header (16): type | cell count (2) | content start (2) | right pointer (4)
///   leaf:     right pointer = next leaf (0 = none)
///   internal: right pointer = rightmost child
/// cell pointers: 2 bytes each, in key order
/// leaf cell:     key length (2) | key | value length (2) | value
/// internal cell: key length (2) | key | child page (4)     child holds keys &lt; key
/// </code>
/// </summary>
internal static class BTreeNode
{
    public const int HeaderSize = 16;
    public const int PointerSize = 2;
    public const int MaxKeySize = 512;
    public const int MaxValueSize = 64;

    public static void Initialize(Span<byte> p, bool leaf)
    {
        p.Clear();
        p[0] = (byte)(leaf ? PageType.BTreeLeaf : PageType.BTreeInternal);
        SetContentStart(p, Pager.PageSize);
    }

    public static bool IsLeaf(ReadOnlySpan<byte> p) => p[0] == (byte)PageType.BTreeLeaf;
    public static bool IsNode(ReadOnlySpan<byte> p) => p[0] is (byte)PageType.BTreeLeaf or (byte)PageType.BTreeInternal;

    public static int Count(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(1));
    private static void SetCount(Span<byte> p, int n) => BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(1), (ushort)n);

    private static int ContentStart(ReadOnlySpan<byte> p)
    {
        int v = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(3));
        return v == 0 ? Pager.PageSize : v;
    }
    private static void SetContentStart(Span<byte> p, int v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(3), (ushort)(v == Pager.PageSize ? 0 : v));

    public static int GetRight(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadInt32LittleEndian(p.Slice(5));
    public static void SetRight(Span<byte> p, int id) => BinaryPrimitives.WriteInt32LittleEndian(p.Slice(5), id);

    private static int CellOffset(ReadOnlySpan<byte> p, int i) =>
        BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(HeaderSize + i * PointerSize));

    public static ReadOnlySpan<byte> GetKey(ReadOnlySpan<byte> p, int i)
    {
        int off = CellOffset(p, i);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(off));
        return p.Slice(off + 2, len);
    }

    public static ReadOnlySpan<byte> GetValue(ReadOnlySpan<byte> p, int i)
    {
        int off = CellOffset(p, i);
        int klen = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(off));
        int voff = off + 2 + klen;
        int vlen = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(voff));
        return p.Slice(voff + 2, vlen);
    }

    public static int GetChild(ReadOnlySpan<byte> p, int i)
    {
        int off = CellOffset(p, i);
        int klen = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(off));
        return BinaryPrimitives.ReadInt32LittleEndian(p.Slice(off + 2 + klen));
    }

    private static void SetChild(Span<byte> p, int i, int child)
    {
        int off = CellOffset(p, i);
        int klen = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(off));
        BinaryPrimitives.WriteInt32LittleEndian(p.Slice(off + 2 + klen), child);
    }

    /// <summary>Child pointer number <paramref name="i"/> (i == Count means the right pointer).</summary>
    public static int ChildAt(ReadOnlySpan<byte> p, int i) => i == Count(p) ? GetRight(p) : GetChild(p, i);

    public static void SetChildAt(Span<byte> p, int i, int child)
    {
        if (i == Count(p)) SetRight(p, child);
        else SetChild(p, i, child);
    }

    /// <summary>Index of the first key &gt;= <paramref name="key"/>, and whether it is an exact match.</summary>
    public static int LowerBound(ReadOnlySpan<byte> p, ReadOnlySpan<byte> key, out bool found)
    {
        int lo = 0, hi = Count(p);
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (GetKey(p, mid).SequenceCompareTo(key) < 0) lo = mid + 1;
            else hi = mid;
        }
        found = lo < Count(p) && GetKey(p, lo).SequenceEqual(key);
        return lo;
    }

    /// <summary>For an internal node: which child pointer to follow for <paramref name="key"/>.</summary>
    public static int ChildIndexFor(ReadOnlySpan<byte> p, ReadOnlySpan<byte> key)
    {
        // First separator strictly greater than the key; keys equal to a separator live to its right.
        int lo = 0, hi = Count(p);
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (GetKey(p, mid).SequenceCompareTo(key) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public static byte[] MakeLeafCell(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        var cell = new byte[2 + key.Length + 2 + value.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(cell, (ushort)key.Length);
        key.CopyTo(cell.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(cell.AsSpan(2 + key.Length), (ushort)value.Length);
        value.CopyTo(cell.AsSpan(4 + key.Length));
        return cell;
    }

    public static byte[] MakeInternalCell(ReadOnlySpan<byte> key, int child)
    {
        var cell = new byte[2 + key.Length + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(cell, (ushort)key.Length);
        key.CopyTo(cell.AsSpan(2));
        BinaryPrimitives.WriteInt32LittleEndian(cell.AsSpan(2 + key.Length), child);
        return cell;
    }

    /// <summary>Raw bytes of cell <paramref name="i"/>.</summary>
    public static byte[] GetCell(ReadOnlySpan<byte> p, int i)
    {
        int off = CellOffset(p, i);
        int klen = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(off));
        int len = 2 + klen;
        if (IsLeaf(p)) len += 2 + BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(off + len));
        else len += 4;
        return p.Slice(off, len).ToArray();
    }

    public static ReadOnlySpan<byte> CellKey(byte[] cell) =>
        cell.AsSpan(2, BinaryPrimitives.ReadUInt16LittleEndian(cell));

    public static int InternalCellChild(byte[] cell) =>
        BinaryPrimitives.ReadInt32LittleEndian(cell.AsSpan(2 + BinaryPrimitives.ReadUInt16LittleEndian(cell)));

    private static int UsedBytes(ReadOnlySpan<byte> p)
    {
        int n = Count(p), total = 0;
        for (int i = 0; i < n; i++) total += CellSize(p, i);
        return total;
    }

    private static int CellSize(ReadOnlySpan<byte> p, int i)
    {
        int off = CellOffset(p, i);
        int klen = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(off));
        return IsLeaf(p)
            ? 2 + klen + 2 + BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(off + 2 + klen))
            : 2 + klen + 4;
    }

    /// <summary>Total bytes a list of cells needs in a fresh node.</summary>
    public static bool Fits(IReadOnlyList<byte[]> cells, int from, int to)
    {
        int total = 0;
        for (int i = from; i < to; i++) total += cells[i].Length + PointerSize;
        return total <= Pager.PageSize - HeaderSize;
    }

    /// <summary>Inserts a cell at position <paramref name="index"/>. Returns false if the node is full.</summary>
    public static bool InsertCell(Span<byte> p, int index, ReadOnlySpan<byte> cell)
    {
        int n = Count(p);
        int dirEnd = HeaderSize + (n + 1) * PointerSize;
        if (ContentStart(p) - dirEnd < cell.Length)
        {
            if (Pager.PageSize - dirEnd - UsedBytes(p) < cell.Length) return false;
            Compact(p);
        }
        int start = ContentStart(p) - cell.Length;
        cell.CopyTo(p.Slice(start));
        SetContentStart(p, start);

        var ptrs = p.Slice(HeaderSize, (n + 1) * PointerSize);
        ptrs.Slice(index * PointerSize, (n - index) * PointerSize).CopyTo(ptrs.Slice((index + 1) * PointerSize));
        BinaryPrimitives.WriteUInt16LittleEndian(ptrs.Slice(index * PointerSize), (ushort)start);
        SetCount(p, n + 1);
        return true;
    }

    public static void RemoveCell(Span<byte> p, int index)
    {
        int n = Count(p);
        var ptrs = p.Slice(HeaderSize, n * PointerSize);
        ptrs.Slice((index + 1) * PointerSize).CopyTo(ptrs.Slice(index * PointerSize));
        SetCount(p, n - 1);
        if (n - 1 == 0) SetContentStart(p, Pager.PageSize);
    }

    /// <summary>Rewrites the node with exactly these cells (used by splits).</summary>
    public static void Rebuild(Span<byte> p, bool leaf, int right, IReadOnlyList<byte[]> cells, int from, int to)
    {
        Initialize(p, leaf);
        SetRight(p, right);
        for (int i = from; i < to; i++)
            if (!InsertCell(p, i - from, cells[i])) throw new InvalidOperationException("split half does not fit in a node");
    }

    private static void Compact(Span<byte> p)
    {
        int n = Count(p);
        Span<byte> scratch = stackalloc byte[Pager.PageSize];
        int end = Pager.PageSize;
        for (int i = 0; i < n; i++)
        {
            int off = CellOffset(p, i);
            int len = CellSize(p, i);
            end -= len;
            p.Slice(off, len).CopyTo(scratch.Slice(end));
            BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(HeaderSize + i * PointerSize), (ushort)end);
        }
        scratch.Slice(end).CopyTo(p.Slice(end));
        SetContentStart(p, end);
    }
}
