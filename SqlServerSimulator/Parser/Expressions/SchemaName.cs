using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SCHEMA_NAME([id])</c>: returns the name of the schema with the
/// given <c>schema_id</c>, or the caller's default schema name (<c>dbo</c>)
/// when called with no argument. Probe-confirmed against SQL Server 2025
/// (2026-05-13): no-arg returns <c>dbo</c> for the user the simulator
/// emulates; a non-existent or negative id returns NULL; a NULL argument
/// returns NULL. Result type is <see cref="SqlType.SystemName"/> (sysname /
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
            return SqlValue.FromString(SqlType.SystemName, Database.DefaultSchemaName);
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;
        foreach (var schema in runtime.Batch.CurrentDatabase.Schemas.Values)
        {
            if (schema.SchemaId == id)
                return SqlValue.FromString(SqlType.SystemName, schema.Name);
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() =>
        this.idArg is null ? "SCHEMA_NAME()" : $"SCHEMA_NAME({this.idArg.DebugDisplay()})";
}
