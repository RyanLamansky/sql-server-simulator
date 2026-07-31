using SqlServerSimulator.Schemas;
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
        var id = ScalarArguments.CoerceToInt(idValue);
        // A restricted principal gets NULL for an id it can't view metadata for
        // (probe-confirmed), matching sys.objects hiding.
        var restrict = PermissionEnforcement.MetadataVisibilityApplies(runtime.Batch);
        var principalId = runtime.Batch.Connection.Security.Effective.DatabasePrincipalId;
        var database = runtime.Batch.CurrentDatabase;
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.ObjectId != id)
                    continue;
                var (governObjectId, governSchemaId) = obj is Trigger trigger
                    ? (trigger.Parent.ObjectId, trigger.Parent.SchemaId)
                    : (obj.ObjectId, obj.SchemaId);
                return restrict && !PermissionChecker.CanViewMetadata(database, principalId, governObjectId, governSchemaId)
                    ? SqlValue.Null(SqlType.SystemName)
                    : SqlValue.FromString(SqlType.SystemName, schema.Name);
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
