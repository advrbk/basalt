using System.Globalization;
using System.Text;

namespace Basalt.Sql;

public enum TokenKind
{
    Identifier,     // names and keywords (keywords are recognised by the parser)
    QuotedIdentifier,
    Integer,
    Real,
    String,
    Parameter,      // ?
    Symbol,         // ( ) , ; . * + - / % = <> != < <= > >= ||
    End,
}

public readonly record struct Token(TokenKind Kind, string Text, int Line, int Column)
{
    public override string ToString() => Kind switch
    {
        TokenKind.End => "end of input",
        TokenKind.String => $"'{Text}'",
        _ => $"'{Text}'",
    };
}

/// <summary>Hand-written tokenizer: one pass over the text, tracking line and column for errors.</summary>
public static class Lexer
{
    public static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, lineStart = 0;

        while (true)
        {
            // Skip whitespace and -- comments.
            while (i < sql.Length)
            {
                char c = sql[i];
                if (c == '\n') { i++; line++; lineStart = i; }
                else if (char.IsWhiteSpace(c) || c == '\uFEFF') i++; // also skip a byte order mark
                else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    while (i < sql.Length && sql[i] != '\n') i++;
                }
                else break;
            }

            int col = i - lineStart + 1;
            if (i >= sql.Length)
            {
                tokens.Add(new Token(TokenKind.End, "", line, col));
                return tokens;
            }

            char ch = sql[i];
            int start = i;

            if (char.IsLetter(ch) || ch == '_')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Identifier, sql[start..i], line, col));
            }
            else if (char.IsDigit(ch) || (ch == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
            {
                bool isReal = false;
                while (i < sql.Length && char.IsDigit(sql[i])) i++;
                if (i < sql.Length && sql[i] == '.')
                {
                    isReal = true;
                    i++;
                    while (i < sql.Length && char.IsDigit(sql[i])) i++;
                }
                if (i < sql.Length && (sql[i] == 'e' || sql[i] == 'E'))
                {
                    int save = i;
                    i++;
                    if (i < sql.Length && (sql[i] == '+' || sql[i] == '-')) i++;
                    if (i < sql.Length && char.IsDigit(sql[i]))
                    {
                        isReal = true;
                        while (i < sql.Length && char.IsDigit(sql[i])) i++;
                    }
                    else i = save;
                }
                if (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_'))
                    throw new SqlSyntaxException($"bad number '{sql[start..(i + 1)]}'", line, col);
                tokens.Add(new Token(isReal ? TokenKind.Real : TokenKind.Integer, sql[start..i], line, col));
            }
            else if (ch == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= sql.Length) throw new SqlSyntaxException("unterminated string literal", line, col);
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (sql[i] == '\n') { line++; lineStart = i + 1; }
                    sb.Append(sql[i++]);
                }
                tokens.Add(new Token(TokenKind.String, sb.ToString(), line, col));
            }
            else if (ch == '"')
            {
                i++;
                int nameStart = i;
                while (i < sql.Length && sql[i] != '"') i++;
                if (i >= sql.Length) throw new SqlSyntaxException("unterminated quoted identifier", line, col);
                if (i == nameStart) throw new SqlSyntaxException("empty quoted identifier", line, col);
                tokens.Add(new Token(TokenKind.QuotedIdentifier, sql[nameStart..i], line, col));
                i++;
            }
            else if (ch == '?')
            {
                i++;
                tokens.Add(new Token(TokenKind.Parameter, "?", line, col));
            }
            else
            {
                string? sym = null;
                if (i + 1 < sql.Length)
                {
                    string two = sql.Substring(i, 2);
                    if (two is "<=" or ">=" or "<>" or "!=" or "||" or "==") sym = two;
                }
                if (sym == null && "(),;.*+-/%=<>".Contains(ch)) sym = ch.ToString();
                if (sym == null) throw new SqlSyntaxException($"unexpected character '{ch}'", line, col);
                i += sym.Length;
                tokens.Add(new Token(TokenKind.Symbol, sym == "==" ? "=" : sym, line, col));
            }
        }
    }

    internal static long ParseInteger(Token t)
    {
        if (!long.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long v))
            throw new SqlSyntaxException($"integer {t.Text} is out of range", t.Line, t.Column);
        return v;
    }

    internal static double ParseReal(Token t) =>
        double.Parse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
}
