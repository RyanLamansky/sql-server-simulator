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
        // the internal action-flag bit values); is_first / is_last read the
        // per-action sp_settriggerorder slots (Trigger.FirstForActions /
        // LastForActions), event_group_type is NULL, and is_trigger_event
        // is 1. SMO's CREATE-scripting trigger query LEFT JOINs it three
        // times (one per DML event) to build the FOR clause.
        Sys("trigger_events",
        [
            new("object_id", SqlType.Int32, null, false),
            new("type", SqlType.Int32, null, false),
            new("type_desc", nvarchar128Catalog, 128, false),
            new("is_first", SqlType.Bit, null, true),
            new("is_last", SqlType.Bit, null, true),
            new("event_group_type", SqlType.Int32, null, true),
            new("event_group_type_desc", nvarchar128Catalog, 128, true),
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
            new("type_name", NVarcharSqlType.Get(64, Collation.Baseline, Coercibility.Implicit), 64, true),
            new("parent_type", SqlType.Int32, null, true),
        ], (batch, database) => EnumerateSysTriggerEventTypes(eventTypeNameCol));

        // The server-scope trigger catalog (CREATE TRIGGER … ON ALL SERVER),
        // identical from every database; shapes probed 2026-09-28 against
        // SQL Server 2025. sys.server_trigger_events orders its columns
        // unlike sys.trigger_events, with is_trigger_event ahead of the
        // ordering pair.
        Sys("server_triggers",
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
        ], (batch, database) => EnumerateSysServerTriggers(batch, charTwo));
        Sys("server_trigger_events",
        [
            new("object_id", SqlType.Int32, null, false),
            new("type", SqlType.Int32, null, false),
            new("type_desc", nvarchar128Catalog, 128, false),
            new("is_trigger_event", SqlType.Bit, null, true),
            new("is_first", SqlType.Bit, null, true),
            new("is_last", SqlType.Bit, null, true),
            new("event_group_type", SqlType.Int32, null, true),
            new("event_group_type_desc", nvarchar128Catalog, 128, true),
        ], static (batch, database) => EnumerateSysServerTriggerEvents(batch));
        Sys("server_sql_modules",
        [
            new("object_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_ansi_nulls", SqlType.Bit, null, true),
            new("uses_quoted_identifier", SqlType.Bit, null, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
        ], static (batch, database) => EnumerateSysServerSqlModules(batch));

        // sys.assembly_modules: one row per CLR routine bound through an
        // EXTERNAL NAME clause. SMO's CREATE-scripting trigger query LEFT JOINs
        // it to detect a CLR trigger; only scalar functions are modeled, so
        // triggers never appear.
        Sys("assembly_modules",
        [
            new("object_id", SqlType.Int32, null, false),
            new("assembly_id", SqlType.Int32, null, false),
            new("assembly_class", NVarcharSqlType.Get(128, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit), 128, true),
            new("assembly_method", NVarcharSqlType.Get(128, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit), 128, true),
            new("null_on_null_input", SqlType.Bit, null, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
        ], static (batch, database) => EnumerateAssemblyModules(database));

        // sys.assemblies: the assemblies registered by CREATE ASSEMBLY, plus
        // the Microsoft.SqlServer.Types system row real SQL Server always
        // carries (assembly_id 1, UNSAFE_ACCESS) — the one sys.assembly_types
        // joins against. SMO's CREATE-scripting trigger query LEFT JOINs this
        // via sys.assembly_modules.assembly_id.
        Sys("assemblies",
        [
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, true),
            new("assembly_id", SqlType.Int32, null, false),
            new("clr_name", NVarcharSqlType.Get(4000, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit), 4000, true),
            new("permission_set", SqlType.TinyInt, null, true),
            new("permission_set_desc", nvarchar60Catalog, 60, true),
            new("is_visible", SqlType.Bit, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_user_defined", SqlType.Bit, null, true),
        ], static (batch, database) => EnumerateAssemblies(database));

        // sys.foreign_keys: probe-confirmed 21-column shape against SQL
        // Server 2025 (2026-05-13). EF Core reads name / parent_object_id /
        // referenced_object_id / delete_referential_action /
        // update_referential_action; the simulator ships the full set so
        // catalog-introspection tooling sees an authentic shape.
        Sys("foreign_keys",
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
            new("referenced_object_id", SqlType.Int32, null, true),
            new("key_index_id", SqlType.Int32, null, true),
            new("is_disabled", SqlType.Bit, null, false),
            new("is_not_for_replication", SqlType.Bit, null, false),
            new("is_not_trusted", SqlType.Bit, null, false),
            new("delete_referential_action", SqlType.TinyInt, null, true),
            new("delete_referential_action_desc", nvarchar60Catalog, 60, true),
            new("update_referential_action", SqlType.TinyInt, null, true),
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

        // INFORMATION_SCHEMA.DOMAINS: one row per alias type and table type,
        // in real's 17 columns.
        Iso("DOMAINS",
        [
            new("DOMAIN_CATALOG", nvarchar128Baseline, 128, true),
            new("DOMAIN_SCHEMA", nvarchar128Baseline, 128, true),
            new("DOMAIN_NAME", SqlType.SystemName, 128, false),
            new("DATA_TYPE", nvarchar128Baseline, 128, true),
            new("CHARACTER_MAXIMUM_LENGTH", SqlType.Int32, null, true),
            new("CHARACTER_OCTET_LENGTH", SqlType.Int32, null, true),
            new("COLLATION_CATALOG", SqlType.SystemName, 128, true),
            new("COLLATION_SCHEMA", SqlType.SystemName, 128, true),
            new("COLLATION_NAME", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
            new("NUMERIC_PRECISION", SqlType.TinyInt, null, true),
            new("NUMERIC_PRECISION_RADIX", SqlType.SmallInt, null, true),
            new("NUMERIC_SCALE", SqlType.Int32, null, true),
            new("DATETIME_PRECISION", SqlType.SmallInt, null, true),
            new("DOMAIN_DEFAULT", SqlType.NVarchar, 4000, true),
        ], EnumerateInformationSchemaDomains);

        // INFORMATION_SCHEMA.DOMAIN_CONSTRAINTS: one row per alias type a
        // CREATE RULE object is bound to, naming the rule (probed 2026-10-02
        // against SQL Server 2025).
        Iso("DOMAIN_CONSTRAINTS",
        [
            new("CONSTRAINT_CATALOG", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_SCHEMA", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_NAME", SqlType.SystemName, 128, false),
            new("DOMAIN_CATALOG", nvarchar128Baseline, 128, true),
            new("DOMAIN_SCHEMA", nvarchar128Baseline, 128, true),
            new("DOMAIN_NAME", SqlType.SystemName, 128, false),
            new("IS_DEFERRABLE", VarcharSqlType.Get(2, Collation.Baseline, Coercibility.Implicit), 2, false),
            new("INITIALLY_DEFERRED", VarcharSqlType.Get(2, Collation.Baseline, Coercibility.Implicit), 2, false),
        ], EnumerateInformationSchemaDomainConstraints);

        // INFORMATION_SCHEMA.TABLE_PRIVILEGES / COLUMN_PRIVILEGES: the
        // explicit GRANTs on a table or view, object-level and column-level
        // respectively (see EnumerateInformationSchemaPrivileges).
        HeapColumn[] PrivilegeShape(bool column) =>
        [
            new("GRANTOR", nvarchar128Baseline, 128, true),
            new("GRANTEE", nvarchar128Baseline, 128, true),
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            .. column ? (HeapColumn[])[new("COLUMN_NAME", nvarchar128Baseline, 128, true)] : [],
            new("PRIVILEGE_TYPE", VarcharSqlType.Get(10, Collation.Baseline, Coercibility.Implicit), 10, true),
            new("IS_GRANTABLE", VarcharSqlType.Get(3, Collation.Baseline, Coercibility.Implicit), 3, true),
        ];
        Iso("TABLE_PRIVILEGES", PrivilegeShape(column: false), (batch, database) => EnumerateInformationSchemaPrivileges(batch, database, columnLevel: false));
        Iso("COLUMN_PRIVILEGES", PrivilegeShape(column: true), (batch, database) => EnumerateInformationSchemaPrivileges(batch, database, columnLevel: true));

        // INFORMATION_SCHEMA.TABLE_CONSTRAINTS: one row per PRIMARY KEY /
        // UNIQUE / FOREIGN KEY / CHECK constraint in the current database.
        // Probe-confirmed 9-column shape (SQL Server 2025): CONSTRAINT_TYPE is
        // varchar(11) ('PRIMARY KEY' / 'UNIQUE' / 'FOREIGN KEY' / 'CHECK'),
        // IS_DEFERRABLE / INITIALLY_DEFERRED are constant 'NO' (SQL Server has
        // no deferrable constraints). CATALOG = database name, SCHEMA = the
        // object's schema. SQLAlchemy's get_pk_constraint reads it.
        Iso("TABLE_CONSTRAINTS",
        [
            new("CONSTRAINT_CATALOG", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_SCHEMA", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_NAME", SqlType.SystemName, 128, false),
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, true),
            new("CONSTRAINT_TYPE", SqlType.Varchar, 11, true),
            new("IS_DEFERRABLE", SqlType.Varchar, 2, false),
            new("INITIALLY_DEFERRED", SqlType.Varchar, 2, false),
        ], EnumerateInformationSchemaTableConstraints);

        // INFORMATION_SCHEMA.KEY_COLUMN_USAGE: one row per column participating
        // in a PRIMARY KEY / UNIQUE / FOREIGN KEY constraint, in key order.
        // Probe-confirmed 8-column shape (SQL Server 2025) — real SQL Server's
        // view does NOT include the ISO-standard POSITION_IN_UNIQUE_CONSTRAINT
        // column (referencing it raises Msg 207), so it is deliberately omitted.
        // For a FK the row names the child (constrained) column. SQLAlchemy's
        // get_pk_constraint / get_foreign_keys read it.
        Iso("KEY_COLUMN_USAGE",
        [
            new("CONSTRAINT_CATALOG", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_SCHEMA", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_NAME", SqlType.SystemName, 128, false),
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("COLUMN_NAME", nvarchar128Baseline, 128, true),
            new("ORDINAL_POSITION", SqlType.Int32, null, false),
        ], EnumerateInformationSchemaKeyColumnUsage);

        // INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS: one row per FOREIGN KEY.
        // Probe-confirmed 9-column shape (SQL Server 2025). UNIQUE_CONSTRAINT_NAME
        // is the referenced PK / UNIQUE constraint's name; MATCH_OPTION is the
        // constant 'SIMPLE'; UPDATE_RULE / DELETE_RULE use ISO wording with a
        // space ('NO ACTION' / 'CASCADE' / 'SET NULL' / 'SET DEFAULT') — distinct
        // from sys.foreign_keys' underscore desc form. SQLAlchemy's
        // get_foreign_keys reads it.
        Iso("REFERENTIAL_CONSTRAINTS",
        [
            new("CONSTRAINT_CATALOG", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_SCHEMA", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_NAME", SqlType.SystemName, 128, false),
            new("UNIQUE_CONSTRAINT_CATALOG", nvarchar128Baseline, 128, true),
            new("UNIQUE_CONSTRAINT_SCHEMA", nvarchar128Baseline, 128, true),
            new("UNIQUE_CONSTRAINT_NAME", SqlType.SystemName, 128, true),
            new("MATCH_OPTION", SqlType.Varchar, 7, true),
            new("UPDATE_RULE", SqlType.Varchar, 11, true),
            new("DELETE_RULE", SqlType.Varchar, 11, true),
        ], EnumerateInformationSchemaReferentialConstraints);

        // INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE: one row per (constraint,
        // column) pair, over PRIMARY KEY / UNIQUE / FOREIGN KEY (the child
        // columns, as in KEY_COLUMN_USAGE) and CHECK. Probe-confirmed 7-column
        // shape (SQL Server 2025), with the table columns leading and the
        // constraint columns trailing — the reverse of TABLE_CONSTRAINTS.
        // mssql-django's `get_relations` joins it twice to resolve a foreign
        // key's child and referenced columns.
        Iso("CONSTRAINT_COLUMN_USAGE",
        [
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("COLUMN_NAME", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_CATALOG", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_SCHEMA", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_NAME", SqlType.SystemName, 128, false),
        ], EnumerateInformationSchemaConstraintColumnUsage);

        // INFORMATION_SCHEMA.CONSTRAINT_TABLE_USAGE: one row per constraint,
        // naming the table it sits on. Probe-confirmed 6-column shape — the
        // same as CONSTRAINT_COLUMN_USAGE without COLUMN_NAME.
        Iso("CONSTRAINT_TABLE_USAGE",
        [
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("CONSTRAINT_CATALOG", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_SCHEMA", nvarchar128Baseline, 128, true),
            new("CONSTRAINT_NAME", SqlType.SystemName, 128, false),
        ], EnumerateInformationSchemaConstraintTableUsage);

        // sys.check_constraints: probe-confirmed 13-column shape (a subset
        // of sys.objects + the check-specific columns). Used by EF Migrations'
        // model snapshot and tooling that introspects existing CHECK rules.
        Sys("check_constraints",
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
            new("is_not_for_replication", SqlType.Bit, null, false),
            new("is_not_trusted", SqlType.Bit, null, false),
            new("parent_column_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_database_collation", SqlType.Bit, null, true),
            new("is_system_named", SqlType.Bit, null, false),
        ], EnumerateSysCheckConstraints);

        // sys.key_constraints: PK + UNIQUE rows, parallel shape to
        // sys.foreign_keys. Probe-confirmed column set.
        Sys("key_constraints",
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
            new("unique_index_id", SqlType.Int32, null, true),
            new("is_system_named", SqlType.Bit, null, false),
            new("is_enforced", SqlType.Bit, null, true),
        ], EnumerateSysKeyConstraints);

        // sys.default_constraints: per-column named DEFAULT bindings. Real
        // SQL Server emits one row per default (inline or named via ALTER).
        Sys("default_constraints",
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
            new("parent_column_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("is_system_named", SqlType.Bit, null, false),
        ], EnumerateSysDefaultConstraints);

        // sys.edge_constraints / sys.edge_constraint_clauses: an edge table's
        // CONNECTION (...) constraints, one clause row per node-table pair.
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
        ], (batch, database) =>
            from schema in database.Schemas.EnumerateValues()
            from table in CatalogTables(schema, batch)
            from ec in table.EdgeConstraints
            orderby ec.ObjectId
            select new SqlValue[]
            {
                SqlValue.FromSystemName(ec.Name),
                SqlValue.FromInt32(ec.ObjectId),
                SqlValue.Null(SqlType.Int32),
                SqlValue.FromInt32(table.SchemaId),
                SqlValue.FromInt32(table.ObjectId),
                SqlValue.FromChar(charTwo, "EC"),
                SqlValue.FromString(nvarchar60Catalog, "EDGE_CONSTRAINT"),
                SqlValue.FromDateTime(ec.CreateDate),
                SqlValue.FromDateTime(ec.ModifyDate),
                SqlValue.FromBoolean(false),
                SqlValue.FromBoolean(false),
                SqlValue.FromBoolean(false),
                SqlValue.FromBoolean(ec.IsDisabled),
                SqlValue.FromBoolean(ec.IsNotTrusted),
                SqlValue.FromBoolean(ec.IsSystemNamed),
                SqlValue.FromByte(ec.CascadeOnDelete ? (byte)1 : (byte)0),
                SqlValue.FromString(nvarchar60Catalog, ec.CascadeOnDelete ? "CASCADE" : "NO_ACTION"),
            });
        Sys("edge_constraint_clauses",
        [
            new("object_id", SqlType.Int32, null, false),
            new("clause_number", SqlType.Int32, null, false),
            new("from_object_id", SqlType.Int32, null, false),
            new("to_object_id", SqlType.Int32, null, false),
        ], (batch, database) =>
            from schema in database.Schemas.EnumerateValues()
            from table in CatalogTables(schema, batch)
            from ec in table.EdgeConstraints
            orderby ec.ObjectId
            from clause in ec.Clauses.Select((pair, index) => (pair.From, pair.To, Number: index + 1))
            select new SqlValue[]
            {
                SqlValue.FromInt32(ec.ObjectId),
                SqlValue.FromInt32(clause.Number),
                SqlValue.FromInt32(clause.From.ObjectId),
                SqlValue.FromInt32(clause.To.ObjectId),
            });

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
            new("type_desc", nvarchar128Catalog, 128, false),
            new("is_trigger_event", SqlType.Bit, null, true),
            new("event_group_type", SqlType.Int32, null, true),
            new("event_group_type_desc", nvarchar128Catalog, 128, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.assembly_files: one row per registered assembly, carrying the
        // verbatim bytes CREATE ASSEMBLY supplied. Probe-confirmed shape (SQL
        // Server 2025); the system Microsoft.SqlServer.Types row real carries is
        // omitted because the simulator has no bytes to project for it.
        // Colocated with sys.assemblies / sys.assembly_modules.
        Sys("assembly_files",
        [
            new("assembly_id", SqlType.Int32, null, false),
            new("name", SqlType.NVarchar, 260, true),
            new("file_id", SqlType.Int32, null, false),
            new("content", VarbinarySqlType.MaxForm, null, true),
            new("sha2_256", SqlType.Varbinary, 8000, true),
            new("sha2_512", SqlType.Varbinary, 8000, true),
        ], static (_, database) => EnumerateAssemblyFiles(database));
    }

    /// <summary>
    /// Rows for <c>sys.triggers</c>: one row per <see cref="Trigger"/> in
    /// every schema (<c>parent_class</c> 1) plus one per database DDL
    /// trigger (<c>parent_class</c> 0, parent_id 0);
    /// <c>is_ms_shipped</c> always 0
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
        // 'TR' / 'SQL_TRIGGER' and 'TA' / 'CLR_TRIGGER' — matches
        // Trigger.ObjectTypeCode / Trigger.ObjectTypeDescription, kept as
        // local constants here to avoid one SqlValue allocation per row.
        var triggerType = SqlValue.FromChar(charTwo, "TR");
        var triggerTypeDesc = SqlValue.FromNVarchar("SQL_TRIGGER");
        var clrTriggerType = SqlValue.FromChar(charTwo, "TA");
        var clrTriggerTypeDesc = SqlValue.FromNVarchar("CLR_TRIGGER");
        var parentClassDatabase = SqlValue.FromByte(0);
        var parentClassDatabaseDesc = SqlValue.FromNVarchar("DATABASE");
        var parentIdZero = SqlValue.FromInt32(0);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var trigger in schema.Triggers.EnumerateValues().OrderBy(t => t.ObjectId))
            {
                yield return [
                    SqlValue.FromSystemName(trigger.Name),
                    SqlValue.FromInt32(trigger.ObjectId),
                    parentClassObjectColumn,
                    parentClassObjectColumnDesc,
                    SqlValue.FromInt32(trigger.Parent.ObjectId),
                    trigger.ClrEntry is null ? triggerType : clrTriggerType,
                    trigger.ClrEntry is null ? triggerTypeDesc : clrTriggerTypeDesc,
                    SqlValue.FromDateTime(trigger.CreateDate),
                    SqlValue.FromDateTime(trigger.ModifyDate),
                    falseBit,
                    trigger.IsDisabled ? trueBit : falseBit,
                    trigger.Timing == TriggerTiming.InsteadOf ? trueBit : falseBit,
                    trigger.NotForReplication ? trueBit : falseBit,
                ];
            }
        }
        // DDL triggers: stored on Database, not per-schema. parent_class=0
        // (DATABASE), parent_class_desc='DATABASE', parent_id=0 — probe-
        // confirmed against SQL Server 2025's sys.triggers for AW's
        // [ddlDatabaseTriggerLog].
        foreach (var ddl in database.DdlTriggers.EnumerateValues().OrderBy(t => t.ObjectId))
        {
            yield return [
                SqlValue.FromSystemName(ddl.Name),
                SqlValue.FromInt32(ddl.ObjectId),
                parentClassDatabase,
                parentClassDatabaseDesc,
                parentIdZero,
                ddl.ClrEntry is null ? triggerType : clrTriggerType,
                ddl.ClrEntry is null ? triggerTypeDesc : clrTriggerTypeDesc,
                SqlValue.FromDateTime(ddl.CreateDate),
                SqlValue.FromDateTime(ddl.ModifyDate),
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
    /// single row with a NULL group. A DML row's <c>is_first</c> /
    /// <c>is_last</c> read that action's <c>sp_settriggerorder</c> slot
    /// (<see cref="Trigger.FirstForActions"/> / <see cref="Trigger.LastForActions"/>),
    /// so ordering a multi-action trigger first for INSERT leaves its UPDATE
    /// row at 0; a DDL trigger reads its per-event slots the same way.
    /// <c>is_trigger_event</c> is 1.
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
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var trigger in schema.Triggers.EnumerateValues().OrderBy(t => t.ObjectId))
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
                        (trigger.FirstForActions & flags[i]) != 0 ? trueBit : falseBit,
                        (trigger.LastForActions & flags[i]) != 0 ? trueBit : falseBit,
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
        // group).
        foreach (var ddl in database.DdlTriggers.EnumerateValues().OrderBy(t => t.ObjectId))
        {
            var objectId = SqlValue.FromInt32(ddl.ObjectId);
            foreach (var (type, desc, groupType, groupDesc) in ExpandDdlTriggerEvents(ddl))
            {
                yield return [
                    objectId,
                    SqlValue.FromInt32(type),
                    SqlValue.FromString(eventTypeName, desc),
                    ddl.FirstForEvents?.Contains(type) == true ? trueBit : falseBit,
                    ddl.LastForEvents?.Contains(type) == true ? trueBit : falseBit,
                    groupType is int g ? SqlValue.FromInt32(g) : nullInt,
                    groupDesc is { } d ? SqlValue.FromString(eventTypeName, d) : nullDesc,
                    trueBit,
                ];
            }
        }
    }

    /// <summary>
    /// The events a database- or server-scope trigger's declared list expands
    /// to: a group name yields every leaf event in its transitive closure,
    /// tagged with the group, and an individual event (or <c>LOGON</c>, which
    /// the event-type catalog doesn't carry) one untagged row. A leaf two
    /// overlapping groups share is emitted once.
    /// </summary>
    private static IEnumerable<(int Type, string Desc, int? GroupType, string? GroupDesc)> ExpandDdlTriggerEvents(DdlTrigger trigger)
    {
        var emitted = new HashSet<int>();
        foreach (var eventName in trigger.EventTypes)
        {
            if (eventName.Equals("LOGON", StringComparison.OrdinalIgnoreCase))
            {
                if (emitted.Add(DdlTrigger.LogonEventType))
                    yield return (DdlTrigger.LogonEventType, "LOGON", null, null);
                continue;
            }
            if (!TriggerEventTypes.TryResolve(eventName, out var entry))
                continue;
            if (TriggerEventTypes.IsGroup(entry))
            {
                foreach (var leaf in TriggerEventTypes.LeafClosure(entry.Type))
                {
                    if (emitted.Add(leaf.Type))
                        yield return (leaf.Type, leaf.TypeName, entry.Type, entry.TypeName);
                }
            }
            else if (emitted.Add(entry.Type))
            {
                yield return (entry.Type, entry.TypeName, null, null);
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.server_triggers</c>: every server-scope trigger, with
    /// <c>parent_class</c> 100 (<c>SERVER</c>) and <c>parent_id</c> 0.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysServerTriggers(Parser.BatchContext batch, SqlType charTwo)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var trigger in batch.Connection.Simulation.ServerTriggers.All)
        {
            yield return [
                SqlValue.FromSystemName(trigger.Name),
                SqlValue.FromInt32(trigger.ObjectId),
                SqlValue.FromByte(100),
                SqlValue.FromNVarchar("SERVER"),
                SqlValue.FromInt32(0),
                SqlValue.FromChar(charTwo, trigger.ObjectTypeCode),
                SqlValue.FromNVarchar(trigger.ObjectTypeDescription),
                SqlValue.FromDateTime(trigger.CreateDate),
                SqlValue.FromDateTime(trigger.ModifyDate),
                falseBit,
                trigger.IsDisabled ? trueBit : falseBit,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.server_trigger_events</c>: one per event each
    /// server-scope trigger fires on, expanded as <c>sys.trigger_events</c>
    /// expands a database-scope trigger's list, with <c>is_first</c> /
    /// <c>is_last</c> reading <c>sp_settriggerorder … 'SERVER'</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysServerTriggerEvents(Parser.BatchContext batch)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var eventTypeName = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        foreach (var trigger in batch.Connection.Simulation.ServerTriggers.All)
        {
            var objectId = SqlValue.FromInt32(trigger.ObjectId);
            foreach (var (type, desc, groupType, groupDesc) in ExpandDdlTriggerEvents(trigger))
            {
                yield return [
                    objectId,
                    SqlValue.FromInt32(type),
                    SqlValue.FromString(eventTypeName, desc),
                    trueBit,
                    trigger.FirstForEvents?.Contains(type) == true ? trueBit : falseBit,
                    trigger.LastForEvents?.Contains(type) == true ? trueBit : falseBit,
                    groupType is int g ? SqlValue.FromInt32(g) : SqlValue.Null(SqlType.Int32),
                    groupDesc is { } d ? SqlValue.FromString(eventTypeName, d) : SqlValue.Null(eventTypeName),
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.server_sql_modules</c>: each T-SQL server-scope
    /// trigger's definition — NULL under <c>WITH ENCRYPTION</c> — and the
    /// server principal id its <c>EXECUTE AS</c> resolved to.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysServerSqlModules(Parser.BatchContext batch)
    {
        foreach (var trigger in batch.Connection.Simulation.ServerTriggers.All)
        {
            if (trigger.ClrEntry is not null)
                continue;
            yield return [
                SqlValue.FromInt32(trigger.ObjectId),
                trigger.DefinitionText is { } definition ? SqlValue.FromNVarchar(definition) : SqlValue.Null(SqlType.NVarchar),
                SqlValue.FromBoolean(trigger.UsesAnsiNulls),
                SqlValue.FromBoolean(trigger.UsesQuotedIdentifier),
                trigger.ExecuteAsPrincipalId is int principalId ? SqlValue.FromInt32(principalId) : SqlValue.Null(SqlType.Int32),
            ];
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
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in CatalogTables(schema, batch))
            {
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    yield return [
                        SqlValue.FromSystemName(fk.Name),
                        SqlValue.FromInt32(fk.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        fkType,
                        fkTypeDesc,
                        SqlValue.FromDateTime(fk.CreateDate),
                        SqlValue.FromDateTime(fk.ModifyDate),
                        falseBit,
                        falseBit,
                        falseBit,
                        SqlValue.FromInt32(fk.ReferencedTable.ObjectId),
                        SqlValue.FromInt32(ResolveForeignKeyIndexId(fk)),
                        fk.IsDisabled ? trueBit : falseBit,
                        fk.NotForReplication ? trueBit : falseBit,
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
    /// The <c>sys.foreign_keys.key_index_id</c> of <paramref name="fk"/>: the
    /// index id on the referenced table whose key columns are exactly the
    /// columns the FK targets, resolved through the same
    /// <see cref="HeapTable.IndexIdentities"/> allocation authority
    /// <c>sys.key_constraints.unique_index_id</c> reads. A NONCLUSTERED
    /// referenced PK reports whatever id it landed on (2 when a clustered
    /// index holds 1), and an FK pointing at a UNIQUE constraint reports that
    /// constraint's own id — probe-confirmed against SQL Server 2025. Falls
    /// back to 1 when nothing matches.
    /// </summary>
    internal static int ResolveForeignKeyIndexId(ForeignKey fk)
    {
        var referenced = fk.ReferencedTable;
        var wanted = fk.ReferencedColumnOrdinals;
        foreach (var identity in referenced.IndexIdentities())
        {
            if (identity.Constraint is { } key)
            {
                if (key.StorageOrdinals.Length != wanted.Length)
                    continue;
                var matched = true;
                foreach (var storageOrdinal in key.StorageOrdinals)
                {
                    if (Array.IndexOf(wanted, Parser.Expressions.IndexLookup.StorageOrdinalToFullOrdinal(referenced, storageOrdinal)) < 0)
                    {
                        matched = false;
                        break;
                    }
                }
                if (matched)
                    return identity.IndexId;
            }
            else if (identity.Index is { IsUnique: true } index && index.KeyColumns.Length == wanted.Length)
            {
                var matched = true;
                foreach (var keyColumn in index.KeyColumns)
                {
                    if (Array.IndexOf(wanted, keyColumn.ColumnOrdinal) < 0)
                    {
                        matched = false;
                        break;
                    }
                }
                if (matched)
                    return identity.IndexId;
            }
        }
        return 1;
    }

    /// <summary>
    /// Rows for <c>sys.foreign_key_columns</c>: one per (FK, column-pair).
    /// <c>parent_column_id</c> and <c>referenced_column_id</c> are the stable
    /// <c>sys.columns.column_id</c>s of the participating columns, not their
    /// positions — <c>DROP COLUMN</c> parts the two (probe-confirmed).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysForeignKeyColumns(Parser.BatchContext batch, Database database)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var table in CatalogTables(schema, batch))
            {
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
                    {
                        yield return [
                            SqlValue.FromInt32(fk.ObjectId),
                            SqlValue.FromInt32(i + 1),
                            SqlValue.FromInt32(fk.ChildTable.ObjectId),
                            SqlValue.FromInt32(fk.ChildTable.Columns[fk.ChildColumnOrdinals[i]].ColumnId),
                            SqlValue.FromInt32(fk.ReferencedTable.ObjectId),
                            SqlValue.FromInt32(fk.ReferencedTable.Columns[fk.ReferencedColumnOrdinals[i]].ColumnId),
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
    /// attached column's stable <c>sys.columns.column_id</c> when the CHECK is
    /// column-attached (inline); 0 for table-level.
    /// <c>uses_database_collation</c> is 1 — real reports 1 for every CHECK it
    /// creates, numeric-only predicates included (probe-confirmed).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysCheckConstraints(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var ckType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "C ");
        var ckTypeDesc = SqlValue.FromNVarchar("CHECK_CONSTRAINT");
        var sysSchemaId = SqlValue.FromInt32(Database.SysSchemaId);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in ConstraintHosts(schema, batch))
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
                                // The stable column_id, not the loop position:
                                // DROP COLUMN parts the two.
                                parentColumnId = table.Columns[i].ColumnId;
                                break;
                            }
                        }
                    }
                    yield return [
                        SqlValue.FromSystemName(ck.Name),
                        SqlValue.FromInt32(ck.ObjectId),
                        nullPrincipal,
                        table.IsTypeTable ? sysSchemaId : schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        ckType,
                        ckTypeDesc,
                        SqlValue.FromDateTime(ck.CreateDate),
                        SqlValue.FromDateTime(ck.ModifyDate),
                        table.IsTypeTable ? trueBit : falseBit, // is_ms_shipped
                        falseBit,
                        falseBit,
                        ck.IsDisabled ? trueBit : falseBit,
                        ck.NotForReplication ? trueBit : falseBit,
                        ck.IsNotTrusted ? trueBit : falseBit,
                        SqlValue.FromInt32(parentColumnId),
                        ck.Definition is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(ck.Definition),
                        trueBit, // uses_database_collation
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
        var sysSchemaId = SqlValue.FromInt32(Database.SysSchemaId);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in ConstraintHosts(schema, batch))
            {
                // unique_index_id must point each constraint at ITS backing
                // index in sys.indexes (DacFx's UQ query joins on it) — resolve
                // it through the shared index-id allocation authority rather
                // than hardcoding 1.
                var identities = table.IndexIdentities();
                foreach (var key in table.KeyConstraints.OrderBy(k => k.ObjectId))
                {
                    var isPk = key.Kind == KeyConstraintKind.PrimaryKey;
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
                        table.IsTypeTable ? sysSchemaId : schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        isPk ? pkType : uqType,
                        isPk ? pkTypeDesc : uqTypeDesc,
                        SqlValue.FromDateTime(key.CreateDate),
                        SqlValue.FromDateTime(key.ModifyDate),
                        table.IsTypeTable ? trueBit : falseBit, // is_ms_shipped — a type table's are the engine's
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
    /// <c>parent_column_id</c> is the bound column's stable
    /// <c>sys.columns.column_id</c>, not its position.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDefaultConstraints(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var dfType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "D ");
        var dfTypeDesc = SqlValue.FromNVarchar("DEFAULT_CONSTRAINT");
        var sysSchemaId = SqlValue.FromInt32(Database.SysSchemaId);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var (hostId, columns, positional, isTypeTable) in DeclaredColumnHosts(schema, batch))
            {
                for (var i = 0; i < columns.Length; i++)
                {
                    var col = columns[i];
                    if (col.DefaultConstraint is not { } df)
                        continue;
                    yield return [
                        SqlValue.FromSystemName(df.Name),
                        SqlValue.FromInt32(df.ObjectId),
                        nullPrincipal,
                        isTypeTable ? sysSchemaId : schemaId,
                        SqlValue.FromInt32(hostId),
                        dfType,
                        dfTypeDesc,
                        SqlValue.FromDateTime(df.CreateDate),
                        SqlValue.FromDateTime(df.ModifyDate),
                        isTypeTable ? trueBit : falseBit, // is_ms_shipped
                        falseBit,
                        falseBit,
                        SqlValue.FromInt32(positional ? i + 1 : col.ColumnId),
                        df.Definition is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(df.Definition),
                        df.IsSystemNamed ? trueBit : falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.DOMAIN_CONSTRAINTS</c>: each alias type
    /// carrying a bound rule, in alias order, the rule named in its own schema.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaDomainConstraints(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var no = SqlValue.FromVarchar(VarcharSqlType.Get(2, Collation.Baseline, Coercibility.Implicit), "NO");
        var aliases = database.Schemas.EnumerateValues()
            .SelectMany(schema => schema.AliasTypes.EnumerateValues().Select(alias => (schema, alias)))
            .OrderBy(pair => pair.alias.UserTypeId);
        foreach (var (schema, alias) in aliases)
        {
            if (alias.BoundRule is not { } rule)
                continue;
            yield return
            [
                catalog, SqlValue.FromSystemName(rule.Schema.Name), SqlValue.FromSystemName(rule.Name),
                catalog, SqlValue.FromSystemName(schema.Name), SqlValue.FromSystemName(alias.Name),
                no, no,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.TABLE_PRIVILEGES</c> (object-level) or
    /// <c>COLUMN_PRIVILEGES</c> (<paramref name="columnLevel"/>): every GRANT
    /// of SELECT, INSERT, UPDATE, DELETE or REFERENCES on a table or view,
    /// <c>IS_GRANTABLE</c> YES for one made WITH GRANT OPTION. A DENY, a
    /// schema-level grant and the implicit owner rights don't appear, and a
    /// column grant appears only in the column view (probed 2026-10-02 against
    /// SQL Server 2025).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaPrivileges(Parser.BatchContext batch, Database database, bool columnLevel)
    {
        var catalog = SqlValue.FromSystemName(database.Name);
        var privilegeType = VarcharSqlType.Get(10, Collation.Baseline, Coercibility.Implicit);
        var grantableType = VarcharSqlType.Get(3, Collation.Baseline, Coercibility.Implicit);
        var names = new Dictionary<int, string>();
        foreach (var (_, principal) in database.Principals)
            names[principal.PrincipalId] = principal.Name;
        var tables = new Dictionary<int, (Schema Schema, string Name, HeapColumn[] Columns)>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var table in CatalogTables(schema, batch))
                tables[table.ObjectId] = (schema, table.Name, table.Columns);
            foreach (var (_, view) in schema.Views)
                tables[view.ObjectId] = (schema, view.Name, view.OutputColumns);
        }
        foreach (var permission in database.Permissions)
        {
            var isColumnGrant = permission.MinorId != 0;
            if (permission.Class != 1 || isColumnGrant != columnLevel
                || permission.State is not (PermissionState.Grant or PermissionState.GrantWithGrantOption)
                || permission.DisplayName is not ("SELECT" or "INSERT" or "UPDATE" or "DELETE" or "REFERENCES")
                || !tables.TryGetValue(permission.MajorId, out var target))
            {
                continue;
            }
            string? columnName = null;
            if (columnLevel)
            {
                columnName = Array.Find(target.Columns, column => column.ColumnId == permission.MinorId)?.Name;
                if (columnName is null)
                    continue;
            }
            SqlValue Name(int principalId) =>
                names.TryGetValue(principalId, out var name) ? SqlValue.FromSystemName(name) : SqlValue.Null(SqlType.SystemName);
            yield return
            [
                Name(permission.GrantorPrincipalId),
                Name(permission.GranteePrincipalId),
                catalog,
                SqlValue.FromSystemName(target.Schema.Name),
                SqlValue.FromSystemName(target.Name),
                .. columnLevel ? (SqlValue[])[SqlValue.FromSystemName(columnName!)] : [],
                SqlValue.FromVarchar(privilegeType, permission.DisplayName),
                SqlValue.FromVarchar(grantableType, permission.State == PermissionState.GrantWithGrantOption ? "YES" : "NO"),
            ];
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.DOMAINS</c>, one per alias type and
    /// table type in <c>user_type_id</c> order, an alias type's base type
    /// described as <c>INFORMATION_SCHEMA.COLUMNS</c> describes a column of it
    /// and a table type's DATA_TYPE <c>table type</c> with no facets (probed
    /// 2026-09-26 against SQL Server 2025). DOMAIN_DEFAULT is the whole
    /// definition of the <c>CREATE DEFAULT</c> object bound to an alias type.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaDomains(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var nullInt32 = SqlValue.Null(SqlType.Int32);
        var nullInt16 = SqlValue.Null(SqlType.SmallInt);
        var nullByte = SqlValue.Null(SqlType.TinyInt);
        var tableTypeDataType = SqlValue.FromNVarchar("table type");
        var databaseCollation = SqlValue.FromSystemName(database.CollationName);

        var types = database.Schemas.EnumerateValues().SelectMany(schema => schema.AliasTypes.EnumerateValues().Select(a => (a.UserTypeId, schema, Alias: (AliasType?)a, a.Name))
            .Concat(schema.TableTypes.EnumerateValues().Select(t => (t.UserTypeId, schema, Alias: (AliasType?)null, t.Name))))
            .OrderBy(t => t.UserTypeId);
        foreach (var (_, schema, alias, name) in types)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            if (alias is null)
            {
                yield return [catalog, schemaName, SqlValue.FromSystemName(name), tableTypeDataType,
                    nullInt32, nullInt32, nullSysName, nullSysName, nullSysName, nullSysName, nullSysName, nullSysName,
                    nullByte, nullInt16, nullInt32, nullInt16, SqlValue.Null(SqlType.NVarchar)];
                continue;
            }

            var type = alias.UnderlyingType;
            var (charLength, octetLength, numericPrecision, numericRadix, numericScale, dateTimePrecision) =
                GetInformationSchemaColumnMetadata(new HeapColumn(name, type, alias.DeclaredMaxLength, alias.IsNullable));
            var isString = SqlType.IsCollatedString(type);
            yield return
            [
                catalog, schemaName, SqlValue.FromSystemName(name),
                IsoDataTypeName(type, alias.SpelledNumeric),
                charLength is int cl ? SqlValue.FromInt32(cl) : nullInt32,
                octetLength is int ol ? SqlValue.FromInt32(ol) : nullInt32,
                nullSysName, nullSysName,
                isString ? databaseCollation : nullSysName,
                nullSysName, nullSysName,
                !isString ? nullSysName : SqlValue.FromSystemName(SqlType.IsNationalStringCategory(type) ? "UNICODE" : CharacterSetName(database.Collation)),
                numericPrecision is byte np ? SqlValue.FromByte(np) : nullByte,
                numericRadix is int radix ? SqlValue.FromInt16((short)radix) : nullInt16,
                numericScale is int ns ? SqlValue.FromInt32(ns) : nullInt32,
                dateTimePrecision is short dp ? SqlValue.FromInt16(dp) : nullInt16,
                alias.BoundDefault?.DefinitionText is { } boundDefault ? SqlValue.FromNVarchar(boundDefault) : SqlValue.Null(SqlType.NVarchar),
            ];
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.TABLE_CONSTRAINTS</c>: one row per
    /// PRIMARY KEY / UNIQUE (<see cref="HeapTable.KeyConstraints"/>), FOREIGN
    /// KEY (<see cref="HeapTable.OutgoingForeignKeys"/>), and CHECK
    /// (<see cref="HeapTable.CheckConstraints"/>) constraint — the same
    /// domain-object traversal the <c>sys.key_constraints</c> /
    /// <c>sys.foreign_keys</c> / <c>sys.check_constraints</c> generators use.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaTableConstraints(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var primaryKey = SqlValue.FromVarchar("PRIMARY KEY");
        var unique = SqlValue.FromVarchar("UNIQUE");
        var foreignKey = SqlValue.FromVarchar("FOREIGN KEY");
        var check = SqlValue.FromVarchar("CHECK");
        var no = SqlValue.FromVarchar("NO");
        var sysSchemaName = SqlValue.FromSystemName("sys");
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var table in CatalogTables(schema, batch))
            {
                var tableName = SqlValue.FromSystemName(table.Name);
                SqlValue[] Row(string constraintName, SqlValue constraintType) =>
                [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(constraintName),
                    catalog,
                    schemaName,
                    tableName,
                    constraintType,
                    no,
                    no,
                ];
                foreach (var key in table.KeyConstraints.OrderBy(k => k.ObjectId))
                    yield return Row(key.Name, key.Kind == KeyConstraintKind.PrimaryKey ? primaryKey : unique);
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                    yield return Row(fk.Name, foreignKey);
                foreach (var ck in table.CheckConstraints.OrderBy(c => c.ObjectId))
                    yield return Row(ck.Name, check);
            }

            // A table type's constraints list under the sys schema with no
            // table named (probed 2026-09-26 against SQL Server 2025).
            foreach (var tableType in schema.TableTypes.EnumerateValues().OrderBy(t => t.ObjectId))
            {
                var shape = tableType.CatalogShape;
                SqlValue[] TypeRow(string constraintName, SqlValue constraintType) =>
                [
                    catalog,
                    sysSchemaName,
                    SqlValue.FromSystemName(constraintName),
                    catalog,
                    SqlValue.Null(SqlType.NVarchar),
                    SqlValue.Null(SqlType.SystemName),
                    constraintType,
                    no,
                    no,
                ];
                foreach (var key in shape.KeyConstraints.OrderBy(k => k.ObjectId))
                    yield return TypeRow(key.Name, key.Kind == KeyConstraintKind.PrimaryKey ? primaryKey : unique);
                foreach (var ck in shape.CheckConstraints.OrderBy(c => c.ObjectId))
                    yield return TypeRow(ck.Name, check);
            }
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.KEY_COLUMN_USAGE</c>: one row per column
    /// participating in a PRIMARY KEY / UNIQUE / FOREIGN KEY constraint, in key
    /// order (<c>ORDINAL_POSITION</c> 1..N). CHECK constraints don't appear.
    /// PK / UNIQUE key columns resolve through <see cref="StorageOrdinalToColumnId"/>
    /// (the shared storage-ordinal → column_id authority); FK child columns use
    /// the full ordinals <see cref="ForeignKey.ChildColumnOrdinals"/> carries.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaKeyColumnUsage(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var table in CatalogTables(schema, batch))
            {
                var tableName = SqlValue.FromSystemName(table.Name);
                SqlValue[] Row(string constraintName, string columnName, int ordinal) =>
                [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(constraintName),
                    catalog,
                    schemaName,
                    tableName,
                    SqlValue.FromSystemName(columnName),
                    SqlValue.FromInt32(ordinal),
                ];
                foreach (var key in table.KeyConstraints.OrderBy(k => k.ObjectId))
                {
                    for (var i = 0; i < key.StorageOrdinals.Length; i++)
                    {
                        var keyColumn = table.StoredColumns[key.StorageOrdinals[i]];
                        yield return Row(key.Name, keyColumn.Name, i + 1);
                    }
                }
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
                        yield return Row(fk.Name, table.Columns[fk.ChildColumnOrdinals[i]].Name, i + 1);
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE</c>: one row per
    /// (constraint, column) pair. PRIMARY KEY / UNIQUE name their key columns
    /// and a FOREIGN KEY its child columns, the same sets
    /// <see cref="EnumerateInformationSchemaKeyColumnUsage"/> reports; a CHECK
    /// names the columns its predicate reads.
    /// <para>
    /// A CHECK's columns come from the declaring column for the inline form and
    /// otherwise from matching the stored definition text against the parent
    /// table's bracketed column names — an approximation of real's expression
    /// walk, which over-reports a column whose bracketed name also appears
    /// inside a string literal of the predicate.
    /// </para>
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaConstraintColumnUsage(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var table in CatalogTables(schema, batch))
            {
                var tableName = SqlValue.FromSystemName(table.Name);
                SqlValue[] Row(string constraintName, string columnName) =>
                [
                    catalog,
                    schemaName,
                    tableName,
                    SqlValue.FromSystemName(columnName),
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(constraintName),
                ];
                foreach (var key in table.KeyConstraints.OrderBy(k => k.ObjectId))
                {
                    foreach (var storageOrdinal in key.StorageOrdinals)
                        yield return Row(key.Name, table.StoredColumns[storageOrdinal].Name);
                }
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    foreach (var ordinal in fk.ChildColumnOrdinals)
                        yield return Row(fk.Name, table.Columns[ordinal].Name);
                }
                foreach (var ck in table.CheckConstraints.OrderBy(c => c.ObjectId))
                {
                    foreach (var columnName in CheckConstraintColumns(database, table, ck))
                        yield return Row(ck.Name, columnName);
                }
            }

            // A table type contributes only its CHECKs, named against its type
            // table in the sys schema (probed 2026-09-26 against SQL Server 2025).
            foreach (var tableType in schema.TableTypes.EnumerateValues().OrderBy(t => t.ObjectId))
            {
                var shape = tableType.CatalogShape;
                var sysSchema = SqlValue.FromSystemName("sys");
                foreach (var ck in shape.CheckConstraints.OrderBy(c => c.ObjectId))
                {
                    foreach (var columnName in CheckConstraintColumns(database, shape, ck))
                    {
                        yield return
                        [
                            catalog,
                            sysSchema,
                            SqlValue.FromSystemName(shape.Name),
                            SqlValue.FromSystemName(columnName),
                            catalog,
                            sysSchema,
                            SqlValue.FromSystemName(ck.Name),
                        ];
                    }
                }
            }
        }
    }

    /// <summary>
    /// The column names a CHECK constraint reads: the declaring column for the
    /// inline form, else every column of <paramref name="table"/> whose
    /// bracketed name appears in the constraint's stored definition text.
    /// </summary>
    private static IEnumerable<string> CheckConstraintColumns(Database database, HeapTable table, CheckConstraint check)
    {
        if (check.InlineColumn is { } inlineColumn)
        {
            yield return inlineColumn;
            yield break;
        }

        var definition = check.Definition;
        if (definition is null)
            yield break;

        // The stored text brackets every column reference (`[b]<>'x'`), so a
        // bare-name match would also hit a column whose name is a fragment of
        // a keyword or of another name (`a` inside `AND`).
        var comparison = database.Collation.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var column in table.Columns)
        {
            if (definition.Contains($"[{column.Name.Replace("]", "]]", StringComparison.Ordinal)}]", comparison))
                yield return column.Name;
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.CONSTRAINT_TABLE_USAGE</c>: one row per
    /// PRIMARY KEY / UNIQUE / FOREIGN KEY / CHECK constraint, naming the table
    /// it sits on.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaConstraintTableUsage(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var table in CatalogTables(schema, batch))
            {
                var tableName = SqlValue.FromSystemName(table.Name);
                SqlValue[] Row(string constraintName) =>
                [
                    catalog,
                    schemaName,
                    tableName,
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(constraintName),
                ];
                foreach (var key in table.KeyConstraints.OrderBy(k => k.ObjectId))
                    yield return Row(key.Name);
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                    yield return Row(fk.Name);
                foreach (var ck in table.CheckConstraints.OrderBy(c => c.ObjectId))
                    yield return Row(ck.Name);
            }
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS</c>: one row per
    /// FOREIGN KEY. <c>UNIQUE_CONSTRAINT_NAME</c> resolves to the PRIMARY KEY /
    /// UNIQUE constraint on the referenced table whose column set matches the
    /// FK's referenced columns; <c>UPDATE_RULE</c> / <c>DELETE_RULE</c> map the
    /// FK's referential actions to the ISO spaced wording.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaReferentialConstraints(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var simple = SqlValue.FromVarchar("SIMPLE");
        var nullName = SqlValue.Null(SqlType.SystemName);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var table in CatalogTables(schema, batch))
            {
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    var referencedSchema = SchemaNameForTable(database, fk.ReferencedTable);
                    var uniqueName = ResolveReferencedKeyName(fk);
                    yield return [
                        catalog,
                        schemaName,
                        SqlValue.FromSystemName(fk.Name),
                        catalog,
                        SqlValue.FromSystemName(referencedSchema),
                        uniqueName is null ? nullName : SqlValue.FromSystemName(uniqueName),
                        simple,
                        SqlValue.FromVarchar(ReferentialActionRule(fk.UpdateAction)),
                        SqlValue.FromVarchar(ReferentialActionRule(fk.DeleteAction)),
                    ];
                }
            }
        }
    }

    private static int StorageOrdinalToFullOrdinal(HeapTable table, int storageOrdinal) =>
        Parser.Expressions.IndexLookup.StorageOrdinalToFullOrdinal(table, storageOrdinal);

    /// <summary>
    /// The name of the PRIMARY KEY / UNIQUE constraint on <paramref name="fk"/>'s
    /// referenced table whose column set equals the FK's referenced columns,
    /// else the unique index the FK rests on — real names that index here too
    /// (probed 2026-10-02 against SQL Server 2025) — or <c>null</c> when
    /// neither matches.
    /// </summary>
    private static string? ResolveReferencedKeyName(ForeignKey fk)
    {
        var referenced = fk.ReferencedTable;
        var wanted = fk.ReferencedColumnOrdinals.OrderBy(o => o).ToArray();
        foreach (var key in referenced.KeyConstraints)
        {
            if (key.StorageOrdinals.Length != wanted.Length)
                continue;
            // ReferencedColumnOrdinals are full-column ordinals (positions in
            // HeapTable.Columns), so the key's storage ordinals have to come
            // back as ordinals too — not column_ids, which stop tracking
            // position once a column is dropped.
            var keyFull = key.StorageOrdinals.Select(o => StorageOrdinalToFullOrdinal(referenced, o)).OrderBy(o => o);
            if (keyFull.SequenceEqual(wanted))
                return key.Name;
        }
        foreach (var index in referenced.Indexes)
        {
            if (index.IsUnique && index.KeyFullOrdinals.Order().SequenceEqual(wanted))
                return index.Name;
        }
        return null;
    }

    private static string SchemaNameForTable(Database database, HeapTable table)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.SchemaId == table.SchemaId)
                return schema.Name;
        }
        return Database.DefaultSchemaName;
    }

    /// <summary>
    /// Maps a <see cref="ReferentialAction"/> to the ISO
    /// <c>REFERENTIAL_CONSTRAINTS.UPDATE_RULE</c> / <c>DELETE_RULE</c> wording
    /// (spaced), distinct from <see cref="ReferentialActionDescription"/>'s
    /// underscore <c>sys.foreign_keys</c> desc form.
    /// </summary>
    private static string ReferentialActionRule(ReferentialAction action) => action switch
    {
        ReferentialAction.NoAction => "NO ACTION",
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };

    /// <summary>
    /// The <c>Microsoft.SqlServer.Types</c> row real SQL Server always carries
    /// in <c>sys.assemblies</c> (assembly_id 1, owned by the <c>sys</c>
    /// principal, UNSAFE_ACCESS). It backs the three CLR system types
    /// <c>sys.assembly_types</c> projects, so without it that view's natural
    /// join to <c>sys.assemblies</c> yields nothing.
    /// </summary>
    private static SqlValue[] SystemAssemblyRow()
    {
        // Real stamps the resource-database build date here; the simulator has
        // no equivalent, so it reports the SQL Server 2025 RTM date rather than
        // a per-run value that would make the row look user-created.
        var stamp = SqlValue.FromDateTime(new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        return
        [
            SqlValue.FromSystemName("Microsoft.SqlServer.Types"),
            SqlValue.FromInt32(4),
            SqlValue.FromInt32(1),
            SqlValue.FromNVarchar("microsoft.sqlserver.types, version=17.0.0.0, culture=neutral, publickeytoken=89845dcd8080cc91, processorarchitecture=msil"),
            SqlValue.FromByte(3),
            SqlValue.FromNVarchar("UNSAFE_ACCESS"),
            SqlValue.FromBoolean(true),
            stamp,
            stamp,
            SqlValue.FromBoolean(false),
        ];
    }

    /// <summary>Rows for <c>sys.assemblies</c>.</summary>
    private static IEnumerable<SqlValue[]> EnumerateAssemblies(Database database)
    {
        yield return SystemAssemblyRow();

        foreach (var assembly in database.Assemblies.EnumerateValues().OrderBy(a => a.AssemblyId))
        {
            yield return
            [
                SqlValue.FromSystemName(assembly.Name),
                SqlValue.FromInt32(assembly.PrincipalId),
                SqlValue.FromInt32(assembly.AssemblyId),
                SqlValue.FromNVarchar(assembly.ClrName),
                SqlValue.FromByte((byte)assembly.PermissionSet),
                SqlValue.FromNVarchar(assembly.PermissionSetDescription),
                SqlValue.FromBoolean(true),
                SqlValue.FromDateTime(assembly.CreateDate),
                SqlValue.FromDateTime(assembly.ModifyDate),
                SqlValue.FromBoolean(true),
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.assembly_files</c>. Real also carries a row for the
    /// system assembly; the simulator has no bytes to project for that one, so
    /// only user assemblies appear.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateAssemblyFiles(Database database)
    {
        foreach (var assembly in database.Assemblies.EnumerateValues().OrderBy(a => a.AssemblyId))
        {
            yield return
            [
                SqlValue.FromInt32(assembly.AssemblyId),
                SqlValue.FromNVarchar(assembly.Name),
                SqlValue.FromInt32(1),
                SqlValue.FromVarbinary(assembly.Content),
                SqlValue.FromVarbinary(System.Security.Cryptography.SHA256.HashData(assembly.Content)),
                SqlValue.FromVarbinary(System.Security.Cryptography.SHA512.HashData(assembly.Content)),
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.assembly_modules</c> — one per CLR routine: the
    /// functions of every kind and the procedures. An aggregate names a class
    /// alone, so its <c>assembly_method</c> is NULL (probed 2026-09-28 against
    /// SQL Server 2025).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateAssemblyModules(Database database)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, function) in schema.Functions)
            {
                if (function is ClrFunction clr)
                    yield return AssemblyModuleRow(clr.ObjectId, clr.Entry);
            }

            foreach (var (_, procedure) in schema.Procedures)
            {
                if (procedure.ClrEntry is { } entry)
                    yield return AssemblyModuleRow(procedure.ObjectId, entry);
            }

            foreach (var (_, trigger) in schema.Triggers)
            {
                if (trigger.ClrEntry is { } entry)
                    yield return AssemblyModuleRow(trigger.ObjectId, entry);
            }
        }

        foreach (var (_, ddl) in database.DdlTriggers)
        {
            if (ddl.ClrEntry is { } entry)
                yield return AssemblyModuleRow(ddl.ObjectId, entry);
        }
    }

    private static SqlValue[] AssemblyModuleRow(int objectId, ClrEntryPoint entry) =>
    [
        SqlValue.FromInt32(objectId),
        SqlValue.FromInt32(entry.Assembly.AssemblyId),
        SqlValue.FromNVarchar(entry.ClassName),
        entry.MethodName is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(entry.MethodName),
        // Real reports 0 here for a routine created without RETURNS NULL ON
        // NULL INPUT (probe-confirmed); the simulator doesn't accept that
        // option on a CLR routine, so the column is constant.
        SqlValue.FromBoolean(false),
        SqlValue.Null(SqlType.Int32),
    ];
}
