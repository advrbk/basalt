using Basalt.Values;

namespace Basalt.Sql;

// ---- expressions ----------------------------------------------------------------------
// ToString() prints a canonical SQL form. The planner uses it to match GROUP BY expressions
// with select-list expressions, and EXPLAIN uses it to show predicates.

public abstract record Expr;

public sealed record LiteralExpr(Value Value) : Expr
{
    public override string ToString() => Value.ToSqlLiteral();
}

public sealed record ParameterExpr(int Index) : Expr
{
    public override string ToString() => $"?{Index + 1}";
}

public sealed record ColumnExpr(string? Table, string Column) : Expr
{
    public override string ToString() => Table == null ? Column : $"{Table}.{Column}";
}

public sealed record UnaryExpr(string Op, Expr Operand) : Expr
{
    public override string ToString() => Op == "NOT" ? $"NOT {Operand}" : $"{Op}{Operand}";
}

public sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr
{
    public override string ToString() => $"({Left} {Op} {Right})";
}

public sealed record IsNullExpr(Expr Operand, bool Negated) : Expr
{
    public override string ToString() => Negated ? $"({Operand} IS NOT NULL)" : $"({Operand} IS NULL)";
}

public sealed record LikeExpr(Expr Operand, Expr Pattern, bool Negated) : Expr
{
    public override string ToString() => $"({Operand} {(Negated ? "NOT LIKE" : "LIKE")} {Pattern})";
}

/// <summary>Aggregate call: COUNT(*), COUNT(x), SUM, AVG, MIN, MAX.</summary>
public sealed record AggregateExpr(string Function, Expr? Argument) : Expr
{
    public override string ToString() => $"{Function}({(Argument == null ? "*" : Argument.ToString())})";
}

/// <summary>Scalar function call: ROUND, ABS, LENGTH, LOWER, UPPER, COALESCE.</summary>
public sealed record FunctionExpr(string Function, IReadOnlyList<Expr> Arguments) : Expr
{
    public override string ToString() => $"{Function}({string.Join(", ", Arguments)})";
}

// ---- statements -----------------------------------------------------------------------

public abstract record Statement;

public sealed record ColumnDef(string Name, DataType Type, bool PrimaryKey, bool NotNull);

public sealed record CreateTableStmt(string Table, IReadOnlyList<ColumnDef> Columns, bool IfNotExists) : Statement;

public sealed record CreateIndexStmt(string Index, string Table, string Column, bool IfNotExists) : Statement;

public sealed record InsertStmt(string Table, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<Expr>> Rows) : Statement;

public sealed record SelectItem(Expr? Expr, string? Alias, string? StarTable)
{
    public bool IsStar => Expr == null;
}

public sealed record TableRef(string Table, string? Alias)
{
    public string Name => Alias ?? Table;
}

public sealed record JoinClause(TableRef Table, Expr On);

public sealed record OrderItem(Expr Expr, bool Descending);

public sealed record SelectStmt(
    IReadOnlyList<SelectItem> Items,
    TableRef? From,
    IReadOnlyList<JoinClause> Joins,
    Expr? Where,
    IReadOnlyList<Expr> GroupBy,
    Expr? Having,
    IReadOnlyList<OrderItem> OrderBy,
    Expr? Limit,
    Expr? Offset) : Statement;

public sealed record Assignment(string Column, Expr Value);

public sealed record UpdateStmt(string Table, IReadOnlyList<Assignment> Assignments, Expr? Where) : Statement;

public sealed record DeleteStmt(string Table, Expr? Where) : Statement;

public sealed record BeginStmt : Statement;
public sealed record CommitStmt : Statement;
public sealed record RollbackStmt : Statement;

public sealed record ExplainStmt(Statement Inner) : Statement;
