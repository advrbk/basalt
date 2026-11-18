using System.Text;
using Basalt.Sql;
using Basalt.Values;

namespace Basalt.Query;

/// <summary>One column of an operator's output: which table (alias) it came from, its name and type.</summary>
internal sealed record ColumnBinding(string? Table, string Name, DataType? Type);

/// <summary>The shape of the rows an operator produces.</summary>
internal sealed class RowSchema
{
    public RowSchema(IReadOnlyList<ColumnBinding> columns) => Columns = columns;

    public IReadOnlyList<ColumnBinding> Columns { get; }
    public int Count => Columns.Count;

    public static RowSchema Concat(RowSchema a, RowSchema b) => new(a.Columns.Concat(b.Columns).ToList());

    /// <summary>Finds a column by (optional) table and name. Throws on unknown or ambiguous names.</summary>
    public int Resolve(ColumnExpr c)
    {
        int found = -1;
        for (int i = 0; i < Columns.Count; i++)
        {
            var col = Columns[i];
            if (!string.Equals(col.Name, c.Column, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Table != null && !string.Equals(col.Table, c.Table, StringComparison.OrdinalIgnoreCase)) continue;
            if (found >= 0) throw new BasaltException($"column name {c} is ambiguous; qualify it with a table name");
            found = i;
        }
        if (found < 0)
        {
            if (c.Table != null && !Columns.Any(x => string.Equals(x.Table, c.Table, StringComparison.OrdinalIgnoreCase)))
                throw new BasaltException($"no such table or alias: {c.Table}");
            throw new BasaltException($"no such column: {c}");
        }
        return found;
    }

    public bool TryResolve(ColumnExpr c, out int ordinal)
    {
        try
        {
            ordinal = Resolve(c);
            return true;
        }
        catch (BasaltException)
        {
            ordinal = -1;
            return false;
        }
    }

    /// <summary>True if every column the expression mentions belongs to this schema.</summary>
    public bool Covers(Expr e) => e switch
    {
        ColumnExpr c => TryResolve(c, out _),
        UnaryExpr u => Covers(u.Operand),
        BinaryExpr b => Covers(b.Left) && Covers(b.Right),
        IsNullExpr n => Covers(n.Operand),
        LikeExpr l => Covers(l.Operand) && Covers(l.Pattern),
        FunctionExpr f => f.Arguments.All(Covers),
        AggregateExpr => false,
        _ => true,
    };
}

/// <summary>An expression with column names resolved to positions, ready to evaluate against a row.</summary>
internal abstract class BoundExpr
{
    public abstract Value Eval(Value[] row);
}

internal sealed class ConstExpr(Value value) : BoundExpr
{
    public Value Value { get; } = value;
    public override Value Eval(Value[] row) => Value;
}

internal sealed class ColumnRefExpr(int ordinal) : BoundExpr
{
    public int Ordinal { get; } = ordinal;
    public override Value Eval(Value[] row) => row[Ordinal];
}

internal sealed class UnaryOpExpr(string op, BoundExpr operand) : BoundExpr
{
    public override Value Eval(Value[] row)
    {
        var v = operand.Eval(row);
        if (v.IsNull) return Value.Null;
        if (op == "NOT")
        {
            if (v.Kind != ValueKind.Bool) throw new BasaltException($"NOT needs a BOOL, got {Value.KindName(v.Kind)}");
            return Value.FromBool(!v.AsBool);
        }
        return v.Kind switch
        {
            ValueKind.Int => v.AsInt == long.MinValue ? throw new BasaltException("integer overflow") : Value.FromInt(-v.AsInt),
            ValueKind.Real => Value.FromReal(-v.AsReal),
            _ => throw new BasaltException($"cannot negate a {Value.KindName(v.Kind)}"),
        };
    }
}

internal sealed class IsNullTestExpr(BoundExpr operand, bool negated) : BoundExpr
{
    public override Value Eval(Value[] row) => Value.FromBool(operand.Eval(row).IsNull != negated);
}

internal sealed class LikeTestExpr(BoundExpr operand, BoundExpr pattern, bool negated) : BoundExpr
{
    public override Value Eval(Value[] row)
    {
        var v = operand.Eval(row);
        var p = pattern.Eval(row);
        if (v.IsNull || p.IsNull) return Value.Null;
        if (v.Kind != ValueKind.Text || p.Kind != ValueKind.Text) throw new BasaltException("LIKE works on TEXT values");
        return Value.FromBool(Like(v.AsText, p.AsText) != negated);
    }

