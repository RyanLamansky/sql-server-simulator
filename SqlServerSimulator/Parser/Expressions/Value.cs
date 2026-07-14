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

    /// <summary>Bare <c>NULL</c> literal — typed as <see cref="SqlType.Int32"/>; SQL Server has no truly untyped NULL, so we pick a default type.</summary>
    public Value() => this.Constant = SqlValue.Null(SqlType.Int32);

    public Value(SqlValue value) => this.Constant = value;

    public Value(DoubleAtPrefixedString doubleAtPrefixedString)
    {
        // Constant-valued @@ keywords land here. Session-state-dependent
        // ones (@@TRANCOUNT, @@ROWCOUNT, @@LOCK_TIMEOUT, @@SPID, @@DBTS,
        // @@NESTLEVEL, @@PROCID) route to dedicated expression classes
        // through Expression.Parse's double-at switch — they need runtime
        // batch/connection/database access this constant form can't reach.
        // Values below match SQL Server 2025 defaults (probe-confirmed
        // 2026-05-22) — exact server name and configurable session knobs
        // (TEXTSIZE, DATEFIRST) collapse to documented defaults since the
        // simulator parses-and-discards the corresponding SET commands.
        switch (doubleAtPrefixedString.Parse())
        {
            case AtAtKeyword.Version:
                this.Constant = SqlValue.FromNVarchar("SQL Server Simulator");
                return;
            case AtAtKeyword.MicrosoftVersion:
                // (major << 24) | (minor << 16) | build, self-consistent with
                // SERVERPROPERTY('ProductVersion') = "17.0.0.0" → 0x11000000.
                // The deliberately 0-build version doubles as a "not a real
                // SQL Server" marker.
                this.Constant = SqlValue.FromInt32(0x11000000);
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
            case AtAtKeyword.TextSize:
                this.Constant = SqlValue.FromInt32(-1);
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
        new(SqlValue.FromInt32(context.QuotedIdentifiers ? 5432 : 5432 & ~256));

    public override SqlValue Run(RuntimeContext runtime) => this.Constant;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Constant.Type;

    internal override string DebugDisplay() => this.Constant.DebugDisplay();

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => this.Constant.IsNull;
}
