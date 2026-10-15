using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The usage views over user-defined types, XML schema collections and
// assemblies, and the securable class list.
partial class BuiltInResources
{
    /// <summary>
    /// Registers <c>sys.column_type_usages</c>, <c>sys.parameter_type_usages</c>,
    /// <c>sys.column_xml_schema_collection_usages</c>,
    /// <c>sys.parameter_xml_schema_collection_usages</c>,
    /// <c>sys.module_assembly_usages</c>, <c>sys.assembly_references</c> and
    /// <c>sys.securable_classes</c>. Each usage view is a filter of the view
    /// its rows come from, so it sees what that view shows: every column a
    /// user-defined type or a typed <c>xml</c> declares — of a table, a view,
    /// a function's result and a table type alike — and every parameter, a
    /// function's return value as parameter 0 (probed 2026-10-15 against SQL
    /// Server 2025).
    /// </summary>
    private static void RegisterUsageViews(Dictionary<string, CatalogView> views)
    {
        HeapColumn[] typeUsage(string member) =>
        [
            new("object_id", SqlType.Int32, null, false),
            new(member, SqlType.Int32, null, false),
            new("user_type_id", SqlType.Int32, null, false),
        ];
        HeapColumn[] collectionUsage(string member) =>
        [
            new("object_id", SqlType.Int32, null, false),
            new(member, SqlType.Int32, null, false),
            new("xml_collection_id", SqlType.Int32, null, false),
        ];

        Usage(views, "column_type_usages", typeUsage("column_id"), "sys.columns", "column_id", "user_type_id", static id => id > 256);
        Usage(views, "parameter_type_usages", typeUsage("parameter_id"), "sys.parameters", "parameter_id", "user_type_id", static id => id > 256);
        Usage(views, "column_xml_schema_collection_usages", collectionUsage("column_id"), "sys.columns", "column_id", "xml_collection_id", static id => id > 0);
        Usage(views, "parameter_xml_schema_collection_usages", collectionUsage("parameter_id"), "sys.parameters", "parameter_id", "xml_collection_id", static id => id > 0);

        views["sys.module_assembly_usages"] = new CatalogView("module_assembly_usages",
        [
            new("object_id", SqlType.Int32, null, false),
            new("assembly_id", SqlType.Int32, null, false),
        ], (batch, database) => Project(batch, database, "sys.assembly_modules", static source =>
        {
            var objectId = OrdinalOf(source, "object_id");
            var assemblyId = OrdinalOf(source, "assembly_id");
            return row => [row[objectId], row[assemblyId]];
        }));

        // An assembly referencing another registered one is refused (Msg
        // 6503), so no reference is ever recorded.
        views["sys.assembly_references"] = new CatalogView("assembly_references",
        [
            new("assembly_id", SqlType.Int32, null, false),
            new("referenced_assembly_id", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);

        var classDesc = NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit);
        var classRows = Array.ConvertAll(SecurableClasses, securable => new[]
        {
            SqlValue.FromString(classDesc, securable.Description),
            SqlValue.FromInt32(securable.Class),
        });
        views["sys.securable_classes"] = new CatalogView("securable_classes",
        [
            new("class_desc", classDesc, 60, true),
            new("class", SqlType.Int32, null, true),
        ], (_, _) => classRows);
    }

    private static void Usage(Dictionary<string, CatalogView> views, string name, HeapColumn[] columns, string sourceKey, string memberColumn, string usageColumn, Func<int, bool> uses) =>
        views["sys." + name] = new CatalogView(name, columns, (batch, database) => UsageRows(batch, database, sourceKey, memberColumn, usageColumn, uses));

    private static IEnumerable<SqlValue[]> UsageRows(BatchContext batch, Database database, string sourceKey, string memberColumn, string usageColumn, Func<int, bool> uses)
    {
        var rows = SourceRows(batch, database, sourceKey, out var source);
        var objectId = OrdinalOf(source, "object_id");
        var member = OrdinalOf(source, memberColumn);
        var usage = OrdinalOf(source, usageColumn);
        foreach (var row in rows)
        {
            if (!row[usage].IsNull && uses(row[usage].AsInt32))
                yield return [row[objectId], row[member], row[usage]];
        }
    }

    /// <summary><c>sys.securable_classes</c>' rows, copied from SQL Server 2025 (probed 2026-10-15).</summary>
    private static readonly (string Description, int Class)[] SecurableClasses =
    [
        ("DATABASE", 0),
        ("OBJECT", 1),
        ("SCHEMA", 3),
        ("USER", 4),
        ("ROLE", 4),
        ("APPLICATION ROLE", 4),
        ("ASSEMBLY", 5),
        ("TYPE", 6),
        ("XML SCHEMA COLLECTION", 10),
        ("MESSAGE TYPE", 15),
        ("CONTRACT", 16),
        ("SERVICE", 17),
        ("REMOTE SERVICE BINDING", 18),
        ("ROUTE", 19),
        ("FULLTEXT CATALOG", 23),
        ("SYMMETRIC KEY", 24),
        ("CERTIFICATE", 25),
        ("ASYMMETRIC KEY", 26),
        ("FULLTEXT STOPLIST", 29),
        ("SEARCH PROPERTY LIST", 31),
        ("DATABASE SCOPED CREDENTIAL", 32),
        ("EXTERNAL LANGUAGE", 34),
        ("SERVER", 100),
        ("SERVER ROLE", 101),
        ("LOGIN", 101),
        ("ENDPOINT", 105),
        ("AVAILABILITY GROUP", 108),
        ("CREDENTIAL", 109),
        ("EXTERNAL MODEL", 110),
    ];
}
