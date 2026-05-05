using System.Globalization;

namespace SqlServerSimulator.Storage;

internal readonly partial struct SqlValue
{
    /// <summary>
    /// Returns this value re-typed as <paramref name="target"/> when a safe
    /// conversion exists; widens or narrows between SQL integer-family types
    /// using <c>checked</c> arithmetic so out-of-range narrowings throw
    /// <see cref="OverflowException"/>. Bit participates as a 0/1 integer
    /// (true=1, false=0; non-zero on the way back is true). Same-typed values
    /// pass through. NULLs re-type freely (no overflow possible). Cross-
    /// category coercions (integer↔string) aren't implemented.
    /// </summary>
    /// <remarks>
    /// Date/time cross-type conventions (matching SQL Server):
    /// <list type="bullet">
    /// <item><c>time → datetime2</c> fills the date portion with
    /// <c>1900-01-01</c> (SQL Server's legacy default fill); the reverse
    /// drops the date.</item>
    /// <item><c>datetime2 / date / time → datetimeoffset</c> treats the
    /// source as a <c>+00:00</c> wall-clock (no time-zone inference).</item>
    /// <item><c>datetimeoffset → datetime2 / date / time</c> returns the
    /// local (offset-bearing) value and drops the offset, not the UTC
    /// instant.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="OverflowException">Value doesn't fit the narrower target type.</exception>
    /// <exception cref="NotSupportedException">No conversion is implemented between this value's type and <paramref name="target"/>.</exception>
    public SqlValue CoerceTo(SqlType target)
    {
        if (this.Type == target)
            return this;
        if (this.IsNull)
            return Null(target);

        if (SqlType.IsIntegerCategory(this.Type) && SqlType.IsIntegerCategory(target))
        {
            var widened = this.Type == SqlType.Bit ? (this.AsBoolean ? 1L : 0L)
                : this.Type == SqlType.TinyInt ? this.AsByte
                : this.Type == SqlType.SmallInt ? this.AsInt16
                : this.Type == SqlType.Int32 ? this.AsInt32
                : this.AsInt64;
            return target == SqlType.Bit ? FromBoolean(widened != 0)
                : target == SqlType.TinyInt ? FromByte(checked((byte)widened))
                : target == SqlType.SmallInt ? FromInt16(checked((short)widened))
                : target == SqlType.Int32 ? FromInt32(checked((int)widened))
                : FromInt64(widened);
        }

        if (SqlType.IsStringCategory(this.Type) && SqlType.IsIntegerCategory(target))
            return ParseStringToInteger(this.AsString, this.Type, target);
        if (SqlType.IsIntegerCategory(this.Type) && SqlType.IsStringCategory(target))
            return FromString(target, FormatIntegerToString(this));

        // uniqueidentifier crossings: only string ↔ uid and varbinary ↔ uid
        // are allowed. Every other source/target pair surfaces as Msg 529.
        if (target == SqlType.UniqueIdentifier)
            return this.CoerceToUniqueIdentifier();
        if (this.Type == SqlType.UniqueIdentifier)
            return this.CoerceFromUniqueIdentifier(target);

        // decimal/numeric crossings: integer↔decimal (within-precision),
        // string↔decimal (parses with half-away-from-zero rounding),
        // decimal↔decimal (scale change with the same rounding rule).
        if (target is DecimalSqlType targetDecimal)
            return this.CoerceToDecimal(targetDecimal);
        if (this.Type is DecimalSqlType)
            return this.CoerceFromDecimal(target);

        // float / real crossings: any numeric or string source coerces in
        // (string parses as double, decimal/integer widens). Targets accept
        // truncation-toward-zero on float→integer and standard double→
        // decimal conversion.
        if (target == SqlType.Float || target == SqlType.Real)
            return this.CoerceToApproximate(target);
        if (SqlType.IsApproximateNumericCategory(this.Type))
            return this.CoerceFromApproximate(target);

        // money / smallmoney crossings: string parses with currency-symbol
        // and thousands-comma stripping (Msg 235 on bad input), integer/
        // decimal widen straight in via the scale-4 storage rep.
        if (SqlType.IsMoneyCategory(target))
            return this.CoerceToMoney(target);
        if (SqlType.IsMoneyCategory(this.Type))
            return this.CoerceFromMoney(target);

        // Date/time → integer: only the legacy types (datetime / smalldatetime)
        // round-trip through an integer day count; everything else throws
        // Msg 529 from inside the helper.
        if (SqlType.IsDateTimeCategory(this.Type) && SqlType.IsIntegerCategory(target))
            return this.CoerceDateTimeToInteger(target);

        // Date/time crossings: dispatch by target, with each per-target helper
        // handling all valid sources. Date/time → string is its own branch
        // because the target is a string-family member.
        return target switch
        {
            _ when target == SqlType.Date => this.CoerceToDate(),
            _ when target == SqlType.DateTime => this.CoerceToDateTime(),
            _ when target == SqlType.SmallDateTime => this.CoerceToSmallDateTime(),
            DateTime2SqlType targetDt2 => this.CoerceToDateTime2(targetDt2),
            TimeSqlType targetTime => this.CoerceToTime(targetTime),
            DateTimeOffsetSqlType targetDto => this.CoerceToDateTimeOffset(targetDto),
            _ when SqlType.IsStringCategory(target) => this.CoerceDateTimeToString(target),
            _ => throw new NotSupportedException($"No coercion implemented from {this.Type} to {target}."),
        };
    }

    private SqlValue CoerceToDate() => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromDate(ParseDate(this.AsString)),
        _ when this.Type == SqlType.DateTime => FromDate(DateOnly.FromDateTime(this.AsDateTime)),
        _ when this.Type == SqlType.SmallDateTime => FromDate(DateOnly.FromDateTime(this.AsSmallDateTime)),
        DateTime2SqlType => FromDate(DateOnly.FromDateTime(this.AsDateTime2)),
        DateTimeOffsetSqlType => FromDate(DateOnly.FromDateTime(this.AsDateTimeOffset.DateTime)),
        _ when SqlType.IsIntegerCategory(this.Type) => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, SqlType.Date),
        _ => throw new NotSupportedException($"No coercion implemented from {this.Type} to {SqlType.Date}."),
    };

    private SqlValue CoerceToDateTime() => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromDateTime(ParseLegacyDateTime(this.AsString)),
        _ when this.Type == SqlType.Date => FromDateTime(this.AsDate.ToDateTime(TimeOnly.MinValue)),
        _ when this.Type == SqlType.SmallDateTime => FromDateTime(this.AsSmallDateTime),
        DateTime2SqlType => FromDateTime(this.AsDateTime2),
        TimeSqlType => FromDateTime(new DateTime(1900, 1, 1).Add(this.AsTime)),
        DateTimeOffsetSqlType => FromDateTime(this.AsDateTimeOffset.DateTime),
        _ when SqlType.IsIntegerCategory(this.Type) => CoerceIntegerDaysToDateTime(AsInt64Widened(this)),
        DecimalSqlType => CoerceFractionalDaysToDateTime(this.AsDecimal),
        _ when this.Type == SqlType.Float => CoerceFractionalDaysToDateTime((decimal)this.AsDouble),
        _ when this.Type == SqlType.Real => CoerceFractionalDaysToDateTime((decimal)this.AsSingle),
        _ when SqlType.IsMoneyCategory(this.Type) => CoerceFractionalDaysToDateTime(this.AsMoney),
        _ => throw new NotSupportedException($"No coercion implemented from {this.Type} to {SqlType.DateTime}."),
    };

    private SqlValue CoerceToSmallDateTime() => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromSmallDateTime(ParseSmallDateTime(this.AsString)),
        _ when this.Type == SqlType.Date => FromSmallDateTime(this.AsDate.ToDateTime(TimeOnly.MinValue)),
        _ when this.Type == SqlType.DateTime => FromSmallDateTime(this.AsDateTime),
        DateTime2SqlType => FromSmallDateTime(this.AsDateTime2),
        TimeSqlType => FromSmallDateTime(new DateTime(1900, 1, 1).Add(this.AsTime)),
        DateTimeOffsetSqlType => FromSmallDateTime(this.AsDateTimeOffset.DateTime),
        _ when SqlType.IsIntegerCategory(this.Type) => CoerceIntegerDaysToSmallDateTime(AsInt64Widened(this)),
        DecimalSqlType => CoerceFractionalDaysToSmallDateTime(this.AsDecimal),
        _ when this.Type == SqlType.Float => CoerceFractionalDaysToSmallDateTime((decimal)this.AsDouble),
        _ when this.Type == SqlType.Real => CoerceFractionalDaysToSmallDateTime((decimal)this.AsSingle),
        _ when SqlType.IsMoneyCategory(this.Type) => CoerceFractionalDaysToSmallDateTime(this.AsMoney),
        _ => throw new NotSupportedException($"No coercion implemented from {this.Type} to {SqlType.SmallDateTime}."),
    };

    private SqlValue CoerceToDateTime2(DateTime2SqlType target) => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromDateTime2(target, ParseDateTime2(this.AsString)),
        _ when this.Type == SqlType.Date => FromDateTime2(target, this.AsDate.ToDateTime(TimeOnly.MinValue)),
        _ when this.Type == SqlType.DateTime => FromDateTime2(target, this.AsDateTime),
        _ when this.Type == SqlType.SmallDateTime => FromDateTime2(target, this.AsSmallDateTime),
        DateTime2SqlType => FromDateTime2(target, this.AsDateTime2),
        // SQL Server fills the date portion with 1900-01-01 for time → datetime2,
        // matching its legacy default. The reverse direction drops the date.
        TimeSqlType => FromDateTime2(target, new DateTime(1900, 1, 1).Add(this.AsTime)),
        // datetimeoffset → datetime2 returns the local (offset-bearing)
        // wall-clock and discards the offset, not the UTC instant.
        DateTimeOffsetSqlType => FromDateTime2(target, this.AsDateTimeOffset.DateTime),
        _ when SqlType.IsIntegerCategory(this.Type) => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
        _ => throw new NotSupportedException($"No coercion implemented from {this.Type} to {target}."),
    };

    private SqlValue CoerceToTime(TimeSqlType target) => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromTime(target, ParseTime(this.AsString)),
        TimeSqlType => FromTime(target, this.AsTime),
        _ when this.Type == SqlType.DateTime => FromTime(target, this.AsDateTime.TimeOfDay),
        _ when this.Type == SqlType.SmallDateTime => FromTime(target, this.AsSmallDateTime.TimeOfDay),
        DateTime2SqlType => FromTime(target, this.AsDateTime2.TimeOfDay),
        DateTimeOffsetSqlType => FromTime(target, this.AsDateTimeOffset.DateTime.TimeOfDay),
        _ when SqlType.IsIntegerCategory(this.Type) => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
        _ => throw new NotSupportedException($"No coercion implemented from {this.Type} to {target}."),
    };

    private SqlValue CoerceToDateTimeOffset(DateTimeOffsetSqlType target) => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromDateTimeOffset(target, ParseDateTimeOffset(this.AsString)),
        // Casts up from offset-less types treat the source as a +00:00
        // wall-clock (no time-zone inference, matching SQL Server).
        _ when this.Type == SqlType.Date => FromDateTimeOffset(target, new DateTimeOffset(this.AsDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
        _ when this.Type == SqlType.DateTime => FromDateTimeOffset(target, new DateTimeOffset(this.AsDateTime, TimeSpan.Zero)),
        _ when this.Type == SqlType.SmallDateTime => FromDateTimeOffset(target, new DateTimeOffset(this.AsSmallDateTime, TimeSpan.Zero)),
        DateTime2SqlType => FromDateTimeOffset(target, new DateTimeOffset(this.AsDateTime2, TimeSpan.Zero)),
        TimeSqlType => FromDateTimeOffset(target, new DateTimeOffset(new DateTime(1900, 1, 1).Add(this.AsTime), TimeSpan.Zero)),
        DateTimeOffsetSqlType => FromDateTimeOffset(target, this.AsDateTimeOffset),
        _ when SqlType.IsIntegerCategory(this.Type) => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
        _ => throw new NotSupportedException($"No coercion implemented from {this.Type} to {target}."),
    };

    /// <summary>
    /// Coerces an integer day count into legacy <c>datetime</c>. The integer
    /// is interpreted as days-since-1900-01-01 (the legacy base date);
    /// fractional days come from string- or future float/decimal-source
    /// paths, not this one. Out-of-range integers raise Msg 8115 with the
    /// target type name in the text — distinct from the Msg 242 used by
    /// string-source overflow.
    /// </summary>
    private static SqlValue CoerceIntegerDaysToDateTime(long days) =>
        days is < DateTimeSqlType.MinDayCount or > DateTimeSqlType.MaxDayCount
            ? throw SimulatedSqlException.ArithmeticOverflow("datetime")
            : FromDateTimeUnchecked(DateTimeSqlType.BaseDate.AddDays(days));

    /// <summary>
    /// Coerces an integer day count into <c>smalldatetime</c>. Range
    /// constraint matches the on-disk uint16 day count (0-65535); negative
    /// values and values past the type's maximum raise Msg 8115 with the
    /// target name.
    /// </summary>
    private static SqlValue CoerceIntegerDaysToSmallDateTime(long days) =>
        days is < 0 or > SmallDateTimeSqlType.MaxDayCount
            ? throw SimulatedSqlException.ArithmeticOverflow("smalldatetime")
            : FromSmallDateTimeUnchecked(SmallDateTimeSqlType.BaseDate.AddDays(days));

    /// <summary>
    /// Coerces a fractional day count (decimal / float / real / money source)
    /// into legacy <c>datetime</c>. The whole part is days-since-1900-01-01;
    /// the fractional part is fraction-of-a-day (verified <c>0.5 → noon</c>,
    /// <c>1.25 → 1900-01-02 06:00:00</c>). The result is routed through
    /// <see cref="FromDateTime(DateTime)"/> so it picks up the same
    /// half-up rounding to legacy 1/300-second tick that string and direct
    /// <c>DateTime</c> sources use.
    /// </summary>
    private static SqlValue CoerceFractionalDaysToDateTime(decimal days) =>
        days is < DateTimeSqlType.MinDayCount or > DateTimeSqlType.MaxDayCount
            ? throw SimulatedSqlException.ArithmeticOverflow("datetime")
            : FromDateTime(DateTimeSqlType.BaseDate.AddTicks((long)decimal.Round(days * TimeSpan.TicksPerDay, 0, MidpointRounding.AwayFromZero)));

    /// <summary>
    /// Fractional-day counterpart of <see cref="CoerceIntegerDaysToSmallDateTime"/>.
    /// Routes through <see cref="FromSmallDateTime(DateTime)"/> for
    /// minute-boundary rounding.
    /// </summary>
    private static SqlValue CoerceFractionalDaysToSmallDateTime(decimal days) =>
        days is < 0 or > SmallDateTimeSqlType.MaxDayCount
            ? throw SimulatedSqlException.ArithmeticOverflow("smalldatetime")
            : FromSmallDateTime(SmallDateTimeSqlType.BaseDate.AddTicks((long)decimal.Round(days * TimeSpan.TicksPerDay, 0, MidpointRounding.AwayFromZero)));

    /// <summary>
    /// Coerces a legacy <c>datetime</c> or <c>smalldatetime</c> value to an
    /// integer day count, with fractional days rounded half-away-from-zero
    /// (matching SQL Server). Other date/time sources raise Msg 529
    /// (explicit-CAST-not-allowed). Narrow integer targets (tinyint /
    /// smallint) range-check the day count and raise Msg 8115 with the
    /// target name on overflow — different from the Msg 244 used by
    /// string-source overflow.
    /// </summary>
    private SqlValue CoerceDateTimeToInteger(SqlType target)
    {
        if (this.Type != SqlType.DateTime && this.Type != SqlType.SmallDateTime)
            throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target);

        var ticks = this.Type == SqlType.DateTime
            ? this.AsDateTime.Ticks - DateTimeSqlType.BaseDate.Ticks
            : this.AsSmallDateTime.Ticks - SmallDateTimeSqlType.BaseDate.Ticks;
        // Half-away-from-zero rounding: bias by +half-day for non-negative
        // ticks, -half-day for negative, then truncate via integer division.
        var halfDay = TimeSpan.TicksPerDay / 2;
        var biased = ticks >= 0 ? ticks + halfDay : ticks - halfDay;
        var days = biased / TimeSpan.TicksPerDay;

        try
        {
            return target == SqlType.Bit ? FromBoolean(days != 0)
                : target == SqlType.TinyInt ? FromByte(checked((byte)days))
                : target == SqlType.SmallInt ? FromInt16(checked((short)days))
                : target == SqlType.Int32 ? FromInt32(checked((int)days))
                : FromInt64(days);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(target.ToString()!);
        }
    }

    /// <summary>
    /// Widens any integer-category value to <see cref="long"/>. Mirrors the
    /// inline expression used inside the integer-widening branch of
    /// <see cref="CoerceTo"/>; extracted so the integer→datetime paths and
    /// the date-arithmetic operator can share it without duplicating the
    /// per-source-type pattern.
    /// </summary>
    internal static long AsInt64Widened(SqlValue value) =>
        value.Type == SqlType.Bit ? (value.AsBoolean ? 1L : 0L)
        : value.Type == SqlType.TinyInt ? value.AsByte
        : value.Type == SqlType.SmallInt ? value.AsInt16
        : value.Type == SqlType.Int32 ? value.AsInt32
        : value.AsInt64;

    /// <summary>
    /// Constructs a legacy <c>datetime</c> from a tick count measured from
    /// 1900-01-01 00:00:00 (i.e. <c>value.Ticks - BaseDate.Ticks</c>). Used
    /// by the date-arithmetic path, where operands resolve to ticks-from-base
    /// before the operator runs and the post-arithmetic tick count needs to
    /// re-materialize as a datetime. Out-of-range raises Msg 8115 with the
    /// target type name in the text.
    /// </summary>
    internal static SqlValue CoerceTicksSinceBaseToDateTime(long ticks)
    {
        var minTicks = DateTimeSqlType.MinDayCount * TimeSpan.TicksPerDay;
        var maxTicks = ((DateTimeSqlType.MaxDayCount + 1L) * TimeSpan.TicksPerDay) - 1;
        return ticks < minTicks || ticks > maxTicks
            ? throw SimulatedSqlException.ArithmeticOverflow("datetime")
            : FromDateTimeUnchecked(DateTimeSqlType.BaseDate.AddTicks(ticks));
    }

    /// <summary>
    /// Constructs a <c>smalldatetime</c> from ticks-since-1900-01-01.
    /// Inputs are expected to be minute-aligned (every operand the
    /// arithmetic path produces is a multiple of <c>TicksPerMinute</c>).
    /// Out-of-range raises Msg 8115 with the target name.
    /// </summary>
    internal static SqlValue CoerceTicksSinceBaseToSmallDateTime(long ticks)
    {
        var maxTicks = (SmallDateTimeSqlType.MaxDayCount * TimeSpan.TicksPerDay)
            + ((SmallDateTimeSqlType.MinutesPerDay - 1) * TimeSpan.TicksPerMinute);
        return ticks < 0 || ticks > maxTicks
            ? throw SimulatedSqlException.ArithmeticOverflow("smalldatetime")
            : FromSmallDateTimeUnchecked(SmallDateTimeSqlType.BaseDate.AddTicks(ticks));
    }

    private SqlValue CoerceDateTimeToString(SqlType target) => this.Type switch
    {
        _ when this.Type == SqlType.Date => FromString(target, this.AsDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        _ when this.Type == SqlType.DateTime => FromString(target, FormatLegacyDateTime(this.AsDateTime)),
        _ when this.Type == SqlType.SmallDateTime => FromString(target, FormatLegacyDateTime(this.AsSmallDateTime)),
        DateTime2SqlType srcDt2 => FromString(target, this.AsDateTime2.ToString(DateTime2Format(srcDt2.precision), CultureInfo.InvariantCulture)),
        TimeSqlType srcTime => FromString(target, FormatTime(this.AsTime, srcTime.precision)),
        DateTimeOffsetSqlType srcDto => FromString(target, FormatDateTimeOffset(this.AsDateTimeOffset, srcDto.precision)),
        _ => throw new NotSupportedException($"No coercion implemented from {this.Type} to {target}."),
    };

    /// <summary>
    /// Parses a string into an integer-family <see cref="SqlValue"/> matching
    /// SQL Server's CAST semantics. Empty / whitespace-only strings convert
    /// to 0. Leading <c>+</c>/<c>-</c> signs and surrounding whitespace are
    /// accepted; decimal points, scientific notation, and hex prefixes are
    /// not (they raise Msg 245). For <c>bit</c> targets, the literal words
    /// <c>true</c>/<c>false</c> are accepted case-insensitively, and any
    /// digit-string with at least one non-zero digit yields true regardless
    /// of magnitude (no overflow check applies — bit holds a single boolean).
    /// Overflow on <c>tinyint</c>/<c>smallint</c>/<c>int</c> raises a
    /// target-specific message; <c>bigint</c> overflow falls through to the
    /// generic Msg 8115 arithmetic-overflow path.
    /// </summary>
    private static SqlValue ParseStringToInteger(string source, SqlType sourceType, SqlType target)
    {
        if (target == SqlType.Bit)
            return ParseStringToBit(source, sourceType);

        if (string.IsNullOrWhiteSpace(source))
            return FromInt64(0).CoerceTo(target);

        if (!long.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            // long.TryParse fails for both bad format and out-of-long-range.
            // BigInteger disambiguates: if it parses as BigInteger, it's
            // valid digits — overflow rather than format error.
            var trimmed = source.Trim();
            if (System.Numerics.BigInteger.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw OverflowOnConvert(sourceType, source, target);
            throw SimulatedSqlException.ConversionFailedFromString(sourceType, source, target);
        }

        try
        {
            return FromInt64(parsed).CoerceTo(target);
        }
        catch (OverflowException)
        {
            throw OverflowOnConvert(sourceType, source, target);
        }
    }

    /// <summary>
    /// Bit-target string parsing. Bypasses the long-range check that the
    /// integer-family path uses: SQL Server treats <c>bit</c> CAST as a
    /// truthiness test (any non-zero digit → true), so a digit string longer
    /// than <see cref="long"/> can hold (e.g. <c>'9999999999999999999'</c>)
    /// still casts cleanly without raising an overflow error.
    /// </summary>
    private static SqlValue ParseStringToBit(string source, SqlType sourceType)
    {
        var trimmed = source.Trim();
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
            return FromBoolean(true);
        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
            return FromBoolean(false);

        if (trimmed.Length == 0)
            return FromBoolean(false);

        var body = trimmed[0] is '+' or '-' ? trimmed[1..] : trimmed;
        if (body.Length == 0)
            throw SimulatedSqlException.ConversionFailedFromString(sourceType, source, SqlType.Bit);

        var sawNonZero = false;
        foreach (var c in body)
        {
            if (c is < '0' or > '9')
                throw SimulatedSqlException.ConversionFailedFromString(sourceType, source, SqlType.Bit);
            if (c != '0')
                sawNonZero = true;
        }
        return FromBoolean(sawNonZero);
    }

    /// <summary>
    /// Maps a target integer type to its target-specific overflow exception.
    /// <c>tinyint</c>/<c>smallint</c> use Msg 244 with the SQL-internal
    /// names <c>INT1</c>/<c>INT2</c>; <c>int</c> uses Msg 248 (lowercase
    /// "int"); <c>bigint</c> falls through to Msg 8115 (and never reaches
    /// here for string→int paths because long parse caught the overflow,
    /// but kept for symmetry).
    /// </summary>
    private static SimulatedSqlException OverflowOnConvert(SqlType sourceType, string sourceValue, SqlType target) =>
        target == SqlType.TinyInt ? SimulatedSqlException.OverflowConvertingNarrowInt(sourceType, sourceValue, "INT1")
        : target == SqlType.SmallInt ? SimulatedSqlException.OverflowConvertingNarrowInt(sourceType, sourceValue, "INT2")
        : target == SqlType.Int32 ? SimulatedSqlException.OverflowConvertingToInt(sourceType, sourceValue)
        : SimulatedSqlException.ArithmeticOverflow(target.ToString()!);

    private SqlValue CoerceToMoney(SqlType target) => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromMoney(target, ParseMoneyString(this.AsString)),
        _ when SqlType.IsIntegerCategory(this.Type) => FromMoney(target, AsInt64Widened(this)),
        DecimalSqlType => FromMoney(target, this.AsDecimal),
        _ when SqlType.IsMoneyCategory(this.Type) => FromMoney(target, this.AsMoney),
        _ when this.Type == SqlType.Float => FromMoney(target, (decimal)this.AsDouble),
        _ when this.Type == SqlType.Real => FromMoney(target, (decimal)this.AsSingle),
        _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
    };

    private SqlValue CoerceFromMoney(SqlType target)
    {
        var m = this.AsMoney;
        // Money → varchar uses 2 decimal places by default — the storage
        // scale is 4 but the textual default is 2 (verified against SQL
        // Server 2025: <c>$5.95 → '5.95'</c>, <c>$0 → '0.00'</c>,
        // money max → <c>'922337203685477.58'</c>).
        if (SqlType.IsStringCategory(target))
            return FromString(target, m.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        if (target is DecimalSqlType targetDecimal)
            return FromDecimal(targetDecimal, RoundAndOverflowCheck(m, targetDecimal));
        if (SqlType.IsMoneyCategory(target))
            return FromMoney(target, m);
        if (target == SqlType.Float)
            return FromDouble((double)m);
        if (target == SqlType.Real)
            return FromSingle((float)m);
        if (target == SqlType.DateTime)
            return this.CoerceToDateTime();
        if (target == SqlType.SmallDateTime)
            return this.CoerceToSmallDateTime();
        if (!SqlType.IsIntegerCategory(target))
            throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target);

        // Money → integer truncates toward zero (consistent with decimal/float).
        var truncated = decimal.Truncate(m);
        try
        {
            return target == SqlType.Bit ? FromBoolean(truncated != 0)
                : target == SqlType.TinyInt ? FromByte(checked((byte)truncated))
                : target == SqlType.SmallInt ? FromInt16(checked((short)truncated))
                : target == SqlType.Int32 ? FromInt32(checked((int)truncated))
                : FromInt64(checked((long)truncated));
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(target.ToString()!);
        }
    }

    /// <summary>
    /// Parses a string into a <see cref="decimal"/> for money-target CAST.
    /// Strips a leading currency symbol from the documented set and any
    /// thousands commas, accepts an optional sign in either pre- or
    /// post-symbol position, then defers to <c>decimal.TryParse</c>
    /// for the numeric body. Bad input raises Msg 235 (the money-specific
    /// "Cannot convert a char value" message — distinct from Msg 8114
    /// used by decimal / float). Verified against SQL Server 2025:
    /// <list type="bullet">
    /// <item><c>'5.95'</c>, <c>'$5.95'</c>, <c>'-$5.95'</c>, <c>'$-5.95'</c>
    /// all valid</item>
    /// <item><c>'  $5.95  '</c> (surrounding whitespace) valid</item>
    /// <item><c>'$5,000.00'</c> (thousands comma) valid</item>
    /// <item><c>'5.5e2'</c> rejected</item>
    /// </list>
    /// </summary>
    private static decimal ParseMoneyString(string source)
    {
        var span = source.AsSpan().Trim();
        // Optional sign before currency symbol.
        var negative = false;
        if (span.Length > 0 && (span[0] == '+' || span[0] == '-'))
        {
            negative = span[0] == '-';
            span = span[1..];
        }
        // Optional currency symbol — drop one if present, else carry on.
        if (span.Length > 0 && IsCurrencySymbol(span[0]))
            span = span[1..];
        // A second sign is allowed when the first slot held the symbol
        // (e.g. <c>'$-5.95'</c>); otherwise a duplicate sign here would
        // be a parse error.
        if (span.Length > 0 && (span[0] == '+' || span[0] == '-'))
        {
            negative ^= span[0] == '-';
            span = span[1..];
        }
        // Strip thousands commas — money parsing is lenient about them
        // even though the literal grammar is not.
        Span<char> buffer = stackalloc char[span.Length];
        var written = 0;
        foreach (var c in span)
        {
            if (c != ',')
                buffer[written++] = c;
        }
        var body = buffer[..written];
        // SQL Server's money parser does NOT accept scientific notation,
        // and empty/whitespace-only bodies raise the money-specific Msg 235.
        // Both conditions are folded into the single TryParse short-circuit
        // so the analyzer's simplification rule stays satisfied.
        return body.Length == 0
            || body.IndexOfAny(['e', 'E']) >= 0
            || !decimal.TryParse(
                body,
                System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d)
                    ? throw SimulatedSqlException.CannotConvertCharToMoney()
                    : negative ? -d : d;
    }

    /// <summary>
    /// True for the currency symbols SQL Server accepts in <c>money</c>
    /// literals and string-to-money CAST. Set was verified against SQL
    /// Server 2025: dollar / cent / pound / yen / euro / Thai baht plus the
    /// first half of the Unicode Currency Symbols block (U+20A0-U+20B1
    /// inclusive — <c>₠</c> through <c>₱</c>). The newer Indian rupee
    /// (<c>₹</c>, U+20B9) and beyond are rejected by SQL Server.
    /// </summary>
    private static bool IsCurrencySymbol(char c) =>
        c is '$' or '¢' or '£' or '¥' or '฿' or (>= '₠' and <= '₱');

    private SqlValue CoerceToApproximate(SqlType target)
    {
        // Empty / whitespace-only strings → 0.0 (verified against SQL Server
        // 2025; differs from decimal, where empty raises Msg 8114).
        var d = this.Type switch
        {
            _ when SqlType.IsStringCategory(this.Type) => ParseStringToDouble(this.AsString, this.Type),
            _ when SqlType.IsIntegerCategory(this.Type) => AsInt64Widened(this),
            DecimalSqlType => (double)this.AsDecimal,
            _ when this.Type == SqlType.Float => this.AsDouble,
            _ when this.Type == SqlType.Real => this.AsSingle,
            _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
        };
        return target == SqlType.Float ? FromDouble(d) : FromSingle((float)d);
    }

    private SqlValue CoerceFromApproximate(SqlType target)
    {
        var d = this.Type == SqlType.Float ? this.AsDouble : (double)this.AsSingle;
        if (SqlType.IsStringCategory(target))
            return FromString(target, FormatDouble(d, this.Type == SqlType.Float ? 15 : 7));
        if (target is DecimalSqlType targetDecimal)
            return FromDecimal(targetDecimal, RoundAndOverflowCheck((decimal)d, targetDecimal));
        if (target == SqlType.DateTime)
            return this.CoerceToDateTime();
        if (target == SqlType.SmallDateTime)
            return this.CoerceToSmallDateTime();
        if (!SqlType.IsIntegerCategory(target))
            throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target);

        // Float → int truncates toward zero (verified 1.5 → 1, -1.5 → -1).
        var truncated = Math.Truncate(d);
        try
        {
            return target == SqlType.Bit ? FromBoolean(truncated != 0)
                : target == SqlType.TinyInt ? FromByte(checked((byte)truncated))
                : target == SqlType.SmallInt ? FromInt16(checked((short)truncated))
                : target == SqlType.Int32 ? FromInt32(checked((int)truncated))
                : FromInt64(checked((long)truncated));
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(target.ToString()!);
        }
    }

    /// <summary>
    /// Parses a string into a <see cref="double"/> using SQL Server's
    /// <c>float</c> CAST rules: signed, decimal point optional, scientific
    /// notation accepted, surrounding whitespace stripped. Empty / whitespace-
    /// only strings return <c>0</c> (different from <c>decimal</c>, which
    /// rejects empty). <c>'inf'</c>/<c>'NaN'</c> are also rejected
    /// (verified Msg 8114 St 5 against SQL Server 2025).
    /// </summary>
    private static double ParseStringToDouble(string source, SqlType sourceType) =>
        source.Trim() is var trimmed && trimmed.Length == 0
            ? 0.0
            : double.TryParse(
                trimmed,
                System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d) && !double.IsNaN(d) && !double.IsInfinity(d)
                    ? d
                    : throw SimulatedSqlException.ConvertingDataTypeError(sourceType, "float");

    /// <summary>
    /// Formats a <see cref="double"/> using SQL Server's <c>float → varchar</c>
    /// default representation. Switches to scientific notation outside the
    /// 6-significant-figure plain-decimal range; the simulator approximates
    /// this with .NET's <c>"G15"</c> (or <c>"G7"</c> for real). Exact matching
    /// of SQL Server's lowercase-e / 3-digit-exponent textual form is
    /// pending — the broader cast-length-not-enforced limitation already
    /// applies here.
    /// </summary>
    private static string FormatDouble(double value, int significantDigits) =>
        value.ToString("G" + significantDigits, System.Globalization.CultureInfo.InvariantCulture);

    private SqlValue CoerceToDecimal(DecimalSqlType target) => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromDecimal(target, RoundAndOverflowCheck(ParseDecimal(this.AsString, this.Type), target)),
        _ when SqlType.IsIntegerCategory(this.Type) => FromDecimal(target, RoundAndOverflowCheck(AsInt64Widened(this), target)),
        DecimalSqlType => FromDecimal(target, RoundAndOverflowCheck(this.AsDecimal, target)),
        _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
    };

    private SqlValue CoerceFromDecimal(SqlType target)
    {
        var d = this.AsDecimal;
        if (SqlType.IsStringCategory(target))
            return FromString(target, FormatDecimal(d, ((DecimalSqlType)this.Type).scale));
        if (target == SqlType.Float)
            return FromDouble((double)d);
        if (target == SqlType.Real)
            return FromSingle((float)d);
        if (SqlType.IsMoneyCategory(target))
            return FromMoney(target, d);
        if (target == SqlType.DateTime)
            return this.CoerceToDateTime();
        if (target == SqlType.SmallDateTime)
            return this.CoerceToSmallDateTime();
        if (!SqlType.IsIntegerCategory(target))
            throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target);

        // Decimal → integer truncates toward zero (verified against
        // SQL Server 2025: 1.5 → 1, -1.5 → -1, 0.5 → 0). Range overflow
        // raises the standard Msg 8115 with the target type name.
        var truncated = decimal.Truncate(d);
        try
        {
            return target == SqlType.Bit ? FromBoolean(truncated != 0)
                : target == SqlType.TinyInt ? FromByte(checked((byte)truncated))
                : target == SqlType.SmallInt ? FromInt16(checked((short)truncated))
                : target == SqlType.Int32 ? FromInt32(checked((int)truncated))
                : FromInt64(checked((long)truncated));
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(target.ToString()!);
        }
    }

    /// <summary>
    /// Rounds a value to the target decimal's scale (half-away-from-zero,
    /// matching SQL Server 2025 — verified <c>'12.345' → 12.35</c>,
    /// <c>'-12.345' → -12.35</c>) and validates against the target's
    /// precision. Overflow surfaces as Msg 8115 ("Arithmetic overflow error
    /// converting to data type numeric.") to match the real-server text.
    /// </summary>
    private static decimal RoundAndOverflowCheck(decimal value, DecimalSqlType target)
    {
        // .NET decimal.Round caps at 28 fractional digits. For targets with
        // larger declared scale, no actual rounding is needed (the input
        // value can't have more fractional digits than .NET decimal stores
        // anyway); skip the call.
        var rounded = target.scale > 28 ? value : decimal.Round(value, target.scale, MidpointRounding.AwayFromZero);
        // Cap integer-digit count at 28 for the overflow check — Pow10Decimal
        // would itself overflow .NET decimal beyond that. Values that fit
        // .NET decimal can't exceed 28 integer digits anyway.
        var integerDigits = Math.Min(28, target.precision - target.scale);
        var maxIntegerPart = Pow10Decimal(integerDigits) - 1;
        return integerDigits < 28 && decimal.Abs(decimal.Truncate(rounded)) > maxIntegerPart
            ? throw SimulatedSqlException.ArithmeticOverflowToNumeric()
            : rounded;
    }

    private static decimal Pow10Decimal(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++)
            result *= 10m;
        return result;
    }

    /// <summary>
    /// Formats a decimal value with exactly <paramref name="scale"/> trailing
    /// fractional digits — matching SQL Server's <c>decimal → varchar</c>
    /// rendering, which always emits the declared scale (verified
    /// <c>decimal(10,5) 0 → "0.00000"</c>, <c>decimal(10,0) 100 → "100"</c>).
    /// </summary>
    private static string FormatDecimal(decimal value, int scale) =>
        value.ToString(scale == 0 ? "0" : "0." + new string('0', scale), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a string into a <see cref="decimal"/> using SQL Server's CAST
    /// rules: signed, decimal point optional on either side, scientific
    /// notation accepted, surrounding whitespace stripped. Empty / whitespace-
    /// only strings raise Msg 8114 (not 0 — verified against SQL Server 2025;
    /// distinct from float, where empty → 0).
    /// </summary>
    private static decimal ParseDecimal(string source, SqlType sourceType)
    {
        var trimmed = source.Trim();
        return decimal.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture,
            out var d)
                ? d
                : throw SimulatedSqlException.ConvertingDataTypeError(sourceType, "numeric");
    }

    private SqlValue CoerceToUniqueIdentifier() => this.Type switch
    {
        _ when SqlType.IsStringCategory(this.Type) => FromGuid(ParseGuid(this.AsString)),
        _ when this.Type == SqlType.Varbinary => FromGuid(VarbinaryToGuid(this.AsBytes)),
        _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, SqlType.UniqueIdentifier),
    };

    private SqlValue CoerceFromUniqueIdentifier(SqlType target) => target switch
    {
        _ when SqlType.IsStringCategory(target) => FromString(target, this.AsGuid.ToString("D").ToUpperInvariant()),
        _ when target == SqlType.Varbinary => FromVarbinary(this.AsGuid.ToByteArray()),
        _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
    };

    /// <summary>
    /// Parses a string into a <see cref="Guid"/> matching SQL Server's
    /// <c>uniqueidentifier</c> CAST rules. Accepts the <c>D</c>
    /// (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>) and <c>B</c> (the same
    /// surrounded by braces) forms, case-insensitive on hex, with trailing
    /// whitespace allowed but leading whitespace rejected. Every parse
    /// failure surfaces as the same Msg 8169.
    /// </summary>
    private static Guid ParseGuid(string source)
    {
        // .NET's Guid.TryParseExact silently trims leading whitespace; SQL
        // Server's CAST does not. Reject the leading-whitespace case
        // explicitly before delegating.
        if (source.Length > 0 && source[0] == ' ')
            throw SimulatedSqlException.ConversionFailedFromStringToUniqueIdentifier();
        var trimmed = source.TrimEnd();
        return Guid.TryParseExact(trimmed, "D", out var g) || Guid.TryParseExact(trimmed, "B", out g)
            ? g
            : throw SimulatedSqlException.ConversionFailedFromStringToUniqueIdentifier();
    }

    /// <summary>
    /// Builds a <see cref="Guid"/> from a varbinary payload, mirroring SQL
    /// Server's lenient length handling: payloads shorter than 16 bytes are
    /// right-padded with zeros, longer payloads are truncated to the first
    /// 16 bytes. No length error is raised.
    /// </summary>
    private static Guid VarbinaryToGuid(byte[] bytes)
    {
        Span<byte> buffer = stackalloc byte[16];
        bytes.AsSpan(0, Math.Min(16, bytes.Length)).CopyTo(buffer);
        return new Guid(buffer);
    }

    /// <summary>
    /// Formats an integer-family value as a SQL-Server-compatible string:
    /// signed decimal digits in invariant culture; <c>bit</c> renders as
    /// <c>"1"</c>/<c>"0"</c>.
    /// </summary>
    private static string FormatIntegerToString(SqlValue value) =>
        value.Type == SqlType.Bit ? (value.AsBoolean ? "1" : "0")
        : value.Type == SqlType.TinyInt ? value.AsByte.ToString(CultureInfo.InvariantCulture)
        : value.Type == SqlType.SmallInt ? value.AsInt16.ToString(CultureInfo.InvariantCulture)
        : value.Type == SqlType.Int32 ? value.AsInt32.ToString(CultureInfo.InvariantCulture)
        : value.AsInt64.ToString(CultureInfo.InvariantCulture);
}