    /// <summary>% matches any run of characters, _ matches one. Case-insensitive for ASCII letters, like SQLite.</summary>
    internal static bool Like(string s, string p)
    {
        int si = 0, pi = 0, starP = -1, starS = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && (p[pi] == '_' || Eq(p[pi], s[si]))) { si++; pi++; }
            else if (pi < p.Length && p[pi] == '%') { starP = pi++; starS = si; }
            else if (starP >= 0) { pi = starP + 1; si = ++starS; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '%') pi++;
        return pi == p.Length;

        static bool Eq(char a, char b) => a == b || (a < 128 && b < 128 && char.ToLowerInvariant(a) == char.ToLowerInvariant(b));
    }
}

internal sealed class BinaryOpExpr(string op, BoundExpr left, BoundExpr right) : BoundExpr
{
    public override Value Eval(Value[] row)
    {
        switch (op)
        {
            case "AND":
            {
                var l = AsLogic(left.Eval(row));
                if (l == false) return Value.False;
                var r = AsLogic(right.Eval(row));
                if (r == false) return Value.False;
                return l == null || r == null ? Value.Null : Value.True;
            }
            case "OR":
            {
                var l = AsLogic(left.Eval(row));
                if (l == true) return Value.True;
                var r = AsLogic(right.Eval(row));
                if (r == true) return Value.True;
                return l == null || r == null ? Value.Null : Value.False;
            }
        }

        var a = left.Eval(row);
        var b = right.Eval(row);
        if (a.IsNull || b.IsNull) return Value.Null;

        switch (op)
        {
            case "=": return Value.FromBool(Compare(a, b) == 0);
            case "<>": return Value.FromBool(Compare(a, b) != 0);
            case "<": return Value.FromBool(Compare(a, b) < 0);
            case "<=": return Value.FromBool(Compare(a, b) <= 0);
            case ">": return Value.FromBool(Compare(a, b) > 0);
            case ">=": return Value.FromBool(Compare(a, b) >= 0);
            case "||": return Value.FromText(a.ToString() + b.ToString());
        }

        if (!a.IsNumeric || !b.IsNumeric)
            throw new BasaltException($"operator {op} needs numbers, got {Value.KindName(a.Kind)} and {Value.KindName(b.Kind)}");

        if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
        {
            long x = a.AsInt, y = b.AsInt;
            try
            {
                return op switch
                {
                    "+" => Value.FromInt(checked(x + y)),
                    "-" => Value.FromInt(checked(x - y)),
                    "*" => Value.FromInt(checked(x * y)),
                    "/" => y == 0 ? throw new BasaltException("division by zero") : Value.FromInt(checked(x / y)),
                    "%" => y == 0 ? throw new BasaltException("division by zero") : Value.FromInt(y == -1 ? 0 : x % y),
                    _ => throw new BasaltException($"unknown operator {op}"),
                };
            }
            catch (OverflowException)
            {
                throw new BasaltException("integer overflow");
            }
        }

        double dx = a.AsReal, dy = b.AsReal;
        return op switch
        {
            "+" => Value.FromReal(dx + dy),
            "-" => Value.FromReal(dx - dy),
            "*" => Value.FromReal(dx * dy),
            "/" => dy == 0 ? throw new BasaltException("division by zero") : Value.FromReal(dx / dy),
            "%" => dy == 0 ? throw new BasaltException("division by zero") : Value.FromReal(dx % dy),
            _ => throw new BasaltException($"unknown operator {op}"),
        };
    }

    private static bool? AsLogic(Value v)
    {
        if (v.IsNull) return null;
        if (v.Kind != ValueKind.Bool) throw new BasaltException($"AND/OR need BOOL operands, got {Value.KindName(v.Kind)}");
        return v.AsBool;
    }

    internal static int Compare(Value a, Value b)
    {
        bool ok = (a.IsNumeric && b.IsNumeric) || a.Kind == b.Kind;
        if (!ok) throw new BasaltException($"cannot compare {Value.KindName(a.Kind)} with {Value.KindName(b.Kind)}");
        return a.CompareTo(b);
    }
}

