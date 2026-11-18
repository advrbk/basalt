using Basalt.Catalog;
using Basalt.Sql;
using Basalt.Storage;
using Basalt.Values;

namespace Basalt.Query;

/// <summary>A planned SELECT: the operator tree and the output column names.</summary>
internal sealed record SelectPlan(PlanNode Root, IReadOnlyList<string> ColumnNames);

/// <summary>
/// Turns parsed statements into operator trees. It is a rule-based planner, not a cost-based one:
/// <list type="number">
/// <item>Access path: look at the top-level AND terms of WHERE. A term "column op constant" on an
///       indexed column can drive an index lookup (=) or range scan (&lt;, &lt;=, &gt;, &gt;=).
///       Equality beats ranges, the primary key beats a secondary index, two-sided ranges beat
///       one-sided ones. Otherwise: full table scan.</item>
/// <item>Joins: left-deep, in the order written. If the ON clause has "outer expr = inner.column"
///       and that inner column is indexed, use an index nested-loop join; with any other equality,
///       a hash join; with no equality at all, a nested-loop join.</item>
/// <item>The whole WHERE is still applied as a filter, so the access path only has to return a
///       superset of the matching rows. This keeps index use simple to get right.</item>
/// <item>Then aggregate, HAVING, sort, limit, project.</item>
/// </list>
/// </summary>
internal sealed class Planner(Pager pager, SchemaCatalog catalog, IReadOnlyList<Value> parameters)
{
    private static readonly HashSet<string> RangeOps = ["=", "<", "<=", ">", ">="];

    // ---- access paths ---------------------------------------------------------------------

    private sealed record KeyTerm(IndexInfo Index, string Op, Value Value);

    public AccessNode PlanAccess(TableInfo table, string alias, IEnumerable<Expr> terms)
    {
        var seq = new SeqScanNode(pager, table, alias);
        var schema = seq.Schema;
        var keyTerms = new List<KeyTerm>();

        foreach (var term in terms)
        {
            if (term is not BinaryExpr b || !RangeOps.Contains(b.Op)) continue;
            ColumnExpr? col;
            Expr other;
            string op = b.Op;
            if (b.Left is ColumnExpr lc && IsConstant(b.Right)) { col = lc; other = b.Right; }
            else if (b.Right is ColumnExpr rc && IsConstant(b.Left)) { col = rc; other = b.Left; op = Flip(op); }
            else continue;

            if (!schema.TryResolve(col, out int ordinal)) continue;
            var index = table.Indexes.Where(i => i.Column == ordinal).OrderByDescending(i => i.IsPrimary).FirstOrDefault();
            if (index == null) continue;
            var value = Binder.EvalConstant(other, parameters);
            if (!TryCoerceKey(value, table.Types[ordinal], out var key)) continue;
            keyTerms.Add(new KeyTerm(index, op, key));
        }

        AccessNode best = seq;
        int bestScore = 0;
        foreach (var group in keyTerms.GroupBy(k => k.Index))
        {
            var index = group.Key;
            var eq = group.FirstOrDefault(k => k.Op == "=");
            if (eq != null)
            {
                int score = index.IsPrimary ? 100 : 90;
                if (score > bestScore) { bestScore = score; best = new IndexLookupNode(pager, table, alias, index, eq.Value); }
                continue;
            }

            // Tightest bounds: the largest low bound and the smallest high bound.
            KeyTerm? low = null, high = null;
            foreach (var k in group)
            {
                if (k.Op is ">" or ">=")
                {
                    if (low == null || k.Value.CompareTo(low.Value) > 0 || (k.Value.CompareTo(low.Value) == 0 && k.Op == ">")) low = k;
                }
                else if (high == null || k.Value.CompareTo(high.Value) < 0 || (k.Value.CompareTo(high.Value) == 0 && k.Op == "<")) high = k;
            }
            int rangeScore = (index.IsPrimary ? 50 : 40) + (low != null && high != null ? 5 : 0);
            if (rangeScore > bestScore)
            {
                bestScore = rangeScore;
                best = new IndexRangeNode(pager, table, alias, index,
                    low?.Value, low?.Op == ">=", high?.Value, high?.Op == "<=");
            }
        }
        return best;
    }

    private static bool IsConstant(Expr e) => e is LiteralExpr or ParameterExpr;

