using System.Globalization;

namespace Basalt.Values;

/// <summary>The four column types Basalt supports.</summary>
public enum DataType : byte
{
    Int = 1,
    Text = 2,
    Real = 3,
    Bool = 4,
}

public enum ValueKind : byte
{
    Null = 0,
    Int = 1,
    Text = 2,
    Real = 3,
    Bool = 4,
}

/// <summary>
/// A single SQL value. A small immutable struct so rows can be plain <c>Value[]</c> arrays.
/// Ordering: NULL first, then numbers (INT and REAL compare numerically), then TEXT
/// (Unicode code point order, which matches the UTF-8 byte order used by index keys), then BOOL.
/// </summary>
public readonly struct Value : IEquatable<Value>, IComparable<Value>
{
    private readonly long _bits;     // INT value, REAL bits, or BOOL as 0/1
    private readonly string? _text;

    public ValueKind Kind { get; }

    private Value(ValueKind kind, long bits, string? text)
    {
        Kind = kind;
        _bits = bits;
        _text = text;
    }

    public static readonly Value Null = default;
    public static readonly Value True = new(ValueKind.Bool, 1, null);
    public static readonly Value False = new(ValueKind.Bool, 0, null);

    public static Value FromInt(long v) => new(ValueKind.Int, v, null);
    public static Value FromReal(double v)
    {
        if (double.IsNaN(v)) throw new BasaltException("REAL value is NaN");
        if (v == 0) v = 0.0; // fold -0.0 into 0.0 so equal values have one encoding
        return new(ValueKind.Real, BitConverter.DoubleToInt64Bits(v), null);
    }
    public static Value FromText(string v) => new(ValueKind.Text, 0, v ?? throw new ArgumentNullException(nameof(v)));
    public static Value FromBool(bool v) => v ? True : False;

    public bool IsNull => Kind == ValueKind.Null;
    public bool IsNumeric => Kind is ValueKind.Int or ValueKind.Real;

    public long AsInt => Kind == ValueKind.Int ? _bits : throw TypeError("INT");
    public double AsReal => Kind switch
    {
        ValueKind.Real => BitConverter.Int64BitsToDouble(_bits),
        ValueKind.Int => _bits,
        _ => throw TypeError("REAL"),
    };
    public string AsText => Kind == ValueKind.Text ? _text! : throw TypeError("TEXT");
    public bool AsBool => Kind == ValueKind.Bool ? _bits != 0 : throw TypeError("BOOL");

    private BasaltException TypeError(string wanted) =>
        new($"expected a {wanted} value but got {KindName(Kind)}");

    public static string KindName(ValueKind k) => k switch
    {
        ValueKind.Null => "NULL",
        ValueKind.Int => "INT",
        ValueKind.Text => "TEXT",
        ValueKind.Real => "REAL",
        ValueKind.Bool => "BOOL",
        _ => k.ToString(),
    };

    /// <summary>Converts a CLR object (as passed to parameterised queries) into a Value.</summary>
    public static Value FromObject(object? o) => o switch
    {
        null => Null,
        Value v => v,
        long l => FromInt(l),
        int i => FromInt(i),
        short s => FromInt(s),
        byte b => FromInt(b),
        double d => FromReal(d),
        float f => FromReal(f),
        decimal m => FromReal((double)m),
        string s => FromText(s),
        bool b => FromBool(b),
        _ => throw new BasaltException($"cannot use a {o.GetType().Name} as a SQL value"),
    };

    /// <summary>The CLR value (long, double, string, bool or null).</summary>
    public object? ToObject() => Kind switch
    {
        ValueKind.Null => null,
        ValueKind.Int => _bits,
        ValueKind.Real => AsReal,
        ValueKind.Text => _text,
        ValueKind.Bool => _bits != 0,
        _ => null,
    };

    private static int KindRank(ValueKind k) => k switch
    {
        ValueKind.Null => 0,
        ValueKind.Int or ValueKind.Real => 1,
        ValueKind.Text => 2,
        ValueKind.Bool => 3,
        _ => 4,
    };

    /// <summary>Total order used for sorting and grouping (NULLs sort first).</summary>
    public int CompareTo(Value other)
    {
        int ra = KindRank(Kind), rb = KindRank(other.Kind);
        if (ra != rb) return ra.CompareTo(rb);
        switch (Kind)
        {
            case ValueKind.Null:
                return 0;
            case ValueKind.Int when other.Kind == ValueKind.Int:
                return _bits.CompareTo(other._bits);
            case ValueKind.Int:
            case ValueKind.Real:
                return AsReal.CompareTo(other.AsReal);
            case ValueKind.Text:
                return CompareText(_text!, other._text!);
            case ValueKind.Bool:
                return _bits.CompareTo(other._bits);
            default:
                return 0;
        }
    }

    /// <summary>
    /// Compares strings by Unicode code point. Plain ordinal comparison of UTF-16 code units
    /// puts U+E000..U+FFFF after surrogate pairs, which would disagree with the UTF-8 byte
    /// order of index keys. The fix-up below maps code units so the two orders match.
    /// </summary>
    public static int CompareText(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            return Fix(x).CompareTo(Fix(y));
        }
        return a.Length.CompareTo(b.Length);

        static int Fix(char c) => c >= 0xE000 ? c - 0x800 : c >= 0xD800 ? c + 0x2000 : c;
    }

    public bool Equals(Value other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is Value v && Equals(v);

    public override int GetHashCode()
    {
        switch (Kind)
        {
            case ValueKind.Null: return 0;
            case ValueKind.Int: return _bits.GetHashCode();
            case ValueKind.Real:
                double d = AsReal;
                // Integral reals must hash like the equal INT.
                if (d >= long.MinValue && d <= long.MaxValue && Math.Floor(d) == d) return ((long)d).GetHashCode();
                return d.GetHashCode();
            case ValueKind.Text: return string.GetHashCode(_text!, StringComparison.Ordinal);
            default: return HashCode.Combine(Kind, _bits);
        }
    }

    public static bool operator ==(Value a, Value b) => a.Equals(b);
    public static bool operator !=(Value a, Value b) => !a.Equals(b);

    /// <summary>Display form used by the REPL and EXPLAIN.</summary>
    public override string ToString() => Kind switch
    {
        ValueKind.Null => "NULL",
        ValueKind.Int => _bits.ToString(CultureInfo.InvariantCulture),
        ValueKind.Real => FormatReal(AsReal),
        ValueKind.Text => _text!,
        ValueKind.Bool => _bits != 0 ? "true" : "false",
        _ => "?",
    };

    /// <summary>SQL literal form (strings quoted), used when printing expressions.</summary>
    public string ToSqlLiteral() => Kind == ValueKind.Text ? "'" + _text!.Replace("'", "''") + "'" : ToString();

    private static string FormatReal(double d)
    {
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        if (s.IndexOfAny(['.', 'E', 'I', 'N']) < 0) s += ".0";
        return s;
    }
}
