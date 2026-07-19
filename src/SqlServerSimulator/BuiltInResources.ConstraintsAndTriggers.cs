using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static void RegisterConstraintsAndTriggers(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        void Iso(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["INFORMATION_SCHEMA." + name] = new CatalogView(name, columns, rows);
        // sys.triggers: per-trigger rows. Probe-confirmed shipped subset
        // (SQL Server 2025): name / object_id / parent_class /
        // parent_class_desc / parent_id / type / type_desc / create_date /
        // modify_date / is_disabled / is_instead_of_trigger /
        // is_not_for_replication. parent_class is always 1
        // (OBJECT_OR_COLUMN) for DML triggers attached to tables;
        // DDL triggers (database/server-scoped) use 0 / 100 and aren't
        // modeled. parent_id is the parent table's object_id.
        var parentClassObjectColumn = SqlValue.FromByte(1);
        var parentClassObjectColumnDesc = SqlValue.FromNVarchar("OBJECT_OR_COLUMN");
        Sys("triggers",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("parent_class", SqlType.TinyInt, null, false),
            new("parent_class_desc", nvarchar60Catalog, 60, true),
            new("parent_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("is_disabled", SqlType.Bit, null, false),
            new("is_instead_of_trigger", SqlType.Bit, null, false),
            new("is_not_for_replication", SqlType.Bit, null, false),
        ], (batch, database) =>
            EnumerateSysTriggers(batch, database, charTwo, parentClassObjectColumn, parentClassObjectColumnDesc));

        // sys.trigger_events: one row per (DML trigger, event) pair. Real SQL
        // Server types are 1 = INSERT, 2 = UPDATE, 3 = DELETE (distinct from
        // the internal action-flag bit values); is_first / is_last default 0
        // (no sp_settriggerorder modeled), event_group_type is NULL, and
        // is_trigger_event is 1. SMO's CREATE-scripting trigger query LEFT JOINs
        // it three times (one per DML event) to build the FOR clause.
        Sys("trigger_events",
        [
            new("object_id", SqlType.Int32, null, false),
            new("type", SqlType.Int32, null, false),
            new("type_desc", SqlType.NVarchar, 128, false),
            new("is_first", SqlType.Bit, null, true),
            new("is_last", SqlType.Bit, null, true),
            new("event_group_type", SqlType.Int32, null, true),
            new("event_group_type_desc", SqlType.NVarchar, 128, true),
            new("is_trigger_event", SqlType.Bit, null, true),
        ], EnumerateSysTriggerEvents);

        // sys.trigger_event_types: SQL Server's static event-type catalog
        // (probe-confirmed 3-column shape, SQL Server 2025). Version-stable
        // reference data backing the DDL-trigger expansion in
        // sys.trigger_events; surfaced as a queryable view for parity. type is
        // NOT NULL; type_name is nvarchar(64); parent_type is NULL for the two
        // roots (DDL_EVENTS, ALTER_SERVER_CONFIGURATION).
        var eventTypeNameCol = NVarcharSqlType.Get(64, Collation.Catalog, Coercibility.Implicit);
        Sys("trigger_event_types",
        [
            new("type", SqlType.Int32, null, false),
            new("type_name", eventTypeNameCol, 64, true),
            new("parent_type", SqlType.Int32, null, true),
        ], (batch, database) => EnumerateSysTriggerEventTypes(eventTypeNameCol));

        // sys.assembly_modules: CLR (SQLCLR) modules aren't modeled, so this is
        // an empty view with the documented SQL Server 2025 shape. SMO's
        // CREATE-scripting trigger query LEFT JOINs it to detect a CLR trigger.
        Sys("assembly_modules",
        [
            new("object_id", SqlType.Int32, null, false),
            new("assembly_id", SqlType.Int32, null, false),
            new("assembly_class", SqlType.NVarchar, 128, true),
            new("assembly_method", SqlType.NVarchar, 128, true),
            new("null_on_null_input", SqlType.Bit, null, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
        ], static (batch, database) => []);

        // sys.assemblies: CLR assemblies aren't modeled, so this is an empty
        // view with the documented SQL Server 2025 shape. SMO's CREATE-scripting
        // trigger query LEFT JOINs it (via sys.assembly_modules.assembly_id) to
        // pull a CLR trigger's assembly name; user triggers never appear here.
        Sys("assemblies",
        [
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, true),
            new("assembly_id", SqlType.Int32, null, false),
            new("clr_name", SqlType.NVarchar, 4000, true),
            new("permission_set", SqlType.TinyInt, null, false),
            new("permission_set_desc", nvarchar60Catalog, 60, true),
            new("is_visible", SqlType.Bit, null, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_user_defined", SqlType.Bit, null, false),
        ], static (batch, database) => []);

        // sys.foreign_keys: probe-confirmed 21-column shape against SQL
        // Server 2025 (2026-05-13). EF Core reads name / parent_object_id /
        // referenced_object_id / delete_referential_action /
        // update_referential_action; the simulator ships the full set so
        // catalog-introspection tooling sees an authentic shape.
        Sys("foreign_keys",
        [
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
            new("is_published", SqlType.Bit, null, true),
            new("is_schema_published", SqlType.Bit, null, true),
            new("referenced_object_id", SqlType.Int32, null, false),
            new("key_index_id", SqlType.Int32, null, false),
            new("is_disabled", SqlType.Bit, null, false),
            new("is_not_for_replication", SqlType.Bit, null, false),
            new("is_not_trusted", SqlType.Bit, null, false),
            new("delete_referential_action", SqlType.TinyInt, null, false),
            new("delete_referential_action_desc", nvarchar60Catalog, 60, true),
            new("update_referential_action", SqlType.TinyInt, null, false),
            new("update_referential_action_desc", nvarchar60Catalog, 60, true),
            new("is_system_named", SqlType.Bit, null, false),
        ], EnumerateSysForeignKeys);

        // sys.foreign_key_columns: probe-confirmed 6-column shape. One row
        // per (FK, column-pair) — composite FKs emit one row per participant
        // column with constraint_column_id starting at 1.
        Sys("foreign_key_columns",
        [
            new("constraint_object_id", SqlType.Int32, null, false),
            new("constraint_column_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("parent_column_id", SqlType.Int32, null, false),
            new("referenced_object_id", SqlType.Int32, null, false),
            new("referenced_column_id", SqlType.Int32, null, false),
        ], EnumerateSysForeignKeyColumns);

        // INFORMATION_SCHEMA.DOMAINS: ISO-standard surface. Real SQL Server
        // emits a row for every user-defined type (scalar UDTs surface their
        // base type; table types surface 'table type' as the data_type
        // literal — probe-confirmed G6). Load-bearing subset: DOMAIN_CATALOG /
        // DOMAIN_SCHEMA / DOMAIN_NAME / DATA_TYPE.
        var tableTypeDataType = SqlValue.FromNVarchar("table type");
        Iso("DOMAINS",
        [
            new("DOMAIN_CATALOG", SqlType.SystemName, 128, true),
            new("DOMAIN_SCHEMA", SqlType.SystemName, 128, true),
            new("DOMAIN_NAME", SqlType.SystemName, 128, false),
            new("DATA_TYPE", SqlType.NVarchar, 128, true),
        ], (batch, database) =>
            EnumerateInformationSchemaDomains(batch, database, tableTypeDataType));

        // sys.check_constraints: probe-confirmed 13-column shape (a subset
        // of sys.objects + the check-specific columns). Used by EF Migrations'
        // model snapshot and tooling that introspects existing CHECK rules.
        Sys("check_constraints",
        [
            new("name", SqlType.SystemName, 128, true),
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
            new("is_disabled", SqlType.Bit, null, false),
            new("is_not_for_replication", SqlType.Bit, null, false),
            new("is_not_trusted", SqlType.Bit, null, false),
            new("parent_column_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_database_collation", SqlType.Bit, null, false),
            new("is_system_named", SqlType.Bit, null, false),
        ], EnumerateSysCheckConstraints);

        // sys.key_constraints: PK + UNIQUE rows, parallel shape to
        // sys.foreign_keys. Probe-confirmed column set.
        Sys("key_constraints",
        [
            new("name", SqlType.SystemName, 128, true),
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
            new("unique_index_id", SqlType.Int32, null, false),
            new("is_system_named", SqlType.Bit, null, false),
            new("is_enforced", SqlType.Bit, null, false),
        ], EnumerateSysKeyConstraints);

        // sys.default_constraints: per-column named DEFAULT bindings. Real
        // SQL Server emits one row per default (inline or named via ALTER).
        Sys("default_constraints",
        [
            new("name", SqlType.SystemName, 128, true),
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
            new("parent_column_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("is_system_named", SqlType.Bit, null, false),
        ], EnumerateSysDefaultConstraints);

        // sys.edge_constraints / sys.edge_constraint_clauses: graph edge
        // constraints (CONNECTION (...) on an edge table). Graph tables aren't
        // modeled (sys.tables.is_edge is a constant 0), so both ship empty with
        // the full probe-confirmed shape (SQL Server 2025, 2026-07-16).
        Sys("edge_constraints",
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
            new("is_disabled", SqlType.Bit, null, false),
            new("is_not_trusted", SqlType.Bit, null, false),
            new("is_system_named", SqlType.Bit, null, false),
            new("delete_referential_action", SqlType.TinyInt, null, true),
            new("delete_referential_action_desc", nvarchar60Catalog, 60, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("edge_constraint_clauses",
        [
            new("object_id", SqlType.Int32, null, false),
            new("clause_number", SqlType.Int32, null, false),
            new("from_object_id", SqlType.Int32, null, false),
            new("to_object_id", SqlType.Int32, null, false),
        ], static (_, _) => EmptyCatalogRows);

        // sys.events: one row per event a trigger or event notification fires
        // for — the broader superset of sys.trigger_events (which the simulator
        // projects for DML triggers). WWI has zero triggers and its sys.events
        // is empty (probe-confirmed 2026-07-16), so an always-empty view with
        // the full shape matches real; DacFx references it. If DDL/event-
        // notification event projection is added later, populate here.
        Sys("events",
        [
            new("object_id", SqlType.Int32, null, false),
            new("type", SqlType.Int32, null, false),
            new("type_desc", SqlType.NVarchar, 128, false),
            new("is_trigger_event", SqlType.Bit, null, true),
            new("event_group_type", SqlType.Int32, null, true),
            new("event_group_type_desc", SqlType.NVarchar, 128, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.assembly_files: CLR assembly source files. SQLCLR isn't modeled
        // (sys.assemblies is empty), so this ships empty with the full probe-
        // confirmed shape (SQL Server 2025). Colocated with sys.assemblies /
        // sys.assembly_modules.
        Sys("assembly_files",
        [
            new("assembly_id", SqlType.Int32, null, false),
            new("name", SqlType.NVarchar, 260, true),
            new("file_id", SqlType.Int32, null, false),
            new("content", VarbinarySqlType.MaxForm, null, true),
            new("sha2_256", SqlType.Varbinary, 8000, true),
            new("sha2_512", SqlType.Varbinary, 8000, true),
        ], static (_, _) => EmptyCatalogRows);
    }

    /// <summary>
    /// Rows for <c>sys.triggers</c>: one row per <see cref="Trigger"/> in
    /// every schema (<c>parent_class</c> 1) plus one per database DDL
    /// trigger (<c>parent_class</c> 0, parent_id 0);
    /// <c>is_not_for_replication</c> is always 0 (the simulator
    /// parse-and-ignores the WITH clause) and <c>is_ms_shipped</c> always 0
    /// (DacFx's DDL-trigger reverse-engineering filters on it). Probe-
    /// confirmed columns; modify date mirrors create date because
    /// <c>ALTER TRIGGER</c> replaces the instance wholesale.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysTriggers(
        Parser.BatchContext batch,
        Database database,
        SqlType charTwo,
        SqlValue parentClassObjectColumn,
        SqlValue parentClassObjectColumnDesc)
    {
        _ = batch;
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        // 'TR' / 'SQL_TRIGGER' — matches Trigger.ObjectTypeCode /
        // Trigger.ObjectTypeDescription, kept as local constants here to
        // avoid one SqlValue allocation per row.
        var triggerType = SqlValue.FromChar(charTwo, "TR");
        var triggerTypeDesc = SqlValue.FromNVarchar("SQL_TRIGGER");
        var parentClassDatabase = SqlValue.FromByte(0);
        var parentClassDatabaseDesc = SqlValue.FromNVarchar("DATABASE");
        var parentIdZero = SqlValue.FromInt32(0);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values.OrderBy(t => t.ObjectId))
            {
                var createDate = SqlValue.FromDateTime(trigger.CreateDate);
                yield return [
                    SqlValue.FromSystemName(trigger.Name),
                    SqlValue.FromInt32(trigger.ObjectId),
                    parentClassObjectColumn,
                    parentClassObjectColumnDesc,
                    SqlValue.FromInt32(trigger.Parent.ObjectId),
                    triggerType,
                    triggerTypeDesc,
                    createDate,
                    createDate,
                    falseBit,
                    trigger.IsDisabled ? trueBit : falseBit,
                    trigger.Timing == TriggerTiming.InsteadOf ? trueBit : falseBit,
                    falseBit,
                ];
            }
        }
        // DDL triggers: stored on Database, not per-schema. parent_class=0
        // (DATABASE), parent_class_desc='DATABASE', parent_id=0 — probe-
        // confirmed against SQL Server 2025's sys.triggers for AW's
        // [ddlDatabaseTriggerLog].
        foreach (var ddl in database.DdlTriggers.Values.OrderBy(t => t.ObjectId))
        {
            var createDate = SqlValue.FromDateTime(ddl.CreateDate);
            yield return [
                SqlValue.FromSystemName(ddl.Name),
                SqlValue.FromInt32(ddl.ObjectId),
                parentClassDatabase,
                parentClassDatabaseDesc,
                parentIdZero,
                triggerType,
                triggerTypeDesc,
                createDate,
                createDate,
                falseBit,
                ddl.IsDisabled ? trueBit : falseBit,
                falseBit,
                falseBit,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.trigger_events</c>: one row per (trigger, event). For
    /// DML triggers the internal <see cref="TriggerActions"/> bit flags
    /// (INSERT=1, UPDATE=2, DELETE=4) map to real SQL Server's dense event type
    /// codes (INSERT=1, UPDATE=2, DELETE=3) with a NULL <c>event_group_type</c>.
    /// For DDL triggers each stored event-type name expands via
    /// <see cref="TriggerEventTypes"/>: a group name
    /// (<c>DDL_DATABASE_LEVEL_EVENTS</c>) yields one row per <em>leaf</em> event
    /// in the group's transitive closure — each carrying the group's id/desc in
    /// <c>event_group_type(_desc)</c> — while an individual event name yields a
    /// single row with a NULL group. <c>is_first</c> / <c>is_last</c> default 0
    /// (no <c>sp_settriggerorder</c> modeled); <c>is_trigger_event</c> is 1.
    /// DacFx's SqlDatabaseDdlTrigger reverse-engineering builds the element's
    /// EventType relationship from these rows.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysTriggerEvents(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var eventTypeName = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        var nullDesc = SqlValue.Null(eventTypeName);
        (int Type, string Desc)[] events =
        [
            (1, "INSERT"),
            (2, "UPDATE"),
            (3, "DELETE"),
        ];
        var flags = new[] { TriggerActions.Insert, TriggerActions.Update, TriggerActions.Delete };
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values.OrderBy(t => t.ObjectId))
            {
                var objectId = SqlValue.FromInt32(trigger.ObjectId);
                for (var i = 0; i < flags.Length; i++)
                {
                    if ((trigger.Actions & flags[i]) == 0)
                        continue;
                    yield return [
                        objectId,
                        SqlValue.FromInt32(events[i].Type),
                        SqlValue.FromString(eventTypeName, events[i].Desc),
                        falseBit,
                        falseBit,
                        nullInt,
                        nullDesc,
                        trueBit,
                    ];
                }
            }
        }

        // DDL triggers: each stored event-type name is either an event group
        // (expand to the leaf events in its transitive closure, tagging each
        // with the group's id/desc) or an individual event (one row, NULL
        // group). Duplicate leaves across overlapping group names are
        // de-duplicated so a trigger declared FOR two overlapping groups
        // doesn't double-emit a shared event.
        foreach (var ddl in database.DdlTriggers.Values.OrderBy(t => t.ObjectId))
        {
            var objectId = SqlValue.FromInt32(ddl.ObjectId);
            var emitted = new HashSet<int>();
            foreach (var eventName in ddl.EventTypes)
            {
                if (!TriggerEventTypes.TryResolve(eventName, out var entry))
                    continue;
                if (TriggerEventTypes.IsGroup(entry))
                {
                    var groupType = SqlValue.FromInt32(entry.Type);
                    var groupDesc = SqlValue.FromString(eventTypeName, entry.TypeName);
                    foreach (var leaf in TriggerEventTypes.LeafClosure(entry.Type))
                    {
                        if (!emitted.Add(leaf.Type))
                            continue;
                        yield return [
                            objectId,
                            SqlValue.FromInt32(leaf.Type),
                            SqlValue.FromString(eventTypeName, leaf.TypeName),
                            falseBit,
                            falseBit,
                            groupType,
                            groupDesc,
                            trueBit,
                        ];
                    }
                }
                else if (emitted.Add(entry.Type))
                {
                    yield return [
                        objectId,
                        SqlValue.FromInt32(entry.Type),
                        SqlValue.FromString(eventTypeName, entry.TypeName),
                        falseBit,
                        falseBit,
                        nullInt,
                        nullDesc,
                        trueBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.trigger_event_types</c>: the static event-type catalog
    /// (<see cref="TriggerEventTypes.All"/>). Server-scoped — identical across
    /// every database.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysTriggerEventTypes(NVarcharSqlType typeName)
    {
        var nullParent = SqlValue.Null(SqlType.Int32);
        foreach (var entry in TriggerEventTypes.All)
        {
            yield return [
                SqlValue.FromInt32(entry.Type),
                SqlValue.FromString(typeName, entry.TypeName),
                entry.ParentType is int parent ? SqlValue.FromInt32(parent) : nullParent,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.foreign_keys</c>: every FOREIGN KEY constraint across
    /// every schema. <c>type</c> = <c>F </c> (probe-confirmed two-char
    /// padding); <c>type_desc</c> = <c>FOREIGN_KEY_CONSTRAINT</c>.
    /// <c>delete_referential_action</c> / <c>update_referential_action</c>
    /// use the integer codes 0=NO_ACTION, 1=CASCADE, 2=SET_NULL, 3=SET_DEFAULT.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysForeignKeys(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var fkType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "F ");
        var fkTypeDesc = SqlValue.FromNVarchar("FOREIGN_KEY_CONSTRAINT");
        // key_index_id is the index id on the referenced table that satisfies
        // the FK — the simulator doesn't model indexes so report 1 (the
        // referenced PK / UQ ordinal in real SQL Server typically lands at 1
        // because PK gets a clustered index id of 1).
        var keyIndexId = SqlValue.FromInt32(1);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    var createDate = SqlValue.FromDateTime(table.CreateDate);
                    yield return [
                        SqlValue.FromSystemName(fk.Name),
                        SqlValue.FromInt32(fk.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        fkType,
                        fkTypeDesc,
                        createDate,
                        createDate,
                        falseBit,
                        falseBit,
                        falseBit,
                        SqlValue.FromInt32(fk.ReferencedTable.ObjectId),
                        keyIndexId,
                        fk.IsDisabled ? trueBit : falseBit,
                        falseBit,
                        fk.IsNotTrusted ? trueBit : falseBit,
                        SqlValue.FromByte((byte)fk.DeleteAction),
                        SqlValue.FromNVarchar(ReferentialActionDescription(fk.DeleteAction)),
                        SqlValue.FromByte((byte)fk.UpdateAction),
                        SqlValue.FromNVarchar(ReferentialActionDescription(fk.UpdateAction)),
                        fk.IsSystemNamed ? trueBit : falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.foreign_key_columns</c>: one per (FK, column-pair).
    /// <c>parent_column_id</c> and <c>referenced_column_id</c> are 1-based
    /// (probe-confirmed) — matches the <c>sys.columns.column_id</c> convention.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysForeignKeyColumns(Parser.BatchContext batch, Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
                    {
                        yield return [
                            SqlValue.FromInt32(fk.ObjectId),
                            SqlValue.FromInt32(i + 1),
                            SqlValue.FromInt32(fk.ChildTable.ObjectId),
                            SqlValue.FromInt32(fk.ChildColumnOrdinals[i] + 1),
                            SqlValue.FromInt32(fk.ReferencedTable.ObjectId),
                            SqlValue.FromInt32(fk.ReferencedColumnOrdinals[i] + 1),
                        ];
                    }
                }
            }
        }
    }

    private static string ReferentialActionDescription(ReferentialAction action) => action switch
    {
        ReferentialAction.NoAction => "NO_ACTION",
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET_NULL",
        ReferentialAction.SetDefault => "SET_DEFAULT",
        _ => "NO_ACTION",
    };

    /// <summary>
    /// Rows for <c>sys.check_constraints</c>: one row per CHECK constraint
    /// across every table in every schema. <c>parent_column_id</c> is the
    /// 1-based column id when the CHECK is column-attached (inline); 0 for
    /// table-level. <c>definition</c> is currently null — the simulator
    /// stores the parsed predicate tree, not source text (documented quirk).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysCheckConstraints(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var ckType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "C ");
        var ckTypeDesc = SqlValue.FromNVarchar("CHECK_CONSTRAINT");
        var falseDbCollation = SqlValue.FromBoolean(false);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var ck in table.CheckConstraints.OrderBy(c => c.ObjectId))
                {
                    var parentColumnId = 0;
                    if (ck.InlineColumn is { } inlineCol)
                    {
                        for (var i = 0; i < table.Columns.Length; i++)
                        {
                            if (database.Collation.Equals(table.Columns[i].Name, inlineCol))
                            {
                                parentColumnId = i + 1;
                                break;
                            }
                        }
                    }
                    var createDate = SqlValue.FromDateTime(table.CreateDate);
                    yield return [
                        SqlValue.FromSystemName(ck.Name),
                        SqlValue.FromInt32(ck.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        ckType,
                        ckTypeDesc,
                        createDate,
                        createDate,
                        falseBit,
                        falseBit,
                        falseBit,
                        ck.IsDisabled ? trueBit : falseBit,
                        falseBit,
                        ck.IsNotTrusted ? trueBit : falseBit,
                        SqlValue.FromInt32(parentColumnId),
                        ck.Definition is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(ck.Definition),
                        falseDbCollation,
                        ck.IsSystemNamed ? trueBit : falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.key_constraints</c>: PK + UNIQUE constraints across
    /// every table. <c>type</c> = <c>PK</c> / <c>UQ</c>;
    /// <c>type_desc</c> = <c>PRIMARY_KEY_CONSTRAINT</c> / <c>UNIQUE_CONSTRAINT</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysKeyConstraints(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var charTwo = CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit);
        var pkType = SqlValue.FromChar(charTwo, "PK");
        var uqType = SqlValue.FromChar(charTwo, "UQ");
        var pkTypeDesc = SqlValue.FromNVarchar("PRIMARY_KEY_CONSTRAINT");
        var uqTypeDesc = SqlValue.FromNVarchar("UNIQUE_CONSTRAINT");
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in schema.HeapTables.Values)
            {
                // unique_index_id must point each constraint at ITS backing
                // index in sys.indexes (DacFx's UQ query joins on it) — resolve
                // it through the shared index-id allocation authority rather
                // than hardcoding 1.
                var identities = table.IndexIdentities();
                foreach (var key in table.KeyConstraints.OrderBy(k => k.ObjectId))
                {
                    var isPk = key.Kind == KeyConstraintKind.PrimaryKey;
                    var createDate = SqlValue.FromDateTime(table.CreateDate);
                    var uniqueIndexId = 1;
                    foreach (var identity in identities)
                    {
                        if (ReferenceEquals(identity.Constraint, key))
                        {
                            uniqueIndexId = identity.IndexId;
                            break;
                        }
                    }
                    // PK gets a system-named flag iff the name starts with
                    // "PK__"; UQ similarly. The simulator tracks is_system_named
                    // on FK / CHECK explicitly; for KeyConstraint we infer from
                    // the auto-name prefix since the existing storage doesn't
                    // carry the flag.
                    var systemNamed = key.Name.StartsWith(isPk ? "PK__" : "UQ__", StringComparison.Ordinal);
                    yield return [
                        SqlValue.FromSystemName(key.Name),
                        SqlValue.FromInt32(key.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        isPk ? pkType : uqType,
                        isPk ? pkTypeDesc : uqTypeDesc,
                        createDate,
                        createDate,
                        falseBit,
                        falseBit,
                        falseBit,
                        SqlValue.FromInt32(uniqueIndexId),
                        systemNamed ? trueBit : falseBit,
                        trueBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.default_constraints</c>: one row per named DEFAULT
    /// binding. Inline DEFAULT at CREATE TABLE and ALTER TABLE ADD DEFAULT
    /// both populate; inline-without-CONSTRAINT-name auto-generates with
    /// <see cref="DefaultConstraint.IsSystemNamed"/> = true.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDefaultConstraints(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var dfType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "D ");
        var dfTypeDesc = SqlValue.FromNVarchar("DEFAULT_CONSTRAINT");
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in schema.HeapTables.Values)
            {
                for (var i = 0; i < table.Columns.Length; i++)
                {
                    var col = table.Columns[i];
                    if (col.DefaultConstraint is not { } df)
                        continue;
                    var createDate = SqlValue.FromDateTime(table.CreateDate);
                    yield return [
                        SqlValue.FromSystemName(df.Name),
                        SqlValue.FromInt32(df.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        dfType,
                        dfTypeDesc,
                        createDate,
                        createDate,
                        falseBit,
                        falseBit,
                        falseBit,
                        SqlValue.FromInt32(i + 1),
                        df.Definition is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(df.Definition),
                        df.IsSystemNamed ? trueBit : falseBit,
                    ];
                }
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaDomains(Parser.BatchContext batch, Database database, SqlValue tableTypeDataType)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.UserTypeId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(tt.Name),
                    tableTypeDataType,
                ];
            }
        }
    }
}
