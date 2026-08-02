using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sys.sql_expression_dependencies — the catalog projection of
// ModuleDependencies. The two dm_sql_referen*_entities DMVs read the same
// analysis but are parameterized rowset functions rather than views, so they
// live in Parser/Selection.SqlDependencies.cs.
internal static partial class BuiltInResources
{
    private static void RegisterDependencies(Dictionary<string, CatalogView> views) =>
        views["sys.sql_expression_dependencies"] = new CatalogView("sql_expression_dependencies",
        [
            new("referencing_id", SqlType.Int32, null, false),
            new("referencing_minor_id", SqlType.Int32, null, false),
            new("referencing_class", SqlType.TinyInt, null, false),
            new("referencing_class_desc", nvarchar60Catalog, 60, true),
            new("is_schema_bound_reference", SqlType.Bit, null, false),
            new("referenced_class", SqlType.TinyInt, null, false),
            new("referenced_class_desc", nvarchar60Catalog, 60, true),
            new("referenced_server_name", SqlType.SystemName, 128, true),
            new("referenced_database_name", SqlType.SystemName, 128, true),
            new("referenced_schema_name", SqlType.SystemName, 128, true),
            new("referenced_entity_name", SqlType.SystemName, 128, false),
            new("referenced_id", SqlType.Int32, null, true),
            new("referenced_minor_id", SqlType.Int32, null, false),
            new("is_caller_dependent", SqlType.Bit, null, false),
            new("is_ambiguous", SqlType.Bit, null, false),
        ], (batch, database) => EnumerateExpressionDependencies(database));

    /// <summary>
    /// One row per (referencing entity, referenced entity) pair, plus — for a
    /// <em>schema-bound</em> reference only — one row per referenced column.
    /// Probe-confirmed asymmetry: a plain view over <c>dbo.t</c> reports a
    /// single <c>referenced_minor_id = 0</c> row while the same view
    /// <c>WITH SCHEMABINDING</c> reports that row plus one per bound column,
    /// and a computed column / CHECK / DEFAULT expression reports only its
    /// column rows, since it reaches its own table without naming it.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateExpressionDependencies(Database database)
    {
        var objectClass = SqlValue.FromByte(ModuleDependencies.ObjectOrColumnClass);
        var objectClassDesc = SqlValue.FromNVarchar(nvarchar60Catalog, "OBJECT_OR_COLUMN");
        var typeClass = SqlValue.FromByte(ModuleDependencies.TypeClass);
        var typeClassDesc = SqlValue.FromNVarchar(nvarchar60Catalog, "TYPE");
        var ddlTriggerClass = SqlValue.FromByte(ModuleDependencies.DatabaseDdlTriggerClass);
        var ddlTriggerClassDesc = SqlValue.FromNVarchar(nvarchar60Catalog, "DATABASE_DDL_TRIGGER");
        var minorZero = SqlValue.FromInt32(0);

        foreach (var entity in ModuleDependencies.Enumerate(database))
        {
            var referencingId = SqlValue.FromInt32(entity.ReferencingId);
            var referencingMinor = SqlValue.FromInt32(entity.ReferencingMinorId);
            var isDdlTrigger = entity.ReferencingClass == ModuleDependencies.DatabaseDdlTriggerClass;
            var referencingClass = isDdlTrigger ? ddlTriggerClass : objectClass;
            var referencingClassDesc = isDdlTrigger ? ddlTriggerClassDesc : objectClassDesc;

            foreach (var reference in entity.References)
            {
                var isType = reference.ReferencedClass == ModuleDependencies.TypeClass;
                var referencedId = reference.ReferencedId is { } id ? SqlValue.FromInt32(id) : SqlValue.Null(SqlType.Int32);
                SqlValue[] Row(SqlValue referencedMinor) =>
                [
                    referencingId,
                    referencingMinor,
                    referencingClass,
                    referencingClassDesc,
                    SqlValue.FromBoolean(reference.IsSchemaBound),
                    isType ? typeClass : objectClass,
                    isType ? typeClassDesc : objectClassDesc,
                    reference.ServerName is { } server ? SqlValue.FromSystemName(server) : SqlValue.Null(SqlType.SystemName),
                    reference.DatabaseName is { } db ? SqlValue.FromSystemName(db) : SqlValue.Null(SqlType.SystemName),
                    reference.SchemaName is { } schema ? SqlValue.FromSystemName(schema) : SqlValue.Null(SqlType.SystemName),
                    SqlValue.FromSystemName(reference.EntityName),
                    referencedId,
                    referencedMinor,
                    SqlValue.FromBoolean(reference.IsCallerDependent),
                    SqlValue.FromBoolean(reference.IsAmbiguous),
                ];

                if (reference.HasObjectReference)
                    yield return Row(minorZero);
                if (!reference.IsSchemaBound || ModuleDependencies.ColumnsOf(reference.Resolved) is not { } columns)
                    continue;
                foreach (var column in columns)
                {
                    if (!reference.Columns.Exists(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var columnId = ModuleDependencies.ColumnIdOf(reference.Resolved, column.Name);
                    if (columnId != 0)
                        yield return Row(SqlValue.FromInt32(columnId));
                }
            }
        }
    }
}
