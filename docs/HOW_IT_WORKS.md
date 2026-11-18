# How Basalt works

This is the guide to read before talking about Basalt in an interview. It explains the ideas in plain language first, then walks through the code, then covers the problems that were hard to get right, and ends with ten likely questions and answers.

## 1. The core ideas

### A database file is an array of 4 KB pages

Everything lives in one file, `name.db`, cut into fixed 4096-byte pages. Page 0 is a header (magic bytes, format version, page count, where the catalog starts). Every other page is one of: a catalog page (the schema), a heap page (table rows) or a B+tree node (an index). Byte 0 of every page says which kind it is.

Fixed-size pages keep things simple. A page id times 4096 is its offset in the file, one page is one read or write, and the cache can be a dictionary from page id to a 4 KB byte array.

### The buffer pool caches pages

Reading from disk for every row would be slow, so `BufferPool` keeps recently used pages in memory, up to a fixed number (2048 pages, 8 MB, by default). When it is full and a new page is needed, it throws out the least recently used page (LRU). If that page was changed and not yet written, it is written to the data file first. The pool tracks this with a `Dirty` flag per page.

### Rows live in slotted pages

A heap page stores variable-length rows. A small slot directory at the front says where each row starts and how long it is, and the row bytes are packed at the back of the page. The two grow towards each other. A row's address is `(page id, slot number)`, called a `RowId`. When rows are deleted the page can be compacted (rows slide together), but slot numbers never change, so a `RowId` stays valid. Indexes store `RowId`s, which is why that matters.

### Indexes are B+trees over bytes

Each index is a B+tree. Inner nodes hold separator keys and child page ids. Leaves hold the keys and values and are linked left to right, so a range scan finds its first key and then walks the leaf chain.

The tree only knows about byte strings. `KeyEncoder` turns SQL values into bytes that sort the same way as the values: integers become big-endian with the sign bit flipped (so -1 sorts before 0), reals get a similar bit trick, and text becomes UTF-8 with a terminator. Comparing two keys is then a plain `memcmp`, whatever the column type.

- The primary key index maps `encoded key -> RowId`.
- A secondary index (`CREATE INDEX`) stores `encoded value + RowId` as the key, with an empty value. Appending the RowId makes every key unique even when many rows share a value, and a lookup for value `v` becomes a range scan over every key that starts with `encode(v)`.

### The write-ahead log makes commits durable

A committed transaction must survive a crash. Writing every changed page into the data file at commit would be slow (random writes), and a crash halfway through would leave the file half old, half new.

Basalt does what SQLite's WAL mode and many other engines do:

1. While a transaction runs, its changed pages stay in memory and are **pinned**: the buffer pool may not evict them. Uncommitted bytes never reach the data file. This is called "no steal".
2. At `COMMIT`, a full copy of every changed page is appended to the log file `name.db-wal`, followed by a COMMIT record, and the log is fsynced (`FlushFileBuffers` on Windows). When `fsync` returns, the commit is durable. Only then does `COMMIT` return to the caller.
3. The pages are now ordinary dirty pages. They get written to the data file later, when evicted or at a checkpoint.
4. A **checkpoint** writes all dirty pages to the data file, fsyncs the data file, and only then empties the log. The order matters: the log may only be thrown away once the data file holds everything the log was protecting.
5. On startup, **recovery** reads the log from the start and copies the page images of every transaction that has a valid COMMIT record into the data file. A transaction without its COMMIT record (the process died while writing it) is ignored.

Every log record carries a CRC-32 checksum. A torn or half-written record fails its checksum and replay stops there. The log header holds a random "salt" that changes at every checkpoint and is mixed into every checksum, so leftover bytes from an older log can never pass as current records.

Because the log stores whole page images (not "insert row X" operations), replay is just "copy these bytes to page N". Doing it twice gives the same result, so a crash during recovery is harmless: the next startup simply replays again.

### Rollback uses before-images

