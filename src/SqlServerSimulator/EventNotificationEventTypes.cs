using System.Collections.Frozen;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server's <c>sys.event_notification_event_types</c> catalog: every DDL
/// event and group <see cref="TriggerEventTypes"/> carries, plus the events
/// only an event notification takes — the two queue events and the SQL Trace
/// event classes with their <c>TRC_*</c> groups (probed 2026-10-16 against
/// SQL Server 2025, 432 rows).
/// </summary>
internal static class EventNotificationEventTypes
{
    /// <summary><c>BROKER_QUEUE_DISABLED</c>, which a queue's poison-message handling raises.</summary>
    internal const int BrokerQueueDisabled = 160;

    /// <summary><c>QUEUE_ACTIVATION</c>, which a queue holding messages no reader takes raises.</summary>
    internal const int QueueActivation = 173;

    /// <summary>The events and groups beyond <see cref="TriggerEventTypes.All"/>, in type order.</summary>
    private static readonly TriggerEventType[] NotificationOnly =
    [
        new(160, "BROKER_QUEUE_DISABLED", null),
        new(173, "QUEUE_ACTIVATION", null),
        new(1014, "AUDIT_LOGIN", 11008),
        new(1015, "AUDIT_LOGOUT", 11008),
        new(1020, "AUDIT_LOGIN_FAILED", 11008),
        new(1021, "EVENTLOG", 11003),
        new(1022, "ERRORLOG", 11003),
        new(1025, "LOCK_DEADLOCK", 11004),
        new(1033, "EXCEPTION", 11003),
        new(1034, "SP_CACHEMISS", 11011),
        new(1035, "SP_CACHEINSERT", 11011),
        new(1036, "SP_CACHEREMOVE", 11011),
        new(1037, "SP_RECOMPILE", 11011),
        new(1046, "OBJECT_CREATED", 11005),
        new(1047, "OBJECT_DELETED", 11005),
        new(1055, "HASH_WARNING", 11003),
        new(1059, "LOCK_DEADLOCK_CHAIN", 11004),
        new(1060, "LOCK_ESCALATION", 11004),
        new(1061, "OLEDB_ERRORS", 11015),
        new(1067, "EXECUTION_WARNINGS", 11003),
        new(1069, "SORT_WARNINGS", 11003),
        new(1079, "MISSING_COLUMN_STATISTICS", 11003),
        new(1080, "MISSING_JOIN_PREDICATE", 11003),
        new(1081, "SERVER_MEMORY_CHANGE", 11009),
        new(1082, "USERCONFIGURABLE_0", 11014),
        new(1083, "USERCONFIGURABLE_1", 11014),
        new(1084, "USERCONFIGURABLE_2", 11014),
        new(1085, "USERCONFIGURABLE_3", 11014),
        new(1086, "USERCONFIGURABLE_4", 11014),
        new(1087, "USERCONFIGURABLE_5", 11014),
        new(1088, "USERCONFIGURABLE_6", 11014),
        new(1089, "USERCONFIGURABLE_7", 11014),
        new(1090, "USERCONFIGURABLE_8", 11014),
        new(1091, "USERCONFIGURABLE_9", 11014),
        new(1092, "DATA_FILE_AUTO_GROW", 11002),
        new(1093, "LOG_FILE_AUTO_GROW", 11002),
        new(1094, "DATA_FILE_AUTO_SHRINK", 11002),
        new(1095, "LOG_FILE_AUTO_SHRINK", 11002),
        new(1102, "AUDIT_DATABASE_SCOPE_GDR_EVENT", 11008),
        new(1103, "AUDIT_SCHEMA_OBJECT_GDR_EVENT", 11008),
        new(1104, "AUDIT_ADDLOGIN_EVENT", 11008),
        new(1105, "AUDIT_LOGIN_GDR_EVENT", 11008),
        new(1106, "AUDIT_LOGIN_CHANGE_PROPERTY_EVENT", 11008),
        new(1107, "AUDIT_LOGIN_CHANGE_PASSWORD_EVENT", 11008),
        new(1108, "AUDIT_ADD_LOGIN_TO_SERVER_ROLE_EVENT", 11008),
        new(1109, "AUDIT_ADD_DB_USER_EVENT", 11008),
        new(1110, "AUDIT_ADD_MEMBER_TO_DB_ROLE_EVENT", 11008),
        new(1111, "AUDIT_ADD_ROLE_EVENT", 11008),
        new(1112, "AUDIT_APP_ROLE_CHANGE_PASSWORD_EVENT", 11008),
        new(1114, "AUDIT_SCHEMA_OBJECT_ACCESS_EVENT", 11008),
        new(1115, "AUDIT_BACKUP_RESTORE_EVENT", 11008),
        new(1116, "AUDIT_DBCC_EVENT", 11008),
        new(1117, "AUDIT_CHANGE_AUDIT_EVENT", 11008),
        new(1119, "OLEDB_CALL_EVENT", 11015),
        new(1120, "OLEDB_QUERYINTERFACE_EVENT", 11015),
        new(1121, "OLEDB_DATAREAD_EVENT", 11015),
        new(1122, "SHOWPLAN_XML", 11006),
        new(1125, "DEPRECATION_ANNOUNCEMENT", 11018),
        new(1126, "DEPRECATION_FINAL_SUPPORT", 11018),
        new(1127, "EXCHANGE_SPILL_EVENT", 11003),
        new(1128, "AUDIT_DATABASE_MANAGEMENT_EVENT", 11008),
        new(1129, "AUDIT_DATABASE_OBJECT_MANAGEMENT_EVENT", 11008),
        new(1130, "AUDIT_DATABASE_PRINCIPAL_MANAGEMENT_EVENT", 11008),
        new(1131, "AUDIT_SCHEMA_OBJECT_MANAGEMENT_EVENT", 11008),
        new(1132, "AUDIT_SERVER_PRINCIPAL_IMPERSONATION_EVENT", 11008),
        new(1133, "AUDIT_DATABASE_PRINCIPAL_IMPERSONATION_EVENT", 11008),
        new(1134, "AUDIT_SERVER_OBJECT_TAKE_OWNERSHIP_EVENT", 11008),
        new(1135, "AUDIT_DATABASE_OBJECT_TAKE_OWNERSHIP_EVENT", 11008),
        new(1137, "BLOCKED_PROCESS_REPORT", 11003),
        new(1146, "SHOWPLAN_XML_STATISTICS_PROFILE", 11006),
        new(1148, "DEADLOCK_GRAPH", 11004),
        new(1150, "TRACE_FILE_CLOSE", 11009),
        new(1152, "AUDIT_CHANGE_DATABASE_OWNER", 11008),
        new(1153, "AUDIT_SCHEMA_OBJECT_TAKE_OWNERSHIP_EVENT", 11008),
        new(1155, "FT_CRAWL_STARTED", 11017),
        new(1156, "FT_CRAWL_STOPPED", 11017),
        new(1157, "FT_CRAWL_ABORTED", 11017),
        new(1162, "USER_ERROR_MESSAGE", 11003),
        new(1164, "OBJECT_ALTERED", 11005),
        new(1166, "SQL_STMTRECOMPILE", 11013),
        new(1167, "DATABASE_MIRRORING_STATE_CHANGE", 11002),
        new(1168, "SHOWPLAN_XML_FOR_QUERY_COMPILE", 11006),
        new(1169, "SHOWPLAN_ALL_FOR_QUERY_COMPILE", 11006),
        new(1170, "AUDIT_SERVER_SCOPE_GDR_EVENT", 11008),
        new(1171, "AUDIT_SERVER_OBJECT_GDR_EVENT", 11008),
        new(1172, "AUDIT_DATABASE_OBJECT_GDR_EVENT", 11008),
        new(1173, "AUDIT_SERVER_OPERATION_EVENT", 11008),
        new(1175, "AUDIT_SERVER_ALTER_TRACE_EVENT", 11008),
        new(1176, "AUDIT_SERVER_OBJECT_MANAGEMENT_EVENT", 11008),
        new(1177, "AUDIT_SERVER_PRINCIPAL_MANAGEMENT_EVENT", 11008),
        new(1178, "AUDIT_DATABASE_OPERATION_EVENT", 11008),
        new(1180, "AUDIT_DATABASE_OBJECT_ACCESS_EVENT", 11008),
        new(1194, "OLEDB_PROVIDER_INFORMATION", 11015),
        new(1195, "MOUNT_TAPE", 11009),
        new(1196, "ASSEMBLY_LOAD", 11020),
        new(1198, "XQUERY_STATIC_TYPE", 11013),
        new(1199, "QN__SUBSCRIPTION", 11021),
        new(1200, "QN__PARAMETER_TABLE", 11021),
        new(1201, "QN__TEMPLATE", 11021),
        new(1202, "QN__DYNAMICS", 11021),
        new(1212, "BITMAP_WARNING", 11003),
        new(1213, "DATABASE_SUSPECT_DATA_PAGE", 11003),
        new(1214, "CPU_THRESHOLD_EXCEEDED", 11003),
        new(1235, "AUDIT_FULLTEXT", 11008),
        new(11000, "TRC_ALL_EVENTS", null),
        new(11002, "TRC_DATABASE", 11000),
        new(11003, "TRC_ERRORS_AND_WARNINGS", 11000),
        new(11004, "TRC_LOCKS", 11000),
        new(11005, "TRC_OBJECTS", 11000),
        new(11006, "TRC_PERFORMANCE", 11000),
        new(11008, "TRC_SECURITY_AUDIT", 11000),
        new(11009, "TRC_SERVER", 11000),
        new(11011, "TRC_STORED_PROCEDURES", 11000),
        new(11013, "TRC_TSQL", 11000),
        new(11014, "TRC_USER_CONFIGURABLE", 11000),
        new(11015, "TRC_OLEDB", 11000),
        new(11017, "TRC_FULL_TEXT", 11000),
        new(11018, "TRC_DEPRECATION", 11000),
        new(11020, "TRC_CLR", 11000),
        new(11021, "TRC_QUERY_NOTIFICATIONS", 11000),
    ];

