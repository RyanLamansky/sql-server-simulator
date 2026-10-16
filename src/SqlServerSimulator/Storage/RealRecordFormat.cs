namespace SqlServerSimulator.Storage;

/// <summary>
/// The sizes of the records real stores a row as — a data record, a
/// nonclustered index's leaf record and an index's non-leaf record — and where
/// a row's variable-length values go: in the row, off it into the LOB
/// allocation unit, or into the row-overflow unit, each measured from the
/// values' lengths alone. <see cref="RealPageLayout"/>, <see cref="LockPageMap"/>
/// and <see cref="StorageLayout"/> all size records here.
/// </summary>
/// <remarks>
/// Probed 2026-10-16 against SQL Server 2025 through
/// <c>sys.dm_db_index_physical_stats</c>' record sizes: a <c>(max)</c> or
/// <c>xml</c> value stays in the row up to 8,000 bytes while the row fits
/// 8,060, real pushing the longest value off until it does; an off-row
/// <c>(max)</c> value is cut into 8,040-byte pieces, each a LOB record 14
/// bytes longer, the row keeping a 12-byte root plus 12 per piece for up to
/// five pieces and a 24-byte pointer to a LOB root record of 20 bytes plus 16
/// per piece past that; a value whose length is a whole number of pieces
/// leaves one LOB page empty past its last; <c>large value types out of row</c>
/// and <c>text</c>, <c>ntext</c> and <c>image</c> without <c>text in row</c>
/// keep a 16-byte pointer to an 84-byte root, which holds a value of up to 64
/// bytes itself and points to the pieces of a longer one; a bounded value
/// pushed off goes to a row-overflow record 14 bytes longer than it, the row
/// keeping 24 bytes.
/// </remarks>
internal static class RealRecordFormat
{
    /// <summary>The bytes a page holds records and slots in: 8,192 less its 96-byte header.</summary>
    public const int PageBytes = 8096;

    /// <summary>The longest record a row may take in its page.</summary>
    public const int MaxRecordBytes = 8060;

    /// <summary>The longest value a <c>(max)</c> column keeps in the row.</summary>
    public const int MaxInRowValueBytes = 8000;

    /// <summary>The bytes of a value each LOB data record carries.</summary>
    public const int LobPieceBytes = 8040;

    /// <summary>The header a LOB or row-overflow record adds to the bytes it carries.</summary>
    public const int LobRecordOverhead = 14;

    /// <summary>The row-versioning tag a row written while snapshot isolation or <c>READ_COMMITTED_SNAPSHOT</c> is on carries.</summary>
    public const int VersioningTagBytes = 14;

    /// <summary>The in-row pointer of a value pushed to the row-overflow unit.</summary>
    private const int OverflowPointerBytes = 24;

    /// <summary>The in-row pointer of a value kept off the row by a table option, or a <c>text</c> value.</summary>
    private const int TextPointerBytes = 16;

    /// <summary>The root record a value kept off the row by a table option starts from.</summary>
    private const int TextRootBytes = 84;

    /// <summary>The longest value such a root holds itself.</summary>
    private const int TextRootInlineBytes = 64;

    /// <summary>The most pieces an in-row root points to directly.</summary>
    private const int InlineRootPieces = 5;

