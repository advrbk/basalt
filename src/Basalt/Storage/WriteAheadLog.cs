using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Basalt.Storage;

/// <summary>What recovery found in the WAL.</summary>
public sealed record RecoveryInfo(int CommittedTransactions, int PagesRedone, long DiscardedBytes, bool HadIncompleteTail);

/// <summary>
/// Redo-only write-ahead log of full page images.
///
/// File layout:
/// <code>
/// header (32 bytes): magic "BWAL" | version | salt (8) | reserved | header CRC
/// record:            type (1) | page id (4) | txn id (8) | payload length (4) | payload | CRC (4)
/// </code>
/// A transaction is appended as one batch: a PAGE record per page it changed followed by one
/// COMMIT record, then the file is fsynced. The CRC of each record covers the salt plus the
/// record bytes, so a torn or stale record is detected and recovery stops there.
/// The salt changes every time the log is reset at a checkpoint.
/// </summary>
internal sealed class WriteAheadLog : IDisposable
{
    private const uint Magic = 0x4C415742; // "BWAL" little endian
    private const int Version = 1;
    public const int HeaderSize = 32;
    private const int RecordHeaderSize = 1 + 4 + 8 + 4;
    private const int RecordTrailerSize = 4;
    private const byte PageRecord = 1;
    private const byte CommitRecord = 2;

    private readonly FileStream _file;
    private readonly SafeFileHandle _handle;
    private ulong _salt;
    private long _length;
    private bool _unsynced; // appended without fsync (SyncOnCommit = false)

    public string Path { get; }
    public long Length => _length;
    public long BytesWritten { get; private set; }
    public long Syncs { get; private set; }

