using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@DATEFIRST</c>: returns the session's <c>SET DATEFIRST</c> value
/// as <see cref="SqlType.TinyInt"/> (real's <c>tinyint</c> projection). Default
/// <c>7</c> — Sunday, the us_english setting a fresh session gets under both
/// sqlcmd and SqlClient (probe-confirmed).
/// </summary>
internal sealed class DateFirstExpression(ParserContext context) : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromByte(context.Connection.DateFirst);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.TinyInt;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@DATEFIRST";
}

/// <summary>
/// Backs <c>@@LANGUAGE</c>: the official name of the session's
/// <c>SET LANGUAGE</c> — the <c>name</c> column of <c>sys.syslanguages</c>, not
/// the alias the statement may have been written with, so
/// <c>SET LANGUAGE German</c> reads back <c>Deutsch</c> (probe-confirmed).
/// </summary>
internal sealed class LanguageExpression(ParserContext context) : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(context.Connection.Language.Name);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@LANGUAGE";
}

/// <summary>
/// Backs <c>@@LANGID</c>: the <c>langid</c> of the session's
/// <c>SET LANGUAGE</c>, as real's <c>smallint</c>. Default <c>0</c>
/// (us_english).
/// </summary>
internal sealed class LangIdExpression(ParserContext context) : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt16(context.Connection.Language.LangId);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@LANGID";
}

/// <summary>
/// Backs <c>@@OPTIONS</c>: SQL Server 2025's fresh-session default 5432
/// (probe-confirmed 2026-05-22 — QUOTED_IDENTIFIER, ANSI_WARNINGS,
/// ANSI_PADDING, ANSI_NULLS, ANSI_NULL_DFLT_ON, CONCAT_NULL_YIELDS_NULL), with
/// the two bits the simulator's <c>SET</c> surface models tracking the session:
/// QUOTED_IDENTIFIER (256) at the parse position, since that option is itself
/// parse-time and the plan cache keys on it, and XACT_ABORT (16384) at run
/// time, since a <c>SET XACT_ABORT</c> inside a procedure body binds and
/// reverts around the read.
/// </summary>
internal sealed class OptionsExpression(ParserContext context) : Expression
{
    private const int FreshSessionOptions = 5432;

    private readonly int baseOptions = context.QuotedIdentifiers ? FreshSessionOptions : FreshSessionOptions & ~256;

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.XactAbort ? this.baseOptions | 16384 : this.baseOptions);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@OPTIONS";
}
