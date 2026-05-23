using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SERVERPROPERTY('property_name')</c>: returns instance-level
/// configuration values. Real SQL Server projects this as
/// <c>sql_variant</c>; the simulator doesn't model sql_variant so values
/// surface as <see cref="SqlType.NVarchar"/> — callers casting to int
/// through SqlClient see numeric properties decoded via implicit
/// conversion. Unknown property name returns NULL (matches real-server
/// convention). Property names are case-insensitive.
/// </summary>
internal sealed class ServerProperty : Expression
{
    private static readonly Dictionary<string, Func<RuntimeContext, SqlValue>> Properties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Edition"] = _ => SqlValue.FromNVarchar("Developer Edition (64-bit)"),
        ["ProductLevel"] = _ => SqlValue.FromNVarchar("RTM"),
        ["ProductVersion"] = _ => SqlValue.FromNVarchar("17.0.0.0"),
        ["ProductMajorVersion"] = _ => SqlValue.FromNVarchar("17"),
        ["ProductMinorVersion"] = _ => SqlValue.FromNVarchar("0"),
        ["ProductBuild"] = _ => SqlValue.FromNVarchar("0"),
        ["ProductBuildType"] = _ => SqlValue.FromNVarchar("R"),
        ["ProductUpdateLevel"] = _ => SqlValue.FromNVarchar("RTM"),
        ["ProductUpdateReference"] = _ => SqlValue.Null(SqlType.NVarchar),
        ["EngineEdition"] = _ => SqlValue.FromNVarchar("3"),
        ["IsClustered"] = _ => SqlValue.FromNVarchar("0"),
        ["IsFullTextInstalled"] = _ => SqlValue.FromNVarchar("1"),
        ["IsHadrEnabled"] = _ => SqlValue.FromNVarchar("0"),
        ["IsIntegratedSecurityOnly"] = _ => SqlValue.FromNVarchar("0"),
        ["IsLocalDB"] = _ => SqlValue.FromNVarchar("0"),
        ["IsSingleUser"] = _ => SqlValue.FromNVarchar("0"),
        ["IsXTPSupported"] = _ => SqlValue.FromNVarchar("1"),
        ["LCID"] = _ => SqlValue.FromNVarchar("1033"),
        ["MachineName"] = _ => SqlValue.FromNVarchar("SIMULATED"),
        ["ServerName"] = _ => SqlValue.FromNVarchar("SIMULATED"),
        ["InstanceName"] = _ => SqlValue.Null(SqlType.NVarchar),
        ["Collation"] = r => SqlValue.FromNVarchar(r.Batch.Connection.Simulation.ServerCollationName),
        ["CollationID"] = _ => SqlValue.FromNVarchar("872468488"),
        ["ComparisonStyle"] = _ => SqlValue.FromNVarchar("196609"),
        ["SqlCharSet"] = _ => SqlValue.FromNVarchar("1"),
        ["SqlCharSetName"] = _ => SqlValue.FromNVarchar("iso_1"),
        ["SqlSortOrder"] = _ => SqlValue.FromNVarchar("0"),
        ["SqlSortOrderName"] = _ => SqlValue.FromNVarchar("BIN"),
        ["ResourceVersion"] = _ => SqlValue.FromNVarchar("17.00.0"),
        ["BuildClrVersion"] = _ => SqlValue.FromNVarchar("v4.0.30319"),
        ["EditionID"] = _ => SqlValue.FromNVarchar("-2117995310"),
        ["FilestreamShareName"] = _ => SqlValue.Null(SqlType.NVarchar),
        ["FilestreamConfiguredLevel"] = _ => SqlValue.FromNVarchar("0"),
        ["FilestreamEffectiveLevel"] = _ => SqlValue.FromNVarchar("0"),
        ["IsAdvancedAnalyticsInstalled"] = _ => SqlValue.FromNVarchar("0"),
        ["IsPolyBaseInstalled"] = _ => SqlValue.FromNVarchar("0"),
        ["IsTempDbMetadataMemoryOptimized"] = _ => SqlValue.FromNVarchar("0"),
    };

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
            return SqlValue.Null(SqlType.NVarchar);
        var name = n.CoerceTo(SqlType.NVarchar).AsString;
        return Properties.TryGetValue(name, out var producer)
            ? producer(runtime)
            : SqlValue.Null(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"SERVERPROPERTY({this.nameArg.DebugDisplay()})";
}
