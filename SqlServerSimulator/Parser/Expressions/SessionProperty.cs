using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SESSIONPROPERTY('option_name')</c>: returns the current session
/// setting for one of the ANSI / arithmetic SET options. Real SQL Server
/// projects the result as <c>sql_variant</c> carrying an inner base type of
/// <c>int</c> (each option reads back as 1 / 0); the simulator doesn't model
/// sql_variant, so — following the <c>SERVERPROPERTY</c> convention — it
/// surfaces the inner base type directly as <see cref="SqlType.Int32"/>. The
/// value tracks live session state on
/// <see cref="SimulatedDbConnection"/>: the six toggles recorded by
/// <c>SET ANSI_NULLS / ANSI_PADDING / ANSI_WARNINGS / ARITHABORT /
/// CONCAT_NULL_YIELDS_NULL / NUMERIC_ROUNDABORT</c> plus the pre-existing
/// <c>QUOTED_IDENTIFIER</c> state. An unknown option name returns NULL
/// (matches real-server convention); names are case-insensitive
/// (probe-confirmed against SQL Server 2025). When the name argument is a
/// compile-time string constant the true type (int) flows to the projection
/// schema; otherwise it falls back to <see cref="SqlType.NVarchar"/> and the
/// runtime value is coerced to match (the static/runtime parity contract).
/// </summary>
/// <remarks>
/// DacFx's bacpac-export preamble reads
/// <c>ISNULL(SESSIONPROPERTY('ANSI_NULLS'), 0)</c> /
/// <c>ISNULL(SESSIONPROPERTY('QUOTED_IDENTIFIER'), 1)</c>.
/// </remarks>
internal sealed class SessionProperty : Expression
{
    private static readonly FrozenDictionary<string, Func<SimulatedDbConnection, bool>> Properties = new Dictionary<string, Func<SimulatedDbConnection, bool>>(StringComparer.OrdinalIgnoreCase)
    {
        ["ANSI_NULLS"] = c => c.AnsiNulls,
        ["ANSI_PADDING"] = c => c.AnsiPadding,
        ["ANSI_WARNINGS"] = c => c.AnsiWarnings,
        ["ARITHABORT"] = c => c.Arithabort,
        ["CONCAT_NULL_YIELDS_NULL"] = c => c.ConcatNullYieldsNull,
        ["NUMERIC_ROUNDABORT"] = c => c.NumericRoundabort,
        ["QUOTED_IDENTIFIER"] = c => c.QuotedIdentifiers,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly Expression nameArg;

    public SessionProperty(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var n = this.nameArg.Run(runtime);
        if (n.IsNull)
            return SqlValue.Null(SqlType.NVarchar);
        var name = n.CoerceTo(SqlType.NVarchar).AsString;
        if (!Properties.TryGetValue(name, out var read))
            return SqlValue.Null(SqlType.NVarchar);
        var value = SqlValue.FromInt32(read(runtime.Batch.Connection) ? 1 : 0);
        // A non-constant name argument couldn't resolve a true type at parse
        // time (GetSqlType fell back to NVarchar); coerce so runtime agrees.
        return this.nameArg is Value ? value : value.CoerceTo(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => this.nameArg is Value { Constant: { IsNull: false } constant }
            && Properties.ContainsKey(constant.CoerceTo(SqlType.NVarchar).AsString)
            ? SqlType.Int32
            : SqlType.NVarchar;

    internal override string DebugDisplay() => $"SESSIONPROPERTY({this.nameArg.DebugDisplay()})";
}
