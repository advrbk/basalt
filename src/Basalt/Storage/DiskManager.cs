using Microsoft.Win32.SafeHandles;

namespace Basalt.Storage;

/// <summary>
/// Reads and writes whole pages of the data file. No caching here; that is the buffer pool's job.
/// The file is opened with FileShare.None so a second process cannot open the same database.
/// </summary>
internal sealed class DiskManager : IDisposable
{
    private readonly FileStream _file;
    private readonly SafeFileHandle _handle;

    public string Path { get; }
    public long PageReads { get; private set; }
    public long PageWrites { get; private set; }

    public DiskManager(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 0, FileOptions.RandomAccess);
        _handle = _file.SafeFileHandle;
    }

    public long Length => RandomAccess.GetLength(_handle);

    /// <summary>Reads page <paramref name="pageId"/>. Pages past the end of the file read as zeros.</summary>
    public void Read(int pageId, Span<byte> buffer)
    {
        long offset = (long)pageId * Pager.PageSize;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(_handle, buffer.Slice(total), offset + total);
            if (n == 0) break;
            total += n;
        }
        if (total < buffer.Length) buffer.Slice(total).Clear();
        PageReads++;
    }

    public void Write(int pageId, ReadOnlySpan<byte> data)
    {
        RandomAccess.Write(_handle, data, (long)pageId * Pager.PageSize);
        PageWrites++;
    }

    /// <summary>Forces written pages to stable storage (FlushFileBuffers on Windows, fsync elsewhere).</summary>
    public void Sync() => _file.Flush(flushToDisk: true);

    public void Dispose() => _file.Dispose();
}
