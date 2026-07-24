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

        // sql_variant wraps any base value (CAST(x AS sql_variant),
        // ISNULL(variant, x) coercing the fallback); coercing a variant to a
        // concrete type unwraps the inner value and re-coerces it.
        if (target is SqlVariantSqlType)
            return FromVariant(this);
        if (this.Type is SqlVariantSqlType)
            return this.AsVariantInner.CoerceTo(target);

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

        // String ↔ string crossings: same content, target's encoding and
        // padding rules. char(N) / nchar(N) targets carry length on the
        // SqlType and pad-or-truncate inside FromString. varchar / nvarchar
        // targets are stateless singletons here; their declared CAST length
        // is enforced separately by Cast.EnforceTargetMaxLength after this
        // method returns. INSERT/UPDATE truncation (Msg 2628 / 8152) is
        // pre-checked at the column-write boundary before this method runs.
        if (SqlType.IsStringCategory(this.Type) && SqlType.IsStringCategory(target))
            return FromString(target, this.AsString);

        // Binary ↔ binary crossings: binary(N) pads or truncates; varbinary
        // wraps the source bytes unchanged. image (the deprecated always-LOB
        // type) shares the byte-bag shape, so any binary source can land in
        // an image target and image rebinds back to varbinary/binary. The
        // varbinary(N)-target branch covers the same-family-different-length
        // case that the top-level reference-equality short-circuit no longer
        // catches now that varbinary singletons are per-length.
        if (this.Type is VarbinarySqlType or BinarySqlType or ImageSqlType && target is BinarySqlType targetBinary)
            return FromBinary(targetBinary, this.AsBytes);
        if (this.Type is VarbinarySqlType or BinarySqlType or ImageSqlType && target is VarbinarySqlType)
            return FromVarbinary(this.AsBytes);
        if (this.Type is VarbinarySqlType or BinarySqlType && target == SqlType.Image)
            return FromImage(this.AsBytes);

        // Binary → string crossing: reinterpret each byte through the target
        // string family's encoding (CP1252 for varchar/char, UTF-16 LE for
        // nvarchar/nchar). Image is intentionally excluded — real SQL Server
        // raises Msg 8116 for the implicit-coerce path on image, matching
        // the rejection at LEN / LOWER / etc. Probe-confirmed 2026-05-22:
        // <c>LEN(0x4142202020) = 2</c> (trailing CP1252-space bytes trim
        // like ASCII spaces in the resulting varchar), <c>LOWER(0x414243) =
        // 'abc'</c>, <c>LEN(CAST(0x010203 AS binary(10))) = 10</c>
        // (binary's zero-padding survives TrimEnd(' ') because nulls aren't
        // spaces). Implementation routes through the existing
        // <see cref="CoerceBinaryToStringWithStyle"/> at style 0.
        if (this.Type is VarbinarySqlType or BinarySqlType && SqlType.IsStringCategory(target))
            return this.CoerceBinaryToStringWithStyle(target, 0);

        // image → string: real disallows the explicit CAST outright (Msg 529)
        // rather than reinterpreting bytes the way varbinary does — image is
        // the always-LOB binary type with no defined text conversion. Probe-
        // confirmed 2026-07-23 via tiberius: CAST(CAST(0x01 AS image) AS
        // varchar(max) / nvarchar(max)) → Msg 529 (the implicit-coerce path
        // through LEN / LOWER is a separate Msg 8116). Without this the crossing
        // fell to the generic "No coercion implemented" NotSupportedException.
        if (this.Type is ImageSqlType && SqlType.IsStringCategory(target))
            throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target);

        // varbinary / binary → integer family (bit/tinyint/smallint/int/bigint):
        // big-endian, left-truncate to the target width (keep the rightmost
        // bytes), zero-fill the high bytes when the source is shorter, then
        // read as a two's-complement integer of the target width. Silent —
        // SQL Server never raises an overflow here. Probe-confirmed against
        // SQL Server 2025 (2026-07-14): <c>cast(0x0102 as int) = 258</c>,
        // <c>cast(0x0102030405 as int) = 33752069</c> (last four bytes),
        // <c>cast(0xFF01 as tinyint) = 1</c>, <c>cast(0x0100 as bit) = 0</c>,
        // <c>cast(0x01 as bit) = 1</c>, <c>cast(0x as int) = 0</c>,
        // <c>cast(0xFFFFFFFF as int) = -1</c>.
        if (this.Type is VarbinarySqlType or BinarySqlType && SqlType.IsIntegerCategory(target))
            return VarbinaryToInteger(this.AsBytes, target);

        // String → binary crossing: encode the string into raw bytes using
        // CP1252 for varchar/char/sysname sources and UTF-16 LE for
        // nvarchar/nchar sources. <c>varbinary(N)</c> targets get the raw
        // byte buffer (CAST-level <see cref="Cast.EnforceTargetMaxLength"/>
        // truncates to N afterwards); <c>binary(N)</c> targets route through
        // <see cref="FromBinary"/> for zero-pad-or-truncate to the declared
        // length. Probe-confirmed against SQL Server 2025 (2026-05-22):
        // <c>CAST('abc' AS VARBINARY(10)) = 0x616263</c> (no padding),
        // <c>CAST('abc' AS BINARY(10)) = 0x61626300000000000000</c> (padded),
        // <c>CAST(N'abc' AS VARBINARY(10)) = 0x610062006300</c> (UTF-16 LE).
        if (SqlType.IsStringCategory(this.Type) && target is VarbinarySqlType)
            return FromVarbinary(EncodeStringForBinary(this.AsString, this.Type));
        if (SqlType.IsStringCategory(this.Type) && target is BinarySqlType targetStringToBinary)
            return FromBinary(targetStringToBinary, EncodeStringForBinary(this.AsString, this.Type));

        // Integer family → binary / varbinary: big-endian native-width two's-
        // complement bytes (bit/tinyint → 1, smallint → 2, int → 4, bigint →
        // 8). <c>binary(N)</c> is fixed-width — left-zero-pad or left-truncate
        // to exactly N; <c>varbinary(N)</c> keeps the native width and only
        // left-truncates when N is narrower (never left-pads). Probe-confirmed
        // against SQL Server 2025 (2026-07-14): <c>cast(258 as binary(4)) =
        // 0x00000102</c>, <c>cast(258 as varbinary(4)) = 0x00000102</c>,
        // <c>cast(258 as binary(1)) = 0x02</c>, <c>cast(-1 as binary(4)) =
        // 0xFFFFFFFF</c>, <c>cast(cast(1 as tinyint) as varbinary(4)) =
        // 0x01</c> (native width kept), <c>cast(258 as binary) = 30
        // zero-padded bytes</c> (CAST default length 30).
        if (SqlType.IsIntegerCategory(this.Type) && target is BinarySqlType intToBinary)
            return FromBinary(intToBinary, EncodeIntegerToBinary(this, intToBinary.length, fixedWidth: true));
        if (SqlType.IsIntegerCategory(this.Type) && target is VarbinarySqlType intToVarbinary)
            return FromVarbinary(EncodeIntegerToBinary(this, intToVarbinary.length, fixedWidth: false));

        // rowversion outbound CAST: bigint reads the 8 bytes big-endian (matches
        // SQL Server: the database-scoped @@DBTS counter is exposed as a signed
        // bigint); varbinary / binary copy the raw 8 bytes. No reverse direction
        // — rowversion can only be auto-generated, never CAST in.
        if (this.Type is RowVersionSqlType)
        {
            if (target == SqlType.BigInt)
                return FromInt64(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(this.AsBytes));
            if (target is VarbinarySqlType)
                return FromVarbinary(this.AsBytes);
            if (target is BinarySqlType targetRvBinary)
                return FromBinary(targetRvBinary, this.AsBytes);
        }

        // hierarchyid ↔ varbinary/binary: hierarchyid stores its canonical
        // OrdPath bytes, so CAST(node AS varbinary) is a zero-copy byte read
        // (byte-identical to a real server). The reverse — CAST(0x… AS
        // hierarchyid) — accepts only a canonical OrdPath encoding, matching
        // real SQL Server's strict rejection (probe-confirmed 2026-07-17: any
        // non-canonical byte string raises the .NET-UDR error, Msg 6522); the
        // validation lives in HierarchyIdOrdPath.DecodeCanonical.
        if (this.Type is HierarchyIdSqlType && target is VarbinarySqlType)
            return FromVarbinary(this.AsHierarchyIdBytes);
        if (this.Type is HierarchyIdSqlType && target is BinarySqlType hierarchyToBinary)
            return FromBinary(hierarchyToBinary, this.AsHierarchyIdBytes);
        if (this.Type is VarbinarySqlType or BinarySqlType && target is HierarchyIdSqlType)
        {
            _ = HierarchyIdOrdPath.DecodeCanonical(this.AsBytes);
            return FromHierarchyIdBytes(this.AsBytes);
        }

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
        VarbinarySqlType or BinarySqlType => FromDate(DecodeDateFromBytes(this.AsBytes)),
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
        VarbinarySqlType or BinarySqlType => FromDateTime(DecodeLegacyDateTimeFromBytes(this.AsBytes)),
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
        VarbinarySqlType or BinarySqlType => FromSmallDateTime(DecodeSmallDateTimeFromBytes(this.AsBytes)),
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
        VarbinarySqlType or BinarySqlType => FromDateTime2(target, DecodeDateTime2FromBytes(this.AsBytes)),
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
        VarbinarySqlType or BinarySqlType => FromTime(target, DecodeTimeFromBytes(this.AsBytes)),
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
        VarbinarySqlType or BinarySqlType => FromDateTimeOffset(target, DecodeDateTimeOffsetFromBytes(this.AsBytes)),
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
    /// CONVERT-style string formatting for a <c>money</c>/<c>smallmoney</c>
    /// source. Style <c>0</c> emits with no thousands separators and 2
    /// fractional digits (<c>1234567.89</c>); style <c>1</c> adds comma
    /// thousands separators (<c>1,234,567.89</c>); style <c>2</c> drops the
    /// thousands separators and uses 4 fractional digits (<c>1234567.8910</c>).
    /// Probe-confirmed verbatim against SQL Server 2025 (2026-05-13).
    /// Any other style raises Msg 281 with <c>"money"</c> as the source
    /// family wording.
    /// </summary>
    internal SqlValue CoerceMoneyToStringWithStyle(SqlType target, int style)
    {
        var value = this.AsMoney;
        var formatted = style switch
        {
            0 => value.ToString("F2", CultureInfo.InvariantCulture),
            1 => value.ToString("N2", CultureInfo.InvariantCulture),
            2 => value.ToString("F4", CultureInfo.InvariantCulture),
            _ => throw SimulatedSqlException.InvalidStyleForCharacterString(style, "money"),
        };
        return FromString(target, formatted);
    }

    /// <summary>
    /// String → date-like coercion with a CONVERT style hint, mirroring SQL
    /// Server's flexible string-to-datetime parser (probed against SQL Server
    /// 2025, 2026-05-27).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Strict styles</strong> (<c>12</c> <c>yymmdd</c>, <c>112</c>
    /// <c>yyyymmdd</c>, <c>23</c> <c>yyyy-mm-dd</c>, <c>126</c>/<c>127</c>
    /// ISO 8601) pin an exact format; a string that's a valid date by some
    /// OTHER format raises Msg 9807, a non-date raises Msg 241.
    /// </para>
    /// <para>
    /// <strong>General styles</strong> route through .NET's flexible parser:
    /// separators (<c>/ - .</c>) are interchangeable, and numeric / ISO
    /// year-first / month-name forms plus an optional trailing time all parse.
    /// The only family distinction is date-part order for ambiguous numeric
    /// dates — the dmy set (<see cref="IsDayMonthYearStyle"/>) reads day-first
    /// (<c>en-GB</c>), every other style month-first (<c>en-US</c>); a leading
    /// 4-digit token is the year, with the trailing pair following the family
    /// order. Known leniency divergences: the 2-digit-vs-4-digit-year
    /// with/without-century restriction isn't enforced, and a <c>T</c>-separated
    /// time is accepted under general styles (real SQL Server reserves it for
    /// 126/127). See [`docs/claude/casting.md`].
    /// </para>
    /// </remarks>
    internal SqlValue CoerceStringToDateLikeWithStyle(SqlType target, int style)
    {
        var input = this.AsString;

        var strictFormats = StrictStyleDateFormats(style);
        if (strictFormats is not null)
        {
            // AssumeUniversal + AdjustToUniversal keeps the wall-clock reading
            // for style 127's `Z` UTC suffix regardless of host timezone.
            const DateTimeStyles StrictParseStyles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            if (DateTime.TryParseExact(input, strictFormats, CultureInfo.InvariantCulture, StrictParseStyles, out var exact))
                return FromDateTime2(SqlType.GetDateTime2(7), exact).CoerceTo(target);
            if (DateTime.TryParse(input, CultureInfo.InvariantCulture, StrictParseStyles, out _))
                throw SimulatedSqlException.InputCharacterStringStyleMismatch(style);
            throw SimulatedSqlException.ConversionFailedDateTimeFromString();
        }

        var dayMonthYear = IsDayMonthYearStyle(style);
        var culture = dayMonthYear ? GbCulture : UsCulture;
        const DateTimeStyles ParseStyles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault;

        // Separatorless yyyyMMdd is accepted under every general style but
        // isn't recognized by the flexible parser.
        if (DateTime.TryParseExact(input.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var basic))
            return FromDateTime2(SqlType.GetDateTime2(7), basic).CoerceTo(target);

        // dmy + year-first: the flexible en-GB parse reads a leading 4-digit
        // token then month-first, so force day-first on the trailing pair to
        // match SQL Server (e.g. style 103 reads '2003-04-05' as 2003-05-04).
        if (dayMonthYear
            && DateTime.TryParseExact(input.Trim(), DayMonthYearFirstFormats, CultureInfo.InvariantCulture, ParseStyles, out var dmyYearFirst))
        {
            return FromDateTime2(SqlType.GetDateTime2(7), dmyYearFirst).CoerceTo(target);
        }

        if (DateTime.TryParse(input, culture, ParseStyles, out var parsed))
        {
            // A bare time (no date component) anchors to 1900-01-01 — NoCurrentDateDefault
            // leaves the date at 0001-01-01, which is below the datetime range anyway.
            if (parsed is { Year: 1, Month: 1, Day: 1 })
                parsed = DateTimeSqlType.BaseDate.Add(parsed.TimeOfDay);
            return FromDateTime2(SqlType.GetDateTime2(7), parsed).CoerceTo(target);
        }
        if (DateTime.TryParse(input, CultureInfo.InvariantCulture, ParseStyles, out _))
            throw SimulatedSqlException.InputCharacterStringStyleMismatch(style);
        throw SimulatedSqlException.ConversionFailedDateTimeFromString();
    }

    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    private static readonly CultureInfo GbCulture = CultureInfo.GetCultureInfo("en-GB");

    private static readonly string[] DayMonthYearFirstFormats = ["yyyy/d/M", "yyyy-d-M", "yyyy.d.M"];

    /// <summary>
    /// The day-month-year CONVERT styles. Every other (general) style orders
    /// ambiguous numeric dates month-first.
    /// </summary>
    private static bool IsDayMonthYearStyle(int style) =>
        style is 3 or 4 or 5 or 13 or 14 or 103 or 104 or 105 or 113 or 114 or 130 or 131;

    /// <summary>
    /// Exact format(s) for the strict CONVERT styles (basic <c>yymmdd</c> /
    /// <c>yyyymmdd</c> and the ISO 8601 forms, each with an optional trailing
    /// time), or null for the general flexible styles.
    /// </summary>
    private static string[]? StrictStyleDateFormats(int style) => style switch
    {
        12 => ["yyMMdd", "yyMMdd HH:mm:ss", "yyMMdd HH:mm:ss.fff"],
        23 => ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.fffffff"],
        112 => ["yyyyMMdd", "yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm:ss.fff"],
        126 or 127 =>
        [
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.f",
            "yyyy-MM-ddTHH:mm:ss.ff",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "yyyy-MM-ddTHH:mm:ss.ffff",
            "yyyy-MM-ddTHH:mm:ss.fffff",
            "yyyy-MM-ddTHH:mm:ss.ffffff",
            "yyyy-MM-ddTHH:mm:ss.fffffff",
            "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-ddTHH:mm:ss.fZ",
            "yyyy-MM-ddTHH:mm:ss.ffZ",
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            "yyyy-MM-ddTHH:mm:ss.ffffZ",
            "yyyy-MM-ddTHH:mm:ss.fffffZ",
            "yyyy-MM-ddTHH:mm:ss.ffffffZ",
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
        ],
        _ => null,
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
        // varbinary / binary → money / smallmoney: the payload's rightmost
        // 8 (money) / 4 (smallmoney) bytes are the raw scale-4 currency units,
        // read big-endian as a two's-complement integer then divided by 10000.
        // Probe-confirmed 2026-07-14: <c>cast(0x01 as money) = 0.0001</c>,
        // <c>cast(0x01 as smallmoney) = 0.0001</c>.
        VarbinarySqlType or BinarySqlType => FromMoney(target, VarbinaryToMoneyUnits(this.AsBytes, target)),
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
        _ when SqlType.IsMoneyCategory(this.Type) => FromDecimal(target, RoundAndOverflowCheck(this.AsMoney, target)),
        // float / real → decimal is a permitted conversion (implicit and
        // explicit), not the Msg 529 rejection: an out-of-range magnitude
        // raises Msg 8115 arithmetic overflow. real converts from its own
        // 4-byte value (not widened to double first) so the decimal keeps the
        // ~7-significant-digit representation real does. Probe-confirmed
        // against SQL Server 2025 (2026-07-23; ODBC / pyodbc binds a Python
        // float parameter as float, so SQLAlchemy's decimal inserts land here).
        _ when this.Type == SqlType.Float => FromDecimal(target, RoundAndOverflowCheck(FloatToDecimalChecked(this.AsDouble), target)),
        _ when this.Type == SqlType.Real => FromDecimal(target, RoundAndOverflowCheck(FloatToDecimalChecked(this.AsSingle), target)),
        // varbinary / binary → decimal / numeric is disallowed: SQL Server
        // raises Msg 8114 ("Error converting data type varbinary to numeric.")
        // rather than the Msg 529 explicit-conversion rejection used elsewhere.
        // Probe-confirmed 2026-07-14; TRY_CAST swallows the 8114 to NULL.
        VarbinarySqlType or BinarySqlType => throw SimulatedSqlException.ConvertingDataTypeError(this.Type, "numeric"),
        _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
    };

    /// <summary>
    /// float (double) / real (single) → decimal with the .NET out-of-range
    /// <see cref="OverflowException"/> mapped to SQL Server's Msg 8115
    /// arithmetic overflow (NaN / ±Infinity / magnitude past decimal's range).
    /// The overload keeps real's narrower conversion distinct from float's.
    /// </summary>
    private static decimal FloatToDecimalChecked(double value)
    {
        try
        {
            return (decimal)value;
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflowToNumeric();
        }
    }

    private static decimal FloatToDecimalChecked(float value)
    {
        try
        {
            return (decimal)value;
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflowToNumeric();
        }
    }

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
        VarbinarySqlType or BinarySqlType => FromGuid(VarbinaryToGuid(this.AsBytes)),
        _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, SqlType.UniqueIdentifier),
    };

    private SqlValue CoerceFromUniqueIdentifier(SqlType target) => target switch
    {
        _ when SqlType.IsStringCategory(target) => FromString(target, this.AsGuid.ToString("D").ToUpperInvariant()),
        _ when target is VarbinarySqlType => FromVarbinary(this.AsGuid.ToByteArray()),
        BinarySqlType binTarget => FromBinary(binTarget, this.AsGuid.ToByteArray()),
        _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
    };

    /// <summary>
    /// Parses a string into a <see cref="Guid"/> matching SQL Server's
    /// <c>uniqueidentifier</c> CAST rules. Accepts the <c>D</c>
    /// (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>) and <c>B</c> (the same
    /// surrounded by braces) forms, case-insensitive on hex, with trailing
    /// whitespace allowed but leading whitespace rejected. A string longer
    /// than the 36-char D-form is accepted by parsing the leading 36
    /// characters and ignoring any trailing content (probe-confirmed:
    /// <c>'…-xxxxxxxxxxxx' + arbitrary tail</c> converts to the leading GUID —
    /// SSMS's Database Properties dialog leans on this, emitting an over-long
    /// all-zero GUID literal via <c>ISNULL(mirroring_guid, '0000…0000')</c>).
    /// Every parse failure surfaces as the same Msg 8169.
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
            : source.Length >= 36 && Guid.TryParseExact(source.AsSpan(0, 36), "D", out g)
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

    // Binary wire-format decoders for the date/time family. Encodings probed
    // against SQL Server 2025 on 2026-05-17 via
    // CAST(CAST(<literal> AS <date-type>) AS varbinary(20)). The layouts
    // documented at each helper match the bytes SSMS emits in BACPAC-style
    // INSERT statements (`CAST(0x… AS DateTimeOffset)`), which is how
    // Optimizely Configured Commerce and many other partner products
    // serialize their seed-data date columns.
    //
    // Common to time(N) / datetime2(N) / datetimeoffset(N):
    //   scale byte 0–7, then a little-endian unsigned integer giving the
    //   wall-clock time in 10^(-scale)-second units, sized 3 / 4 / 5 bytes
    //   for scales 0–2 / 3–4 / 5–7 respectively. Followed (for datetime2 and
    //   datetimeoffset) by 3 little-endian bytes of days since 0001-01-01,
    //   and (for datetimeoffset only) a 2-byte signed little-endian offset
    //   in minutes. datetimeoffset stores time + date as UTC; the offset
    //   shifts back to the original wall-clock during round-trip.

    private static DateOnly DecodeDateFromBytes(byte[] bytes) =>
        bytes.Length != 3
            ? throw new NotSupportedException($"CAST(varbinary(…) AS date) requires exactly 3 bytes; got {bytes.Length}.")
            : DateOnly.MinValue.AddDays((int)ReadLittleEndianUInt(bytes, 0, 3));

    private static TimeSpan DecodeTimeFromBytes(byte[] bytes)
    {
        if (bytes.Length < 1)
            throw new NotSupportedException($"CAST(varbinary(…) AS time) requires at least 1 byte; got {bytes.Length}.");
        var scale = bytes[0];
        var timeBytes = TimeWidthForScale(scale);
        return bytes.Length != 1 + timeBytes
            ? throw new NotSupportedException($"CAST(varbinary(…) AS time({scale})) requires {1 + timeBytes} bytes; got {bytes.Length}.")
            : DecodeTimeOfDay(bytes, 1, timeBytes, scale);
    }

    private static DateTime DecodeDateTime2FromBytes(byte[] bytes)
    {
        if (bytes.Length < 4)
            throw new NotSupportedException($"CAST(varbinary(…) AS datetime2) requires at least 4 bytes; got {bytes.Length}.");
        var scale = bytes[0];
        var timeBytes = TimeWidthForScale(scale);
        if (bytes.Length != 1 + timeBytes + 3)
            throw new NotSupportedException($"CAST(varbinary(…) AS datetime2({scale})) requires {1 + timeBytes + 3} bytes; got {bytes.Length}.");
        var time = DecodeTimeOfDay(bytes, 1, timeBytes, scale);
        var date = DateOnly.MinValue.AddDays((int)ReadLittleEndianUInt(bytes, 1 + timeBytes, 3));
        return date.ToDateTime(TimeOnly.MinValue).Add(time);
    }

    private static DateTimeOffset DecodeDateTimeOffsetFromBytes(byte[] bytes)
    {
        if (bytes.Length < 6)
            throw new NotSupportedException($"CAST(varbinary(…) AS datetimeoffset) requires at least 6 bytes; got {bytes.Length}.");
        var scale = bytes[0];
        var timeBytes = TimeWidthForScale(scale);
        if (bytes.Length != 1 + timeBytes + 3 + 2)
            throw new NotSupportedException($"CAST(varbinary(…) AS datetimeoffset({scale})) requires {1 + timeBytes + 5} bytes; got {bytes.Length}.");
        var utcTime = DecodeTimeOfDay(bytes, 1, timeBytes, scale);
        var utcDate = DateOnly.MinValue.AddDays((int)ReadLittleEndianUInt(bytes, 1 + timeBytes, 3));
        var offsetMinutes = (short)(bytes[1 + timeBytes + 3] | (bytes[1 + timeBytes + 4] << 8));
        // SQL Server stores the UTC instant; the constructor expects a local
        // wall-clock + offset, so shift back by adding the offset to the UTC
        // components before composing.
        var utc = new DateTime(utcDate.Year, utcDate.Month, utcDate.Day, 0, 0, 0, DateTimeKind.Unspecified).Add(utcTime);
        var localWallClock = utc.AddMinutes(offsetMinutes);
        return new DateTimeOffset(localWallClock, TimeSpan.FromMinutes(offsetMinutes));
    }

    /// <summary>
    /// Decodes the legacy 8-byte <c>datetime</c> binary format: 4 bytes
    /// big-endian signed day count from 1900-01-01, then 4 bytes big-endian
    /// unsigned 1/300-second ticks since midnight.
    /// </summary>
    private static DateTime DecodeLegacyDateTimeFromBytes(byte[] bytes)
    {
        if (bytes.Length != 8)
            throw new NotSupportedException($"CAST(varbinary(…) AS datetime) requires exactly 8 bytes; got {bytes.Length}.");
        // Day count is a signed 32-bit integer (the byte-rebuild is intentional
        // wrap-aware to recover the sign bit). Tick count is an unsigned 32-bit
        // count of 1/300-second granules.
        var days = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        var ticks300 = (uint)(bytes[4] << 24) | (uint)(bytes[5] << 16) | (uint)(bytes[6] << 8) | bytes[7];
        // 1/300-second granularity. The literal `TicksPerSecond / 300L`
        // truncates because 10_000_000 / 300 isn't whole — keep the
        // multiplication on the high side of the division.
        var baseDate = new DateTime(1900, 1, 1).AddDays(days);
        return baseDate.AddTicks(ticks300 * TimeSpan.TicksPerSecond / 300L);
    }

    /// <summary>
    /// Decodes the legacy 4-byte <c>smalldatetime</c> binary format: 2 bytes
    /// big-endian unsigned days since 1900-01-01, then 2 bytes big-endian
    /// unsigned minutes since midnight.
    /// </summary>
    private static DateTime DecodeSmallDateTimeFromBytes(byte[] bytes)
    {
        if (bytes.Length != 4)
            throw new NotSupportedException($"CAST(varbinary(…) AS smalldatetime) requires exactly 4 bytes; got {bytes.Length}.");
        var days = (bytes[0] << 8) | bytes[1];
        var minutes = (bytes[2] << 8) | bytes[3];
        return new DateTime(1900, 1, 1).AddDays(days).AddMinutes(minutes);
    }

    private static int TimeWidthForScale(byte scale) => scale switch
    {
        <= 2 => 3,
        <= 4 => 4,
        <= 7 => 5,
        _ => throw new NotSupportedException($"varbinary→time/datetime2/datetimeoffset scale must be 0–7; got {scale}."),
    };

    private static TimeSpan DecodeTimeOfDay(byte[] bytes, int offset, int width, byte scale)
    {
        var raw = ReadLittleEndianUInt(bytes, offset, width);
        // raw is in 10^(-scale)-second units. Convert to .NET Ticks (10^-7 s):
        // multiplier = 10^(7-scale).
        var multiplier = scale switch
        {
            0 => 10_000_000L,
            1 => 1_000_000L,
            2 => 100_000L,
            3 => 10_000L,
            4 => 1_000L,
            5 => 100L,
            6 => 10L,
            _ => 1L,
        };
        return TimeSpan.FromTicks(raw * multiplier);
    }

    private static long ReadLittleEndianUInt(byte[] bytes, int offset, int width)
    {
        long acc = 0;
        for (var i = 0; i < width; i++)
            acc |= (long)bytes[offset + i] << (i * 8);
        return acc;
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

    /// <summary>
    /// Encodes a string for binary/varbinary CAST: CP1252 (Latin1) for
    /// <c>varchar</c> / <c>char</c> / <c>sysname</c> sources, UTF-16 LE for
    /// <c>nvarchar</c> / <c>nchar</c> / <c>ntext</c> / <c>text</c> sources.
    /// Matches the default-style (style 0) byte rendering real SQL Server
    /// uses; explicit-style CONVERT routes through
    /// <see cref="CoerceStringToBinaryWithStyle"/> for the hex-string forms.
    /// </summary>
    private static byte[] EncodeStringForBinary(string source, SqlType sourceType) =>
        sourceType is NVarcharSqlType or NCharSqlType or NTextSqlType or SystemNameSqlType
            ? System.Text.Encoding.Unicode.GetBytes(source)
            : System.Text.Encoding.Latin1.GetBytes(source);

    /// <summary>
    /// Decodes a varbinary/binary payload into an integer-family value the way
    /// SQL Server's binary→integer CAST does: big-endian, keeping the rightmost
    /// <c>width</c> bytes (left-truncation), zero-filling the high bytes when the
    /// payload is shorter, then reading the window as a two's-complement integer
    /// of the target's width. Truncation is silent (no overflow). <c>bit</c>
    /// tests only the final byte for non-zero; an empty payload is 0 / false.
    /// </summary>
    private static SqlValue VarbinaryToInteger(ReadOnlySpan<byte> bytes, SqlType target)
    {
        if (target == SqlType.Bit)
            return FromBoolean(bytes.Length > 0 && bytes[^1] != 0);

        var width = target == SqlType.TinyInt ? 1
            : target == SqlType.SmallInt ? 2
            : target == SqlType.Int32 ? 4
            : 8;
        Span<byte> buffer = stackalloc byte[8];
        var take = Math.Min(width, bytes.Length);
        // Right-align the rightmost `take` source bytes into the low end of the
        // 8-byte window so a big-endian int64 read yields the target integer.
        bytes[^take..].CopyTo(buffer[(8 - take)..]);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(buffer);
        return target == SqlType.TinyInt ? FromByte((byte)value)
            : target == SqlType.SmallInt ? FromInt16((short)value)
            : target == SqlType.Int32 ? FromInt32((int)value)
            : FromInt64(value);
    }

    /// <summary>
    /// Decodes a varbinary/binary payload into a <c>money</c>/<c>smallmoney</c>
    /// value: the rightmost 8 (money) / 4 (smallmoney) bytes are read big-endian
    /// as a two's-complement integer of scale-4 currency units, then divided by
    /// 10000. Shorter payloads zero-fill the high bytes.
    /// </summary>
    private static decimal VarbinaryToMoneyUnits(ReadOnlySpan<byte> bytes, SqlType target)
    {
        var width = target == SqlType.SmallMoney ? 4 : 8;
        Span<byte> buffer = stackalloc byte[8];
        var take = Math.Min(width, bytes.Length);
        bytes[^take..].CopyTo(buffer[(8 - take)..]);
        var units = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(buffer);
        // smallmoney is a 4-byte signed unit count; sign-extend from int32.
        if (target == SqlType.SmallMoney)
            units = (int)units;
        return units / 10000m;
    }

    /// <summary>
    /// Encodes an integer-family value into big-endian bytes for a binary /
    /// varbinary CAST target. The value is first rendered in its native width
    /// (bit/tinyint → 1, smallint → 2, int → 4, bigint → 8) as big-endian
    /// two's-complement, then fitted to the target width: fixed-width
    /// <c>binary(N)</c> left-zero-pads or left-truncates to exactly N bytes;
    /// <c>varbinary(N)</c> keeps the native width and only left-truncates when
    /// N is narrower (never left-pads). Length ≤ 0 (unspecified / MAX
    /// varbinary) keeps the native width.
    /// </summary>
    private static byte[] EncodeIntegerToBinary(SqlValue value, int declaredLength, bool fixedWidth)
    {
        var native = value.Type == SqlType.SmallInt ? 2
            : value.Type == SqlType.Int32 ? 4
            : value.Type == SqlType.BigInt ? 8
            : 1;
        Span<byte> wide = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(wide, AsInt64Widened(value));
        var nativeBytes = wide[(8 - native)..];

        var width = fixedWidth ? declaredLength
            : declaredLength <= 0 ? native
            : Math.Min(declaredLength, native);

        var result = new byte[width];
        if (width <= native)
            nativeBytes[(native - width)..].CopyTo(result);
        else
            nativeBytes.CopyTo(result.AsSpan(width - native));
        return result;
    }
}
