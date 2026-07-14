using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SERVERPROPERTY('property_name')</c>: returns instance-level
/// configuration values. Real SQL Server projects this as
/// <c>sql_variant</c> carrying a per-property inner base type; the
/// simulator doesn't model sql_variant, so it surfaces the bare true type
/// instead — numeric properties as <see cref="SqlType.Int32"/> /
/// <see cref="SqlType.TinyInt"/>, string properties as
/// <see cref="SqlType.NVarchar"/>. When the property-name argument is a
/// compile-time constant the true type flows to the projection schema;
/// when it isn't, the type falls back to <see cref="SqlType.NVarchar"/>
/// and the runtime value is coerced to match (the static/runtime parity
/// contract). Unknown property name returns NULL (matches real-server
/// convention). Property names are case-insensitive.
/// </summary>
internal sealed class ServerProperty : Expression
{
    private static readonly FrozenDictionary<string, (SqlType Type, Func<RuntimeContext, SqlValue> Produce)> Properties = new Dictionary<string, (SqlType Type, Func<RuntimeContext, SqlValue> Produce)>
    {
        ["Edition"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("Developer Edition (64-bit)")),
        ["ProductLevel"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("RTM")),
        ["ProductVersion"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("17.0.0.0")),
        ["ProductMajorVersion"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("17")),
        ["ProductMinorVersion"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("0")),
        ["ProductBuild"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("0")),
        ["ProductBuildType"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("R")),
        // Real SQL Server returns NULL for ProductUpdateLevel on an RTM/GDR
        // build; the simulator reports build 0 / RTM, so NULL is correct.
        ["ProductUpdateLevel"] = (SqlType.NVarchar, _ => SqlValue.Null(SqlType.NVarchar)),
        ["ProductUpdateReference"] = (SqlType.NVarchar, _ => SqlValue.Null(SqlType.NVarchar)),
        ["EngineEdition"] = (SqlType.Int32, _ => SqlValue.FromInt32(3)),
        ["IsClustered"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsFullTextInstalled"] = (SqlType.Int32, _ => SqlValue.FromInt32(1)),
        ["IsHadrEnabled"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsIntegratedSecurityOnly"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsLocalDB"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsSingleUser"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsXTPSupported"] = (SqlType.Int32, _ => SqlValue.FromInt32(1)),
        ["LCID"] = (SqlType.Int32, _ => SqlValue.FromInt32(1033)),
        ["MachineName"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("SIMULATED")),
        ["ServerName"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("SIMULATED")),
        ["InstanceName"] = (SqlType.NVarchar, _ => SqlValue.Null(SqlType.NVarchar)),
        ["Collation"] = (SqlType.NVarchar, r => SqlValue.FromNVarchar(r.Batch.Connection.Simulation.ServerCollationName)),
        ["CollationID"] = (SqlType.Int32, _ => SqlValue.FromInt32(872468488)),
        ["ComparisonStyle"] = (SqlType.Int32, _ => SqlValue.FromInt32(196609)),
        ["SqlCharSet"] = (SqlType.TinyInt, _ => SqlValue.FromByte(1)),
        ["SqlCharSetName"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("iso_1")),
        ["SqlSortOrder"] = (SqlType.TinyInt, r => SqlValue.FromByte(SortIdFor(r.Batch.Connection.Simulation.ServerCollationName))),
        // No sort-order name table ships in the repo; "nocase_iso" is the name
        // for the default collation's sortId (52). Other SQL sort orders fall
        // back to "BIN" rather than their true probed name.
        ["SqlSortOrderName"] = (SqlType.NVarchar, r => SqlValue.FromNVarchar(SortIdFor(r.Batch.Connection.Simulation.ServerCollationName) == 52 ? "nocase_iso" : "BIN")),
        ["ResourceVersion"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("17.00.0")),
        ["BuildClrVersion"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("v4.0.30319")),
        ["EditionID"] = (SqlType.Int32, _ => SqlValue.FromInt32(-2117995310)),
        ["FilestreamShareName"] = (SqlType.NVarchar, _ => SqlValue.Null(SqlType.NVarchar)),
        ["FilestreamConfiguredLevel"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["FilestreamEffectiveLevel"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsAdvancedAnalyticsInstalled"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsPolyBaseInstalled"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsTempDbMetadataMemoryOptimized"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
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
            return SqlValue.Null(SqlType.NVarchar);
        var name = n.CoerceTo(SqlType.NVarchar).AsString;
        if (!Properties.TryGetValue(name, out var def))
            return SqlValue.Null(SqlType.NVarchar);
        var value = def.Produce(runtime);
        // A non-constant name argument couldn't resolve a true type at parse
        // time (GetSqlType fell back to NVarchar); coerce so runtime agrees.
        return this.nameArg is Value ? value : value.CoerceTo(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => this.nameArg is Value { Constant: { IsNull: false } constant }
            && Properties.TryGetValue(constant.CoerceTo(SqlType.NVarchar).AsString, out var def)
            ? def.Type
            : SqlType.NVarchar;

    // Derive the SQL sort-order id from the collation name; real SQL Server
    // reports 0 for collations with no SQL_* sort order.
    private static byte SortIdFor(string collationName)
        => Collation.SqlServerSortOrders.TryGetValue(collationName, out var so) ? checked((byte)so.OrderNumber) : (byte)0;

    internal override string DebugDisplay() => $"SERVERPROPERTY({this.nameArg.DebugDisplay()})";
}
