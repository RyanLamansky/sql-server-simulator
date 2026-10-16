using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The undocumented <c>ODBCSCALE(system_type_id, scale)</c> SqlClient's
/// <c>GetSchema("StructuredTypeMembers")</c> query calls: the scale back as an
/// <c>int</c> for a type that has one — the integers, <c>decimal</c> /
/// <c>numeric</c>, the money pair and the date and time types — and NULL for
/// any other. Both arguments convert to <c>tinyint</c> first, so a value out
/// of its range is Msg 220 and a non-numeric string Msg 245 (probed
/// 2026-10-16 against SQL Server 2025).
/// </summary>
internal sealed class OdbcScale : Expression
{
    private readonly Expression typeId;
    private readonly Expression scale;

    public OdbcScale(ParserContext context)
    {
        this.typeId = Parse(context);
        context.MoveNextRequired();
        this.scale = Parse(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var typeValue = this.typeId.Run(runtime);
        var scaleValue = this.scale.Run(runtime);
        if (typeValue.IsNull || scaleValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var type = ScalarArguments.CoerceToTinyInt(typeValue);
        var written = ScalarArguments.CoerceToTinyInt(scaleValue);
        return type is 40 or 41 or 42 or 43 or 48 or 52 or 56 or 58 or 60 or 61 or 106 or 108 or 122 or 127
            ? SqlValue.FromInt32(written)
            : SqlValue.Null(SqlType.Int32);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = this.typeId.GetSqlType(batch, resolveColumnType);
        _ = this.scale.GetSqlType(batch, resolveColumnType);
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => $"ODBCSCALE({this.typeId.DebugDisplay()}, {this.scale.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.typeId).Child(this.scale);
}