/// <summary>Built-in scalar functions. NULL in gives NULL out, except COALESCE.</summary>
internal sealed class FunctionCallExpr(string function, BoundExpr[] args) : BoundExpr
{
    public override Value Eval(Value[] row)
    {
        var v = args.Select(a => a.Eval(row)).ToArray();
        if (function == "COALESCE")
        {
            if (v.Length == 0) throw new BasaltException("COALESCE needs at least one argument");
            return v.FirstOrDefault(x => !x.IsNull);
        }
        int expected = function == "ROUND" ? (v.Length is 1 or 2 ? v.Length : -1) : 1;
        if (v.Length != expected) throw new BasaltException($"wrong number of arguments to {function}");
        if (v.Any(x => x.IsNull)) return Value.Null;
        switch (function)
        {
            case "ABS":
                if (v[0].Kind == ValueKind.Int) return v[0].AsInt == long.MinValue ? throw new BasaltException("integer overflow") : Value.FromInt(Math.Abs(v[0].AsInt));
                if (v[0].Kind == ValueKind.Real) return Value.FromReal(Math.Abs(v[0].AsReal));
                break;
            case "ROUND":
                if (!v[0].IsNumeric) break;
                int digits = v.Length == 2 ? (int)Math.Clamp(v[1].AsInt, 0, 15) : 0;
                return Value.FromReal(Math.Round(v[0].AsReal, digits, MidpointRounding.AwayFromZero));
            case "LENGTH":
                if (v[0].Kind == ValueKind.Text) return Value.FromInt(new System.Globalization.StringInfo(v[0].AsText).LengthInTextElements);
                break;
            case "LOWER":
                if (v[0].Kind == ValueKind.Text) return Value.FromText(v[0].AsText.ToLowerInvariant());
                break;
            case "UPPER":
                if (v[0].Kind == ValueKind.Text) return Value.FromText(v[0].AsText.ToUpperInvariant());
                break;
        }
        throw new BasaltException($"{function} cannot take a {Value.KindName(v[0].Kind)} value");
    }
}

/// <summary>Resolves AST expressions against a row schema.</summary>
internal static class Binder
{
    public static BoundExpr Bind(Expr e, RowSchema schema, IReadOnlyList<Value> parameters) => e switch
    {
        LiteralExpr l => new ConstExpr(l.Value),
        ParameterExpr p => new ConstExpr(Param(p, parameters)),
        ColumnExpr c => new ColumnRefExpr(schema.Resolve(c)),
        UnaryExpr u => new UnaryOpExpr(u.Op, Bind(u.Operand, schema, parameters)),
        BinaryExpr b => new BinaryOpExpr(b.Op, Bind(b.Left, schema, parameters), Bind(b.Right, schema, parameters)),
        IsNullExpr n => new IsNullTestExpr(Bind(n.Operand, schema, parameters), n.Negated),
        LikeExpr l => new LikeTestExpr(Bind(l.Operand, schema, parameters), Bind(l.Pattern, schema, parameters), l.Negated),
        FunctionExpr f => new FunctionCallExpr(f.Function, f.Arguments.Select(a => Bind(a, schema, parameters)).ToArray()),
        AggregateExpr a => throw new BasaltException($"aggregate {a} is not allowed here"),
        _ => throw new BasaltException($"unsupported expression {e}"),
    };

    public static Value Param(ParameterExpr p, IReadOnlyList<Value> parameters)
    {
        if (p.Index >= parameters.Count)
            throw new BasaltException($"statement needs at least {p.Index + 1} parameters but {parameters.Count} were given");
        return parameters[p.Index];
    }

    /// <summary>Evaluates an expression that may not reference columns (LIMIT, VALUES, ...).</summary>
    public static Value EvalConstant(Expr e, IReadOnlyList<Value> parameters) =>
        Bind(e, new RowSchema([]), parameters).Eval([]);

