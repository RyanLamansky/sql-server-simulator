using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@DATEFIRST</c>: returns the session's <c>SET DATEFIRST</c> value
/// as <see cref="SqlType.TinyInt"/> (real's <c>tinyint</c> projection). Default
/// <c>7</c> — Sunday, the us_english setting a fresh session gets under both
/// sqlcmd and SqlClient (probe-confirmed).
/// </summary>
internal sealed class DateFirstExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromByte(runtime.Batch.Connection.DateFirst);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.TinyInt;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@DATEFIRST";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// Backs <c>@@LANGUAGE</c>: the official name of the session's
/// <c>SET LANGUAGE</c> — the <c>name</c> column of <c>sys.syslanguages</c>, not
/// the alias the statement may have been written with, so
/// <c>SET LANGUAGE German</c> reads back <c>Deutsch</c> (probe-confirmed).
/// </summary>
internal sealed class LanguageExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(runtime.Batch.Connection.Language.Name);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@LANGUAGE";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// Backs <c>@@LANGID</c>: the <c>langid</c> of the session's
/// <c>SET LANGUAGE</c>, as real's <c>smallint</c>. Default <c>0</c>
/// (us_english).
/// </summary>
internal sealed class LangIdExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt16(runtime.Batch.Connection.Language.LangId);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@LANGID";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// Backs <c>@@OPTIONS</c>: the session's option bits as it holds them when the
/// read runs — ANSI_WARNINGS 8, ANSI_PADDING 16, ANSI_NULLS 32, ARITHABORT 64,
/// NOCOUNT 512, CONCAT_NULL_YIELDS_NULL 4096, NUMERIC_ROUNDABORT 8192 and
/// XACT_ABORT 16384 — with QUOTED_IDENTIFIER (256) at the parse position, since
/// that option is itself parse-time and the plan cache keys on it, and
/// ANSI_NULL_DFLT_ON (1024) constant; a fresh session reads 5432 (probed
/// 2026-09-26 against SQL Server 2025).
/// </summary>
internal sealed class OptionsExpression(ParserContext context) : Expression
{
    // ANSI_NULL_DFLT_ON, which SqlClient's login sets and the simulator doesn't
    // otherwise track.
    private const int AnsiNullDefaultOn = 1024;

    // QUOTED_IDENTIFIER is settled while parsing, as the setting itself is.
    private readonly int parsedOptions = AnsiNullDefaultOn | (context.QuotedIdentifiers ? 256 : 0);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var connection = runtime.Batch.Connection;
        return SqlValue.FromInt32(this.parsedOptions
            | (connection.AnsiWarnings ? 8 : 0)
            | (connection.AnsiPadding ? 16 : 0)
            | (connection.AnsiNulls ? 32 : 0)
            | (connection.Arithabort ? 64 : 0)
            | (connection.NoCount ? 512 : 0)
            | (connection.ConcatNullYieldsNull ? 4096 : 0)
            | (connection.NumericRoundabort ? 8192 : 0)
            | (connection.XactAbort ? 16384 : 0));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@OPTIONS";

    internal override void Describe(NodeShape shape) => shape.Local(this.parsedOptions);
}
