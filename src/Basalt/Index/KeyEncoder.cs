using System.Buffers.Binary;
using System.Text;
using Basalt.Storage;
using Basalt.Values;

namespace Basalt.Index;

/// <summary>
/// Turns values into byte strings whose plain byte-by-byte order equals the SQL value order.
/// The B+tree then only ever compares bytes (memcmp), whatever the column type.
/// <code>
/// NULL           00
/// INT            01 + 8 bytes big endian with the sign bit flipped
/// REAL           01 + IEEE bits (all bits flipped if negative, else sign bit flipped), big endian
/// BOOL           01 + 00 / 01
/// TEXT           01 + UTF-8 with 00 escaped as 00 FF, terminated by 00 00
/// </code>
/// Every encoding is self-delimiting, so a secondary index key can simply append the row id.
/// </summary>
internal static class KeyEncoder
{
    public static byte[] Encode(Value v, DataType type)
    {
        var buf = new byte[EncodedLength(v, type)];
        Write(buf, v, type);
        return buf;
    }

    /// <summary>Key for a secondary index entry: encoded value followed by the row id.</summary>
    public static byte[] EncodeWithRowId(Value v, DataType type, RowId rowId)
    {
        int len = EncodedLength(v, type);
        var buf = new byte[len + RowId.EncodedSize];
        Write(buf, v, type);
        rowId.WriteTo(buf.AsSpan(len));
        return buf;
    }

    private static int EncodedLength(Value v, DataType type)
    {
        if (v.IsNull) return 1;
        return type switch
        {
            DataType.Int or DataType.Real => 9,
            DataType.Bool => 2,
            DataType.Text => 1 + Utf8Escaped(v.AsText) + 2,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    private static int Utf8Escaped(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        // Only U+0000 encodes to a 00 byte in UTF-8, and each one needs an extra FF.
        foreach (char c in s) if (c == '\0') n++;
        return n;
    }

    private static void Write(Span<byte> buf, Value v, DataType type)
    {
        if (v.IsNull)
        {
            buf[0] = 0x00;
            return;
        }
        buf[0] = 0x01;
        switch (type)
        {
            case DataType.Int:
                BinaryPrimitives.WriteUInt64BigEndian(buf.Slice(1), (ulong)v.AsInt ^ 0x8000_0000_0000_0000UL);
                break;
            case DataType.Real:
                long bits = BitConverter.DoubleToInt64Bits(v.AsReal == 0 ? 0.0 : v.AsReal);
                ulong u = bits < 0 ? ~(ulong)bits : (ulong)bits ^ 0x8000_0000_0000_0000UL;
                BinaryPrimitives.WriteUInt64BigEndian(buf.Slice(1), u);
                break;
            case DataType.Bool:
                buf[1] = v.AsBool ? (byte)1 : (byte)0;
                break;
            case DataType.Text:
                var raw = Encoding.UTF8.GetBytes(v.AsText);
                int o = 1;
                foreach (var b in raw)
                {
                    buf[o++] = b;
                    if (b == 0) buf[o++] = 0xFF;
                }
                buf[o++] = 0x00;
                buf[o] = 0x00;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    /// <summary>Upper bound for "every key starting with this prefix": prefix + FF * n.</summary>
    public static byte[] PadHigh(byte[] prefix, int n)
    {
        var r = new byte[prefix.Length + n];
        prefix.CopyTo(r, 0);
        r.AsSpan(prefix.Length).Fill(0xFF);
        return r;
    }
}
