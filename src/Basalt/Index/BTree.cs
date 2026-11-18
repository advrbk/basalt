using Basalt.Storage;

namespace Basalt.Index;

/// <summary>
/// A B+tree mapping unique byte-string keys to small byte-string values, stored in pager pages.
/// Values live only in leaves; leaves are linked left to right for range scans.
/// The root page id never changes: when the root splits, its contents move to a new page and the
/// root becomes an internal node above the two halves, so nothing that stores the root id needs updating.
/// Deletes remove the entry from its leaf but do not merge under-full nodes (see README limitations).
/// </summary>
internal sealed class BTree
{
    private readonly Pager _pager;

    public int RootPage { get; }

    public BTree(Pager pager, int rootPage)
    {
        _pager = pager;
        RootPage = rootPage;
    }

    public static BTree Create(Pager pager)
    {
        int root = pager.Allocate();
        BTreeNode.Initialize(pager.Write(root), leaf: true);
        return new BTree(pager, root);
    }

    private static void CheckSizes(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (key.Length > BTreeNode.MaxKeySize)
            throw new ConstraintException($"index key is {key.Length} bytes; the limit is {BTreeNode.MaxKeySize}");
        if (value.Length > BTreeNode.MaxValueSize)
            throw new ArgumentException("index value too large", nameof(value));
    }

    private int FindLeaf(ReadOnlySpan<byte> key, List<(int PageId, int ChildIndex)>? path)
    {
        int pageId = RootPage;
        for (int depth = 0; ; depth++)
        {
            var page = _pager.Read(pageId);
            if (!BTreeNode.IsNode(page)) throw new CorruptionException($"page {pageId} is not a B+tree node");
            if (BTreeNode.IsLeaf(page)) return pageId;
            if (depth > 64) throw new CorruptionException("B+tree is too deep (cycle?)");
            int idx = BTreeNode.ChildIndexFor(page, key);
            path?.Add((pageId, idx));
            pageId = BTreeNode.ChildAt(page, idx);
        }
    }

    public byte[]? Find(ReadOnlySpan<byte> key)
    {
        var leaf = _pager.Read(FindLeaf(key, null));
        int i = BTreeNode.LowerBound(leaf, key, out bool found);
        return found ? BTreeNode.GetValue(leaf, i).ToArray() : null;
    }

    /// <summary>Adds a key. Returns false (and changes nothing) if the key already exists.</summary>
    public bool Insert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        CheckSizes(key, value);
        var path = new List<(int PageId, int ChildIndex)>();
        int leafId = FindLeaf(key, path);
        int pos = BTreeNode.LowerBound(_pager.Read(leafId), key, out bool found);
        if (found) return false;

        var cell = BTreeNode.MakeLeafCell(key, value);
        if (BTreeNode.InsertCell(_pager.Write(leafId), pos, cell)) return true;

        // The leaf is full: split it and push a separator up, splitting parents as needed.
        var (separator, rightId) = Split(leafId, pos, cell);
        int leftId = leafId;
        for (int level = path.Count - 1; level >= 0; level--)
        {
            var (parentId, childIndex) = path[level];
            var parent = _pager.Write(parentId);
            // The pointer that led to the old node now leads to its right half; the left half
            // goes in front of it under the new separator.
            BTreeNode.SetChildAt(parent, childIndex, rightId);
            var sepCell = BTreeNode.MakeInternalCell(separator, leftId);
            if (BTreeNode.InsertCell(parent, childIndex, sepCell)) return true;
            (separator, rightId) = Split(parentId, childIndex, sepCell);
            leftId = parentId;
        }

