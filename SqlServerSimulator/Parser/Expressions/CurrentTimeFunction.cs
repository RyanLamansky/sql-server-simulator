using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Discriminator for the six current-time scalar functions. The kind also
/// dictates result type and which storage factory is invoked.
/// </summary>
internal enum CurrentTimeKind
{
    GetDate,
    GetUtcDate,
    SysDateTime,
    SysUtcDateTime,
    SysDateTimeOffset,
    CurrentTimestamp,
}

/// <summary>
/// Backs the six current-time scalar functions (<c>GETDATE</c>,
/// <c>GETUTCDATE</c>, <c>SYSDATETIME</c>, <c>SYSUTCDATETIME</c>,
/// <c>SYSDATETIMEOFFSET</c>, <c>CURRENT_TIMESTAMP</c>). All six read from
/// the executing statement's <see cref="StatementContext.UtcNow"/>
/// — captured once per top-level statement — so multiple calls within one
/// statement return identical values (matching SQL Server's per-statement
/// freeze, probe-confirmed 2026-05-09). The simulator collapses local time
/// onto UTC the way Azure SQL Database does by default: every variant
/// returns the same instant; the offset-returning variant reports
/// <c>+00:00</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reads <see cref="RuntimeContext.Batch"/>'s
/// <see cref="BatchContext.CurrentStatement"/> directly at evaluation time —
/// nothing captured at parse time. Critical for column-default reuse:
/// <c>HasDefaultValueSql("getutcdate()")</c> parses once at CREATE TABLE,
/// but every subsequent INSERT runs in a different batch, and the default
/// has to read that runtime batch's per-statement freeze.
/// </para>
/// <para>
/// <c>CURRENT_TIMESTAMP</c> is unique in being a parens-less identifier in
/// SQL Server's grammar. The parser recognizes it as
/// <c>ReservedKeyword { Keyword: Keyword.Current_Timestamp }</c> directly in
/// the expression-start switch (<see cref="Expression.Parse"/>), rather than
/// routing through <c>ResolveBuiltIn</c> which assumes a function-call
/// shape with parens. <c>CURRENT_TIMESTAMP()</c> with parens raises Msg 102
/// in SQL Server (probe-confirmed 2026-05-09) — the simulator inherits the
/// same Msg 102 from the surrounding caller's syntax check, though the
/// "near X" snippet in the error text differs.
/// </para>
/// </remarks>
internal sealed class CurrentTimeFunction(CurrentTimeKind kind) : Expression
{
    private static readonly SqlType DateTime2_7 = SqlType.GetDateTime2(7);

    private static readonly SqlType DateTimeOffset_7 = SqlType.GetDateTimeOffset(7);

    public readonly CurrentTimeKind Kind = kind;

    /// <summary>
    /// Constructs the parens-required variants. The caller (<c>ResolveBuiltIn</c>)
    /// has already consumed the opening <c>(</c>; this constructor checks that
    /// the next token is the closing <c>)</c> with no arguments in between
    /// (real SQL Server rejects any argument with Msg 174 — but the simpler
    /// "expected )" path is fine here since the message number divergence
    /// isn't observable through SqlClient unless the caller reads the text).
    /// </summary>
    public CurrentTimeFunction(ParserContext context, CurrentTimeKind kind)
        : this(kind)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var utcNow = runtime.Batch.CurrentStatement.UtcNow;
        return this.Kind switch
        {
            CurrentTimeKind.GetDate or CurrentTimeKind.GetUtcDate or CurrentTimeKind.CurrentTimestamp =>
                SqlValue.FromDateTime(utcNow),
            CurrentTimeKind.SysDateTime or CurrentTimeKind.SysUtcDateTime =>
                SqlValue.FromDateTime2(DateTime2_7, utcNow),
            CurrentTimeKind.SysDateTimeOffset =>
                SqlValue.FromDateTimeOffset(DateTimeOffset_7, new DateTimeOffset(utcNow, TimeSpan.Zero)),
            _ => throw new InvalidOperationException($"Unknown current-time kind {this.Kind}."),
        };
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => this.Kind switch
    {
        CurrentTimeKind.GetDate or CurrentTimeKind.GetUtcDate or CurrentTimeKind.CurrentTimestamp => SqlType.DateTime,
        CurrentTimeKind.SysDateTime or CurrentTimeKind.SysUtcDateTime => DateTime2_7,
        CurrentTimeKind.SysDateTimeOffset => DateTimeOffset_7,
        _ => throw new InvalidOperationException($"Unknown current-time kind {this.Kind}."),
    };

    internal override string DebugDisplay() => this.Kind switch
    {
        CurrentTimeKind.GetDate => "GETDATE()",
        CurrentTimeKind.GetUtcDate => "GETUTCDATE()",
        CurrentTimeKind.SysDateTime => "SYSDATETIME()",
        CurrentTimeKind.SysUtcDateTime => "SYSUTCDATETIME()",
        CurrentTimeKind.SysDateTimeOffset => "SYSDATETIMEOFFSET()",
        CurrentTimeKind.CurrentTimestamp => "CURRENT_TIMESTAMP",
        _ => throw new InvalidOperationException($"Unknown current-time kind {this.Kind}."),
    };
}
