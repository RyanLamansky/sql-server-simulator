using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COLLATIONPROPERTY(collation_name, property)</c>: returns a metadata
/// value for a collation. Like real SQL Server, the result is always
/// <c>sql_variant</c> (<see cref="SqlType.SqlVariant"/>) carrying a per-property
/// inner base type — <c>CodePage</c> / <c>LCID</c> / <c>ComparisonStyle</c> as
/// <see cref="SqlType.Int32"/>, <c>Version</c> as <see cref="SqlType.TinyInt"/>
/// (probe-confirmed against SQL Server 2025), <c>Name</c> as
/// <see cref="SqlType.NVarchar"/>. An unrecognized collation name or an unknown
/// property returns a NULL <c>sql_variant</c>. Property names are
/// case-insensitive; the underlying values derive from the simulator's
/// collation model, so any recognized collation resolves.
/// </summary>
internal sealed class CollationProperty : Expression
{
    private static readonly FrozenDictionary<string, Func<Collation.CollationMetrics, SqlValue>> Properties = new Dictionary<string, Func<Collation.CollationMetrics, SqlValue>>
    {
        ["CodePage"] = m => SqlValue.FromInt32(m.CodePage),
        ["LCID"] = m => SqlValue.FromInt32(m.Lcid),
        ["ComparisonStyle"] = m => SqlValue.FromInt32(m.ComparisonStyle),
        ["Version"] = m => SqlValue.FromByte(checked((byte)m.Version)),
        ["Name"] = m => SqlValue.FromNVarchar(m.Name),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly Expression collationArg;
    private readonly Expression propertyArg;

    public CollationProperty(ParserContext context)
    {
        this.collationArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        this.propertyArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var collationValue = this.collationArg.Run(runtime);
        var propertyValue = this.propertyArg.Run(runtime);
        return collationValue.IsNull || propertyValue.IsNull
            || !Properties.TryGetValue(propertyValue.CoerceTo(SqlType.NVarchar).AsString, out var produce)
            || !Collation.TryGetMetrics(collationValue.CoerceTo(SqlType.NVarchar).AsString, out var metrics)
            ? SqlValue.Null(SqlType.SqlVariant)
            : SqlValue.FromVariant(produce(metrics));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    internal override string DebugDisplay() => $"COLLATIONPROPERTY({this.collationArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
