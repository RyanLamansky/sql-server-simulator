using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SESSIONPROPERTY('option_name')</c>: returns the current session
/// setting for one of the ANSI / arithmetic SET options. Like real SQL Server,
/// the result is <c>sql_variant</c> (<see cref="SqlType.SqlVariant"/>) carrying
/// an inner base type of <see cref="SqlType.Int32"/> (each option reads back as
/// 1 / 0). The value tracks live session state on
/// <see cref="SimulatedDbConnection"/>: the six toggles recorded by
/// <c>SET ANSI_NULLS / ANSI_PADDING / ANSI_WARNINGS / ARITHABORT /
/// CONCAT_NULL_YIELDS_NULL / NUMERIC_ROUNDABORT</c> plus the pre-existing
/// <c>QUOTED_IDENTIFIER</c> state. An unknown option name returns a NULL
/// <c>sql_variant</c>; names are case-insensitive (probe-confirmed against SQL
/// Server 2025).
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
            return SqlValue.Null(SqlType.SqlVariant);
        var name = n.CoerceTo(SqlType.NVarchar).AsString;
        return Properties.TryGetValue(name, out var read)
            ? SqlValue.FromVariant(SqlValue.FromInt32(read(runtime.Batch.Connection) ? 1 : 0))
            : SqlValue.Null(SqlType.SqlVariant);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    internal override string DebugDisplay() => $"SESSIONPROPERTY({this.nameArg.DebugDisplay()})";
}
