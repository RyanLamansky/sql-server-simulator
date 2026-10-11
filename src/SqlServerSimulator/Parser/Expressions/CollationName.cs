using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COLLATIONNAME(tds_collation [, flags])</c>, undocumented: the name
/// of the collation whose <c>COLLATIONPROPERTY(…, 'TDSCollation')</c> bytes
/// the <c>varbinary</c> argument holds — SQL Server's OLE DB provider asks it
/// of a server it links to — or <c>NULL</c> for bytes no collation has, a
/// length other than five among them. Collations sharing their bytes (one
/// with <c>_SC</c> and one without, a <c>_VSS</c> pair, the EBCDIC sort
/// variants) answer with the one of smallest collation id, and any second
/// argument makes the answer <c>NULL</c> once it converts to <c>int</c>
/// (probed 2026-10-11 against SQL Server 2025, all 5540 collations).
/// </summary>
internal sealed class CollationName : Expression
{
    private readonly Expression operand;
    private readonly Expression? flags;

    public CollationName(ParserContext context)
    {
        this.operand = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            context.MoveNextRequired();
            this.flags = Parse(context);
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var type = MetadataNameType(runtime.Batch);
        var value = this.operand.Run(runtime);
        if (this.flags is not null)
        {
            var flagValue = this.flags.Run(runtime);
            if (!flagValue.IsNull)
                _ = flagValue.CoerceTo(SqlType.Int32);
            return SqlValue.Null(type);
        }
        return !value.IsNull && Network.TdsCollationCodec.NameForPropertyBytes(value.CoerceTo(SqlType.VarbinaryMax).AsBytes) is { } name
            ? SqlValue.FromNVarchar(type, name)
            : SqlValue.Null(type);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = AssignmentRules.ArgumentType(this.operand, SqlType.Varbinary, batch, resolveColumnType);
        if (this.flags is not null)
            _ = AssignmentRules.ArgumentType(this.flags, SqlType.Int32, batch, resolveColumnType);
        return MetadataNameType(batch);
    }

    internal override string DebugDisplay() =>
        this.flags is null ? $"COLLATIONNAME({this.operand.DebugDisplay()})" : $"COLLATIONNAME({this.operand.DebugDisplay()}, {this.flags.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.operand).Child(this.flags);
}