    /// <summary>
    /// The bytes of a data record of <paramref name="columns"/> whose values
    /// take <paramref name="inRow"/> bytes in the row (-1 for NULL): a
    /// four-byte header, the fixed-length columns — every eight bits sharing a
    /// byte — the column count and NULL bitmap, and, when a variable-length
    /// column holds a value of a byte or more, the count and end offset of each
    /// up to the last that does and their data — real keeps no offset for a
    /// trailing empty value (probed 2026-10-16 against SQL Server 2025) — then
    /// the uniquifier of a non-unique clustered key's repeated value and the
    /// versioning tag.
    /// </summary>
    public static int DataRecordBytes(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<int> inRow, bool versioned, bool uniquified)
    {
        var fixedBytes = 0;
        var bits = 0;
        var variable = 0;
        var offsets = 0;
        var data = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            var type = columns[i].Type;
            if (type == SqlType.Bit)
            {
                bits++;
                continue;
            }
            if (type.IsFixedLength)
            {
                fixedBytes += type.FixedLength;
                continue;
            }
            variable++;
            if (inRow[i] <= 0)
                continue;
            offsets = variable;
            data += inRow[i];
        }
        if (uniquified)
        {
            offsets++;
            data += 4;
        }
        var bytes = 4 + fixedBytes + ((bits + 7) / 8) + 2 + ((columns.Length + 7) / 8);
        if (offsets > 0)
            bytes += 2 + (2 * offsets) + data;
        return versioned ? bytes + VersioningTagBytes : bytes;
    }

    /// <summary>
    /// The bytes of a nonclustered index's leaf record of the stored columns
    /// <paramref name="leaf"/>, whose values take <paramref name="inRow"/>
    /// bytes in the row, indexed as <paramref name="leaf"/> is: a status byte,
    /// the fixed-length columns — every eight bits sharing a byte — and a
    /// heap's eight-byte RID, the column count and NULL bitmap, which the
    /// record always carries, and, when a variable-length column holds a
    /// value, the count and end offset of each up to the last that does and
    /// their data, a uniquifier the row locator carries counting as one
    /// (probed 2026-10-16 against SQL Server 2025 with <c>%%physloc%%</c>
    /// through the index: 578 rows of an <c>int</c> key over an <c>int</c>
    /// clustered key to a page, 449 over a <c>bigint</c> one or a heap, 337 of
    /// a ten-character <c>varchar</c>, 71 with a <c>char(100)</c>
    /// <c>INCLUDE</c>).
    /// </summary>
    public static int IndexLeafRecordBytes(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<int> leaf, ReadOnlySpan<int> inRow, bool heapLocator, bool uniquified)
    {
        var (fixedBytes, bits, offsets, data) = Measure(columns, leaf, inRow, uniquified, omitVariable: false);
        if (heapLocator)
            fixedBytes += 8;
        var count = leaf.Length + (heapLocator ? 1 : 0) + (uniquified ? 1 : 0);
        var bytes = 1 + fixedBytes + ((bits + 7) / 8) + 2 + ((count + 7) / 8);
        if (offsets > 0)
            bytes += 2 + (2 * offsets) + data;
        return bytes;
    }

    /// <summary>
    /// The bytes of an index's non-leaf record naming a child page by the
    /// stored columns <paramref name="node"/> — the key, and for a non-unique
    /// nonclustered index the row locator — whose values take
    /// <paramref name="lengths"/> bytes: a status byte, the fixed-length
    /// columns and a heap's eight-byte RID, the six-byte child page pointer,
    /// the column count and NULL bitmap only when a column may be NULL, and the
    /// variable-length block as a leaf record's, a uniquifier counting as one
    /// of its columns; the first record of a level an insert grew keeps no key
    /// past its fixed part (probed 2026-10-16 against SQL Server 2025:
    /// 11 bytes over an <c>int</c> key, 15 for an <c>int</c> index over an
    /// <c>int</c> clustered key, 18 when the indexed column is nullable, 19
    /// over a heap, a <c>varchar</c> key's 5 plus its length beside its first
    /// record's 7).
    /// </summary>
    public static int IndexNodeRecordBytes(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<int> node, ReadOnlySpan<int> lengths, bool heapLocator, bool uniquified, bool omitVariable)
    {
        var (fixedBytes, bits, offsets, data) = Measure(columns, node, lengths, uniquified, omitVariable);
        if (heapLocator)
            fixedBytes += 8;
        var nullable = false;
        foreach (var ordinal in node)
            nullable |= columns[ordinal].Nullable;
        var bytes = 1 + fixedBytes + ((bits + 7) / 8) + 6;
        if (nullable)
            bytes += 2 + ((node.Length + (heapLocator ? 1 : 0) + (uniquified ? 1 : 0) + 7) / 8);
        if (offsets > 0)
            bytes += 2 + (2 * offsets) + data;
        return bytes;
    }

    private static (int FixedBytes, int Bits, int Offsets, int Data) Measure(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<int> ordinals, ReadOnlySpan<int> lengths, bool uniquified, bool omitVariable)
    {
        var fixedBytes = 0;
        var bits = 0;
        var variable = 0;
        var offsets = 0;
        var data = 0;
        for (var i = 0; i < ordinals.Length; i++)
        {
            var type = columns[ordinals[i]].Type;
            if (type == SqlType.Bit)
            {
                bits++;
                continue;
            }
            if (type.IsFixedLength)
            {
                fixedBytes += type.FixedLength;
                continue;
            }
            variable++;
            if (lengths[i] <= 0 || omitVariable)
                continue;
            offsets = variable;
            data += lengths[i];
        }
        if (uniquified && !omitVariable)
        {
            offsets = variable + 1;
            data += 4;
        }
        return (fixedBytes, bits, offsets, data);
    }

    /// <summary>
    /// Settles where each value of a row goes: <paramref name="inRow"/>
    /// receives, per position of <paramref name="ordinals"/>, the bytes the
    /// value of <paramref name="natural"/> length takes in the row (-1 for
    /// NULL), and <paramref name="spill"/> the LOB and row-overflow records
    /// the values kept off the row take. <paramref name="shape"/> sizes the
    /// record from the in-row lengths, so the push off the row stops as soon
    /// as it fits; the record's size is returned.
    /// </summary>
    public static int Place(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<int> ordinals, ReadOnlySpan<int> natural, Span<int> inRow, StorageOptions options, in RecordShape shape, SpillPacker? spill)
    {
        for (var i = 0; i < ordinals.Length; i++)
        {
            var column = columns[ordinals[i]];
            var length = natural[i];
            if (length < 0 || column.Type.IsFixedLength || column.Type == SqlType.Bit)
            {
                inRow[i] = length;
                continue;
            }
            if (column.Type is TextSqlType or NTextSqlType or ImageSqlType)
            {
                if (options.TextInRowLimit > 0 && length <= options.TextInRowLimit)
                {
                    inRow[i] = length;
                }
                else if (options.TextInRowLimit > 0)
                {
                    inRow[i] = InlineRootBytes(length);
                    spill?.AddPieces(length);
                }
                else
                {
                    inRow[i] = TextPointerBytes;
                    spill?.AddRooted(length);
                }
                continue;
            }
            if (column.IsLob && options.LargeValuesOutOfRow)
            {
                inRow[i] = TextPointerBytes;
                spill?.AddRooted(length);
                continue;
            }
            if (column.IsLob && length > MaxInRowValueBytes)
            {
                inRow[i] = InlineRootBytes(length);
                spill?.AddPieces(length);
                continue;
            }
            inRow[i] = length;
        }

        var bytes = shape.Bytes(columns, ordinals, inRow);
        while (bytes > MaxRecordBytes)
        {
            // Real pushes the longest value still in the row first.
            var longest = -1;
            for (var i = 0; i < ordinals.Length; i++)
            {
                var column = columns[ordinals[i]];
                if (column.Type.IsFixedLength || column.Type == SqlType.Bit || column.Type is TextSqlType or NTextSqlType or ImageSqlType
                    || inRow[i] != natural[i] || natural[i] <= OverflowPointerBytes)
                {
                    continue;
                }
                if (longest < 0 || natural[i] > natural[longest])
                    longest = i;
            }
            if (longest < 0)
                break;
            var length = natural[longest];
            if (columns[ordinals[longest]].IsLob)
            {
                inRow[longest] = InlineRootBytes(length);
                spill?.AddPieces(length);
            }
            else
            {
                inRow[longest] = OverflowPointerBytes;
                spill?.AddOverflow(length + LobRecordOverhead);
            }
            bytes = shape.Bytes(columns, ordinals, inRow);
        }
        return bytes;
    }

    /// <summary>The bytes an off-row <c>(max)</c> value's pointer takes in the row.</summary>
    private static int InlineRootBytes(int length)
    {
        var pieces = Pieces(length);
        return pieces <= InlineRootPieces ? 12 + (12 * pieces) : 24;
    }

    private static int Pieces(int length) => Math.Max(1, (length + LobPieceBytes - 1) / LobPieceBytes);

    /// <summary>
    /// The LOB and row-overflow pages a rowset's off-row values fill: a
    /// whole-piece LOB record and a LOB root record take a page of their own,
    /// and the shorter records fill pages in turn — a page taking the next
    /// record while it fits and a new page starting otherwise.
    /// </summary>
    internal sealed class SpillPacker
    {
        public readonly SpillUnit Lob = new();

        public readonly SpillUnit Overflow = new();

        /// <summary>The pieces of a value kept off the row by its length or a push.</summary>
        public void AddPieces(int length)
        {
            var pieces = Pieces(length);
            for (var piece = 0; piece < pieces; piece++)
            {
                var carried = Math.Min(LobPieceBytes, length - (piece * LobPieceBytes));
                this.Lob.Add(carried + LobRecordOverhead, ownPage: carried == LobPieceBytes);
            }
            if (length >= LobPieceBytes && length % LobPieceBytes == 0)
                this.Lob.AddEmptyPage();
            if (pieces > InlineRootPieces)
                this.Lob.Add(20 + (16 * pieces), ownPage: true);
        }

        /// <summary>A value a table option or a <c>text</c> type keeps off the row.</summary>
        public void AddRooted(int length)
        {
            this.Lob.Add(TextRootBytes, ownPage: false);
            if (length > TextRootInlineBytes)
                this.AddPieces(length);
        }

        /// <summary>A bounded value pushed to the row-overflow unit.</summary>
        public void AddOverflow(int record) => this.Overflow.Add(record, ownPage: false);
    }

    /// <summary>One allocation unit's off-row records and the pages they fill.</summary>
    internal sealed class SpillUnit
    {
        public long Pages;

        public long Records;

        public long Bytes;

        public int MinRecord;

        public int MaxRecord;

        private int shared = PageBytes;

        public void Add(int record, bool ownPage)
        {
            this.Records++;
            this.Bytes += record + 2;
            this.MinRecord = this.Records == 1 ? record : Math.Min(this.MinRecord, record);
            this.MaxRecord = Math.Max(this.MaxRecord, record);
            if (ownPage)
            {
                this.Pages++;
                return;
            }
            if (this.shared + record + 2 > PageBytes)
            {
                this.Pages++;
                this.shared = 0;
            }
            this.shared += record + 2;
        }

        public void AddEmptyPage() => this.Pages++;
    }
}

