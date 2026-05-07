using System.Buffers.Binary;

namespace SqlServerSimulator.Storage;

internal sealed class DateSqlType() : SqlType(SqlTypeCategory.DateTime)
{
    public override Type ClrType => typeof(DateTime);

    public override bool IsFixedLength => true;

    public override int FixedLength => 3;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var days = value.AsDate.DayNumber;
        destination[0] = (byte)days;
        destination[1] = (byte)(days >> 8);
        destination[2] = (byte)(days >> 16);
        return 3;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        var days = source[0] | (source[1] << 8) | (source[2] << 16);
        return SqlValue.FromDate(DateOnly.FromDayNumber(days));
    }

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromDate(raw switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => throw new NotSupportedException($"Don't know how to convert raw parameter value of type {raw.GetType().Name} to {this}."),
    });

    public override string ToString() => "date";
}

/// <summary>
/// Legacy <c>datetime</c> backing type. Stores rounded 100-ns ticks in
/// <see cref="SqlValue"/>'s primitive slot, decoded on demand via
/// <see cref="SqlValue.AsDateTime"/>.
/// </summary>
internal sealed class DateTimeSqlType() : SqlType(SqlTypeCategory.DateTime)
{
    public override Type ClrType => typeof(DateTime);

    /// <summary>Reference date for the day-count portion of legacy datetime.</summary>
    public static readonly DateTime BaseDate = new(1900, 1, 1);

    /// <summary>Day count of <c>1753-01-01</c> relative to <see cref="BaseDate"/>.</summary>
    public const int MinDayCount = -53690;

    /// <summary>Day count of <c>9999-12-31</c> relative to <see cref="BaseDate"/>.</summary>
    public const int MaxDayCount = 2958463;

    /// <summary>Number of 1/300-second ticks in a day (300 × 86400).</summary>
    public const int TicksPerDay = 25_920_000;

    public override bool IsFixedLength => true;

    public override int FixedLength => 8;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var dt = value.AsDateTime;
        var dayCount = (int)(dt.Date - BaseDate).TotalDays;
        var timeUnits = (uint)(((dt.TimeOfDay.Ticks * 300) + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, timeUnits);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], dayCount);
        return 8;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        var timeUnits = BinaryPrimitives.ReadUInt32LittleEndian(source);
        var dayCount = BinaryPrimitives.ReadInt32LittleEndian(source[4..]);
        var timeTicks = timeUnits * TimeSpan.TicksPerSecond / 300;
        return SqlValue.FromDateTimeUnchecked(BaseDate.AddDays(dayCount).AddTicks(timeTicks));
    }

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromDateTime(raw switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => throw new NotSupportedException($"Don't know how to convert raw parameter value of type {raw.GetType().Name} to {this}."),
    });

    public override string ToString() => "datetime";
}

/// <summary>
/// <c>smalldatetime</c> backing type. Stores rounded 100-ns ticks in
/// <see cref="SqlValue"/>'s primitive slot (always aligned to a minute
/// boundary), decoded on demand via <see cref="SqlValue.AsSmallDateTime"/>.
/// </summary>
internal sealed class SmallDateTimeSqlType() : SqlType(SqlTypeCategory.DateTime)
{
    public override Type ClrType => typeof(DateTime);

    /// <summary>Reference date for the day-count portion of smalldatetime.</summary>
    public static readonly DateTime BaseDate = new(1900, 1, 1);

    /// <summary>Day count of <c>2079-06-06</c> relative to <see cref="BaseDate"/> (uint16 max).</summary>
    public const int MaxDayCount = 65535;

    /// <summary>Number of minutes in a day (24 × 60).</summary>
    public const int MinutesPerDay = 1440;

    public override bool IsFixedLength => true;