The first time a transaction touches a page, the pager saves a copy of the page as it was. `ROLLBACK` copies those back. Pages that the transaction allocated are simply dropped. Because uncommitted changes never left memory, nothing on disk needs undoing, so there is no undo log at all.

The same trick gives **statement-level rollback**: inside `BEGIN ... COMMIT`, a failing statement (for example an INSERT that hits a duplicate primary key on its third row) is undone on its own, using copies taken at the start of that statement, and the transaction stays open.

### Concurrency: one writer or many readers

Basalt uses one readers-writer lock for the whole database.

- Any number of read-only statements can run at once.
- A write takes the lock exclusively. In autocommit mode it holds it for one statement; after `BEGIN` it holds it until `COMMIT` or `ROLLBACK`.
- The lock alternates fairly, so a busy writer cannot starve readers and a stream of readers cannot starve a writer.

This is serializable: every transaction sees the database as if it ran alone. Readers never see uncommitted data, because they cannot run while a write transaction is open. The cost is less concurrency: readers wait for writers. Why this choice: it is the simplest model that is clearly correct, and it fits the storage design (one set of pages in memory, no version chains). MVCC (keeping old row versions so readers do not wait) is the next step up and is listed as a limitation.

### SQL: tokenizer, parser, planner, executor

1. `Lexer` turns text into tokens (`SELECT`, `id`, `=`, `42`, ...), tracking line and column for error messages.
2. `Parser` is recursive descent: one method per grammar rule, and one method per precedence level for expressions (`OR` < `AND` < `NOT` < comparisons < `||` < `+ -` < `* / %` < unary minus). It builds an AST of C# records.
3. `Planner` turns a `SELECT` into a tree of operators. It is rule based:
   - Split `WHERE` into its top-level `AND` terms. A term like `column = constant` on an indexed column becomes an index lookup; `<`, `<=`, `>`, `>=` become a range scan. Otherwise the table is scanned in full.
   - Joins are planned left to right. If the join condition is `outer = inner.column` and that column has an index, use an index nested-loop join. Any other equality becomes a hash join, and no equality at all becomes a plain nested-loop join.
   - The full `WHERE` is always applied again as a filter on top. So the index only has to return a superset of the right rows, which keeps index use easy to get right.
4. Operators are iterators (the "Volcano" model): each one pulls rows from its child, one at a time. In C# that is `IEnumerable<Value[]>` with `yield return`. `EXPLAIN` prints the tree.

## 2. Walkthrough of the key files

```
src/Basalt/
  Storage/
    DiskManager.cs      read/write one page at offset id*4096; fsync
    BufferPool.cs       page cache: LRU list, dirty flags, pinning for running transactions
    WriteAheadLog.cs    append a commit batch + fsync; recovery scan with CRC checks; reset
    Pager.cs            the storage front door: Read/Write/Allocate pages, Begin/Commit/Rollback,
                        statement savepoints, Checkpoint, recovery on open
    SlottedPage.cs      heap page layout: insert/delete/update/compact rows, validation
    TableHeap.cs        a table = chain of heap pages; RowId
    CatalogPages.cs     stores the serialized schema across a page chain
    Crc32.cs, PageLayout.cs
  Index/
    BTreeNode.cs        node page layout: sorted cell pointers, binary search, insert/remove cell
    BTree.cs            find, insert with splits, delete, range scan, structural Check()
    KeyEncoder.cs       order-preserving value -> bytes
  Catalog/Catalog.cs    tables, columns, indexes; CREATE TABLE / CREATE INDEX
  Values/               Value (one SQL value), RowSerializer (row <-> bytes)
  Sql/                  Lexer, Ast, Parser
  Query/
    Expressions.cs      RowSchema, bound expressions and their evaluation (3-valued logic)
    Planner.cs          access paths, join strategy, aggregation, ORDER BY / LIMIT
    PlanNodes.cs        the operators: scans, filter, joins, aggregate, sort, limit, project
    Executor.cs         INSERT / UPDATE / DELETE / DDL and index maintenance
  Engine/
    BasaltDatabase.cs   public API: Open, Execute, Prepare, OpenSession, Checkpoint, CheckIntegrity
    BasaltSession.cs    transaction state, locking, statement savepoints
    DatabaseLock.cs     fair readers-writer lock
    IntegrityChecker.cs cross-checks every page, row and index entry
src/Basalt.Cli/         REPL, ASCII tables, the scripted demo
src/Basalt.CrashTest/   kill-and-verify harness and the worker process it kills
bench/Basalt.Bench/     Stopwatch benchmarks, optional SQLite comparison
tests/Basalt.Tests/     xUnit tests
```

