using Basalt.Query;
using Basalt.Sql;
using Basalt.Values;

namespace Basalt;

/// <summary>
/// A connection to the database. It owns transaction state:
/// <list type="bullet">
/// <item>Outside BEGIN, every statement is its own transaction (autocommit). Reads take the shared
///       lock; writes take the exclusive lock, run, and commit (fsync) before returning.</item>
/// <item>BEGIN takes the exclusive lock and keeps it until COMMIT or ROLLBACK. Other sessions wait.</item>
/// <item>Inside a transaction a failing statement is undone on its own (statement savepoint);
///       the transaction stays open, as in PostgreSQL's savepoint-per-statement behaviour or SQLite.</item>
/// </list>
/// </summary>
public sealed class BasaltSession : IDisposable
{
    private readonly BasaltDatabase _db;
    private bool _inTransaction;
    private bool _disposed;

    internal BasaltSession(BasaltDatabase db) => _db = db;

    public bool InTransaction => _inTransaction;

    public QueryResult Execute(string sql, params object?[] args)
    {
        var script = Parser.ParseScript(sql);
        if (script.Count == 0) throw new BasaltException("no statement given");
        var values = args.Select(Value.FromObject).ToArray();
        QueryResult? last = null;
        foreach (var (stmt, _) in script) last = Execute(stmt, values);
        return last!;
    }

    /// <summary>Runs every statement in the text and returns each result (used by the REPL).</summary>
    public IReadOnlyList<QueryResult> ExecuteScript(string sql)
    {
        var results = new List<QueryResult>();
        foreach (var (stmt, _) in Parser.ParseScript(sql)) results.Add(Execute(stmt, []));
        return results;
    }

    public PreparedStatement Prepare(string sql)
    {
        var script = Parser.ParseScript(sql);
        if (script.Count != 1) throw new BasaltException("Prepare takes exactly one statement");
        return new PreparedStatement(this, script[0].Statement, script[0].ParameterCount, sql);
    }

    internal QueryResult Execute(Statement statement, IReadOnlyList<Value> parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pager = _db.Pager;
        var catalog = _db.Catalog;

        switch (statement)
        {
            case BeginStmt:
                if (_inTransaction) throw new BasaltException("a transaction is already active");
                _db.Lock.EnterWrite(_db.Options.LockTimeout);
                try { pager.BeginTransaction(); }
                catch { _db.Lock.ExitWrite(); throw; }
                _inTransaction = true;
                return QueryResult.ForMessage("BEGIN", 0);

            case CommitStmt:
                if (!_inTransaction) throw new BasaltException("no transaction is active");
                try
                {
                    pager.Commit();
                }
                catch
                {
                    catalog.Reload(); // the pager rolled back; make the schema match
                    throw;
                }
                finally
                {
                    _inTransaction = false;
                    _db.Lock.ExitWrite();
                }
                return QueryResult.ForMessage("COMMIT", 0);

            case RollbackStmt:
                if (!_inTransaction) throw new BasaltException("no transaction is active");
                try
                {
                    pager.Rollback();
                    catalog.Reload();
                }
                finally
                {
                    _inTransaction = false;
                    _db.Lock.ExitWrite();
                }
                return QueryResult.ForMessage("ROLLBACK", 0);
        }

        var executor = new Executor(pager, catalog, parameters);

        if (Executor.IsReadOnly(statement))
        {
            if (_inTransaction) return executor.Execute(statement); // sees its own uncommitted changes
            _db.Lock.EnterRead(_db.Options.LockTimeout);
            try { return executor.Execute(statement); }
            finally { _db.Lock.ExitRead(); }
        }

        if (_inTransaction)
        {
            pager.BeginStatement();
            try
            {
                var result = executor.Execute(statement);
                pager.EndStatement();
                return result;
            }
            catch
            {
                pager.RollbackStatement();
                catalog.Reload();
                throw;
            }
        }

        // Autocommit write.
        _db.Lock.EnterWrite(_db.Options.LockTimeout);
        try
        {
            pager.BeginTransaction();
            QueryResult result;
            try
            {
                result = executor.Execute(statement);
            }
            catch
            {
                pager.Rollback();
                catalog.Reload();
                throw;
            }
            try
            {
                pager.Commit();
            }
            catch
            {
                catalog.Reload();
                throw;
            }
            return result;
        }
        finally
        {
            _db.Lock.ExitWrite();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_inTransaction)
        {
            try
            {
                _db.Pager.Rollback();
                _db.Catalog.Reload();
            }
            finally
            {
                _inTransaction = false;
                _db.Lock.ExitWrite();
            }
        }
        _disposed = true;
        _db.Forget(this);
    }
}
