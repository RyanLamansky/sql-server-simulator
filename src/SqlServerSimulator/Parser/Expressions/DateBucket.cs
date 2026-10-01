using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATE_BUCKET(datepart, bucket_width, date [, origin])</c>:
/// returns the start of the bucket containing <c>date</c> when bins of
/// size <c>bucket_width × datepart</c> are laid down starting from
/// <c>origin</c>. Default <c>origin</c> is <c>1900-01-01</c> (the
/// legacy datetime baseline). Result type follows the input
/// <c>date</c>'s type, matching real SQL Server's projection rule.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-22). Algorithm:
/// <c>n = DATEDIFF(part, origin, date)</c>;
/// <c>bucket_offset = floor(n / bucket_width) * bucket_width</c>;
/// <c>result = DATEADD(part, bucket_offset, origin)</c>.
/// </remarks>
internal sealed class DateBucket : Expression
{
    private static readonly DateTime DefaultOriginDateTime = new(1900, 1, 1);
    private static readonly DateOnly DefaultOriginDate = new(1900, 1, 1);

    private readonly DatePartKind kind;
    private readonly string keywordText;
    private readonly Expression bucketWidth;
    private readonly Expression date;
    private readonly Expression? origin;

    public DateBucket(ParserContext context)
    {
        this.kind = DatePartKinds.Read(context, "Date_Bucket", out this.keywordText);
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.bucketWidth = Parse(context.MoveNextRequiredReturnSelf());
        // Neither the width nor the date takes a bare NULL (Msg 8116, probed
        // 2026-09-24 and, for the width, 2026-10-01 against SQL Server 2025).
        if (IsUntypedNullLiteral(this.bucketWidth))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", 2, "Date_Bucket");
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.date = Parse(context.MoveNextRequiredReturnSelf());
        if (IsUntypedNullLiteral(this.date))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", 3, "Date_Bucket");
        if (context.Token is Operator { Character: ',' })
            this.origin = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var dateValue = this.date.Run(runtime);
        _ = DatePartKinds.RequireDateArgument(this.date, dateValue.Type, 3, "Date_Bucket", acceptsString: false, acceptsTime: true);
        if (dateValue.IsNull)
            return SqlValue.Null(dateValue.Type);
        // Real spells the function Date_Bucket in this one message.
        DatePartKinds.RequireCompatible(this.kind, dateValue.Type, "Date_Bucket");
        var width = this.bucketWidth.Run(runtime);
        if (width.IsNull)
            return SqlValue.Null(dateValue.Type);
        var widthInt = ScalarArguments.CoerceToInt(width);
        if (widthInt < 1)
            throw SimulatedSqlException.DateBucketWidthNotPositive();
        // A NULL origin is the default one rather than a NULL answer (probed
        // 2026-10-01 against SQL Server 2025).
        var originValue = this.origin?.Run(runtime) is { IsNull: false } written ? written : DefaultOriginFor(dateValue.Type);
        // The bucket is the latest origin + k * width at or before the date,
        // counted in whole spans — probe-confirmed 2026-09-23: weeks are
        // 7-day spans from the origin (1900-01-01 is a Monday), not the
        // Sunday boundaries DATEDIFF counts, and an origin off midnight or
        // mid-month shifts every hour, day and month bucket with it. The
        // boundary count is a starting estimate at most one step out, which
        // the two loops settle.
        var distance = DatePartKinds.Diff(this.kind, originValue, dateValue);
        // The offset stays a long: a millisecond count from 1900 is past int's
        // range, and narrowing it sent the settling loops round forever.
        var bucketOffset = (long)Math.Floor((double)distance / widthInt) * widthInt;
        var bucket = DatePartKinds.Add(this.kind, originValue, bucketOffset);
        while (Later(bucket, dateValue))
        {
            bucketOffset -= widthInt;
            bucket = DatePartKinds.Add(this.kind, originValue, bucketOffset);
        }
        while (NextBucketStart(originValue, bucketOffset + widthInt) is { } next && !Later(next, dateValue))
        {
            bucketOffset += widthInt;
            bucket = next;
        }
        // A datetimeoffset's bucket is the same instant written at the date's
        // own offset (probed 2026-09-26 against SQL Server 2025).
        return dateValue.Type is DateTimeOffsetSqlType && bucket.Type is DateTimeOffsetSqlType
            ? SqlValue.FromDateTimeOffset(bucket.Type, bucket.AsDateTimeOffset.ToOffset(dateValue.AsDateTimeOffset.Offset))
            : bucket;
    }

    private SqlValue? NextBucketStart(SqlValue origin, long offset)
    {
        try
        {
            return DatePartKinds.Add(this.kind, origin, offset);
        }
        catch (SimulatedSqlException)
        {
            // Past the end of the range there is no later bucket.
            return null;
        }
    }

    private static bool Later(SqlValue candidate, SqlValue date) =>
        candidate.CoerceTo(date.Type).CompareTo(date) > 0;

    private static SqlValue DefaultOriginFor(SqlType type) =>
        type == SqlType.Date ? SqlValue.FromDate(DefaultOriginDate)
        : type == SqlType.DateTime ? SqlValue.FromDateTime(DefaultOriginDateTime)
        : type == SqlType.SmallDateTime ? SqlValue.FromSmallDateTime(DefaultOriginDateTime)
        : type is DateTime2SqlType ? SqlValue.FromDateTime2(type, DefaultOriginDateTime)
        : type is DateTimeOffsetSqlType ? SqlValue.FromDateTimeOffset(type, new DateTimeOffset(DefaultOriginDateTime, TimeSpan.Zero))
        // A time buckets from midnight and stays a time (probed 2026-10-01).
        : type is TimeSqlType ? SqlValue.FromTime(type, TimeSpan.Zero)
        : SqlValue.FromDateTime(DefaultOriginDateTime);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        // Unlike the other date functions, DATE_BUCKET takes no string for
        // its date, and real spells the function Date_Bucket (probe-confirmed
        // 2026-09-23 against SQL Server 2025).
        DatePartKinds.RequireDateArgument(this.date, this.date.GetSqlType(batch, resolveColumnType), 3, "Date_Bucket", acceptsString: false, acceptsTime: true);

    internal override string DebugDisplay() => $"DATE_BUCKET({this.keywordText}, {this.bucketWidth.DebugDisplay()}, {this.date.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.kind).Child(this.bucketWidth).Child(this.date).Child(this.origin);
}
