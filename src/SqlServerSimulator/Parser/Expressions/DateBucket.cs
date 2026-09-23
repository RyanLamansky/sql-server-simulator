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
        this.keywordText = context.Token is Name name
            ? name.Value
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        this.kind = DatePartKinds.ResolveOrThrow(this.keywordText, "date_bucket");
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.bucketWidth = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.date = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Operator { Character: ',' })
            this.origin = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var dateValue = this.date.Run(runtime);
        _ = RejectStringDate(dateValue.Type);
        if (dateValue.IsNull)
            return SqlValue.Null(dateValue.Type);
        var width = this.bucketWidth.Run(runtime);
        if (width.IsNull)
            return SqlValue.Null(dateValue.Type);
        var widthInt = ScalarArguments.CoerceToInt(width);
        if (widthInt < 1)
            throw SimulatedSqlException.DateAddOverflow("int");
        var originValue = this.origin?.Run(runtime) ?? DefaultOriginFor(dateValue.Type);
        if (originValue.IsNull)
            return SqlValue.Null(dateValue.Type);
        // The bucket is the latest origin + k * width at or before the date,
        // counted in whole spans — probe-confirmed 2026-09-23: weeks are
        // 7-day spans from the origin (1900-01-01 is a Monday), not the
        // Sunday boundaries DATEDIFF counts, and an origin off midnight or
        // mid-month shifts every hour, day and month bucket with it. The
        // boundary count is a starting estimate at most one step out, which
        // the two loops settle.
        var distance = DatePartKinds.Diff(this.kind, originValue, dateValue);
        var bucketOffset = (long)Math.Floor((double)distance / widthInt) * widthInt;
        var bucket = DatePartKinds.Add(this.kind, originValue, (int)bucketOffset);
        while (Later(bucket, dateValue))
        {
            bucketOffset -= widthInt;
            bucket = DatePartKinds.Add(this.kind, originValue, (int)bucketOffset);
        }
        while (NextBucketStart(originValue, bucketOffset + widthInt) is { } next && !Later(next, dateValue))
        {
            bucketOffset += widthInt;
            bucket = next;
        }
        return bucket;
    }

    private SqlValue? NextBucketStart(SqlValue origin, long offset)
    {
        try
        {
            return DatePartKinds.Add(this.kind, origin, (int)offset);
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
        : SqlValue.FromDateTime(DefaultOriginDateTime);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        DatePartKinds.ResolveImplicitDateType(RejectStringDate(this.date.GetSqlType(batch, resolveColumnType)));

    /// <summary>
    /// Unlike the other date functions, DATE_BUCKET takes no string for its
    /// date — Msg 8116, spelling the function <c>Date_Bucket</c>
    /// (probe-confirmed 2026-09-23 against SQL Server 2025).
    /// </summary>
    private static SqlType RejectStringDate(SqlType dateType) =>
        SqlType.IsStringCategory(dateType)
            ? throw SimulatedSqlException.InvalidArgumentDataType(SimulatedSqlException.FamilyRootName(dateType), 3, "Date_Bucket")
            : dateType;

    internal override string DebugDisplay() => $"DATE_BUCKET({this.keywordText}, {this.bucketWidth.DebugDisplay()}, {this.date.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.kind).Child(this.bucketWidth).Child(this.date).Child(this.origin);
}
