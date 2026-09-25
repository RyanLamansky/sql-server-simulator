using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SCHEMA_NAME([id])</c>: returns the name of the schema with the
/// given <c>schema_id</c>, or the caller's default schema name (<c>dbo</c>)
/// when called with no argument. Probe-confirmed against SQL Server 2025
/// (2026-05-13): no-arg returns <c>dbo</c> for the user the simulator
/// emulates; a non-existent or negative id returns NULL; a NULL argument
/// returns NULL. Result type is <see cref="Expression.MetadataNameType"/> (<c>nvarchar(128)</c> /
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
            return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), Database.DefaultSchemaName);
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(MetadataNameType(runtime.Batch));
        var id = ScalarArguments.CoerceToInt(idValue);
        foreach (var schema in runtime.Batch.CurrentDatabase.Schemas.Values)
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
