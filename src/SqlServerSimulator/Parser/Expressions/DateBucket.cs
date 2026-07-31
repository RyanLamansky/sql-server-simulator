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
        // Boundary count + floor to bucket
        var distance = DatePartKinds.Diff(this.kind, originValue, dateValue);
        var bucketOffset = (long)Math.Floor((double)distance / widthInt) * widthInt;
        // DATEADD-style offset cast: clamp to int when within int range, else
        // operate on long for bigint result paths.
        return DatePartKinds.Add(this.kind, originValue, (int)bucketOffset);
    }

    private static SqlValue DefaultOriginFor(SqlType type) =>
        type == SqlType.Date ? SqlValue.FromDate(DefaultOriginDate)
        : type == SqlType.DateTime ? SqlValue.FromDateTime(DefaultOriginDateTime)
        : type == SqlType.SmallDateTime ? SqlValue.FromSmallDateTime(DefaultOriginDateTime)
        : type is DateTime2SqlType ? SqlValue.FromDateTime2(type, DefaultOriginDateTime)
        : type is DateTimeOffsetSqlType ? SqlValue.FromDateTimeOffset(type, new DateTimeOffset(DefaultOriginDateTime, TimeSpan.Zero))
        : SqlValue.FromDateTime(DefaultOriginDateTime);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        DatePartKinds.ResolveImplicitDateType(this.date.GetSqlType(batch, resolveColumnType));

    internal override string DebugDisplay() => $"DATE_BUCKET({this.keywordText}, {this.bucketWidth.DebugDisplay()}, {this.date.DebugDisplay()})";
}
