using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The Extended Events views: the engine's catalog of packages, objects,
// object columns and map values, read from SQL Server 2025's own rows; see
// docs/claude/extended-events.md.
internal static partial class BuiltInResources
{
    private static void RegisterExtendedEvents(Dictionary<string, CatalogView> views)
    {
        var collation = Collation.Get("SQL_Latin1_General_CP1_CI_AS");
        NVarcharSqlType Text(int length) => NVarcharSqlType.Get(length, collation, Coercibility.Implicit);
        var name = Text(256);
        var description = Text(3072);
        var objectType = Text(60);
        var capabilitiesDesc = Text(256);
        var moduleGuid = Text(60);

        HeapColumn[] packages =
        [
            new("name", name, 256, false),
            new("guid", SqlType.UniqueIdentifier, null, false),
            new("description", description, 3072, false),
            new("capabilities", SqlType.Int32, null, true),
            new("capabilities_desc", capabilitiesDesc, 256, true),
            new("module_guid", moduleGuid, 60, false),
            new("module_address", SqlType.Varbinary, 8, false),
        ];
        views["sys.dm_xe_packages"] = new CatalogView("dm_xe_packages", packages, (_, _) => XeMetadata.Instance.Packages.Select(package => new[]
        {
            Value(name, package.Name),
            SqlValue.FromGuid(package.Guid),
            Value(description, package.Description),
            OptionalInt(package.Capabilities),
            Value(capabilitiesDesc, package.CapabilitiesDesc),
            Value(moduleGuid, package.ModuleGuid),
            SqlValue.FromVarbinary(package.ModuleAddress),
        }));

        HeapColumn[] objects =
        [
            new("name", name, 256, false),
            new("object_type", objectType, 60, false),
            new("package_guid", SqlType.UniqueIdentifier, null, false),
            new("description", description, 3072, false),
            new("capabilities", SqlType.Int32, null, true),
            new("capabilities_desc", capabilitiesDesc, 256, true),
            new("type_name", name, 256, true),
            new("type_package_guid", SqlType.UniqueIdentifier, null, true),
            new("type_size", SqlType.Int32, null, true),
        ];
        views["sys.dm_xe_objects"] = new CatalogView("dm_xe_objects", objects, (_, _) => XeMetadata.Instance.Objects.Select(item => new[]
        {
            Value(name, item.Name),
            Value(objectType, item.ObjectType),
            SqlValue.FromGuid(item.Package.Guid),
            Value(description, item.Description),
            OptionalInt(item.Capabilities),
            Value(capabilitiesDesc, item.CapabilitiesDesc),
            Value(name, item.TypeName),
            OptionalGuid(item.TypePackage),
            OptionalInt(item.TypeSize),
        }));

        HeapColumn[] objectColumns =
        [
            new("name", name, 256, false),
            new("column_id", SqlType.Int32, null, false),
            new("object_name", name, 256, false),
            new("object_package_guid", SqlType.UniqueIdentifier, null, false),
            new("type_name", name, 256, false),
            new("type_package_guid", SqlType.UniqueIdentifier, null, false),
            new("column_type", objectType, 60, false),
            new("column_value", name, 256, true),
            new("capabilities", SqlType.Int32, null, true),
            new("capabilities_desc", capabilitiesDesc, 256, true),
            new("description", description, 3072, true),
        ];
        views["sys.dm_xe_object_columns"] = new CatalogView("dm_xe_object_columns", objectColumns, (_, _) => XeMetadata.Instance.Columns.Select(column => new[]
        {
            Value(name, column.Name),
            SqlValue.FromInt32(column.ColumnId),
            Value(name, column.Owner.Name),
            SqlValue.FromGuid(column.Owner.Package.Guid),
            Value(name, column.TypeName),
            OptionalGuid(column.TypePackage),
            Value(objectType, column.ColumnType),
            Value(name, column.ColumnValue),
            OptionalInt(column.Capabilities),
            Value(capabilitiesDesc, column.CapabilitiesDesc),
            Value(description, column.Description),
        }));

        HeapColumn[] mapValues =
        [
            new("name", name, 256, false),
            new("object_package_guid", SqlType.UniqueIdentifier, null, false),
            new("map_key", SqlType.Int32, null, false),
            new("map_value", description, 3072, false),
        ];
        views["sys.dm_xe_map_values"] = new CatalogView("dm_xe_map_values", mapValues, (_, _) => XeMetadata.Instance.MapValues.Select(value => new[]
        {
            Value(name, value.Map.Name),
            SqlValue.FromGuid(value.Map.Package.Guid),
            SqlValue.FromInt32(value.Key),
            Value(description, value.Value),
        }));

        static SqlValue Value(NVarcharSqlType type, string? text) => text is null ? SqlValue.Null(type) : SqlValue.FromNVarchar(type, text);

        static SqlValue OptionalInt(int? value) => value is { } number ? SqlValue.FromInt32(number) : SqlValue.Null(SqlType.Int32);

        static SqlValue OptionalGuid(XePackage? package) => package is null ? SqlValue.Null(SqlType.UniqueIdentifier) : SqlValue.FromGuid(package.Guid);
    }
}
