using Basalt.Values;

namespace Basalt.Sql;

/// <summary>
/// Recursive-descent parser. One method per grammar rule; expression precedence from lowest to highest:
/// OR, AND, NOT, comparison (= &lt;&gt; &lt; &lt;= &gt; &gt;= IS LIKE BETWEEN IN), || , + -, * / %, unary -, primary.
/// </summary>
public sealed class Parser
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "HAVING", "ORDER", "LIMIT", "OFFSET", "INSERT", "INTO",
        "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "TABLE", "INDEX", "ON", "JOIN", "INNER", "AND", "OR",
        "NOT", "NULL", "IS", "AS", "ASC", "DESC", "BEGIN", "COMMIT", "ROLLBACK", "EXPLAIN", "PRIMARY", "KEY",
        "TRUE", "FALSE", "LIKE", "BETWEEN", "IN", "IF", "EXISTS", "DISTINCT", "LEFT", "RIGHT", "CROSS",
    };

    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX",
    };

    private static readonly HashSet<string> ScalarFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROUND", "ABS", "LENGTH", "LOWER", "UPPER", "COALESCE",
    };

    private readonly List<Token> _tokens;
    private int _pos;
    private int _paramCount;

    private Parser(string sql) => _tokens = Lexer.Tokenize(sql);

    /// <summary>Number of ? placeholders seen so far.</summary>
    public int ParameterCount => _paramCount;

    /// <summary>Parses exactly one statement (a trailing semicolon is allowed).</summary>
    public static Statement ParseOne(string sql)
    {
        var all = ParseScript(sql);
        if (all.Count != 1) throw new BasaltException(all.Count == 0 ? "no statement given" : "expected a single statement");
        return all[0].Statement;
    }

    /// <summary>Parses a script of statements separated by semicolons.</summary>
    public static List<(Statement Statement, int ParameterCount)> ParseScript(string sql)
    {
        var p = new Parser(sql);
        var list = new List<(Statement, int)>();
        while (true)
        {
            while (p.AcceptSymbol(";")) { }
            if (p.Peek.Kind == TokenKind.End) break;
            p._paramCount = 0;
            var stmt = p.ParseStatement();
            list.Add((stmt, p._paramCount));
            if (p.Peek.Kind != TokenKind.End && !p.AcceptSymbol(";"))
                throw p.Error($"expected ';' or end of input but found {p.Peek}");
        }
        return list;
    }

    // ---- token helpers -------------------------------------------------------------------

    private Token Peek => _tokens[_pos];
    private Token PeekAt(int ahead) => _tokens[Math.Min(_pos + ahead, _tokens.Count - 1)];
    private Token Next()
    {
        var t = _tokens[_pos];
        if (_pos < _tokens.Count - 1) _pos++;
        return t;
    }

    private SqlSyntaxException Error(string message) => new(message, Peek.Line, Peek.Column);

    private static bool IsKeyword(Token t, string kw) =>
        t.Kind == TokenKind.Identifier && string.Equals(t.Text, kw, StringComparison.OrdinalIgnoreCase);

    private bool PeekKeyword(string kw) => IsKeyword(Peek, kw);

    private bool AcceptKeyword(string kw)
    {
        if (!PeekKeyword(kw)) return false;
        _pos++;
        return true;
    }

    private void ExpectKeyword(string kw)
    {
        if (!AcceptKeyword(kw)) throw Error($"expected {kw} but found {Peek}");
    }

    private bool PeekSymbol(string s) => Peek.Kind == TokenKind.Symbol && Peek.Text == s;

    private bool AcceptSymbol(string s)
    {
        if (!PeekSymbol(s)) return false;
        _pos++;
        return true;
    }

    private void ExpectSymbol(string s)
    {
        if (!AcceptSymbol(s)) throw Error($"expected '{s}' but found {Peek}");
    }

    private string ExpectName(string what)
    {
        var t = Peek;
        if (t.Kind == TokenKind.QuotedIdentifier) { _pos++; return t.Text; }
        if (t.Kind == TokenKind.Identifier && !Reserved.Contains(t.Text)) { _pos++; return t.Text; }
        throw Error($"expected {what} but found {t}");
    }

    // ---- statements ------------------------------------------------------------------------

    private Statement ParseStatement()
    {
        if (AcceptKeyword("EXPLAIN"))
        {
            var inner = ParseStatement();
            if (inner is not (SelectStmt or UpdateStmt or DeleteStmt))
                throw Error("EXPLAIN works with SELECT, UPDATE and DELETE");
            return new ExplainStmt(inner);
        }
        if (PeekKeyword("SELECT")) return ParseSelect();
        if (PeekKeyword("INSERT")) return ParseInsert();
        if (PeekKeyword("UPDATE")) return ParseUpdate();
        if (PeekKeyword("DELETE")) return ParseDelete();
        if (PeekKeyword("CREATE")) return ParseCreate();
        if (AcceptKeyword("BEGIN"))
        {
            AcceptKeyword("TRANSACTION");
            return new BeginStmt();
        }
        if (AcceptKeyword("COMMIT") || AcceptKeyword("END"))
        {
            AcceptKeyword("TRANSACTION");
            return new CommitStmt();
        }
        if (AcceptKeyword("ROLLBACK"))
        {
            AcceptKeyword("TRANSACTION");
            return new RollbackStmt();
        }
        throw Error($"expected a statement but found {Peek}");
    }

    private Statement ParseCreate()
    {
        ExpectKeyword("CREATE");
        if (AcceptKeyword("TABLE"))
        {
            bool ifNotExists = ParseIfNotExists();
            string table = ExpectName("a table name");
            ExpectSymbol("(");
            var columns = new List<ColumnDef>();
            string? tablePk = null;
            do
            {
                if (PeekKeyword("PRIMARY"))
                {
                    _pos++;
                    ExpectKeyword("KEY");
                    ExpectSymbol("(");
                    tablePk = ExpectName("a column name");
                    ExpectSymbol(")");
                    continue;
                }
                columns.Add(ParseColumnDef());
            } while (AcceptSymbol(","));
            ExpectSymbol(")");

            if (tablePk != null)
            {
                int i = columns.FindIndex(c => string.Equals(c.Name, tablePk, StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw new BasaltException($"PRIMARY KEY names unknown column {tablePk}");
                if (columns.Any(c => c.PrimaryKey)) throw new BasaltException("only one PRIMARY KEY is allowed");
                columns[i] = columns[i] with { PrimaryKey = true, NotNull = true };
            }
            return new CreateTableStmt(table, columns, ifNotExists);
        }
        if (AcceptKeyword("INDEX"))
        {
            bool ifNotExists = ParseIfNotExists();
            string index = ExpectName("an index name");
            ExpectKeyword("ON");
            string table = ExpectName("a table name");
            ExpectSymbol("(");
            string column = ExpectName("a column name");
            if (PeekSymbol(",")) throw Error("only single-column indexes are supported");
            ExpectSymbol(")");
            return new CreateIndexStmt(index, table, column, ifNotExists);
        }
        throw Error($"expected TABLE or INDEX after CREATE but found {Peek}");
    }

    private bool ParseIfNotExists()
    {
        if (!AcceptKeyword("IF")) return false;
        ExpectKeyword("NOT");
        ExpectKeyword("EXISTS");
        return true;
    }

    private ColumnDef ParseColumnDef()
    {
        string name = ExpectName("a column name");
        var typeTok = Next();
        DataType type = typeTok.Kind == TokenKind.Identifier ? typeTok.Text.ToUpperInvariant() switch
        {
            "INT" or "INTEGER" or "BIGINT" => DataType.Int,
            "TEXT" or "VARCHAR" or "STRING" => DataType.Text,
            "REAL" or "FLOAT" or "DOUBLE" => DataType.Real,
            "BOOL" or "BOOLEAN" => DataType.Bool,
            _ => throw new SqlSyntaxException($"unknown column type {typeTok}; use INT, TEXT, REAL or BOOL", typeTok.Line, typeTok.Column),
        } : throw new SqlSyntaxException($"expected a column type but found {typeTok}", typeTok.Line, typeTok.Column);

        // VARCHAR(100) style lengths are accepted and ignored.
        if (AcceptSymbol("("))
        {
            if (Next().Kind != TokenKind.Integer) throw Error("expected a length");
            ExpectSymbol(")");
        }

        bool pk = false, notNull = false;
        while (true)
        {
            if (AcceptKeyword("PRIMARY")) { ExpectKeyword("KEY"); pk = true; notNull = true; }
            else if (AcceptKeyword("NOT")) { ExpectKeyword("NULL"); notNull = true; }
            else if (AcceptKeyword("NULL")) { }
            else break;
        }
        return new ColumnDef(name, type, pk, notNull);
    }

    private InsertStmt ParseInsert()
    {
        ExpectKeyword("INSERT");
        ExpectKeyword("INTO");
        string table = ExpectName("a table name");
        List<string>? columns = null;
        if (AcceptSymbol("("))
        {
            columns = new List<string>();
            do columns.Add(ExpectName("a column name")); while (AcceptSymbol(","));
            ExpectSymbol(")");
        }
        ExpectKeyword("VALUES");
        var rows = new List<IReadOnlyList<Expr>>();
        do
        {
            ExpectSymbol("(");
            var row = new List<Expr>();
            do row.Add(ParseExpr()); while (AcceptSymbol(","));
            ExpectSymbol(")");
            rows.Add(row);
        } while (AcceptSymbol(","));
        return new InsertStmt(table, columns, rows);
    }

    private UpdateStmt ParseUpdate()
    {
        ExpectKeyword("UPDATE");
        string table = ExpectName("a table name");
        ExpectKeyword("SET");
        var sets = new List<Assignment>();
        do
        {
            string col = ExpectName("a column name");
            ExpectSymbol("=");
            sets.Add(new Assignment(col, ParseExpr()));
        } while (AcceptSymbol(","));
        Expr? where = AcceptKeyword("WHERE") ? ParseExpr() : null;
        return new UpdateStmt(table, sets, where);
    }

    private DeleteStmt ParseDelete()
    {
        ExpectKeyword("DELETE");
        ExpectKeyword("FROM");
        string table = ExpectName("a table name");
        Expr? where = AcceptKeyword("WHERE") ? ParseExpr() : null;
        return new DeleteStmt(table, where);
    }

    private SelectStmt ParseSelect()
    {
        ExpectKeyword("SELECT");
        if (PeekKeyword("DISTINCT")) throw Error("DISTINCT is not supported (use GROUP BY)");

        var items = new List<SelectItem>();
        do items.Add(ParseSelectItem()); while (AcceptSymbol(","));

        TableRef? from = null;
        var joins = new List<JoinClause>();
        if (AcceptKeyword("FROM"))
        {
            from = ParseTableRef();
            while (true)
            {
                if (PeekKeyword("LEFT") || PeekKeyword("RIGHT") || PeekKeyword("CROSS"))
                    throw Error($"{Peek.Text.ToUpperInvariant()} JOIN is not supported; only inner joins");
                if (PeekSymbol(",")) throw Error("comma joins are not supported; use JOIN ... ON");
                bool inner = AcceptKeyword("INNER");
                if (!AcceptKeyword("JOIN"))
                {
                    if (inner) throw Error("expected JOIN after INNER");
                    break;
                }
                var t = ParseTableRef();
                ExpectKeyword("ON");
                joins.Add(new JoinClause(t, ParseExpr()));
            }
        }

        Expr? where = AcceptKeyword("WHERE") ? ParseExpr() : null;

        var groupBy = new List<Expr>();
        if (AcceptKeyword("GROUP"))
        {
            ExpectKeyword("BY");
            do groupBy.Add(ParseExpr()); while (AcceptSymbol(","));
        }
        Expr? having = AcceptKeyword("HAVING") ? ParseExpr() : null;

        var orderBy = new List<OrderItem>();
        if (AcceptKeyword("ORDER"))
        {
            ExpectKeyword("BY");
            do
            {
                var e = ParseExpr();
                bool desc = false;
                if (AcceptKeyword("DESC")) desc = true;
                else AcceptKeyword("ASC");
                orderBy.Add(new OrderItem(e, desc));
            } while (AcceptSymbol(","));
        }

        Expr? limit = null, offset = null;
        if (AcceptKeyword("LIMIT"))
        {
            limit = ParseExpr();
            if (AcceptKeyword("OFFSET")) offset = ParseExpr();
        }
        return new SelectStmt(items, from, joins, where, groupBy, having, orderBy, limit, offset);
    }

    private SelectItem ParseSelectItem()
    {
        if (AcceptSymbol("*")) return new SelectItem(null, null, null);
        // t.*
        if ((Peek.Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier) && PeekAt(1).Text == "." && PeekAt(2).Text == "*"
            && PeekAt(1).Kind == TokenKind.Symbol && PeekAt(2).Kind == TokenKind.Symbol)
        {
            string t = ExpectName("a table name");
            _pos += 2;
            return new SelectItem(null, null, t);
        }
        var expr = ParseExpr();
        string? alias = null;
        if (AcceptKeyword("AS")) alias = ExpectName("an alias");
        else if (Peek.Kind == TokenKind.QuotedIdentifier || (Peek.Kind == TokenKind.Identifier && !Reserved.Contains(Peek.Text)))
            alias = ExpectName("an alias");
        return new SelectItem(expr, alias, null);
    }

    private TableRef ParseTableRef()
    {
        string table = ExpectName("a table name");
        string? alias = null;
        if (AcceptKeyword("AS")) alias = ExpectName("an alias");
        else if (Peek.Kind == TokenKind.QuotedIdentifier || (Peek.Kind == TokenKind.Identifier && !Reserved.Contains(Peek.Text)))
            alias = ExpectName("an alias");
        return new TableRef(table, alias);
    }

    // ---- expressions -----------------------------------------------------------------------

    public Expr ParseExpr() => ParseOr();

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (AcceptKeyword("OR")) left = new BinaryExpr("OR", left, ParseAnd());
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (AcceptKeyword("AND")) left = new BinaryExpr("AND", left, ParseNot());
        return left;
    }

    private Expr ParseNot()
    {
        if (AcceptKeyword("NOT")) return new UnaryExpr("NOT", ParseNot());
        return ParseComparison();
    }

    private Expr ParseComparison()
    {
        var left = ParseConcat();
        while (true)
        {
            if (Peek.Kind == TokenKind.Symbol && Peek.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
            {
                string op = Next().Text;
                if (op == "!=") op = "<>";
                left = new BinaryExpr(op, left, ParseConcat());
                continue;
            }
            if (AcceptKeyword("IS"))
            {
                bool not = AcceptKeyword("NOT");
                ExpectKeyword("NULL");
                left = new IsNullExpr(left, not);
                continue;
            }

            bool negated = false;
            int save = _pos;
            if (AcceptKeyword("NOT")) negated = true;
            if (AcceptKeyword("LIKE"))
            {
                left = new LikeExpr(left, ParseConcat(), negated);
                continue;
            }
            if (AcceptKeyword("BETWEEN"))
            {
                // a BETWEEN x AND y  ==>  a >= x AND a <= y (planner can then use an index range)
                var lo = ParseConcat();
                ExpectKeyword("AND");
                var hi = ParseConcat();
                Expr range = new BinaryExpr("AND", new BinaryExpr(">=", left, lo), new BinaryExpr("<=", left, hi));
                left = negated ? new UnaryExpr("NOT", range) : range;
                continue;
            }
            if (AcceptKeyword("IN"))
            {
                // a IN (x, y)  ==>  a = x OR a = y
                ExpectSymbol("(");
                Expr? any = null;
                do
                {
                    var eq = new BinaryExpr("=", left, ParseExpr());
                    any = any == null ? eq : new BinaryExpr("OR", any, eq);
                } while (AcceptSymbol(","));
                ExpectSymbol(")");
                left = negated ? new UnaryExpr("NOT", any!) : any!;
                continue;
            }
            _pos = save;
            return left;
        }
    }

    private Expr ParseConcat()
    {
        var left = ParseAdditive();
        while (AcceptSymbol("||")) left = new BinaryExpr("||", left, ParseAdditive());
        return left;
    }

    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (PeekSymbol("+") || PeekSymbol("-"))
        {
            string op = Next().Text;
            left = new BinaryExpr(op, left, ParseMultiplicative());
        }
        return left;
    }

    private Expr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (PeekSymbol("*") || PeekSymbol("/") || PeekSymbol("%"))
        {
            string op = Next().Text;
            left = new BinaryExpr(op, left, ParseUnary());
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (AcceptSymbol("-"))
        {
            // Fold "-5" into a literal so it can be used for index lookups.
            if (Peek.Kind == TokenKind.Integer)
            {
                var t = Next();
                if (t.Text == "9223372036854775808") return new LiteralExpr(Value.FromInt(long.MinValue));
                return new LiteralExpr(Value.FromInt(-Lexer.ParseInteger(t)));
            }
            if (Peek.Kind == TokenKind.Real) return new LiteralExpr(Value.FromReal(-Lexer.ParseReal(Next())));
            return new UnaryExpr("-", ParseUnary());
        }
        if (AcceptSymbol("+")) return ParseUnary();
        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case TokenKind.Integer:
                _pos++;
                return new LiteralExpr(Value.FromInt(Lexer.ParseInteger(t)));
            case TokenKind.Real:
                _pos++;
                return new LiteralExpr(Value.FromReal(Lexer.ParseReal(t)));
            case TokenKind.String:
                _pos++;
                return new LiteralExpr(Value.FromText(t.Text));
            case TokenKind.Parameter:
                _pos++;
                return new ParameterExpr(_paramCount++);
            case TokenKind.Symbol when t.Text == "(":
                _pos++;
                var inner = ParseExpr();
                ExpectSymbol(")");
                return inner;
        }

        if (AcceptKeyword("NULL")) return new LiteralExpr(Value.Null);
        if (AcceptKeyword("TRUE")) return new LiteralExpr(Value.True);
        if (AcceptKeyword("FALSE")) return new LiteralExpr(Value.False);

        if (t.Kind == TokenKind.Identifier && Aggregates.Contains(t.Text) && PeekAt(1).Kind == TokenKind.Symbol && PeekAt(1).Text == "(")
        {
            string fn = t.Text.ToUpperInvariant();
            _pos += 2;
            if (PeekKeyword("DISTINCT")) throw Error("DISTINCT inside aggregates is not supported");
            Expr? arg = null;
            if (AcceptSymbol("*"))
            {
                if (fn != "COUNT") throw Error($"{fn}(*) is not valid; only COUNT(*)");
            }
            else arg = ParseExpr();
            ExpectSymbol(")");
            return new AggregateExpr(fn, arg);
        }

        if (t.Kind == TokenKind.Identifier && ScalarFunctions.Contains(t.Text) && PeekAt(1).Kind == TokenKind.Symbol && PeekAt(1).Text == "(")
        {
            string fn = t.Text.ToUpperInvariant();
            _pos += 2;
            var args = new List<Expr>();
            if (!PeekSymbol(")"))
                do args.Add(ParseExpr()); while (AcceptSymbol(","));
            ExpectSymbol(")");
            return new FunctionExpr(fn, args);
        }

        if (t.Kind == TokenKind.Identifier && Reserved.Contains(t.Text))
            throw Error($"expected an expression but found {t}");

        if (t.Kind is TokenKind.Identifier or TokenKind.QuotedIdentifier)
        {
            if (t.Kind == TokenKind.Identifier && PeekAt(1).Kind == TokenKind.Symbol && PeekAt(1).Text == "(")
                throw Error($"unknown function {t.Text}");
            string first = ExpectName("a column name");
            if (AcceptSymbol("."))
            {
                string col = ExpectName("a column name");
                return new ColumnExpr(first, col);
            }
            return new ColumnExpr(null, first);
        }

        throw Error($"expected an expression but found {t}");
    }
}