    public override int FixedLength => 4;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var sdt = value.AsSmallDateTime;
        var dayCount = (int)(sdt.Date - BaseDate).TotalDays;
        var minutes = (int)(sdt.TimeOfDay.Ticks / TimeSpan.TicksPerMinute);
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)minutes);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)dayCount);
        return 4;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        var minutes = BinaryPrimitives.ReadUInt16LittleEndian(source);
        var dayCount = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
        return SqlValue.FromSmallDateTimeUnchecked(BaseDate.AddDays(dayCount).AddMinutes(minutes));
    }

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromSmallDateTime(raw switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => throw new NotSupportedException($"Don't know how to convert raw parameter value of type {raw.GetType().Name} to {this}."),
    });

    public override string ToString() => "smalldatetime";
}

/// <remarks>
/// Layout matches SQL Server's documented <c>datetime2(N)</c> on-disk size
/// (6/7/8 bytes for precisions 0-2/3-4/5-7): a little-endian time-of-day
/// integer in units of 10^-precision seconds (3/4/5 bytes), followed by
/// the same 3-byte day count as <see cref="DateSqlType"/>. SQL Server's
/// exact byte layout isn't publicly specified at the bit level; LE matches
/// the engine's overall on-disk endianness.
/// </remarks>
/// <summary>
/// Precision-bearing concrete <c>datetime2</c> type. <see cref="SqlValue"/>
/// pattern-matches against it to read the precision-derived fields it needs
/// (rounding unit, etc.) when constructing or rendering datetime2 values.
/// </summary>
internal sealed class DateTime2SqlType(int precision) : SqlType(SqlTypeCategory.DateTime)
{
    public readonly int precision = precision;
    public readonly int timeBytes = precision <= 2 ? 3 : precision <= 4 ? 4 : 5;
    public readonly long ticksPerUnit = TicksPerPrecisionUnit(precision);

    public override Type ClrType => typeof(DateTime);

    public override string SqlServerName => "datetime2";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.timeBytes + 3;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var dt = value.AsDateTime2;
        var days = dt.Date.Subtract(System.DateTime.MinValue).Days;
        var timeUnits = dt.TimeOfDay.Ticks / this.ticksPerUnit;
        for (var i = 0; i < this.timeBytes; i++)
            destination[i] = (byte)(timeUnits >> (8 * i));
        var d = this.timeBytes;
        destination[d] = (byte)days;
        destination[d + 1] = (byte)(days >> 8);
        destination[d + 2] = (byte)(days >> 16);
        return this.FixedLength;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        long timeUnits = 0;
        for (var i = 0; i < this.timeBytes; i++)
            timeUnits |= (long)source[i] << (8 * i);
        var d = this.timeBytes;
        var days = source[d] | (source[d + 1] << 8) | (source[d + 2] << 16);
        return SqlValue.FromDateTime2(this, System.DateTime.MinValue.AddDays(days).AddTicks(timeUnits * this.ticksPerUnit));
    }

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromDateTime2(this, raw switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => throw new NotSupportedException($"Don't know how to convert raw parameter value of type {raw.GetType().Name} to {this}."),
    });

    public override string ToString() => $"datetime2({this.precision})";
}

/// <remarks>
/// Precision-bearing concrete <c>time</c> type. The time-of-day encoding
/// matches the time portion of <see cref="DateTime2SqlType"/> (units of
/// 10^-precision seconds since midnight, 3/4/5 bytes for N=0-2/3-4/5-7);
/// no date portion is stored.
/// </remarks>
internal sealed class TimeSqlType(int precision) : SqlType(SqlTypeCategory.DateTime)
{
    public readonly int precision = precision;
    public readonly int timeBytes = precision <= 2 ? 3 : precision <= 4 ? 4 : 5;
    public readonly long ticksPerUnit = TicksPerPrecisionUnit(precision);

    public override Type ClrType => typeof(TimeSpan);

