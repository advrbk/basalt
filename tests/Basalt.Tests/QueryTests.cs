using Basalt.Values;

namespace Basalt.Tests;

public class QueryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly BasaltDatabase _db;

    public QueryTests()
    {
        _db = BasaltDatabase.Open(_dir.File("q.db"));
        _db.ExecuteScript("""
            CREATE TABLE customers (id INT PRIMARY KEY, name TEXT NOT NULL, city TEXT);
            CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT NOT NULL, amount REAL, paid BOOL);
            INSERT INTO customers VALUES (1, 'Ana', 'Lisbon'), (2, 'Bo', 'Oslo'), (3, 'Cy', 'Lisbon'), (4, 'Di', NULL);
            INSERT INTO orders VALUES
              (10, 1, 20.0, TRUE), (11, 1, 5.5, FALSE), (12, 2, 100.0, TRUE),
              (13, 3, 7.25, TRUE), (14, 3, NULL, FALSE), (15, 9, 1.0, TRUE);
            """);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private QueryResult Q(string sql, params object?[] args) => _db.Execute(sql, args);

    [Fact]
    public void Where_with_and_or_and_comparisons()
    {
        Assert.Equal([1L, 3L], Q("SELECT id FROM customers WHERE city = 'Lisbon' ORDER BY id").Longs());
        Assert.Equal([2L, 3L], Q("SELECT id FROM customers WHERE id >= 2 AND id < 4 ORDER BY id").Longs());
        Assert.Equal([1L, 2L, 4L], Q("SELECT id FROM customers WHERE id = 1 OR city = 'Oslo' OR city IS NULL ORDER BY id").Longs());
        Assert.Equal([3L], Q("SELECT id FROM customers WHERE NOT (city = 'Oslo') AND id <> 1").Longs());
        Assert.Equal([2L], Q("SELECT id FROM customers WHERE name LIKE 'b%'").Longs());
        Assert.Equal([1L, 2L], Q("SELECT id FROM customers WHERE id BETWEEN 1 AND 2 ORDER BY id").Longs());
        Assert.Equal([1L, 4L], Q("SELECT id FROM customers WHERE id IN (4, 1) ORDER BY id").Longs());
    }

    [Fact]
    public void Null_comparisons_are_unknown()
    {
        // city = NULL is never true; only IS NULL finds the row.
        Assert.Empty(Q("SELECT id FROM customers WHERE city = NULL").Rows);
        Assert.Equal([4L], Q("SELECT id FROM customers WHERE city IS NULL").Longs());
        Assert.Equal(3, Q("SELECT COUNT(*) FROM customers WHERE city IS NOT NULL").Long());
    }

    [Fact]
    public void Order_by_limit_offset()
    {
        Assert.Equal([12L, 10L, 13L], Q("SELECT id FROM orders WHERE amount IS NOT NULL ORDER BY amount DESC LIMIT 3").Longs());
        Assert.Equal([13L, 11L], Q("SELECT id FROM orders WHERE amount IS NOT NULL ORDER BY amount DESC LIMIT 2 OFFSET 2").Longs());
        // NULL sorts first ascending.
        Assert.Equal(14L, Q("SELECT id FROM orders ORDER BY amount LIMIT 1").Long());
        // ORDER BY an alias and by position.
        Assert.Equal(["Ana", "Bo"], Q("SELECT name AS n FROM customers ORDER BY n LIMIT 2").Strings());
        Assert.Equal(["Di", "Cy"], Q("SELECT name FROM customers ORDER BY 1 DESC LIMIT 2").Strings());
    }

    [Fact]
    public void Aggregates_with_and_without_group_by()
    {
        var r = Q("SELECT COUNT(*), COUNT(amount), SUM(amount), MIN(amount), MAX(amount) FROM orders").Rows.Single();
        Assert.Equal(6L, r[0].AsInt);
        Assert.Equal(5L, r[1].AsInt); // NULL not counted
        Assert.Equal(133.75, r[2].AsReal, 6);
        Assert.Equal(1.0, r[3].AsReal);
        Assert.Equal(100.0, r[4].AsReal);
        Assert.Equal(133.75 / 5, Q("SELECT AVG(amount) FROM orders").Scalar().AsReal, 6);

        var groups = Q("SELECT customer_id, COUNT(*) AS n, SUM(amount) FROM orders GROUP BY customer_id ORDER BY customer_id").Plain();
        Assert.Equal(4, groups.Count);
        Assert.Equal(new object?[] { 1L, 2L, 25.5 }, groups[0]);
        Assert.Equal(new object?[] { 3L, 2L, 7.25 }, groups[2]);

        Assert.Equal([1L, 3L], Q("SELECT customer_id FROM orders GROUP BY customer_id HAVING COUNT(*) > 1 ORDER BY customer_id").Longs());
        Assert.Equal(0, Q("SELECT COUNT(*) FROM orders WHERE amount > 1000").Long());
        Assert.True(Q("SELECT SUM(amount) FROM orders WHERE amount > 1000").Scalar().IsNull);
        Assert.Equal(3L, Q("SELECT SUM(o.customer_id) FROM customers c JOIN orders o ON o.customer_id = c.id WHERE c.id < 3 AND o.paid = TRUE").Scalar().AsInt);
    }

    [Fact]
    public void Group_by_rejects_bare_columns()
    {
        var ex = Assert.Throws<BasaltException>(() => Q("SELECT city, name FROM customers GROUP BY city"));
        Assert.Contains("GROUP BY", ex.Message);
    }

    [Fact]
    public void Inner_join_on_equality()
    {
        var rows = Q("""
            SELECT c.name, o.id FROM customers c JOIN orders o ON c.id = o.customer_id
            WHERE o.paid = TRUE ORDER BY o.id
            """).Plain();
        Assert.Equal([new object?[] { "Ana", 10L }, new object?[] { "Bo", 12L }, new object?[] { "Cy", 13L }], rows);

        // Order 15 points at customer 9, which does not exist: an inner join drops it.
        Assert.Equal(5, Q("SELECT COUNT(*) FROM orders o JOIN customers c ON o.customer_id = c.id").Long());

        var spend = Q("""
            SELECT c.city, SUM(o.amount) AS total FROM orders o JOIN customers c ON o.customer_id = c.id
            GROUP BY c.city ORDER BY total DESC
            """).Plain();
        Assert.Equal(new object?[] { "Oslo", 100.0 }, spend[0]);
        Assert.Equal(new object?[] { "Lisbon", 32.75 }, spend[1]);
    }

    [Fact]
    public void Join_strategies_agree()
    {
        // pk index join, hash join (no index on the inner side) and nested loop must return the same rows.
        var indexJoin = Q("SELECT o.id, c.name FROM orders o JOIN customers c ON o.customer_id = c.id ORDER BY o.id").Plain();
        var hashJoin = Q("SELECT o.id, c.name FROM customers c JOIN orders o ON o.customer_id = c.id ORDER BY o.id").Plain();
        var loopJoin = Q("SELECT o.id, c.name FROM customers c JOIN orders o ON o.customer_id <= c.id AND o.customer_id >= c.id ORDER BY o.id").Plain();
        Assert.Equal(indexJoin, hashJoin);
        Assert.Equal(indexJoin, loopJoin);
        Assert.Contains("Index nested-loop join", Explain("SELECT * FROM orders o JOIN customers c ON o.customer_id = c.id"));
        Assert.Contains("Hash join", Explain("SELECT * FROM customers c JOIN orders o ON o.customer_id = c.id"));
        Assert.Contains("Nested-loop join", Explain("SELECT * FROM customers c JOIN orders o ON o.customer_id < c.id"));
    }

    [Fact]
    public void Three_way_join()
    {
        Q("CREATE TABLE cities (name TEXT PRIMARY KEY, country TEXT)");
        Q("INSERT INTO cities VALUES ('Lisbon', 'PT'), ('Oslo', 'NO')");
        var rows = Q("""
            SELECT ci.country, COUNT(*) FROM orders o
            JOIN customers c ON o.customer_id = c.id
            JOIN cities ci ON ci.name = c.city
            GROUP BY ci.country ORDER BY ci.country
            """).Plain();
        Assert.Equal([new object?[] { "NO", 1L }, new object?[] { "PT", 4L }], rows);
    }

    [Fact]
    public void Update_and_delete_return_counts_and_change_rows()
    {
        Assert.Equal(2, Q("UPDATE orders SET amount = amount * 2 WHERE customer_id = 1").RowsAffected);
        Assert.Equal(51.0, Q("SELECT SUM(amount) FROM orders WHERE customer_id = 1").Scalar().AsReal);
        Assert.Equal(1, Q("DELETE FROM orders WHERE amount IS NULL").RowsAffected);
        Assert.Equal(5, Q("SELECT COUNT(*) FROM orders").Long());
        // Changing the primary key keeps the index right.
        Q("UPDATE customers SET id = 40 WHERE id = 4");
        Assert.Equal("Di", Q("SELECT name FROM customers WHERE id = 40").Scalar().AsText);
        Assert.Empty(Q("SELECT name FROM customers WHERE id = 4").Rows);
        Assert.True(_db.CheckIntegrity().Ok);
    }

    [Fact]
    public void Update_that_grows_rows_moves_them_and_keeps_indexes_valid()
    {
        Q("CREATE INDEX idx_city ON customers (city)");
        for (int i = 100; i < 400; i++) Q("INSERT INTO customers VALUES (?, ?, ?)", i, "n", "c" + (i % 7));
        Q("UPDATE customers SET name = ? WHERE id >= 100", new string('x', 900));
        Assert.Equal(300, Q("SELECT COUNT(*) FROM customers WHERE name = ?", new string('x', 900)).Long());
        Assert.Equal(43, Q("SELECT COUNT(*) FROM customers WHERE city = 'c0'").Long());
        var report = _db.CheckIntegrity();
        Assert.True(report.Ok, report.ToString());
    }

    [Fact]
    public void Planner_uses_indexes_when_the_where_clause_allows()
    {
        Assert.Contains("Index lookup on customers using pk_customers (id = 2)", Explain("SELECT * FROM customers WHERE id = 2"));
        Assert.Contains("Index lookup on customers using pk_customers (id = 2)", Explain("SELECT * FROM customers WHERE 2 = id AND name = 'x'"));
        Assert.Contains("Index range scan on orders using pk_orders (id >= 11 AND id < 14)", Explain("SELECT * FROM orders WHERE id >= 11 AND id < 14"));
        Assert.Contains("Full table scan on customers", Explain("SELECT * FROM customers WHERE city = 'Oslo'"));
        Assert.Contains("Full table scan", Explain("SELECT * FROM customers WHERE id = 1 OR id = 2"));

        Q("CREATE INDEX idx_city ON customers (city)");
        Assert.Contains("Index lookup on customers using idx_city (city = 'Oslo')", Explain("SELECT * FROM customers WHERE city = 'Oslo'"));
        // The primary key wins over a secondary index.
        Assert.Contains("pk_customers", Explain("SELECT * FROM customers WHERE city = 'Oslo' AND id = 2"));
        // Parameters are planned with their values.
        Assert.Contains("(id = 3)", _db.Execute("EXPLAIN SELECT * FROM customers WHERE id = ?", 3).Strings().First(s => s.Contains("Index")));
        Assert.Contains("Index lookup on customers", string.Join("\n", Q("EXPLAIN DELETE FROM customers WHERE id = 1").Strings()));
    }

    [Fact]
    public void Index_and_scan_give_identical_results()
    {
        var rng = new Random(9);
        Q("CREATE TABLE nums (id INT PRIMARY KEY, k INT, r REAL, s TEXT)");
        for (int i = 0; i < 2000; i++)
            Q("INSERT INTO nums VALUES (?, ?, ?, ?)", i, rng.Next(-50, 50), rng.NextDouble() * 10 - 5, "s" + rng.Next(100));
        string[] queries =
        [
            "SELECT id FROM nums WHERE k = 7 ORDER BY id",
            "SELECT id FROM nums WHERE k >= -3 AND k < 4 ORDER BY id",
            "SELECT id FROM nums WHERE k > 45 ORDER BY id",
            "SELECT id FROM nums WHERE k <= -48 ORDER BY id",
            "SELECT id FROM nums WHERE r > 4.5 ORDER BY id",
            "SELECT id FROM nums WHERE r >= -1 AND r <= 1 ORDER BY id",
            "SELECT id FROM nums WHERE s = 's42' ORDER BY id",
            "SELECT id FROM nums WHERE s >= 's5' AND s < 's6' ORDER BY id",
            "SELECT id FROM nums WHERE k = 2.0 ORDER BY id",
            "SELECT id FROM nums WHERE k < 2.5 ORDER BY id",
        ];
        var before = queries.Select(q => Q(q).Longs()).ToList();
        Q("CREATE INDEX ik ON nums (k)");
        Q("CREATE INDEX ir ON nums (r)");
        Q("CREATE INDEX isx ON nums (s)");
        for (int i = 0; i < queries.Length; i++)
            Assert.Equal(before[i], Q(queries[i]).Longs());
        Assert.Contains("Index range scan on nums using ir", Explain(queries[5]));
        Assert.Contains("Index lookup on nums using isx", Explain(queries[6]));
    }

    [Fact]
    public void Constraints_and_types_are_enforced()
    {
        Assert.Throws<ConstraintException>(() => Q("INSERT INTO customers VALUES (1, 'again', 'x')"));
        Assert.Throws<ConstraintException>(() => Q("INSERT INTO customers VALUES (5, NULL, 'x')"));
        Assert.Throws<ConstraintException>(() => Q("INSERT INTO customers VALUES ('five', 'x', 'x')"));
        Assert.Throws<ConstraintException>(() => Q("INSERT INTO customers (name) VALUES ('no id')"));
        Q("INSERT INTO orders VALUES (99, 1, 3, 1)"); // INT into REAL and 1 into BOOL are converted
        var row = Q("SELECT amount, paid FROM orders WHERE id = 99").Rows.Single();
        Assert.Equal(ValueKind.Real, row[0].Kind);
        Assert.True(row[1].AsBool);
        Assert.Throws<BasaltException>(() => Q("SELECT * FROM nope"));
        Assert.Throws<BasaltException>(() => Q("SELECT nope FROM customers"));
        Assert.Throws<BasaltException>(() => Q("SELECT id FROM customers c JOIN orders o ON c.id = o.customer_id"));
        Assert.Throws<BasaltException>(() => Q("SELECT * FROM customers WHERE name > 3"));
    }

    [Fact]
    public void Expressions_and_parameters()
    {
        var r = Q("SELECT 1 + 2 * 3, 7 / 2, 7.0 / 2, 7 % 3, 'a' || 'b', -(-4), NULL + 1").Rows.Single();
        Assert.Equal(7L, r[0].AsInt);
        Assert.Equal(3L, r[1].AsInt);
        Assert.Equal(3.5, r[2].AsReal);
        Assert.Equal(1L, r[3].AsInt);
        Assert.Equal("ab", r[4].AsText);
        Assert.Equal(4L, r[5].AsInt);
        Assert.True(r[6].IsNull);
        Assert.Throws<BasaltException>(() => Q("SELECT 1 / 0"));
        Assert.Throws<BasaltException>(() => Q("SELECT 9223372036854775807 + 1"));
        Assert.Equal("Bo", Q("SELECT name FROM customers WHERE id = ? AND city = ?", 2, "Oslo").Scalar().AsText);
        var fn = Q("SELECT ROUND(3.14159, 2), ABS(-3), LENGTH('héllo'), UPPER('ab'), COALESCE(NULL, 5), ROUND(SUM(amount), 1) FROM orders").Rows.Single();
        Assert.Equal(3.14, fn[0].AsReal);
        Assert.Equal(3L, fn[1].AsInt);
        Assert.Equal(5L, fn[2].AsInt);
        Assert.Equal("AB", fn[3].AsText);
        Assert.Equal(5L, fn[4].AsInt);
        Assert.Equal(133.8, fn[5].AsReal);
        Assert.Equal(["ANA"], Q("SELECT UPPER(name) FROM customers WHERE LOWER(name) = 'ana'").Strings());
        var prepared = _db.Prepare("SELECT COUNT(*) FROM orders WHERE customer_id = ?");
        Assert.Equal(2, prepared.Execute(1).Long());
        Assert.Equal(1, prepared.Execute(2).Long());
    }

    [Fact]
    public void Text_with_special_characters_round_trips()
    {
        Q("CREATE TABLE notes (id INT PRIMARY KEY, body TEXT)");
        Q("CREATE INDEX idx_body ON notes (body)");
        string[] bodies = ["it's", "line\nbreak", "zero\0byte", "emoji ðŸ˜€", "", "Ã¼nÃ¯cÃ¶dÃ©"];
        for (int i = 0; i < bodies.Length; i++) Q("INSERT INTO notes VALUES (?, ?)", i, bodies[i]);
        for (int i = 0; i < bodies.Length; i++)
            Assert.Equal(i, Q("SELECT id FROM notes WHERE body = ?", bodies[i]).Long());
        Assert.True(_db.CheckIntegrity().Ok);
    }

    private string Explain(string sql) => string.Join("\n", Q("EXPLAIN " + sql).Strings());
}