Good order to read the code in: `Pager.cs` (the durability rules are all in one place), `WriteAheadLog.cs`, `BTree.cs`, `SlottedPage.cs`, then `Planner.cs` and `Executor.cs`, then `Harness.cs`.

### Following one INSERT through the system

`db.Execute("INSERT INTO items VALUES (?, ?)", 1, "lamp")`

1. `BasaltSession.Execute` parses the text, sees a write outside `BEGIN`, takes the write lock and calls `Pager.BeginTransaction`.
2. `Executor.Insert` converts the values to the column types, checks `NOT NULL`, and looks the primary key up in its B+tree to reject duplicates.
3. `TableHeap.Insert` asks the pager for the table's last heap page with `Pager.Write(id)`. That call saves a before-image of the page and pins it. `SlottedPage.Insert` places the row bytes and returns the slot.
4. The primary key B+tree gets `encode(1) -> RowId`. If the leaf is full it splits, and the split can travel up to the root.
5. Each secondary index gets `encode(value) + RowId`.
6. `Pager.Commit` appends every pinned page to the WAL plus a COMMIT record, fsyncs, unpins the pages (now dirty, evictable) and, if the WAL has grown past 4 MB, checkpoints.
7. The session releases the lock and returns "inserted 1 row".

## 3. The hard problems and how they were solved

**Making rollback and crash recovery simple at the same time.** Logging operations ("insert row X") needs both redo and undo logic, and each operation needs its own inverse. Logging full page images with a no-steal buffer pool removes undo completely: uncommitted pages never reach the disk, so recovery only ever redoes, and redo is "copy 4 KB". The price is log volume (a one-row insert writes about three 4 KB pages to the log) and that a transaction's changed pages must fit in memory.

**Knowing where the log really ends.** After a crash the end of the log can be garbage: half a record, or bytes from before the last reset. Each record has a CRC over its contents plus the log's salt, and the scan stops at the first record that fails. Only transactions whose COMMIT record passed are applied. A unit test cuts the log at 28 random points and checks that the recovered table is always exactly the first N transactions, never a partial one. Another flips a byte in the middle of the log.

**Checkpoint ordering.** If the log were emptied before the data file was fsynced, a crash between the two would lose data that was in neither place. The order is: write dirty pages, fsync the data file, then reset the log. A crash at any point either finds the log intact (replay again, harmless) or finds the data file already complete.

**B+tree splits that reach the root.** When the root splits, a new root is normally created and every reference to the root must change. Basalt keeps the root's page id fixed: the root's contents move to a new page and the root page itself becomes the new parent. The catalog never needs updating on a split.

**Index entries that follow moving rows.** An UPDATE that makes a row bigger than its page can hold moves the row to another page, so its RowId changes, and every index entry for it must be rewritten. The executor removes the old index entries, updates (maybe moves) the row, then inserts new entries. It also collects all target rows before changing any of them, otherwise a moved row could be found and updated a second time later in the same scan (the "Halloween problem").

**Keeping savepoints cheap.** Statement-level rollback first made bulk loading slow: every INSERT inside a transaction copied the pages it touched with a fresh 4 KB allocation. The fix rents those copies from `ArrayPool` and shares the transaction's copy when a page is first touched in the same statement. Together with removing a `List<byte>` from the key encoder, this roughly doubled bulk-insert speed during development (that before/after was not kept as a reproducible benchmark; the current numbers are in the README).

