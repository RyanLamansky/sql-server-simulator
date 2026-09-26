using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The INFORMATION_SCHEMA views real defines as plain queries over sys.* —
// CHECK_CONSTRAINTS, the two VIEW_*_USAGE views over sys.sql_dependencies,
// SEQUENCES and COLUMN_DOMAIN_USAGE — each following real's own definition
// (read from OBJECT_DEFINITION and probed 2026-09-26 against SQL Server 2025).
internal static partial class BuiltInResources
{
    private static void RegisterInformationSchemaUsage(Dictionary<string, CatalogView> views)
    {
        void Iso(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["INFORMATION_SCHEMA." + name] = new CatalogView(name, columns, rows);

        Iso("CHECK_CONSTRAINTS",
        [
            new("CONSTRAINT_CATALOG", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_SCHEMA", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_NAME", SqlType.SystemName, 128, false),
            new("CHECK_CLAUSE", SqlType.NVarchar, 4000, true),
        ], EnumerateInformationSchemaCheckConstraints);

        Iso("VIEW_TABLE_USAGE",
        [
            new("VIEW_CATALOG", nvarchar128Baseline, 128, true),
            new("VIEW_SCHEMA", nvarchar128Baseline, 128, true),
            new("VIEW_NAME", SqlType.SystemName, 128, false),
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
        ], (batch, database) => EnumerateInformationSchemaViewUsage(batch, database, columns: false));

        Iso("VIEW_COLUMN_USAGE",
        [
            new("VIEW_CATALOG", nvarchar128Baseline, 128, true),
            new("VIEW_SCHEMA", nvarchar128Baseline, 128, true),
            new("VIEW_NAME", SqlType.SystemName, 128, false),
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("COLUMN_NAME", SqlType.SystemName, 128, true),
        ], (batch, database) => EnumerateInformationSchemaViewUsage(batch, database, columns: true));

        Iso("SEQUENCES",
        [
            new("SEQUENCE_CATALOG", nvarchar128Baseline, 128, true),
            new("SEQUENCE_SCHEMA", nvarchar128Baseline, 128, true),
            new("SEQUENCE_NAME", SqlType.SystemName, 128, false),
            new("DATA_TYPE", nvarchar128Baseline, 128, false),
            new("NUMERIC_PRECISION", SqlType.TinyInt, null, false),
            new("NUMERIC_PRECISION_RADIX", SqlType.SmallInt, null, true),
            new("NUMERIC_SCALE", SqlType.Int32, null, true),
            new("START_VALUE", SqlType.SqlVariant, null, false),
            new("MINIMUM_VALUE", SqlType.SqlVariant, null, false),
            new("MAXIMUM_VALUE", SqlType.SqlVariant, null, false),
            new("INCREMENT", SqlType.SqlVariant, null, false),
            new("CYCLE_OPTION", SqlType.Bit, null, true),
            new("DECLARED_DATA_TYPE", SqlType.SystemName, 128, false),
            new("DECLARED_NUMERIC_PRECISION", SqlType.TinyInt, null, false),
            new("DECLARED_NUMERIC_SCALE", SqlType.TinyInt, null, false),
        ], EnumerateInformationSchemaSequences);

        Iso("COLUMN_DOMAIN_USAGE",
        [
            new("DOMAIN_CATALOG", nvarchar128Baseline, 128, true),
            new("DOMAIN_SCHEMA", nvarchar128Baseline, 128, true),
            new("DOMAIN_NAME", SqlType.SystemName, 128, false),
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("COLUMN_NAME", SqlType.SystemName, 128, true),
        ], EnumerateInformationSchemaColumnDomainUsage);
    }

    /// <summary>One row per CHECK constraint, its clause the stored definition.</summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaCheckConstraints(Parser.BatchContext batch, Database database)
    {
        var catalog = SqlValue.FromNVarchar(database.Name);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromNVarchar(schema.Name);
            foreach (var table in CatalogTables(schema, batch).OrderBy(t => t.ObjectId))
            {
                foreach (var check in table.CheckConstraints)
                {
                    yield return
                    [
                        catalog,
                        schemaName,
                        SqlValue.FromSystemName(check.Name),
                        check.Definition is { } definition ? RoutineDefinition(definition) : SqlValue.Null(SqlType.NVarchar),
                    ];
                }
            }
        }
    }

