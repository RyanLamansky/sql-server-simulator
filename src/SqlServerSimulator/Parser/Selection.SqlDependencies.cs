using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    private static readonly SqlType[] ReferencingEntitiesSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.Int32,
        SqlType.TinyInt, NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit), SqlType.Bit,
    ];

    private static readonly string[] ReferencingEntitiesColumnNames =
    [
        "referencing_schema_name", "referencing_entity_name", "referencing_id",
        "referencing_class", "referencing_class_desc", "is_caller_dependent",
    ];

    private static readonly SqlType[] ReferencedEntitiesSchema =
    [
        SqlType.Int32,
        SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName,
        SqlType.SystemName, SqlType.Int32, SqlType.Int32,
        SqlType.TinyInt, NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit),
        SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit,
        SqlType.Bit, SqlType.Bit, SqlType.Bit,
    ];

    private static readonly string[] ReferencedEntitiesColumnNames =
    [
        "referencing_minor_id",
        "referenced_server_name", "referenced_database_name", "referenced_schema_name", "referenced_entity_name",
        "referenced_minor_name", "referenced_id", "referenced_minor_id",
        "referenced_class", "referenced_class_desc",
        "is_caller_dependent", "is_ambiguous", "is_selected", "is_updated", "is_select_all",
        "is_all_columns_found", "is_insert_all", "is_incomplete",
    ];

    /// <summary>
    /// Built-in system TVF <c>sys.dm_sql_referencing_entities(name, class)</c> —
    /// every entity in the current database whose definition names
    /// <paramref name="functionName"/>'s first argument, one row each,
    /// <em>directly</em> (real reports no transitive closure — probe-confirmed
    /// that a procedure calling a procedure that reads a table isn't listed
    /// against the table), in schema-then-name order. A table whose computed
    /// columns read its own columns lists itself (probed 2026-09-30 against
    /// SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// Both arguments are required and both are read as strings. A class string
    /// the DMV doesn't recognize, a name that resolves to nothing, and a NULL
    /// name all yield <em>zero rows</em> rather than an error (probe-confirmed
    /// against SQL Server 2025, 2026-08-02) — as does a one-part name, which
    /// real refuses to bind without a schema qualifier.
    /// </remarks>
    public static Selection ParseSqlReferencingEntities(ParserContext context, string functionName)
    {
        var (nameArg, classArg) = ParseDependencyDmvArguments(context, functionName);
        return new Selection(ReferencingEntitiesSchema, ReferencingEntitiesColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateReferencingEntities(nameArg, classArg, batch, outerResolver));
    }

    /// <summary>
    /// Built-in system TVF <c>sys.dm_sql_referenced_entities(name, class)</c> —
    /// what the named entity itself references: one row per referenced object
    /// plus one per referenced column, for <em>every</em> referencing kind
    /// rather than only the schema-bound ones
    /// <c>sys.sql_expression_dependencies</c> details. A table answers for its
    /// computed columns, each row carrying the column's id as
    /// <c>referencing_minor_id</c>, and a CHECK or DEFAULT constraint for its
    /// expression; a column of the entity's own table is a column row with no
    /// object row beside it (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// A reference the analysis can't resolve marks its rows
    /// <c>is_incomplete</c> and makes the DMV raise <strong>Msg 2020</strong>
    /// after handing the rows back — probe-confirmed ordering: the rows arrive,
    /// then the error. A name that carries no expression or doesn't exist
    /// yields zero rows and no error.
    /// </remarks>
    public static Selection ParseSqlReferencedEntities(ParserContext context, string functionName)
    {
        var (nameArg, classArg) = ParseDependencyDmvArguments(context, functionName);
        return new Selection(ReferencedEntitiesSchema, ReferencedEntitiesColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateReferencedEntities(nameArg, classArg, batch, outerResolver));
    }

    /// <summary>Parses the shared <c>(name, class)</c> argument pair both dependency DMVs take.</summary>
    private static (Expression Name, Expression Class) ParseDependencyDmvArguments(ParserContext context, string functionName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var nameArg = Expression.Parse(context);

        if (context.Token is Operator { Character: ')' })
            throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var classArg = Expression.Parse(context);

        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.TooManyArgumentsToFunction(functionName);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return (nameArg, classArg);
    }

    private static IEnumerable<byte[]> EnumerateReferencingEntities(
        Expression nameExpr, Expression classExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var database = batch.CurrentDatabase;
        if (ResolveDependencyDmvTarget(nameExpr, classExpr, batch, outerResolver, database) is not { } targetId)
            yield break;

        var objectClassDesc = SqlValue.FromNVarchar(
            (NVarcharSqlType)ReferencingEntitiesSchema[4], "OBJECT_OR_COLUMN");
        var ddlTriggerClassDesc = SqlValue.FromNVarchar(
            (NVarcharSqlType)ReferencingEntitiesSchema[4], "DATABASE_DDL_TRIGGER");
        // One row per referencing object, though a table's computed columns are
        // an entity each; the one the first of them yields carries the flag the
        // rest would.
        var rows = new List<(ModuleDependencies.Entity Entity, bool CallerDependent)>();
        foreach (var entity in ModuleDependencies.Enumerate(database))
        {
            // A module naming itself isn't listed against itself; a table whose
            // computed columns read its own columns is.
            if (entity.ReferencingId == targetId && entity.ReferencingMinorId == 0)
                continue;
            var callerDependent = false;
            var references = false;
            foreach (var reference in entity.References)
            {
                if (reference.ReferencedId != targetId)
                    continue;
                references = true;
                callerDependent |= reference.IsCallerDependent;
            }
            if (references && !rows.Exists(row => row.Entity.ReferencingId == entity.ReferencingId))
                rows.Add((entity, callerDependent));
        }
        rows.Sort(static (a, b) => ModuleDependencies.CompareByName(a.Entity, b.Entity));

        foreach (var (entity, callerDependent) in rows)
        {
            var isDdlTrigger = entity.ReferencingClass == ModuleDependencies.DatabaseDdlTriggerClass;
            yield return RowEncoder.EncodeRow(ReferencingEntitiesSchema,
            [
                SqlValue.FromSystemName(entity.SchemaName),
                SqlValue.FromSystemName(entity.EntityName),
                SqlValue.FromInt32(entity.ReferencingId),
                SqlValue.FromByte(isDdlTrigger ? ModuleDependencies.DatabaseDdlTriggerClass : ModuleDependencies.ObjectOrColumnClass),
                isDdlTrigger ? ddlTriggerClassDesc : objectClassDesc,
                SqlValue.FromBoolean(callerDependent),
            ]);
        }
    }

    private static IEnumerable<byte[]> EnumerateReferencedEntities(
        Expression nameExpr, Expression classExpr, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var database = batch.CurrentDatabase;
        if (ResolveDependencyDmvTarget(nameExpr, classExpr, batch, outerResolver, database) is not { } targetId)
            yield break;
        // What an entity references is part of its definition, so a principal
        // that may not read the definition reads no rows (probed 2026-10-04
        // against SQL Server 2025).
        if (!batch.Connection.Security.EffectiveIsDbo
            && Expressions.ObjectProperty.FindObject(database, targetId) is { } target
            && !PermissionEnforcement.CanSeeDefinition(batch, database, target))
        {
            yield break;
        }
        var entities = ModuleDependencies.ForObject(database, targetId);
        if (entities.Count == 0)
            yield break;

        var classType = (NVarcharSqlType)ReferencedEntitiesSchema[9];
        var objectClassDesc = SqlValue.FromNVarchar(classType, "OBJECT_OR_COLUMN");
        var typeClassDesc = SqlValue.FromNVarchar(classType, "TYPE");
        var nullName = SqlValue.Null(SqlType.SystemName);
        var incomplete = false;

        var pairs = new List<(ModuleDependencies.Entity Entity, ModuleDependencies.Reference Reference)>();
        foreach (var entity in entities)
        {
            foreach (var reference in entity.References)
                pairs.Add((entity, reference));
        }

        foreach (var (entity, reference) in pairs)
        {
            var isType = reference.ReferencedClass == ModuleDependencies.TypeClass;
            var resolved = reference.ReferencedId is not null;
            incomplete |= !resolved;
            var columns = ModuleDependencies.ColumnsOf(reference.Resolved);

            SqlValue[] Row(int minorId, string? minorName, bool selected, bool updated, bool selectAll) =>
            [
                SqlValue.FromInt32(entity.ReferencingMinorId),
                reference.ServerName is { } server ? SqlValue.FromSystemName(server) : nullName,
                reference.DatabaseName is { } db ? SqlValue.FromSystemName(db) : nullName,
                reference.SchemaName is { } schema ? SqlValue.FromSystemName(schema) : nullName,
                SqlValue.FromSystemName(reference.EntityName),
                minorName is null ? nullName : SqlValue.FromSystemName(minorName),
                resolved ? SqlValue.FromInt32(reference.ReferencedId!.Value) : SqlValue.Null(SqlType.Int32),
                SqlValue.FromInt32(minorId),
                SqlValue.FromByte(isType ? ModuleDependencies.TypeClass : ModuleDependencies.ObjectOrColumnClass),
                isType ? typeClassDesc : objectClassDesc,
                SqlValue.FromBoolean(reference.IsCallerDependent),
                SqlValue.FromBoolean(reference.IsAmbiguous),
                SqlValue.FromBoolean(selected),
                SqlValue.FromBoolean(updated),
                SqlValue.FromBoolean(selectAll),
                SqlValue.FromBoolean(resolved),
                SqlValue.FromBoolean(reference.IsInsertAll),
                SqlValue.FromBoolean(!resolved),
            ];

            // A computed column, CHECK or DEFAULT reaches its own table's
            // columns without naming the table, so that reference has no
            // object row.
            if (reference.HasObjectReference)
            {
                yield return RowEncoder.EncodeRow(ReferencedEntitiesSchema,
                    Row(0, null, reference.IsSelected, reference.IsUpdated, reference.IsSelectAll));
            }

            if (columns is null)
                continue;
            // Column rows follow the referenced object's own column order, which
            // is the order real reports them in.
            foreach (var column in columns)
            {
                var use = reference.Columns.Find(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));
                if (use is null)
                    continue;
                var columnId = ModuleDependencies.ColumnIdOf(reference.Resolved, use.Name);
                if (columnId != 0)
                {
                    yield return RowEncoder.EncodeRow(ReferencedEntitiesSchema,
                        Row(columnId, use.Name, use.Selected, use.Updated, use.SelectAll));
                }
            }
        }

        // Real reports the rows it did find and then complains that the set may
        // be short of columns — the error follows the rowset rather than
        // replacing it.
        if (incomplete)
            throw SimulatedSqlException.DependencyReportMayBeIncomplete($"{entities[0].SchemaName}.{entities[0].EntityName}");
    }

    /// <summary>
    /// Resolves the <c>(name, class)</c> pair to the id of the object both DMVs
    /// report on — a table type's <c>user_type_id</c>, which is the id a
    /// TYPE-class reference carries, and any other securable's object id, a
    /// CHECK or DEFAULT constraint's included — or null when the pair names
    /// nothing they can answer for. Every miss is silent by design: real
    /// returns an empty rowset for an unrecognized class string, a NULL name, a
    /// one-part name, and a name no object carries.
    /// </summary>
    private static int? ResolveDependencyDmvTarget(
        Expression nameExpr, Expression classExpr, BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver, Database database)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var runtime = new RuntimeContext(resolver, batch);
        var nameValue = nameExpr.Run(runtime);
        var classValue = classExpr.Run(runtime);
        if (nameValue.IsNull || classValue.IsNull)
            return null;
        if (!BuiltInToken.Equals(classValue.AsString, "OBJECT")
            && !BuiltInToken.Equals(classValue.AsString, "TYPE")
            && !BuiltInToken.Equals(classValue.AsString, "XML_SCHEMA_COLLECTION")
            && !BuiltInToken.Equals(classValue.AsString, "PARTITION_FUNCTION"))
        {
            return null;
        }

        var parts = nameValue.AsString.Split('.');
        if (parts.Length is not (2 or 3) || !database.Schemas.TryGetValue(parts[^2].Trim('[', ']'), out var schema))
            return null;
        var leaf = parts[^1].Trim('[', ']');
        return BuiltInToken.Equals(classValue.AsString, "TYPE")
            ? schema.TableTypes.TryGetValue(leaf, out var tableType) ? tableType.UserTypeId : null
            : schema.HeapTables.TryGetValue(leaf, out var table) ? table.ObjectId
            : schema.Views.TryGetValue(leaf, out var view) ? view.ObjectId
            : schema.Functions.TryGetValue(leaf, out var function) ? function.ObjectId
            : schema.Procedures.TryGetValue(leaf, out var procedure) ? procedure.ObjectId
            : schema.Triggers.TryGetValue(leaf, out var trigger) ? trigger.ObjectId
            : schema.Synonyms.TryGetValue(leaf, out var synonym) ? synonym.ObjectId
            : schema.Sequences.TryGetValue(leaf, out var sequence) ? sequence.ObjectId
            : schema.SecurityPolicies.TryGetValue(leaf, out var policy) ? policy.ObjectId
            : ModuleDependencies.ExpressionConstraintId(database, schema, leaf);
    }
}