**Reader starvation, found by a test.** The first lock let a waiting writer block new readers. With a writer committing back to back, the readers in the concurrency test never got a turn. The fix makes the lock alternate: when a writer leaves, the readers already waiting go next.

**A cache that never shrank.** A large transaction pins all its pages, so the pool grows past its capacity. Nothing shrank it again until a cache miss happened. A test caught it, and commit and rollback now trim the pool back to its capacity.

**The write-ahead rule when fsync is turned off.** `BasaltOptions.SyncOnCommit = false` skips the fsync at commit. The first version still let the buffer pool write committed pages to the data file on eviction or checkpoint while their WAL records were only in the OS cache. After a power cut the data file could then hold some pages of a transaction whose log records were lost, with nothing left to repair it. The rule every WAL engine follows is "log before data": a page may only be written to the data file after the log records describing it are on disk. The buffer pool now calls `WriteAheadLog.SyncIfNeeded()` before it writes any dirty page back. With the default (fsync on every commit) this costs nothing, because the log is always already synced. A unit test checks that, with sync off, the WAL is fsynced before evicted pages reach the data file.

**Proving durability, not just claiming it.** See the next section.

### The crash harness

`Basalt.CrashTest` starts a separate worker process that commits transactions in a loop. Each transaction inserts log rows, moves money between accounts, sometimes deletes or rewrites old rows (which makes rows move between pages), sometimes renames an account owner (which changes a secondary index), and records its number in a `meta` table. About one in ten transactions rolls back on purpose.

