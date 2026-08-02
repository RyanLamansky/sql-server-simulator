using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sys.sql_expression_dependencies plus the two views it replaced —
// sys.sql_dependencies and the SQL-Server-2000-shaped sysdepends — all
// projections of ModuleDependencies. The two dm_sql_referen*_entities DMVs
// read the same analysis but are parameterized rowset functions rather than
// views, so they live in Parser/Selection.SqlDependencies.cs.
internal static partial class BuiltInResources
{
    private static void RegisterDependencies(Dictionary<string, CatalogView> views)
    {
        RegisterExpressionDependencies(views);
        RegisterSqlDependencies(views);
        RegisterSysdepends(views);
    }

    private static void RegisterExpressionDependencies(Dictionary<string, CatalogView> views) =>
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

    /// <summary>
    /// Registers <c>sys.sql_dependencies</c>, the per-column dependency store
    /// <c>sys.sql_expression_dependencies</c> replaced. Only the <c>sys.</c>
    /// qualifier resolves it (probe-confirmed: a bare
    /// <c>SELECT … FROM sql_dependencies</c> is Msg 208), unlike its own
    /// predecessor <c>sysdepends</c>.
    /// </summary>
    private static void RegisterSqlDependencies(Dictionary<string, CatalogView> views) =>
        views["sys.sql_dependencies"] = new CatalogView("sql_dependencies",
        [
            new("class", SqlType.TinyInt, null, false),
            new("class_desc", nvarchar60Catalog, 60, true),
            new("object_id", SqlType.Int32, null, false),
            new("column_id", SqlType.Int32, null, false),
            new("referenced_major_id", SqlType.Int32, null, false),
            new("referenced_minor_id", SqlType.Int32, null, false),
            new("is_selected", SqlType.Bit, null, false),
            new("is_updated", SqlType.Bit, null, false),
            new("is_select_all", SqlType.Bit, null, false),
        ], (batch, database) => EnumerateSqlDependencies(database));

    /// <summary>
    /// Registers <c>sysdepends</c>, the SQL-Server-2000-shaped view of the same
    /// rows. Like <c>sysobjects</c> it resolves both unqualified and under the
    /// <c>sys.</c> qualifier (probe-confirmed).
    /// </summary>
    private static void RegisterSysdepends(Dictionary<string, CatalogView> views)
    {
        HeapColumn[] columns =
        [
            new("id", SqlType.Int32, null, false),
            new("depid", SqlType.Int32, null, false),
            new("number", SqlType.SmallInt, null, true),
            new("depnumber", SqlType.SmallInt, null, true),
            new("status", SqlType.SmallInt, null, true),
            new("deptype", SqlType.TinyInt, null, false),
            new("depdbid", SqlType.SmallInt, null, true),
            new("depsiteid", SqlType.SmallInt, null, true),
            new("selall", SqlType.Bit, null, false),
            new("resultobj", SqlType.Bit, null, false),
            new("readobj", SqlType.Bit, null, false),
        ];
        var view = new CatalogView("sysdepends", columns, (batch, database) => EnumerateSysdepends(database));
        views["sysdepends"] = view;
        views["sys.sysdepends"] = view;
    }

