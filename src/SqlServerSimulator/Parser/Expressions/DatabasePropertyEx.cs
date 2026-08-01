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
/// Closed accept-list of recognized property names mirrors what BACPAC
/// tooling + EF Core query: <c>Collation</c>, <c>Status</c>, <c>UserAccess</c>,
/// <c>IsAutoClose</c>, <c>IsAutoShrink</c>, <c>Recovery</c>,
/// <c>SnapshotIsolationState</c>, <c>IsReadCommittedSnapshotOn</c>,
/// <c>IsRecursiveTriggersEnabled</c>, <c>ComparisonStyle</c>, <c>LCID</c>,
/// <c>SQLSortOrder</c>, <c>Version</c>.
/// Properties not on the list return NULL (forward-compatible with future
/// tooling that may query newer property names). <see cref="Produce"/>
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

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SqlVariant;

    private static SqlValue Produce(string property, Database db)
    {
        // Longer than any recognized property name; also bounds the stackalloc
        // against an adversarially long argument.
        if (property.Length > 32)
            return SqlValue.Null(SqlType.SqlVariant);
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "COLLATION" => SqlValue.FromNVarchar(db.CollationName),
            "COMPARISONSTYLE" => SqlValue.FromInt32(196609),
            "ISAUTOCLOSE" => SqlValue.FromInt32(0),
            "ISAUTOSHRINK" => SqlValue.FromInt32(0),
            "ISREADCOMMITTEDSNAPSHOTON" => SqlValue.FromInt32(db.ReadCommittedSnapshot ? 1 : 0),
            "ISRECURSIVETRIGGERSENABLED" => SqlValue.FromInt32(db.RecursiveTriggers ? 1 : 0),
            // DBCC CHECKDB isn't modeled, so the last-good-checkdb time is a
            // NULL sql_variant. SMO's CAST(ISNULL(..., 0) AS datetime) resolves
            // to 1900-01-01: ISNULL over the NULL variant fixes to sql_variant
            // wrapping the int 0, which CASTs to the datetime epoch (matching
            // real, probe-confirmed 2026-07-19).
            "LASTGOODCHECKDBTIME" => SqlValue.Null(SqlType.DateTime),
            "LCID" => SqlValue.FromInt32(1033),
            "RECOVERY" => SqlValue.FromNVarchar("FULL"),
            "SNAPSHOTISOLATIONSTATE" => SqlValue.FromInt32(db.AllowSnapshotIsolation ? 1 : 0),
            "SQLSORTORDER" => SqlValue.FromByte(SortIdFor(db.CollationName)),
            "STATUS" => SqlValue.FromNVarchar("ONLINE"),
            // Updateability is always READ_WRITE (the simulator models no
            // read-only databases at the DATABASEPROPERTYEX surface). SMO's
            // database-properties preamble reads it as [IsUpdateable].
            "UPDATEABILITY" => SqlValue.FromNVarchar("READ_WRITE"),
            "USERACCESS" => SqlValue.FromNVarchar("MULTI_USER"),
            "VERSION" => SqlValue.FromInt32(0),
            _ => SqlValue.Null(SqlType.SqlVariant),
        };
    }

    // Derive the SQL sort-order id from the collation name; real SQL Server
    // reports 0 for collations with no SQL_* sort order.
    private static byte SortIdFor(string collationName)
        => Collation.SqlServerSortOrders.TryGetValue(collationName, out var so) ? checked((byte)so.OrderNumber) : (byte)0;

    internal override string DebugDisplay() => $"DATABASEPROPERTYEX({this.dbNameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