        GrowRoot(separator, rightId);
        return true;
    }

    /// <summary>Inserts or overwrites.</summary>
    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (Insert(key, value)) return;
        Delete(key);
        Insert(key, value);
    }

    /// <summary>Removes a key. Returns false if it was not there.</summary>
    public bool Delete(ReadOnlySpan<byte> key)
    {
        int leafId = FindLeaf(key, null);
        int pos = BTreeNode.LowerBound(_pager.Read(leafId), key, out bool found);
        if (!found) return false;
        BTreeNode.RemoveCell(_pager.Write(leafId), pos);
        return true;
    }

    /// <summary>
    /// Splits node <paramref name="pageId"/> while inserting <paramref name="newCell"/> at <paramref name="pos"/>.
    /// The node keeps the lower half; a new page gets the upper half. Returns the separator key
    /// (every key in the right half is &gt;= it) and the new page id.
    /// </summary>
    private (byte[] Separator, int RightId) Split(int pageId, int pos, byte[] newCell)
    {
        var page = _pager.Write(pageId);
        bool leaf = BTreeNode.IsLeaf(page);
        int n = BTreeNode.Count(page);
        var cells = new List<byte[]>(n + 1);
        for (int i = 0; i < n; i++) cells.Add(BTreeNode.GetCell(page, i));
        cells.Insert(pos, newCell);
        int oldRight = BTreeNode.GetRight(page);

        // Split point: balance the bytes, keeping at least one cell on each side.
        int total = cells.Sum(c => c.Length + BTreeNode.PointerSize);
        int mid = 0, acc = 0;
        while (mid < cells.Count - 1 && acc + cells[mid].Length + BTreeNode.PointerSize <= total / 2)
        {
            acc += cells[mid].Length + BTreeNode.PointerSize;
            mid++;
        }
        mid = Math.Clamp(mid, 1, cells.Count - (leaf ? 1 : 2));

        int rightId = _pager.Allocate();
        var right = _pager.Write(rightId);
        page = _pager.Write(pageId);

        if (leaf)
        {
            // Leaves: right half starts at mid; separator is a copy of its first key.
            BTreeNode.Rebuild(right, leaf: true, right: oldRight, cells, mid, cells.Count);
            BTreeNode.Rebuild(page, leaf: true, right: rightId, cells, 0, mid);
            return (BTreeNode.CellKey(cells[mid]).ToArray(), rightId);
        }

        // Internal nodes: the middle cell's key moves up; its child becomes the left half's right pointer.
        int middleChild = BTreeNode.InternalCellChild(cells[mid]);
        BTreeNode.Rebuild(right, leaf: false, right: oldRight, cells, mid + 1, cells.Count);
        BTreeNode.Rebuild(page, leaf: false, right: middleChild, cells, 0, mid);
        return (BTreeNode.CellKey(cells[mid]).ToArray(), rightId);
    }

    /// <summary>The root split: move its (left-half) contents to a new page and make it the parent.</summary>
    private void GrowRoot(byte[] separator, int rightId)
    {
        int leftId = _pager.Allocate();
        var root = _pager.Write(RootPage);
        root.CopyTo(_pager.Write(leftId), 0);
        BTreeNode.Initialize(root, leaf: false);
        BTreeNode.SetRight(root, rightId);
        BTreeNode.InsertCell(root, 0, BTreeNode.MakeInternalCell(separator, leftId));
    }

    /// <summary>
    /// Entries with key &gt;= <paramref name="from"/> (or all if null) in key order, stopping after
    /// keys greater than <paramref name="to"/> (inclusive upper bound, or no bound if null).
    /// Keys and values are copied, so the caller may hold on to them.
    /// </summary>
    public IEnumerable<(byte[] Key, byte[] Value)> Scan(byte[]? from = null, byte[]? to = null)
    {
        int leafId;
        int pos;
        if (from == null)
        {
            leafId = LeftmostLeaf();
            pos = 0;
        }
        else
        {
            leafId = FindLeaf(from, null);
            pos = BTreeNode.LowerBound(_pager.Read(leafId), from, out _);
        }

        while (leafId != 0)
        {
            var leaf = _pager.Read(leafId);
            int n = BTreeNode.Count(leaf);
            var batch = new List<(byte[], byte[])>(Math.Max(0, n - pos));
            bool done = false;
            for (int i = pos; i < n; i++)
            {
                var key = BTreeNode.GetKey(leaf, i);
                if (to != null && key.SequenceCompareTo(to) > 0)
                {
                    done = true;
                    break;
                }
                batch.Add((key.ToArray(), BTreeNode.GetValue(leaf, i).ToArray()));
            }
            int next = BTreeNode.GetRight(leaf);
            foreach (var item in batch) yield return item;
            if (done) yield break;
            leafId = next;
            pos = 0;
        }
    }

    private int LeftmostLeaf()
    {
        int pageId = RootPage;
        for (int depth = 0; depth <= 64; depth++)
        {
            var page = _pager.Read(pageId);
            if (BTreeNode.IsLeaf(page)) return pageId;
            pageId = BTreeNode.ChildAt(page, 0);
        }
        throw new CorruptionException("B+tree is too deep (cycle?)");
    }

    /// <summary>Height of the tree (1 = root is a leaf).</summary>
    public int Height()
    {
        int h = 1, pageId = RootPage;
        while (!BTreeNode.IsLeaf(_pager.Read(pageId)))
        {
            pageId = BTreeNode.ChildAt(_pager.Read(pageId), 0);
            h++;
        }
        return h;
    }

    /// <summary>
    /// Full structural check: node types, key order inside nodes, keys within their parent's
    /// separator bounds, all leaves at one depth, and a leaf chain that visits the same keys in
    /// order. Returns problems found (empty when healthy) and the pages that belong to the tree.
    /// </summary>
    public (List<string> Problems, List<int> Pages, long Entries) Check()
    {
        var problems = new List<string>();
        var pages = new List<int>();
        var leaves = new List<int>();
        int leafDepth = -1;
        long entries = 0;

        void Visit(int pageId, byte[]? low, byte[]? high, int depth)
        {
            if (depth > 64) { problems.Add("tree deeper than 64 levels"); return; }
            pages.Add(pageId);
            var p = _pager.Read(pageId);
            if (!BTreeNode.IsNode(p)) { problems.Add($"page {pageId} is not a B+tree node"); return; }
            int n = BTreeNode.Count(p);
            for (int i = 0; i < n; i++)
            {
                var k = BTreeNode.GetKey(p, i);
                if (i > 0 && BTreeNode.GetKey(p, i - 1).SequenceCompareTo(k) >= 0)
                    problems.Add($"page {pageId}: keys out of order at cell {i}");
                if (low != null && k.SequenceCompareTo(low) < 0)
                    problems.Add($"page {pageId}: key below its lower bound");
                if (high != null && k.SequenceCompareTo(high) >= 0)
                    problems.Add($"page {pageId}: key at or above its upper bound");
            }

            if (BTreeNode.IsLeaf(p))
            {
                if (leafDepth < 0) leafDepth = depth;
                else if (leafDepth != depth) problems.Add($"leaf {pageId} is at depth {depth}, expected {leafDepth}");
                leaves.Add(pageId);
                entries += n;
                return;
            }

            if (n == 0) problems.Add($"internal page {pageId} has no keys");
            byte[]? prev = low;
            for (int i = 0; i <= n; i++)
            {
                byte[]? sep = i < n ? BTreeNode.GetKey(p, i).ToArray() : high;
                Visit(BTreeNode.ChildAt(p, i), prev, sep, depth + 1);
                prev = sep;
            }
        }

        Visit(RootPage, null, null, 0);

        // The leaf chain must link the leaves exactly in left-to-right order.
        for (int i = 0; i < leaves.Count; i++)
        {
            int expectedNext = i + 1 < leaves.Count ? leaves[i + 1] : 0;
            int actual = BTreeNode.GetRight(_pager.Read(leaves[i]));
            if (actual != expectedNext)
                problems.Add($"leaf {leaves[i]} links to {actual}, expected {expectedNext}");
        }
        return (problems, pages, entries);
    }
}
