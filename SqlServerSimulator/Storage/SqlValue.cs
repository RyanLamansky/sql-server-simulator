using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A type-tagged value for the storage layer.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="SqlValue"/> always carries its <see cref="Type"/>; NULL values
/// retain their type so that, e.g., <c>NULL</c> of <c>int</c> can be
/// distinguished from <c>NULL</c> of <c>varchar</c> when needed.
/// </para>
/// <para>
/// The payload is split into a 64-bit primitive field (covering all current
/// fixed-width primitive types) and a reference field (for variable-length
/// types added later such as <c>varchar</c> and <c>varbinary</c>). Each
/// <see cref="SqlType"/> documents which field it uses.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebugDisplay(),nq}")]
internal readonly partial struct SqlValue : IEquatable<SqlValue>, IComparable<SqlValue>
{
    private readonly long primitive;
    private readonly object? reference;

    public readonly SqlType Type;

    public readonly bool IsNull;

    private SqlValue(SqlType type, long primitive, object? reference, bool isNull)
    {
        this.Type = type;
        this.primitive = primitive;
        this.reference = reference;
        this.IsNull = isNull;
    }

    /// <summary>NULL value of the given type.</summary>
    public static SqlValue Null(SqlType type) => new(type, 0, null, isNull: true);

    /// <summary>
    /// Returns a copy of this value re-tagged with a different
    /// <see cref="SqlType"/>. The new type must be wire-compatible with the
    /// current type (same primitive / reference shape) — used by
    /// <c>CollateExpression</c> to swap a string value's type to one carrying
    /// an explicit collation, where the underlying string reference is
    /// unchanged.
    /// </summary>
    internal SqlValue WithType(SqlType type) => new(type, this.primitive, this.reference, this.IsNull);

    /// <summary>Non-NULL <see cref="int"/> value.</summary>
    public static SqlValue FromInt32(int value) => new(SqlType.Int32, value, null, isNull: false);

    /// <summary>Non-NULL <see cref="long"/> (SQL <c>bigint</c>) value.</summary>
    public static SqlValue FromInt64(long value) => new(SqlType.BigInt, value, null, isNull: false);

    /// <summary>Non-NULL <see cref="short"/> (SQL <c>smallint</c>) value.</summary>
    public static SqlValue FromInt16(short value) => new(SqlType.SmallInt, value, null, isNull: false);

    /// <summary>Non-NULL <see cref="byte"/> (SQL <c>tinyint</c>) value.</summary>
    public static SqlValue FromByte(byte value) => new(SqlType.TinyInt, value, null, isNull: false);

    /// <summary>Non-NULL <see cref="bool"/> (SQL <c>bit</c>) value.</summary>
    public static SqlValue FromBoolean(bool value) => new(SqlType.Bit, value ? 1L : 0L, null, isNull: false);

    /// <summary>Non-NULL SQL <c>varchar</c> value (UTF-8 on disk, .NET string in memory). Type collation defaults to <see cref="Collation.Baseline"/> at <see cref="Coercibility.CoercibleDefault"/> — appropriate for literals, parameters, and CAST results.</summary>
    public static SqlValue FromVarchar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Varchar, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL <c>varchar</c> value tagged with an explicit type instance — preserves the column's pinned collation through row decode and per-column INSERT.</summary>
    public static SqlValue FromVarchar(VarcharSqlType type, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(type, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL <c>nvarchar</c> value (UTF-16 LE on disk, .NET string in memory). Type collation defaults to <see cref="Collation.Baseline"/> at <see cref="Coercibility.CoercibleDefault"/>.</summary>
    public static SqlValue FromNVarchar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.NVarchar, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL <c>nvarchar</c> value tagged with an explicit type instance — preserves the column's pinned collation through row decode and per-column INSERT.</summary>
    public static SqlValue FromNVarchar(NVarcharSqlType type, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(type, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL <c>xml</c> value (encoded identically to <c>nvarchar(MAX)</c>; identity preserved through <see cref="SqlType.Xml"/>).</summary>
    public static SqlValue FromXml(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Xml, 0, value, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>geography</c> value. Stored as raw-WKT (UTF-16 LE on
    /// disk, .NET string in memory) — degraded-mode encoding for the
    /// skip-with-diagnostic bacpac stance. Identity preserved through
    /// <see cref="SqlType.Geography"/>.
    /// </summary>
    public static SqlValue FromGeography(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Geography, 0, value, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>geometry</c> value. Stored as raw-WKT (UTF-16 LE on
    /// disk, .NET string in memory) — degraded-mode encoding for the
    /// skip-with-diagnostic bacpac stance. Identity preserved through
    /// <see cref="SqlType.Geometry"/>.
    /// </summary>
    public static SqlValue FromGeometry(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Geometry, 0, value, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>sql_variant</c> value wrapping <paramref name="inner"/>.
    /// The inner value carries its own base type, which the variant surfaces
    /// per row. Wrapping is idempotent — a variant never nests, so passing a
    /// variant returns it unchanged. A NULL variant is
    /// <see cref="Null(SqlType)"/> of <see cref="SqlType.SqlVariant"/>, not a
    /// wrapped NULL.
    /// </summary>
    public static SqlValue FromVariant(SqlValue inner) =>
        inner.Type is SqlVariantSqlType ? inner : new(SqlType.SqlVariant, 0, inner, isNull: false);

    /// <summary>Non-NULL SQL <c>sysname</c> value (encoded identically to <c>nvarchar</c>; identity preserved across system catalogs).</summary>
    public static SqlValue FromSystemName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.SystemName, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL <c>varbinary</c> value. The array is held by reference; callers shouldn't mutate it after construction.</summary>
    public static SqlValue FromVarbinary(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Varbinary, 0, value, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>varbinary</c> value carrying an explicit varbinary type
    /// (e.g. <see cref="SqlType.VarbinaryMax"/> for <c>varbinary(max)</c>
    /// producers such as <c>COMPRESS</c> / <c>DECOMPRESS</c>, whose payload can
    /// exceed the bounded 2-byte wire length prefix). The array is held by
    /// reference; callers shouldn't mutate it after construction.
    /// </summary>
    public static SqlValue FromVarbinary(VarbinarySqlType type, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        return new(type, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL deprecated <c>text</c> value. Encoded as CP1252 bytes; same in-memory shape as <see cref="FromVarchar(string)"/>.</summary>
    public static SqlValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Text, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL deprecated <c>ntext</c> value. Encoded as UTF-16 LE bytes.</summary>
    public static SqlValue FromNText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.NText, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL deprecated <c>image</c> value. Same caller-owns-the-bytes contract as <see cref="FromVarbinary(byte[])"/>.</summary>
    public static SqlValue FromImage(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Image, 0, value, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>char(N)</c> value. The .NET string is normalized so its
    /// storage-encoded form is exactly N bytes: shorter inputs are right-
    /// padded with U+0020 (ASCII space = 1 byte under both CP1252 and UTF-8),
    /// longer inputs are truncated at a rune boundary. Truncation is silent
    /// — matches SQL Server's <c>CAST</c> semantics. The INSERT/UPDATE
    /// pipeline raises Msg 2628 / 8152 before this method is reached when the
    /// source would lose data. The padding count is computed against the
    /// column's collation's <see cref="Collation.StorageEncoding"/>, so a
    /// UTF-8 collation with a 2-byte input into <c>char(5)</c> appends 3
    /// space chars (not 4).
    /// </summary>
    public static SqlValue FromChar(SqlType type, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return type is not CharSqlType c
            ? throw new ArgumentException($"{type} is not a char(N) type.", nameof(type))
            : new(type, 0, NormalizeFixedLengthStringToByteCount(value, c.length, c.Collation.StorageEncoding), isNull: false);
    }

    /// <summary>Non-NULL SQL <c>nchar(N)</c> value. Same padding/truncation rule as <see cref="FromChar"/>; declared length is in code units.</summary>
    public static SqlValue FromNChar(SqlType type, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return type is not NCharSqlType nc
            ? throw new ArgumentException($"{type} is not an nchar(N) type.", nameof(type))
            : new(type, 0, NormalizeFixedLengthString(value, nc.length), isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>binary(N)</c> value. Bytes are normalized to the
    /// declared length: shorter inputs are right-padded with <c>0x00</c>,
    /// longer inputs are truncated. The array is owned by the value;
    /// callers shouldn't mutate it after construction.
    /// </summary>
    public static SqlValue FromBinary(SqlType type, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return type is not BinarySqlType b
            ? throw new ArgumentException($"{type} is not a binary(N) type.", nameof(type))
            : new(type, 0, NormalizeFixedLengthBytes(value, b.length), isNull: false);
    }

    /// <summary>
    /// Wraps the raw <see cref="long"/> counter of a <c>rowversion</c>
    /// value. Stored in the primitive slot — no <c>byte[]</c> allocation —
    /// so encode / decode / equality / compare run alloc-free in the hot
    /// path. Auto-generated values flow in through
    /// <see cref="Database.AllocateRowVersion"/>; the bytes form
    /// (big-endian, 8 bytes) materializes on demand via
    /// <see cref="AsBytes"/> for CAST to varbinary, the wire-typed
    /// accessor, and debug rendering.
    /// </summary>
    public static SqlValue FromRowVersion(long counter) =>
        new(SqlType.RowVersion, counter, reference: null, isNull: false);

    /// <summary>
    /// Returns the raw <see cref="long"/> counter behind a non-NULL
    /// <c>rowversion</c> value. Used by <see cref="RowVersionSqlType.Encode"/>
    /// to write the bytes big-endian without going through
    /// <see cref="AsBytes"/>'s on-demand allocation.
    /// </summary>
    internal long RowVersionCounter => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type is not RowVersionSqlType
            ? throw new InvalidOperationException($"Value is {this.Type}, not rowversion.")
            : this.primitive;

    private static string NormalizeFixedLengthString(string value, int length) =>
        value.Length == length ? value
        : value.Length < length ? value.PadRight(length, ' ')
        : value[..length];

    /// <summary>
    /// Normalizes a <c>char(N)</c>-bound value so its
    /// <paramref name="encoding"/>-encoded form is exactly
    /// <paramref name="byteLength"/> bytes. Used by <see cref="FromChar"/>
    /// to honor per-collation storage encoding: CP1252 chars are
    /// single-byte so the result matches <see cref="NormalizeFixedLengthString"/>
    /// trivially, but UTF-8 chars can be 1-4 bytes each so padding must
    /// count bytes rather than chars (a 2-UTF-8-byte <c>é</c> in
    /// <c>char(5)</c> gets 3 trailing spaces, not 4). Truncation walks
    /// runes to avoid splitting a multi-byte sequence; the resulting
    /// rune-truncated string then gets space-padded to the target byte
    /// width when the prefix doesn't land exactly.
    /// </summary>
    private static string NormalizeFixedLengthStringToByteCount(string value, int byteLength, Encoding encoding)
    {
        var actualBytes = encoding.GetByteCount(value);
        if (actualBytes == byteLength) return value;
        if (actualBytes < byteLength)
            return value + new string(' ', byteLength - actualBytes);
        // Truncation: single-byte encodings can slice directly; multi-byte
        // encodings (UTF-8) must walk runes to avoid splitting mid-sequence.
        if (encoding.IsSingleByte) return value[..byteLength];
        var bytesUsed = 0;
        var charsUsed = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = encoding.GetByteCount(rune.ToString());
            if (bytesUsed + runeBytes > byteLength) break;
            bytesUsed += runeBytes;
            charsUsed += rune.Utf16SequenceLength;
        }
        var pad = byteLength - bytesUsed;
        return pad > 0 ? value[..charsUsed] + new string(' ', pad) : value[..charsUsed];
    }

    private static byte[] NormalizeFixedLengthBytes(byte[] value, int length)
    {
        if (value.Length == length)
            return value;
        var buffer = new byte[length];
        Buffer.BlockCopy(value, 0, buffer, 0, Math.Min(value.Length, length));
        return buffer;
    }

    /// <summary>Non-NULL SQL <c>date</c> value.</summary>
    public static SqlValue FromDate(DateOnly value) => new(SqlType.Date, value.DayNumber, null, isNull: false);

    /// <summary>
    /// Non-NULL legacy SQL <c>datetime</c> value. Inputs are rounded half-up
    /// to the nearest 1/300-second tick; the result is range-checked against
    /// <c>[1753-01-01, 9999-12-31 23:59:59.997]</c>. Out-of-range raises
    /// Msg 242 (the conversion-overflow message SQL Server uses for both
    /// string parses and direct binding paths).
    /// </summary>
    public static SqlValue FromDateTime(DateTime value)
    {
        // Split into day count + time-of-day so the * 300 arithmetic doesn't
        // overflow long for late dates.
        var dayCount = (int)(value.Date - DateTimeSqlType.BaseDate).TotalDays;
        var timeUnits = ((value.TimeOfDay.Ticks * 300) + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond;
        if (timeUnits == DateTimeSqlType.TicksPerDay)
        {
            // Half-up rounding pushed past midnight; carry into the next day.
            dayCount++;
            timeUnits = 0;
        }
        if (dayCount is < DateTimeSqlType.MinDayCount or > DateTimeSqlType.MaxDayCount)
            throw SimulatedSqlException.OutOfRangeDateTimeConversion(SqlType.DateTime);

        var roundedTimeTicks = timeUnits * TimeSpan.TicksPerSecond / 300;
        var rounded = DateTimeSqlType.BaseDate.AddDays(dayCount).AddTicks(roundedTimeTicks);
        return new(SqlType.DateTime, rounded.Ticks, null, isNull: false);
    }

    /// <summary>
    /// Non-NULL legacy <c>datetime</c> value built from already-canonical
    /// 100-ns ticks. Bypasses the rounding-and-range-check path; reserved for
    /// the storage-layer decoder, where the bytes were validated at encode
    /// time and the tick count is the source of truth.
    /// </summary>
    internal static SqlValue FromDateTimeUnchecked(DateTime canonical) =>
        new(SqlType.DateTime, canonical.Ticks, null, isNull: false);

    /// <summary>
    /// Non-NULL SQL <c>smalldatetime</c> value. Inputs are first quantized to
    /// legacy 1/300-second tick (matching SQL Server's internal pipeline),
    /// then rounded half-up to the nearest minute; midnight rollover carries
    /// into the next day. The result is range-checked against
    /// <c>[1900-01-01, 2079-06-06 23:59]</c> — out-of-range raises Msg 242
    /// (the same conversion-overflow message SQL Server uses for <c>datetime</c>,
    /// with the type name swapped).
    /// </summary>
    public static SqlValue FromSmallDateTime(DateTime value)
    {
        var dayCount = (int)(value.Date - SmallDateTimeSqlType.BaseDate).TotalDays;
        // Quantize to legacy 1/300s tick first so the .999/.998 boundary
        // matches SQL Server: only ticks at or after the 30s mark roll to
        // the next minute.
        var legacyUnits = ((value.TimeOfDay.Ticks * 300) + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond;
        if (legacyUnits == DateTimeSqlType.TicksPerDay)
        {
            dayCount++;
            legacyUnits = 0;
        }
        var quantizedTicks = legacyUnits * TimeSpan.TicksPerSecond / 300;
        // Half-up minute rounding on the quantized ticks. With 1/300s
        // granularity the smallest distance from the 30s boundary is one
        // tick, so adding TicksPerMinute/2 and integer-dividing produces
        // the documented "≥30s rolls up, <30s stays" behavior.
        var minutes = (quantizedTicks + (TimeSpan.TicksPerMinute / 2)) / TimeSpan.TicksPerMinute;
        if (minutes == SmallDateTimeSqlType.MinutesPerDay)
        {
            dayCount++;
            minutes = 0;
        }
        if (dayCount is < 0 or > SmallDateTimeSqlType.MaxDayCount)
            throw SimulatedSqlException.OutOfRangeDateTimeConversion(SqlType.SmallDateTime);

        var rounded = SmallDateTimeSqlType.BaseDate.AddDays(dayCount).AddMinutes(minutes);
        return new(SqlType.SmallDateTime, rounded.Ticks, null, isNull: false);
    }

    /// <summary>
    /// Non-NULL <c>smalldatetime</c> value built from already-canonical
    /// 100-ns ticks (must already be aligned to a minute boundary and within
    /// the smalldatetime range). Reserved for the storage-layer decoder.
    /// </summary>
    internal static SqlValue FromSmallDateTimeUnchecked(DateTime canonical) =>
        new(SqlType.SmallDateTime, canonical.Ticks, null, isNull: false);

    /// <summary>
    /// Non-NULL SQL <c>datetime2(N)</c> value. The supplied <paramref name="value"/>
    /// is rounded to the precision of <paramref name="type"/>: SQL Server's
    /// CAST/parameter-binding semantics round half-away-from-zero when more
    /// fractional precision is supplied than the destination can hold.
    /// </summary>
    public static SqlValue FromDateTime2(SqlType type, DateTime value)
    {
        if (type is not DateTime2SqlType dt2)
            throw new ArgumentException($"{type} is not a datetime2 type.", nameof(type));
        var unit = dt2.ticksPerUnit;
        var rounded = unit == 1 ? value.Ticks : (value.Ticks + (unit / 2)) / unit * unit;
        return new(type, rounded, null, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>time(N)</c> value. <paramref name="value"/> must fall
    /// within <c>[00:00:00, 24:00:00)</c>; SQL Server rejects negative or
    /// over-24-hour <c>TimeSpan</c>s on its <c>time</c> column. The supplied
    /// value is rounded to the precision of <paramref name="type"/> using
    /// the same half-away-from-zero rule as <see cref="FromDateTime2"/>.
    /// </summary>
    public static SqlValue FromTime(SqlType type, TimeSpan value)
    {
        if (type is not TimeSqlType tt)
            throw new ArgumentException($"{type} is not a time type.", nameof(type));
        if (value.Ticks is < 0 or >= TimeSpan.TicksPerDay)
            throw new ArgumentOutOfRangeException(nameof(value), $"time value must be within [00:00:00, 24:00:00); got {value}.");
        var unit = tt.ticksPerUnit;
        var rounded = unit == 1 ? value.Ticks : (value.Ticks + (unit / 2)) / unit * unit;
        // Half-up rounding can push a value at the day boundary (e.g. 23:59:59.9999999
        // at precision 6) into the next day; clamp to the in-range maximum.
        if (rounded >= TimeSpan.TicksPerDay)
            rounded = TimeSpan.TicksPerDay - unit;
        return new(type, rounded, null, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>datetimeoffset(N)</c> value. The supplied
    /// <paramref name="value"/> is rounded to the precision of
    /// <paramref name="type"/> using the same half-away-from-zero rule as
    /// <see cref="FromDateTime2"/>; the offset is preserved unchanged.
    /// </summary>
    /// <remarks>
    /// The boxed <see cref="DateTimeOffset"/> lives in the reference slot
    /// (mirroring the pattern used for <c>varchar</c>/<c>varbinary</c>); the
    /// primitive slot stores the post-rounding UTC ticks. Equality and
    /// ordering compare by UTC instant only — the offset round-trips on the
    /// value but is intentionally not part of identity, matching SQL Server's
    /// <c>datetimeoffset</c> comparison rule.
    /// </remarks>
    public static SqlValue FromDateTimeOffset(SqlType type, DateTimeOffset value)
    {
        if (type is not DateTimeOffsetSqlType dto)
            throw new ArgumentException($"{type} is not a datetimeoffset type.", nameof(type));
        var unit = dto.ticksPerUnit;
        if (unit != 1)
        {
            // Round the local-instant ticks (DateTimeOffset.Ticks) so that the
            // wall-clock representation rounds half-away-from-zero, matching
            // SQL Server. Reconstructing with the same offset preserves the
            // user-visible time-zone label.
            var rounded = (value.Ticks + (unit / 2)) / unit * unit;
            value = new DateTimeOffset(rounded, value.Offset);
        }
        return new(type, value.UtcTicks, value, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>uniqueidentifier</c> value. The 16-byte payload is
    /// boxed into the reference slot (mirroring the <see cref="DateTimeOffset"/>
    /// pattern); equality and ordering route through
    /// <see cref="SqlGuid"/> to honor SQL Server's quirky uniqueidentifier
    /// sort order rather than .NET's natural <see cref="Guid.CompareTo(Guid)"/>.
    /// </summary>
    public static SqlValue FromGuid(Guid value) => new(SqlType.UniqueIdentifier, 0, value, isNull: false);

    /// <summary>
    /// Non-NULL SQL <c>hierarchyid</c> value built from the segment-array path
    /// form. The path is encoded into its canonical OrdPath byte form (the
    /// simulator's in-memory representation for <c>hierarchyid</c>, matching what
    /// a real server stores / compares / sends) and stored in the reference slot;
    /// equality and ordering are unsigned bytewise, which equals depth-first tree
    /// order. Raises <see cref="NotSupportedException"/> if a label falls outside
    /// the modeled OrdPath tier range.
    /// </summary>
    public static SqlValue FromHierarchyId(int[][] path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new(SqlType.HierarchyId, 0, HierarchyIdOrdPath.Encode(path), isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>hierarchyid</c> value from raw OrdPath bytes, stored
    /// verbatim (no validation or re-encoding) — the passthrough path for BACPAC
    /// import, the ADO.NET byte-parameter path, and <c>CAST(varbinary AS
    /// hierarchyid)</c> (after that path validates canonicality). Lets a value in
    /// an unmodeled tier still round-trip through storage even though
    /// <see cref="AsHierarchyId"/> / <c>ToString()</c> can't decode it. The caller
    /// transfers ownership of the array.
    /// </summary>
    public static SqlValue FromHierarchyIdBytes(byte[] ordPathBytes)
    {
        ArgumentNullException.ThrowIfNull(ordPathBytes);
        return new(SqlType.HierarchyId, 0, ordPathBytes, isNull: false);
    }

    /// <summary>
    /// Non-NULL SQL <c>decimal(p, s)</c> value. The .NET <see cref="decimal"/>
    /// payload is boxed in the reference slot; <paramref name="type"/> carries
    /// the precision and scale identity. Caller is responsible for ensuring
    /// the value already fits the declared scale (use
    /// <see cref="CoerceTo(SqlType)"/> for the rounding-and-overflow path).
    /// </summary>
    public static SqlValue FromDecimal(SqlType type, decimal value) =>
        type is not DecimalSqlType
            ? throw new ArgumentException($"{type} is not a decimal type.", nameof(type))
            : new(type, 0, value, isNull: false);

    /// <summary>
    /// Non-NULL SQL <c>money</c> / <c>smallmoney</c> value. The decimal
    /// input is rounded half-away-from-zero to scale 4 and stored as a
    /// scaled int64 (smallmoney is range-checked at encode time, since it
    /// shares this representation with money).
    /// </summary>
    public static SqlValue FromMoney(SqlType type, decimal value)
    {
        if (type != SqlType.Money && type != SqlType.SmallMoney)
            throw new ArgumentException($"{type} is not a money type.", nameof(type));
        var scaled = decimal.Round(value * MoneySqlType.ScaleFactor, 0, MidpointRounding.AwayFromZero);
        long units;
        try
        {
            units = checked((long)scaled);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflowToTarget(type.ToString()!);
        }
        return type == SqlType.SmallMoney && units is < int.MinValue or > int.MaxValue
            ? throw SimulatedSqlException.ArithmeticOverflowToTarget("smallmoney")
            : new(type, units, null, isNull: false);
    }

    /// <summary>
    /// Internal constructor for the storage-layer decoder: the on-disk
    /// scaled int has already been validated at encode time, so range checks
    /// don't apply.
    /// </summary>
    internal static SqlValue FromMoneyScaledUnits(SqlType type, long units) =>
        new(type, units, null, isNull: false);

    /// <summary>Non-NULL SQL <c>float</c> value. The 8-byte payload lives in the primitive slot via <see cref="BitConverter.DoubleToInt64Bits"/>.</summary>
    public static SqlValue FromDouble(double value) => new(SqlType.Float, BitConverter.DoubleToInt64Bits(value), null, isNull: false);

    /// <summary>Non-NULL SQL <c>real</c> value. The 4-byte payload lives in the primitive slot via <see cref="BitConverter.SingleToInt32Bits"/>.</summary>
    public static SqlValue FromSingle(float value) => new(SqlType.Real, BitConverter.SingleToInt32Bits(value), null, isNull: false);

    /// <summary>
    /// Constructs a string-typed value of the given SQL type, preserving the
    /// target type's pinned collation and coercibility. Used by string
    /// functions (UPPER, LEFT, etc.) that preserve their input's string
    /// subtype and by the cross-string-family coercion path.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not a string type.</exception>
    public static SqlValue FromString(SqlType type, string value) =>
        type is VarcharSqlType v ? FromVarchar(v, value)
        : type is NVarcharSqlType n ? FromNVarchar(n, value)
        : type == SqlType.SystemName ? FromSystemName(value)
        : type == SqlType.Text ? FromText(value)
        : type == SqlType.NText ? FromNText(value)
        : type == SqlType.Xml ? FromXml(value)
        : type == SqlType.Geography ? FromGeography(value)
        : type == SqlType.Geometry ? FromGeometry(value)
        : type is CharSqlType ? FromChar(type, value)
        : type is NCharSqlType ? FromNChar(type, value)
        : throw new ArgumentException($"{type} is not a string type.", nameof(type));

    /// <summary>Implicit lift from <see cref="int"/>; convenient in test code.</summary>
    public static implicit operator SqlValue(int value)
    {
        return FromInt32(value);
    }

    private T As<T>(SqlType expected, Func<long, T> project) => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != expected
            ? throw new InvalidOperationException($"Value is {this.Type}, not {expected}.")
            : project(this.primitive);

    /// <summary>Returns the value as <see cref="int"/>. Throws if NULL or wrong type.</summary>
    public int AsInt32 => this.As(SqlType.Int32, p => (int)p);

    /// <summary>Returns the value as <see cref="long"/>. Throws if NULL or wrong type.</summary>
    public long AsInt64 => this.As(SqlType.BigInt, p => p);

    /// <summary>Returns the value as <see cref="short"/>. Throws if NULL or wrong type.</summary>
    public short AsInt16 => this.As(SqlType.SmallInt, p => (short)p);

    /// <summary>Returns the value as <see cref="byte"/>. Throws if NULL or wrong type.</summary>
    public byte AsByte => this.As(SqlType.TinyInt, p => (byte)p);

    /// <summary>Returns the value as <see cref="bool"/>. Throws if NULL or wrong type.</summary>
    public bool AsBoolean => this.As(SqlType.Bit, p => p != 0);

    /// <summary>Returns the value as <see cref="string"/>. Throws if NULL or if not a string-typed value.</summary>
    public string AsString => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type.Category != SqlTypeCategory.String
            ? throw new InvalidOperationException($"Value is {this.Type}, not a string type.")
            : (string)this.reference!;

    /// <summary>Returns the value as <see cref="byte"/><c>[]</c>. Throws if NULL or not a binary/varbinary/image/rowversion value. For rowversion, allocates a fresh 8-byte big-endian buffer on each call (the in-memory rep is the raw <see cref="long"/> counter); the row-encoding hot path goes through <see cref="RowVersionSqlType.Encode"/> instead, which writes the bytes directly into the row buffer.</summary>
    public byte[] AsBytes => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type is RowVersionSqlType
            ? RowVersionToBytes(this.primitive)
            : this.Type is not (VarbinarySqlType or BinarySqlType or ImageSqlType)
                ? throw new InvalidOperationException($"Value is {this.Type}, not varbinary, binary, image, or rowversion.")
                : (byte[])this.reference!;

    private static byte[] RowVersionToBytes(long counter)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, counter);
        return bytes;
    }

    /// <summary>Returns the value as <see cref="DateOnly"/>. Throws if NULL or wrong type.</summary>
    public DateOnly AsDate => this.As(SqlType.Date, p => DateOnly.FromDayNumber((int)p));

    /// <summary>Returns the value as <see cref="DateTime"/>. Throws if NULL or not a legacy datetime value.</summary>
    public DateTime AsDateTime => this.As(SqlType.DateTime, p => new DateTime(p));

    /// <summary>Returns the value as <see cref="DateTime"/>. Throws if NULL or not a smalldatetime value.</summary>
    public DateTime AsSmallDateTime => this.As(SqlType.SmallDateTime, p => new DateTime(p));

    /// <summary>Returns the value as <see cref="DateTime"/>. Throws if NULL or not a datetime2 value.</summary>
    public DateTime AsDateTime2 => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type is not DateTime2SqlType
            ? throw new InvalidOperationException($"Value is {this.Type}, not a datetime2 type.")
            : new DateTime(this.primitive);

    /// <summary>Returns the value as <see cref="TimeSpan"/>. Throws if NULL or not a time value.</summary>
    public TimeSpan AsTime => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type is not TimeSqlType
            ? throw new InvalidOperationException($"Value is {this.Type}, not a time type.")
            : new TimeSpan(this.primitive);

    /// <summary>Returns the value as <see cref="Guid"/>. Throws if NULL or not a uniqueidentifier value.</summary>
    public Guid AsGuid => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != SqlType.UniqueIdentifier
            ? throw new InvalidOperationException($"Value is {this.Type}, not uniqueidentifier.")
            : (Guid)this.reference!;

    /// <summary>Returns the inner value carried by a non-NULL <c>sql_variant</c>. Throws if NULL or not a sql_variant value.</summary>
    public SqlValue AsVariantInner => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type is not SqlVariantSqlType
            ? throw new InvalidOperationException($"Value is {this.Type}, not sql_variant.")
            : (SqlValue)this.reference!;

    /// <summary>Returns the hierarchyid path decoded to segment-array form. Throws if NULL or not a hierarchyid value; <see cref="NotSupportedException"/> if the stored bytes use an unmodeled OrdPath tier.</summary>
    public int[][] AsHierarchyId => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != SqlType.HierarchyId
            ? throw new InvalidOperationException($"Value is {this.Type}, not hierarchyid.")
            : HierarchyIdOrdPath.Decode((byte[])this.reference!);

    /// <summary>Returns the raw canonical OrdPath bytes backing a hierarchyid value (zero-copy). Throws if NULL or not a hierarchyid value.</summary>
    public byte[] AsHierarchyIdBytes => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != SqlType.HierarchyId
            ? throw new InvalidOperationException($"Value is {this.Type}, not hierarchyid.")
            : (byte[])this.reference!;

    /// <summary>Returns the value as <see cref="decimal"/>. Throws if NULL or not a decimal-typed value.</summary>
    public decimal AsDecimal => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type is not DecimalSqlType
            ? throw new InvalidOperationException($"Value is {this.Type}, not a decimal type.")
            : (decimal)this.reference!;

    /// <summary>Returns the money/smallmoney value as a <see cref="decimal"/> with scale 4.</summary>
    public decimal AsMoney => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != SqlType.Money && this.Type != SqlType.SmallMoney
            ? throw new InvalidOperationException($"Value is {this.Type}, not a money type.")
            : this.primitive / (decimal)MoneySqlType.ScaleFactor;

    /// <summary>Returns the raw scaled-int64 representation of a money/smallmoney value (storage-layer use only).</summary>
    internal long AsMoneyScaledUnits => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != SqlType.Money && this.Type != SqlType.SmallMoney
            ? throw new InvalidOperationException($"Value is {this.Type}, not a money type.")
            : this.primitive;

    /// <summary>Returns the value as <see cref="double"/>. Throws if NULL or not a float value.</summary>
    public double AsDouble => this.As(SqlType.Float, BitConverter.Int64BitsToDouble);

    /// <summary>Returns the value as <see cref="float"/>. Throws if NULL or not a real value.</summary>
    public float AsSingle => this.As(SqlType.Real, p => BitConverter.Int32BitsToSingle((int)p));

    /// <summary>Returns the value as <see cref="DateTimeOffset"/>. Throws if NULL or not a datetimeoffset value.</summary>
    public DateTimeOffset AsDateTimeOffset => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type is not DateTimeOffsetSqlType
            ? throw new InvalidOperationException($"Value is {this.Type}, not a datetimeoffset type.")
            : (DateTimeOffset)this.reference!;

    /// <summary>
    /// Returns the .NET object representation of this value, or <c>null</c> for SQL NULL.
    /// Used by the data reader at the public-API boundary to expose values via
    /// <see cref="object"/>-typed accessors.
    /// </summary>
    internal object? ToObject() => this.IsNull ? null : this.Type switch
    {
        var t when t == SqlType.Int32 => this.AsInt32,
        var t when t == SqlType.BigInt => this.AsInt64,
        var t when t == SqlType.SmallInt => this.AsInt16,
        var t when t == SqlType.TinyInt => this.AsByte,
        var t when t == SqlType.Bit => this.AsBoolean,
        { Category: SqlTypeCategory.String } => this.AsString,
        VarbinarySqlType or BinarySqlType or ImageSqlType or RowVersionSqlType => this.AsBytes,
        // SqlClient surfaces a date column as DateTime at midnight (Kind=Unspecified)
        // when read via the untyped accessors. EF's DateOnly mapping reads
        // through GetDateTime and converts on its own.
        var t when t == SqlType.Date => this.AsDate.ToDateTime(TimeOnly.MinValue),
        var t when t == SqlType.DateTime => DateTimeSqlType.RoundToClientMilliseconds(this.AsDateTime),
        var t when t == SqlType.SmallDateTime => this.AsSmallDateTime,
        DateTime2SqlType => this.AsDateTime2,
        // SqlClient surfaces a time column as TimeSpan via the untyped
        // accessors. EF's TimeOnly mapping reads through GetFieldValue<TimeOnly>().
        TimeSqlType => this.AsTime,
        DateTimeOffsetSqlType => this.AsDateTimeOffset,
        var t when t == SqlType.UniqueIdentifier => this.AsGuid,
        // hierarchyid surfaces as its raw OrdPath bytes via untyped accessors —
        // the same buffer a real server stores and sends over the UDT wire.
        var t when t == SqlType.HierarchyId => this.AsHierarchyIdBytes,
        DecimalSqlType => this.AsDecimal,
        var t when t == SqlType.Float => this.AsDouble,
        var t when t == SqlType.Real => this.AsSingle,
        var t when t == SqlType.Money || t == SqlType.SmallMoney => this.AsMoney,
        // sql_variant surfaces the inner value's CLR object per row, so an
        // untyped reader accessor hands back bool / int / string / … matching
        // the per-cell base type (the DacFx (bool)reader[…] unbox path).
        SqlVariantSqlType => this.AsVariantInner.ToObject(),
        _ => throw new NotSupportedException($"No object representation for {this.Type}."),
    };


    public bool Equals(SqlValue other) =>
        (this.Type == other.Type || IsLengthOnlyBinaryVariance(this.Type, other.Type))
        && this.IsNull == other.IsNull
        && (this.IsNull
            || (this.Type is SqlVariantSqlType
                ? this.AsVariantInner.Equals(other.AsVariantInner)
                : IsStringTypeRef(this.Type)
                ? this.Type.Collation!.Equals(TrimTrailing((string)this.reference!), TrimTrailing((string)other.reference!))
                : this.Type is DateTimeOffsetSqlType
                    ? this.primitive == other.primitive
                    : this.Type == SqlType.UniqueIdentifier
                        ? (Guid)this.reference! == (Guid)other.reference!
                        : this.Type is DecimalSqlType
                            ? (decimal)this.reference! == (decimal)other.reference!
                            : this.Type == SqlType.HierarchyId
                                ? ((byte[])this.reference!).AsSpan().SequenceEqual((byte[])other.reference!)
                                : this.primitive == other.primitive && ReferenceContentEquals(this.reference, other.reference)));

    /// <summary>
    /// Object equality that respects content for <c>byte[]</c> (varbinary) and
    /// delegates to <see cref="object.Equals(object?, object?)"/> otherwise.
    /// String references take a separate, collation-aware path in
    /// <see cref="Equals(SqlValue)"/> and don't reach this helper.
    /// </summary>
    private static bool ReferenceContentEquals(object? a, object? b) => (a, b) switch
    {
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        _ => Equals(a, b),
    };

    /// <summary>
    /// True for the string-family types that participate in SQL Server's
    /// collation-aware comparison (case folding + ANSI trailing-space padding).
    /// Used to gate the per-string equality / hash / compare paths. The
    /// category check covers <c>varchar</c>, <c>nvarchar</c>, <c>sysname</c>,
    /// and the fixed-length cousins <c>char(N)</c>, <c>nchar(N)</c> — all
    /// share the same in-memory string representation.
    /// </summary>
    private static bool IsStringTypeRef(SqlType t) => t.Category == SqlTypeCategory.String;

    /// <summary>
    /// True when two types differ only in the declared length of the same
    /// variable- or fixed-length binary family (<c>varbinary(N)</c> vs
    /// <c>varbinary(M)</c>, <c>binary(N)</c> vs <c>binary(M)</c>). Binary
    /// equality / ordering compares the raw byte spans regardless of declared
    /// length, so the type-identity guards in <see cref="Equals(SqlValue)"/> /
    /// <see cref="CompareTo(SqlValue)"/> admit these pairs — needed since two
    /// binary literals now carry their exact value width (<c>0x01</c> →
    /// <c>varbinary(1)</c>, <c>0x0100</c> → <c>varbinary(2)</c>) and
    /// <c>varbinary</c> coercion doesn't pin the target length.
    /// </summary>
    private static bool IsLengthOnlyBinaryVariance(SqlType a, SqlType b) =>
        (a is VarbinarySqlType && b is VarbinarySqlType)
            || (a is BinarySqlType && b is BinarySqlType);

    /// <summary>
    /// Strips trailing ASCII spaces, modeling SQL Server's ANSI padding for
    /// <c>=</c>/<c>&lt;&gt;</c>/<c>ORDER BY</c> on varchar/nvarchar — only
    /// space (U+0020) is trimmed; other whitespace is significant.
    /// </summary>
    private static string TrimTrailing(string s) => s.TrimEnd(' ');

    /// <summary>
    /// Orders two non-NULL same-typed values. NULL handling is the caller's
    /// responsibility (SQL's NULL-comparison semantics differ from .NET's
    /// IComparable convention, so we throw here rather than pick a side).
    /// </summary>
    /// <remarks>
    /// Dispatches on <see cref="SqlType.Category"/> so the hot path is a
    /// jump-table-friendly switch rather than the linear type-test chain it
    /// replaced. The single-arm collapse for Integer / DateTime / Money relies
    /// on the invariant that each type in those categories stores its ordered
    /// value directly in <see cref="primitive"/> — Bit's 0/1 sorts as long the
    /// same way <see cref="bool.CompareTo(bool)"/> does, all DateTime variants
    /// already used <see cref="primitive"/>, and Money/SmallMoney hold scaled
    /// integers there. Approximate is the only category that can't unify
    /// (the IEEE 754 bit pattern in <see cref="primitive"/> isn't monotonic
    /// across the sign bit and mishandles NaN), so it splits Float vs Real.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Either operand is NULL.</exception>
    /// <exception cref="NotSupportedException">The operands' types differ, or comparison for that type isn't implemented yet.</exception>
    public int CompareTo(SqlValue other) =>
        this.IsNull || other.IsNull
            ? throw new InvalidOperationException("CompareTo on NULL is undefined; check IsNull before calling.")
            : this.Type != other.Type && !IsLengthOnlyBinaryVariance(this.Type, other.Type)
            ? throw new NotSupportedException($"Cross-type comparison isn't implemented: {this.Type} vs {other.Type}.")
            : this.Type.Category switch
            {
                SqlTypeCategory.Integer or SqlTypeCategory.DateTime or SqlTypeCategory.Money
                    => this.primitive.CompareTo(other.primitive),
                SqlTypeCategory.Decimal => this.AsDecimal.CompareTo(other.AsDecimal),
                SqlTypeCategory.Approximate => this.Type == SqlType.Float
                    ? this.AsDouble.CompareTo(other.AsDouble)
                    : this.AsSingle.CompareTo(other.AsSingle),
                SqlTypeCategory.String => this.Type.Collation!.Compare(
                    TrimTrailing((string)this.reference!),
                    TrimTrailing((string)other.reference!)),
                SqlTypeCategory.UniqueIdentifier => new SqlGuid(this.AsGuid).CompareTo(new SqlGuid(other.AsGuid)),
                SqlTypeCategory.Other => this.Type switch
                {
                    VarbinarySqlType or BinarySqlType or ImageSqlType => this.AsBytes.AsSpan().SequenceCompareTo(other.AsBytes),
                    RowVersionSqlType => this.primitive.CompareTo(other.primitive),
                    // OrdPath's defining property: unsigned bytewise order equals
                    // depth-first tree order, so hierarchyid comparison is a memcmp.
                    HierarchyIdSqlType => this.AsHierarchyIdBytes.AsSpan().SequenceCompareTo(other.AsHierarchyIdBytes),
                    // sql_variant ordering compares the inner values; differing
                    // inner base types fall to the inner CompareTo's own
                    // cross-type rejection (SQL Server's full sql_variant sort
                    // by type-family rank is out of scope — see docs).
                    SqlVariantSqlType => this.AsVariantInner.CompareTo(other.AsVariantInner),
                    _ => throw new NotSupportedException($"Comparison for {this.Type} isn't implemented yet."),
                },
                _ => throw new NotSupportedException($"Comparison for {this.Type} isn't implemented yet."),
            };

    public override bool Equals(object? obj) => obj is SqlValue other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(this.Type);
        hash.Add(this.IsNull);
        if (this.IsNull)
            return hash.ToHashCode();

        if (this.Type is SqlVariantSqlType)
        {
            // Identity is the inner value alone; equality unwraps to it.
            hash.Add(this.AsVariantInner.GetHashCode());
        }
        else if (IsStringTypeRef(this.Type))
        {
            // Trailing spaces and case folding are part of equality, so the
            // hash must agree.
            hash.Add(this.Type.Collation!.GetHashCode(TrimTrailing((string)this.reference!)));
        }
        else if (this.Type is DateTimeOffsetSqlType)
        {
            // Equality is by UTC instant; the offset isn't part of identity.
            hash.Add(this.primitive);
        }
        else if (this.Type == SqlType.UniqueIdentifier)
        {
            // Identity is the Guid value alone; the unused primitive slot is zero.
            hash.Add((Guid)this.reference!);
        }
        else if (this.Type is DecimalSqlType)
        {
            hash.Add((decimal)this.reference!);
        }
        else if (this.Type == SqlType.HierarchyId)
        {
            // Equal paths encode to identical OrdPath bytes, so hashing the
            // bytes agrees with the bytewise equality above.
            hash.AddBytes((byte[])this.reference!);
        }
        else if (this.reference is byte[] bytes)
        {
            hash.Add(this.primitive);
            hash.AddBytes(bytes);
        }
        else
        {
            hash.Add(this.primitive);
            hash.Add(this.reference);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(SqlValue left, SqlValue right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SqlValue left, SqlValue right)
    {
        return !left.Equals(right);
    }

    /// <summary>
    /// Diagnostic-only string rendering, surfaced via
    /// <see cref="DebuggerDisplayAttribute"/>. NOT user-visible — production
    /// paths produce purpose-built formats (CAST-to-varchar, error-message
    /// rendering, key-violation rendering) instead. Internal so callers in
    /// the same project (e.g. <see cref="Parser.Expressions.Value.DebugDisplay"/>)
    /// can compose it; not for use across assemblies.
    /// </summary>
    internal string DebugDisplay() => this.IsNull ? $"NULL ({this.Type})" : $"{this.AsCurrentType()} ({this.Type})";

    private string AsCurrentType() => this.Type switch
    {
        _ when this.Type == SqlType.Int32 => this.AsInt32.ToString(CultureInfo.InvariantCulture),
        _ when this.Type == SqlType.BigInt => this.AsInt64.ToString(CultureInfo.InvariantCulture),
        _ when this.Type == SqlType.SmallInt => this.AsInt16.ToString(CultureInfo.InvariantCulture),
        _ when this.Type == SqlType.TinyInt => this.AsByte.ToString(CultureInfo.InvariantCulture),
        _ when this.Type == SqlType.Bit => this.AsBoolean ? "1" : "0",
        { Category: SqlTypeCategory.String } => $"'{this.AsString}'",
        VarbinarySqlType or BinarySqlType or ImageSqlType or RowVersionSqlType => $"0x{Convert.ToHexString(this.AsBytes)}",
        _ when this.Type == SqlType.Date => $"'{this.AsDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
        _ when this.Type == SqlType.DateTime => $"'{this.AsDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}'",
        _ when this.Type == SqlType.SmallDateTime => $"'{this.AsSmallDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}'",
        DateTime2SqlType dt2 => $"'{this.AsDateTime2.ToString(DateTime2Format(dt2.precision), CultureInfo.InvariantCulture)}'",
        TimeSqlType tt => $"'{FormatTime(this.AsTime, tt.precision)}'",
        DateTimeOffsetSqlType dto => $"'{FormatDateTimeOffset(this.AsDateTimeOffset, dto.precision)}'",
        _ when this.Type == SqlType.UniqueIdentifier => $"'{this.AsGuid:D}'",
        _ when this.Type == SqlType.HierarchyId => $"'{HierarchyIdSqlType.PathToString(this.AsHierarchyId)}'",
        DecimalSqlType d => this.AsDecimal.ToString($"F{d.scale}", CultureInfo.InvariantCulture),
        _ when this.Type == SqlType.Float => this.AsDouble.ToString("G15", CultureInfo.InvariantCulture),
        _ when this.Type == SqlType.Real => this.AsSingle.ToString("G7", CultureInfo.InvariantCulture),
        _ when this.Type == SqlType.Money || this.Type == SqlType.SmallMoney => this.AsMoney.ToString("F4", CultureInfo.InvariantCulture),
        SqlVariantSqlType => this.AsVariantInner.AsCurrentType(),
        _ => "?",
    };
}