    public override string SqlServerName => "time";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.timeBytes;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var timeUnits = value.AsTime.Ticks / this.ticksPerUnit;
        for (var i = 0; i < this.timeBytes; i++)
            destination[i] = (byte)(timeUnits >> (8 * i));
        return this.timeBytes;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        long timeUnits = 0;
        for (var i = 0; i < this.timeBytes; i++)
            timeUnits |= (long)source[i] << (8 * i);
        return SqlValue.FromTime(this, new TimeSpan(timeUnits * this.ticksPerUnit));
    }

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromTime(this, raw switch
    {
        TimeSpan ts => ts,
        TimeOnly to => to.ToTimeSpan(),
        _ => throw new NotSupportedException($"Don't know how to convert raw parameter value of type {raw.GetType().Name} to {this}."),
    });

    public override string ToString() => $"time({this.precision})";
}

/// <remarks>
/// Encoding: time-of-day units (3/4/5 LE bytes) of the UTC instant,
/// followed by the UTC day count (3 LE bytes), followed by the offset
/// in minutes (signed 16-bit LE, range -840 to +840). Storing the time
/// and date as UTC matches SQL Server's documented on-disk layout —
/// equality and ordering are by UTC instant — while the offset is kept
/// alongside so the original wall-clock representation round-trips.
/// </remarks>
/// <summary>
/// Precision-bearing concrete <c>datetimeoffset</c> type. <see cref="SqlValue"/>
/// pattern-matches against it for type-specific paths (rounding, formatting,
/// cross-type cast targets).
/// </summary>
internal sealed class DateTimeOffsetSqlType(int precision) : SqlType(SqlTypeCategory.DateTime)
{
    public readonly int precision = precision;
    public readonly int timeBytes = precision <= 2 ? 3 : precision <= 4 ? 4 : 5;
    public readonly long ticksPerUnit = TicksPerPrecisionUnit(precision);

    public override Type ClrType => typeof(DateTimeOffset);

    public override string SqlServerName => "datetimeoffset";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.timeBytes + 5;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var dto = value.AsDateTimeOffset;
        var utc = dto.UtcDateTime;
        var days = utc.Date.Subtract(System.DateTime.MinValue).Days;
        var timeUnits = utc.TimeOfDay.Ticks / this.ticksPerUnit;
        for (var i = 0; i < this.timeBytes; i++)
            destination[i] = (byte)(timeUnits >> (8 * i));
        var d = this.timeBytes;
        destination[d] = (byte)days;
        destination[d + 1] = (byte)(days >> 8);
        destination[d + 2] = (byte)(days >> 16);
        var offsetMinutes = (short)(dto.Offset.Ticks / TimeSpan.TicksPerMinute);
        BinaryPrimitives.WriteInt16LittleEndian(destination[(d + 3)..], offsetMinutes);
        return this.FixedLength;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        long timeUnits = 0;
        for (var i = 0; i < this.timeBytes; i++)
            timeUnits |= (long)source[i] << (8 * i);
        var d = this.timeBytes;
        var days = source[d] | (source[d + 1] << 8) | (source[d + 2] << 16);
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(source[(d + 3)..]);
        var utc = System.DateTime.SpecifyKind(System.DateTime.MinValue.AddDays(days).AddTicks(timeUnits * this.ticksPerUnit), DateTimeKind.Utc);
        return SqlValue.FromDateTimeOffset(this, new DateTimeOffset(utc).ToOffset(TimeSpan.FromMinutes(offsetMinutes)));
    }

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromDateTimeOffset(this, raw switch
    {
        DateTimeOffset dto => dto,
        // A bare DateTime is treated as +00:00 when bound to a datetimeoffset
        // parameter, matching SqlClient's behavior.
        DateTime dt => new DateTimeOffset(System.DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero),
        _ => throw new NotSupportedException($"Don't know how to convert raw parameter value of type {raw.GetType().Name} to {this}."),
    });

    public override string ToString() => $"datetimeoffset({this.precision})";
}

