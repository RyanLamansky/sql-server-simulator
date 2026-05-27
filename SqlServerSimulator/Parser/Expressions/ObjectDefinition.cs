using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>OBJECT_DEFINITION(object_id)</c>: returns the source-text definition
/// of the programmable module (stored procedure, view, DML / DDL trigger,
/// scalar / inline / multi-statement function) with the given
/// <c>object_id</c>, scoped to the connection's current database. Returns NULL
/// for a NULL / missing / non-module id, and for modules created
/// <c>WITH ENCRYPTION</c> (whose <see cref="Schemas.SchemaObject.DefinitionText"/>
/// is null). The stored text is the original <c>CREATE</c> statement verbatim,
/// with the leading verb normalized to <c>CREATE</c> for <c>ALTER</c> /
/// <c>CREATE OR ALTER</c> — see <see cref="Simulation.BuildModuleDefinition"/>.
/// Unlike <see cref="ObjectName"/>, there is no <c>database_id</c> argument
/// (probe-confirmed against SQL Server 2025: <c>OBJECT_DEFINITION</c> takes a
/// single argument). Result type is <see cref="SqlType.NVarchar"/>
/// (<c>nvarchar(max)</c>), mirroring the JSON scalars.
/// </summary>
internal sealed class ObjectDefinition : Expression
{
    private readonly Expression idArg;

    public ObjectDefinition(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.NVarchar);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;

        var database = runtime.Batch.CurrentDatabase;
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.ObjectId == id)
                    return Definition(obj.DefinitionText);
            }
        }
        foreach (var ddlTrigger in database.DdlTriggers.Values)
        {
            if (ddlTrigger.ObjectId == id)
                return Definition(ddlTrigger.DefinitionText);
        }
        return SqlValue.Null(SqlType.NVarchar);
    }

    private static SqlValue Definition(string? text) =>
        text is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(text);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"OBJECT_DEFINITION({this.idArg.DebugDisplay()})";
}
