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

        // Phase 1: database options + schemas + UDDTs. These don't depend on
        // any other DDL and must land before tables (column types may
        // reference an alias type, FK definitions may reference a schema, etc.).
        RunPhase(elements, connection, result, isPhase1: true);
        // Phase 2: tables. Constraints / indexes / views / etc. arrive in
        // later phases as they're added.
        RunPhase(elements, connection, result, isPhase1: false);
    }

    private static void RunPhase(List<XElement> elements, DbConnection connection, BacpacLoadResult result, bool isPhase1)
    {
        foreach (var element in elements)
        {
            var type = element.Attribute("Type")?.Value!;
            var name = element.Attribute("Name")?.Value;
            switch ((type, isPhase1))
            {
                case ("SqlDatabaseOptions", true):
                    EmitDatabaseOptions(element, connection, result);
                    break;
                case ("SqlSchema", true):
                    EmitSchema(name, connection);
                    break;
                case ("SqlUserDefinedDataType", true):
                    EmitUserDefinedDataType(element, name, connection);
                    break;
                case ("SqlTable", false):
                    EmitTable(element, name, connection, result);
                    break;
                case (_, true):
                    // Skipped recording happens in phase 2's default arm so
                    // an element doesn't get reported twice.
                    break;
                default:
                    if (type is not "SqlDatabaseOptions" and not "SqlSchema" and not "SqlUserDefinedDataType" and not "SqlTable")
                        result.Skipped.Add(new BacpacSkipped(type, name, "Element type not yet handled by the loader."));
                    break;
            }
        }
    }

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
        foreach (var columnElement in columnsRelationship.Elements(Ns + "Entry").Elements(Ns + "Element"))
        {
            var columnType = columnElement.Attribute("Type")?.Value;
            var columnName = columnElement.Attribute("Name")?.Value;
            switch (columnType)
            {
                case "SqlSimpleColumn":
                    columnDdls.Add(TranslateSimpleColumn(columnElement, columnName, result));
                    break;
                case "SqlComputedColumn":
                    // Deferred to a follow-up phase. Recording on Skipped so
                    // the caller has a per-column inventory of what's missing.
                    result.Skipped.Add(new BacpacSkipped(
                        "SqlComputedColumn",
                        columnName,
                        $"Computed column on '{qualifiedName}' deferred to a follow-up phase."));
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
        var nullability = isNullable ? " NULL" : " NOT NULL";
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
    /// → <c>hierarchyid</c>. The simulator's <see cref="SqlType.GetByName"/>
    /// keyword table doesn't include <c>sysname</c> (it's a sys-schema alias
    /// over nvarchar(128) rather than a parser keyword); the loader expands
    /// the alias inline so DACFx's <c>[sys].[sysname]</c> reaches the parser
    /// as <c>nvarchar(128)</c>. Surface fidelity loss: <c>sys.columns</c>
    /// reports nvarchar(128) instead of sysname for the affected columns —
    /// acceptable for the loader baseline (storage shape is identical).
    /// </summary>
    private static string NormalizeBuiltinName(string bracketedName)
    {
        var lastDot = bracketedName.LastIndexOf('.');
        var lastSegment = lastDot < 0 ? bracketedName : bracketedName[(lastDot + 1)..];
        var bare = lastSegment.Trim('[', ']');
        return string.Equals(bare, "sysname", StringComparison.OrdinalIgnoreCase) ? "nvarchar(128)" : bare;
    }

    private static bool ReadBoolProperty(XElement element, string name, bool defaultValue) =>
        ReadStringProperty(element, name) is { } value ? IsTrue(value) : defaultValue;

    private static string? ReadStringProperty(XElement element, string name) =>
        element.Elements(Ns + "Property")
            .FirstOrDefault(p => p.Attribute("Name")?.Value == name)
            ?.Attribute("Value")?.Value;
}
