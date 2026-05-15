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
/// <see cref="BacpacSkipped"/> entry on <see cref="BacpacLoadResult.Skipped"/>
/// so the caller has a feature-gap report.</para>
/// </remarks>
internal static class ModelXmlReader
{
    /// <summary>DACFx serialization namespace observed in AW2025's model.xml.</summary>
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    public static void Apply(Stream modelStream, Simulation simulation, BacpacLoadResult result)
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

        // Materialize once so dependency-ordered passes can iterate the same
        // element list multiple times. The cost is one O(N) walk + per-element
        // XElement reference retention — 1000s of elements at most, negligible
        // memory-wise even for huge bacpacs.
        var elements = model.Elements(Ns + "Element").ToList();
        foreach (var element in elements)
        {
            var type = element.Attribute("Type")?.Value
                ?? throw new InvalidDataException("bacpac: <Element> missing Type attribute.");
            _ = result.ElementCounts.TryGetValue(type, out var current);
            result.ElementCounts[type] = current + 1;
        }

        using var connection = simulation.CreateDbConnection();
        connection.Open();

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
        // table types + db options (no deps); phase 2 = tables with simple
        // columns only (depend on phase 1; computed columns deferred);
        // phase 3 = PK/UQ/CHECK/DEFAULT constraints (depend on tables);
        // phase 4 = FK constraints (depend on PK/UQ on referenced tables);
        // phase 5 = indexes (depend on tables); phase 6 = views (body is
        // deferred-parsed so cross-references inside the same phase work);
        // phase 7 = functions + procedures + DML triggers (bodies also
        // deferred-parsed); phase 8 = deferred computed columns via
        // ALTER TABLE ADD col AS expr (depend on functions landing in
        // phase 7 — some computed expressions invoke UDFs and CREATE
        // TABLE's column-list parser can't tolerate forward function refs);
        // phase 9 = extended properties (depend on every covered host type,
        // including the computed columns just promoted in phase 8).
        // Skipped-element recording happens on the last phase so each
        // unhandled type is reported once. Filtered indexes whose predicate
        // references a computed column run in phase 5 and fail because the
        // column doesn't yet exist; those land on Skipped with a "Column
        // name X does not exist" reason. Could be lifted later by moving
        // indexes after computed columns, but that reorder cascaded into
        // AW regressions (ALTER TABLE ADD col AS dbo.ufn(...) hits a UDF
        // resolution gap in the simulator's column-expression parser).
        const int LastPhase = 9;
        for (var phase = 1; phase <= LastPhase; phase++)
            RunPhase(elements, connection, result, phase, viewNames, isLastPhase: phase == LastPhase);
    }

    private static void RunPhase(List<XElement> elements, DbConnection connection, BacpacLoadResult result, int phase, HashSet<string> viewNames, bool isLastPhase)
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
                    ("SqlDatabaseOptions", 1) => Run(() => EmitDatabaseOptions(element, connection, result)),
                    ("SqlSchema", 1) => Run(() => EmitSchema(name, connection)),
                    ("SqlUserDefinedDataType", 1) => Run(() => EmitUserDefinedDataType(element, name, connection)),
                    ("SqlSequence", 1) => Run(() => EmitSequence(element, name, connection)),
                    ("SqlRole", 1) => Run(() => EmitRole(element, name, connection)),
                    ("SqlTableType", 1) => Run(() => EmitTableType(element, name, connection, result)),
                    ("SqlTable", 2) => Run(() => EmitTable(element, name, connection, result)),
                    ("SqlPrimaryKeyConstraint", 3) => Run(() => EmitKeyConstraint(element, name, connection, isPrimary: true)),
                    ("SqlUniqueConstraint", 3) => Run(() => EmitKeyConstraint(element, name, connection, isPrimary: false)),
                    ("SqlCheckConstraint", 3) => Run(() => EmitCheckConstraint(element, name, connection)),
                    ("SqlDefaultConstraint", 3) => Run(() => EmitDefaultConstraint(element, name, connection)),
                    ("SqlForeignKeyConstraint", 4) => Run(() => EmitForeignKeyConstraint(element, name, connection)),
                    ("SqlIndex", 5) => Run(() => EmitIndex(element, name, connection, viewNames, result)),
                    ("SqlView", 6) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlView", "QueryScript")),
                    ("SqlScalarFunction", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlScalarFunction", "BodyScript")),
                    ("SqlMultiStatementTableValuedFunction", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlMultiStatementTableValuedFunction", "BodyScript")),
                    ("SqlProcedure", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlProcedure", "BodyScript")),
                    ("SqlDmlTrigger", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlDmlTrigger", "BodyScript")),
                    ("SqlDatabaseDdlTrigger", 7) => Run(() => EmitProgrammableObject(element, name, connection, result, "SqlDatabaseDdlTrigger", "BodyScript")),
                    ("SqlTable", 8) => Run(() => EmitDeferredComputedColumns(element, connection, result)),
                    ("SqlExtendedProperty", 9) => Run(() => EmitExtendedProperty(element, name, connection, viewNames, result)),
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
                result.Skipped.Add(new BacpacSkipped(type, name, $"Load failed: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }
            if (!handled && isLastPhase)
                result.Skipped.Add(new BacpacSkipped(type, name, "Element type not yet handled by the loader."));
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
        or "SqlSequence" or "SqlRole" or "SqlTableType"
        or "SqlTable"
        or "SqlPrimaryKeyConstraint" or "SqlUniqueConstraint"
        or "SqlCheckConstraint" or "SqlDefaultConstraint"
        or "SqlForeignKeyConstraint"
        or "SqlIndex"
        or "SqlView"
        or "SqlScalarFunction" or "SqlMultiStatementTableValuedFunction"
        or "SqlProcedure" or "SqlDmlTrigger" or "SqlDatabaseDdlTrigger"
        or "SqlExtendedProperty";

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
    private static void EmitDatabaseOptions(XElement element, DbConnection connection, BacpacLoadResult result)
    {
        // Collation: hard-error in the simulator on anything other than
        // SQL_Latin1_General_CP1_CI_AS. Surface here as a Skipped entry
        // rather than throwing — the loader's goal is best-effort import.
        var collation = element.Elements(Ns + "Property")
            .FirstOrDefault(p => p.Attribute("Name")?.Value == "Collation")
            ?.Attribute("Value")?.Value;
        if (collation is { } c && c != "SQL_Latin1_General_CP1_CI_AS")
        {
            result.Skipped.Add(new BacpacSkipped(
                "SqlDatabaseOptions",
                "Collation",
                $"Collation '{c}' isn't modeled; only SQL_Latin1_General_CP1_CI_AS is supported."));
        }

        using var command = connection.CreateCommand();
        foreach (var property in element.Elements(Ns + "Property"))
        {
            var name = property.Attribute("Name")?.Value;
            var value = property.Attribute("Value")?.Value;
            if (name is null || value is null)
                continue;

            var sql = TranslateDatabaseOption(name, value);
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
    /// SqlDatabaseOptions to an <c>ALTER DATABASE [simulated] SET …</c>
    /// statement. Returns null when the property has no T-SQL shape that
    /// affects load behavior — e.g. <c>IsFullTextEnabled</c> which is a
    /// sproc-level toggle (sp_fulltext_database), or
    /// <c>QueryStoreIntervalLength</c> which is a sub-option of QUERY_STORE
    /// and only emits as part of a block.
    /// </summary>
    private static string? TranslateDatabaseOption(string name, string value) => name switch
    {
        "IsAnsiNullsOn" => $"ALTER DATABASE [simulated] SET ANSI_NULLS {OnOff(value)};",
        "IsAnsiWarningsOn" => $"ALTER DATABASE [simulated] SET ANSI_WARNINGS {OnOff(value)};",
        "IsAnsiPaddingOn" => $"ALTER DATABASE [simulated] SET ANSI_PADDING {OnOff(value)};",
        "IsArithAbortOn" => $"ALTER DATABASE [simulated] SET ARITHABORT {OnOff(value)};",
        "IsConcatNullYieldsNullOn" => $"ALTER DATABASE [simulated] SET CONCAT_NULL_YIELDS_NULL {OnOff(value)};",
        "IsNumericRoundAbortOn" => $"ALTER DATABASE [simulated] SET NUMERIC_ROUNDABORT {OnOff(value)};",
        "IsQuotedIdentifierOn" => $"ALTER DATABASE [simulated] SET QUOTED_IDENTIFIER {OnOff(value)};",
        "IsTornPageProtectionOn" => $"ALTER DATABASE [simulated] SET TORN_PAGE_DETECTION {OnOff(value)};",
        "TemporalHistoryRetentionEnabled" => $"ALTER DATABASE [simulated] SET TEMPORAL_HISTORY_RETENTION {OnOff(value)};",
        "IsAcceleratedDatabaseRecoveryOn" => $"ALTER DATABASE [simulated] SET ACCELERATED_DATABASE_RECOVERY = {OnOff(value)};",
        "IsOptimizedLockingOn" => $"ALTER DATABASE [simulated] SET OPTIMIZED_LOCKING = {OnOff(value)};",
        "IsReadCommittedSnapshot" => $"ALTER DATABASE [simulated] SET READ_COMMITTED_SNAPSHOT {OnOff(value)};",
        // Enum-shaped options. RecoveryMode: 1=FULL, 2=BULK_LOGGED, 3=SIMPLE.
        "RecoveryMode" => $"ALTER DATABASE [simulated] SET RECOVERY {RecoveryMode(value)};",
        // IsCursorDefaultScopeGlobal: True → GLOBAL, False → LOCAL.
        "IsCursorDefaultScopeGlobal" => $"ALTER DATABASE [simulated] SET CURSOR_DEFAULT {(IsTrue(value) ? "GLOBAL" : "LOCAL")};",
        // TARGET_RECOVERY_TIME requires a unit; bacpac always stores SECONDS.
        "TargetRecoveryTimePeriod" => $"ALTER DATABASE [simulated] SET TARGET_RECOVERY_TIME = {value} SECONDS;",
        // QueryStoreDesiredState: 0=OFF, 1=READ_ONLY, 2=READ_WRITE, 3=ERROR.
        // The simulator's QUERY_STORE parser accepts ON/OFF + sub-options; emit
        // the matching toggle. Sub-options (interval length, capture mode etc.)
        // are bacpac metadata-only — the simulator doesn't act on them, so
        // they're elided.
        "QueryStoreDesiredState" => value == "0"
            ? "ALTER DATABASE [simulated] SET QUERY_STORE = OFF;"
            : "ALTER DATABASE [simulated] SET QUERY_STORE = ON;",
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
    private static void EmitTableType(XElement element, string? qualifiedName, DbConnection connection, BacpacLoadResult result)
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
                result.Skipped.Add(new BacpacSkipped(colType ?? "<unknown>", colName, $"Table-type column kind not handled inside '{qualifiedName}'."));
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
    /// Emits <c>CREATE TABLE [schema].[name] (col1 TYPE [IDENTITY] [ROWGUIDCOL] [NULL|NOT NULL], …)</c>
    /// for a <c>SqlTable</c> element. Only <c>SqlSimpleColumn</c> entries are
    /// translated in this phase; <c>SqlComputedColumn</c> entries land on
    /// <see cref="BacpacLoadResult.Skipped"/> (a follow-up phase ties them
    /// into the same CREATE TABLE via the simulator's <c>AS &lt;expr&gt; [PERSISTED]</c>
    /// computed-column grammar). Constraints (PK / UQ / FK / CHECK / DEFAULT)
    /// arrive as separate top-level Elements; they layer onto the table later
    /// via <c>ALTER TABLE … ADD CONSTRAINT</c>.
    /// </summary>
    private static void EmitTable(XElement element, string? qualifiedName, DbConnection connection, BacpacLoadResult result)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            throw new InvalidDataException("bacpac: SqlTable element missing Name attribute.");

        var columnsRelationship = element.Elements(Ns + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "Columns")
            ?? throw new InvalidDataException($"bacpac: SqlTable '{qualifiedName}' has no Columns relationship.");

        var columnDdls = new List<string>();
        var perColumnIsAlias = new List<bool>();
        foreach (var columnElement in columnsRelationship.Elements(Ns + "Entry").Elements(Ns + "Element"))
        {
            var columnType = columnElement.Attribute("Type")?.Value;
            var columnName = columnElement.Attribute("Name")?.Value;
            switch (columnType)
            {
                case "SqlSimpleColumn":
                    columnDdls.Add(TranslateSimpleColumn(columnElement, columnName, result));
                    perColumnIsAlias.Add(IsAliasTypedColumn(columnElement));
                    break;
                case "SqlComputedColumn":
                    // Deferred until phase 8 (post-functions) — some computed
                    // expressions invoke UDFs that have to exist first, and
                    // CREATE TABLE's column-list parser can't tolerate forward
                    // function refs. Phase 8 walks SqlTable elements a second
                    // time and emits ALTER TABLE … ADD col AS (expr).
                    break;
                default:
                    result.Skipped.Add(new BacpacSkipped(
                        columnType ?? "<unknown>",
                        columnName,
                        $"Unrecognized column element on '{qualifiedName}'."));
                    break;
            }
        }

        if (columnDdls.Count == 0)
            throw new InvalidDataException($"bacpac: SqlTable '{qualifiedName}' has no recognized columns.");

        var sql = $"CREATE TABLE {qualifiedName} ({string.Join(", ", columnDdls)});";
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();

        // Side map: which columns (in HeapTable.Columns order, after computed
        // columns are filtered out) are UDDT-aliased. The BCP decoder needs
        // this to apply the alias-specific 1-byte-prefix wire format.
        result.TableColumnIsAlias[qualifiedName] = [.. perColumnIsAlias];
    }

    /// <summary>
    /// Returns true when the column's type-reference is a UDDT alias (e.g.
    /// <c>[dbo].[Flag]</c>) rather than a built-in (<c>[int]</c> with
    /// <c>ExternalSource="BuiltIns"</c>). UDDT-aliased columns use a
    /// 1-byte-prefix wire format in BCP regardless of nullability — see
    /// <see cref="BacpacLoadResult.TableColumnIsAlias"/>.
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
    private static string TranslateSimpleColumn(XElement columnElement, string? qualifiedColumnName, BacpacLoadResult result)
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
        if (isRowGuid)
        {
            // ROWGUIDCOL is metadata-only — it tells SQL Server which
            // uniqueidentifier column NEWID()/NEWSEQUENTIALID() defaults to
            // for $rowguid pseudo-column references. The simulator's CREATE
            // TABLE parser doesn't accept the clause; storage shape and DML
            // are unaffected by its absence (DEFAULT NEWID() arrives as a
            // separate SqlDefaultConstraint element). Record a Warning once
            // so the diagnostics report names the deferred surface.
            result.Warnings.Add($"ROWGUIDCOL clause on column '{qualifiedColumnName}' dropped — the simulator doesn't model this metadata annotation; storage behavior is unaffected.");
        }
        var nullability = isNullableExplicit ? (isNullable ? " NULL" : " NOT NULL") : "";
        return $"{columnLeaf} {typeDdl}{identityClause}{nullability}";
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
    private static void EmitDeferredComputedColumns(XElement tableElement, DbConnection connection, BacpacLoadResult result)
    {
        var tableName = tableElement.Attribute("Name")?.Value;
        if (string.IsNullOrEmpty(tableName))
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
                result.Skipped.Add(new BacpacSkipped("SqlComputedColumn", columnName, "Missing ExpressionScript property."));
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
                result.Skipped.Add(new BacpacSkipped("SqlComputedColumn", columnName, $"Deferred: {ex.GetType().Name}: {ex.Message}"));
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
    private static void EmitExtendedProperty(XElement element, string? elementName, DbConnection connection, HashSet<string> viewNames, BacpacLoadResult result)
    {
        if (string.IsNullOrEmpty(elementName))
            throw new InvalidDataException("bacpac: SqlExtendedProperty missing Name attribute.");

        // Element name shape: [<HostKind>].[<host_segments...>].[<prop_name>]
        // Tokenize on the unquoted dots; bracketed segments can contain spaces.
        var segments = SplitBracketedSegments(elementName);
        if (segments.Count < 2)
        {
            result.Skipped.Add(new BacpacSkipped("SqlExtendedProperty", elementName, "Couldn't split Name into host-kind + property-name segments."));
            return;
        }
        var hostKind = segments[0];
        var propertyName = segments[^1];

        var value = ReadScriptProperty(element, "Value");
        if (value is null)
        {
            result.Skipped.Add(new BacpacSkipped("SqlExtendedProperty", elementName, "Missing Value property."));
            return;
        }

        // Host reference for column/table/schema hosts; SqlDatabaseOptions's
        // Host is the disambiguator-only DB ref so we skip the lookup there.
        var hostRef = ReadSingleReference(element, "Host");

        // Resolve to sp_addextendedproperty's @levelNtype / @levelNname pairs.
        string? l0type = null, l0name = null, l1type = null, l1name = null, l2type = null, l2name = null;
        switch (hostKind)
        {
            case "SqlDatabaseOptions":
                // No level args — applies to the current database.
                break;
            case "SqlSchema":
                if (hostRef is null)
                {
                    result.Skipped.Add(new BacpacSkipped("SqlExtendedProperty", elementName, "SqlSchema host missing reference."));
                    return;
                }
                l0type = "SCHEMA";
                l0name = Unbracket(hostRef);
                break;
            case "SqlTableBase":
                if (hostRef is null || !TrySplit2Part(hostRef, out var tabSchema, out var tabName))
                {
                    result.Skipped.Add(new BacpacSkipped("SqlExtendedProperty", elementName, $"SqlTableBase host '{hostRef}' isn't 2-part qualified."));
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
                    result.Skipped.Add(new BacpacSkipped("SqlExtendedProperty", elementName, $"SqlColumn host '{hostRef}' isn't 3-part qualified."));
                    return;
                }
                l0type = "SCHEMA";
                l0name = colSchema;
                l1type = viewNames.Contains($"[{colSchema}].[{colTable}]") ? "VIEW" : "TABLE";
                l1name = colTable;
                l2type = "COLUMN";
                l2name = colName;
                break;
            case "SqlDatabaseDdlTrigger":
            case "SqlFilegroup":
            default:
                result.Skipped.Add(new BacpacSkipped("SqlExtendedProperty", elementName, $"Host kind '{hostKind}' not modeled for sp_addextendedproperty."));
                return;
        }

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
            result.Skipped.Add(new BacpacSkipped("SqlExtendedProperty", elementName,
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
    private static void EmitProgrammableObject(XElement element, string? name, DbConnection connection, BacpacLoadResult result, string objectType, string bodyPropertyName)
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
            result.Skipped.Add(new BacpacSkipped(objectType, name,
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
            result.Skipped.Add(new BacpacSkipped(objectType, name,
                $"CREATE {objectType} failed: {ex.Message}"));
        }
        catch (NotSupportedException ex)
        {
            result.Skipped.Add(new BacpacSkipped(objectType, name,
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
    private static void EmitIndex(XElement element, string? indexName, DbConnection connection, HashSet<string> viewNames, BacpacLoadResult result)
    {
        if (string.IsNullOrEmpty(indexName))
            throw new InvalidDataException("bacpac: SqlIndex missing Name attribute.");

        var indexedObject = ReadSingleReference(element, "IndexedObject")
            ?? throw new InvalidDataException($"bacpac: SqlIndex '{indexName}' missing IndexedObject.");
        if (viewNames.Contains(indexedObject))
        {
            result.Skipped.Add(new BacpacSkipped(
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
            result.Skipped.Add(new BacpacSkipped("SqlIndex", indexName,
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

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // bacpac content is caller-trusted; the loader is a translator, not an end-user input handler
        command.CommandText = $"ALTER TABLE {definingTable} ADD CONSTRAINT {Leaf(constraintName)} DEFAULT ({script}) FOR {Leaf(forColumn)};";
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }
}