    public WriteAheadLog(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 0, FileOptions.SequentialScan);
        _handle = _file.SafeFileHandle;
        _length = RandomAccess.GetLength(_handle);
    }

    public static int BatchSize(int pageCount) =>
        pageCount * (RecordHeaderSize + Pager.PageSize + RecordTrailerSize) + RecordHeaderSize + RecordTrailerSize;

    /// <summary>
    /// Scans the log and hands every page image of every committed transaction, in log order,
    /// to <paramref name="apply"/>. Pages of a transaction whose COMMIT record is missing or
    /// damaged are thrown away. Returns what was found; the caller then resets the log.
    /// </summary>
    public RecoveryInfo Recover(Action<int, byte[]> apply, out long maxTxnId)
    {
        maxTxnId = 0;
        long fileLength = RandomAccess.GetLength(_handle);
        if (fileLength < HeaderSize) return new RecoveryInfo(0, 0, fileLength, fileLength > 0);

        var header = new byte[HeaderSize];
        ReadExactly(0, header);
        if (!TryParseHeader(header, out ulong salt))
            return new RecoveryInfo(0, 0, fileLength, true);

        int committed = 0, redone = 0;
        long pos = HeaderSize;
        long lastGood = HeaderSize;
        var pending = new List<(int PageId, byte[] Image)>();
        long pendingTxn = -1;
        var recHeader = new byte[RecordHeaderSize];
        var trailer = new byte[RecordTrailerSize];
        Span<byte> saltBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(saltBytes, salt);

        while (pos + RecordHeaderSize + RecordTrailerSize <= fileLength)
        {
            ReadExactly(pos, recHeader);
            byte type = recHeader[0];
            int pageId = BinaryPrimitives.ReadInt32LittleEndian(recHeader.AsSpan(1));
            long txnId = BinaryPrimitives.ReadInt64LittleEndian(recHeader.AsSpan(5));
            int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(recHeader.AsSpan(13));

            bool shapeOk = type == PageRecord ? payloadLen == Pager.PageSize && pageId >= 0
                         : type == CommitRecord && payloadLen == 0;
            if (!shapeOk || pos + RecordHeaderSize + payloadLen + RecordTrailerSize > fileLength) break;

            var payload = new byte[payloadLen];
            if (payloadLen > 0) ReadExactly(pos + RecordHeaderSize, payload);
            ReadExactly(pos + RecordHeaderSize + payloadLen, trailer);

            uint crc = Crc32.Append(Crc32.Append(Crc32.Compute(saltBytes), recHeader), payload);
            if (crc != BinaryPrimitives.ReadUInt32LittleEndian(trailer)) break;

            pos += RecordHeaderSize + payloadLen + RecordTrailerSize;

            if (pendingTxn != txnId)
            {
                pending.Clear(); // a new batch started; anything unfinished before it is dropped
                pendingTxn = txnId;
            }

            if (type == PageRecord)
            {
                pending.Add((pageId, payload));
            }
            else
            {
                foreach (var (id, image) in pending) apply(id, image);
                redone += pending.Count;
                committed++;
                maxTxnId = Math.Max(maxTxnId, txnId);
                pending.Clear();
                pendingTxn = -1;
                lastGood = pos;
            }
        }

        long discarded = fileLength - lastGood;
        return new RecoveryInfo(committed, redone, discarded, discarded > 0);
    }

    /// <summary>
    /// Appends one transaction (its page images plus a COMMIT record) and, when
    /// <paramref name="sync"/> is true, fsyncs before returning. When this returns the commit is durable.
    /// </summary>
    public void AppendCommit(long txnId, IReadOnlyList<(int PageId, byte[] Image)> pages, bool sync)
    {
        if (_length < HeaderSize) Reset();

        var buffer = new byte[BatchSize(pages.Count)];
        Span<byte> saltBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(saltBytes, _salt);
        uint saltCrc = Crc32.Compute(saltBytes);

        int off = 0;
        foreach (var (pageId, image) in pages)
            off = WriteRecord(buffer, off, PageRecord, pageId, txnId, image, saltCrc);
        off = WriteRecord(buffer, off, CommitRecord, 0, txnId, ReadOnlySpan<byte>.Empty, saltCrc);

        long start = _length;
        try
        {
            RandomAccess.Write(_handle, buffer, start);
            if (sync)
            {
                _file.Flush(flushToDisk: true);
                Syncs++;
            }
            else
            {
                _unsynced = true;
            }
        }
        catch
        {
            // Cut off the half-written batch so later commits are not hidden behind it.
            try { RandomAccess.SetLength(_handle, start); } catch { /* caller poisons the database */ }
            throw;
        }
        _length = start + off;
        BytesWritten += off;
    }

    /// <summary>
    /// The write-ahead rule: a page may only reach the data file after the log records that
    /// describe it are on disk. With fsync on every commit that is always true; with
    /// SyncOnCommit off, the buffer pool calls this before it writes any dirty page back.
    /// </summary>
    public void SyncIfNeeded()
    {
        if (!_unsynced) return;
        _file.Flush(flushToDisk: true);
        Syncs++;
        _unsynced = false;
    }

    /// <summary>Empties the log and starts a new generation with a fresh salt. Called after a checkpoint.</summary>
    public void Reset()
    {
        _salt = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), _salt);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), Crc32.Compute(header.AsSpan(0, 28)));

        RandomAccess.SetLength(_handle, 0);
        _length = 0; // if writing the header fails, the next append starts with a fresh Reset
        RandomAccess.Write(_handle, header, 0);
        _file.Flush(flushToDisk: true);
        Syncs++;
        _unsynced = false;
        _length = HeaderSize;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> header, out ulong salt)
    {
        salt = 0;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4)) != Version) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(28)) != Crc32.Compute(header.Slice(0, 28))) return false;
        salt = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8));
        return true;
    }

    private static int WriteRecord(byte[] buffer, int off, byte type, int pageId, long txnId,
        ReadOnlySpan<byte> payload, uint saltCrc)
    {
        var span = buffer.AsSpan(off);
        span[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(1), pageId);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(5), txnId);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(13), payload.Length);
        payload.CopyTo(span.Slice(RecordHeaderSize));
        uint crc = Crc32.Append(saltCrc, span.Slice(0, RecordHeaderSize + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(RecordHeaderSize + payload.Length), crc);
        return off + RecordHeaderSize + payload.Length + RecordTrailerSize;
    }

    private void ReadExactly(long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(_handle, buffer.Slice(total), offset + total);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }
    }

    public void Dispose() => _file.Dispose();
}
