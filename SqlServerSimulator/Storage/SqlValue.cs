using System.Data.SqlTypes;
#if DEBUG
using System.Globalization;
#endif

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

    /// <summary>Non-NULL SQL <c>varchar</c> value (UTF-8 on disk, .NET string in memory).</summary>
    public static SqlValue FromVarchar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Varchar, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL <c>nvarchar</c> value (UTF-16 LE on disk, .NET string in memory).</summary>
    public static SqlValue FromNVarchar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.NVarchar, 0, value, isNull: false);
    }

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
    /// Constructs a string-typed value of the given SQL type. Used by string
    /// functions (UPPER, LEFT, etc.) that preserve their input's string subtype.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not a string type.</exception>
    public static SqlValue FromString(SqlType type, string value) =>
        type == SqlType.Varchar ? FromVarchar(value)
        : type == SqlType.NVarchar ? FromNVarchar(value)
        : type == SqlType.SystemName ? FromSystemName(value)
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
        : this.Type != SqlType.Varchar && this.Type != SqlType.NVarchar && this.Type != SqlType.SystemName
            ? throw new InvalidOperationException($"Value is {this.Type}, not a string type.")
            : (string)this.reference!;

    /// <summary>Returns the value as <see cref="byte"/><c>[]</c>. Throws if NULL or not a varbinary value.</summary>
    public byte[] AsBytes => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != SqlType.Varbinary
            ? throw new InvalidOperationException($"Value is {this.Type}, not varbinary.")
            : (byte[])this.reference!;

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
        var t when t == SqlType.Varchar || t == SqlType.NVarchar || t == SqlType.SystemName => this.AsString,
        var t when t == SqlType.Varbinary => this.AsBytes,
        // SqlClient surfaces a date column as DateTime at midnight (Kind=Unspecified)
        // when read via the untyped accessors. EF's DateOnly mapping reads
        // through GetDateTime and converts on its own.
        var t when t == SqlType.Date => this.AsDate.ToDateTime(TimeOnly.MinValue),
        var t when t == SqlType.DateTime => this.AsDateTime,
        var t when t == SqlType.SmallDateTime => this.AsSmallDateTime,
        DateTime2SqlType => this.AsDateTime2,
        // SqlClient surfaces a time column as TimeSpan via the untyped
        // accessors. EF's TimeOnly mapping reads through GetFieldValue<TimeOnly>().
        TimeSqlType => this.AsTime,
        DateTimeOffsetSqlType => this.AsDateTimeOffset,
        var t when t == SqlType.UniqueIdentifier => this.AsGuid,
        _ => throw new NotSupportedException($"No object representation for {this.Type}."),
    };


    public bool Equals(SqlValue other) =>
        this.Type == other.Type
        && this.IsNull == other.IsNull
        && (this.IsNull
            || (IsStringTypeRef(this.Type)
                ? Collation.Default.Equals(TrimTrailing((string)this.reference!), TrimTrailing((string)other.reference!))
                : this.Type is DateTimeOffsetSqlType
                    ? this.primitive == other.primitive
                    : this.Type == SqlType.UniqueIdentifier
                        ? (Guid)this.reference! == (Guid)other.reference!
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
    /// True for the variable-length string types that participate in SQL
    /// Server's collation-aware comparison (case folding + ANSI trailing-space
    /// padding). Used to gate the per-string equality / hash / compare paths.
    /// </summary>
    private static bool IsStringTypeRef(SqlType t) =>
        t == SqlType.Varchar || t == SqlType.NVarchar || t == SqlType.SystemName;

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
    /// <exception cref="InvalidOperationException">Either operand is NULL.</exception>
    /// <exception cref="NotSupportedException">The operands' types differ, or comparison for that type isn't implemented yet.</exception>
    public int CompareTo(SqlValue other) =>
        this.IsNull || other.IsNull ? throw new InvalidOperationException("CompareTo on NULL is undefined; check IsNull before calling.")
        : this.Type != other.Type ? throw new NotSupportedException($"Cross-type comparison isn't implemented: {this.Type} vs {other.Type}.")
        : this.Type == SqlType.Int32 ? this.AsInt32.CompareTo(other.AsInt32)
        : this.Type == SqlType.BigInt ? this.AsInt64.CompareTo(other.AsInt64)
        : this.Type == SqlType.SmallInt ? this.AsInt16.CompareTo(other.AsInt16)
        : this.Type == SqlType.TinyInt ? this.AsByte.CompareTo(other.AsByte)
        : this.Type == SqlType.Bit ? this.AsBoolean.CompareTo(other.AsBoolean)
        : IsStringTypeRef(this.Type) ? Collation.Default.Compare(TrimTrailing((string)this.reference!), TrimTrailing((string)other.reference!))
        : this.Type == SqlType.Varbinary ? this.AsBytes.AsSpan().SequenceCompareTo(other.AsBytes)
        : this.Type == SqlType.Date ? this.primitive.CompareTo(other.primitive)
        : this.Type == SqlType.DateTime ? this.primitive.CompareTo(other.primitive)
        : this.Type == SqlType.SmallDateTime ? this.primitive.CompareTo(other.primitive)
        : this.Type is DateTime2SqlType ? this.primitive.CompareTo(other.primitive)
        : this.Type is TimeSqlType ? this.primitive.CompareTo(other.primitive)
        : this.Type is DateTimeOffsetSqlType ? this.primitive.CompareTo(other.primitive)
        : this.Type == SqlType.UniqueIdentifier ? new SqlGuid(this.AsGuid).CompareTo(new SqlGuid(other.AsGuid))
        : throw new NotSupportedException($"Comparison for {this.Type} isn't implemented yet.");

    public override bool Equals(object? obj) => obj is SqlValue other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(this.Type);
        hash.Add(this.IsNull);
        if (this.IsNull)
            return hash.ToHashCode();

        if (IsStringTypeRef(this.Type))
        {
            // Trailing spaces and case folding are part of equality, so the
            // hash must agree.
            hash.Add(Collation.Default.GetHashCode(TrimTrailing((string)this.reference!)));
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

#if DEBUG
    public override string ToString() => this.IsNull ? $"NULL ({this.Type})" : $"{this.AsCurrentType()} ({this.Type})";

    private string AsCurrentType() =>
        this.Type == SqlType.Int32 ? this.AsInt32.ToString(CultureInfo.InvariantCulture) :
        this.Type == SqlType.BigInt ? this.AsInt64.ToString(CultureInfo.InvariantCulture) :
        this.Type == SqlType.SmallInt ? this.AsInt16.ToString(CultureInfo.InvariantCulture) :
        this.Type == SqlType.TinyInt ? this.AsByte.ToString(CultureInfo.InvariantCulture) :
        this.Type == SqlType.Bit ? (this.AsBoolean ? "1" : "0") :
        this.Type == SqlType.Varchar || this.Type == SqlType.NVarchar || this.Type == SqlType.SystemName ? $"'{this.AsString}'" :
        this.Type == SqlType.Varbinary ? $"0x{Convert.ToHexString(this.AsBytes)}" :
        this.Type == SqlType.Date ? $"'{this.AsDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'" :
        this.Type == SqlType.DateTime ? $"'{this.AsDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}'" :
        this.Type == SqlType.SmallDateTime ? $"'{this.AsSmallDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}'" :
        this.Type is DateTime2SqlType dt2 ? $"'{this.AsDateTime2.ToString(DateTime2Format(dt2.precision), CultureInfo.InvariantCulture)}'" :
        this.Type is TimeSqlType tt ? $"'{FormatTime(this.AsTime, tt.precision)}'" :
        this.Type is DateTimeOffsetSqlType dto ? $"'{FormatDateTimeOffset(this.AsDateTimeOffset, dto.precision)}'" :
        this.Type == SqlType.UniqueIdentifier ? $"'{this.AsGuid:D}'" :
        "?";
#endif
}
