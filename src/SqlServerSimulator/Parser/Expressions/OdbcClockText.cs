using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>Which part of the clock an <see cref="OdbcClockText"/> writes.</summary>
internal enum OdbcClock
{
    Date,
    Time,
    Timestamp,
}

/// <summary>
/// ODBC's clock escapes, which real answers as text of <c>GETDATE()</c>'s
/// value in the database's collation (probed 2026-10-26 against SQL Server
/// 2025): <c>{fn CURDATE()}</c> / <c>{fn CURRENT_DATE()}</c> its date as
/// <c>varchar(10)</c>, <c>{fn CURTIME()}</c> / <c>{fn CURRENT_TIME([p])}</c>
/// its time and <c>{fn CURRENT_TIMESTAMP([p])}</c> both as
/// <c>varchar(30)</c>. The time carries its three millisecond digits cut or
/// zero-padded to <c>p</c>: a NULL <c>p</c> keeps three, one below 1 writes
/// no fraction, and past ten it writes ten.
/// </summary>
internal sealed class OdbcClockText : Expression
{
    private readonly CurrentTimeFunction now = new(CurrentTimeKind.GetDate);
    private readonly OdbcClock clock;
    private readonly Expression? precision;

    /// <summary>
    /// Parses the call from the token after its <c>(</c>, leaving the cursor
    /// on its <c>)</c>; a precision is read where <paramref name="takesPrecision"/>.
    /// </summary>
    public OdbcClockText(ParserContext context, OdbcClock clock, bool takesPrecision)
    {
        this.clock = clock;
        if (takesPrecision && context.Token is not Tokens.Operator { Character: ')' })
            this.precision = Parse(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var instant = this.now.Run(runtime).CoerceTo(SqlType.GetDateTime2(3)).AsDateTime2;
        var digits = this.precision?.Run(runtime) is { IsNull: false } written ? ScalarArguments.CoerceToInt(written) : 3;
        var text = this.clock switch
        {
            OdbcClock.Date => instant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            OdbcClock.Time => TimeText(instant, digits),
            _ => instant.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + TimeText(instant, digits),
        };
        return SqlValue.FromVarchar(this.ResultType(runtime.Batch), text);
    }

    private static string TimeText(DateTime instant, int digits)
    {
        var time = instant.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (digits < 1)
            return time;
        var milliseconds = instant.Millisecond.ToString("D3", CultureInfo.InvariantCulture);
        var fraction = digits <= 3 ? milliseconds[..digits] : milliseconds.PadRight(Math.Min(digits, 10), '0');
        return time + "." + fraction;
    }

    private VarcharSqlType ResultType(BatchContext batch) =>
        VarcharSqlType.Get(this.clock == OdbcClock.Date ? 10 : 30, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.precision is not null)
            _ = AssignmentRules.ArgumentType(this.precision, SqlType.Int32, batch, resolveColumnType);
        return this.ResultType(batch);
    }

    internal override string DebugDisplay() => this.clock switch
    {
        OdbcClock.Date => "{fn CURRENT_DATE()}",
        OdbcClock.Time => $"{{fn CURRENT_TIME({this.precision?.DebugDisplay()})}}",
        _ => $"{{fn CURRENT_TIMESTAMP({this.precision?.DebugDisplay()})}}",
    };

    internal override void Describe(NodeShape shape) => shape.Local(this.clock).Child(this.precision);
}
