using System.Text;
using Basalt.Values;

namespace Basalt.Cli;

/// <summary>Renders a result as an ASCII table with numbers right-aligned.</summary>
internal static class TablePrinter
{
    public static string Render(QueryResult result, int maxRows = 1000, int maxWidth = 60)
    {
        var cols = result.Columns;
        var rows = result.Rows.Take(maxRows).ToList();
        var cells = rows.Select(r => r.Select(v => Clip(v.ToString(), maxWidth)).ToArray()).ToList();
        var numeric = new bool[cols.Count];
        for (int c = 0; c < cols.Count; c++)
            numeric[c] = rows.Count > 0 && rows.All(r => r[c].IsNumeric || r[c].IsNull);

        var widths = new int[cols.Count];
        for (int c = 0; c < cols.Count; c++)
        {
            widths[c] = Clip(cols[c], maxWidth).Length;
            foreach (var row in cells) widths[c] = Math.Max(widths[c], row[c].Length);
        }

        var sb = new StringBuilder();
        string border = "+" + string.Join("+", widths.Select(w => new string('-', w + 2))) + "+";
        sb.AppendLine(border);
        sb.Append('|');
        for (int c = 0; c < cols.Count; c++) sb.Append(' ').Append(Clip(cols[c], maxWidth).PadRight(widths[c])).Append(" |");
        sb.AppendLine();
        sb.AppendLine(border);
        foreach (var row in cells)
        {
            sb.Append('|');
            for (int c = 0; c < cols.Count; c++)
                sb.Append(' ').Append(numeric[c] ? row[c].PadLeft(widths[c]) : row[c].PadRight(widths[c])).Append(" |");
            sb.AppendLine();
        }
        sb.AppendLine(border);
        if (result.Rows.Count > maxRows) sb.AppendLine($"... {result.Rows.Count - maxRows} more rows not shown");
        return sb.ToString();
    }

    /// <summary>EXPLAIN output reads better without a box around it.</summary>
    public static string RenderPlan(QueryResult result) =>
        string.Join(Environment.NewLine, result.Rows.Select(r => "  " + r[0])) + Environment.NewLine;

    private static string Clip(string s, int max)
    {
        s = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", " ");
        return s.Length <= max ? s : s[..(max - 3)] + "...";
    }
}
