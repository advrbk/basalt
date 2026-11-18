# Basalt

Basalt is a small relational database written from scratch in C# (.NET 10), with no database libraries. It stores tables in a single file of 4 KB pages, caches pages in an LRU buffer pool, keeps rows in slotted pages, and indexes them with B+trees. Every commit goes through a checksummed write-ahead log that is fsynced before `COMMIT` returns, and crash recovery replays it on startup. SQL goes through a hand-written tokenizer and recursive-descent parser into a simple rule-based planner, and there is a REPL and an embedded C# API. Only a subset of SQL is supported. The main test is a crash harness: a separate process commits transactions while the harness kills it at random moments, hundreds of times, and after every kill it checks that no acknowledged commit was lost and nothing uncommitted appeared.

## Features

- Storage is one data file of 4 KB pages. A buffer pool handles LRU eviction, dirty-page tracking and pinning, slotted heap pages hold variable-length rows, and the catalog is stored in pages as well.
- Indexes are B+trees with node splits up to the root, linked leaves for range scans, and an order-preserving key encoding, so all comparisons are byte comparisons. Each table has a primary key index, and `CREATE INDEX` works on any column.
- The WAL is redo-only and holds full page images, with CRC-32 per record and a per-generation salt. It is fsynced on commit, and always before a changed page is written back to the data file ("log before data"). Checkpoints run automatically or manually, and recovery redoes committed transactions and drops incomplete ones.
- Transactions support `BEGIN` / `COMMIT` / `ROLLBACK`, autocommit and statement-level rollback inside a transaction. A fair database-wide readers-writer lock allows one writer or many readers, which keeps execution serializable.
- The SQL covers `CREATE TABLE` (INT, TEXT, REAL, BOOL; PRIMARY KEY; NOT NULL), `CREATE INDEX`, `INSERT` (multi-row, `?` parameters), `SELECT` with `WHERE` (AND/OR/NOT, comparisons, `IS NULL`, `LIKE`, `BETWEEN`, `IN`), arithmetic and `||`, `ROUND/ABS/LENGTH/LOWER/UPPER/COALESCE`, `ORDER BY`, `LIMIT/OFFSET`, `COUNT/SUM/AVG/MIN/MAX` with `GROUP BY` and `HAVING`, inner `JOIN ... ON`, `UPDATE`, `DELETE`, and `EXPLAIN`.
- The planner picks an index lookup for `col = const`, an index range scan for `<, <=, >, >=` and `BETWEEN`, and a full scan otherwise. For joins it uses an index nested-loop join when the join column is indexed, otherwise a hash join (or a nested loop when there is no equality).
- The repo also has a REPL with ASCII tables and timing, an integrity checker (`.check`), a scripted demo, the crash harness and a benchmark with an optional SQLite comparison.

## Architecture

```
            SQL text
               |
     Lexer -> Parser (recursive descent) -> AST
               |
     BasaltSession: autocommit / BEGIN..COMMIT, readers-writer lock, statement savepoints
               |
     Planner -> operator tree -> iterators (scan, index lookup/range, filter,
               |                 index join, hash join, aggregate, sort, limit, project)
               |
   +-----------+--------------------+-------------------------+
   |  SchemaCatalog                 |  TableHeap              |  BTree
   |  (schema in catalog pages)     |  (slotted heap pages)   |  (primary + secondary indexes)
   +-----------+--------------------+-------------------------+
               |
            Pager: Read / Write / Allocate pages, before-images, commit, rollback, checkpoint
               |                                         |
          BufferPool (LRU, dirty, pinned)          WriteAheadLog (page images, CRC, fsync)
               |                                         |
          data file: name.db                       log file: name.db-wal
```

While a transaction runs, its changed pages stay pinned in memory, so they are never written to the data file uncommitted. `COMMIT` appends a full image of each changed page plus a COMMIT record to the WAL and fsyncs it. Pages reach the data file later, on eviction or at a checkpoint, and the WAL is emptied only after the data file has been fsynced. See [docs/HOW_IT_WORKS.md](docs/HOW_IT_WORKS.md) for the full explanation.

```
src/Basalt/            the engine (Storage, Index, Catalog, Values, Sql, Query, Engine)
src/Basalt.Cli/        REPL and demo
src/Basalt.CrashTest/  kill-and-verify harness + the worker process it kills
bench/Basalt.Bench/    Stopwatch benchmarks (SQLite via Microsoft.Data.Sqlite, for comparison only)
tests/Basalt.Tests/    xUnit tests
docs/HOW_IT_WORKS.md   design walkthrough and interview Q&A
```