    /// <summary>
    /// The objects (or, for <paramref name="columns"/>, the columns) a view
    /// reads, from <c>sys.sql_dependencies</c>: the table form is distinct per
    /// object, the column form one row per referenced column.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaViewUsage(Parser.BatchContext batch, Database database, bool columns)
    {
        _ = batch;
        var catalog = SqlValue.FromNVarchar(database.Name);
        var schemaNames = new Dictionary<int, string>();
        var views = new Dictionary<int, View>();
        foreach (var schema in database.Schemas.Values)
        {
            schemaNames[schema.SchemaId] = schema.Name;
            foreach (var view in schema.Views.Values)
                views[view.ObjectId] = view;
        }

        var seen = new HashSet<(int, int)>();
        foreach (var (entity, _, referencedId, minorId, _, _, _, referenced) in EnumerateLegacyDependencies(database))
        {
            if (!views.TryGetValue(entity.ReferencingId, out var view))
                continue;
            string? columnName = null;
            if (columns)
            {
                if (minorId == 0 || ColumnNameById(referenced, minorId) is not { } name)
                    continue;
                columnName = name;
            }
            else if (!seen.Add((view.ObjectId, referencedId)))
            {
                continue;
            }

            SqlValue[] row =
            [
                catalog,
                SqlValue.FromNVarchar(schemaNames.GetValueOrDefault(view.SchemaId, Database.DefaultSchemaName)),
                SqlValue.FromSystemName(view.Name),
                catalog,
                SqlValue.FromNVarchar(schemaNames.GetValueOrDefault(referenced.SchemaId, Database.DefaultSchemaName)),
                SqlValue.FromSystemName(referenced.Name),
            ];
            yield return columnName is null ? row : [.. row, SqlValue.FromSystemName(columnName)];
        }
    }

    private static string? ColumnNameById(SchemaObject referenced, int columnId) => referenced switch
    {
        HeapTable table => Array.Find(table.Columns, c => c.ColumnId == columnId)?.Name,
        View view when columnId <= view.OutputColumns.Length => view.OutputColumns[columnId - 1].Name,
        _ => null,
    };

    /// <summary>
    /// One row per sequence: DATA_TYPE and the numeric facets from the base
    /// type, the four bounds as <c>sql_variant</c>s, and DECLARED_* from the
    /// declared type's own <c>sys.types</c> row — so a decimal sequence's
    /// declared precision and scale are that row's 38 / 38.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaSequences(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromNVarchar(database.Name);
        var radix10 = SqlValue.FromInt16(10);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromNVarchar(schema.Name);
            foreach (var sequence in schema.Sequences.Values.OrderBy(s => s.ObjectId))
            {
                var type = sequence.DeclaredType;
                var (precision, scale) = SequencePrecisionScale(type);
                var typeName = SqlValue.FromNVarchar(type.SqlServerName);
                var (declaredPrecision, declaredScale) = type is DecimalSqlType ? ((byte)38, (byte)38) : (precision, (byte)0);
                yield return
                [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(sequence.Name),
                    typeName,
                    SqlValue.FromByte(precision),
                    radix10,
                    SqlValue.FromInt32(scale),
                    sequence.AsDeclaredVariant(sequence.StartValue),
                    sequence.AsDeclaredVariant(sequence.MinValue),
                    sequence.AsDeclaredVariant(sequence.MaxValue),
                    sequence.AsDeclaredVariant(sequence.Increment),
                    SqlValue.FromBoolean(sequence.Cycle),
                    SqlValue.FromSystemName(type.SqlServerName),
                    SqlValue.FromByte(declaredPrecision),
                    SqlValue.FromByte(declaredScale),
                ];
            }
        }
    }

    /// <summary>
    /// One row per column declared with an alias type, across the objects
    /// <c>sys.columns</c> lists: tables, views, table-valued functions and
    /// table types.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaColumnDomainUsage(Parser.BatchContext batch, Database database)
    {
        var catalog = SqlValue.FromNVarchar(database.Name);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromNVarchar(schema.Name);
            IEnumerable<(string Name, HeapColumn[] Columns)> hosts =
            [
                .. CatalogTables(schema, batch).OrderBy(t => t.ObjectId).Select(t => (t.Name, t.Columns)),
                .. schema.Functions.Values.OrderBy(f => f.ObjectId).Select(f => (f.Name, f switch
                {
                    InlineTableValuedFunction inline => inline.OutputColumns,
                    MultiStatementTableValuedFunction multiStatement => multiStatement.OutputColumns,
                    _ => [],
                })),
                .. schema.Views.Values.OrderBy(v => v.ObjectId).Select(v => (v.Name, v.OutputColumns)),
                .. schema.TableTypes.Values.OrderBy(t => t.ObjectId).Select(t => (t.BackingTableName, t.Columns)),
            ];
            foreach (var (hostName, columns) in hosts)
            {
                foreach (var column in columns)
                {
                    if (column.AliasType is not { } alias)
                        continue;
                    yield return
                    [
                        catalog,
                        SqlValue.FromNVarchar(alias.Schema.Name),
                        SqlValue.FromSystemName(alias.Name),
                        catalog,
                        schemaName,
                        SqlValue.FromSystemName(hostName),
                        SqlValue.FromSystemName(column.Name),
                    ];
                }
            }
        }
    }
}
