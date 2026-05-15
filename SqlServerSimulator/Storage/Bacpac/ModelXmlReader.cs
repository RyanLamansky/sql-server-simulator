using System.Data.Common;
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

        using var connection = simulation.CreateDbConnection();
        connection.Open();

        foreach (var element in model.Elements(Ns + "Element"))
        {
            var type = element.Attribute("Type")?.Value
                ?? throw new InvalidDataException("bacpac: <Element> missing Type attribute.");
            var name = element.Attribute("Name")?.Value;

            _ = result.ElementCounts.TryGetValue(type, out var current);
            result.ElementCounts[type] = current + 1;

            switch (type)
            {
                case "SqlDatabaseOptions":
                    EmitDatabaseOptions(element, connection, result);
                    break;
                case "SqlSchema":
                    EmitSchema(name, connection);
                    break;
                default:
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
}
