using Basalt.Sql;
using Basalt.Values;

namespace Basalt.Tests;

public class LexerTests
{
    [Fact]
    public void Tokenizes_all_kinds()
    {
        var tokens = Lexer.Tokenize("SELECT a, 'it''s', 3, 2.5, 1e3, ? FROM \"My Table\" WHERE x <= -1 -- comment\n;");
        var kinds = tokens.Select(t => t.Kind).ToList();
        Assert.Equal(TokenKind.Identifier, kinds[0]);
        Assert.Contains(tokens, t => t.Kind == TokenKind.String && t.Text == "it's");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Integer && t.Text == "3");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Real && t.Text == "2.5");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Real && t.Text == "1e3");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Parameter);
        Assert.Contains(tokens, t => t.Kind == TokenKind.QuotedIdentifier && t.Text == "My Table");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Symbol && t.Text == "<=");
        Assert.DoesNotContain(tokens, t => t.Text.Contains("comment"));
        Assert.Equal(TokenKind.End, kinds[^1]);
    }

    [Fact]
    public void Reports_line_and_column()
    {
        var ex = Assert.Throws<SqlSyntaxException>(() => Lexer.Tokenize("SELECT 1\nFROM t WHERE a = 'open"));
        Assert.Equal(2, ex.Line);
        Assert.Equal(18, ex.Column);
    }

    [Theory]
    [InlineData("SELECT 1 # 2")]
    [InlineData("SELECT 12abc")]
    public void Rejects_bad_input(string sql) => Assert.Throws<SqlSyntaxException>(() => Lexer.Tokenize(sql));
}

public class ParserTests
{
    [Fact]
    public void Parses_create_table_with_constraints()
    {
        var s = Assert.IsType<CreateTableStmt>(Parser.ParseOne(
            "CREATE TABLE users (id INT PRIMARY KEY, name TEXT NOT NULL, score REAL, active BOOL)"));
        Assert.Equal("users", s.Table);
        Assert.Equal(4, s.Columns.Count);
        Assert.True(s.Columns[0].PrimaryKey);
        Assert.True(s.Columns[1].NotNull);
        Assert.Equal(DataType.Real, s.Columns[2].Type);
        Assert.Equal(DataType.Bool, s.Columns[3].Type);
    }

    [Fact]
    public void Parses_table_level_primary_key()
    {
        var s = Assert.IsType<CreateTableStmt>(Parser.ParseOne("CREATE TABLE t (a INT, b TEXT, PRIMARY KEY (b))"));
        Assert.True(s.Columns[1].PrimaryKey);
        Assert.True(s.Columns[1].NotNull);
    }

    [Fact]
    public void Respects_operator_precedence()
    {
        var s = Assert.IsType<SelectStmt>(Parser.ParseOne("SELECT 1 + 2 * 3 FROM t WHERE a = 1 OR b = 2 AND NOT c = 3"));
        Assert.Equal("(1 + (2 * 3))", s.Items[0].Expr!.ToString());
        Assert.Equal("((a = 1) OR ((b = 2) AND NOT (c = 3)))", s.Where!.ToString());
    }

    [Fact]
    public void Parses_full_select()
    {
        var s = Assert.IsType<SelectStmt>(Parser.ParseOne("""
            SELECT c.city AS place, COUNT(*), SUM(o.amount) total
            FROM orders o INNER JOIN customers c ON o.customer_id = c.id
            WHERE o.amount BETWEEN 10 AND 20 AND c.city IN ('a', 'b')
            GROUP BY c.city HAVING COUNT(*) > 1
            ORDER BY total DESC, place
            LIMIT 5 OFFSET 2
            """));
        Assert.Equal(3, s.Items.Count);
        Assert.Equal("place", s.Items[0].Alias);
        Assert.Equal("total", s.Items[2].Alias);
        Assert.IsType<AggregateExpr>(s.Items[1].Expr);
        Assert.Equal("o", s.From!.Alias);
        Assert.Single(s.Joins);
        Assert.Single(s.GroupBy);
        Assert.NotNull(s.Having);
        Assert.Equal(2, s.OrderBy.Count);
        Assert.True(s.OrderBy[0].Descending);
        Assert.NotNull(s.Limit);
        Assert.NotNull(s.Offset);
        Assert.Contains(">= 10", s.Where!.ToString());
    }

    [Fact]
    public void Parses_insert_update_delete_and_transactions()
    {
        var ins = Assert.IsType<InsertStmt>(Parser.ParseOne("INSERT INTO t (a, b) VALUES (1, 'x'), (2, NULL)"));
        Assert.Equal(2, ins.Rows.Count);
        var upd = Assert.IsType<UpdateStmt>(Parser.ParseOne("UPDATE t SET a = a + 1, b = 'y' WHERE a > 0"));
        Assert.Equal(2, upd.Assignments.Count);
        Assert.IsType<DeleteStmt>(Parser.ParseOne("DELETE FROM t"));
        var script = Parser.ParseScript("BEGIN; INSERT INTO t VALUES (1); COMMIT; ROLLBACK;;");
        Assert.Collection(script.Select(x => x.Statement),
            x => Assert.IsType<BeginStmt>(x),
            x => Assert.IsType<InsertStmt>(x),
            x => Assert.IsType<CommitStmt>(x),
            x => Assert.IsType<RollbackStmt>(x));
    }

    [Fact]
    public void Numbers_parameters_in_order()
    {
        var script = Parser.ParseScript("SELECT * FROM t WHERE a = ? AND b = ?");
        Assert.Equal(2, script[0].ParameterCount);
    }

    [Fact]
    public void Explain_wraps_statement()
    {
        var e = Assert.IsType<ExplainStmt>(Parser.ParseOne("EXPLAIN SELECT * FROM t WHERE id = 1"));
        Assert.IsType<SelectStmt>(e.Inner);
    }

    [Theory]
    [InlineData("SELECT FROM t", "expected an expression")]
    [InlineData("SELEC * FROM t", "expected a statement")]
    [InlineData("CREATE TABLE t (a BLOB)", "unknown column type")]
    [InlineData("SELECT * FROM t LEFT JOIN u ON t.a = u.a", "only inner joins")]
    [InlineData("INSERT INTO t VALUES (1", "expected ')'")]
    [InlineData("SELECT * FROM t WHERE", "expected an expression")]
    [InlineData("SELECT foo(1)", "unknown function")]
    public void Gives_useful_errors(string sql, string fragment)
    {
        var ex = Assert.ThrowsAny<BasaltException>(() => Parser.ParseOne(sql));
        Assert.Contains(fragment, ex.Message);
    }
}
