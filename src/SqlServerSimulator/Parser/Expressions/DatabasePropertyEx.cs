using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATABASEPROPERTYEX(database_name, property_name)</c>: returns
/// the named property of a database. Like real SQL Server, the result is
/// always <c>sql_variant</c> (<see cref="SqlType.SqlVariant"/>); each
/// property carries its probed inner base type — numeric properties as
/// <see cref="SqlType.Int32"/> / <see cref="SqlType.TinyInt"/>, string
/// properties as <see cref="SqlType.NVarchar"/>, <c>LastGoodCheckDbTime</c>
/// as <see cref="SqlType.DateTime"/> — so the projection reports
/// <c>sql_variant</c> and each cell surfaces its inner type. NULL database /
/// NULL property → NULL <c>sql_variant</c>; unknown database → NULL;
/// unknown property → NULL (matches real SQL Server, probe-confirmed
/// 2026-05-16).
/// </summary>
/// <remarks>
/// Every documented property answers as SQL Server 2025 does (probed
/// 2026-09-26 on a default database and one with every switch moved): the
/// <c>ALTER DATABASE … SET</c> switches read <see cref="Database.Switches"/>,
/// <c>Recovery</c> / <c>UserAccess</c> / <c>Updateability</c> the database's
/// state, <c>ComparisonStyle</c> / <c>LCID</c> its collation, and the
/// unmodeled replication, clone and standby states read off.
/// Properties not on the list return NULL. <see cref="Produce"/>
/// resolves a property to its inner value; a NULL result (a null-valued
/// property, or an unrecognized name via the default arm) becomes the NULL
/// <c>sql_variant</c> in <see cref="Run"/>.
/// </remarks>
internal sealed class DatabasePropertyEx : Expression
{
    private readonly Expression dbNameArg;
    private readonly Expression propertyArg;