## Quick start

Requires the .NET 10 SDK.

```
dotnet run -c Release --project src/Basalt.Cli -- demo
```

The demo creates `demo-data/demo.db`, loads 52,000 rows, runs lookups, shows `EXPLAIN` switching from a full scan to an index after `CREATE INDEX`, runs a join with `GROUP BY`, rolls back a transaction, then stops the engine without a checkpoint (dropping its cache, like a killed process), reopens it and shows recovery.

Interactive shell:

```
dotnet run --project src/Basalt.Cli -- my.db
basalt> CREATE TABLE users (id INT PRIMARY KEY, name TEXT NOT NULL, age INT);
basalt> INSERT INTO users VALUES (1, 'ada', 36), (2, 'linus', 28);
basalt> SELECT name FROM users WHERE age > 30;
basalt> EXPLAIN SELECT * FROM users WHERE id = 2;
basalt> .help
```

You can also pass a statement with `dotnet run --project src/Basalt.Cli -- my.db -c "SELECT COUNT(*) FROM users"`, or pipe a script into it.

Embedded API:

```csharp
using Basalt;

using var db = BasaltDatabase.Open("shop.db");
db.Execute("CREATE TABLE IF NOT EXISTS items (id INT PRIMARY KEY, name TEXT NOT NULL, price REAL)");

db.Execute("BEGIN");
var insert = db.Prepare("INSERT INTO items VALUES (?, ?, ?)");
for (int i = 0; i < 1000; i++) insert.Execute(i, $"item {i}", i * 1.5);
db.Execute("COMMIT");

foreach (var row in db.Execute("SELECT name, price FROM items WHERE id = ?", 42).Rows)
    Console.WriteLine($"{row[0].AsText} costs {row[1].AsReal}");

// One session per thread for concurrent use:
using var session = db.OpenSession();
long n = session.Execute("SELECT COUNT(*) FROM items").Scalar().AsInt;
```

## Tests, crash test and benchmarks

```
dotnet test                                                        # 69 xUnit tests, 10-35 s depending on machine load
dotnet run -c Release --project src/Basalt.CrashTest -- --cycles 500
dotnet run -c Release --project bench/Basalt.Bench                 # add --no-sqlite to skip SQLite
```

The test suite covers the tokenizer and parser, slotted pages, the buffer pool, the B+tree (including randomized inserts and deletes checked against `SortedDictionary` after every batch with a 32-page cache), key encoding order, WAL recovery (crash before checkpoint, uncommitted work discarded, the WAL cut at 28 points, a corrupted record, recovery run twice, the WAL fsynced before pages reach the data file when commit fsync is turned off), transactions and rollback (including DDL and statement-level rollback), lock fairness and concurrent readers during transfers, query results for every SQL feature, join strategies agreeing with each other, index and full-scan results agreeing, integrity-check failure detection, and a 12-cycle run of the real crash harness (half of its cycles also kill a freshly restarted worker).

## Measured results

All numbers below were measured on one Windows 11 machine (8 logical CPUs, SSD, .NET 10.0.12, Release build) on 2026-11-18. Other jobs were running on the machine at the same time, so treat them as rough and expect differences between runs.

### Crash test

Command: `dotnet run -c Release --project src/Basalt.CrashTest -- --cycles 500` (seed 12345, kill 20-400 ms after the worker is ready, worker cache 64 pages, checkpoint every 256 KB of WAL). In about 10% of cycles the harness then starts a second worker and kills it blindly 0-150 ms after start, while it is still starting up and recovering the committed work the first kill left in the WAL.

| | Final build | Build before review fixes |
|---|---:|---:|
| Kill cycles | 500 | 500 |
| Worker processes killed | 547 | 500 |
| Second kills right after a restart (all before the worker had finished opening the database) | 47 | (not in this build) |
| Acknowledged commits checked | 31,015 | 18,077 |
| Commits durable but killed before the ACK was printed | 179 | 135 |
| Reopens where recovery replayed committed work | 308 | 243 |
| Rows in the log table at the end | 82,922 | 48,389 |
| Elapsed | 484 s | 399 s |
| **Acknowledged commits lost** | **0** | **0** |
| Uncommitted transactions visible / integrity problems | 0 / 0 | 0 / 0 |

