using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SCHEMA_NAME([id])</c>: returns the name of the schema with the
/// given <c>schema_id</c>, or with no argument the effective principal's
/// default schema — the caller's inside a module body too, and NULL when its
/// <c>DEFAULT_SCHEMA</c> names no schema (probed 2026-10-04 against SQL Server
/// 2025). A non-existent or negative id returns NULL; a NULL argument returns
/// NULL. Result type is <see cref="Expression.MetadataNameType"/> (<c>nvarchar(128)</c> /
/// nvarchar(128)) — mirrors <see cref="SchemaId"/>'s int-result inverse.
/// </summary>
internal sealed class SchemaName : Expression
{
    private readonly Expression? idArg;

    public SchemaName(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.idArg is null)
        {
            return runtime.Batch.DefaultSchema is { } defaultSchema
                ? SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), defaultSchema.Name)
                : SqlValue.Null(MetadataNameType(runtime.Batch));
        }
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(MetadataNameType(runtime.Batch));
        var id = ScalarArguments.CoerceToInt(idValue);
        foreach (var (_, schema) in runtime.Batch.CurrentDatabase.Schemas)
        {
            if (schema.SchemaId == id)
                return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), schema.Name);
        }
        return SqlValue.Null(MetadataNameType(runtime.Batch));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.idArg is not null)
            _ = AssignmentRules.ArgumentType(this.idArg, SqlType.Int32, batch, resolveColumnType);
        return MetadataNameType(batch);
    }

    internal override string DebugDisplay() =>
        this.idArg is null ? "SCHEMA_NAME()" : $"SCHEMA_NAME({this.idArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.idArg);
}