    private static string Flip(string op) => op switch
    {
        "<" => ">",
        "<=" => ">=",
        ">" => "<",
        ">=" => "<=",
        _ => op,
    };

    /// <summary>Converts a constant to the column's type for an index key, or says it cannot be used.</summary>
    public static bool TryCoerceKey(Value v, DataType type, out Value key)
    {
        key = v;
        switch (type)
        {
            case DataType.Int when v.Kind == ValueKind.Int:
                return true;
            case DataType.Int when v.Kind == ValueKind.Real:
                double d = v.AsReal;
                if (Math.Floor(d) != d || d < long.MinValue || d >= 9.2233720368547758e18) return false;
                key = Value.FromInt((long)d);
                return true;
            case DataType.Real when v.IsNumeric:
                key = Value.FromReal(v.AsReal);
                return true;
            case DataType.Text when v.Kind == ValueKind.Text:
            case DataType.Bool when v.Kind == ValueKind.Bool:
                return true;
            default:
                return false;
        }
    }

    // ---- SELECT ---------------------------------------------------------------------------

    public SelectPlan PlanSelect(SelectStmt s)
    {
        var whereTerms = Binder.SplitAnd(s.Where);
        PlanNode node;

        if (s.From == null)
        {
            node = new SingleRowNode();
        }
        else
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s.From.Name };
            var first = catalog.GetTable(s.From.Table);
            node = PlanAccess(first, s.From.Name, whereTerms.Where(t => new SeqScanNode(pager, first, s.From.Name).Schema.Covers(t)));

