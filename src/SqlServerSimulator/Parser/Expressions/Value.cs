using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Value : Expression
{
    /// <summary>
    /// The literal this expression represents. Exposed so callers (e.g. the
    /// ORDER BY parser) can detect the integer-ordinal form syntactically
    /// rather than waiting for runtime evaluation.
    /// </summary>
    public readonly SqlValue Constant;

    /// <summary>
    /// Significant-digit count when this is a non-negative <b>integer</b>
    /// literal (<c>0</c> otherwise). Lets the promotion sites size the literal
    /// as <c>numeric(digit_count, 0)</c> when it meets a decimal partner — see
    /// <see cref="Tokens.Numeric.IntegerLiteralDigitCount"/> and
    /// <see cref="Expression.IntegerLiteralDigits"/>.
    /// </summary>
    internal readonly int IntegerLiteralDigitCount;

    /// <summary>
    /// True only for the bare <c>NULL</c> keyword (the parameterless ctor): an
    /// untyped NULL that yields to any typed operand in <c>COALESCE</c> /
    /// <c>ISNULL</c> / <c>CASE</c> / <c>IIF</c> / set-op promotion rather than
    /// forcing its <see cref="SqlType.Int32"/> placeholder onto the result.
    /// A typed NULL (<c>CAST(NULL AS …)</c>, <c>@@REMSERVER</c>) is not flagged.
    /// </summary>
    internal readonly bool IsUntypedNull;

    /// <summary>
    /// True when the constant was written as a literal in the SQL text, as
    /// opposed to standing in for a constant-valued <c>@@</c> keyword or a
    /// parser-synthesized placeholder. ORDER BY's Msg 408 gate reads this:
    /// real rejects a literal term but sorts happily by <c>@@VERSION</c> /
    /// <c>@@MAX_PRECISION</c> (probe-confirmed), because a niladic function is
    /// evaluated per statement rather than folded.
    /// </summary>
    internal readonly bool IsLiteral;

    /// <summary>Bare <c>NULL</c> literal — typed as <see cref="SqlType.Int32"/>; SQL Server has no truly untyped NULL, so we pick a default type that yields to any typed sibling in promotion.</summary>
    public Value()
    {
        this.Constant = SqlValue.Null(SqlType.Int32);
        this.IsUntypedNull = true;
        this.IsLiteral = true;
    }

    public Value(SqlValue value)
    {
        this.Constant = value;
        this.IsLiteral = true;
    }

    /// <summary>
    /// Constant that isn't a written literal — the <c>@@OPTIONS</c> bitmask.
    /// Kept out of <see cref="IsLiteral"/> so it doesn't read as a constant
    /// ORDER BY term.
    /// </summary>
    internal static Value NonLiteral(SqlValue value) => new(value, untypedNull: false);

    /// <summary>
    /// Untyped-NULL placeholder standing in for an expression the parser
    /// discarded (a skip-mode deferred function call). Promotes like the bare
    /// <c>NULL</c> keyword but isn't a written literal, so a dead branch's
    /// <c>ORDER BY dbo.missing()</c> doesn't read as a constant term.
    /// </summary>
    internal static Value UntypedNullPlaceholder() => new(SqlValue.Null(SqlType.Int32), untypedNull: true);

    private Value(SqlValue value, bool untypedNull)
    {
        this.Constant = value;
        this.IsUntypedNull = untypedNull;
    }

    /// <summary>
    /// Integer-literal ctor carrying the token's significant-digit count for
    /// decimal-arithmetic sizing (see <see cref="IntegerLiteralDigitCount"/>).
    /// </summary>
    public Value(SqlValue value, int integerLiteralDigitCount)
    {
        this.Constant = value;
        this.IntegerLiteralDigitCount = integerLiteralDigitCount;
        this.IsLiteral = true;
    }

    public Value(DoubleAtPrefixedString doubleAtPrefixedString)
    {
        // Constant-valued @@ keywords land here. Session-state-dependent
        // ones (@@TRANCOUNT, @@ROWCOUNT, @@LOCK_TIMEOUT, @@SPID, @@DBTS,
        // @@NESTLEVEL, @@PROCID) route to dedicated expression classes
        // through Expression.Parse's double-at switch — they need runtime
        // batch/connection/database access this constant form can't reach.
        // Values below match SQL Server 2025 defaults (probe-confirmed
        // 2026-05-22) — exact server name and configurable session knobs
        // (DATEFIRST) collapse to documented defaults since the simulator
        // parses-and-discards the corresponding SET commands. @@TEXTSIZE
        // routes to TextSizeExpression (SET TEXTSIZE carries semantic effect).
        switch (doubleAtPrefixedString.Parse())
        {
            case AtAtKeyword.Version:
                this.Constant = SqlValue.FromNVarchar(ReferenceBuild.Banner);
                return;
            case AtAtKeyword.MicrosoftVersion:
                this.Constant = SqlValue.FromInt32(ReferenceBuild.MicrosoftVersion);
                return;
            case AtAtKeyword.MaxPrecision:
                this.Constant = SqlValue.FromByte(38);
                return;
            case AtAtKeyword.MaxConnections:
                this.Constant = SqlValue.FromInt32(32767);
                return;
            case AtAtKeyword.LangId:
                this.Constant = SqlValue.FromInt16(0);
                return;
            case AtAtKeyword.Language:
                this.Constant = SqlValue.FromNVarchar("us_english");
                return;
            case AtAtKeyword.ServiceName:
                this.Constant = SqlValue.FromNVarchar("MSSQLSERVER");
                return;
            case AtAtKeyword.ServerName:
                this.Constant = SqlValue.FromNVarchar("SIMULATED");
                return;
            case AtAtKeyword.RemServer:
                this.Constant = SqlValue.Null(SqlType.NVarchar);
                return;
            case AtAtKeyword.DateFirst:
                this.Constant = SqlValue.FromByte(7);
                return;
            // System statistical counters (all int, probe-confirmed
            // 2026-07-19 against SQL Server 2025). The in-process simulator
            // does no physical IO, CPU-time accounting, or TDS packet
            // counting, so the elapsed-activity totals report 0 — the honest
            // reading for a freshly started, idle instance. @@PACKET_ERRORS
            // and @@TOTAL_ERRORS report 0 on a healthy real server too.
            // @@CONNECTIONS routes to a dedicated runtime expression (it
            // reflects the live session-allocation count). @@TIMETICKS is the
            // hardware-invariant microseconds-per-tick constant real reports.
            case AtAtKeyword.CpuBusy:
            case AtAtKeyword.Idle:
            case AtAtKeyword.IoBusy:
            case AtAtKeyword.PackReceived:
            case AtAtKeyword.PacketErrors:
            case AtAtKeyword.PackSent:
            case AtAtKeyword.TotalErrors:
            case AtAtKeyword.TotalRead:
            case AtAtKeyword.TotalWrite:
                this.Constant = SqlValue.FromInt32(0);
                return;
            case AtAtKeyword.TimeTicks:
                this.Constant = SqlValue.FromInt32(31250);
                return;
        }

        throw new NotSupportedException($"Simulator doesn't recognize {doubleAtPrefixedString}.");
    }

    /// <summary>
    /// Builds the <c>@@OPTIONS</c> constant: SQL Server 2025's fresh-session
    /// default 5432 (probe-confirmed 2026-05-22), with the QUOTED_IDENTIFIER
    /// bit (256) tracking the parse-position setting — the one component the
    /// simulator's <c>SET</c> surface actually models. Baking the value at
    /// parse is correct because QUOTED_IDENTIFIER is itself a parse-time
    /// option and the plan cache keys on it.
    /// </summary>
    public static Value FromAtAtOptions(ParserContext context) =>
        NonLiteral(SqlValue.FromInt32(context.QuotedIdentifiers ? 5432 : 5432 & ~256));

    public override SqlValue Run(RuntimeContext runtime) => this.Constant;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Constant.Type;

    internal override string DebugDisplay() => this.Constant.DebugDisplay();

    internal override bool IsRowIndependent => true;

    internal override bool IsWrittenConstant => this.IsLiteral;

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => this.Constant.IsNull;

    // A decimal-typed Value is always a decimal/numeric literal (constant @@
    // keywords never land on decimal, and parameters are separate expression
    // classes), and every such literal is numeric-named.
    internal override bool ResultReportsNumeric => this.Constant.Type is DecimalSqlType;
}
