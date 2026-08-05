using System.Collections.Frozen;

namespace SqlServerSimulator;

/// <summary>
/// One row of SQL Server's static <c>sys.trigger_event_types</c> catalog: an
/// event type (or event <em>group</em>) and its parent group. Individual
/// events carry type codes below 10000 (<c>CREATE_TABLE</c> = 21); event
/// groups carry codes at or above 10001 (<c>DDL_DATABASE_LEVEL_EVENTS</c> =
/// 10016). <see cref="ParentType"/> is null only for the two roots
/// (<c>DDL_EVENTS</c> = 10001, <c>ALTER_SERVER_CONFIGURATION</c> = 296).
/// </summary>
internal readonly struct TriggerEventType(int type, string typeName, int? parentType)
{
    public readonly int Type = type;
    public readonly string TypeName = typeName;
    public readonly int? ParentType = parentType;
}

/// <summary>
/// SQL Server's version-stable <c>sys.trigger_event_types</c> catalog, hard-coded
/// from a live SQL Server 2025 reference (<c>SELECT type, type_name, parent_type
/// FROM sys.trigger_event_types</c>). Backs the <c>sys.trigger_event_types</c>
/// catalog view and the DDL-trigger expansion in
/// <c>BuiltInResources.EnumerateSysTriggerEvents</c>: a trigger created
/// <c>FOR &lt;group&gt;</c> (e.g. <c>DDL_DATABASE_LEVEL_EVENTS</c>) surfaces one
/// <c>sys.trigger_events</c> row per <em>leaf</em> event in the group's transitive
/// closure — 158 rows for <c>DDL_DATABASE_LEVEL_EVENTS</c>, matching real.
/// </summary>
internal static class TriggerEventTypes
{
    /// <summary>Type codes at or above this value are event groups; below are individual events.</summary>
    internal const int GroupTypeThreshold = 10000;

