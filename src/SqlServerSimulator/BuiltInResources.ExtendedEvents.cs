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
            XeText(name, package.Name),
            SqlValue.FromGuid(package.Guid),
            XeText(description, package.Description),
            XeInt(package.Capabilities),
            XeText(capabilitiesDesc, package.CapabilitiesDesc),
            XeText(moduleGuid, package.ModuleGuid),
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
            XeText(name, item.Name),
            XeText(objectType, item.ObjectType),
            SqlValue.FromGuid(item.Package.Guid),
            XeText(description, item.Description),
            XeInt(item.Capabilities),
            XeText(capabilitiesDesc, item.CapabilitiesDesc),
            XeText(name, item.TypeName),
            XeGuid(item.TypePackage),
            XeInt(item.TypeSize),
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
            XeText(name, column.Name),
            SqlValue.FromInt32(column.ColumnId),
            XeText(name, column.Owner.Name),
            SqlValue.FromGuid(column.Owner.Package.Guid),
            XeText(name, column.TypeName),
            XeGuid(column.TypePackage),
            XeText(objectType, column.ColumnType),
            XeText(name, column.ColumnValue),
            XeInt(column.Capabilities),
            XeText(capabilitiesDesc, column.CapabilitiesDesc),
            XeText(description, column.Description),
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
            XeText(name, value.Map.Name),
            SqlValue.FromGuid(value.Map.Package.Guid),
            SqlValue.FromInt32(value.Key),
            XeText(description, value.Value),
        }));

        RegisterEventSessionCatalog(views, collation);
        RegisterEventSessionDmvs(views, name, description, objectType);
    }

    private static SqlValue XeText(NVarcharSqlType type, string? text) => text is null ? SqlValue.Null(type) : SqlValue.FromNVarchar(type, text);

    private static SqlValue XeInt(int? value) => value is { } number ? SqlValue.FromInt32(number) : SqlValue.Null(SqlType.Int32);

    private static SqlValue XeGuid(XePackage? package) => package is null ? SqlValue.Null(SqlType.UniqueIdentifier) : SqlValue.FromGuid(package.Guid);

    /// <summary>
    /// Whether the session reads the event-session catalog's rows: real
    /// filters <c>sys.server_event_sessions</c> and its companions to nothing
    /// without <c>VIEW SERVER PERFORMANCE STATE</c>, which <c>ALTER ANY EVENT
    /// SESSION</c> doesn't grant (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static bool SeesEventSessions(Parser.BatchContext batch) =>
        batch.Connection.Security.EffectiveIsDbo
        || batch.Connection.Simulation.HoldsServerPermission(batch.Connection.Security.Effective.LoginName, Permission.ViewServerPerformanceState);

    /// <summary>
    /// <c>sys.server_event_sessions</c> and the four views under it, one row
    /// per definition, event, action, attribute and target, in id order.
    /// </summary>
    private static void RegisterEventSessionCatalog(Dictionary<string, CatalogView> views, Collation collation)
    {
        var text128 = NVarcharSqlType.Get(128, collation, Coercibility.Implicit);
        var text60 = NVarcharSqlType.Get(60, collation, Coercibility.Implicit);
        var mode = CharSqlType.Get(1, collation, Coercibility.Implicit);
        static EventSessionDefinition[] Definitions(Parser.BatchContext batch) =>
            SeesEventSessions(batch) ? batch.Connection.Simulation.ExtendedEventsFor(batch).Definitions() : [];

        HeapColumn[] sessions =
        [
            new("event_session_id", SqlType.Int32, null, false),
            new("name", text128, 128, true),
            new("event_retention_mode", mode, 1, true),
            new("event_retention_mode_desc", text60, 60, true),
            new("max_dispatch_latency", SqlType.Int32, null, true),
            new("max_memory", SqlType.Int32, null, true),
            new("max_event_size", SqlType.Int32, null, true),
            new("memory_partition_mode", mode, 1, true),
            new("memory_partition_mode_desc", text60, 60, true),
            new("track_causality", SqlType.Bit, null, true),
            new("startup_state", SqlType.Bit, null, true),
            new("has_long_running_target", SqlType.Bit, null, true),
            new("max_duration", SqlType.BigInt, null, true),
        ];
        views["sys.server_event_sessions"] = new CatalogView("server_event_sessions", sessions, (batch, _) => Definitions(batch).Select(definition =>
        {
            var options = definition.Options;
            return new[]
            {
                SqlValue.FromInt32(definition.Id),
                SqlValue.FromNVarchar(text128, definition.Name),
                SqlValue.FromChar(mode, options.RetentionMode.ToString()),
                SqlValue.FromNVarchar(text60, options.RetentionModeDesc),
                SqlValue.FromInt32(options.MaxDispatchLatency),
                SqlValue.FromInt32(options.MaxMemory),
                SqlValue.FromInt32(options.MaxEventSize),
                SqlValue.FromChar(mode, options.PartitionMode.ToString()),
                SqlValue.FromNVarchar(text60, options.PartitionModeDesc),
                SqlValue.FromBoolean(options.TrackCausality),
                SqlValue.FromBoolean(options.StartupState),
                SqlValue.FromBoolean(false),
                SqlValue.FromInt64(options.MaxDuration),
            };
        }));

        var predicate = NVarcharSqlType.Get(3000, collation, Coercibility.Implicit);
        var predicateXml = NVarcharSqlType.Get(SqlType.MaxLengthSentinel, collation, Coercibility.Implicit);
        HeapColumn[] events =
        [
            new("event_session_id", SqlType.Int32, null, false),
            new("event_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("package", text128, 128, true),
            new("module", text128, 128, true),
            new("predicate", predicate, 3000, true),
            new("predicate_xml", predicateXml, null, true),
        ];
        views["sys.server_event_session_events"] = new CatalogView("server_event_session_events", events, (batch, _) =>
            Definitions(batch).SelectMany(definition => definition.Events.Select(sessionEvent => new[]
            {
                SqlValue.FromInt32(definition.Id),
                SqlValue.FromInt32(sessionEvent.Id),
                SqlValue.FromSystemName(sessionEvent.Name),
                SqlValue.FromNVarchar(text128, sessionEvent.Event.Package.Name),
                SqlValue.FromNVarchar(text128, sessionEvent.Event.Package.ModuleGuid),
                XeText(predicate, sessionEvent.PredicateText),
                XeText(predicateXml, sessionEvent.Predicate?.Xml),
            })));

        HeapColumn[] actions =
        [
            new("event_session_id", SqlType.Int32, null, false),
            new("event_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("package", text128, 128, true),
            new("module", text128, 128, true),
        ];
        views["sys.server_event_session_actions"] = new CatalogView("server_event_session_actions", actions, (batch, _) =>
            Definitions(batch).SelectMany(definition => definition.Events.SelectMany(sessionEvent => sessionEvent.Actions.Select(action => new[]
            {
                SqlValue.FromInt32(definition.Id),
                SqlValue.FromInt32(sessionEvent.Id),
                SqlValue.FromSystemName(action.Name),
                SqlValue.FromNVarchar(text128, action.Package.Name),
                SqlValue.FromNVarchar(text128, action.Package.ModuleGuid),
            }))));

        HeapColumn[] fields =
        [
            new("event_session_id", SqlType.Int32, null, false),
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("value", SqlType.SqlVariant, null, true),
        ];
        views["sys.server_event_session_fields"] = new CatalogView("server_event_session_fields", fields, (batch, _) =>
            Definitions(batch).SelectMany(definition =>
                definition.Events.SelectMany(sessionEvent => sessionEvent.Fields.Select(field => (sessionEvent.Id, field)))
                    .Concat(definition.Targets.SelectMany(target => target.Fields.Select(field => (target.Id, field))))
                    .Select(entry => new[]
                    {
                        SqlValue.FromInt32(definition.Id),
                        SqlValue.FromInt32(entry.Id),
                        SqlValue.FromSystemName(entry.field.Name),
                        SqlValue.FromVariant(entry.field.Literal),
                    })));

        HeapColumn[] targets =
        [
            new("event_session_id", SqlType.Int32, null, false),
            new("target_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("package", text128, 128, true),
            new("module", text128, 128, true),
        ];
        views["sys.server_event_session_targets"] = new CatalogView("server_event_session_targets", targets, (batch, _) =>
            Definitions(batch).SelectMany(definition => definition.Targets.Select(target => new[]
            {
                SqlValue.FromInt32(definition.Id),
                SqlValue.FromInt32(target.Id),
                SqlValue.FromSystemName(target.Name),
                SqlValue.FromNVarchar(text128, target.Target.Package.Name),
                SqlValue.FromNVarchar(text128, target.Target.Package.ModuleGuid),
            })));
    }

    /// <summary>
    /// <c>sys.dm_xe_sessions</c> and the four views under it: the running
    /// sessions — the internal ones first, then the rest in the order they
    /// started — with each one's buffers, targets, events, actions and the
    /// attribute values it runs with.
    /// </summary>
    private static void RegisterEventSessionDmvs(Dictionary<string, CatalogView> views, NVarcharSqlType name, NVarcharSqlType description, NVarcharSqlType objectType)
    {
        HeapColumn[] sessions =
        [
            new("address", SqlType.Varbinary, 8, false),
            new("name", name, 256, false),
            new("pending_buffers", SqlType.Int32, null, false),
            new("total_regular_buffers", SqlType.Int32, null, false),
            new("regular_buffer_size", SqlType.BigInt, null, false),
            new("total_large_buffers", SqlType.Int32, null, false),
            new("large_buffer_size", SqlType.BigInt, null, false),
            new("total_buffer_size", SqlType.BigInt, null, false),
            new("buffer_policy_flags", SqlType.Int32, null, false),
            new("buffer_policy_desc", name, 256, false),
            new("flags", SqlType.Int32, null, false),
            new("flag_desc", name, 256, false),
            new("dropped_event_count", SqlType.Int32, null, false),
            new("dropped_buffer_count", SqlType.Int32, null, false),
            new("blocked_event_fire_time", SqlType.Int32, null, false),
            new("create_time", SqlType.DateTime, null, false),
            new("largest_event_dropped_size", SqlType.Int32, null, false),
            new("session_source", name, 256, false),
            new("buffer_processed_count", SqlType.BigInt, null, false),
            new("buffer_full_count", SqlType.BigInt, null, false),
            new("total_bytes_generated", SqlType.BigInt, null, false),
            new("total_target_memory", SqlType.BigInt, null, false),
            new("buffer_processing_count", SqlType.Int32, null, false),
        ];
        views["sys.dm_xe_sessions"] = new CatalogView("dm_xe_sessions", sessions, (batch, _) => XeSessionRows(batch, name));

        HeapColumn[] targets =
        [
            new("event_session_address", SqlType.Varbinary, 8, false),
            new("target_name", name, 256, false),
            new("target_package_guid", SqlType.UniqueIdentifier, null, false),
            new("execution_count", SqlType.BigInt, null, false),
            new("execution_duration_ms", SqlType.BigInt, null, false),
            new("target_data", SqlType.NVarcharMax, null, true),
            new("bytes_written", SqlType.BigInt, null, false),
            new("used_memory", SqlType.BigInt, null, false),
            new("max_memory", SqlType.BigInt, null, false),
            new("failed_buffer_count", SqlType.BigInt, null, true),
        ];
        views["sys.dm_xe_session_targets"] = new CatalogView("dm_xe_session_targets", targets, (batch, _) => XeSessionTargetRows(batch, name));

        HeapColumn[] events =
        [
            new("event_session_address", SqlType.Varbinary, 8, false),
            new("event_name", name, 256, false),
            new("event_package_guid", SqlType.UniqueIdentifier, null, false),
            new("event_predicate", description, 3072, true),
            new("event_fire_count", SqlType.BigInt, null, false),
            new("event_fire_average_time", SqlType.BigInt, null, false),
            new("event_fire_min_time", SqlType.BigInt, null, false),
            new("event_fire_max_time", SqlType.BigInt, null, false),
        ];
        views["sys.dm_xe_session_events"] = new CatalogView("dm_xe_session_events", events, (batch, _) => XeSessionEventRows(batch, name, description));

        HeapColumn[] actions =
        [
            new("event_session_address", SqlType.Varbinary, 8, false),
            new("action_name", name, 256, false),
            new("action_package_guid", SqlType.UniqueIdentifier, null, false),
            new("event_name", name, 256, false),
            new("event_package_guid", SqlType.UniqueIdentifier, null, false),
        ];
        views["sys.dm_xe_session_event_actions"] = new CatalogView("dm_xe_session_event_actions", actions, (batch, _) =>
            RunningXeSessions(batch).SelectMany(session => session.Definition.Events.Reverse().SelectMany(sessionEvent => sessionEvent.Actions.Reverse().Select(action => new[]
            {
                SqlValue.FromVarbinary(session.Address),
                SqlValue.FromNVarchar(name, action.Name),
                SqlValue.FromGuid(action.Package.Guid),
                SqlValue.FromNVarchar(name, sessionEvent.Event.Name),
                SqlValue.FromGuid(sessionEvent.Event.Package.Guid),
            }))));

        HeapColumn[] objectColumns =
        [
            new("event_session_address", SqlType.Varbinary, 8, false),
            new("column_name", name, 256, false),
            new("column_id", SqlType.Int32, null, false),
            new("column_value", description, 3072, true),
            new("object_type", objectType, 60, false),
            new("object_name", name, 256, false),
            new("object_package_guid", SqlType.UniqueIdentifier, null, false),
        ];
        views["sys.dm_xe_session_object_columns"] = new CatalogView("dm_xe_session_object_columns", objectColumns, (batch, _) => XeSessionObjectColumnRows(batch, name, description, objectType));
    }

    private static RunningEventSession[] RunningXeSessions(Parser.BatchContext batch) =>
        batch.Connection.Simulation.ExtendedEventsFor(batch).RunningSessions();

    private static IEnumerable<SqlValue[]> XeSessionRows(Parser.BatchContext batch, NVarcharSqlType name)
    {
        var server = batch.Connection.Simulation.ExtendedEventsFor(batch);
        foreach (var (internalSession, running) in server.ListOrder())
        {
            if (internalSession is { } engine)
            {
                yield return XeSessionRow(name, engine.Address, engine.Name, engine.TotalRegularBuffers, engine.RegularBufferSize, 0, 0,
                    engine.BufferPolicyFlags, engine.BufferPolicyDesc, engine.Flags, engine.FlagDesc, server.StartTime, "internal", 0);
                continue;
            }
            var session = running!;
            var options = session.Definition.Options;
            var (regular, regularSize, large, largeSize) = options.Buffers();
            var (policy, policyDesc) = options.RetentionMode switch
            {
                'M' => (3, "drop_buffer"),
                'N' => (1, "block"),
                _ => (0, "drop_event"),
            };
            long targetMemory = 0;
            lock (session.Gate)
            {
                foreach (var (_, target) in session.Targets)
                    targetMemory = Math.Min(uint.MaxValue, targetMemory + target.MaxMemory);
            }
            yield return XeSessionRow(name, session.Address, session.Definition.Name, regular, regularSize, large, largeSize, policy, policyDesc,
                options.TrackCausality ? 2057 : 2049, options.TrackCausality ? "flush_on_close track_causality" : "flush_on_close", session.CreateTime, "server", targetMemory);
        }
    }

    private static SqlValue[] XeSessionRow(NVarcharSqlType name, byte[] address, string sessionName, int regular, long regularSize, int large, long largeSize,
        int policy, string policyDesc, int flags, string flagDesc, DateTime createTime, string source, long targetMemory) =>
    [
        SqlValue.FromVarbinary(address),
        SqlValue.FromNVarchar(name, sessionName),
        SqlValue.FromInt32(0),
        SqlValue.FromInt32(regular),
        SqlValue.FromInt64(regularSize),
        SqlValue.FromInt32(large),
        SqlValue.FromInt64(largeSize),
        SqlValue.FromInt64((regular * regularSize) + (large * largeSize)),
        SqlValue.FromInt32(policy),
        SqlValue.FromNVarchar(name, policyDesc),
        SqlValue.FromInt32(flags),
        SqlValue.FromNVarchar(name, flagDesc),
        SqlValue.FromInt32(0),
        SqlValue.FromInt32(0),
        SqlValue.FromInt32(0),
        SqlValue.FromDateTime(createTime),
        SqlValue.FromInt32(0),
        SqlValue.FromNVarchar(name, source),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(targetMemory),
        SqlValue.FromInt32(0),
    ];

    /// <summary>The running sessions' targets, each session's in the reverse of the order it added them, as real lists them.</summary>
    private static IEnumerable<SqlValue[]> XeSessionTargetRows(Parser.BatchContext batch, NVarcharSqlType name)
    {
        var server = batch.Connection.Simulation.ExtendedEventsFor(batch);
        foreach (var (internalSession, running) in server.ListOrder())
        {
            if (running is { } session)
            {
                foreach (var row in RunningTargetRows(session, name))
                    yield return row;
                continue;
            }
            foreach (var (targetName, package) in internalSession!.Targets)
            {
                var data = targetName == "event_file"
                    ? $"<EventFileTarget truncated=\"0\"><Buffers logged=\"0\" dropped=\"0\"/><File name=\"{EventFileTarget.LogDirectory}HkEngineEventFile_0_{server.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture)}.xel\"/></EventFileTarget>"
                    : null;
                yield return
                [
                    SqlValue.FromVarbinary(internalSession.Address),
                    SqlValue.FromNVarchar(name, targetName),
                    SqlValue.FromGuid(package),
                    SqlValue.FromInt64(0),
                    SqlValue.FromInt64(0),
                    data is null ? SqlValue.Null(SqlType.NVarcharMax) : SqlValue.FromNVarchar(SqlType.NVarcharMax, data),
                    SqlValue.FromInt64(data is null ? 0 : 16384),
                    SqlValue.FromInt64(0),
                    SqlValue.FromInt64(0),
                    SqlValue.FromInt64(0),
                ];
            }
        }
    }

    private static List<SqlValue[]> RunningTargetRows(RunningEventSession session, NVarcharSqlType name)
    {
        {
            List<SqlValue[]> rows = [];
            lock (session.Gate)
            {
                foreach (var target in session.Definition.TargetsInListOrder())
                {
                    if (!session.Targets.TryGetValue(target.Id, out var state))
                        continue;
                    var data = state.TargetData();
                    rows.Add(
                    [
                        SqlValue.FromVarbinary(session.Address),
                        SqlValue.FromNVarchar(name, target.Target.Name),
                        SqlValue.FromGuid(target.Target.Package.Guid),
                        SqlValue.FromInt64(state.ExecutionCount),
                        SqlValue.FromInt64(0),
                        data is null ? SqlValue.Null(SqlType.NVarcharMax) : SqlValue.FromNVarchar(SqlType.NVarcharMax, data),
                        SqlValue.FromInt64(state.BytesWritten),
                        SqlValue.FromInt64(state.UsedMemory),
                        SqlValue.FromInt64(state.MaxMemory),
                        SqlValue.FromInt64(0),
                    ]);
                }
            }
            return rows;
        }
    }

    /// <summary>The running sessions' events, each session's in the reverse of its <c>event_id</c> order, as real lists them.</summary>
    private static IEnumerable<SqlValue[]> XeSessionEventRows(Parser.BatchContext batch, NVarcharSqlType name, NVarcharSqlType description)
    {
        var server = batch.Connection.Simulation.ExtendedEventsFor(batch);
        foreach (var (internalSession, running) in server.ListOrder())
        {
            if (internalSession is { } engine)
            {
                foreach (var (eventName, package) in engine.Events)
                    yield return XeSessionEventRow(name, description, engine.Address, eventName, package, null);
                continue;
            }
            var session = running!;
            foreach (var sessionEvent in session.Definition.Events.Reverse())
                yield return XeSessionEventRow(name, description, session.Address, sessionEvent.Event.Name, sessionEvent.Event.Package.Guid, sessionEvent.Predicate?.Xml);
        }
    }

    private static SqlValue[] XeSessionEventRow(NVarcharSqlType name, NVarcharSqlType description, byte[] address, string eventName, Guid package, string? predicate) =>
    [
        SqlValue.FromVarbinary(address),
        SqlValue.FromNVarchar(name, eventName),
        SqlValue.FromGuid(package),
        XeText(description, predicate),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
        SqlValue.FromInt64(0),
    ];

    /// <summary>
    /// Every customizable column of each running session's targets, in the
    /// reverse of their added order, then of its events the same way — the
    /// value set, else the column's default (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    private static IEnumerable<SqlValue[]> XeSessionObjectColumnRows(Parser.BatchContext batch, NVarcharSqlType name, NVarcharSqlType description, NVarcharSqlType objectType)
    {
        var server = batch.Connection.Simulation.ExtendedEventsFor(batch);
        foreach (var (internalSession, running) in server.ListOrder())
        {
            if (internalSession is { } engine)
            {
                foreach (var (columnName, columnId, value, type, objectName, package) in engine.Columns)
                    yield return XeObjectColumnRow(name, description, objectType, engine.Address, columnName, columnId, value, type, objectName, package);
                continue;
            }
            var session = running!;
            var definition = session.Definition;
            foreach (var target in definition.TargetsInListOrder())
            {
                foreach (var column in target.Target.Columns.Where(column => column.ColumnType == "customizable"))
                {
                    // A server session's file target reports lazy_create_blob
                    // off, where the column's default is on (probed 2026-10-16
                    // against SQL Server 2025).
                    var value = column.Name == "lazy_create_blob" && target.Target.Name == "event_file" && !Array.Exists(target.Fields, field => ReferenceEquals(field.Column, column))
                        ? "false"
                        : SessionField.TextFor(target.Fields, column);
                    yield return XeObjectColumnRow(name, description, objectType, session.Address, column.Name, column.ColumnId, value, "target", target.Target.Name, target.Target.Package.Guid);
                }
            }
            foreach (var sessionEvent in definition.Events.Reverse())
            {
                foreach (var column in sessionEvent.Event.Columns.Where(column => column.ColumnType == "customizable"))
                    yield return XeObjectColumnRow(name, description, objectType, session.Address, column.Name, column.ColumnId, sessionEvent.FieldText(column), "event", sessionEvent.Event.Name, sessionEvent.Event.Package.Guid);
            }
        }
    }

    private static SqlValue[] XeObjectColumnRow(NVarcharSqlType name, NVarcharSqlType description, NVarcharSqlType objectType, byte[] address,
        string columnName, int columnId, string? value, string type, string objectName, Guid package) =>
    [
        SqlValue.FromVarbinary(address),
        SqlValue.FromNVarchar(name, columnName),
        SqlValue.FromInt32(columnId),
        XeText(description, value),
        SqlValue.FromNVarchar(objectType, type),
        SqlValue.FromNVarchar(name, objectName),
        SqlValue.FromGuid(package),
    ];
}