    /// <summary>
    /// A string identifying what an expression computes, with columns replaced by their positions,
    /// so "t.category" and "category" compare equal when they mean the same column.
    /// </summary>
    public static string Canonical(Expr e, RowSchema schema)
    {
        var sb = new StringBuilder();
        Write(e);
        return sb.ToString();

        void Write(Expr x)
        {
            switch (x)
            {
                case ColumnExpr c:
                    sb.Append(schema.TryResolve(c, out int i) ? $"#{i}" : c.ToString());
                    break;
                case UnaryExpr u:
                    sb.Append(u.Op).Append('(');
                    Write(u.Operand);
                    sb.Append(')');
                    break;
                case BinaryExpr b:
                    sb.Append('(');
                    Write(b.Left);
                    sb.Append(' ').Append(b.Op).Append(' ');
                    Write(b.Right);
                    sb.Append(')');
                    break;
                case IsNullExpr n:
                    sb.Append('(');
                    Write(n.Operand);
                    sb.Append(n.Negated ? " IS NOT NULL)" : " IS NULL)");
                    break;
                case LikeExpr l:
                    sb.Append('(');
                    Write(l.Operand);
                    sb.Append(l.Negated ? " NOT LIKE " : " LIKE ");
                    Write(l.Pattern);
                    sb.Append(')');
                    break;
                case FunctionExpr f:
                    sb.Append(f.Function).Append('(');
                    for (int ai = 0; ai < f.Arguments.Count; ai++)
                    {
                        if (ai > 0) sb.Append(", ");
                        Write(f.Arguments[ai]);
                    }
                    sb.Append(')');
                    break;
                case AggregateExpr a:
                    sb.Append(a.Function).Append('(');
                    if (a.Argument == null) sb.Append('*');
                    else Write(a.Argument);
                    sb.Append(')');
                    break;
                default:
                    sb.Append(x);
                    break;
            }
        }
    }

    /// <summary>Splits a WHERE clause into its top-level AND terms.</summary>
    public static List<Expr> SplitAnd(Expr? e)
    {
        var list = new List<Expr>();
        void Walk(Expr x)
        {
            if (x is BinaryExpr { Op: "AND" } b)
            {
                Walk(b.Left);
                Walk(b.Right);
            }
            else list.Add(x);
        }
        if (e != null) Walk(e);
        return list;
    }

    public static Expr? JoinAnd(IEnumerable<Expr> terms)
    {
        Expr? result = null;
        foreach (var t in terms) result = result == null ? t : new BinaryExpr("AND", result, t);
        return result;
    }

    public static bool ContainsAggregate(Expr? e) => e switch
    {
        null => false,
        AggregateExpr => true,
        UnaryExpr u => ContainsAggregate(u.Operand),
        BinaryExpr b => ContainsAggregate(b.Left) || ContainsAggregate(b.Right),
        IsNullExpr n => ContainsAggregate(n.Operand),
        LikeExpr l => ContainsAggregate(l.Operand) || ContainsAggregate(l.Pattern),
        FunctionExpr f => f.Arguments.Any(ContainsAggregate),
        _ => false,
    };

    public static void CollectAggregates(Expr? e, List<AggregateExpr> into)
    {
        switch (e)
        {
            case AggregateExpr a:
                if (ContainsAggregate(a.Argument)) throw new BasaltException("aggregates cannot be nested");
                into.Add(a);
                break;
            case UnaryExpr u: CollectAggregates(u.Operand, into); break;
            case BinaryExpr b: CollectAggregates(b.Left, into); CollectAggregates(b.Right, into); break;
            case IsNullExpr n: CollectAggregates(n.Operand, into); break;
            case LikeExpr l: CollectAggregates(l.Operand, into); CollectAggregates(l.Pattern, into); break;
            case FunctionExpr f: foreach (var a in f.Arguments) CollectAggregates(a, into); break;
        }
    }

    /// <summary>Replaces ? placeholders with their values so EXPLAIN can print real numbers.</summary>
    public static Expr Substitute(Expr e, IReadOnlyList<Value> parameters) => e switch
    {
        ParameterExpr p => new LiteralExpr(Param(p, parameters)),
        UnaryExpr u => u with { Operand = Substitute(u.Operand, parameters) },
        BinaryExpr b => b with { Left = Substitute(b.Left, parameters), Right = Substitute(b.Right, parameters) },
        IsNullExpr n => n with { Operand = Substitute(n.Operand, parameters) },
        LikeExpr l => l with { Operand = Substitute(l.Operand, parameters), Pattern = Substitute(l.Pattern, parameters) },
        FunctionExpr f => f with { Arguments = f.Arguments.Select(a => Substitute(a, parameters)).ToList() },
        AggregateExpr a => a.Argument == null ? a : a with { Argument = Substitute(a.Argument, parameters) },
        _ => e,
    };
}
