using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SERVERPROPERTY('property_name')</c>: returns instance-level
/// configuration values. Like real SQL Server, the result is always
/// <c>sql_variant</c> (<see cref="SqlType.SqlVariant"/>); each property
/// carries its probed inner base type — numeric properties as
/// <see cref="SqlType.Int32"/> / <see cref="SqlType.TinyInt"/>, string
/// properties as <see cref="SqlType.NVarchar"/> — so the projection reports
/// <c>sql_variant</c> COLMETADATA (0x62 over the wire) and each cell surfaces
/// its inner CLR type. An unknown property name (or NULL) returns a NULL
/// <c>sql_variant</c>. Property names are case-insensitive.
/// </summary>
internal sealed class ServerProperty : Expression
{
    private static readonly FrozenDictionary<string, Func<RuntimeContext, SqlValue>> Properties = new Dictionary<string, Func<RuntimeContext, SqlValue>>
    {
        ["Edition"] = _ => SqlValue.FromNVarchar("Developer Edition (64-bit)"),
        ["ProductLevel"] = _ => SqlValue.FromNVarchar("RTM"),
        // Version identity mirrors the SQL Server 2025 reference build the
        // simulator emulates (17.0.4065.4, RTM-CU7 / KB5096981); a real build
        // number is what lets SSMS's per-build client feature gates proceed.
        ["ProductVersion"] = _ => SqlValue.FromNVarchar("17.0.4065.4"),
        ["ProductMajorVersion"] = _ => SqlValue.FromNVarchar("17"),
        ["ProductMinorVersion"] = _ => SqlValue.FromNVarchar("0"),
        ["ProductBuild"] = _ => SqlValue.FromNVarchar("4065"),
        // Real SQL Server reports NULL for ProductBuildType on a CU build
        // (it's non-null only for GDR/OD servicing branches).
        ["ProductBuildType"] = _ => SqlValue.Null(SqlType.NVarchar),
        ["ProductUpdateLevel"] = _ => SqlValue.FromNVarchar("CU7"),
        ["ProductUpdateReference"] = _ => SqlValue.FromNVarchar("KB5096981"),
        ["EngineEdition"] = _ => SqlValue.FromInt32(3),
        ["IsClustered"] = _ => SqlValue.FromInt32(0),
        ["IsFullTextInstalled"] = _ => SqlValue.FromInt32(1),
        ["IsHadrEnabled"] = _ => SqlValue.FromInt32(0),
        ["IsIntegratedSecurityOnly"] = _ => SqlValue.FromInt32(0),
        ["IsLocalDB"] = _ => SqlValue.FromInt32(0),
        ["IsSingleUser"] = _ => SqlValue.FromInt32(0),
        ["IsXTPSupported"] = _ => SqlValue.FromInt32(1),
        ["LCID"] = _ => SqlValue.FromInt32(1033),
        ["MachineName"] = _ => SqlValue.FromNVarchar("SIMULATED"),
        ["ServerName"] = _ => SqlValue.FromNVarchar("SIMULATED"),
        ["InstanceName"] = _ => SqlValue.Null(SqlType.NVarchar),
        // Real reports the engine's OS process id and physical host name; both
        // must be non-NULL — SSMS Activity Monitor casts them without a NULL
        // check ("Object cannot be cast from DBNull to other types"). The
        // simulator's engine process is the host process, so its id is the
        // faithful value.
        ["ProcessID"] = _ => SqlValue.FromInt32(Environment.ProcessId),
        ["ComputerNamePhysicalNetBIOS"] = _ => SqlValue.FromNVarchar("SIMULATED"),
        ["Collation"] = r => SqlValue.FromNVarchar(r.Batch.Connection.Simulation.ServerCollationName),
        ["CollationID"] = _ => SqlValue.FromInt32(872468488),
        ["ComparisonStyle"] = _ => SqlValue.FromInt32(196609),
        ["SqlCharSet"] = _ => SqlValue.FromByte(1),
        ["SqlCharSetName"] = _ => SqlValue.FromNVarchar("iso_1"),
        ["SqlSortOrder"] = r => SqlValue.FromByte(SortIdFor(r.Batch.Connection.Simulation.ServerCollationName)),
        // No sort-order name table ships in the repo; "nocase_iso" is the name
        // for the default collation's sortId (52). Other SQL sort orders fall
        // back to "BIN" rather than their true probed name.
        ["SqlSortOrderName"] = r => SqlValue.FromNVarchar(SortIdFor(r.Batch.Connection.Simulation.ServerCollationName) == 52 ? "nocase_iso" : "BIN"),
        ["ResourceVersion"] = _ => SqlValue.FromNVarchar("17.00.4065"),
        ["BuildClrVersion"] = _ => SqlValue.FromNVarchar("v4.0.30319"),
        ["EditionID"] = _ => SqlValue.FromInt32(-2117995310),
        ["FilestreamShareName"] = _ => SqlValue.Null(SqlType.NVarchar),
        ["FilestreamConfiguredLevel"] = _ => SqlValue.FromInt32(0),
        ["FilestreamEffectiveLevel"] = _ => SqlValue.FromInt32(0),
        ["IsAdvancedAnalyticsInstalled"] = _ => SqlValue.FromInt32(0),
        ["IsPolyBaseInstalled"] = _ => SqlValue.FromInt32(0),
        ["IsTempDbMetadataMemoryOptimized"] = _ => SqlValue.FromInt32(0),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly Expression nameArg;

    public ServerProperty(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var n = this.nameArg.Run(runtime);
        if (n.IsNull)
            return SqlValue.Null(SqlType.SqlVariant);
        var name = n.CoerceTo(SqlType.NVarchar).AsString;
        if (!Properties.TryGetValue(name, out var produce))
            return SqlValue.Null(SqlType.SqlVariant);
        var value = produce(runtime);
        return value.IsNull ? SqlValue.Null(SqlType.SqlVariant) : SqlValue.FromVariant(value);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    // Derive the SQL sort-order id from the collation name; real SQL Server
    // reports 0 for collations with no SQL_* sort order.
    private static byte SortIdFor(string collationName)
        => Collation.SqlServerSortOrders.TryGetValue(collationName, out var so) ? checked((byte)so.OrderNumber) : (byte)0;

    internal override string DebugDisplay() => $"SERVERPROPERTY({this.nameArg.DebugDisplay()})";
}
