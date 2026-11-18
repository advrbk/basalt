using Basalt.Values;

namespace Basalt.Tests;

/// <summary>A scratch directory under the test output folder, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a file may still be closing; leave it */ }
    }
}

internal static class ResultExtensions
{
    /// <summary>Rows as plain CLR values, handy for Assert.Equal.</summary>
    public static List<object?[]> Plain(this QueryResult r) => r.Rows.Select(row => row.Select(v => v.ToObject()).ToArray()).ToList();

    public static long Long(this QueryResult r) => r.Scalar().AsInt;

    public static List<long> Longs(this QueryResult r, int column = 0) => r.Rows.Select(x => x[column].AsInt).ToList();

    public static List<string> Strings(this QueryResult r, int column = 0) => r.Rows.Select(x => x[column].ToString()).ToList();
}