The worker prints `ACK n` only after `COMMIT` for transaction `n` has returned, which is after the fsync. The harness kills the process at a random moment with `TerminateProcess`. In about one cycle in ten it then starts a second worker straight away and kills it blindly 0-150 ms after start. The first kill left committed work in the WAL that nobody has recovered yet, so this second kill can land while the worker is still starting up or replaying the log. (The harness's own reopen always finishes recovery and checkpoints on close, so without this step no worker would ever be killed mid-recovery.) After opening, each worker prints `RESUME n`, the last committed transaction it found, so the harness also learns about a commit the first worker made durable but never acknowledged. Then the harness opens the database itself and checks:

- the database contains transaction `lastAck` or the one committed right after it (it is fine to be killed after the fsync but before printing the ACK), and nothing later;
- every table equals what an in-memory model predicts after replaying the same transactions, which are generated from `(seed, transaction number)` so both sides agree;
- the total balance never changed (money is only moved);
- secondary index lookups match the model;
- the integrity checker finds nothing wrong: every page belongs to exactly one structure, every row decodes, every index has exactly one correct entry per row.

Then it starts the worker again on the same file and repeats.

What it does not prove: killing a process leaves the operating system's file cache intact, so writes the process made before dying still reach the disk. It tests "the process crashed" thoroughly. It does not test "the power went out", which would also need the drive to honour `FlushFileBuffers`. The torn-log unit tests stand in for that case.

## 4. Ten interview questions and answers

**1. Why a write-ahead log instead of writing pages straight to the data file at commit?**
Two reasons. Atomicity: a transaction can touch many pages, and a crash halfway through writing them in place would leave the file half old, half new. With a log, the commit point is a single append of a COMMIT record, and recovery either sees it or not. Speed: the log is one sequential append and one fsync per commit, while in-place writes would be random writes with an fsync each. The data file is updated later, in bulk, at checkpoints.

**2. What exactly happens if the process dies in the middle of COMMIT?**
If it dies before the COMMIT record is fully on disk, that record is missing or fails its checksum, so recovery throws the whole batch away and the transaction never happened. The caller was never told it succeeded, because `COMMIT` returns only after the fsync. If it dies after the fsync, recovery replays the batch and the transaction is there, even if the caller never saw the reply. The harness counts these cases and allows exactly one such transaction past the last acknowledged one.

**3. Why does recovery not need an undo phase?**
Because of the no-steal rule: pages changed by a running transaction are pinned in memory and can never be written to the data file. So the data file only ever contains committed data, and recovery only has to redo. Most production engines allow stealing (writing uncommitted pages when memory is tight). InnoDB therefore keeps undo logs to roll those changes back; PostgreSQL avoids undo differently, by never overwriting rows in place and marking aborted transactions so their row versions stay invisible. The cost of no-steal is that a transaction's changed pages must fit in memory.

**4. How does a B+tree insert split work here?**
Find the leaf by walking down, remembering the path. If the new cell fits, done. If not, gather the leaf's cells plus the new one, keep the lower half in the leaf, move the upper half to a new page, link the new page into the leaf chain, and push the first key of the new page up to the parent as a separator. If the parent is full, it splits the same way, except that the middle key moves up instead of being copied. If the root splits, its contents move to a new page and the root becomes an internal node over the two halves, so the root's page id never changes.

**5. Deletes do not merge B+tree nodes. Is the tree still correct?**
Yes. Searches stay correct because separator keys remain valid boundaries even when the keys they once copied are gone. Leaves can become sparse or empty, which wastes space and makes scans walk more pages, but never gives wrong answers. The integrity checker verifies the ordering and bounds after random inserts and deletes, and a randomized test checks the tree against `SortedDictionary` after every batch. Merging would be the fix for space use. It is listed as a limitation.

**6. How do you store several index entries with the same value?**
The secondary index key is `encode(value)` followed by the 6-byte RowId. Every key is unique, and all entries for a value sit next to each other in key order. Looking up `value = v` is a range scan from `encode(v)` to `encode(v)` followed by six `FF` bytes. This only works because the encoding is self-delimiting (text ends with a terminator), so no value's encoding is a prefix of another's.

**7. How does the planner decide between an index and a full scan?**
Rules, not costs. It looks at the top-level `AND` terms of `WHERE` for `column op constant` on an indexed column. Equality beats ranges, the primary key beats a secondary index, and a two-sided range beats a one-sided one. If nothing matches, or the condition uses `OR`, it scans. It then re-applies the whole `WHERE` as a filter, so the index only has to narrow things down. A cost-based planner would keep statistics (row counts, value distributions) and estimate which plan reads fewer pages. For example, it might choose a full scan when an index would match 90% of the table.

**8. What is your isolation level, and what anomalies are possible?**
Serializable, because there is only ever one writer and readers cannot overlap with it, so no dirty reads, non-repeatable reads, phantoms or lost updates. The trade-off is throughput: a long write transaction blocks all readers. With MVCC, readers would see a snapshot and never wait, but the engine would need row versions and garbage collection.

**9. How do you know the crash test is actually testing something?**
The checks are specific: exact table contents against a model, a conservation invariant, index-versus-model lookups, and a full structural check, after every kill. The harness kills at random times with a tiny cache and a 256 KB checkpoint threshold, so kills also land during evictions and checkpoints. In some cycles it kills a second, freshly started worker while it is still recovering the log the first one left behind. The model allows exactly one unacknowledged commit, so a lost commit or a leaked uncommitted transaction fails the run. During development the test suite did catch real bugs: readers starving behind back-to-back writers, the cache never shrinking after a large transaction, and the integrity checker crashing on a corrupted slot instead of reporting it. A later review found one the crash test could not have caught, because it only kills processes and always fsyncs: with fsync turned off, pages could reach the data file before their log records (see "The write-ahead rule when fsync is turned off" above).

**10. What would you build next?**
In order: a free-page list plus B+tree merges, so space is reused and the file stops only growing; overflow pages for rows and keys larger than a page; MVCC so readers do not wait for writers; a cost-based planner with table statistics; and checksums on data pages, not just log records, so a bad disk sector is detected on read instead of during an integrity check.