            foreach (var join in s.Joins)
            {
                if (!aliases.Add(join.Table.Name))
                    throw new BasaltException($"table name {join.Table.Name} is used twice; give one of them an alias");
                node = PlanJoin(node, join, whereTerms);
            }
        }

        if (s.Where != null)
        {
            if (Binder.ContainsAggregate(s.Where)) throw new BasaltException("aggregates are not allowed in WHERE; use HAVING");
            node = new FilterNode(node, Binder.Bind(s.Where, node.Schema, parameters), Text(s.Where));
        }

        var input = node.Schema;

        // Expand * and t.* into column references.
        var items = new List<(Expr Expr, string Name)>();
        foreach (var item in s.Items)
        {
            if (item.IsStar)
            {
                if (s.From == null) throw new BasaltException("SELECT * needs a FROM clause");
                bool any = false;
                foreach (var c in input.Columns)
                {
                    if (item.StarTable != null && !string.Equals(c.Table, item.StarTable, StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add((new ColumnExpr(c.Table, c.Name), c.Name));
                    any = true;
                }
                if (!any) throw new BasaltException($"no such table or alias: {item.StarTable}");
                continue;
            }
            string name = item.Alias ?? (item.Expr is ColumnExpr ce ? ce.Column : Text(item.Expr!));
            items.Add((item.Expr!, name));
        }

        var orderExprs = s.OrderBy.Select(o => (Expr: ResolveOutputReference(o.Expr, s.Items, items), o.Descending)).ToList();
        var groupExprs = s.GroupBy.Select(g => ResolveOutputReference(g, s.Items, items)).ToList();
        foreach (var g in groupExprs)
            if (Binder.ContainsAggregate(g)) throw new BasaltException("aggregates are not allowed in GROUP BY");

        bool aggregate = groupExprs.Count > 0 || s.Having != null
            || items.Any(i => Binder.ContainsAggregate(i.Expr)) || orderExprs.Any(o => Binder.ContainsAggregate(o.Expr));

        Func<Expr, BoundExpr> bindOutput;
        if (!aggregate)
        {
            bindOutput = e => Binder.Bind(e, input, parameters);
        }
        else
        {
            var aggCalls = new List<AggregateExpr>();
            foreach (var i in items) Binder.CollectAggregates(i.Expr, aggCalls);
            Binder.CollectAggregates(s.Having, aggCalls);
            foreach (var o in orderExprs) Binder.CollectAggregates(o.Expr, aggCalls);

            var groupCanon = groupExprs.Select(g => Binder.Canonical(g, input)).ToList();
            var aggCanon = new List<string>();
            var specs = new List<AggregateSpec>();
            foreach (var a in aggCalls)
            {
                string canon = Binder.Canonical(a, input);
                if (aggCanon.Contains(canon)) continue;
                aggCanon.Add(canon);
                specs.Add(new AggregateSpec(a.Function, a.Argument == null ? null : Binder.Bind(a.Argument, input, parameters), Text(a)));
            }

            var aggSchema = new RowSchema(
                groupExprs.Select(g => new ColumnBinding(null, Text(g), null))
                    .Concat(specs.Select(sp => new ColumnBinding(null, sp.Text, null))).ToList());
            node = new AggregateNode(node,
                groupExprs.Select(g => Binder.Bind(g, input, parameters)).ToList(),
                groupExprs.Select(Text).ToList(), specs, aggSchema);

            BoundExpr BindPostAggregate(Expr e)
            {
                string canon = Binder.Canonical(e, input);
                int gi = groupCanon.IndexOf(canon);
                if (gi >= 0) return new ColumnRefExpr(gi);
                switch (e)
                {
                    case AggregateExpr:
                        return new ColumnRefExpr(groupExprs.Count + aggCanon.IndexOf(canon));
                    case LiteralExpr l:
                        return new ConstExpr(l.Value);
                    case ParameterExpr p:
                        return new ConstExpr(Binder.Param(p, parameters));
                    case ColumnExpr c:
                        input.Resolve(c); // reports unknown/ambiguous columns first
                        throw new BasaltException($"column {c} must appear in GROUP BY or inside an aggregate");
                    case UnaryExpr u:
                        return new UnaryOpExpr(u.Op, BindPostAggregate(u.Operand));
                    case BinaryExpr b:
                        return new BinaryOpExpr(b.Op, BindPostAggregate(b.Left), BindPostAggregate(b.Right));
                    case IsNullExpr n:
                        return new IsNullTestExpr(BindPostAggregate(n.Operand), n.Negated);
                    case LikeExpr l:
                        return new LikeTestExpr(BindPostAggregate(l.Operand), BindPostAggregate(l.Pattern), l.Negated);
                    case FunctionExpr f:
                        return new FunctionCallExpr(f.Function, f.Arguments.Select(BindPostAggregate).ToArray());
                    default:
                        throw new BasaltException($"unsupported expression {e}");
                }
            }

            bindOutput = BindPostAggregate;
            if (s.Having != null) node = new FilterNode(node, BindPostAggregate(s.Having), "(having) " + Text(s.Having));
        }

        if (orderExprs.Count > 0)
        {
            var keys = orderExprs.Select(o => (bindOutput(o.Expr), o.Descending)).ToList();
            node = new SortNode(node, keys, string.Join(", ", orderExprs.Select(o => Text(o.Expr) + (o.Descending ? " DESC" : ""))));
        }

        if (s.Limit != null)
        {
            long limit = ConstantInt(s.Limit, "LIMIT");
            long offset = s.Offset == null ? 0 : ConstantInt(s.Offset, "OFFSET");
            node = new LimitNode(node, limit, offset);
        }

        var outSchema = new RowSchema(items.Select(i => new ColumnBinding(null, i.Name, null)).ToList());
        node = new ProjectNode(node, items.Select(i => bindOutput(i.Expr)).ToList(), outSchema,
            string.Join(", ", items.Select(i => i.Name)));
        return new SelectPlan(node, items.Select(i => i.Name).ToList());
    }

    private PlanNode PlanJoin(PlanNode outer, JoinClause join, List<Expr> whereTerms)
    {
        var innerTable = catalog.GetTable(join.Table.Table);
        string innerAlias = join.Table.Name;
        var innerSchema = new SeqScanNode(pager, innerTable, innerAlias).Schema;
        var onTerms = Binder.SplitAnd(join.On);
        if (onTerms.Any(Binder.ContainsAggregate)) throw new BasaltException("aggregates are not allowed in ON");

        // Find "outer side = inner side".
        for (int i = 0; i < onTerms.Count; i++)
        {
            if (onTerms[i] is not BinaryExpr { Op: "=" } eq) continue;
            Expr? outerSide = null, innerSide = null;
            if (OnlyCovers(outer.Schema, innerSchema, eq.Left) && OnlyCovers(innerSchema, outer.Schema, eq.Right)) { outerSide = eq.Left; innerSide = eq.Right; }
            else if (OnlyCovers(outer.Schema, innerSchema, eq.Right) && OnlyCovers(innerSchema, outer.Schema, eq.Left)) { outerSide = eq.Right; innerSide = eq.Left; }
            if (outerSide == null || innerSide == null) continue;

            var rest = onTerms.Where((_, j) => j != i).ToList();
            var outerKey = Binder.Bind(outerSide, outer.Schema, parameters);
            PlanNode joined;

            IndexInfo? index = null;
            if (innerSide is ColumnExpr ic && innerSchema.TryResolve(ic, out int ord))
                index = innerTable.Indexes.Where(x => x.Column == ord).OrderByDescending(x => x.IsPrimary).FirstOrDefault();

            if (index != null)
            {
                joined = new IndexJoinNode(outer, pager, innerTable, innerAlias, index, outerKey, Text(eq));
            }
            else
            {
                // Hash join; WHERE terms that only touch the inner table can filter its input.
                var innerTerms = whereTerms.Where(t => innerSchema.Covers(t) && !outer.Schema.Covers(t)).ToList();
                PlanNode innerNode = PlanAccess(innerTable, innerAlias, innerTerms);
                var innerFilter = Binder.JoinAnd(innerTerms);
                if (innerFilter != null)
                    innerNode = new FilterNode(innerNode, Binder.Bind(innerFilter, innerSchema, parameters), Text(innerFilter));
                joined = new HashJoinNode(outer, innerNode, outerKey, Binder.Bind(innerSide, innerSchema, parameters), Text(eq));
            }

            var restExpr = Binder.JoinAnd(rest);
            if (restExpr != null)
                joined = new FilterNode(joined, Binder.Bind(restExpr, joined.Schema, parameters), Text(restExpr));
            return joined;
        }

        var nlInner = new SeqScanNode(pager, innerTable, innerAlias);
        var combined = RowSchema.Concat(outer.Schema, nlInner.Schema);
        return new NestedLoopJoinNode(outer, nlInner, Binder.Bind(join.On, combined, parameters), Text(join.On));
    }

    private static bool OnlyCovers(RowSchema mine, RowSchema theirs, Expr e) =>
        mine.Covers(e) && !theirs.Covers(e) && HasColumn(e);

    private static bool HasColumn(Expr e) => e switch
    {
        ColumnExpr => true,
        UnaryExpr u => HasColumn(u.Operand),
        BinaryExpr b => HasColumn(b.Left) || HasColumn(b.Right),
        IsNullExpr n => HasColumn(n.Operand),
        LikeExpr l => HasColumn(l.Operand) || HasColumn(l.Pattern),
        FunctionExpr f => f.Arguments.Any(HasColumn),
        _ => false,
    };

    /// <summary>ORDER BY / GROUP BY may name a select-list alias or a 1-based column position.</summary>
    private static Expr ResolveOutputReference(Expr e, IReadOnlyList<SelectItem> raw, List<(Expr Expr, string Name)> items)
    {
        if (e is LiteralExpr { Value.Kind: ValueKind.Int } lit)
        {
            long pos = lit.Value.AsInt;
            if (pos < 1 || pos > items.Count) throw new BasaltException($"column position {pos} is out of range");
            return items[(int)pos - 1].Expr;
        }
        if (e is ColumnExpr { Table: null } c)
        {
            var aliased = raw.FirstOrDefault(r => r.Alias != null && string.Equals(r.Alias, c.Column, StringComparison.OrdinalIgnoreCase));
            if (aliased?.Expr != null) return aliased.Expr;
        }
        return e;
    }

    private long ConstantInt(Expr e, string what)
    {
        var v = Binder.EvalConstant(e, parameters);
        if (v.Kind != ValueKind.Int || v.AsInt < 0) throw new BasaltException($"{what} must be a non-negative integer");
        return v.AsInt;
    }

    private string Text(Expr e)
    {
        string s = Binder.Substitute(e, parameters).ToString();
        return s.Length > 1 && s[0] == '(' && s[^1] == ')' && Balanced(s[1..^1]) ? s[1..^1] : s;
    }

    private static bool Balanced(string s)
    {
        int depth = 0;
        bool inString = false;
        foreach (char ch in s)
        {
            if (ch == '\'') inString = !inString;
            if (inString) continue;
            if (ch == '(') depth++;
            else if (ch == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }
}