    internal static readonly TriggerEventType[] All =
    [
        new(21, "CREATE_TABLE", 10018),
        new(22, "ALTER_TABLE", 10018),
        new(23, "DROP_TABLE", 10018),
        new(24, "CREATE_INDEX", 10020),
        new(25, "ALTER_INDEX", 10020),
        new(26, "DROP_INDEX", 10020),
        new(27, "CREATE_STATISTICS", 10021),
        new(28, "UPDATE_STATISTICS", 10021),
        new(29, "DROP_STATISTICS", 10021),
        new(34, "CREATE_SYNONYM", 10022),
        new(36, "DROP_SYNONYM", 10022),
        new(41, "CREATE_VIEW", 10019),
        new(42, "ALTER_VIEW", 10019),
        new(43, "DROP_VIEW", 10019),
        new(51, "CREATE_PROCEDURE", 10024),
        new(52, "ALTER_PROCEDURE", 10024),
        new(53, "DROP_PROCEDURE", 10024),
        new(61, "CREATE_FUNCTION", 10023),
        new(62, "ALTER_FUNCTION", 10023),
        new(63, "DROP_FUNCTION", 10023),
        new(71, "CREATE_TRIGGER", 10025),
        new(72, "ALTER_TRIGGER", 10025),
        new(73, "DROP_TRIGGER", 10025),
        new(74, "CREATE_EVENT_NOTIFICATION", 10026),
        new(76, "DROP_EVENT_NOTIFICATION", 10026),
        new(91, "CREATE_TYPE", 10028),
        new(93, "DROP_TYPE", 10028),
        new(101, "CREATE_ASSEMBLY", 10027),
        new(102, "ALTER_ASSEMBLY", 10027),
        new(103, "DROP_ASSEMBLY", 10027),
        new(131, "CREATE_USER", 10031),
        new(132, "ALTER_USER", 10031),
        new(133, "DROP_USER", 10031),
        new(134, "CREATE_ROLE", 10032),
        new(135, "ALTER_ROLE", 10032),
        new(136, "DROP_ROLE", 10032),
        new(137, "CREATE_APPLICATION_ROLE", 10033),
        new(138, "ALTER_APPLICATION_ROLE", 10033),
        new(139, "DROP_APPLICATION_ROLE", 10033),
        new(141, "CREATE_SCHEMA", 10034),
        new(142, "ALTER_SCHEMA", 10034),
        new(143, "DROP_SCHEMA", 10034),
        new(144, "CREATE_LOGIN", 10006),
        new(145, "ALTER_LOGIN", 10006),
        new(146, "DROP_LOGIN", 10006),
        new(151, "CREATE_MESSAGE_TYPE", 10042),
        new(152, "ALTER_MESSAGE_TYPE", 10042),
        new(153, "DROP_MESSAGE_TYPE", 10042),
        new(154, "CREATE_CONTRACT", 10043),
        new(156, "DROP_CONTRACT", 10043),
        new(157, "CREATE_QUEUE", 10044),
        new(158, "ALTER_QUEUE", 10044),
        new(159, "DROP_QUEUE", 10044),
        new(161, "CREATE_SERVICE", 10045),
        new(162, "ALTER_SERVICE", 10045),
        new(163, "DROP_SERVICE", 10045),
        new(164, "CREATE_ROUTE", 10046),
        new(165, "ALTER_ROUTE", 10046),
        new(166, "DROP_ROUTE", 10046),
        new(167, "GRANT_SERVER", 10007),
        new(168, "DENY_SERVER", 10007),
        new(169, "REVOKE_SERVER", 10007),
        new(170, "GRANT_DATABASE", 10035),
        new(171, "DENY_DATABASE", 10035),
        new(172, "REVOKE_DATABASE", 10035),
        new(174, "CREATE_REMOTE_SERVICE_BINDING", 10047),
        new(175, "ALTER_REMOTE_SERVICE_BINDING", 10047),
        new(176, "DROP_REMOTE_SERVICE_BINDING", 10047),
        new(177, "CREATE_XML_SCHEMA_COLLECTION", 10048),
        new(178, "ALTER_XML_SCHEMA_COLLECTION", 10048),
        new(179, "DROP_XML_SCHEMA_COLLECTION", 10048),
        new(181, "CREATE_ENDPOINT", 10003),
        new(182, "ALTER_ENDPOINT", 10003),
        new(183, "DROP_ENDPOINT", 10003),
        new(191, "CREATE_PARTITION_FUNCTION", 10050),
        new(192, "ALTER_PARTITION_FUNCTION", 10050),
        new(193, "DROP_PARTITION_FUNCTION", 10050),
        new(194, "CREATE_PARTITION_SCHEME", 10051),
        new(195, "ALTER_PARTITION_SCHEME", 10051),
        new(196, "DROP_PARTITION_SCHEME", 10051),
        new(197, "CREATE_CERTIFICATE", 10030),
        new(198, "ALTER_CERTIFICATE", 10030),
        new(199, "DROP_CERTIFICATE", 10030),
        new(201, "CREATE_DATABASE", 10004),
        new(202, "ALTER_DATABASE", 10004),
        new(203, "DROP_DATABASE", 10004),
        new(204, "ALTER_AUTHORIZATION_SERVER", 10008),
        new(205, "ALTER_AUTHORIZATION_DATABASE", 10036),
        new(206, "CREATE_XML_INDEX", 10020),
        new(207, "ADD_ROLE_MEMBER", 10032),
        new(208, "DROP_ROLE_MEMBER", 10032),
        new(209, "ADD_SERVER_ROLE_MEMBER", 10005),
        new(210, "DROP_SERVER_ROLE_MEMBER", 10005),
        new(211, "ALTER_EXTENDED_PROPERTY", 10053),
        new(212, "ALTER_FULLTEXT_CATALOG", 10054),
        new(213, "ALTER_FULLTEXT_INDEX", 10020),
        new(214, "ALTER_INSTANCE", 10002),
        new(215, "ALTER_MESSAGE", 10014),
        new(216, "ALTER_PLAN_GUIDE", 10055),
        new(217, "ALTER_REMOTE_SERVER", 10015),
        new(218, "BIND_DEFAULT", 10052),
        new(219, "BIND_RULE", 10056),
        new(220, "CREATE_DEFAULT", 10052),
        new(221, "CREATE_EXTENDED_PROCEDURE", 10011),
        new(222, "CREATE_EXTENDED_PROPERTY", 10053),
        new(223, "CREATE_FULLTEXT_CATALOG", 10054),
        new(224, "CREATE_FULLTEXT_INDEX", 10020),
        new(225, "CREATE_LINKED_SERVER", 10012),
        new(226, "CREATE_LINKED_SERVER_LOGIN", 10013),
        new(227, "CREATE_MESSAGE", 10014),
        new(228, "CREATE_PLAN_GUIDE", 10055),
        new(229, "CREATE_RULE", 10056),
        new(230, "CREATE_REMOTE_SERVER", 10015),
        new(231, "DROP_DEFAULT", 10052),
        new(232, "DROP_EXTENDED_PROCEDURE", 10011),
        new(233, "DROP_EXTENDED_PROPERTY", 10053),
        new(234, "DROP_FULLTEXT_CATALOG", 10054),
        new(235, "DROP_FULLTEXT_INDEX", 10020),
        new(236, "DROP_LINKED_SERVER_LOGIN", 10013),
        new(237, "DROP_MESSAGE", 10014),
        new(238, "DROP_PLAN_GUIDE", 10055),
        new(239, "DROP_RULE", 10056),
        new(240, "DROP_REMOTE_SERVER", 10015),
        new(241, "RENAME", 10016),
        new(242, "UNBIND_DEFAULT", 10052),
        new(243, "UNBIND_RULE", 10056),
        new(244, "CREATE_SYMMETRIC_KEY", 10037),
        new(245, "ALTER_SYMMETRIC_KEY", 10037),
        new(246, "DROP_SYMMETRIC_KEY", 10037),
        new(247, "CREATE_ASYMMETRIC_KEY", 10038),
        new(248, "ALTER_ASYMMETRIC_KEY", 10038),
        new(249, "DROP_ASYMMETRIC_KEY", 10038),
        new(251, "ALTER_SERVICE_MASTER_KEY", 10010),
        new(252, "CREATE_MASTER_KEY", 10040),
        new(253, "ALTER_MASTER_KEY", 10040),
        new(254, "DROP_MASTER_KEY", 10040),
        new(255, "ADD_SIGNATURE_SCHEMA_OBJECT", 10039),
        new(256, "DROP_SIGNATURE_SCHEMA_OBJECT", 10039),
        new(257, "ADD_SIGNATURE", 10039),
        new(258, "DROP_SIGNATURE", 10039),
        new(259, "CREATE_CREDENTIAL", 10009),
        new(260, "ALTER_CREDENTIAL", 10009),
        new(261, "DROP_CREDENTIAL", 10009),
        new(262, "DROP_LINKED_SERVER", 10012),
        new(263, "ALTER_LINKED_SERVER", 10012),
        new(264, "CREATE_EVENT_SESSION", 10057),
        new(265, "ALTER_EVENT_SESSION", 10057),
        new(266, "DROP_EVENT_SESSION", 10057),
        new(267, "CREATE_RESOURCE_POOL", 10059),
        new(268, "ALTER_RESOURCE_POOL", 10059),
        new(269, "DROP_RESOURCE_POOL", 10059),
        new(270, "CREATE_WORKLOAD_GROUP", 10060),
        new(271, "ALTER_WORKLOAD_GROUP", 10060),
        new(272, "DROP_WORKLOAD_GROUP", 10060),
        new(273, "ALTER_RESOURCE_GOVERNOR_CONFIG", 10058),
        new(274, "CREATE_SPATIAL_INDEX", 10020),
        new(275, "CREATE_CRYPTOGRAPHIC_PROVIDER", 10061),
        new(276, "ALTER_CRYPTOGRAPHIC_PROVIDER", 10061),
        new(277, "DROP_CRYPTOGRAPHIC_PROVIDER", 10061),
        new(278, "CREATE_DATABASE_ENCRYPTION_KEY", 10062),
        new(279, "ALTER_DATABASE_ENCRYPTION_KEY", 10062),
        new(280, "DROP_DATABASE_ENCRYPTION_KEY", 10062),
        new(281, "CREATE_BROKER_PRIORITY", 10063),
        new(282, "ALTER_BROKER_PRIORITY", 10063),
        new(283, "DROP_BROKER_PRIORITY", 10063),
        new(284, "CREATE_SERVER_AUDIT", 10064),
        new(285, "ALTER_SERVER_AUDIT", 10064),
        new(286, "DROP_SERVER_AUDIT", 10064),
        new(287, "CREATE_SERVER_AUDIT_SPECIFICATION", 10065),
        new(288, "ALTER_SERVER_AUDIT_SPECIFICATION", 10065),
        new(289, "DROP_SERVER_AUDIT_SPECIFICATION", 10065),
        new(290, "CREATE_DATABASE_AUDIT_SPECIFICATION", 10066),
        new(291, "ALTER_DATABASE_AUDIT_SPECIFICATION", 10066),
        new(292, "DROP_DATABASE_AUDIT_SPECIFICATION", 10066),
        new(293, "CREATE_FULLTEXT_STOPLIST", 10067),
        new(294, "ALTER_FULLTEXT_STOPLIST", 10067),
        new(295, "DROP_FULLTEXT_STOPLIST", 10067),
        new(296, "ALTER_SERVER_CONFIGURATION", null),
        new(297, "CREATE_SEARCH_PROPERTY_LIST", 10069),
        new(298, "ALTER_SEARCH_PROPERTY_LIST", 10069),
        new(299, "DROP_SEARCH_PROPERTY_LIST", 10069),
        new(300, "CREATE_SERVER_ROLE", 10005),
        new(301, "ALTER_SERVER_ROLE", 10005),
        new(302, "DROP_SERVER_ROLE", 10005),
        new(303, "CREATE_SEQUENCE", 10070),
        new(304, "ALTER_SEQUENCE", 10070),
        new(305, "DROP_SEQUENCE", 10070),
        new(306, "CREATE_AVAILABILITY_GROUP", 10071),
        new(307, "ALTER_AVAILABILITY_GROUP", 10071),
        new(308, "DROP_AVAILABILITY_GROUP", 10071),
        new(309, "CREATE_AUDIT", 10072),
        new(310, "DROP_AUDIT", 10072),
        new(311, "ALTER_AUDIT", 10072),
        new(312, "CREATE_SECURITY_POLICY", 10073),
        new(313, "ALTER_SECURITY_POLICY", 10073),
        new(314, "DROP_SECURITY_POLICY", 10073),
        new(315, "CREATE_COLUMN_MASTER_KEY", 10074),
        new(316, "DROP_COLUMN_MASTER_KEY", 10074),
        new(317, "CREATE_COLUMN_ENCRYPTION_KEY", 10075),
        new(318, "ALTER_COLUMN_ENCRYPTION_KEY", 10075),
        new(319, "DROP_COLUMN_ENCRYPTION_KEY", 10075),
        new(320, "ALTER_DATABASE_SCOPED_CONFIGURATION", 10016),
        new(321, "CREATE_EXTERNAL_RESOURCE_POOL", 10076),
        new(322, "ALTER_EXTERNAL_RESOURCE_POOL", 10076),
        new(323, "DROP_EXTERNAL_RESOURCE_POOL", 10076),
        new(324, "CREATE_EXTERNAL_LIBRARY", 10077),
        new(325, "ALTER_EXTERNAL_LIBRARY", 10077),
        new(326, "DROP_EXTERNAL_LIBRARY", 10077),
        new(327, "ADD_SENSITIVITY_CLASSIFICATION", 10078),
        new(328, "DROP_SENSITIVITY_CLASSIFICATION", 10078),
        new(329, "CREATE_EXTERNAL_LANGUAGE", 10079),
        new(330, "ALTER_EXTERNAL_LANGUAGE", 10079),
        new(331, "DROP_EXTERNAL_LANGUAGE", 10079),
        new(332, "CREATE_EXTERNAL_STREAM", 10080),
        new(333, "DROP_EXTERNAL_STREAM", 10080),
        new(334, "CREATE_POOL", 10081),
        new(335, "ALTER_POOL", 10081),
        new(336, "DROP_POOL", 10081),
        new(337, "CREATE_SYNAPSE_WLG", 10082),
        new(338, "ALTER_SYNAPSE_WLG", 10082),
        new(339, "DROP_SYNAPSE_WLG", 10082),
        new(340, "CREATE_SYNAPSE_WLC", 10083),
        new(341, "ALTER_SYNAPSE_WLC", 10083),
        new(342, "DROP_SYNAPSE_WLC", 10083),
        new(343, "UNDO_DROP", 10084),
        new(344, "CREATE_VECTOR_INDEX", 10020),
        new(345, "ADD_INFORMATION_PROTECTION", 10078),
        new(346, "DROP_INFORMATION_PROTECTION", 10078),
        new(347, "CREATE_JSON_INDEX", 10020),
        new(10001, "DDL_EVENTS", null),
        new(10002, "DDL_SERVER_LEVEL_EVENTS", 10001),
        new(10003, "DDL_ENDPOINT_EVENTS", 10002),
        new(10004, "DDL_DATABASE_EVENTS", 10002),
        new(10005, "DDL_SERVER_SECURITY_EVENTS", 10002),
        new(10006, "DDL_LOGIN_EVENTS", 10005),
        new(10007, "DDL_GDR_SERVER_EVENTS", 10005),
        new(10008, "DDL_AUTHORIZATION_SERVER_EVENTS", 10005),
        new(10009, "DDL_CREDENTIAL_EVENTS", 10005),
        new(10010, "DDL_SERVICE_MASTER_KEY_EVENTS", 10005),
        new(10011, "DDL_EXTENDED_PROCEDURE_EVENTS", 10002),
        new(10012, "DDL_LINKED_SERVER_EVENTS", 10002),
        new(10013, "DDL_LINKED_SERVER_LOGIN_EVENTS", 10012),
        new(10014, "DDL_MESSAGE_EVENTS", 10002),
        new(10015, "DDL_REMOTE_SERVER_EVENTS", 10002),
        new(10016, "DDL_DATABASE_LEVEL_EVENTS", 10001),
        new(10017, "DDL_TABLE_VIEW_EVENTS", 10016),
        new(10018, "DDL_TABLE_EVENTS", 10017),
        new(10019, "DDL_VIEW_EVENTS", 10017),
        new(10020, "DDL_INDEX_EVENTS", 10017),
        new(10021, "DDL_STATISTICS_EVENTS", 10017),
        new(10022, "DDL_SYNONYM_EVENTS", 10016),
        new(10023, "DDL_FUNCTION_EVENTS", 10016),
        new(10024, "DDL_PROCEDURE_EVENTS", 10016),
        new(10025, "DDL_TRIGGER_EVENTS", 10016),
        new(10026, "DDL_EVENT_NOTIFICATION_EVENTS", 10016),
        new(10027, "DDL_ASSEMBLY_EVENTS", 10016),
        new(10028, "DDL_TYPE_EVENTS", 10016),
        new(10029, "DDL_DATABASE_SECURITY_EVENTS", 10016),
        new(10030, "DDL_CERTIFICATE_EVENTS", 10029),
        new(10031, "DDL_USER_EVENTS", 10029),
        new(10032, "DDL_ROLE_EVENTS", 10029),
        new(10033, "DDL_APPLICATION_ROLE_EVENTS", 10029),
        new(10034, "DDL_SCHEMA_EVENTS", 10029),
        new(10035, "DDL_GDR_DATABASE_EVENTS", 10029),
        new(10036, "DDL_AUTHORIZATION_DATABASE_EVENTS", 10029),
        new(10037, "DDL_SYMMETRIC_KEY_EVENTS", 10029),
        new(10038, "DDL_ASYMMETRIC_KEY_EVENTS", 10029),
        new(10039, "DDL_CRYPTO_SIGNATURE_EVENTS", 10029),
        new(10040, "DDL_MASTER_KEY_EVENTS", 10029),
        new(10041, "DDL_SSB_EVENTS", 10016),
        new(10042, "DDL_MESSAGE_TYPE_EVENTS", 10041),
        new(10043, "DDL_CONTRACT_EVENTS", 10041),
        new(10044, "DDL_QUEUE_EVENTS", 10041),
        new(10045, "DDL_SERVICE_EVENTS", 10041),
        new(10046, "DDL_ROUTE_EVENTS", 10041),
        new(10047, "DDL_REMOTE_SERVICE_BINDING_EVENTS", 10041),
        new(10048, "DDL_XML_SCHEMA_COLLECTION_EVENTS", 10016),
        new(10049, "DDL_PARTITION_EVENTS", 10016),
        new(10050, "DDL_PARTITION_FUNCTION_EVENTS", 10049),
        new(10051, "DDL_PARTITION_SCHEME_EVENTS", 10049),
        new(10052, "DDL_DEFAULT_EVENTS", 10016),
        new(10053, "DDL_EXTENDED_PROPERTY_EVENTS", 10016),
        new(10054, "DDL_FULLTEXT_CATALOG_EVENTS", 10016),
        new(10055, "DDL_PLAN_GUIDE_EVENTS", 10016),
        new(10056, "DDL_RULE_EVENTS", 10016),
        new(10057, "DDL_EVENT_SESSION_EVENTS", 10002),
        new(10058, "DDL_RESOURCE_GOVERNOR_EVENTS", 10002),
        new(10059, "DDL_RESOURCE_POOL", 10058),
        new(10060, "DDL_WORKLOAD_GROUP", 10058),
        new(10061, "DDL_CRYPTOGRAPHIC_PROVIDER_EVENTS", 10005),
        new(10062, "DDL_DATABASE_ENCRYPTION_KEY_EVENTS", 10029),
        new(10063, "DDL_BROKER_PRIORITY_EVENTS", 10041),
        new(10064, "DDL_SERVER_AUDIT_EVENTS", 10005),
        new(10065, "DDL_SERVER_AUDIT_SPECIFICATION_EVENTS", 10005),
        new(10066, "DDL_DATABASE_AUDIT_SPECIFICATION_EVENTS", 10029),
        new(10067, "DDL_FULLTEXT_STOPLIST_EVENTS", 10016),
        new(10069, "DDL_SEARCH_PROPERTY_LIST_EVENTS", 10016),
        new(10070, "DDL_SEQUENCE_EVENTS", 10016),
        new(10071, "DDL_AVAILABILITY_GROUP_EVENTS", 10002),
        new(10072, "DDL_DATABASE_AUDIT_EVENTS", 10029),
        new(10073, "DDL_SECURITY_POLICY_EVENTS", 10016),
        new(10074, "DDL_COLUMN_MASTER_KEY_EVENTS", 10016),
        new(10075, "DDL_COLUMN_ENCRYPTION_KEY_EVENTS", 10016),
        new(10076, "DDL_EXTERNAL_RESOURCE_POOL_EVENTS", 10058),
        new(10077, "DDL_LIBRARY_EVENTS", 10016),
        new(10078, "DDL_SENSITIVITY_EVENTS", 10016),
        new(10079, "DDL_EXTERNAL_LANGUAGE_EVENTS", 10016),
        new(10080, "DDL_EXTERNAL_STREAM_EVENTS", 10016),
        new(10081, "DDL_SYNAPSE_POOL_EVENTS", 10002),
        new(10082, "DDL_SYNAPSE_WLG_EVENTS", 10002),
        new(10083, "DDL_SYNAPSE_WLC_EVENTS", 10002),
        new(10084, "DDL_FIDO_EVENTS", 10017),
    ];

