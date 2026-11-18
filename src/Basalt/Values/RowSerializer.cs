using System.Buffers.Binary;
using System.Text;

namespace Basalt.Values;

/// <summary>
/// Row format on a heap page:
/// <code>
/// column count (2) | null bitmap (1 bit per column) | non-null values in column order
/// INT 8 bytes, REAL 8 bytes, BOOL 1 byte, TEXT length (2) + UTF-8 bytes
/// </code>
/// The column count lets the decoder catch rows that do not match the schema.
/// </summary>
internal static class RowSerializer
{
    public static byte[] Serialize(IReadOnlyList<DataType> types, ReadOnlySpan<Value> values)
    {
        int n = types.Count;
        if (values.Length != n) throw new ArgumentException("value count does not match column count");
        int bitmapBytes = (n + 7) / 8;
        int size = 2 + bitmapBytes;
        for (int i = 0; i < n; i++)
        {
            if (values[i].IsNull) continue;
            size += types[i] switch
            {
                DataType.Int or DataType.Real => 8,
                DataType.Bool => 1,
                DataType.Text => 2 + Encoding.UTF8.GetByteCount(values[i].AsText),
                _ => throw new ArgumentOutOfRangeException(),
            };
        }

        var buf = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)n);
        int off = 2 + bitmapBytes;
        for (int i = 0; i < n; i++)
        {
            var v = values[i];
            if (v.IsNull)
            {
                buf[2 + i / 8] |= (byte)(1 << (i % 8));
                continue;
            }
            switch (types[i])
            {
                case DataType.Int:
                    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off), v.AsInt);
                    off += 8;
                    break;
                case DataType.Real:
                    BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(off), v.AsReal);
                    off += 8;
                    break;
                case DataType.Bool:
                    buf[off++] = v.AsBool ? (byte)1 : (byte)0;
                    break;
                case DataType.Text:
                    int len = Encoding.UTF8.GetBytes(v.AsText, buf.AsSpan(off + 2));
                    if (len > ushort.MaxValue) throw new ConstraintException("TEXT value too long");
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), (ushort)len);
                    off += 2 + len;
                    break;
            }
        }
        return buf;
    }

    public static Value[] Deserialize(IReadOnlyList<DataType> types, ReadOnlySpan<byte> row)
    {
        int n = types.Count;
        if (row.Length < 2 || BinaryPrimitives.ReadUInt16LittleEndian(row) != n)
            throw new CorruptionException("row does not match its table's column count");
        int bitmapBytes = (n + 7) / 8;
        int off = 2 + bitmapBytes;
        var values = new Value[n];
        for (int i = 0; i < n; i++)
        {
            if ((row[2 + i / 8] & (1 << (i % 8))) != 0) continue; // NULL
            switch (types[i])
            {
                case DataType.Int:
                    values[i] = Value.FromInt(BinaryPrimitives.ReadInt64LittleEndian(row.Slice(off)));
                    off += 8;
                    break;
                case DataType.Real:
                    values[i] = Value.FromReal(BinaryPrimitives.ReadDoubleLittleEndian(row.Slice(off)));
                    off += 8;
                    break;
                case DataType.Bool:
                    values[i] = Value.FromBool(row[off++] != 0);
                    break;
                case DataType.Text:
                    int len = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(off));
                    values[i] = Value.FromText(Encoding.UTF8.GetString(row.Slice(off + 2, len)));
                    off += 2 + len;
                    break;
            }
        }
        if (off != row.Length) throw new CorruptionException("row has trailing bytes");
        return values;
    }
}