    /// <summary>Every row of the catalog, in type order.</summary>
    internal static readonly TriggerEventType[] All = [.. TriggerEventTypes.All.Concat(NotificationOnly).OrderBy(static entry => entry.Type)];

    private static readonly FrozenDictionary<string, TriggerEventType> ByName =
        NotificationOnly.ToFrozenDictionary(static entry => entry.TypeName, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<int, TriggerEventType> ByType = All.ToFrozenDictionary(static entry => entry.Type);

    /// <summary>The leaf events of each <c>TRC_*</c> group, in type order.</summary>
    private static readonly FrozenDictionary<int, TriggerEventType[]> TraceClosures = BuildTraceClosures();

    private static FrozenDictionary<int, TriggerEventType[]> BuildTraceClosures()
    {
        var byType = NotificationOnly.ToDictionary(static entry => entry.Type);
        bool Reaches(TriggerEventType entry, int group)
        {
            for (var parent = entry.ParentType; parent is int type; parent = byType.TryGetValue(type, out var up) ? up.ParentType : null)
            {
                if (type == group)
                    return true;
            }
            return false;
        }
        var closures = new Dictionary<int, TriggerEventType[]>();
        foreach (var group in NotificationOnly)
        {
            if (group.Type >= TriggerEventTypes.GroupTypeThreshold)
                closures[group.Type] = [.. NotificationOnly.Where(entry => entry.Type < TriggerEventTypes.GroupTypeThreshold && Reaches(entry, group.Type))];
        }
        return closures.ToFrozenDictionary();
    }

    /// <summary>Resolves an event or group name, a DDL one or one only a notification takes.</summary>
    internal static bool TryResolve(string name, out TriggerEventType entry) =>
        TriggerEventTypes.TryResolve(name, out entry) || ByName.TryGetValue(name, out entry);

    /// <summary>Resolves a type code to its catalog row.</summary>
    internal static bool TryResolveType(int type, out TriggerEventType entry) => ByType.TryGetValue(type, out entry);

    /// <summary>Whether <paramref name="entry"/> is a SQL Trace event class or one of the <c>TRC_*</c> groups.</summary>
    internal static bool IsTrace(in TriggerEventType entry) => entry.Type is (>= 1000 and < TriggerEventTypes.GroupTypeThreshold) or >= 11000;

    /// <summary>Whether <paramref name="entry"/> is one of the two events a queue raises.</summary>
    internal static bool IsQueueEvent(in TriggerEventType entry) => entry.Type is BrokerQueueDisabled or QueueActivation;

    /// <summary>The leaf events <paramref name="entry"/> names: itself, or a group's closure.</summary>
    internal static TriggerEventType[] Leaves(in TriggerEventType entry) =>
        entry.Type < TriggerEventTypes.GroupTypeThreshold ? [entry]
        : TraceClosures.TryGetValue(entry.Type, out var trace) ? trace
        : TriggerEventTypes.LeafClosure(entry.Type);
}
