using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATABASEPROPERTYEX(database_name, property_name)</c>: returns
/// the named property of a database. Real SQL Server projects this as
/// <c>sql_variant</c> carrying a per-property inner base type; the
/// simulator doesn't model sql_variant here, so it surfaces the bare true
/// type instead — numeric properties as <see cref="SqlType.Int32"/> /
/// <see cref="SqlType.TinyInt"/>, string properties as
/// <see cref="SqlType.NVarchar"/>. When the property-name argument is a
/// compile-time constant the true type flows to the projection schema;
/// when it isn't, the type falls back to <see cref="SqlType.NVarchar"/>
/// and the runtime value is coerced to match (the static/runtime parity
/// contract; only the property name drives the type — the database-name
/// argument may be non-constant). NULL database / NULL property → NULL
/// result; unknown database → NULL; unknown property → NULL (matches real
/// SQL Server, probe-confirmed 2026-05-16).
/// </summary>
/// <remarks>
/// Closed accept-list of recognized property names mirrors what BACPAC
/// tooling + EF Core query: <c>Collation</c>, <c>Status</c>, <c>UserAccess</c>,
/// <c>IsAutoClose</c>, <c>IsAutoShrink</c>, <c>Recovery</c>,
/// <c>SnapshotIsolationState</c>, <c>IsReadCommittedSnapshotOn</c>,
/// <c>ComparisonStyle</c>, <c>LCID</c>, <c>SQLSortOrder</c>, <c>Version</c>.
/// Properties not on the list return NULL (forward-compatible with future
/// tooling that may query newer property names). <see cref="Produce"/> and
/// <see cref="TypeOf"/> carry mirrored arm lists — every property appears in
/// both, with <see cref="TypeOf"/> declaring the type the matching
/// <see cref="Produce"/> arm's non-NULL value carries.
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
            return SqlValue.Null(SqlType.NVarchar);

        var dbName = dbNameValue.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)).AsString;
        var property = propertyValue.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)).AsString;

        // The simulator's database dictionary is keyed by name; only the
        // currently-attached databases resolve.
        if (!runtime.Batch.Connection.Simulation.Databases.TryGetValue(dbName, out var db))
            return SqlValue.Null(SqlType.NVarchar);

        var value = Produce(property, db);
        // A non-constant property name couldn't resolve a true type at parse
        // time (GetSqlType fell back to NVarchar); coerce so runtime agrees.
        return this.propertyArg is Value ? value : value.CoerceTo(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => this.propertyArg is Value { Constant: { IsNull: false } constant }
            ? TypeOf(constant.CoerceTo(SqlType.NVarchar).AsString)
            : SqlType.NVarchar;

    private static SqlValue Produce(string property, Database db)
    {
        // Longer than any recognized property name; also bounds the stackalloc
        // against an adversarially long argument.
        if (property.Length > 32)
            return SqlValue.Null(SqlType.NVarchar);
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "COLLATION" => SqlValue.FromNVarchar(db.CollationName),
            "COMPARISONSTYLE" => SqlValue.FromInt32(196609),
            "ISAUTOCLOSE" => SqlValue.FromInt32(0),
            "ISAUTOSHRINK" => SqlValue.FromInt32(0),
            "ISREADCOMMITTEDSNAPSHOTON" => SqlValue.FromInt32(db.ReadCommittedSnapshot ? 1 : 0),
            // DBCC CHECKDB isn't modeled, so the last-good-checkdb time is
            // NULL — typed datetime (not the unknown-property NVarchar) so
            // SMO's CAST(ISNULL(..., 0) AS datetime) resolves to 1900-01-01
            // rather than failing to convert the string '0' to datetime.
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
            _ => SqlValue.Null(SqlType.NVarchar),
        };
    }

    private static SqlType TypeOf(string property)
    {
        // Same bound as Produce so a long name types as the unknown-property
        // NVarchar it evaluates to.
        if (property.Length > 32)
            return SqlType.NVarchar;
        Span<char> upper = stackalloc char[property.Length];
        _ = property.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "COLLATION" => SqlType.NVarchar,
            "COMPARISONSTYLE" => SqlType.Int32,
            "ISAUTOCLOSE" => SqlType.Int32,
            "ISAUTOSHRINK" => SqlType.Int32,
            "ISREADCOMMITTEDSNAPSHOTON" => SqlType.Int32,
            "LASTGOODCHECKDBTIME" => SqlType.DateTime,
            "LCID" => SqlType.Int32,
            "RECOVERY" => SqlType.NVarchar,
            "SNAPSHOTISOLATIONSTATE" => SqlType.Int32,
            "SQLSORTORDER" => SqlType.TinyInt,
            "STATUS" => SqlType.NVarchar,
            "UPDATEABILITY" => SqlType.NVarchar,
            "USERACCESS" => SqlType.NVarchar,
            "VERSION" => SqlType.Int32,
            _ => SqlType.NVarchar,
        };
    }

    // Derive the SQL sort-order id from the collation name; real SQL Server
    // reports 0 for collations with no SQL_* sort order.
    private static byte SortIdFor(string collationName)
        => Collation.SqlServerSortOrders.TryGetValue(collationName, out var so) ? checked((byte)so.OrderNumber) : (byte)0;

    internal override string DebugDisplay() => $"DATABASEPROPERTYEX({this.dbNameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