    private static IEnumerable<SqlValue[]> EnumerateSqlDependencies(Database database)
    {
        var nonSchemaBoundClass = SqlValue.FromByte(0);
        var schemaBoundClass = SqlValue.FromByte(1);
        var nonSchemaBoundDesc = SqlValue.FromNVarchar(nvarchar60Catalog, "OBJECT_OR_COLUMN_REFERENCE_NON_SCHEMA_BOUND");
        var schemaBoundDesc = SqlValue.FromNVarchar(nvarchar60Catalog, "OBJECT_OR_COLUMN_REFERENCE_SCHEMA_BOUND");

        foreach (var (entity, reference, referencedId, minorId, selected, updated, selectAll) in EnumerateLegacyDependencies(database))
        {
            var isSchemaBound = reference.IsSchemaBound;
            yield return
            [
                isSchemaBound ? schemaBoundClass : nonSchemaBoundClass,
                isSchemaBound ? schemaBoundDesc : nonSchemaBoundDesc,
                SqlValue.FromInt32(entity.ReferencingId),
                SqlValue.FromInt32(entity.ReferencingMinorId),
                SqlValue.FromInt32(referencedId),
                SqlValue.FromInt32(minorId),
                SqlValue.FromBoolean(selected),
                SqlValue.FromBoolean(updated),
                SqlValue.FromBoolean(selectAll),
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysdepends(Database database)
    {
        var zero = SqlValue.FromInt16(0);
        foreach (var (entity, reference, referencedId, minorId, selected, updated, selectAll) in EnumerateLegacyDependencies(database))
        {
            // status packs the same three flags the trailing bit columns carry
            // one apiece, probe-confirmed: 2 = selall, 4 = resultobj, 8 =
            // readobj (so a read of a whole object reports 8, a SELECT * column
            // 2, an UPDATE's SET column 4).
            var status = (selectAll ? 2 : 0) + (updated ? 4 : 0) + (selected ? 8 : 0);
            yield return
            [
                SqlValue.FromInt32(entity.ReferencingId),
                SqlValue.FromInt32(referencedId),
                // number is the referencing entity's minor id — a computed
                // column's own column_id — except on a procedure, where it is
                // the procedure group number a numbered `CREATE PROC p;n`
                // would set and 1 stands for the single ungrouped body
                // (probe-confirmed both ways).
                SqlValue.FromInt16((short)(string.Equals(entity.ObjectTypeCode, "P ", StringComparison.Ordinal)
                    ? 1
                    : entity.ReferencingMinorId)),
                SqlValue.FromInt16((short)minorId),
                SqlValue.FromInt16((short)status),
                SqlValue.FromByte(reference.IsSchemaBound ? (byte)1 : (byte)0),
                // depdbid / depsiteid addressed a cross-database or replicated
                // dependency the legacy store never populated; probed 0 on
                // every row of a live database.
                zero,
                zero,
                SqlValue.FromBoolean(selectAll),
                SqlValue.FromBoolean(updated),
                SqlValue.FromBoolean(selected),
            ];
        }
    }

    /// <summary>
    /// The row set both legacy views project, one tuple per (referencing
    /// entity, referenced object, referenced column) triple.
    /// <para>
    /// The legacy store holds <em>ids</em> where
    /// <c>sys.sql_expression_dependencies</c> holds names, so a reference whose
    /// id the analysis can't produce — a dropped object, another database or
    /// server, a caller-dependent <c>EXEC</c> — contributes nothing, and
    /// neither does a table-type parameter, whose class is outside a domain
    /// that is object-or-column only (probe-confirmed: a procedure taking a
    /// TVP is absent from both views).
    /// </para>
    /// <para>
    /// The <c>referenced_minor_id = 0</c> row is <em>narrower</em> than
    /// <c>sys.sql_expression_dependencies</c>': real records the object itself
    /// only where the reference doesn't land on one of its columns — a
    /// whole-object read or write (<c>SELECT 1 FROM t</c>, <c>DELETE</c>, an
    /// <c>INSERT</c> carrying no column list), an <c>EXEC</c> or a function
    /// call — plus every schema-bound reference, which binds the object as well
    /// as its columns. A plain <c>SELECT a FROM t</c> reports column <c>a</c>
    /// and nothing else.
    /// </para>
    /// </summary>
    private static IEnumerable<(ModuleDependencies.Entity Entity, ModuleDependencies.Reference Reference, int ReferencedId, int MinorId, bool Selected, bool Updated, bool SelectAll)>
        EnumerateLegacyDependencies(Database database)
    {
        foreach (var entity in ModuleDependencies.Enumerate(database))
        {
            foreach (var reference in entity.References)
            {
                // The id is the one the reference actually binds to, not the
                // narrower one sys.sql_expression_dependencies reports: the
                // legacy store settled a one-part EXEC name at create time, so
                // it names the procedure where the modern view answers NULL and
                // is_caller_dependent (probe-confirmed against a procedure
                // calling `EXEC sfx_p`).
                if (reference.ReferencedClass != ModuleDependencies.ObjectOrColumnClass
                    || reference.DatabaseName is not null || reference.ServerName is not null
                    || ModuleDependencies.ResolveForStoredId(database, reference) is not { } resolvedObject)
                {
                    continue;
                }

                List<(int ColumnId, ModuleDependencies.ColumnUse Use)> columnRows = [];
                if (ModuleDependencies.ColumnsOf(reference.Resolved) is { } columns)
                {
                    // Column rows follow the referenced object's own column
                    // order, which is the order real reports them in.
                    foreach (var column in columns)
                    {
                        var use = reference.Columns.Find(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));
                        if (use is null)
                            continue;
                        var columnId = ModuleDependencies.ColumnIdOf(reference.Resolved, column.Name);
                        if (columnId != 0)
                            columnRows.Add((columnId, use));
                    }
                }

                var referencedId = resolvedObject.ObjectId;
                if (reference.HasObjectReference && (columnRows.Count == 0 || reference.IsSchemaBound))
                    yield return (entity, reference, referencedId, 0, reference.IsSelected, reference.IsUpdated, reference.IsSelectAll);
                foreach (var (columnId, use) in columnRows)
                    yield return (entity, reference, referencedId, columnId, use.Selected, use.Updated, use.SelectAll);
            }
        }
    }
}
