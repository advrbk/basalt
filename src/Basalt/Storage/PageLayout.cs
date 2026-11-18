using System.Buffers.Binary;

namespace Basalt.Storage;

/// <summary>Byte 0 of every page says what kind of page it is.</summary>
internal enum PageType : byte
{
    Unused = 0,
    Header = 1,
    Catalog = 2,
    Heap = 3,
    BTreeLeaf = 4,
    BTreeInternal = 5,
}

/// <summary>
/// Page 0 of the data file. Layout (little endian):
/// <code>
///  0      type (Header)
///  8..15  magic "BASALTDB"
/// 16..19  format version
/// 20..23  page size
/// 24..27  page count (pages 0..count-1 are allocated)
/// 28..31  first catalog page
/// </code>
/// </summary>
internal static class HeaderPage
{
    public const int FormatVersion = 1;
    private static ReadOnlySpan<byte> Magic => "BASALTDB"u8;

    public static void Initialize(Span<byte> page, int catalogRoot)
    {
        page.Clear();
        page[0] = (byte)PageType.Header;
        Magic.CopyTo(page.Slice(8, 8));
        BinaryPrimitives.WriteInt32LittleEndian(page.Slice(16), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(page.Slice(20), Pager.PageSize);
        SetPageCount(page, 1);
        SetCatalogRoot(page, catalogRoot);
    }

    public static void Validate(ReadOnlySpan<byte> page)
    {
        if (page[0] != (byte)PageType.Header || !page.Slice(8, 8).SequenceEqual(Magic))
            throw new CorruptionException("not a Basalt database (bad header magic)");
        int version = BinaryPrimitives.ReadInt32LittleEndian(page.Slice(16));
        if (version != FormatVersion)
            throw new CorruptionException($"unsupported format version {version}");
        int pageSize = BinaryPrimitives.ReadInt32LittleEndian(page.Slice(20));
        if (pageSize != Pager.PageSize)
            throw new CorruptionException($"database uses page size {pageSize}, this build uses {Pager.PageSize}");
    }

    public static int GetPageCount(ReadOnlySpan<byte> page) => BinaryPrimitives.ReadInt32LittleEndian(page.Slice(24));
    public static void SetPageCount(Span<byte> page, int count) => BinaryPrimitives.WriteInt32LittleEndian(page.Slice(24), count);
    public static int GetCatalogRoot(ReadOnlySpan<byte> page) => BinaryPrimitives.ReadInt32LittleEndian(page.Slice(28));
    public static void SetCatalogRoot(Span<byte> page, int id) => BinaryPrimitives.WriteInt32LittleEndian(page.Slice(28), id);
}
