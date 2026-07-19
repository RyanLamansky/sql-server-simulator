using System.Data.SqlTypes;

namespace SqlServerSimulator.Storage;

/// <summary>
/// <c>sql_variant</c> cross-type comparison: SQL Server orders variant values
/// two-level — first by datatype-family rank, then by value within the family
/// (probe-confirmed against SQL Server 2025, 2026-07-19). A value of a higher
/// family sorts above <em>any</em> value of a lower family, value ignored;
/// within a family values compare by actual value across base types, and
/// equal values are truly equal (<c>=</c> true, one GROUP BY / DISTINCT
/// bucket, ORDER BY tie with undefined relative order). The six observed
/// families, lowest to highest: 1 <c>uniqueidentifier</c>; 2 binary
/// (<c>binary</c>/<c>varbinary</c>, byte-lexicographic); 3 character
/// (<c>char</c>/<c>varchar</c>/<c>nchar</c>/<c>nvarchar</c> — Unicode and
/// non-Unicode are one family); 4 exact numeric (<c>bit</c> through
/// <c>bigint</c>, <c>decimal</c>, <c>money</c>/<c>smallmoney</c>);
/// 5 approximate (<c>real</c>/<c>float</c> — above every exact value
/// regardless of magnitude); 6 date/time (compared as an instant:
/// <c>time</c> anchored to 1900-01-01, <c>datetimeoffset</c> by its UTC
/// instant). Consumed by the <c>sql_variant</c> arms of
/// <see cref="SqlValue.CompareTo"/> / <see cref="SqlValue.Equals(SqlValue)"/> /
/// <see cref="SqlValue.GetHashCode"/>, which ORDER BY, GROUP BY, DISTINCT,
/// MIN/MAX, and the both-operands-variant comparison path all ride.
/// </summary>
internal static class SqlVariantOrdering
{
    private static readonly long Epoch1900Ticks = new DateTime(1900, 1, 1).Ticks;

    /// <summary>Orders two non-NULL variant inner values by family rank, then value.</summary>
    public static int Compare(SqlValue a, SqlValue b)
    {
        var rankA = FamilyRank(a.Type);
        var rankB = FamilyRank(b.Type);
        return rankA != rankB ? rankA.CompareTo(rankB) : rankA switch
        {
            1 => new SqlGuid(a.AsGuid).CompareTo(new SqlGuid(b.AsGuid)),
            2 => a.AsBytes.AsSpan().SequenceCompareTo(b.AsBytes),
            3 => CompareStrings(a, b),
            4 => ExactValue(a).CompareTo(ExactValue(b)),
            5 => ApproximateValue(a).CompareTo(ApproximateValue(b)),
            _ => InstantTicks(a).CompareTo(InstantTicks(b)),
        };
    }

    /// <summary>
    /// Hash agreeing with <see cref="Compare"/>-equality: family rank plus the
    /// canonical within-family value, so <c>int 5</c> / <c>bigint 5</c> /
    /// <c>decimal 5.00</c> land in one bucket. The character family hashes by
    /// rank alone: same-collation pairs compare by that collation while
    /// cross-collation pairs compare by code point (see
    /// <see cref="CompareStrings"/>), and no single string hash can agree with
    /// both regimes at once — string-variant grouping degrades to bucket
    /// scans, a negligible cost for the surface.
    /// </summary>
    public static int InnerHashCode(SqlValue value)
    {
        var rank = FamilyRank(value.Type);
        var hash = new HashCode();
        hash.Add(rank);
        switch (rank)
        {
            case 1:
                hash.Add(value.AsGuid);
                break;
            case 2:
                hash.AddBytes(value.AsBytes);
                break;
            case 3:
                break;
            case 4:
                hash.Add(ExactValue(value));
                break;
            case 5:
                hash.Add(ApproximateValue(value));
                break;
            default:
                hash.Add(InstantTicks(value));
                break;
        }

        return hash.ToHashCode();
    }

    private static int FamilyRank(SqlType type) => type.Category switch
    {
        SqlTypeCategory.UniqueIdentifier => 1,
        SqlTypeCategory.String => 3,
        SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money => 4,
        SqlTypeCategory.Approximate => 5,
        SqlTypeCategory.DateTime => 6,
        _ => type is BinarySqlType or VarbinarySqlType
            ? 2
            : throw new NotSupportedException($"sql_variant ordering for inner type '{type.SqlServerName}' isn't implemented."),
    };

    /// <summary>
    /// Character-family compare. A same-collation pair compares under that
    /// collation; a cross-collation pair compares by code point without a
    /// Msg 468 conflict (probe-confirmed: variant-wrapped
    /// <c>Latin1_General_BIN</c> vs <c>SQL_Latin1_General_CP1_CI_AS</c>
    /// operands sort case-sensitively, unlike bare cross-collation
    /// <c>varchar</c> which raises). Trailing spaces are trimmed, matching
    /// the engine's ANSI-padding string equality.
    /// </summary>
    private static int CompareStrings(SqlValue a, SqlValue b)
    {
        var left = a.AsString.TrimEnd(' ');
        var right = b.AsString.TrimEnd(' ');
        var collation = a.Type.Collation;
        return collation is not null && ReferenceEquals(collation, b.Type.Collation)
            ? collation.Compare(left, right)
            : string.CompareOrdinal(left, right);
    }

    /// <summary>The exact-numeric family's canonical value — decimal holds every member's range.</summary>
    private static decimal ExactValue(SqlValue value) => value.Type switch
    {
        BitSqlType => value.AsBoolean ? 1m : 0m,
        TinyIntSqlType => value.AsByte,
        SmallIntSqlType => value.AsInt16,
        Int32SqlType => value.AsInt32,
        BigIntSqlType => value.AsInt64,
        DecimalSqlType => value.AsDecimal,
        _ => value.AsMoneyScaledUnits / 10000m,
    };

    private static double ApproximateValue(SqlValue value) =>
        value.Type == SqlType.Float ? value.AsDouble : value.AsSingle;

    /// <summary>
    /// The date/time family's canonical instant in ticks: <c>time</c> anchors
    /// to 1900-01-01 (probe: <c>time 23:59</c> sorts below
    /// <c>date 2050-…</c>), <c>datetimeoffset</c> compares by its UTC
    /// instant (probe: <c>datetimeoffset 1990</c> sorts below
    /// <c>datetime 2050</c>).
    /// </summary>
    private static long InstantTicks(SqlValue value) => value.Type switch
    {
        TimeSqlType => Epoch1900Ticks + value.AsTime.Ticks,
        DateSqlType => value.AsDate.DayNumber * TimeSpan.TicksPerDay,
        SmallDateTimeSqlType => value.AsSmallDateTime.Ticks,
        DateTime2SqlType => value.AsDateTime2.Ticks,
        DateTimeOffsetSqlType => value.AsDateTimeOffset.UtcTicks,
        _ => value.AsDateTime.Ticks,
    };
}
