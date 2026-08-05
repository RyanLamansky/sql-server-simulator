using System.Globalization;
using SqlServerSimulator.Storage.Spatial;

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
        {
            // A temporal payload converts to text with style 0, where the same
            // base type converted directly uses its own ISO default — real
            // gives '1:45PM' for a time inside a variant and '13:45:12.345'
            // for the time itself (probe-confirmed for date / time /
            // datetime2 / datetimeoffset). datetime and smalldatetime already
            // default to style 0, so the two paths agree there.
            var inner = this.AsVariantInner;
            return SqlType.IsStringCategory(target) && !inner.IsNull && IsTemporal(inner.Type)
                ? inner.CoerceDateTimeToStringWithStyle(target, 0)
                : inner.CoerceTo(target);
        }

        static bool IsTemporal(SqlType type) =>
            type == SqlType.Date || type == SqlType.DateTime || type == SqlType.SmallDateTime
            || type is DateTime2SqlType or TimeSqlType or DateTimeOffsetSqlType;

        // decimal → decimal, hoisted ahead of the crossings below: a decimal
        // column feeding a CASE arm and then a SUM takes this several times per
        // row, and the general dispatch reaches its decimal branch only after a
        // dozen type tests. Identical to that branch — see the
        // <see cref="CoerceToDecimal"/> arm it duplicates.
        if (target is DecimalSqlType hotDecimalTarget && this.Type is DecimalSqlType)
            return FromDecimal(hotDecimalTarget, RoundAndOverflowCheck(this.AsDecimal, hotDecimalTarget));

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
        // varbinary / binary → geography / geometry deserializes the UDT byte
        // form, so it has to precede the general binary-to-string branch below:
        // the spatial types sit in the string category, and reinterpreting
        // their bytes as text is what real does not do.
        if (this.Type is VarbinarySqlType or BinarySqlType && target is SpatialSqlType binaryToSpatial)
            return FromSpatial(SpatialBinaryCodec.Decode(this.AsBytes, binaryToSpatial.IsGeography), binaryToSpatial.IsGeography);

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
        // geography / geometry ↔ varbinary / binary: a spatial value's byte
        // form is SQL Server's UDT serialization, not its WKT text, so the
        // cast round-trips through the binary codec. This has to precede the
        // string-category branch below because the spatial types sit in that
        // category. The inbound direction raises real's own version failure
        // (Msg 6522 / 24210) on bytes it can't read.
        if (this.Type is SpatialSqlType spatialToVarbinary && target is VarbinarySqlType)
            return FromVarbinary(this.AsSpatial.Encoded(spatialToVarbinary.IsGeography));
        if (this.Type is SpatialSqlType spatialToBinary && target is BinarySqlType spatialBinaryTarget)
            return FromBinary(spatialBinaryTarget, this.AsSpatial.Encoded(spatialToBinary.IsGeography));

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
    /// String → date-like coercion with a CONVERT style hint. Every style
    /// carries its own input grammar, and the grammar additionally depends on
    /// whether the target is a legacy or a modern date type — probed
    /// exhaustively against SQL Server 2025 (40 styles × 19 input shapes × 6
    /// targets). See <c>docs/claude/casting.md</c> for the tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are exactly two grammars. The <strong>legacy</strong> family
    /// (<c>datetime</c> / <c>smalldatetime</c>) reads a style's numeric date as
    /// an order (<see cref="DatePartOrder"/>) plus a year width, where the
    /// width is the published table's "with century" / "without century"
    /// split — <c>CONVERT(datetime, '01/02/99', 101)</c> fails because 101 is a
    /// four-digit-year style and <c>… '01/02/1999', 1)</c> because 1 is a
    /// two-digit one. Four-digit legacy styles additionally accept a
    /// year-leading form, with the remaining pair still in the style's own
    /// order, which is why style 103 reads <c>1999-01-02</c> as 2 Jan.
    /// </para>
    /// <para>
    /// The <strong>modern</strong> family (<c>date</c> / <c>datetime2</c> /
    /// <c>time</c> / <c>datetimeoffset</c>) instead accepts only the style's
    /// <em>own published output layout</em>: style 101 takes
    /// <c>mm/dd/yyyy</c> but not year-first, style 120 takes
    /// <c>yyyy-mm-dd</c> but not <c>mm/dd/yyyy</c>, and style 23 — which reads
    /// nothing numeric under the legacy family — takes ISO. It also accepts a
    /// <c>T</c> separator under every style, where the legacy family reserves
    /// <c>T</c> for 0 / 126 / 127.
    /// </para>
    /// <para>
    /// Independently of both, most styles accept a set of <em>shared</em>
    /// forms: separatorless <c>yyyyMMdd</c> / <c>yyMMdd</c>, the English
    /// month-name spellings, and a bare time (anchored to 1900-01-01).
    /// Legacy 127 accepts none of them; 130 / 131 exclude the month-name ones.
    /// </para>
    /// </remarks>
    internal SqlValue CoerceStringToDateLikeWithStyle(SqlType target, int style)
    {
        // datetime / smalldatetime share one grammar; date / datetime2 / time /
        // datetimeoffset share the other (probe-confirmed: the four modern
        // targets differ from the legacy pair in exactly the same 88 cells).
        var legacyFamily = target is DateTimeSqlType or SmallDateTimeSqlType;
        var grammar = GrammarForStyle(style, legacyFamily);
        // A trailing `Z` is universal in the modern family; the legacy one
        // takes it only under the permissive default style and under 127, the
        // style whose own output carries the UTC suffix — 126 rejects it
        // (probe-confirmed).
        var allowsZoneSuffix = !legacyFamily || style is 0 or 127;

        // `smalldatetime` reports its own Msg 295 for a format failure where
        // every other target reports Msg 241; an out-of-range *value* is
        // Msg 242 on all of them (probe-confirmed).
        SimulatedSqlException Fail(bool outOfRange) =>
            outOfRange && legacyFamily ? SimulatedSqlException.OutOfRangeDateTimeConversion(target)
            : target is SmallDateTimeSqlType ? SimulatedSqlException.ConversionFailedSmallDateTimeFromString()
            : SimulatedSqlException.ConversionFailedDateTimeFromString();
        var input = this.AsString.Trim();

        // A bare time anchors to 1900-01-01 and is accepted under every style.
        if (TryParseBareTime(input, out var timeOnly))
            return FromDateTime2(SqlType.GetDateTime2(7), timeOnly).CoerceTo(target);

        var (datePart, timePart, separator) = SplitDateAndTime(input);

        // The legacy family reserves `T` for the ISO styles. Which error it
        // reports depends on whether the style could read the date half at all
        // (probe-confirmed: style 101 gives the out-of-range Msg 242, styles 1
        // and 100 give Msg 241).
        if (separator == 'T' && !grammar.AllowsIsoT)
        {
            throw Fail(grammar.Order != DatePartOrder.None && grammar.FourDigitYear);
        }

        if (TryParseSharedForm(grammar, datePart, timePart, allowsZoneSuffix, out var shared))
            return FromDateTime2(SqlType.GetDateTime2(7), shared).CoerceTo(target);

        // An unambiguous ISO 8601 date-with-`T` is accepted under every style
        // in the modern family, independent of that style's numeric grammar —
        // `CONVERT(date, '1999-01-02T10:00:00', 3)` succeeds where the plain
        // `1999-01-02` under style 3 does not.
        if (!legacyFamily && separator == 'T'
            && DateTime.TryParseExact(datePart, "yyyy-MM-dd", UsCulture, DateTimeStyles.None, out var isoDate)
            && TryAttachTime(isoDate, timePart, allowsZoneSuffix, out var isoWithTime))
        {
            return FromDateTime2(SqlType.GetDateTime2(7), isoWithTime).CoerceTo(target);
        }

        // The mirror rule for legacy 126 / 127: ISO 8601 wants its `T`, so a
        // space-separated time is rejected for their numeric form.
        if (separator == ' ' && grammar.RequiresIsoDash)
            throw Fail(outOfRange: false);

        if (TryParseStyleNumericForm(grammar, datePart, timePart, allowsZoneSuffix, out var numeric, out var wrongWidthOnly, out var outOfRange))
            return FromDateTime2(SqlType.GetDateTime2(7), numeric).CoerceTo(target);

        // A numeric date the style has no grammar for at all — as opposed to
        // one that merely got the year width wrong — reports the style-mismatch
        // Msg 9807 on the modern targets. The legacy targets funnel every
        // failure to Msg 241 instead (probe-confirmed both ways).
        throw !legacyFamily && grammar.Order == DatePartOrder.None && !wrongWidthOnly && LooksNumericDate(datePart)
            ? SimulatedSqlException.InputCharacterStringStyleMismatch(style)
            : Fail(outOfRange);
    }

    /// <summary>Order of the date parts a style's numeric form uses.</summary>
    private enum DatePartOrder
    {
        /// <summary>The style accepts no separator-bearing numeric date at all.</summary>
        None,
        Mdy,
        Dmy,
        /// <summary>Year leads, at the style's own width.</summary>
        Ymd,
    }

    /// <summary>
    /// One style's string-input grammar for one target family.
    /// <see cref="TwoDigitYear"/> / <see cref="FourDigitYear"/> are the
    /// "without century" / "with century" halves of SQL Server's style table;
    /// a style admits exactly one except style 0, which takes either.
    /// </summary>
    private readonly struct StyleDateGrammar(
        DatePartOrder order,
        bool twoDigitYear,
        bool fourDigitYear,
        bool allowsYearFirstAlternative,
        bool allowsIsoT,
        bool requiresIsoDash,
        bool allowsSharedForms,
        bool allowsMonthNames)
    {
        public readonly DatePartOrder Order = order;
        public readonly bool TwoDigitYear = twoDigitYear;
        public readonly bool FourDigitYear = fourDigitYear;
        public readonly bool AllowsYearFirstAlternative = allowsYearFirstAlternative;
        public readonly bool AllowsIsoT = allowsIsoT;
        public readonly bool RequiresIsoDash = requiresIsoDash;
        public readonly bool AllowsSharedForms = allowsSharedForms;
        public readonly bool AllowsMonthNames = allowsMonthNames;
    }

    private static StyleDateGrammar GrammarForStyle(int style, bool legacyFamily) =>
        legacyFamily ? LegacyGrammar(style) : ModernGrammar(style);

    private static StyleDateGrammar LegacyGrammar(int style) => style switch
    {
        // The default style is the permissive one: either year width, and the
        // only style outside 126 / 127 taking a `T` separator.
        0 => new(DatePartOrder.Mdy, twoDigitYear: true, fourDigitYear: true, allowsYearFirstAlternative: true,
                 allowsIsoT: true, requiresIsoDash: false, allowsSharedForms: true, allowsMonthNames: true),
        1 or 10 => new(DatePartOrder.Mdy, true, false, false, false, false, true, true),
        3 or 4 or 5 => new(DatePartOrder.Dmy, true, false, false, false, false, true, true),
        2 or 11 => new(DatePartOrder.Ymd, true, false, false, false, false, true, true),
        20 or 21 or 101 or 102 or 110 or 111 or 120 or 121 => new(DatePartOrder.Mdy, false, true, true, false, false, true, true),
        103 or 104 or 105 => new(DatePartOrder.Dmy, false, true, true, false, false, true, true),
        126 => new(DatePartOrder.Ymd, false, true, false, true, true, true, true),
        // 127 is the one style rejecting the shared forms outright.
        127 => new(DatePartOrder.Ymd, false, true, false, true, true, false, false),
        // Hijri input isn't modeled; acceptance matches, the calendar doesn't.
        130 or 131 => new(DatePartOrder.Mdy, false, true, true, false, false, true, false),
        // Every remaining style reads only the shared forms — its own numeric
        // layout is an *output* format that says nothing about what it parses.
        _ => new(DatePartOrder.None, false, false, false, false, false, true, true),
    };

    /// <summary>
    /// The modern family's grammar: the style's own output layout is the whole
    /// numeric grammar (no year-first alternative), and `T` is universal.
    /// </summary>
    private static StyleDateGrammar ModernGrammar(int style)
    {
        var (order, fourDigit) = style switch
        {
            1 => (DatePartOrder.Mdy, false),
            101 or 110 or 22 => (DatePartOrder.Mdy, style != 22),
            10 => (DatePartOrder.Mdy, false),
            3 or 4 or 5 => (DatePartOrder.Dmy, false),
            103 or 104 or 105 => (DatePartOrder.Dmy, true),
            2 or 11 => (DatePartOrder.Ymd, false),
            20 or 21 or 23 or 25 or 102 or 111 or 120 or 121 or 126 or 127 => (DatePartOrder.Ymd, true),
            // Style 0 stays the permissive default in both families.
            0 => (DatePartOrder.Mdy, true),
            _ => (DatePartOrder.None, false),
        };
        return style == 0
            ? new(DatePartOrder.Mdy, true, true, true, true, false, true, true)
            : new(order, !fourDigit && order != DatePartOrder.None, fourDigit, false, true, false, true, style is not (130 or 131));
    }

    private static readonly string[] BareTimeFormats =
    [
        "H:mm", "H:mm:ss", "H:mm:ss.f", "H:mm:ss.ff", "H:mm:ss.fff", "H:mm:ss.ffff",
        "H:mm:ss.fffff", "H:mm:ss.ffffff", "H:mm:ss.fffffff", "H:mm:ss:fff",
        "h:mm tt", "h:mm:ss tt", "h:mmtt", "h:mm:sstt",
    ];

    private static readonly string[] MonthNameFormats =
    [
        "MMM d yyyy", "MMM d, yyyy", "d MMM yyyy", "MMMM d yyyy", "MMMM d, yyyy", "d MMMM yyyy",
        "MMM d yy", "MMM d, yy", "d MMM yy", "MMMM d yy", "MMMM d, yy", "d MMMM yy",
    ];

    private static bool TryParseBareTime(string input, out DateTime result)
    {
        if (DateTime.TryParseExact(input, BareTimeFormats, UsCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
        {
            result = DateTimeSqlType.BaseDate.Add(time.TimeOfDay);
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// Splits a trailing time-of-day off the date portion, reporting which
    /// character separated them so the caller can enforce the ISO-only
    /// <c>T</c> rule. The separator is <c>'\0'</c> when the input is date-only.
    /// Scans from the right, because the date half may itself contain spaces
    /// (<c>Jan 2 1999 10:00</c>).
    /// </summary>
    private static (string Date, string Time, char Separator) SplitDateAndTime(string input)
    {
        var t = input.IndexOf('T', StringComparison.OrdinalIgnoreCase);
        // A leading `T` would be a month name's letter, not a separator; the
        // date half has to have some substance before it counts.
        if (t > 0 && char.IsAsciiDigit(input[t - 1]) && t + 1 < input.Length && char.IsAsciiDigit(input[t + 1]))
            return (input[..t], input[(t + 1)..], 'T');

        for (var i = input.LastIndexOf(' '); i > 0; i = input.LastIndexOf(' ', i - 1))
        {
            var tail = input[(i + 1)..];
            // Only a genuine clock reading ends the date half. An AM/PM marker
            // is part of the time, so keep widening left past it.
            if (tail.Contains(':', StringComparison.Ordinal)
                && DateTime.TryParseExact(tail, BareTimeFormats, UsCulture, DateTimeStyles.AllowWhiteSpaces, out _))
            {
                return (input[..i], tail, ' ');
            }
        }
        return (input, string.Empty, '\0');
    }

    /// <summary>
    /// The forms accepted regardless of a style's numeric grammar:
    /// separatorless <c>yyyyMMdd</c> / <c>yyMMdd</c> and the English
    /// month-name spellings.
    /// </summary>
    private static bool TryParseSharedForm(StyleDateGrammar grammar, string datePart, string timePart, bool allowsZoneSuffix, out DateTime result)
    {
        result = default;
        return grammar.AllowsSharedForms
            && (datePart.Length is 8 or 6 && datePart.All(char.IsAsciiDigit)
                ? DateTime.TryParseExact(datePart, datePart.Length == 8 ? "yyyyMMdd" : "yyMMdd", UsCulture, DateTimeStyles.None, out var compact)
                    && TryAttachTime(compact, timePart, allowsZoneSuffix, out result)
                : grammar.AllowsMonthNames
                    && DateTime.TryParseExact(datePart, MonthNameFormats, UsCulture, DateTimeStyles.AllowWhiteSpaces, out var named)
                    && TryAttachTime(named, timePart, allowsZoneSuffix, out result));
    }

    /// <summary>
    /// Parses the separator-bearing numeric date a style's grammar admits.
    /// <paramref name="wrongWidthOnly"/> reports that the shape was right for
    /// this style but the year width wasn't, which keeps a century-restriction
    /// failure on the Msg 241 path rather than the style-mismatch Msg 9807.
    /// </summary>
    private static bool TryParseStyleNumericForm(StyleDateGrammar grammar, string datePart, string timePart, bool allowsZoneSuffix, out DateTime result, out bool wrongWidthOnly, out bool outOfRange)
    {
        result = default;
        wrongWidthOnly = false;
        outOfRange = false;
        if (grammar.Order == DatePartOrder.None || !LooksNumericDate(datePart))
            return false;

        // 126 / 127 under the legacy family pin the dash form; `yyyy/MM/dd` is
        // rejected there but accepted under the modern one.
        if (grammar.RequiresIsoDash && datePart.Contains('/', StringComparison.Ordinal))
            return false;
        if (grammar.RequiresIsoDash && datePart.Contains('.', StringComparison.Ordinal))
            return false;

        // Separators are interchangeable, so normalize to one and let the
        // format list carry the ordering.
        var normalized = datePart.Replace('-', '/').Replace('.', '/');
        var yearFirst = normalized.Length > 4 && normalized[..4].All(char.IsAsciiDigit) && normalized[4] == '/';

        string[] formats;
        if (grammar.Order == DatePartOrder.Ymd)
        {
            if (!yearFirst && grammar.FourDigitYear)
                return false;
            formats = grammar.FourDigitYear ? ["yyyy/M/d"] : ["yy/M/d"];
        }
        else if (yearFirst)
        {
            // Only the legacy four-digit styles take a year-leading form, and
            // the remaining pair stays in the style's own order.
            if (!grammar.AllowsYearFirstAlternative)
                return false;
            formats = grammar.Order == DatePartOrder.Dmy ? ["yyyy/d/M"] : ["yyyy/M/d"];
        }
        else
        {
            formats = grammar.Order == DatePartOrder.Dmy ? ["d/M/yy", "d/M/yyyy"] : ["M/d/yy", "M/d/yyyy"];
        }

        if (!DateTime.TryParseExact(normalized, formats, UsCulture, DateTimeStyles.None, out var parsed))
        {
            // The layout was right for this style and only a field value was
            // out of range (`05/13/2026` read day-first under 103, month 13):
            // real reports the out-of-range Msg 242 rather than a format
            // failure. A shape that doesn't fit the style at all stays 241.
            outOfRange = MatchesLayoutShape(grammar, normalized);
            return false;
        }

        // The century restriction. `yy` and `yyyy` are distinguished by the
        // token's digit count, since TryParseExact's `yy` accepts four digits.
        var yearToken = yearFirst || grammar.Order == DatePartOrder.Ymd
            ? normalized[..normalized.IndexOf('/', StringComparison.Ordinal)]
            : normalized[(normalized.LastIndexOf('/') + 1)..];
        if (!(yearToken.Length == 4 ? grammar.FourDigitYear : grammar.TwoDigitYear))
        {
            wrongWidthOnly = true;
            return false;
        }

        return TryAttachTime(parsed, timePart, allowsZoneSuffix, out result);
    }

    /// <summary>
    /// Whether the input has this style's layout — three numeric tokens, the
    /// year one at an accepted width and the other two no wider than two
    /// digits — ignoring whether the field values are in range. That's the
    /// line between an out-of-range value (Msg 242 on the legacy targets) and
    /// a shape the style can't read at all (Msg 241): style 2 reads
    /// <c>01/02/99</c> as a y-m-d with an impossible day and reports 242, but
    /// <c>01/02/1999</c> isn't its layout at all and reports 241.
    /// </summary>
    private static bool MatchesLayoutShape(StyleDateGrammar grammar, string normalized)
    {
        var parts = normalized.Split('/');
        if (parts.Length != 3 || !parts.All(t => t.Length > 0 && t.All(char.IsAsciiDigit)))
            return false;
        var yearIndex = grammar.Order == DatePartOrder.Ymd ? 0 : 2;
        for (var i = 0; i < 3; i++)
        {
            if (i != yearIndex && parts[i].Length > 2)
                return false;
        }
        return parts[yearIndex].Length == 4 ? grammar.FourDigitYear : grammar.TwoDigitYear;
    }

    private static bool LooksNumericDate(string datePart) =>
        datePart.Length > 0
        && datePart.All(c => char.IsAsciiDigit(c) || c is '/' or '-' or '.')
        && datePart.Any(c => c is '/' or '-' or '.');

    private static bool TryAttachTime(DateTime date, string timePart, bool allowsZoneSuffix, out DateTime result)
    {
        if (timePart.Length == 0)
        {
            result = date;
            return true;
        }
        // The wall-clock reading is what SQL Server keeps for a `Z` suffix, so
        // strip it rather than shifting.
        var trimmed = allowsZoneSuffix ? timePart.Trim().TrimEnd('Z', 'z') : timePart.Trim();
        if (!DateTime.TryParseExact(trimmed, BareTimeFormats, UsCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
        {
            result = default;
            return false;
        }
        result = date.Add(time.TimeOfDay);
        return true;
    }

    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

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
        target == SqlType.TinyInt ? SimulatedSqlException.OverflowConvertingNarrowInt(sourceType, sourceValue, "INT1", state: 1)
        : target == SqlType.SmallInt ? SimulatedSqlException.OverflowConvertingNarrowInt(sourceType, sourceValue, "INT2", state: 2)
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
            // money picks a different error per target (Msg 232 / 220 / 237
            // for tinyint / smallint / int); smallmoney stays Msg 8115.
            throw SimulatedSqlException.TryConversionOverflow(this, target)
                ?? SimulatedSqlException.ArithmeticOverflow(target.ToString()!);
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
            DecimalSqlType => DecimalToDouble(this.AsDecimal),
            _ when this.Type == SqlType.Float => this.AsDouble,
            _ when this.Type == SqlType.Real => this.AsSingle,
            _ => throw SimulatedSqlException.ExplicitConversionNotAllowed(this.Type, target),
        };
        return target == SqlType.Float ? FromDouble(d) : FromSingle((float)d);
    }

    /// <summary>
    /// Widens a <c>decimal</c> to <see cref="double"/>, folding away .NET's
    /// signed zero. SQL Server's exact numerics have no negative zero — real
    /// reports <c>CAST(0.0 * -1 AS float)</c> as <c>0</c> — but .NET's
    /// <see cref="decimal"/> carries a sign bit through a zero result
    /// (<c>0.0m * -1</c> and <c>decimal.Negate(0m)</c> both set it), and the
    /// widening conversion would turn that into an IEEE negative zero real
    /// only produces from <c>float</c> / <c>real</c> arithmetic.
    /// </summary>
    private static double DecimalToDouble(decimal value) => value == 0 ? 0d : (double)value;

    /// <summary>
    /// Narrows a <c>decimal</c> to <see cref="float"/>, folding away .NET's
    /// signed zero for the reason <see cref="DecimalToDouble"/> gives.
    /// </summary>
    private static float DecimalToSingle(decimal value) => value == 0 ? 0f : (float)value;

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
            // float/real report the value-bearing Msg 232 for tinyint /
            // smallint / int targets; a bigint target stays Msg 8115.
            throw SimulatedSqlException.TryConversionOverflow(this, target)
                ?? SimulatedSqlException.ArithmeticOverflow(target.ToString()!);
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
        _ when SqlType.IsStringCategory(this.Type) => FromDecimal(target, RoundAndOverflowCheck(ParseDecimal(this.AsString, this.Type, target), target)),
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
            return FromDouble(DecimalToDouble(d));
        if (target == SqlType.Real)
            return FromSingle(DecimalToSingle(d));
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
        // Rounding only has work to do when the value carries more fractional
        // digits than the target declares. Asking .NET to round to a *wider*
        // scale makes it re-scale the mantissa, which overflows on a value
        // whose digits already fill the type — and there is nothing to round.
        var rounded = target.scale > 28 || value.Scale <= target.scale
            ? value
            : decimal.Round(value, target.scale, MidpointRounding.AwayFromZero);

        // Cap integer-digit count at 28 for the overflow check — a larger
        // power of ten would itself overflow .NET decimal. Values that fit
        // .NET decimal can't exceed 28 integer digits anyway, so a target
        // that wide admits everything and needs no compare at all: the
        // magnitude test is evaluated only below the cap, which also keeps
        // the table lookup in range.
        // |trunc(v)| > 10^k - 1 and |v| >= 10^k agree for every v (10^k is an
        // integer, and truncation towards zero never crosses it), so the
        // magnitude test reads the value directly rather than truncating first.
        var integerDigits = Math.Min(28, target.precision - target.scale);
        return integerDigits < 28 && decimal.Abs(rounded) >= Pow10Decimal[integerDigits]
            ? throw SimulatedSqlException.ArithmeticOverflowToNumeric()
            : rounded;
    }

    /// <summary>
    /// 10^0 … 10^28 — every power of ten .NET <see cref="decimal"/> can hold.
    /// Read by the numeric overflow check, which runs on every conversion into
    /// a <c>decimal</c> / <c>numeric</c> target: computing the bound by
    /// repeated multiplication cost up to 28 decimal multiplies per coerced
    /// value, which profiling put at a seventh of a decimal-summing aggregate's
    /// whole CPU.
    /// </summary>
    private static readonly decimal[] Pow10Decimal =
    [
        1m, 10m, 100m, 1000m, 10000m, 100000m, 1000000m, 10000000m, 100000000m,
        1000000000m, 10000000000m, 100000000000m, 1000000000000m, 10000000000000m,
        100000000000000m, 1000000000000000m, 10000000000000000m, 100000000000000000m,
        1000000000000000000m, 10000000000000000000m, 100000000000000000000m,
        1000000000000000000000m, 10000000000000000000000m, 100000000000000000000000m,
        1000000000000000000000000m, 10000000000000000000000000m,
        100000000000000000000000000m, 1000000000000000000000000000m,
        10000000000000000000000000000m,
    ];

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
    private static decimal ParseDecimal(string source, SqlType sourceType, DecimalSqlType target)
    {
        var trimmed = source.Trim();
        if (decimal.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture,
            out var d))
        {
            return d;
        }

        // A plain-digit text .NET decimal can't hold is a magnitude question,
        // not a syntax one: real reads the number and judges it against the
        // target's own precision, so a text wider than the target could ever
        // hold is Msg 8115 and everything the target would have held is the
        // simulator's 28-digit ceiling. Scientific notation is not that shape —
        // real answers Msg 8114 for `'1e40'` where it answers Msg 8115 for the
        // same magnitude written out (probed 2026-08-05).
        var integerDigits = PlainIntegerDigitCount(trimmed);
        if (integerDigits < 0)
            throw SimulatedSqlException.ConvertingDataTypeError(sourceType, "numeric");
        if (integerDigits > target.precision - target.scale)
            throw SimulatedSqlException.ArithmeticOverflowConverting(sourceType, "numeric", state: integerDigits > 38 ? (byte)6 : (byte)8);
        throw DecimalCeiling.Exceeded($"converting the string '{trimmed}' to {target}");
    }

    /// <summary>
    /// The number of integer digits an optionally-signed plain decimal text
    /// carries, leading zeros excluded, or -1 when the text is anything else
    /// (an exponent, a stray character, an empty string). Only asked once a
    /// parse has already failed, so it never runs on the conversion hot path.
    /// </summary>
    private static int PlainIntegerDigitCount(string text)
    {
        var i = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        var digits = 0;
        var seenDigit = false;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
        {
            seenDigit = true;
            if (digits > 0 || text[i] != '0')
                digits++;
        }

        if (i < text.Length && text[i] == '.')
        {
            for (i++; i < text.Length && char.IsAsciiDigit(text[i]); i++)
                seenDigit = true;
        }

        return i == text.Length && seenDigit ? digits : -1;
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
    /// Encodes a string for binary/varbinary CAST: UTF-16 LE for
    /// <c>nvarchar</c> / <c>nchar</c> / <c>ntext</c> / <c>sysname</c> sources,
    /// otherwise the source collation's own storage encoding — the bytes the
    /// column actually holds, so <c>CONVERT(varbinary, col)</c> agrees with
    /// <c>DATALENGTH(col)</c> and round-trips. Matches the default-style
    /// (style 0) byte rendering real SQL Server uses; explicit-style CONVERT
    /// routes through <see cref="CoerceStringToBinaryWithStyle"/> for the
    /// hex-string forms.
    /// </summary>
    /// <remarks>
    /// Not <c>Encoding.Latin1</c>: that is ISO-8859-1, which differs from
    /// CP1252 across 0x80-0x9F and best-fit-folds rather than preserving
    /// (probe-confirmed real: <c>€</c> → 0x80, <c>Š</c> → 0x8A, <c>—</c> →
    /// 0x97, where Latin1 gives 0x3F / 0x53 / 0x2D).
    /// </remarks>
    private static byte[] EncodeStringForBinary(string source, SqlType sourceType) =>
        sourceType is NVarcharSqlType or NCharSqlType or NTextSqlType or SystemNameSqlType
            ? System.Text.Encoding.Unicode.GetBytes(source)
            : (sourceType.Collation ?? Collation.Baseline).StorageEncoding.GetBytes(source);

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
