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
        return n.IsNull
            ? SqlValue.Null(SqlType.SqlVariant)
            : Read(n.CoerceTo(SqlType.NVarchar).AsString, runtime.Batch.Connection);
    }

    private static SqlValue Read(string name, SimulatedDbConnection connection)
    {
        // Longer than any recognized option name; also bounds the stackalloc
        // against an adversarially long argument.
        if (name.Length > 32)
            return SqlValue.Null(SqlType.SqlVariant);
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        bool? option = upper switch
        {
            "ANSI_NULLS" => connection.AnsiNulls,
            "ANSI_PADDING" => connection.AnsiPadding,
            "ANSI_WARNINGS" => connection.AnsiWarnings,
            "ARITHABORT" => connection.Arithabort,
            "CONCAT_NULL_YIELDS_NULL" => connection.ConcatNullYieldsNull,
            "NUMERIC_ROUNDABORT" => connection.NumericRoundabort,
            "QUOTED_IDENTIFIER" => connection.QuotedIdentifiers,
            _ => null,
        };
        return option is { } on
            ? SqlValue.FromVariant(SqlValue.FromInt32(on ? 1 : 0))
            : SqlValue.Null(SqlType.SqlVariant);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    internal override string DebugDisplay() => $"SESSIONPROPERTY({this.nameArg.DebugDisplay()})";
}