/// <summary>Which of <see cref="RealRecordFormat"/>'s records a row is stored as.</summary>
internal readonly struct RecordShape(bool indexLeaf, bool heapLocator, bool versioned, bool uniquified)
{
    /// <summary>A nonclustered index's leaf record rather than a data record.</summary>
    public readonly bool IndexLeaf = indexLeaf;

    public readonly bool HeapLocator = heapLocator;

    public readonly bool Versioned = versioned;

    public readonly bool Uniquified = uniquified;

    /// <summary>The record's bytes with its values taking <paramref name="inRow"/>, indexed as <paramref name="ordinals"/> is.</summary>
    public int Bytes(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<int> ordinals, ReadOnlySpan<int> inRow) =>
        this.IndexLeaf
            ? RealRecordFormat.IndexLeafRecordBytes(columns, ordinals, inRow, this.HeapLocator, this.Uniquified)
            : RealRecordFormat.DataRecordBytes(columns, inRow, this.Versioned, this.Uniquified);
}

/// <summary>The table options that steer where real keeps a row's long values.</summary>
internal readonly struct StorageOptions(bool largeValuesOutOfRow, int textInRowLimit)
{
    /// <summary><c>sp_tableoption 'large value types out of row'</c>.</summary>
    public readonly bool LargeValuesOutOfRow = largeValuesOutOfRow;

    /// <summary><c>sp_tableoption 'text in row'</c>'s limit, 0 when off.</summary>
    public readonly int TextInRowLimit = textInRowLimit;
}
