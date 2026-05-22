using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>OBJECT_SCHEMA_NAME(object_id [, database_id])</c>: returns the
/// name of the schema that owns the object with the given <c>object_id</c>,
/// or NULL when not found. The optional database-id argument is accepted
/// and ignored (single-database simulator). Companion to
/// <see cref="ObjectName"/> — same lookup walk over
/// <see cref="Database.Schemas"/> → <see cref="Schema.SchemaObjects"/>
/// plus <see cref="Schema.TableTypes"/>; this variant returns the owning
/// schema's <see cref="Schema.Name"/> instead of the object's leaf.
/// Probe-confirmed against SQL Server 2025 (2026-05-13).
/// </summary>
internal sealed class ObjectSchemaName : Expression
{
    private readonly Expression idArg;

    public ObjectSchemaName(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            // database_id arg: accepted and ignored (single-DB simulator).
            _ = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Tokens.Operator { Character: ',' })
                throw SimulatedSqlException.FunctionRequiresNArguments("object_schema_name", 2);
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var idValue = this.idArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;
        foreach (var schema in runtime.Batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.ObjectId == id)
                    return SqlValue.FromString(SqlType.SystemName, schema.Name);
            }
            foreach (var tableType in schema.TableTypes.Values)
            {
                if (tableType.ObjectId == id)
                    return SqlValue.FromString(SqlType.SystemName, schema.Name);
            }
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => $"OBJECT_SCHEMA_NAME({this.idArg.DebugDisplay()})";
}
