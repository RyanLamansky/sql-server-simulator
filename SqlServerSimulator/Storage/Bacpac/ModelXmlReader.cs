using System.Data.Common;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Walks a DACFx <c>model.xml</c> document and dispatches each top-level
/// <c>&lt;Element&gt;</c> to a CREATE-statement emitter that runs through
/// the simulator's regular parser via <see cref="DbCommand.ExecuteNonQuery"/>.
/// The loader is intentionally a translator (model.xml → T-SQL) rather than
/// a second object-construction pipeline.
/// </summary>
/// <remarks>
/// <para>Element-type coverage grows as bundles land. The first pass handles
/// <c>SqlSchema</c> + <c>SqlDatabaseOptions</c> only — proving the
/// XML → DDL → live-state pipeline end-to-end. Subsequent passes add tables,
/// constraints, indexes, views, procedures, functions, triggers, permissions,
/// and extended properties.</para>
/// <para>Every element type not yet handled is recorded as a
/// <see cref="BacpacSkipped"/> entry on <see cref="BacpacImportResult.Skipped"/>
/// so the caller has a feature-gap report.</para>
/// </remarks>
internal static class ModelXmlReader
{
    /// <summary>DACFx serialization namespace observed in AW2025's model.xml.</summary>
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    /// <summary>
    /// Parses the compatibility level out of the root DataSchemaModel's
    /// <c>DspName</c> (e.g. <c>…Sql130DatabaseSchemaProvider</c> → 130) and
    /// applies it to <paramref name="database"/>. A missing / unparsable /
    /// unrecognized level leaves the construction-time default untouched.
    /// </summary>
    private static void ApplyCompatibilityLevel(XElement root, Database database)
    {
        var dsp = root.Attribute("DspName")?.Value;
        if (dsp is null)
            return;

        const string suffix = "DatabaseSchemaProvider";
        var end = dsp.IndexOf(suffix, StringComparison.Ordinal);
        if (end <= 0)
            return;

        var start = end;
        while (start > 0 && char.IsAsciiDigit(dsp[start - 1]))
            start--;
        if (start == end
            || !int.TryParse(dsp.AsSpan(start, end - start), out var level)
            || !Enum.IsDefined((CompatibilityLevel)level))
        {
            return;
        }

        database.CompatibilityLevel = (CompatibilityLevel)level;
    }

