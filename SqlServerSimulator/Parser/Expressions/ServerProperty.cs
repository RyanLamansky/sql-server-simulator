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
        var value = Produce(n.CoerceTo(SqlType.NVarchar).AsString, runtime);
        return value.IsNull ? SqlValue.Null(SqlType.SqlVariant) : SqlValue.FromVariant(value);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    /// <summary>
    /// Resolves one property to its inner value; a NULL result (a null-valued
    /// property, or an unrecognized name via the default arm) becomes the NULL
    /// <c>sql_variant</c> in <see cref="Run"/>. The version identity derives
    /// from <see cref="ReferenceBuild"/>.
    /// </summary>
    private static SqlValue Produce(string name, RuntimeContext runtime)
    {
        // Longer than any recognized property name; also bounds the stackalloc
        // against an adversarially long argument.
        if (name.Length > 32)
            return SqlValue.Null(SqlType.SqlVariant);
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "BUILDCLRVERSION" => SqlValue.FromNVarchar("v4.0.30319"),
            "COLLATION" => SqlValue.FromNVarchar(runtime.Batch.Connection.Simulation.ServerCollationName),
            "COLLATIONID" => SqlValue.FromInt32(872468488),
            "COMPARISONSTYLE" => SqlValue.FromInt32(196609),
            // Must be non-NULL: SSMS Activity Monitor reads it at startup and
            // casts without a NULL check ("Object cannot be cast from DBNull
            // to other types").
            "COMPUTERNAMEPHYSICALNETBIOS" => SqlValue.FromNVarchar("SIMULATED"),
            "EDITION" => SqlValue.FromNVarchar("Developer Edition (64-bit)"),
            "EDITIONID" => SqlValue.FromInt32(-2117995310),
            "ENGINEEDITION" => SqlValue.FromInt32(3),
            "FILESTREAMCONFIGUREDLEVEL" => SqlValue.FromInt32(0),
            "FILESTREAMEFFECTIVELEVEL" => SqlValue.FromInt32(0),
            "FILESTREAMSHARENAME" => SqlValue.Null(SqlType.NVarchar),
            "INSTANCENAME" => SqlValue.Null(SqlType.NVarchar),
            "ISADVANCEDANALYTICSINSTALLED" => SqlValue.FromInt32(0),
            "ISCLUSTERED" => SqlValue.FromInt32(0),
            "ISFULLTEXTINSTALLED" => SqlValue.FromInt32(1),
            "ISHADRENABLED" => SqlValue.FromInt32(0),
            "ISINTEGRATEDSECURITYONLY" => SqlValue.FromInt32(0),
            "ISLOCALDB" => SqlValue.FromInt32(0),
            "ISPOLYBASEINSTALLED" => SqlValue.FromInt32(0),
            "ISSINGLEUSER" => SqlValue.FromInt32(0),
            "ISTEMPDBMETADATAMEMORYOPTIMIZED" => SqlValue.FromInt32(0),
            "ISXTPSUPPORTED" => SqlValue.FromInt32(1),
            "LCID" => SqlValue.FromInt32(1033),
            "MACHINENAME" => SqlValue.FromNVarchar("SIMULATED"),
            // Real reports the engine's OS process id; the simulator's engine
            // process is the host process, so its id is the faithful value.
            // Must be non-NULL — Activity Monitor casts it like the NetBIOS
            // name above.
            "PROCESSID" => SqlValue.FromInt32(Environment.ProcessId),
            "PRODUCTBUILD" => SqlValue.FromNVarchar(ReferenceBuild.ProductBuild),
            // Real SQL Server reports NULL for ProductBuildType on a CU build
            // (it's non-null only for GDR/OD servicing branches).
            "PRODUCTBUILDTYPE" => SqlValue.Null(SqlType.NVarchar),
            "PRODUCTLEVEL" => SqlValue.FromNVarchar("RTM"),
            "PRODUCTMAJORVERSION" => SqlValue.FromNVarchar(ReferenceBuild.ProductMajorVersion),
            "PRODUCTMINORVERSION" => SqlValue.FromNVarchar(ReferenceBuild.ProductMinorVersion),
            "PRODUCTUPDATELEVEL" => SqlValue.FromNVarchar(ReferenceBuild.UpdateLevel),
            "PRODUCTUPDATEREFERENCE" => SqlValue.FromNVarchar(ReferenceBuild.UpdateReference),
            "PRODUCTVERSION" => SqlValue.FromNVarchar(ReferenceBuild.ProductVersion),
            "RESOURCEVERSION" => SqlValue.FromNVarchar(ReferenceBuild.MajorMinorBuild),
            "SERVERNAME" => SqlValue.FromNVarchar("SIMULATED"),
            "SQLCHARSET" => SqlValue.FromByte(1),
            "SQLCHARSETNAME" => SqlValue.FromNVarchar("iso_1"),
            "SQLSORTORDER" => SqlValue.FromByte(SortIdFor(runtime.Batch.Connection.Simulation.ServerCollationName)),
            // No sort-order name table ships in the repo; "nocase_iso" is the
            // name for the default collation's sortId (52). Other SQL sort
            // orders fall back to "BIN" rather than their true probed name.
            "SQLSORTORDERNAME" => SqlValue.FromNVarchar(SortIdFor(runtime.Batch.Connection.Simulation.ServerCollationName) == 52 ? "nocase_iso" : "BIN"),
            _ => SqlValue.Null(SqlType.SqlVariant),
        };
    }

    // Derive the SQL sort-order id from the collation name; real SQL Server
    // reports 0 for collations with no SQL_* sort order.
    private static byte SortIdFor(string collationName)
        => Collation.SqlServerSortOrders.TryGetValue(collationName, out var so) ? checked((byte)so.OrderNumber) : (byte)0;

    internal override string DebugDisplay() => $"SERVERPROPERTY({this.nameArg.DebugDisplay()})";
}