    /// <summary>Event type keyed by uppercase <c>type_name</c> — the form stored in <c>DdlTrigger.EventTypes</c>.</summary>
    private static readonly FrozenDictionary<string, TriggerEventType> ByName =
        All.ToFrozenDictionary(e => e.TypeName, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<int, TriggerEventType> ByType =
        All.ToFrozenDictionary(e => e.Type);

    /// <summary>
    /// Leaf-event closure per group type, computed once: every individual event
    /// (type &lt; <see cref="GroupTypeThreshold"/>) whose transitive parent chain
    /// reaches the group. Emitted in ascending type order to match real's
    /// <c>sys.trigger_events</c> projection.
    /// </summary>
    private static readonly FrozenDictionary<int, TriggerEventType[]> LeafClosureByGroup = BuildLeafClosures();

    private static FrozenDictionary<int, TriggerEventType[]> BuildLeafClosures()
    {
        var groups = new List<int>();
        foreach (var e in All)
        {
            if (e.Type >= GroupTypeThreshold)
                groups.Add(e.Type);
        }

        static bool Reaches(int type, int group)
        {
            var cursor = ByType[type].ParentType;
            while (cursor is int parent)
            {
                if (parent == group)
                    return true;
                cursor = ByType[parent].ParentType;
            }
            return false;
        }

        var result = new Dictionary<int, TriggerEventType[]>(groups.Count);
        foreach (var group in groups)
        {
            var leaves = new List<TriggerEventType>();
            foreach (var e in All)
            {
                if (e.Type < GroupTypeThreshold && Reaches(e.Type, group))
                    leaves.Add(e);
            }
            leaves.Sort((a, b) => a.Type.CompareTo(b.Type));
            result[group] = [.. leaves];
        }
        return result.ToFrozenDictionary();
    }

    /// <summary>
    /// Resolves an uppercase event-type / group name to its catalog entry.
    /// </summary>
    internal static bool TryResolve(string name, out TriggerEventType entry) => ByName.TryGetValue(name, out entry);

    /// <summary>
    /// Whether a resolved entry is an event group (expands to member events)
    /// rather than an individual event.
    /// </summary>
    internal static bool IsGroup(in TriggerEventType entry) => entry.Type >= GroupTypeThreshold;

    /// <summary>
    /// The leaf events reachable under <paramref name="group"/> (ascending type
    /// order). Empty for a non-group type.
    /// </summary>
    internal static TriggerEventType[] LeafClosure(int group) =>
        LeafClosureByGroup.TryGetValue(group, out var leaves) ? leaves : [];
}
