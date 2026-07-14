using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COLLATIONPROPERTY(collation_name, property)</c>: returns a metadata
/// value for a collation. Real SQL Server projects this as <c>sql_variant</c>
/// carrying a per-property inner base type; the simulator doesn't model
/// sql_variant, so it surfaces the bare true type instead — <c>CodePage</c> /
/// <c>LCID</c> / <c>ComparisonStyle</c> / <c>Version</c> as
/// <see cref="SqlType.Int32"/>, <c>Name</c> as <see cref="SqlType.NVarchar"/>.
/// When the property-name argument is a compile-time constant the true type
/// flows to the projection schema; otherwise the type falls back to
/// <see cref="SqlType.NVarchar"/> and the runtime value is coerced to match
/// (the static/runtime parity contract). An unrecognized collation name or an
/// unknown property returns NULL (matches real-server convention). Property
/// names are case-insensitive; the underlying values derive from the
/// simulator's collation model, so any recognized collation resolves.
/// </summary>
internal sealed class CollationProperty : Expression
{
    private static readonly FrozenDictionary<string, (SqlType Type, Func<Collation.CollationMetrics, SqlValue> Produce)> Properties = new Dictionary<string, (SqlType Type, Func<Collation.CollationMetrics, SqlValue> Produce)>
    {
        ["CodePage"] = (SqlType.Int32, m => SqlValue.FromInt32(m.CodePage)),
        ["LCID"] = (SqlType.Int32, m => SqlValue.FromInt32(m.Lcid)),
        ["ComparisonStyle"] = (SqlType.Int32, m => SqlValue.FromInt32(m.ComparisonStyle)),
        ["Version"] = (SqlType.Int32, m => SqlValue.FromInt32(m.Version)),
        ["Name"] = (SqlType.NVarchar, m => SqlValue.FromNVarchar(m.Name)),
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
        var staticType = StaticType(this.propertyArg);
        var collationValue = this.collationArg.Run(runtime);
        var propertyValue = this.propertyArg.Run(runtime);
        if (collationValue.IsNull || propertyValue.IsNull)
            return SqlValue.Null(staticType);
        if (!Properties.TryGetValue(propertyValue.CoerceTo(SqlType.NVarchar).AsString, out var def)
            || !Collation.TryGetMetrics(collationValue.CoerceTo(SqlType.NVarchar).AsString, out var metrics))
        {
            return SqlValue.Null(staticType);
        }
        var value = def.Produce(metrics);
        // A non-constant property argument couldn't resolve a true type at
        // parse time (GetSqlType fell back to NVarchar); coerce so runtime
        // agrees.
        return this.propertyArg is Value ? value : value.CoerceTo(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => StaticType(this.propertyArg);

    private static SqlType StaticType(Expression propertyArg)
        => propertyArg is Value { Constant: { IsNull: false } constant }
            && Properties.TryGetValue(constant.CoerceTo(SqlType.NVarchar).AsString, out var def)
            ? def.Type
            : SqlType.NVarchar;

    internal override string DebugDisplay() => $"COLLATIONPROPERTY({this.collationArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
