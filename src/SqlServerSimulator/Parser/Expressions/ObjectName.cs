using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>OBJECT_NAME(object_id [, database_id])</c>: returns the leaf
/// name of the object with the given <c>object_id</c>, or NULL when not
/// found. Walks <see cref="Database.Schemas"/> → <see cref="Schema.SchemaObjects"/>
/// (the shared object-name namespace: tables / views / functions /
/// procedures / sequences / triggers) plus <see cref="Schema.TableTypes"/>,
/// then the constraint objects hanging off a table (see
/// <see cref="ConstraintLookup"/>).
/// Probe-confirmed against SQL Server 2025 (2026-05-23): the
/// <c>database_id</c> argument is load-bearing — without it the lookup is
/// scoped to the connection's current database; with it the lookup is
/// scoped to the named database (NULL / unknown / out-of-range id →
/// NULL). NULL <c>object_id</c> returns NULL; missing / negative id
/// returns NULL; result type is <see cref="SqlType.SystemName"/>.
/// Database-id allocation matches <see cref="DbId"/>'s
/// alphabetical-position scheme.
/// </summary>
internal sealed class ObjectName : Expression
{
    private readonly Expression idArg;
    private readonly Expression? dbIdArg;

    public ObjectName(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            this.dbIdArg = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Tokens.Operator { Character: ',' })
                throw SimulatedSqlException.FunctionRequiresNArguments("object_name", 2);
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

        Database? targetDb;
        if (this.dbIdArg is null)
        {
            targetDb = runtime.Batch.CurrentDatabase;
        }
        else
        {
            var dbIdValue = this.dbIdArg.Run(runtime);
            if (dbIdValue.IsNull)
                return SqlValue.Null(SqlType.SystemName);
            var dbIdInt = ScalarArguments.CoerceToInt(dbIdValue);
            targetDb = LookupDatabaseById(runtime.Batch.Connection.Simulation, dbIdInt);
            if (targetDb is null)
                return SqlValue.Null(SqlType.SystemName);
        }

        // A restricted principal gets NULL for an id it can't view metadata for
        // (probe-confirmed), matching sys.objects hiding. Scoped to the current
        // database — the session principal is a current-DB user, so a cross-DB
        // lookup passes through unfiltered.
        var restrict = PermissionEnforcement.MetadataVisibilityApplies(runtime.Batch)
            && ReferenceEquals(targetDb, runtime.Batch.CurrentDatabase);
        var principalId = runtime.Batch.Connection.Security.Effective.DatabasePrincipalId;
        foreach (var schema in targetDb.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.ObjectId != id)
                    continue;
                var (governObjectId, governSchemaId) = obj is Trigger trigger
                    ? (trigger.Parent.ObjectId, trigger.Parent.SchemaId)
                    : (obj.ObjectId, obj.SchemaId);
                return restrict && !PermissionChecker.CanViewMetadata(targetDb, principalId, governObjectId, governSchemaId)
                    ? SqlValue.Null(SqlType.SystemName)
                    : SqlValue.FromString(SqlType.SystemName, obj.Name);
            }
            foreach (var tableType in schema.TableTypes.Values)
            {
                if (tableType.ObjectId == id)
                    return SqlValue.FromString(SqlType.SystemName, tableType.Name);
            }
        }
        // A constraint id reads back its own name, visibility following the
        // table it hangs off (constraints aren't SchemaObjects, so the walk
        // above can't reach one).
        return ConstraintLookup.TryResolveById(targetDb, id, out var constraint)
            && (!restrict || PermissionChecker.CanViewMetadata(targetDb, principalId, constraint.Table.ObjectId, constraint.Table.SchemaId))
            ? SqlValue.FromString(SqlType.SystemName, constraint.Name)
            : SqlValue.Null(SqlType.SystemName);
    }

    private static Database? LookupDatabaseById(Simulation simulation, int requested)
    {
        foreach (var (db, id) in DbId.DatabasesWithIds(simulation))
        {
            if (id == requested)
                return db;
        }
        return null;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() =>
        this.dbIdArg is null
            ? $"OBJECT_NAME({this.idArg.DebugDisplay()})"
            : $"OBJECT_NAME({this.idArg.DebugDisplay()}, {this.dbIdArg.DebugDisplay()})";
}
