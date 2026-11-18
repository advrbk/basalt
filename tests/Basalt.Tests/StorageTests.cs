using Basalt.Storage;

namespace Basalt.Tests;

public class SlottedPageTests
{
    private static byte[] NewPage()
    {
        var p = new byte[Pager.PageSize];
        SlottedPage.Initialize(p);
        return p;
    }

    [Fact]
    public void Insert_get_delete_keeps_slot_numbers()
    {
        var p = NewPage();
        int a = SlottedPage.Insert(p, "alpha"u8);
        int b = SlottedPage.Insert(p, "bravo"u8);
        int c = SlottedPage.Insert(p, "charlie"u8);
        Assert.Equal([0, 1, 2], new[] { a, b, c });
        SlottedPage.Delete(p, b);
        Assert.False(SlottedPage.IsLive(p, b));
        Assert.Equal("charlie"u8.ToArray(), SlottedPage.GetRow(p, c).ToArray());
        // The freed slot is reused.
        Assert.Equal(b, SlottedPage.Insert(p, "delta"u8));
        Assert.Null(SlottedPage.Validate(p));
    }

    [Fact]
    public void Compaction_reclaims_deleted_space()
    {
        var p = NewPage();
        var row = new byte[1000];
        var slots = new List<int>();
        int s;
        while ((s = SlottedPage.Insert(p, row)) >= 0) slots.Add(s);
        Assert.Equal(4, slots.Count);
        SlottedPage.Delete(p, slots[1]);
        SlottedPage.Delete(p, slots[2]);
        // 2000 bytes are free but not contiguous until compaction runs.
        Assert.True(SlottedPage.Insert(p, new byte[1500]) >= 0);
        Assert.Null(SlottedPage.Validate(p));
        Assert.Equal(row, SlottedPage.GetRow(p, slots[0]).ToArray());
        Assert.Equal(row, SlottedPage.GetRow(p, slots[3]).ToArray());
    }

    [Fact]
    public void Update_grows_in_place_when_there_is_room_and_refuses_when_not()
    {
        var p = NewPage();
        int a = SlottedPage.Insert(p, new byte[100]);
        int b = SlottedPage.Insert(p, new byte[100]);
        Assert.True(SlottedPage.Update(p, a, Enumerable.Repeat((byte)7, 500).ToArray()));
        Assert.Equal(500, SlottedPage.GetRow(p, a).Length);
        Assert.Equal(100, SlottedPage.GetRow(p, b).Length);
        Assert.False(SlottedPage.Update(p, b, new byte[3700]));
        Assert.Null(SlottedPage.Validate(p));
    }

    [Fact]
    public void Randomized_operations_stay_consistent()
    {
        var rng = new Random(11);
        var p = NewPage();
        var model = new Dictionary<int, byte[]>();
        for (int i = 0; i < 5000; i++)
        {
            int op = rng.Next(3);
            if (op == 0 || model.Count == 0)
            {
                var row = new byte[rng.Next(1, 300)];
                rng.NextBytes(row);
                int slot = SlottedPage.Insert(p, row);
                if (slot >= 0) model[slot] = row;
            }
            else if (op == 1)
            {
                int slot = model.Keys.ElementAt(rng.Next(model.Count));
                SlottedPage.Delete(p, slot);
                model.Remove(slot);
            }
            else
            {
                int slot = model.Keys.ElementAt(rng.Next(model.Count));
                var row = new byte[rng.Next(1, 400)];
                rng.NextBytes(row);
                if (SlottedPage.Update(p, slot, row)) model[slot] = row;
            }
            Assert.Null(SlottedPage.Validate(p));
        }
        foreach (var (slot, row) in model) Assert.Equal(row, SlottedPage.GetRow(p, slot).ToArray());
    }
}

public class BufferPoolTests
{
    [Fact]
    public void Evicts_least_recently_used_and_writes_back_dirty_pages()
    {
        using var dir = new TempDir();
        using var pager = Pager.Open(dir.File("t.db"), cachePages: 16);
        pager.BeginTransaction();
        var ids = Enumerable.Range(0, 100).Select(_ => pager.Allocate()).ToList();
        foreach (var id in ids) pager.Write(id)[100] = (byte)(id % 251);
        pager.Commit();

        // Touch every page again: the pool holds 16, so most reads come back from disk.
        foreach (var id in ids) Assert.Equal((byte)(id % 251), pager.Read(id)[100]);
        var stats = pager.GetStats();
        Assert.True(stats.Evictions > 0);
        Assert.True(stats.CachedPages <= 16);
    }

    [Fact]
    public void Uncommitted_pages_are_never_evicted_and_pool_grows_instead()
    {
        using var dir = new TempDir();
        string path = dir.File("t.db");
        var pager = Pager.Open(path, cachePages: 16);
        pager.BeginTransaction();
        for (int i = 0; i < 200; i++) pager.Write(pager.Allocate())[0] = 0xAB;
        Assert.True(pager.GetStats().CachedPages > 16);
        long fileBefore = new FileInfo(path).Length;
        pager.SimulateCrash(); // nothing was committed, so nothing may have reached the data file
        Assert.Equal(fileBefore, new FileInfo(path).Length);

        using var reopened = Pager.Open(path);
        Assert.Equal(2, reopened.PageCount); // header + catalog only
    }
}
