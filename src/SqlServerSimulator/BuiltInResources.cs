using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    public static readonly Lazy<Dictionary<string, CatalogView>> CatalogViews = new(BuildCatalogViews);

    // Catalog-pinned types reused across catalog views — Latin1_General_CI_AS_KS_WS
    // at Implicit rank, matching what real SQL Server's catalog DDL pins
    // for _desc enum columns, permission_name, and the char(1)/char(2)
    // type/state code columns. See Collation.Catalog for empirical
    // grounding and the contained-DB-vs-non-contained-DB distinction.
    private static readonly NVarcharSqlType nvarchar60Catalog = NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit);
    private static readonly NVarcharSqlType nvarchar128Catalog = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);

    // numeric(25, 0) — the log-sequence-number (LSN) storage shape shared
    // by the mirroring / replica-state / master-files views. Always surfaced
    // NULL here (the simulator has no physical log), so precision matters
    // only for the column schema clients read back.
    private static readonly SqlType lsnNumeric = SqlType.GetDecimal(25, 0);

    // char(2) type-code cell shared across the object / constraint / filegroup
    // catalog views ('U ' table, 'PK' / 'UQ' / 'C ' constraint, 'FG' filegroup);
    // char(1) for the single-character permission / principal state and type codes.
    private static readonly CharSqlType charTwo = CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit);
    private static readonly CharSqlType charOne = CharSqlType.Get(1, Collation.Catalog, Coercibility.Implicit);
    private static readonly SqlValue notMsShipped = SqlValue.FromBoolean(false);
    private static readonly SqlValue defaultCollation = SqlValue.FromSystemName("SQL_Latin1_General_CP1_CI_AS");

    /// <summary>
    /// Shared empty row set for the AlwaysOn Availability-Group catalog views
    /// (<c>sys.availability_replicas</c> / <c>sys.availability_groups</c> /
    /// <c>sys.dm_hadr_database_replica_states</c>). No AGs are ever configured
    /// in the simulator, so all three project zero rows; SSMS's enumeration
    /// relies only on their column shape resolving for its
    /// <c>INSERT … SELECT … FROM</c> preamble.
    /// </summary>
    private static readonly SqlValue[][] EmptyCatalogRows = [];

    /// <summary>
    /// Registers the <c>sys.&lt;view&gt;</c> and <c>INFORMATION_SCHEMA.&lt;view&gt;</c>
    /// virtual tables. Each row generator projects from live <see cref="Database"/> /
    /// <see cref="Schema"/> / <see cref="HeapTable"/> metadata at iteration
    /// time, so changes made earlier in the same batch (CREATE TABLE,
    /// CREATE SCHEMA, DROP TABLE) appear immediately in the next read. Keys
    /// are fully-qualified names (<c>"sys.tables"</c>,
    /// <c>"INFORMATION_SCHEMA.COLUMNS"</c>) so the single resolver in
    /// <see cref="Parser.BatchContext.TryResolveCatalogView"/> can serve both
    /// schemas without per-namespace dispatch. Shipped views: <c>sys.schemas</c>
    /// / <c>sys.tables</c> / <c>sys.objects</c> / <c>sys.columns</c> (load-
    /// bearing subset of real SQL Server's column set) plus
    /// <c>INFORMATION_SCHEMA.TABLES</c> / <c>.COLUMNS</c> / <c>.SCHEMATA</c>
    /// (the full ISO column shape).
    /// </summary>
    private static Dictionary<string, CatalogView> BuildCatalogViews()
    {
        var views = new Dictionary<string, CatalogView>(BuiltInToken.Comparer);
        RegisterCoreObjects(views);
        RegisterColumnFamily(views);
        RegisterProgrammable(views);
        RegisterConstraintsAndTriggers(views);
        RegisterIndexes(views);
        RegisterSecurity(views);
        RegisterFullTextXmlSpatial(views);
        RegisterServiceBroker(views);
        RegisterServerAndDatabases(views);
        RegisterLegacyCompat(views);
        ApplyMetadataVisibility(views);
        return views;
    }
}