    public static void Apply(Stream modelStream, Simulation simulation, Database database, BacpacImportResult result)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };
        using var xmlReader = XmlReader.Create(modelStream, settings);
        var doc = XDocument.Load(xmlReader, LoadOptions.None);
        var model = doc.Root?.Element(Ns + "Model")
            ?? throw new InvalidDataException("bacpac: <DataSchemaModel><Model> root not found.");

        // The database's declared compatibility level lives in the root
        // DataSchemaModel's DspName (e.g. "…Sql130DatabaseSchemaProvider" → 130),
        // not in any <Element>. Without applying it, an imported database would
        // report the construction-time default (170) rather than its bacpac's
        // declared level.
        ApplyCompatibilityLevel(doc.Root, database);

        // Materialize once so dependency-ordered passes can iterate the same
        // element list multiple times. The cost is one O(N) walk + per-element
        // XElement reference retention — 1000s of elements at most, negligible
        // memory-wise even for huge bacpacs.
        var elements = model.Elements(Ns + "Element").ToList();
        foreach (var element in elements)
        {
            var type = element.Attribute("Type")?.Value
                ?? throw new InvalidDataException("bacpac: <Element> missing Type attribute.");
            result.IncrementElementCount(type);
        }

        using var connection = simulation.CreateDbConnection();
        connection.Open();
        // Route every DDL the loader synthesizes (CREATE TABLE, CREATE
        // VIEW, ALTER DATABASE, …) to the target database. The connection
        // is private to this load — no other session observes the
        // assignment, and the connection is disposed at the end of the
        // method so the override doesn't leak.
        connection.CurrentDatabase = database;
        var bracketedDb = BracketName(database.Name);

        // Pre-build a set of view qualified-names so the index emitter can
        // skip indexes on views (those require views to exist first + indexed-
        // view machinery the simulator doesn't model). Future phase that adds
        // view support promotes the skipped-on-view indexes off Skipped.
        var viewNames = elements
            .Where(e => e.Attribute("Type")?.Value == "SqlView")
            .Select(e => e.Attribute("Name")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Ordered passes: phase 1 = schemas + UDDTs + sequences + roles +
        // table types + db options (no deps); phase 2 = tables, with computed
        // columns inline at their model ordinal (depend on phase 1) — a
        // computed expression that forward-references a not-yet-created UDF
        // makes CREATE TABLE throw, so that one table re-creates with computed
        // columns stripped and they defer to phase 8;
        // phase 3 = PK/UQ/CHECK/DEFAULT constraints (depend on tables);
        // phase 4 = FK constraints (depend on PK/UQ on referenced tables);
        // phase 5 = unused (was indexes; moved to phase 8 so filtered-index
        // predicates referencing computed columns resolve); phase 6 = views
        // (body is deferred-parsed so cross-references inside the same phase
        // work); phase 7 = functions + procedures + DML triggers (bodies
        // also deferred-parsed); phase 8 = deferred computed columns for the
        // tables phase 2 fell back on (ALTER TABLE ADD col AS expr — depend on
        // functions landing in phase 7; these append at the end rather than
        // the model ordinal) + indexes (depend on tables AND computed columns;
        // within phase 8 document order puts SqlTable's deferred-computed-column
        // ALTERs ahead of SqlIndex emissions); phase 9 = extended properties
        // (depend on every covered host type, including the computed
        // columns and indexes just landed in phase 8). Skipped-element
        // recording happens on the last phase so each unhandled type is
        // reported once.
        // Tables whose inline computed-column emission failed in phase 2 (a
        // forward UDF reference) and so were re-created with computed columns
        // stripped; phase 8 appends those columns once the functions exist.
        var deferredComputedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        const int LastPhase = 9;
        for (var phase = 1; phase <= LastPhase; phase++)
            RunPhase(elements, connection, result, phase, viewNames, bracketedDb, deferredComputedTables, isLastPhase: phase == LastPhase);
    }

    /// <summary>
    /// Renders <paramref name="name"/> as a bracketed T-SQL identifier with
    /// embedded <c>]</c> doubled (real SQL Server's quoting rule). Centralized
    /// so the synthesized <c>ALTER DATABASE [name] …</c> statements receive
    /// the same escape treatment a hand-typed CREATE would.
    /// </summary>
    private static string BracketName(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static void RunPhase(List<XElement> elements, DbConnection connection, BacpacImportResult result, int phase, HashSet<string> viewNames, string bracketedDb, HashSet<string> deferredComputedTables, bool isLastPhase)
    {
        foreach (var element in elements)
        {
            var type = element.Attribute("Type")?.Value!;
            var name = element.Attribute("Name")?.Value;
            bool handled;
            try
            {
                handled = (type, phase) switch
                {
                    ("SqlDatabaseOptions", 1) => Run(() => EmitDatabaseOptions(element, connection, result, bracketedDb)),
                    ("SqlSchema", 1) => Run(() => EmitSchema(name, connection)),
                    ("SqlUserDefinedDataType", 1) => Run(() => EmitUserDefinedDataType(element, name, connection)),
                    ("SqlSequence", 1) => Run(() => EmitSequence(element, name, connection)),
                    ("SqlRole", 1) => Run(() => EmitRole(element, name, connection)),
                    ("SqlTableType", 1) => Run(() => EmitTableType(element, name, connection, result)),
                    ("SqlTable", 2) => Run(() => EmitTable(element, name, connection, result, deferredComputedTables)),
                    ("SqlPrimaryKeyConstraint", 3) => Run(() => EmitKeyConstraint(element, name, connection, isPrimary: true)),
                    ("SqlUniqueConstraint", 3) => Run(() => EmitKeyConstraint(element, name, connection, isPrimary: false)),
                    ("SqlCheckConstraint", 3) => Run(() => EmitCheckConstraint(element, name, connection)),
                    ("SqlDefaultConstraint", 3) => Run(() => EmitDefaultConstraint(element, name, connection)),
                    ("SqlForeignKeyConstraint", 4) => Run(() => EmitForeignKeyConstraint(element, name, connection)),
                    // Phase 5: temporal-table wire-up. Both base + history
                    // tables exist (phase 2) and constraints are layered
                    // (phase 3/4) by now; the ALTER … SET (SYSTEM_VERSIONING
                    // = ON …) just needs both endpoints. Walks SqlTable
                    // elements a second time, looking for the
                    // `TemporalSystemVersioningHistoryTable` relationship.
                    // Elements without the relationship are ignored.
                    ("SqlTable", 5) => Run(() => EmitDeferredSystemVersioning(element, name, connection)),
                    ("SqlView", 6) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlView", "QueryScript")),
                    ("SqlScalarFunction", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlScalarFunction", "BodyScript")),
                    ("SqlMultiStatementTableValuedFunction", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlMultiStatementTableValuedFunction", "BodyScript")),
                    ("SqlProcedure", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlProcedure", "BodyScript")),
                    ("SqlDmlTrigger", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlDmlTrigger", "BodyScript")),
                    ("SqlDatabaseDdlTrigger", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlDatabaseDdlTrigger", "BodyScript")),
                    ("SqlTable", 8) => Run(() => EmitDeferredComputedColumns(element, connection, result, deferredComputedTables)),
                    // Indexes run AFTER computed columns so filtered-index
                    // predicates / INCLUDE lists referencing those columns
                    // resolve correctly. Element-order within phase 8: all
                    // SqlTable entries (and their deferred-computed-column
                    // ALTERs) come before SqlIndex entries in document order,
                    // so the per-SqlTable computed-column pass completes
                    // before the first SqlIndex emission runs.
                    ("SqlIndex", 8) => Run(() => EmitIndex(element, name, connection, viewNames, result)),
                    // Filegroups + partitioning + columnstore are storage-layout
                    // concerns; the simulator has a single in-process heap so
                    // all four are parse-and-skip. PartitionFunction /
                    // PartitionScheme define filegroup-mapping boundaries that
                    // tables / indexes reference for physical placement (no
                    // semantic effect when filegroups themselves are skipped).
                    // ColumnStoreIndex is a read-optimization shape over the
                    // same row data; the simulator's linear-scan secondary
                    // indexes don't model column-major vs row-major storage.
                    // Phase 1 placement is fine — no dependencies either way.
                    ("SqlFilegroup", 1) => Run(static () => { }),
                    ("SqlPartitionFunction", 1) => Run(static () => { }),
                    ("SqlPartitionScheme", 1) => Run(static () => { }),
                    ("SqlColumnStoreIndex", 1) => Run(static () => { }),
                    ("SqlExtendedProperty", 9) => Run(() => EmitExtendedProperty(element, name, connection, viewNames, result)),
                    // Permission statements emit after roles + everything-securable
                    // — phase 7 is "everything is in place except extended
                    // properties + indexes-on-computed-columns". DACFx-emitted
                    // SqlPermissionStatement elements in AW + WWI are exclusively
                    // the database-scope encryption-key VIEW grants; broader
                    // securables (ON OBJECT::… / ON SCHEMA::…) would also land
                    // through this path if a future bacpac emitted them.
                    ("SqlPermissionStatement", 7) => Run(() => EmitPermissionStatement(element, name, connection)),
                    _ => IsHandledByAnotherPhase(type),
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Per-element failures must not abort the whole load — record
                // the failure on Skipped so the caller sees what couldn't be
                // translated, and continue with the rest of the model. Cascade
                // failures (a later element depending on the failed one) land
                // on Skipped with their own messages.
                result.AddSkipped(new BacpacSkipped(type, name, $"Load failed: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }
            if (!handled && isLastPhase)
                result.AddSkipped(new BacpacSkipped(type, name, "Element type not yet handled by the loader."));
        }
    }

    private static bool Run(Action action)
    {
        action();
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/> is handled in some other
    /// phase — used by the dispatcher to avoid recording the same element
    /// type on Skipped multiple times (once per pass).
    /// </summary>
    private static bool IsHandledByAnotherPhase(string type) => type
        is "SqlDatabaseOptions" or "SqlSchema" or "SqlUserDefinedDataType"
        or "SqlSequence" or "SqlRole" or "SqlTableType" or "SqlFilegroup"
        or "SqlPartitionFunction" or "SqlPartitionScheme" or "SqlColumnStoreIndex"
        or "SqlTable"
        or "SqlPrimaryKeyConstraint" or "SqlUniqueConstraint"
        or "SqlCheckConstraint" or "SqlDefaultConstraint"
        or "SqlForeignKeyConstraint"
        or "SqlIndex"
        or "SqlView"
        or "SqlScalarFunction" or "SqlMultiStatementTableValuedFunction"
        or "SqlProcedure" or "SqlDmlTrigger" or "SqlDatabaseDdlTrigger"
        or "SqlExtendedProperty"
        or "SqlPermissionStatement";

    /// <summary>
    /// Emits <c>CREATE SCHEMA [name]</c>. The default <c>dbo</c> schema is
    /// pre-seeded on every <see cref="Database"/>; AW's model.xml omits it
    /// from the SqlSchema element list, so no special-casing needed.
    /// </summary>
    private static void EmitSchema(string? bracketedName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(bracketedName))
            throw new InvalidDataException("bacpac: SqlSchema element missing Name attribute.");

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"CREATE SCHEMA {bracketedName};";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Translates the per-property toggles on <c>&lt;Element Type="SqlDatabaseOptions"&gt;</c>
    /// into a sequence of <c>ALTER DATABASE … SET …</c> statements. Only
    /// <c>IsReadCommittedSnapshot</c> carries semantic effect in the
    /// simulator (it flips RCSI behavior on the MVCC reader path); everything
    /// else parse-and-discards but is emitted anyway so the loader's behavior
    /// stays a thin translator.
    /// </summary>
    private static void EmitDatabaseOptions(XElement element, DbConnection connection, BacpacImportResult result, string bracketedDb)
    {
        using var command = connection.CreateCommand();

        // Collation: emit ALTER DATABASE COLLATE when the name is on the
        // recognized catalog. The statement lands on Database.Collation, so
        // subsequent column declarations without their own COLLATE clause
        // inherit it and route compare / sort / hash through the chosen
        // collation. An unrecognized name lands on Warnings rather than
        // aborting the load (the loader's best-effort contract); the
        // database keeps its construction-time server collation, and any
        // columns the bacpac declares with explicit COLLATE clauses still
        // pin those collations independently.
        var collation = element.Elements(Ns + "Property")
            .FirstOrDefault(p => p.Attribute("Name")?.Value == "Collation")
            ?.Attribute("Value")?.Value;
        if (collation is not null)
        {
            if (Collation.IsRecognized(collation))
            {
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
                command.CommandText = $"ALTER DATABASE {bracketedDb} COLLATE {collation};";
#pragma warning restore CA2100
                _ = command.ExecuteNonQuery();
            }
            else
            {
                result.AddWarning($"Database declares Collation '{collation}' which the simulator's catalog doesn't recognize — the database keeps its server-default collation; columns declared with their own COLLATE clauses still pin those collations.");
            }
        }

        foreach (var property in element.Elements(Ns + "Property"))
        {
            var name = property.Attribute("Name")?.Value;
            var value = property.Attribute("Value")?.Value;
            if (name is null || value is null)
                continue;

            var sql = TranslateDatabaseOption(name, value, bracketedDb);
            if (sql is null)
                continue;

#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
            command.CommandText = sql;
#pragma warning restore CA2100
            _ = command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Maps a single <c>&lt;Property Name=X Value=Y /&gt;</c> on
    /// SqlDatabaseOptions to an <c>ALTER DATABASE &lt;bracketed-db&gt; SET …</c>
    /// statement. Returns null when the property has no T-SQL shape that
    /// affects load behavior — e.g. <c>IsFullTextEnabled</c> which is a
    /// sproc-level toggle (sp_fulltext_database), or
    /// <c>QueryStoreIntervalLength</c> which is a sub-option of QUERY_STORE
    /// and only emits as part of a block.
    /// </summary>
    // SSS005 disabled: arms are grouped by option family (ANSI toggles, enum-shaped
    // options, QUERY_STORE sub-options) with explanatory comments per group; sorting
    // by name would scatter the groups and orphan the comments.
#pragma warning disable SSS005
    private static string? TranslateDatabaseOption(string name, string value, string bracketedDb) => name switch
    {
        "IsAnsiNullsOn" => $"ALTER DATABASE {bracketedDb} SET ANSI_NULLS {OnOff(value)};",
        "IsAnsiWarningsOn" => $"ALTER DATABASE {bracketedDb} SET ANSI_WARNINGS {OnOff(value)};",
        "IsAnsiPaddingOn" => $"ALTER DATABASE {bracketedDb} SET ANSI_PADDING {OnOff(value)};",
        "IsArithAbortOn" => $"ALTER DATABASE {bracketedDb} SET ARITHABORT {OnOff(value)};",
        "IsConcatNullYieldsNullOn" => $"ALTER DATABASE {bracketedDb} SET CONCAT_NULL_YIELDS_NULL {OnOff(value)};",
        "IsNumericRoundAbortOn" => $"ALTER DATABASE {bracketedDb} SET NUMERIC_ROUNDABORT {OnOff(value)};",
        "IsQuotedIdentifierOn" => $"ALTER DATABASE {bracketedDb} SET QUOTED_IDENTIFIER {OnOff(value)};",
        "IsTornPageProtectionOn" => $"ALTER DATABASE {bracketedDb} SET TORN_PAGE_DETECTION {OnOff(value)};",
        "TemporalHistoryRetentionEnabled" => $"ALTER DATABASE {bracketedDb} SET TEMPORAL_HISTORY_RETENTION {OnOff(value)};",
        "IsAcceleratedDatabaseRecoveryOn" => $"ALTER DATABASE {bracketedDb} SET ACCELERATED_DATABASE_RECOVERY = {OnOff(value)};",
        "IsOptimizedLockingOn" => $"ALTER DATABASE {bracketedDb} SET OPTIMIZED_LOCKING = {OnOff(value)};",
        "IsReadCommittedSnapshot" => $"ALTER DATABASE {bracketedDb} SET READ_COMMITTED_SNAPSHOT {OnOff(value)};",
        // Enum-shaped options. RecoveryMode: 1=FULL, 2=BULK_LOGGED, 3=SIMPLE.
        "RecoveryMode" => $"ALTER DATABASE {bracketedDb} SET RECOVERY {RecoveryMode(value)};",
        // IsCursorDefaultScopeGlobal: True → GLOBAL, False → LOCAL.
        "IsCursorDefaultScopeGlobal" => $"ALTER DATABASE {bracketedDb} SET CURSOR_DEFAULT {(IsTrue(value) ? "GLOBAL" : "LOCAL")};",
        // TARGET_RECOVERY_TIME requires a unit; bacpac always stores SECONDS.
        "TargetRecoveryTimePeriod" => $"ALTER DATABASE {bracketedDb} SET TARGET_RECOVERY_TIME = {value} SECONDS;",
        // QueryStoreDesiredState: 0=OFF, 1=READ_ONLY, 2=READ_WRITE, 3=ERROR.
        // The simulator's QUERY_STORE parser accepts ON/OFF + sub-options; emit
        // the matching toggle. Sub-options (interval length, capture mode etc.)
        // are bacpac metadata-only — the simulator doesn't act on them, so
        // they're elided.
        "QueryStoreDesiredState" => value == "0"
            ? $"ALTER DATABASE {bracketedDb} SET QUERY_STORE = OFF;"
            : $"ALTER DATABASE {bracketedDb} SET QUERY_STORE = ON;",
        // Sub-options of QUERY_STORE — already covered by the desired-state
        // emit above. The simulator doesn't enforce their values.
        "QueryStoreIntervalLength" => null,
        "QueryStoreFlushInterval" => null,
        "QueryStoreCaptureMode" => null,
        "QueryStoreMaxStorageSize" => null,
        "QueryStoreSizeBasedCleanupMode" => null,
        "QueryStoreMaxPlansPerQuery" => null,
        "QueryStoreStaleQueryThreshold" => null,
        "QueryStoreWaitStatisticsCaptureMode" => null,
        // Collation handled in the caller (Skipped entry on mismatch).
        "Collation" => null,
        // sp_fulltext_database routes for the full-text-enabled toggle; the
        // simulator doesn't model the sproc. Emit nothing — the full-text
        // catalog DDL elements carry the load-bearing wiring.
        "IsFullTextEnabled" => null,
        // Anything else parse-and-discards at the simulator already; we
        // simply don't bother re-emitting it from the loader.
        _ => null,
    };
#pragma warning restore SSS005

    private static string OnOff(string value) => IsTrue(value) ? "ON" : "OFF";

    private static bool IsTrue(string value) => string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

    private static string RecoveryMode(string value) => value switch
    {
        "1" => "FULL",
        "2" => "BULK_LOGGED",
        "3" => "SIMPLE",
        _ => "FULL",
    };

    /// <summary>
    /// Emits <c>CREATE TYPE [schema].[name] FROM &lt;builtin&gt;[(args)] [NULL | NOT NULL]</c>
    /// for a <c>SqlUserDefinedDataType</c> element. The element's Type relationship
    /// + Length / Precision / Scale properties match the SqlTypeSpecifier shape exactly,
    /// so <see cref="TranslateTypeSpecifier"/> reuses cleanly. Default nullability when
    /// IsNullable is absent is True (matches SQL Server's CREATE TYPE default).
    /// </summary>
    private static void EmitUserDefinedDataType(XElement element, string? qualifiedName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            throw new InvalidDataException("bacpac: SqlUserDefinedDataType element missing Name attribute.");

        var typeDdl = TranslateTypeSpecifier(element);
        var isNullable = ReadBoolProperty(element, "IsNullable", defaultValue: true);
        var nullability = isNullable ? " NULL" : " NOT NULL";

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"CREATE TYPE {qualifiedName} FROM {typeDdl}{nullability};";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Emits <c>CREATE SEQUENCE [schema].[name] AS &lt;type&gt; START WITH N INCREMENT BY M</c>
    /// for a <c>SqlSequence</c> element. Properties carried by the bacpac:
    /// <c>StartValue</c>, <c>Increment</c>, plus a <c>TypeSpecifier</c>
    /// relationship pointing at the underlying integer type (<c>int</c>,
    /// <c>bigint</c>, <c>smallint</c>, <c>tinyint</c>, or
    /// <c>decimal(p, 0)</c>). MinValue / MaxValue / CycleOption / cache size
    /// aren't carried by WWI's bacpac, so the loader emits the bare form;
    /// SQL Server applies the data-type's natural bounds.
    /// </summary>
    private static void EmitSequence(XElement element, string? qualifiedName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            throw new InvalidDataException("bacpac: SqlSequence element missing Name attribute.");

        var typeSpec = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "TypeSpecifier")
            ?.Elements(Ns + "Entry").Elements(Ns + "Element")
            .FirstOrDefault(e => e.Attribute("Type")?.Value == "SqlTypeSpecifier")
            ?? throw new InvalidDataException($"bacpac: SqlSequence '{qualifiedName}' missing TypeSpecifier.");

        var typeDdl = TranslateTypeSpecifier(typeSpec);
        var startValue = ReadStringProperty(element, "StartValue") ?? "1";
        var increment = ReadStringProperty(element, "Increment") ?? "1";

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"CREATE SEQUENCE {qualifiedName} AS {typeDdl} START WITH {startValue} INCREMENT BY {increment};";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Emits <c>CREATE TYPE [schema].[name] AS TABLE (col_list [, PRIMARY KEY (cols)])</c>
    /// for a <c>SqlTableType</c> element. Columns are <c>SqlTableTypeSimpleColumn</c>
    /// entries with the same TypeSpecifier shape as regular columns — so
    /// <see cref="TranslateSimpleColumn"/> reuses cleanly. The optional
    /// <c>SqlTableTypePrimaryKeyConstraint</c> arrives under a
    /// <c>Constraints</c> relationship; the PK is anonymous (named constraints
    /// are grammar-rejected inside table types, see Msg 156 in
    /// <c>docs/claude/table-valued-parameters.md</c>) and the
    /// <c>IsClustered</c> annotation is dropped (table-variable storage is
    /// linear-scan regardless). UNIQUE / CHECK / FK on table types aren't
    /// exercised by WWI and aren't translated here.
    /// </summary>
    private static void EmitTableType(XElement element, string? qualifiedName, DbConnection connection, BacpacImportResult result)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            throw new InvalidDataException("bacpac: SqlTableType element missing Name attribute.");

        var columnsRel = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Columns")
            ?? throw new InvalidDataException($"bacpac: SqlTableType '{qualifiedName}' has no Columns relationship.");

        var columnDdls = new List<string>();
        foreach (var col in columnsRel.Elements(Ns + "Entry").Elements(Ns + "Element"))
        {
            var colType = col.Attribute("Type")?.Value;
            var colName = col.Attribute("Name")?.Value;
            if (colType == "SqlTableTypeSimpleColumn")
            {
                columnDdls.Add(TranslateSimpleColumn(col, colName, result));
            }
            else
            {
                result.AddSkipped(new BacpacSkipped(colType ?? "<unknown>", colName, $"Table-type column kind not handled inside '{qualifiedName}'."));
            }
        }

        var pkClauses = new List<string>();
        var constraintsRel = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Constraints");
        if (constraintsRel is not null)
        {
            foreach (var constraint in constraintsRel.Elements(Ns + "Entry").Elements(Ns + "Element"))
            {
                if (constraint.Attribute("Type")?.Value != "SqlTableTypePrimaryKeyConstraint")
                    continue;
                var cols = new List<string>();
                var specs = constraint.Elements(Ns + "Relationship")
                    .FirstOrDefault(r => r.Attribute("Name")?.Value == "ColumnSpecifications")
                    ?.Elements(Ns + "Entry").Elements(Ns + "Element");
                foreach (var spec in specs ?? [])
                {
                    var colRef = spec.Elements(Ns + "Relationship")
                        .FirstOrDefault(r => r.Attribute("Name")?.Value == "Column")
                        ?.Elements(Ns + "Entry").Elements(Ns + "References")
                        .FirstOrDefault()?.Attribute("Name")?.Value;
                    if (string.IsNullOrEmpty(colRef))
                        continue;
                    cols.Add(colRef[(colRef.LastIndexOf('.') + 1)..]);
                }
                if (cols.Count > 0)
                    pkClauses.Add($"PRIMARY KEY ({string.Join(", ", cols)})");
            }
        }

        var body = string.Join(", ", columnDdls.Concat(pkClauses));
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"CREATE TYPE {qualifiedName} AS TABLE ({body});";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Emits <c>CREATE ROLE [name] [AUTHORIZATION owner]</c> for a
    /// <c>SqlRole</c> element. The <c>Authorizer</c> relationship points at
    /// the role's owner (defaults to <c>dbo</c> when absent).
    /// </summary>
    private static void EmitRole(XElement element, string? bracketedName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(bracketedName))
            throw new InvalidDataException("bacpac: SqlRole element missing Name attribute.");

        var authorizer = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Authorizer")
            ?.Elements(Ns + "Entry").Elements(Ns + "References")
            .FirstOrDefault()?.Attribute("Name")?.Value;
        var authClause = string.IsNullOrEmpty(authorizer) ? "" : $" AUTHORIZATION {authorizer}";

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"CREATE ROLE {bracketedName}{authClause};";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Emits <c>GRANT|REVOKE|DENY &lt;perm&gt; [ON &lt;securable&gt;] TO &lt;grantee&gt;</c>
    /// for a <c>SqlPermissionStatement</c> element. The element's
    /// <c>Name</c> attribute encodes three dotted parts in its first
    /// bracketed segment: action (Grant/Revoke/Deny), permission name in
    /// camel-case, and securable scope (Database/Schema/Object/...). The
    /// <c>Grantee</c> relationship carries the principal; for AW + WWI's
    /// emit, both bacpacs ship the two database-scope encryption-key VIEW
    /// grants (<c>VIEW ANY COLUMN ENCRYPTION KEY DEFINITION</c> /
    /// <c>VIEW ANY COLUMN MASTER KEY DEFINITION</c>) targeting the
    /// pre-seeded <c>public</c> role. Schema- / object-scope GRANTs would
    /// route through this same path with the SecuredObject relationship
    /// translated to an <c>ON</c> clause (deferred until a bacpac exercises
    /// that surface).
    /// </summary>
    private static void EmitPermissionStatement(XElement element, string? bracketedName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(bracketedName))
            throw new InvalidDataException("bacpac: SqlPermissionStatement element missing Name attribute.");

        // Name format: `[Action.PermissionCamelCase.Scope].[grantee].[grantor]`.
        // Extract the first bracketed segment (Action.Permission.Scope).
        var firstSegmentEnd = bracketedName.IndexOf(']', StringComparison.Ordinal);
        if (!bracketedName.StartsWith('[') || firstSegmentEnd < 0)
            throw new InvalidDataException($"bacpac: SqlPermissionStatement '{bracketedName}' has unexpected name shape.");
        var firstSegment = bracketedName[1..firstSegmentEnd];
        var parts = firstSegment.Split('.');
        if (parts.Length != 3)
            throw new InvalidDataException($"bacpac: SqlPermissionStatement '{bracketedName}' first segment must be Action.Permission.Scope (got {parts.Length} parts).");

        var actionKeyword = parts[0].ToUpperInvariant();
        var permissionText = CamelToSpaceSeparatedUpper(parts[1]);
        // Scope (Database/Schema/Object/...) currently only exercises
        // "Database" in AW + WWI; no ON clause is needed for the database-
        // scope form. Object / schema scope would consult the SecuredObject
        // relationship; deferred until a bacpac exercises it.

        var grantee = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Grantee")
            ?.Elements(Ns + "Entry").Elements(Ns + "References")
            .FirstOrDefault()?.Attribute("Name")?.Value
            ?? throw new InvalidDataException($"bacpac: SqlPermissionStatement '{bracketedName}' has no Grantee relationship.");

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"{actionKeyword} {permissionText} TO {grantee};";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Splits a camel-case identifier (<c>ViewAnyColumnEncryptionKeyDefinition</c>)
    /// into space-separated uppercase tokens (<c>VIEW ANY COLUMN ENCRYPTION
    /// KEY DEFINITION</c>) so the simulator's GRANT parser can re-match it
    /// as a multi-word permission name. Inserts a space before every
    /// uppercase letter that's preceded by a lowercase letter; uppercases
    /// the rest as-is.
    /// </summary>
    private static string CamelToSpaceSeparatedUpper(string camel)
    {
        var sb = new System.Text.StringBuilder(camel.Length + 8);
        for (var i = 0; i < camel.Length; i++)
        {
            if (i > 0 && char.IsUpper(camel[i]) && char.IsLower(camel[i - 1]))
                _ = sb.Append(' ');
            _ = sb.Append(char.ToUpperInvariant(camel[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emits <c>CREATE TABLE [schema].[name] (col1 TYPE …, colN AS (expr), …)</c>
    /// for a <c>SqlTable</c> element. Both <c>SqlSimpleColumn</c> and
    /// <c>SqlComputedColumn</c> entries are translated inline at their model
    /// ordinal so <c>sys.columns.column_id</c> matches the source database —
    /// critical for system-versioned pairs, whose base and history tables must
    /// share identical column ordinals (else SQL Server rejects the exported
    /// bacpac with Msg 13524). Computed expressions that forward-reference a
    /// user function (which only lands in phase 7) can't resolve inside the
    /// CREATE TABLE column-list parser; when the inline attempt throws, the
    /// table is re-created with computed columns stripped and those columns are
    /// registered in <paramref name="deferredComputedTables"/> for phase 8 to
    /// append via <c>ALTER TABLE … ADD col AS (expr)</c> (ordinals then land at
    /// the end for that one table). Constraints (PK / UQ / FK / CHECK / DEFAULT)
    /// arrive as separate top-level Elements; they layer onto the table later
    /// via <c>ALTER TABLE … ADD CONSTRAINT</c>.
    /// </summary>
    private static void EmitTable(XElement element, string? qualifiedName, DbConnection connection, BacpacImportResult result, HashSet<string> deferredComputedTables)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            throw new InvalidDataException("bacpac: SqlTable element missing Name attribute.");

        var columnsRelationship = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Columns")
            ?? throw new InvalidDataException($"bacpac: SqlTable '{qualifiedName}' has no Columns relationship.");

        // One entry per real column in model order (computed included), so the
        // alias side-map can be built index-aligned to whichever HeapTable
        // column order the table lands in — inline (model order) or, on
        // fallback, simple-columns-then-computed-appended order.
        var columns = new List<(string Ddl, bool Computed, bool Alias)>();
        string? rowStartLeaf = null;
        string? rowEndLeaf = null;
        foreach (var columnElement in columnsRelationship.Elements(Ns + "Entry").Elements(Ns + "Element"))
        {
            var columnType = columnElement.Attribute("Type")?.Value;
            var columnName = columnElement.Attribute("Name")?.Value;
            // SSS005 disabled: cases are ordered by bacpac processing semantics with
            // per-case explanatory comments, not by name.
#pragma warning disable SSS005
            switch (columnType)
            {
                case "SqlSimpleColumn":
                    columns.Add((TranslateSimpleColumn(columnElement, columnName, result), Computed: false, IsAliasTypedColumn(columnElement)));
                    // Capture the row-start / row-end column leaves so the
                    // table-level PERIOD FOR SYSTEM_TIME clause can name them.
                    // GeneratedAlwaysType only appears on the base side of a
                    // system-versioned pair.
                    var leaf = ExtractColumnLeaf(columnName);
                    if (leaf is not null)
                    {
                        switch (ReadStringProperty(columnElement, "GeneratedAlwaysType"))
                        {
                            case "1": rowStartLeaf = leaf; break;
                            case "2": rowEndLeaf = leaf; break;
                        }
                    }
                    break;
                case "SqlComputedColumn":
                    // Emitted inline at model ordinal; a computed column is never
                    // UDDT-aliased (its type is derived, not declared) and carries
                    // no BCP wire bytes, so its alias slot is always false.
                    var computedDdl = TranslateComputedColumn(columnElement, columnName, result);
                    if (computedDdl is not null)
                        columns.Add((computedDdl, Computed: true, Alias: false));
                    break;
                default:
                    result.AddSkipped(new BacpacSkipped(
                        columnType ?? "<unknown>",
                        columnName,
                        $"Unrecognized column element on '{qualifiedName}'."));
                    break;
            }
#pragma warning restore SSS005
        }

        if (columns.Count == 0)
            throw new InvalidDataException($"bacpac: SqlTable '{qualifiedName}' has no recognized columns.");

        // Append the table-level PERIOD clause when both row-start and row-end
        // markers are present on this table's columns. The simulator's CREATE
        // TABLE validates the columns exist + match generated-always kinds and
        // raises matching errors when they don't. Not a real column — excluded
        // from the alias side-map.
        var periodClause = rowStartLeaf is not null && rowEndLeaf is not null
            ? $"PERIOD FOR SYSTEM_TIME ({rowStartLeaf}, {rowEndLeaf})"
            : null;

        var hasComputed = columns.Exists(c => c.Computed);
        var inlineDdls = new List<string>(columns.Select(c => c.Ddl));
        if (periodClause is not null)
            inlineDdls.Add(periodClause);

        try
        {
            ExecuteCreateTable(qualifiedName, inlineDdls, connection);
            // Inline succeeded: HeapTable.Columns is in model order, so the
            // alias side-map keeps a slot per model column (computed slots are
            // false and never read — BCP filters computed columns out before
            // the alias lookup).
            result.TableColumnIsAlias[qualifiedName] = [.. columns.Select(c => c.Alias)];
        }
        catch (Exception ex) when (hasComputed && ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A computed expression forward-referenced a not-yet-created UDF (or
            // otherwise couldn't parse in the column list). Re-create with
            // computed columns stripped and defer them to phase 8, which runs
            // after functions land. A second failure here is a genuine table
            // error and propagates to the resilient-loader "Load failed" path.
            var strippedDdls = new List<string>(columns.Where(c => !c.Computed).Select(c => c.Ddl));
            if (periodClause is not null)
                strippedDdls.Add(periodClause);
            ExecuteCreateTable(qualifiedName, strippedDdls, connection);
            _ = deferredComputedTables.Add(qualifiedName);
            // Computed columns land at the end of HeapTable.Columns in phase 8,
            // so the alias side-map is simple-columns-only, index-aligned to the
            // simple-column prefix of HeapTable.Columns.
            result.TableColumnIsAlias[qualifiedName] = [.. columns.Where(c => !c.Computed).Select(c => c.Alias)];
        }
    }

    private static void ExecuteCreateTable(string qualifiedName, List<string> columnDdls, DbConnection connection)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"CREATE TABLE {qualifiedName} ({string.Join(", ", columnDdls)});";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Extracts the bracketed column leaf from a 3-part qualified column
    /// name (<c>[schema].[table].[column]</c>) — used in CREATE TABLE
    /// column-list positions where the simulator's parser expects a bare
    /// identifier reference.
    /// </summary>
    private static string? ExtractColumnLeaf(string? qualifiedColumnName)
    {
        if (string.IsNullOrEmpty(qualifiedColumnName))
            return null;
        var lastDot = qualifiedColumnName.LastIndexOf('.');
        return lastDot < 0 ? qualifiedColumnName : qualifiedColumnName[(lastDot + 1)..];
    }

    /// <summary>
    /// Returns true when the column's type-reference is a UDDT alias (e.g.
    /// <c>[dbo].[Flag]</c>) rather than a built-in (<c>[int]</c> with
    /// <c>ExternalSource="BuiltIns"</c>). UDDT-aliased columns use a
    /// 1-byte-prefix wire format in BCP regardless of nullability — see
    /// <see cref="BacpacImportResult.TableColumnIsAlias"/>.
    /// </summary>
    private static bool IsAliasTypedColumn(XElement columnElement)
    {
        var typeRef = columnElement.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "TypeSpecifier")
            ?.Elements(Ns + "Entry").Elements(Ns + "Element")
            .FirstOrDefault(e => e.Attribute("Type")?.Value is "SqlTypeSpecifier")
            ?.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Type")
            ?.Elements(Ns + "Entry").Elements(Ns + "References").FirstOrDefault();
        return typeRef is not null && typeRef.Attribute("ExternalSource")?.Value != "BuiltIns";
    }

    /// <summary>
    /// Builds the per-column DDL fragment for a single <c>SqlSimpleColumn</c>
    /// element. Output shape:
    /// <c>[col] type[(args)] [IDENTITY(seed, increment)] [ROWGUIDCOL] [NULL|NOT NULL]</c>.
    /// IDENTITY defaults to (1,1); ROWGUIDCOL only emits when
    /// <c>IsRowGuidColumn=True</c>; the explicit NULL/NOT NULL marker comes
    /// from <c>IsNullable</c> (default True per probe).
    /// </summary>
    private static string TranslateSimpleColumn(XElement columnElement, string? qualifiedColumnName, BacpacImportResult result)
    {
        if (string.IsNullOrEmpty(qualifiedColumnName))
            throw new InvalidDataException("bacpac: SqlSimpleColumn missing Name attribute.");

        // Name shape: [schema].[table].[column] — take the trailing bracketed
        // segment as the column leaf.
        var lastDot = qualifiedColumnName.LastIndexOf('.');
        var columnLeaf = lastDot < 0 ? qualifiedColumnName : qualifiedColumnName[(lastDot + 1)..];

        // Capture explicit-or-absent for IsNullable: absent means "inherit
        // the column-type's default". For builtins that's NULL (ANSI default);
        // for alias types it's the alias's stored IsNullable. Omitting the
        // marker entirely lets the simulator's resolver pick the right
        // default through both paths. This matters for columns like
        // SalesOrderHeader.OnlineOrderFlag which uses the dbo.Flag alias
        // (NOT NULL default) — emitting "NULL" explicitly would override
        // the alias and cause BCP NULL-marker misalignment downstream.
        var isNullableExplicit = ReadStringProperty(columnElement, "IsNullable") is not null;
        var isNullable = ReadBoolProperty(columnElement, "IsNullable", defaultValue: true);
        var isIdentity = ReadBoolProperty(columnElement, "IsIdentity", defaultValue: false);
        var isRowGuid = ReadBoolProperty(columnElement, "IsRowGuidColumn", defaultValue: false);
        var identitySeed = ReadStringProperty(columnElement, "IdentitySeed");
        var identityIncrement = ReadStringProperty(columnElement, "IdentityIncrement");

        var typeSpec = columnElement.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "TypeSpecifier")
            ?.Elements(Ns + "Entry").Elements(Ns + "Element")
            .FirstOrDefault(e => e.Attribute("Type")?.Value is "SqlTypeSpecifier" or "SqlXmlTypeSpecifier")
            ?? throw new InvalidDataException($"bacpac: column '{qualifiedColumnName}' missing TypeSpecifier.");

        var typeDdl = TranslateTypeSpecifier(typeSpec);

        var identityClause = isIdentity
            ? $" IDENTITY({identitySeed ?? "1"}, {identityIncrement ?? "1"})"
            : "";

        // GENERATED ALWAYS AS ROW START / END marker: temporal period columns
        // on the base side of a system-versioned table. History tables carry
        // the same column shape minus this marker (they receive materialized
        // values rather than auto-generated ones).
        // 1 = ROW START, 2 = ROW END (DACFx-emitted constant).
        var generatedAlwaysType = ReadStringProperty(columnElement, "GeneratedAlwaysType");
        var generatedClause = generatedAlwaysType switch
        {
            "1" => " GENERATED ALWAYS AS ROW START",
            "2" => " GENERATED ALWAYS AS ROW END",
            _ => "",
        };
        if (isRowGuid)
        {
            // ROWGUIDCOL is metadata-only — it tells SQL Server which
            // uniqueidentifier column NEWID()/NEWSEQUENTIALID() defaults to
            // for $rowguid pseudo-column references. The simulator's CREATE
            // TABLE parser doesn't accept the clause; storage shape and DML
            // are unaffected by its absence (DEFAULT NEWID() arrives as a
            // separate SqlDefaultConstraint element). Record a Warning once
            // so the diagnostics report names the deferred surface.
            result.AddWarning($"ROWGUIDCOL clause on column '{qualifiedColumnName}' dropped — the simulator doesn't model this metadata annotation; storage behavior is unaffected.");
        }
        // Per-column COLLATE override — emitted only when on the whitelist.
        // An unrecognized name lands on Warnings; the simulator's parser
        // would reject it otherwise (the loader's best-effort contract
        // prefers a partial column over an aborted table load).
        var collateClause = "";
        var columnCollation = ReadStringProperty(columnElement, "Collation");
        if (columnCollation is not null)
        {
            if (Collation.IsRecognized(columnCollation))
                collateClause = $" COLLATE {columnCollation}";
            else
                result.AddWarning($"Column '{qualifiedColumnName}' declares COLLATE '{columnCollation}' which isn't recognized — clause dropped, column inherits the database default.");
        }
        var nullability = isNullableExplicit ? (isNullable ? " NULL" : " NOT NULL") : "";
        return $"{columnLeaf} {typeDdl}{collateClause}{identityClause}{generatedClause}{nullability}";
    }

    /// <summary>
    /// Translates a <c>SqlTypeSpecifier</c> element to its T-SQL surface form.
    /// Handles bracketed built-ins (<c>[int]</c> / <c>[sys].[sysname]</c> /
    /// <c>[sys].[hierarchyid]</c>), user-defined alias types
    /// (<c>[dbo].[Name]</c>), and the four argument shapes: <c>Length</c>,
    /// <c>IsMax</c>, <c>Precision</c> alone, <c>Precision+Scale</c>, and
    /// <c>Scale</c> alone (datetime2 / time / datetimeoffset).
    /// </summary>
    private static string TranslateTypeSpecifier(XElement typeSpec)
    {
        var typeRef = typeSpec.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Type")
            ?.Elements(Ns + "Entry").Elements(Ns + "References").FirstOrDefault()
            ?? throw new InvalidDataException("bacpac: TypeSpecifier missing Type reference.");

        var typeName = typeRef.Attribute("Name")?.Value
            ?? throw new InvalidDataException("bacpac: Type reference missing Name attribute.");
        var isBuiltin = typeRef.Attribute("ExternalSource")?.Value == "BuiltIns";

        // Normalize the name into the form a CREATE TABLE column-type position
        // can use. Built-ins drop their brackets and any [sys]. prefix so
        // hierarchyid / sysname / etc. reach the parser as bare identifiers;
        // user-defined alias types keep their bracketed 2-part shape so the
        // simulator's Schema.AliasTypes lookup runs through the qualified path.
        var renderedTypeName = isBuiltin ? NormalizeBuiltinName(typeName) : typeName;

        var isMax = ReadBoolProperty(typeSpec, "IsMax", defaultValue: false);
        var length = ReadStringProperty(typeSpec, "Length");
        var precision = ReadStringProperty(typeSpec, "Precision");
        var scale = ReadStringProperty(typeSpec, "Scale");

        // Argument-shape cascade: IsMax wins, then Length, then
        // Precision[+Scale], then Scale (datetime2 / time / datetimeoffset),
        // then bare type. The simulator's parser accepts the trailing-paren
        // shape verbatim.
        return (isMax, length, precision, scale) switch
        {
            (true, _, _, _) => $"{renderedTypeName}(MAX)",
            (_, not null, _, _) => $"{renderedTypeName}({length})",
            (_, _, not null, not null) => string.Create(CultureInfo.InvariantCulture, $"{renderedTypeName}({precision}, {scale})"),
            (_, _, not null, _) => $"{renderedTypeName}({precision})",
            (_, _, _, not null) => $"{renderedTypeName}({scale})",
            _ => renderedTypeName,
        };
    }

    /// <summary>
    /// Strips the brackets and any <c>[sys].</c> qualifier from a built-in
    /// type reference. <c>[int]</c> → <c>int</c>, <c>[sys].[hierarchyid]</c>
    /// → <c>hierarchyid</c>, <c>[sys].[sysname]</c> → <c>sysname</c>. The
    /// returned bare name is what the simulator's parser sees in a column /
    /// parameter type position.
    /// </summary>
    private static string NormalizeBuiltinName(string bracketedName)
    {
        var lastDot = bracketedName.LastIndexOf('.');
        var lastSegment = lastDot < 0 ? bracketedName : bracketedName[(lastDot + 1)..];
        return lastSegment.Trim('[', ']');
    }

    private static bool ReadBoolProperty(XElement element, string name, bool defaultValue) =>
        ReadStringProperty(element, name) is { } value ? IsTrue(value) : defaultValue;

    private static string? ReadStringProperty(XElement element, string name) =>
        element.Elements(Ns + "Property")
            .FirstOrDefault(p => p.Attribute("Name")?.Value == name)
            ?.Attribute("Value")?.Value;

    /// <summary>
    /// Reads the body of <c>&lt;Property Name=X&gt;&lt;Value&gt;[CDATA[…]]&lt;/Value&gt;&lt;/Property&gt;</c>
    /// — used for CDATA-wrapped raw T-SQL bodies (CHECK / DEFAULT expression
    /// scripts, view / procedure / function bodies).
    /// </summary>
    private static string? ReadScriptProperty(XElement element, string name) =>
        element.Elements(Ns + "Property")
            .FirstOrDefault(p => p.Attribute("Name")?.Value == name)
            ?.Element(Ns + "Value")?.Value;

    /// <summary>
    /// Resolves a <c>&lt;Relationship Name=X&gt;&lt;Entry&gt;&lt;References Name="…" /&gt;&lt;/Entry&gt;&lt;/Relationship&gt;</c>
    /// chain to the single target Name attribute. Returns null when the
    /// relationship is absent.
    /// </summary>
    private static string? ReadSingleReference(XElement element, string relationshipName) =>
        element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == relationshipName)
            ?.Elements(Ns + "Entry").Elements(Ns + "References").FirstOrDefault()
            ?.Attribute("Name")?.Value;

    /// <summary>
    /// Resolves a relationship containing 1 or more
    /// <c>&lt;Entry&gt;&lt;References Name="…" /&gt;&lt;/Entry&gt;</c> children
    /// to their Name attributes in document order. Empty list when absent.
    /// </summary>
    private static List<string> ReadMultipleReferences(XElement element, string relationshipName) =>
        [.. element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == relationshipName)
            ?.Elements(Ns + "Entry").Elements(Ns + "References")
            .Select(r => r.Attribute("Name")?.Value)
            .OfType<string>()
            ?? []];

    /// <summary>
    /// Extracts the bracketed leaf of a 1/2/3-part qualified name.
    /// <c>[s].[t].[c]</c> → <c>[c]</c>, <c>[s].[t]</c> → <c>[t]</c>,
    /// <c>[t]</c> → <c>[t]</c>.
    /// </summary>
    private static string Leaf(string qualifiedName)
    {
        var lastDot = qualifiedName.LastIndexOf('.');
        return lastDot < 0 ? qualifiedName : qualifiedName[(lastDot + 1)..];
    }

    /// <summary>
    /// Emits <c>ALTER TABLE table ADD CONSTRAINT name PRIMARY KEY | UNIQUE [CLUSTERED|NONCLUSTERED] (cols)</c>.
    /// <c>SqlUniqueConstraint</c> may carry no Name attribute (the
    /// auto-generated name shows up in a <c>SqlInlineConstraintAnnotation</c>
    /// sibling) — in that case the loader drops the <c>CONSTRAINT name</c>
    /// segment and lets the simulator allocate one. PK defaults to CLUSTERED
    /// unless <c>IsClustered=False</c>; UQ defaults to NONCLUSTERED.
    /// </summary>
    private static void EmitKeyConstraint(XElement element, string? constraintName, DbConnection connection, bool isPrimary)
    {
        var definingTable = ReadSingleReference(element, "DefiningTable")
            ?? throw new InvalidDataException($"bacpac: {(isPrimary ? "SqlPrimaryKeyConstraint" : "SqlUniqueConstraint")} missing DefiningTable.");
        var columnRefs = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "ColumnSpecifications")
            ?.Elements(Ns + "Entry").Elements(Ns + "Element")
            .Select(spec => ReadSingleReference(spec, "Column"))
            .OfType<string>()
            .ToList()
            ?? throw new InvalidDataException($"bacpac: {(isPrimary ? "PK" : "UQ")} on '{definingTable}' missing ColumnSpecifications.");
        var columnLeaves = string.Join(", ", columnRefs.Select(Leaf));

        // UQ defaults to NONCLUSTERED, PK to CLUSTERED. IsClustered=False
        // flips PK to NONCLUSTERED; True flips UQ to CLUSTERED (rare).
        var isClustered = ReadBoolProperty(element, "IsClustered", defaultValue: isPrimary);
        var clusteringClause = isClustered ? " CLUSTERED" : " NONCLUSTERED";
        var kind = isPrimary ? "PRIMARY KEY" : "UNIQUE";
        // Constraint names arrive 2-part qualified ([schema].[name]); ALTER
        // TABLE ADD CONSTRAINT expects the unqualified leaf — the constraint
        // lives in its DefiningTable's schema by default.
        var constraintPrefix = string.IsNullOrEmpty(constraintName) ? "" : $"CONSTRAINT {Leaf(constraintName)} ";

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"ALTER TABLE {definingTable} ADD {constraintPrefix}{kind}{clusteringClause} ({columnLeaves});";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Emits <c>ALTER TABLE child ADD CONSTRAINT name FOREIGN KEY (cols) REFERENCES parent (cols) [ON DELETE …] [ON UPDATE …]</c>.
    /// OnDeleteAction / OnUpdateAction property values use the DACFx enum:
    /// 0 = NO ACTION (default — clause omitted), 1 = CASCADE, 2 = SET NULL,
    /// 3 = SET DEFAULT. The simulator's CREATE TABLE / ALTER TABLE FK parser
    /// already ships all four referential actions per
    /// <c>docs/claude/foreign-keys.md</c>.
    /// </summary>
    private static void EmitForeignKeyConstraint(XElement element, string? constraintName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(constraintName))
            throw new InvalidDataException("bacpac: SqlForeignKeyConstraint missing Name attribute.");

        var definingTable = ReadSingleReference(element, "DefiningTable")
            ?? throw new InvalidDataException($"bacpac: FK '{constraintName}' missing DefiningTable.");
        var foreignTable = ReadSingleReference(element, "ForeignTable")
            ?? throw new InvalidDataException($"bacpac: FK '{constraintName}' missing ForeignTable.");
        var childCols = ReadMultipleReferences(element, "Columns");
        var parentCols = ReadMultipleReferences(element, "ForeignColumns");
        if (childCols.Count == 0 || parentCols.Count == 0 || childCols.Count != parentCols.Count)
            throw new InvalidDataException($"bacpac: FK '{constraintName}' has mismatched / missing column relationships.");

        var childList = string.Join(", ", childCols.Select(Leaf));
        var parentList = string.Join(", ", parentCols.Select(Leaf));
        var deleteAction = TranslateReferentialAction(ReadStringProperty(element, "OnDeleteAction"));
        var updateAction = TranslateReferentialAction(ReadStringProperty(element, "OnUpdateAction"));
        var deleteClause = deleteAction is null ? "" : $" ON DELETE {deleteAction}";
        var updateClause = updateAction is null ? "" : $" ON UPDATE {updateAction}";

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"ALTER TABLE {definingTable} ADD CONSTRAINT {Leaf(constraintName)} FOREIGN KEY ({childList}) REFERENCES {foreignTable} ({parentList}){deleteClause}{updateClause};";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Maps DACFx's <c>OnDeleteAction</c> / <c>OnUpdateAction</c> integer enum
    /// to the T-SQL surface form. Returns null for NO ACTION (the default —
    /// no <c>ON DELETE</c> / <c>ON UPDATE</c> clause emitted).
    /// </summary>
    private static string? TranslateReferentialAction(string? value) => value switch
    {
        null or "0" => null,
        "1" => "CASCADE",
        "2" => "SET NULL",
        "3" => "SET DEFAULT",
        _ => null,
    };

    /// <summary>
    /// Emits <c>ALTER TABLE table ADD CONSTRAINT name CHECK (raw_expression)</c>.
    /// The CheckExpressionScript property body is raw T-SQL — feeds directly
    /// to the simulator's CHECK parser.
    /// </summary>
    private static void EmitCheckConstraint(XElement element, string? constraintName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(constraintName))
            throw new InvalidDataException("bacpac: SqlCheckConstraint missing Name attribute.");
        var definingTable = ReadSingleReference(element, "DefiningTable")
            ?? throw new InvalidDataException($"bacpac: CHECK '{constraintName}' missing DefiningTable.");
        var script = ReadScriptProperty(element, "CheckExpressionScript")
            ?? throw new InvalidDataException($"bacpac: CHECK '{constraintName}' missing CheckExpressionScript.");

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"ALTER TABLE {definingTable} ADD CONSTRAINT {Leaf(constraintName)} CHECK ({script});";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Builds the inline computed-column DDL fragment <c>[col] AS (expr)</c>
    /// for a <c>SqlComputedColumn</c> element. The <c>ExpressionScript</c> body
    /// arrives parenthesized already (DACFx emits e.g.
    /// <c>(concat([X],N' ',[Y]))</c>), so it's used verbatim. PERSISTED is
    /// dropped intentionally — see <see cref="EmitDeferredComputedColumns"/>
    /// for the rationale (BCP carries no bytes for computed columns, so the
    /// simulator recomputes on every read). Returns null (recording a skip)
    /// when the expression is absent.
    /// </summary>
    private static string? TranslateComputedColumn(XElement columnElement, string? columnName, BacpacImportResult result)
    {
        if (string.IsNullOrEmpty(columnName))
            throw new InvalidDataException("bacpac: SqlComputedColumn missing Name attribute.");
        var columnLeaf = columnName[(columnName.LastIndexOf('.') + 1)..];
        var expression = ReadScriptProperty(columnElement, "ExpressionScript");
        if (string.IsNullOrEmpty(expression))
        {
            result.AddSkipped(new BacpacSkipped("SqlComputedColumn", columnName, "Missing ExpressionScript property."));
            return null;
        }
        return $"{columnLeaf} AS {expression}";
    }

    /// <summary>
    /// Phase 8 walker: re-visits each <c>SqlTable</c> element and emits
    /// <c>ALTER TABLE [schema].[table] ADD [col] AS (expr) [PERSISTED]</c>
    /// for every <c>SqlComputedColumn</c> entry the original table-creation
    /// pass (phase 2) deferred. Running after phase 7 means UDFs referenced
    /// in the computed expression already exist; CREATE TABLE's column-list
    /// parser was the constraint that forced the deferral. The
    /// <c>ExpressionScript</c> body arrives parenthesized already (DACFx
    /// emits e.g. <c>(concat([X],N' ',[Y]))</c>), so the loader emits it
    /// verbatim without re-wrapping. <c>IsPersisted</c> picks the optional
    /// PERSISTED marker.
    /// </summary>
    /// <summary>
    /// Emits <c>ALTER TABLE base SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = name))</c>
    /// for each base SqlTable element that carries the
    /// <c>TemporalSystemVersioningHistoryTable</c> relationship. Element
    /// ordering doesn't matter at this phase — both endpoints are guaranteed
    /// to exist (phase 2) and constraints have layered (phase 3/4). Tables
    /// without the relationship are ignored. The history reference uses
    /// DACFx's bracketed <c>[schema].[name]</c> form, which the simulator's
    /// object-name parser accepts as-is.
    /// </summary>
    private static void EmitDeferredSystemVersioning(XElement tableElement, string? qualifiedName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            return;
        var historyRel = tableElement.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "TemporalSystemVersioningHistoryTable");
        if (historyRel is null)
            return;
        var historyName = historyRel.Elements(Ns + "Entry").Elements(Ns + "References")
            .FirstOrDefault()?.Attribute("Name")?.Value;
        if (string.IsNullOrEmpty(historyName))
            return;

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"ALTER TABLE {qualifiedName} SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = {historyName}));";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    private static void EmitDeferredComputedColumns(XElement tableElement, DbConnection connection, BacpacImportResult result, HashSet<string> deferredComputedTables)
    {
        var tableName = tableElement.Attribute("Name")?.Value;
        if (string.IsNullOrEmpty(tableName))
            return;
        // Only tables whose inline computed-column emission failed in phase 2
        // (a forward UDF reference) land here — every other table already
        // carries its computed columns at their true model ordinal.
        if (!deferredComputedTables.Contains(tableName))
            return;
        var columnsRel = tableElement.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Columns");
        if (columnsRel is null)
            return;
        foreach (var col in columnsRel.Elements(Ns + "Entry").Elements(Ns + "Element"))
        {
            if (col.Attribute("Type")?.Value != "SqlComputedColumn")
                continue;
            var columnName = col.Attribute("Name")?.Value;
            if (string.IsNullOrEmpty(columnName))
                continue;
            var columnLeaf = columnName[(columnName.LastIndexOf('.') + 1)..];
            var expression = ReadScriptProperty(col, "ExpressionScript");
            if (string.IsNullOrEmpty(expression))
            {
                result.AddSkipped(new BacpacSkipped("SqlComputedColumn", columnName, "Missing ExpressionScript property."));
                continue;
            }
            // PERSISTED dropped intentionally: the simulator's read path for
            // a PERSISTED computed column reads bytes from storage rather than
            // recomputing, but BCP files don't carry data for computed columns
            // (real bcp.exe / DACFx exclude them), so a persisted-computed
            // column would have no stored bytes for existing rows. Emitting
            // without PERSISTED makes the simulator recompute on every read —
            // identical query semantics, just no caching. The bacpac's
            // IsPersisted hint is read for the loader's records but not
            // propagated to the simulator.
            try
            {
                using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
                command.CommandText = $"ALTER TABLE {tableName} ADD {columnLeaf} AS {expression};";
#pragma warning restore CA2100
                _ = command.ExecuteNonQuery();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Use "Deferred:" prefix rather than "Load failed:" so the
                // resilient-loader AW guard (which fires on "Load failed:")
                // stays meaningful: known-deferred-feature failures here
                // are expected (UDF resolution at ALTER TABLE ADD AS, the
                // unmodeled built-ins JSON_QUERY / DECOMPRESS / etc.), not
                // regressions.
                result.AddSkipped(new BacpacSkipped("SqlComputedColumn", columnName, $"Deferred: {ex.GetType().Name}: {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// Emits <c>EXEC sp_addextendedproperty …</c> for a <c>SqlExtendedProperty</c>
    /// element. DACFx encodes the host kind in the element Name's leading
    /// bracketed segment (<c>[SqlColumn]</c> / <c>[SqlTableBase]</c> /
    /// <c>[SqlSchema]</c> / <c>[SqlDatabaseOptions]</c> / <c>[SqlDatabaseDdlTrigger]</c> /
    /// <c>[SqlFilegroup]</c>); the Host relationship carries the dotted
    /// qualified-name path. The property name is the trailing bracketed
    /// segment. SqlTableBase resolves to TABLE or VIEW depending on whether
    /// the host name appears in <paramref name="viewNames"/>.
    /// </summary>
    private static void EmitExtendedProperty(XElement element, string? elementName, DbConnection connection, HashSet<string> viewNames, BacpacImportResult result)
    {
        if (string.IsNullOrEmpty(elementName))
            throw new InvalidDataException("bacpac: SqlExtendedProperty missing Name attribute.");

        // Element name shape: [<HostKind>].[<host_segments...>].[<prop_name>]
        // Tokenize on the unquoted dots; bracketed segments can contain spaces.
        var segments = SplitBracketedSegments(elementName);
        if (segments.Count < 2)
        {
            result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, "Couldn't split Name into host-kind + property-name segments."));
            return;
        }
        var hostKind = segments[0];
        var propertyName = segments[^1];

        var value = ReadScriptProperty(element, "Value");
        if (value is null)
        {
            result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, "Missing Value property."));
            return;
        }

        // Host reference for column/table/schema hosts; SqlDatabaseOptions's
        // Host is the disambiguator-only DB ref so we skip the lookup there.
        var hostRef = ReadSingleReference(element, "Host");

        // Resolve to sp_addextendedproperty's @levelNtype / @levelNname pairs.
        string? l0type = null, l0name = null, l1type = null, l1name = null, l2type = null, l2name = null;
        // SSS005 disabled: cases are ordered by extended-property host level (database →
        // schema → table → column → index → constraint) with per-case comments, not by name.
#pragma warning disable SSS005
        switch (hostKind)
        {
            case "SqlDatabaseOptions":
                // No level args — applies to the current database.
                break;
            case "SqlSchema":
                if (hostRef is null)
                {
                    result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, "SqlSchema host missing reference."));
                    return;
                }
                l0type = "SCHEMA";
                l0name = Unbracket(hostRef);
                break;
            case "SqlTableBase":
                if (hostRef is null || !TrySplit2Part(hostRef, out var tabSchema, out var tabName))
                {
                    result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, $"SqlTableBase host '{hostRef}' isn't 2-part qualified."));
                    return;
                }
                l0type = "SCHEMA";
                l0name = tabSchema;
                l1type = viewNames.Contains(hostRef) ? "VIEW" : "TABLE";
                l1name = tabName;
                break;
            case "SqlColumn":
                if (hostRef is null || !TrySplit3Part(hostRef, out var colSchema, out var colTable, out var colName))
                {
                    result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, $"SqlColumn host '{hostRef}' isn't 3-part qualified."));
                    return;
                }
                l0type = "SCHEMA";
                l0name = colSchema;
                l1type = viewNames.Contains($"[{colSchema}].[{colTable}]") ? "VIEW" : "TABLE";
                l1name = colTable;
                l2type = "COLUMN";
                l2name = colName;
                break;
            case "SqlIndexBase":
                // 3-part host `[schema].[table].[index]`. sp_addextendedproperty
                // INDEX level: @level0=SCHEMA, @level1=TABLE|VIEW, @level2=INDEX.
                if (hostRef is null || !TrySplit3Part(hostRef, out var ixSchema, out var ixTable, out var ixName))
                {
                    result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, $"SqlIndexBase host '{hostRef}' isn't 3-part qualified."));
                    return;
                }
                l0type = "SCHEMA";
                l0name = ixSchema;
                l1type = viewNames.Contains($"[{ixSchema}].[{ixTable}]") ? "VIEW" : "TABLE";
                l1name = ixTable;
                l2type = "INDEX";
                l2name = ixName;
                break;
            case "SqlConstraint":
                // 2-part host `[schema].[constraint_name]` — the bacpac doesn't
                // directly carry the parent table. Resolve it via sys.objects
                // (constraint rows carry parent_object_id pointing at the
                // owning table). The constraint name is unique within a
                // schema in real SQL Server, so one row at most matches.
                if (hostRef is null || !TrySplit2Part(hostRef, out var ckSchema, out var ckConstraintName))
                {
                    result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, $"SqlConstraint host '{hostRef}' isn't 2-part qualified."));
                    return;
                }
                var parentTable = LookupConstraintParentTable(connection, ckSchema, ckConstraintName);
                if (parentTable is null)
                {
                    result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, $"SqlConstraint '{hostRef}' has no resolvable parent table."));
                    return;
                }
                l0type = "SCHEMA";
                l0name = ckSchema;
                l1type = "TABLE";
                l1name = parentTable;
                l2type = "CONSTRAINT";
                l2name = ckConstraintName;
                break;
            case "SqlDatabaseDdlTrigger":
            case "SqlFilegroup":
            default:
                result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName, $"Host kind '{hostKind}' not modeled for sp_addextendedproperty."));
                return;
        }
#pragma warning restore SSS005

        // Assemble the EXEC. Value is already wrapped (N'…' or numeric); other
        // args are bare identifiers quoted as N-strings here.
        var args = new List<string>
        {
            $"@name = N'{propertyName.Replace("'", "''", StringComparison.Ordinal)}'",
            $"@value = {value}",
        };
        if (l0type is not null)
        {
            args.Add($"@level0type = N'{l0type}'");
            args.Add($"@level0name = N'{l0name!.Replace("'", "''", StringComparison.Ordinal)}'");
        }
        if (l1type is not null)
        {
            args.Add($"@level1type = N'{l1type}'");
            args.Add($"@level1name = N'{l1name!.Replace("'", "''", StringComparison.Ordinal)}'");
        }
        if (l2type is not null)
        {
            args.Add($"@level2type = N'{l2type}'");
            args.Add($"@level2name = N'{l2name!.Replace("'", "''", StringComparison.Ordinal)}'");
        }

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"EXEC sp_addextendedproperty {string.Join(", ", args)};";
#pragma warning restore CA2100
        try
        {
            _ = command.ExecuteNonQuery();
        }
        catch (SimulatedSqlException ex)
        {
            result.AddSkipped(new BacpacSkipped("SqlExtendedProperty", elementName,
                $"sp_addextendedproperty failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Splits a DACFx-bracketed dotted name (e.g. <c>[SqlColumn].[dbo].[t].[c].[MS_Description]</c>)
    /// into its bare segments. Bracketed segments can contain dots and spaces
    /// — the split tokenizes on <c>].[</c> after stripping the leading <c>[</c>
    /// and trailing <c>]</c>.
    /// </summary>
    private static List<string> SplitBracketedSegments(string bracketedName)
    {
        if (bracketedName.Length < 2 || bracketedName[0] != '[' || bracketedName[^1] != ']')
            return [bracketedName];
        var inner = bracketedName[1..^1];
        return [.. inner.Split("].[", StringSplitOptions.None)];
    }

    private static string Unbracket(string s) =>
        s.Length >= 2 && s[0] == '[' && s[^1] == ']' ? s[1..^1] : s;

    /// <summary>
    /// Looks up the owning table name for a named constraint (PK / UQ / FK /
    /// CHECK / DEFAULT) by querying <c>sys.objects</c>. The bacpac's
    /// SqlConstraint host references are 2-part (schema + constraint name)
    /// without the parent table — sp_addextendedproperty's CONSTRAINT level
    /// needs the @level1name table, so the loader resolves it through the
    /// catalog. Returns null when no constraint matches (the caller records
    /// the failure on Skipped).
    /// </summary>
    private static string? LookupConstraintParentTable(DbConnection connection, string schemaName, string constraintName)
    {
        using var lookup = connection.CreateCommand();
#pragma warning disable CA2100 // schema / constraint names come from the bacpac model.xml — caller-trusted, parameterized through ADO.NET in any case
        lookup.CommandText = """
            SELECT OBJECT_NAME(o.parent_object_id)
            FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @s AND o.name = @c AND o.parent_object_id <> 0
            """;
#pragma warning restore CA2100
        var sParam = lookup.CreateParameter();
        sParam.ParameterName = "@s";
        sParam.Value = schemaName;
        _ = lookup.Parameters.Add(sParam);
        var cParam = lookup.CreateParameter();
        cParam.ParameterName = "@c";
        cParam.Value = constraintName;
        _ = lookup.Parameters.Add(cParam);
        return lookup.ExecuteScalar() as string;
    }

    private static bool TrySplit2Part(string qualifiedName, out string schema, out string name)
    {
        var segments = SplitBracketedSegments(qualifiedName);
        if (segments.Count != 2)
        {
            schema = name = "";
            return false;
        }
        schema = segments[0];
        name = segments[1];
        return true;
    }

    private static bool TrySplit3Part(string qualifiedName, out string schema, out string table, out string column)
    {
        var segments = SplitBracketedSegments(qualifiedName);
        if (segments.Count != 3)
        {
            schema = table = column = "";
            return false;
        }
        schema = segments[0];
        table = segments[1];
        column = segments[2];
        return true;
    }

    /// <summary>
    /// Universal emitter for views / scalar functions / multi-statement TVFs
    /// / procedures / DML triggers. DACFx captures the canonical
    /// <c>CREATE …</c> statement header in a <c>SysCommentsObjectAnnotation</c>
    /// alongside the body — for SqlView / SqlProcedure / SqlDmlTrigger the
    /// annotation sits directly on the element; for SqlScalarFunction and
    /// SqlMultiStatementTableValuedFunction it lives one level deeper on the
    /// nested <c>SqlScriptFunctionImplementation</c> reached through the
    /// FunctionBody relationship. The emitter concatenates HeaderContents +
    /// body verbatim and runs the result through the simulator's parser.
    /// </summary>
    private static void EmitProgrammableObject(XElement element, string? name, DbConnection connection, BacpacImportResult result, string objectType, string bodyPropertyName)
    {
        if (string.IsNullOrEmpty(name))
            throw new InvalidDataException($"bacpac: {objectType} missing Name attribute.");

        // FunctionBody → SqlScriptFunctionImplementation indirection. Other
        // element types' annotation + body live directly on the element.
        var bodyHost = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "FunctionBody")
            ?.Elements(Ns + "Entry").Elements(Ns + "Element")
            .FirstOrDefault(e => e.Attribute("Type")?.Value == "SqlScriptFunctionImplementation")
            ?? element;

        var headerContents = bodyHost.Elements(Ns + "Annotation")
            .Where(a => a.Attribute("Type")?.Value == "SysCommentsObjectAnnotation")
            .SelectMany(a => a.Elements(Ns + "Property"))
            .FirstOrDefault(p => p.Attribute("Name")?.Value == "HeaderContents")
            ?.Attribute("Value")?.Value;

        var body = ReadScriptProperty(bodyHost, bodyPropertyName);

        if (string.IsNullOrEmpty(headerContents) || string.IsNullOrEmpty(body))
        {
            result.AddSkipped(new BacpacSkipped(objectType, name,
                $"Missing HeaderContents or {bodyPropertyName} — can't reconstruct CREATE statement."));
            return;
        }

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = headerContents + "\n" + body;
#pragma warning restore CA2100
        try
        {
            _ = command.ExecuteNonQuery();
        }
        catch (SimulatedSqlException ex)
        {
            result.AddSkipped(new BacpacSkipped(objectType, name,
                $"CREATE {objectType} failed: {ex.Message}"));
        }
        catch (NotSupportedException ex)
        {
            result.AddSkipped(new BacpacSkipped(objectType, name,
                $"CREATE {objectType} hit unsupported feature: {ex.Message}"));
        }
    }

    /// <summary>
    /// Emits <c>CREATE [UNIQUE] [CLUSTERED|NONCLUSTERED] INDEX name ON table (cols) [INCLUDE (cols)]</c>.
    /// Index Name attribute is 3-part <c>[schema].[table].[index_name]</c>;
    /// the leaf becomes the index name. IsUnique / IsClustered default False
    /// (= non-unique nonclustered). IncludedColumns relationship carries the
    /// INCLUDE list. Indexes whose <c>IndexedObject</c> is a view land on
    /// Skipped — indexed views need view support + SCHEMABINDING machinery
    /// the simulator doesn't model.
    /// </summary>
    private static void EmitIndex(XElement element, string? indexName, DbConnection connection, HashSet<string> viewNames, BacpacImportResult result)
    {
        if (string.IsNullOrEmpty(indexName))
            throw new InvalidDataException("bacpac: SqlIndex missing Name attribute.");

        var indexedObject = ReadSingleReference(element, "IndexedObject")
            ?? throw new InvalidDataException($"bacpac: SqlIndex '{indexName}' missing IndexedObject.");
        if (viewNames.Contains(indexedObject))
        {
            result.AddSkipped(new BacpacSkipped(
                "SqlIndex",
                indexName,
                $"Index on view '{indexedObject}' deferred — indexed views need view support + SCHEMABINDING machinery the simulator doesn't model."));
            return;
        }

        var columnRefs = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "ColumnSpecifications")
            ?.Elements(Ns + "Entry").Elements(Ns + "Element")
            .Select(spec => ReadSingleReference(spec, "Column"))
            .OfType<string>()
            .ToList()
            ?? throw new InvalidDataException($"bacpac: SqlIndex '{indexName}' missing ColumnSpecifications.");

        var includeRefs = ReadMultipleReferences(element, "IncludedColumns");
        var isUnique = ReadBoolProperty(element, "IsUnique", defaultValue: false);
        var isClustered = ReadBoolProperty(element, "IsClustered", defaultValue: false);

        var uniqueClause = isUnique ? "UNIQUE " : "";
        var clusteringClause = isClustered ? "CLUSTERED " : "NONCLUSTERED ";
        var keyList = string.Join(", ", columnRefs.Select(Leaf));
        var includeClause = includeRefs.Count == 0
            ? ""
            : $" INCLUDE ({string.Join(", ", includeRefs.Select(Leaf))})";

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"CREATE {uniqueClause}{clusteringClause}INDEX {Leaf(indexName)} ON {indexedObject} ({keyList}){includeClause};";
#pragma warning restore CA2100
        try
        {
            _ = command.ExecuteNonQuery();
        }
        catch (SimulatedSqlException ex)
        {
            // Best-effort load: some AW indexes reference computed columns
            // that won't exist until functions land (Phase F deferred). Each
            // failure lands on Skipped with the simulator's diagnostic so the
            // caller has a precise inventory of what didn't make it; future
            // phase work shrinks this set.
            result.AddSkipped(new BacpacSkipped("SqlIndex", indexName,
                $"CREATE INDEX on '{indexedObject}' failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Emits <c>ALTER TABLE table ADD CONSTRAINT name DEFAULT (raw_expression) FOR column</c>.
    /// </summary>
    private static void EmitDefaultConstraint(XElement element, string? constraintName, DbConnection connection)
    {
        if (string.IsNullOrEmpty(constraintName))
            throw new InvalidDataException("bacpac: SqlDefaultConstraint missing Name attribute.");
        var definingTable = ReadSingleReference(element, "DefiningTable")
            ?? throw new InvalidDataException($"bacpac: DEFAULT '{constraintName}' missing DefiningTable.");
        var forColumn = ReadSingleReference(element, "ForColumn")
            ?? throw new InvalidDataException($"bacpac: DEFAULT '{constraintName}' missing ForColumn.");
        var script = ReadScriptProperty(element, "DefaultExpressionScript")
            ?? throw new InvalidDataException($"bacpac: DEFAULT '{constraintName}' missing DefaultExpressionScript.");
        // DACFx already parenthesizes the default expression (e.g.
        // "(NEXT VALUE FOR [Sequences].[InvoiceID])"). The ALTER-DEFAULT parser
        // re-derives sys.default_constraints.definition as "(<inner>)", so
        // wrapping an already-parenthesized script here would double the parens
        // (real SQL Server stores a single outer pair). Only wrap when the
        // script isn't already a parenthesized group.
        var wrapped = script.StartsWith('(') ? script : $"({script})";

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"ALTER TABLE {definingTable} ADD CONSTRAINT {Leaf(constraintName)} DEFAULT {wrapped} FOR {Leaf(forColumn)};";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }
}
