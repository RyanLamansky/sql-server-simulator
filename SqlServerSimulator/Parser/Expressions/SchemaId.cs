using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SCHEMA_ID([name])</c>: returns the int schema id of the named
/// schema (matching <c>sys.schemas.schema_id</c>), or the caller's default
/// schema id when called with no argument. The simulator has no user model,
/// so the no-arg form always returns <see cref="Database.DboSchemaId"/>
/// (=1) — matching real SQL Server's <c>dbo</c>-default behavior for the
/// user we ship with. Probe-confirmed against SQL Server 2025 (2026-05-11)
/// that built-in schemas land at conventional ids: <c>dbo=1</c>,
/// <c>INFORMATION_SCHEMA=3</c>, <c>sys=4</c>; user schemas start at 5.
/// NULL name or unknown name → NULL.
/// </summary>
internal sealed class SchemaId : Expression
{
    private readonly Expression? nameArg;

    public SchemaId(ParserContext context)
    {
        // Zero-arg form: cursor is on ')' (the function call's close paren) —
        // ResolveBuiltIn's dispatch hands us the open-paren-advanced position.
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.nameArg is null)
            return SqlValue.FromInt32(Database.DboSchemaId);
        var nameValue = this.nameArg.Run(runtime);
        if (nameValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var schemaName = nameValue.CoerceTo(SqlType.NVarchar).AsString;
        return runtime.Batch.CurrentDatabase.Schemas.TryGetValue(schemaName, out var schema)
            ? SqlValue.FromInt32(schema.SchemaId)
            : SqlValue.Null(SqlType.Int32);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        this.nameArg is null ? "SCHEMA_ID()" : $"SCHEMA_ID({this.nameArg.DebugDisplay()})";
}
