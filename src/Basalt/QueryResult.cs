using Basalt.Values;

namespace Basalt;

/// <summary>Result of one statement: rows for SELECT/EXPLAIN, a message and count for everything else.</summary>
public sealed class QueryResult
{
    private QueryResult(IReadOnlyList<string> columns, IReadOnlyList<Value[]> rows, int rowsAffected, string? message)
    {
        Columns = columns;
        Rows = rows;
        RowsAffected = rowsAffected;
        Message = message;
    }

    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<Value[]> Rows { get; }
    public int RowsAffected { get; }
    public string? Message { get; }
    public bool HasRows => Columns.Count > 0;

    internal static QueryResult ForRows(IReadOnlyList<string> columns, IReadOnlyList<Value[]> rows) => new(columns, rows, 0, null);
    internal static QueryResult ForMessage(string message, int affected) => new([], [], affected, message);

    /// <summary>The single value of a one-row, one-column result (e.g. SELECT COUNT(*)).</summary>
    public Value Scalar()
    {
        if (Rows.Count != 1 || Columns.Count != 1) throw new BasaltException($"expected one value, got {Rows.Count} rows x {Columns.Count} columns");
        return Rows[0][0];
    }

    public int ColumnIndex(string name)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new BasaltException($"result has no column {name}");
    }
}
