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
        SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName,
        SqlType.Int32, SqlType.Int32, SqlType.SystemName,
        SqlType.TinyInt, NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit),
        SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit,
        SqlType.Bit, SqlType.Bit, SqlType.Bit,
    ];

    private static readonly string[] ReferencedEntitiesColumnNames =
    [
        "referenced_server_name", "referenced_database_name", "referenced_schema_name", "referenced_entity_name",
        "referenced_id", "referenced_minor_id", "referenced_minor_name",
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
    /// against the table).
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
    /// <c>sys.sql_expression_dependencies</c> details.
    /// </summary>
    /// <remarks>
    /// A reference the analysis can't resolve marks its rows
    /// <c>is_incomplete</c> and makes the DMV raise <strong>Msg 2020</strong>
    /// after handing the rows back — probe-confirmed ordering: the rows arrive,
    /// then the error. A name that isn't a module (a table) or doesn't exist
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
        if (ResolveDependencyDmvTarget(nameExpr, classExpr, batch, outerResolver, database) is not { } target)
            yield break;

        var objectClassDesc = SqlValue.FromNVarchar(
            (NVarcharSqlType)ReferencingEntitiesSchema[4], "OBJECT_OR_COLUMN");
        var ddlTriggerClassDesc = SqlValue.FromNVarchar(
            (NVarcharSqlType)ReferencingEntitiesSchema[4], "DATABASE_DDL_TRIGGER");
        // A table type is addressed by its user_type_id, which is the id a
        // TYPE-class reference carries; every other securable by its object id.
        var targetId = target is TableType tableTypeTarget ? tableTypeTarget.UserTypeId : target.ObjectId;
        foreach (var entity in ModuleDependencies.Enumerate(database))
        {
            if (entity.ReferencingId == target.ObjectId)
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
            if (!references)
                continue;

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
        if (ResolveDependencyDmvTarget(nameExpr, classExpr, batch, outerResolver, database) is not { } target
            || ModuleDependencies.ForObject(database, target) is not { } entity)
        {
            yield break;
        }

        var classType = (NVarcharSqlType)ReferencedEntitiesSchema[8];
        var objectClassDesc = SqlValue.FromNVarchar(classType, "OBJECT_OR_COLUMN");
        var typeClassDesc = SqlValue.FromNVarchar(classType, "TYPE");
        var nullName = SqlValue.Null(SqlType.SystemName);
        var incomplete = false;

        foreach (var reference in entity.References)
        {
            var isType = reference.ReferencedClass == ModuleDependencies.TypeClass;
            var resolved = reference.ReferencedId is not null;
            incomplete |= !resolved;
            var columns = ModuleDependencies.ColumnsOf(reference.Resolved);

            SqlValue[] Row(int minorId, string? minorName, bool selected, bool updated, bool selectAll) =>
            [
                reference.ServerName is { } server ? SqlValue.FromSystemName(server) : nullName,
                reference.DatabaseName is { } db ? SqlValue.FromSystemName(db) : nullName,
                reference.SchemaName is { } schema ? SqlValue.FromSystemName(schema) : nullName,
                SqlValue.FromSystemName(reference.EntityName),
                resolved ? SqlValue.FromInt32(reference.ReferencedId!.Value) : SqlValue.Null(SqlType.Int32),
                SqlValue.FromInt32(minorId),
                minorName is null ? nullName : SqlValue.FromSystemName(minorName),
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

            yield return RowEncoder.EncodeRow(ReferencedEntitiesSchema,
                Row(0, null, reference.IsSelected, reference.IsUpdated, reference.IsSelectAll));

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
            throw SimulatedSqlException.DependencyReportMayBeIncomplete($"{entity.SchemaName}.{entity.EntityName}");
    }

    /// <summary>
    /// Resolves the <c>(name, class)</c> pair to the object both DMVs report
    /// on, or null when the pair names nothing they can answer for. Every miss
    /// is silent by design: real returns an empty rowset for an unrecognized
    /// class string, a NULL name, a one-part name, and a name no object
    /// carries.
    /// </summary>
    private static SchemaObject? ResolveDependencyDmvTarget(
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
            ? schema.TableTypes.TryGetValue(leaf, out var tableType) ? tableType : null
            : schema.HeapTables.TryGetValue(leaf, out var table) ? table
            : schema.Views.TryGetValue(leaf, out var view) ? view
            : schema.Functions.TryGetValue(leaf, out var function) ? function
            : schema.Procedures.TryGetValue(leaf, out var procedure) ? procedure
            : schema.Triggers.TryGetValue(leaf, out var trigger) ? trigger
            : schema.Synonyms.TryGetValue(leaf, out var synonym) ? synonym
            : schema.Sequences.TryGetValue(leaf, out var sequence) ? sequence
            : null;
    }
}
