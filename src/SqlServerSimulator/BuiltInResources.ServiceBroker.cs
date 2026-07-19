using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    /// <summary>
    /// Registers the Service Broker catalog views (<c>sys.services</c> /
    /// <c>sys.service_queues</c> / <c>sys.service_contracts</c> /
    /// <c>sys.service_message_types</c> / <c>sys.routes</c> / …). Service Broker
    /// isn't modeled: no queues, services, contracts, message types, routes,
    /// conversation priorities, remote service bindings, or event notifications
    /// are ever created. Real SQL Server seeds a handful of system rows into
    /// several of these (a stock WWI reports service_queues = 3, services = 3,
    /// service_contracts = 6, service_message_types = 14, routes = 1) — all
    /// <c>is_ms_shipped</c> system objects a bacpac export never scripts — so
    /// every view ships <b>empty</b> with the full probe-confirmed shape
    /// (SQL Server 2025, 2026-07-16), the cheapest faithful option for DacFx's
    /// user-object reverse-engineering. See docs/claude/catalog-views.md.
    /// </summary>
    private static void RegisterServiceBroker(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns) =>
            views["sys." + name] = new CatalogView(name, columns, static (_, _) => EmptyCatalogRows);

        Sys("services",
        [
            new("name", SqlType.SystemName, 128, false),
            new("service_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("service_queue_id", SqlType.Int32, null, false),
        ]);
        Sys("service_queues",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("is_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
            new("max_readers", SqlType.SmallInt, null, true),
            new("activation_procedure", SqlType.NVarchar, 776, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
            new("is_activation_enabled", SqlType.Bit, null, false),
            new("is_receive_enabled", SqlType.Bit, null, false),
            new("is_enqueue_enabled", SqlType.Bit, null, false),
            new("is_retention_enabled", SqlType.Bit, null, false),
            new("is_poison_message_handling_enabled", SqlType.Bit, null, true),
        ]);
        Sys("service_contracts",
        [
            new("name", SqlType.SystemName, 128, false),
            new("service_contract_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
        ]);
        Sys("service_contract_usages",
        [
            new("service_id", SqlType.Int32, null, false),
            new("service_contract_id", SqlType.Int32, null, false),
        ]);
        Sys("service_contract_message_usages",
        [
            new("service_contract_id", SqlType.Int32, null, false),
            new("message_type_id", SqlType.Int32, null, false),
            new("is_sent_by_initiator", SqlType.Bit, null, false),
            new("is_sent_by_target", SqlType.Bit, null, false),
        ]);
        Sys("service_message_types",
        [
            new("name", SqlType.SystemName, 128, false),
            new("message_type_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("validation", charTwo, 2, false),
            new("validation_desc", nvarchar60Catalog, 60, true),
            new("xml_collection_id", SqlType.Int32, null, true),
        ]);
        Sys("routes",
        [
            new("name", SqlType.SystemName, 128, false),
            new("route_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("remote_service_name", SqlType.NVarchar, 256, true),
            new("broker_instance", SqlType.NVarchar, 128, true),
            new("lifetime", SqlType.DateTime, null, true),
            new("address", SqlType.NVarchar, 256, true),
            new("mirror_address", SqlType.NVarchar, 256, true),
        ]);
        Sys("conversation_priorities",
        [
            new("priority_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("service_contract_id", SqlType.Int32, null, true),
            new("local_service_id", SqlType.Int32, null, true),
            new("remote_service_name", SqlType.NVarchar, 256, true),
            new("priority", SqlType.TinyInt, null, false),
        ]);
        Sys("remote_service_bindings",
        [
            new("name", SqlType.SystemName, 128, false),
            new("remote_service_binding_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("remote_service_name", SqlType.NVarchar, 256, true),
            new("service_contract_id", SqlType.Int32, null, false),
            new("remote_principal_id", SqlType.Int32, null, true),
            new("is_anonymous_on", SqlType.Bit, null, false),
        ]);
        Sys("event_notifications",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("parent_class", SqlType.TinyInt, null, false),
            new("parent_class_desc", nvarchar60Catalog, 60, true),
            new("parent_id", SqlType.Int32, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("service_name", SqlType.NVarchar, 256, true),
            new("broker_instance", SqlType.NVarchar, 128, true),
            new("creator_sid", SqlType.Varbinary, 85, true),
            new("principal_id", SqlType.Int32, null, true),
        ]);
    }
}