Both runs used the same seed; how many transactions fit in each kill window depends on machine load. After every kill the harness compares every table with an in-memory model and runs the full integrity check (see [docs/HOW_IT_WORKS.md](docs/HOW_IT_WORKS.md#the-crash-harness)).

### Benchmarks

Command: `dotnet run -c Release --project bench/Basalt.Bench` (100,000 rows of `(id INT PRIMARY KEY, code INT, category INT, name TEXT, price REAL)`, prepared statements with parameters, fsync on every commit for both engines; SQLite in WAL mode with `synchronous=FULL` through Microsoft.Data.Sqlite 10.0.0). Two runs back to back:

| Workload | Basalt run 1 | Basalt run 2 | SQLite run 1 | SQLite run 2 |
|---|---:|---:|---:|---:|
| Insert 100,000 rows in one transaction | 123,733 rows/s | 105,593 rows/s | 248,754 rows/s | 254,858 rows/s |
| Autocommit inserts (one fsync each) | 2,392/s | 2,382/s | 1,943/s | 1,604/s |
| Point lookup by primary key | 74,048/s | 63,930/s | 71,829/s | 61,214/s |
| Point lookup on an unindexed column (full scan) | 49/s | 42/s | 97/s | 82/s |
| `CREATE INDEX` on 100,000 rows | 171 ms | 270 ms | 140 ms | 122 ms |
| Point lookup on that column after `CREATE INDEX` | 177,728/s | 114,921/s | 48,161/s | 49,624/s |
| Range scan of 100 rows by primary key | 14,325 scans/s | 15,797 scans/s | 13,715 scans/s | 13,773 scans/s |
| Full scan + `GROUP BY` over 100,000 rows | 42 ms | 35 ms | 53 ms | 55 ms |

How to read this:

- An index turns a 100,000-row lookup from under 50 per second (a full scan each time) into tens of thousands per second.
- Commit rate is set by fsync: both engines do roughly 1,600-2,400 durable commits per second on this disk.
- The SQLite columns are there for context. For tiny queries it mostly measures the ADO.NET driver's per-call overhead (Basalt is called in-process with no driver layer), which is why SQLite's point lookups look no faster than Basalt's. SQLite's engine itself is far more mature. Where real work dominates (bulk insert, building an index, scanning without an index), SQLite is about 1.2 to 2.4 times faster.
- Basalt's secondary-index lookups measured faster than its primary-key lookups. That loop runs later in the benchmark, so it probably benefits from JIT warm-up; it is not a property of the index. Treat the two lookup rows as the same order of magnitude.
- Basalt's small-transaction write amplification is visible in the WAL: each commit writes full 4 KB images of every page it touched.

## Limitations

- Concurrency is coarse. There is one writer at a time, readers wait while a write transaction is open, and there is no MVCC.
- `SyncOnCommit = false` trades durability for speed. Commits then return before the WAL is fsynced, so a power cut can lose the most recent commits. The WAL is still fsynced before any page is written to the data file, so the file stays consistent.
- The kill test covers process crashes, not power loss. Killing a process leaves the OS file cache intact. Unit tests cut and damage the WAL to cover torn and corrupted log tails, but nothing here pulls the power, and durability also depends on the drive honouring `FlushFileBuffers`.
- Space is not reclaimed. There is no free-page list: deleted rows free space inside their page, but new rows only go to a table's last page, B+tree nodes are never merged, and the file never shrinks. There is no `DROP TABLE`, `DROP INDEX` or `VACUUM`.
- A row must fit in one page (about 4 KB) and an index key in 512 bytes, so long TEXT values cannot be indexed. There are no overflow pages.
- The WAL stores full 4 KB page images, so even a one-row insert writes about three pages to the log. A transaction's changed pages (plus their before-images) must fit in memory.
- The planner is rule based and has no statistics. `OR` conditions fall back to a full scan, join order is the order written, a joined table uses an index for its own `WHERE` terms only when it is hash-joined, `ORDER BY` always sorts in memory, and there are no index-only scans.
- Missing SQL: subqueries, outer joins, `DISTINCT`, `ALTER TABLE`, multi-column keys or indexes, `UNIQUE` on non-key columns, foreign keys, and date types.
- Only the log has checksums. Data pages have none. Damage to the data file is found by the integrity checker or when a row fails to decode, not on every read.
- Tested only on Windows 11 with .NET 10.

## License

MIT, see [LICENSE](LICENSE).
