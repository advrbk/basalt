namespace Basalt.Storage;

/// <summary>One cached page.</summary>
internal sealed class Frame
{
    public Frame(int pageId)
    {
        PageId = pageId;
        Data = new byte[Pager.PageSize];
    }

    public int PageId { get; }
    public byte[] Data { get; }

    /// <summary>Content differs from the data file and must be written back before eviction.</summary>
    public bool Dirty;

    /// <summary>
    /// Modified by the transaction that is still running. Such frames are kept out of the LRU list,
    /// so they can never be evicted: uncommitted bytes never reach the data file ("no steal").
    /// </summary>
    public bool InTransaction;

    public LinkedListNode<Frame>? LruNode;
}

/// <summary>
/// Page cache with least-recently-used eviction and dirty-page write-back.
/// The LRU list only holds frames that may be evicted; frames owned by the running
/// transaction live only in the dictionary until commit or rollback.
/// All methods are thread safe (one lock) because concurrent readers share the pool.
/// </summary>
internal sealed class BufferPool
{
    private readonly DiskManager _disk;
    private readonly Action? _beforeWriteBack;
    private readonly Dictionary<int, Frame> _frames = new();
    private readonly LinkedList<Frame> _lru = new(); // First = most recently used
    private readonly object _lock = new();

    public int Capacity { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }

    /// <param name="beforeWriteBack">Called before a dirty page is written to the data file
    /// (the pager uses it to make sure the WAL is fsynced first).</param>
    public BufferPool(DiskManager disk, int capacity, Action? beforeWriteBack = null)
    {
        if (capacity < 16) throw new ArgumentOutOfRangeException(nameof(capacity), "buffer pool needs at least 16 pages");
        _disk = disk;
        _beforeWriteBack = beforeWriteBack;
        Capacity = capacity;
    }

    public int Count { get { lock (_lock) return _frames.Count; } }

    /// <summary>Returns the frame for a page, reading it from disk on a miss.</summary>
    public Frame Get(int pageId)
    {
        lock (_lock)
        {
            if (_frames.TryGetValue(pageId, out var frame))
            {
                Hits++;
                if (frame.LruNode != null && frame.LruNode != _lru.First)
                {
                    _lru.Remove(frame.LruNode);
                    _lru.AddFirst(frame.LruNode);
                }
                return frame;
            }

            Misses++;
            MakeRoom();
            frame = new Frame(pageId);
            _disk.Read(pageId, frame.Data);
            frame.LruNode = _lru.AddFirst(frame);
            _frames.Add(pageId, frame);
            return frame;
        }
    }

    /// <summary>Adds a zero-filled frame for a newly allocated page, already owned by the transaction.</summary>
    public Frame CreateForTransaction(int pageId)
    {
        lock (_lock)
        {
            RemoveLocked(pageId);
            MakeRoom();
            var frame = new Frame(pageId) { InTransaction = true };
            _frames.Add(pageId, frame);
            return frame;
        }
    }

    public Frame? TryGetResident(int pageId)
    {
        lock (_lock) return _frames.GetValueOrDefault(pageId);
    }

    /// <summary>Marks a frame as owned by the running transaction (not evictable).</summary>
    public void Pin(Frame frame)
    {
        lock (_lock)
        {
            if (frame.InTransaction) return;
            if (!_frames.TryGetValue(frame.PageId, out var resident) || !ReferenceEquals(resident, frame))
                throw new InvalidOperationException($"page {frame.PageId} was evicted while in use");
            frame.InTransaction = true;
            if (frame.LruNode != null)
            {
                _lru.Remove(frame.LruNode);
                frame.LruNode = null;
            }
        }
    }

    /// <summary>Releases a transaction frame back to the LRU list. Its content now needs write-back.</summary>
    public void Unpin(Frame frame)
    {
        lock (_lock)
        {
            frame.InTransaction = false;
            frame.Dirty = true;
            frame.LruNode ??= _lru.AddFirst(frame);
        }
    }

    /// <summary>Evicts down to capacity again after a large transaction made the pool grow.</summary>
    public void Trim()
    {
        lock (_lock) MakeRoom(Capacity);
    }

    /// <summary>Forgets a page without writing it (used when a rollback discards a newly allocated page).</summary>
    public void Drop(int pageId)
    {
        lock (_lock) RemoveLocked(pageId);
    }

    /// <summary>Writes every committed dirty page to the data file (no fsync).</summary>
    public int FlushDirty()
    {
        lock (_lock)
        {
            int n = 0;
            foreach (var frame in _frames.Values.OrderBy(f => f.PageId))
            {
                if (frame.Dirty && !frame.InTransaction)
                {
                    if (n == 0) _beforeWriteBack?.Invoke();
                    _disk.Write(frame.PageId, frame.Data);
                    frame.Dirty = false;
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>Drops every cached page without writing anything (used to simulate a crash).</summary>
    public void DiscardAll()
    {
        lock (_lock)
        {
            _frames.Clear();
            _lru.Clear();
        }
    }

    private void RemoveLocked(int pageId)
    {
        if (_frames.Remove(pageId, out var old) && old.LruNode != null)
        {
            _lru.Remove(old.LruNode);
            old.LruNode = null;
        }
    }

    private void MakeRoom() => MakeRoom(Capacity - 1);

    /// <summary>Evicts least-recently-used frames until at most <paramref name="target"/> remain (or nothing is evictable).</summary>
    private void MakeRoom(int target)
    {
        // If every frame belongs to the running transaction the pool grows past its
        // capacity instead of failing; it shrinks again after commit.
        while (_frames.Count > target && _lru.Last != null)
        {
            var victim = _lru.Last.Value;
            _lru.RemoveLast();
            victim.LruNode = null;
            if (victim.Dirty)
            {
                // Safe: this page's content is committed and its WAL records are on disk.
                _beforeWriteBack?.Invoke();
                _disk.Write(victim.PageId, victim.Data);
                victim.Dirty = false;
            }
            _frames.Remove(victim.PageId);
            Evictions++;
        }
    }
}