    public DatabasePropertyEx(ParserContext context)
    {
        this.dbNameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("DATABASEPROPERTYEX", 2);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var dbNameValue = this.dbNameArg.Run(runtime);
        var propertyValue = this.propertyArg.Run(runtime);
        if (dbNameValue.IsNull || propertyValue.IsNull)
            return SqlValue.Null(SqlType.SqlVariant);

        var dbName = dbNameValue.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)).AsString;
        var property = propertyValue.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)).AsString;

        // The simulator's database dictionary is keyed by name; only the
        // currently-attached databases resolve.
        if (!runtime.Batch.Connection.Simulation.Databases.TryGetValue(dbName, out var db))
            return SqlValue.Null(SqlType.SqlVariant);

        var value = Produce(property, db);
        return value.IsNull ? SqlValue.Null(SqlType.SqlVariant) : SqlValue.FromVariant(value);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = AssignmentRules.ArgumentType(this.dbNameArg, SqlType.NVarchar, batch, resolveColumnType);
        return SqlType.SqlVariant;
    }

    private static SqlValue Produce(string property, Database db)
    {
        // Longer than any recognized property name; also bounds the stackalloc
        // against an adversarially long argument.
        if (property.Length > 64)
            return SqlValue.Null(SqlType.SqlVariant);
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        var switches = db.Switches;
        SqlValue Switch(DatabaseSwitches flag) => SqlValue.FromInt32((switches & flag) != 0 ? 1 : 0);
        var hasMetrics = Collation.TryGetMetrics(db.CollationName, out var metrics);
        return upper switch
        {
            "COLLATION" => SqlValue.FromNVarchar(db.CollationName),
            "COMPARISONSTYLE" => hasMetrics ? SqlValue.FromInt32(metrics.ComparisonStyle) : SqlValue.Null(SqlType.SqlVariant),
            "ISANSINULLDEFAULT" => Switch(DatabaseSwitches.AnsiNullDefault),
            "ISANSINULLSENABLED" => Switch(DatabaseSwitches.AnsiNulls),
            "ISANSIPADDINGENABLED" => Switch(DatabaseSwitches.AnsiPadding),
            "ISANSIWARNINGSENABLED" => Switch(DatabaseSwitches.AnsiWarnings),
            "ISARITHMETICABORTENABLED" => Switch(DatabaseSwitches.ArithAbort),
            "ISAUTOCLOSE" => Switch(DatabaseSwitches.AutoClose),
            "ISAUTOCREATESTATISTICS" => Switch(DatabaseSwitches.AutoCreateStatistics),
            "ISAUTOCREATESTATISTICSINCREMENTAL" => Switch(DatabaseSwitches.AutoCreateStatisticsIncremental),
            "ISAUTOSHRINK" => Switch(DatabaseSwitches.AutoShrink),
            "ISAUTOUPDATESTATISTICS" => Switch(DatabaseSwitches.AutoUpdateStatistics),
            // Undocumented, but answered: tinyint 1 while the database tracks changes.
            "ISCHANGETRACKINGENABLED" => SqlValue.FromByte(db.ChangeTracking is null ? (byte)0 : (byte)1),
            // The clone and snapshot-backup states read tinyint, and the
            // replication, standby and memory-optimized ones int — all off.
            "ISCLONE" or "ISDATABASESUSPENDEDFORSNAPSHOTBACKUP" or "ISVERIFIEDCLONE" => SqlValue.FromByte(0),
            "ISCLOSECURSORSONCOMMITENABLED" => Switch(DatabaseSwitches.CursorCloseOnCommit),
            "ISFULLTEXTENABLED" => SqlValue.FromInt32(BuiltInResources.ReportsFullTextEnabled(db) ? 1 : 0),
            "ISINSTANDBY" or "ISMEMORYOPTIMIZEDELEVATETOSNAPSHOTENABLED" or "ISMERGEPUBLISHED" or "ISPUBLISHED" or "ISSUBSCRIBED"
                or "ISSYNCWITHBACKUP" => SqlValue.FromInt32(0),
            "ISLOCALCURSORSDEFAULT" => Switch(DatabaseSwitches.LocalCursorDefault),
            "ISNULLCONCAT" => Switch(DatabaseSwitches.ConcatNullYieldsNull),
            "ISNUMERICROUNDABORTENABLED" => Switch(DatabaseSwitches.NumericRoundAbort),
            "ISPARAMETERIZATIONFORCED" => Switch(DatabaseSwitches.ParameterizationForced),
            "ISQUOTEDIDENTIFIERSENABLED" => Switch(DatabaseSwitches.QuotedIdentifier),
            "ISREADCOMMITTEDSNAPSHOTON" => SqlValue.FromInt32(db.ReadCommittedSnapshot ? 1 : 0),
            "ISRECURSIVETRIGGERSENABLED" => SqlValue.FromInt32(db.RecursiveTriggers ? 1 : 0),
            "ISTORNPAGEDETECTIONENABLED" => SqlValue.FromInt32(db.PageVerify == 1 ? 1 : 0),
            "ISXTPSUPPORTED" => SqlValue.FromByte(1),
            // DBCC CHECKDB isn't modeled, so the last-good-checkdb time is a
            // NULL sql_variant. SMO's CAST(ISNULL(..., 0) AS datetime) resolves
            // to 1900-01-01: ISNULL over the NULL variant fixes to sql_variant
            // wrapping the int 0, which CASTs to the datetime epoch (matching
            // real, probe-confirmed 2026-07-19).
            "LASTGOODCHECKDBTIME" => SqlValue.Null(SqlType.DateTime),
            "LCID" => SqlValue.FromInt32(hasMetrics ? metrics.Lcid : 1033),
            "RECOVERY" => SqlValue.FromNVarchar(db.RecoveryModel switch
            {
                RecoveryModel.Simple => "SIMPLE",
                RecoveryModel.BulkLogged => "BULK_LOGGED",
                _ => "FULL",
            }),
            "SNAPSHOTISOLATIONSTATE" => SqlValue.FromInt32(db.AllowSnapshotIsolation ? 1 : 0),
            "SQLSORTORDER" => SqlValue.FromByte(SortIdFor(db.CollationName)),
            "STATUS" => SqlValue.FromNVarchar("ONLINE"),
            // The database's access mode, moved by ALTER DATABASE … SET
            // { READ_ONLY | READ_WRITE }. SMO's database-properties preamble
            // reads it as [IsUpdateable].
            "UPDATEABILITY" => SqlValue.FromNVarchar(db.IsReadOnly ? "READ_ONLY" : "READ_WRITE"),
            "USERACCESS" => SqlValue.FromNVarchar(db.UserAccess switch
            {
                1 => "SINGLE_USER",
                2 => "RESTRICTED_USER",
                _ => "MULTI_USER",
            }),
            // The database version SQL Server 2025 stamps.
            "VERSION" => SqlValue.FromInt32(998),
            _ => SqlValue.Null(SqlType.SqlVariant),
        };
    }

    // Derive the SQL sort-order id from the collation name; real SQL Server
    // reports 0 for collations with no SQL_* sort order.
    private static byte SortIdFor(string collationName)
        => Collation.SqlServerSortOrders.TryGetValue(collationName, out var so) ? checked((byte)so.OrderNumber) : (byte)0;

    internal override string DebugDisplay() => $"DATABASEPROPERTYEX({this.dbNameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.dbNameArg).Child(this.propertyArg);
}
