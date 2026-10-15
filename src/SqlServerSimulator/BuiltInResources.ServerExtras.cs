using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Smaller server-scope views: the valid USE HINT names, tempdb's space DMVs,
// memory grants, and the security and backup-device views whose rows come from
// state the simulator keeps or never fills. Shapes and object ids probed
// 2026-10-15 against SQL Server 2025.
internal static partial class BuiltInResources
{
    private static void RegisterServerExtras(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        var hintNameType = NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);
        var hintRows = Array.ConvertAll(Parser.Selection.ValidUseHintsInCatalogOrder, name => new[] { SqlValue.FromNVarchar(hintNameType, name) });
        Sys("dm_exec_valid_use_hints", [new("name", hintNameType, 128, false)], (_, _) => hintRows);

        // Every session holds a row in tempdb's space accounting, as does its
        // one task; the simulator allocates nothing there for a session, so
        // the counts are zero.
        Sys("dm_db_session_space_usage",
        [
            new("session_id", SqlType.SmallInt, null, true),
            new("database_id", SqlType.Int32, null, true),
            new("user_objects_alloc_page_count", SqlType.BigInt, null, true),
            new("user_objects_dealloc_page_count", SqlType.BigInt, null, true),
            new("internal_objects_alloc_page_count", SqlType.BigInt, null, true),
            new("internal_objects_dealloc_page_count", SqlType.BigInt, null, true),
            new("user_objects_deferred_dealloc_page_count", SqlType.BigInt, null, true),
        ], static (batch, _) => SessionSpaceRows(batch, task: false));
        Sys("dm_db_task_space_usage",
        [
            new("task_address", SqlType.Varbinary, 8, true),
            new("is_remote_task", SqlType.Bit, null, false),
            new("session_id", SqlType.SmallInt, null, true),
            new("request_id", SqlType.Int32, null, true),
            new("exec_context_id", SqlType.Int32, null, true),
            new("database_id", SqlType.Int32, null, true),
            new("user_objects_alloc_page_count", SqlType.BigInt, null, true),
            new("user_objects_dealloc_page_count", SqlType.BigInt, null, true),
            new("internal_objects_alloc_page_count", SqlType.BigInt, null, true),
            new("internal_objects_dealloc_page_count", SqlType.BigInt, null, true),
        ], static (batch, _) => SessionSpaceRows(batch, task: true));

        // A query is granted memory while it runs a memory-consuming operator,
        // which no query the simulator runs reports, so the view is empty.
        Sys("dm_exec_query_memory_grants",
        [
            new("session_id", SqlType.SmallInt, null, true),
            new("request_id", SqlType.Int32, null, true),
            new("scheduler_id", SqlType.Int32, null, true),
            new("dop", SqlType.SmallInt, null, true),
            new("request_time", SqlType.DateTime, null, true),
            new("grant_time", SqlType.DateTime, null, true),
            new("requested_memory_kb", SqlType.BigInt, null, true),
            new("granted_memory_kb", SqlType.BigInt, null, true),
            new("required_memory_kb", SqlType.BigInt, null, true),
            new("used_memory_kb", SqlType.BigInt, null, true),
            new("max_used_memory_kb", SqlType.BigInt, null, true),
            new("query_cost", SqlType.Float, null, true),
            new("timeout_sec", SqlType.Int32, null, true),
            new("resource_semaphore_id", SqlType.SmallInt, null, true),
            new("queue_id", SqlType.SmallInt, null, true),
            new("wait_order", SqlType.Int32, null, true),
            new("is_next_candidate", SqlType.Bit, null, true),
            new("wait_time_ms", SqlType.BigInt, null, true),
            new("plan_handle", SqlType.Varbinary, 64, true),
            new("sql_handle", SqlType.Varbinary, 64, true),
            new("group_id", SqlType.Int32, null, true),
            new("pool_id", SqlType.Int32, null, true),
            new("is_small", SqlType.Bit, null, true),
            new("ideal_memory_kb", SqlType.BigInt, null, true),
            new("reserved_worker_count", SqlType.Int32, null, true),
            new("used_worker_count", SqlType.Int32, null, true),
            new("max_used_worker_count", SqlType.Int32, null, true),
            new("reserved_node_bitmap", SqlType.BigInt, null, true),
            new("query_hash", SqlType.GetBinary(8), 8, true),
            new("query_plan_hash", SqlType.GetBinary(8), 8, true),
        ], static (_, _) => EmptyCatalogRows);

        // The logins mapped to a credential (CREATE / ALTER LOGIN … CREDENTIAL).
        Sys("server_principal_credentials",
        [
            new("principal_id", SqlType.Int32, null, false),
            new("credential_id", SqlType.Int32, null, false),
        ], static (batch, _) => batch.Connection.Simulation.LoginCredentials.ToArray().OrderBy(static entry => entry.Key)
            .Select(static entry => new[] { SqlValue.FromInt32(entry.Key), SqlValue.FromInt32(entry.Value) }));

        // Filled by sp_control_dbmasterkey_password, which isn't built.
        Sys("master_key_passwords",
        [
            new("credential_id", SqlType.Int32, null, false),
            new("family_guid", SqlType.UniqueIdentifier, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // Filled by sp_addumpdevice, which isn't built.
        Sys("backup_devices",
        [
            new("name", SqlType.SystemName, 128, false),
            new("type", SqlType.TinyInt, null, true),
            new("type_desc", NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit), 60, true),
            new("physical_name", SqlType.NVarchar, 260, true),
        ], static (_, _) => EmptyCatalogRows);

        // Extended procedures implemented in a DLL, of which a Linux server has
        // none: its xp_ procedures list in sys.all_objects but not here.
        Sys("extended_procedures",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), 2, true),
            new("type_desc", NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit), 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("is_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
            new("dll_name", SqlType.NVarchar, 260, true),
        ], static (_, _) => EmptyCatalogRows);

        // Server-scope CLR modules: CLR server triggers, which aren't built.
        var binary128 = NVarcharSqlType.Get(128, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);
        Sys("server_assembly_modules",
        [
            new("object_id", SqlType.Int32, null, false),
            new("assembly_id", SqlType.Int32, null, false),
            new("assembly_class", binary128, 128, true),
            new("assembly_method", binary128, 128, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // The words of user-defined full-text stoplists, which aren't built.
        Sys("fulltext_stopwords",
        [
            new("stoplist_id", SqlType.Int32, null, false),
            new("stopword", NVarcharSqlType.Get(64, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit), 64, false),
            new("language", SqlType.NVarchar, 128, false),
            new("language_id", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);
    }

    /// <summary>
    /// One row per session — or per session's task — accounting its tempdb
    /// pages, all zero: real keeps a row for every session, its system
    /// sessions included, and a task row for each session's one task (probed
    /// 2026-10-15 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SqlValue[]> SessionSpaceRows(Parser.BatchContext batch, bool task)
    {
        var zero = SqlValue.FromInt64(0);
        var tempdb = SqlValue.FromInt32(2);
        foreach (var connection in batch.Connection.Simulation.SnapshotConnections())
        {
            var session = SqlValue.FromInt16((short)connection.Spid);
            if (task)
            {
                var address = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(address, 0x0000_0200_0000_0000 + (connection.Spid * 0x1000L));
                yield return [SqlValue.FromVarbinary(address), SqlValue.FromBoolean(false), session, SqlValue.FromInt32(0), SqlValue.FromInt32(0), tempdb, zero, zero, zero, zero];
            }
            else
            {
                yield return [session, tempdb, zero, zero, zero, zero, zero];
            }
        }
    }
}
