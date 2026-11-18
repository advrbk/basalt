namespace Basalt;

/// <summary>Any error reported to a caller: bad SQL, constraint violations, type errors, I/O problems.</summary>
public class BasaltException : Exception
{
    public BasaltException(string message) : base(message) { }
    public BasaltException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A constraint (NOT NULL, PRIMARY KEY uniqueness, type) was violated.</summary>
public sealed class ConstraintException : BasaltException
{
    public ConstraintException(string message) : base(message) { }
}

/// <summary>The SQL text could not be tokenized or parsed.</summary>
public sealed class SqlSyntaxException : BasaltException
{
    public int Line { get; }
    public int Column { get; }

    public SqlSyntaxException(string message, int line, int column)
        : base($"syntax error at line {line}, column {column}: {message}")
    {
        Line = line;
        Column = column;
    }
}

/// <summary>The on-disk data failed a structural check (bad magic, checksum, broken page).</summary>
public sealed class CorruptionException : BasaltException
{
    public CorruptionException(string message) : base(message) { }
}
