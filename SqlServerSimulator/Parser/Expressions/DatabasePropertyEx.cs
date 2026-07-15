using System.Collections.Frozen;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATABASEPROPERTYEX(database_name, property_name)</c>: returns
/// the named property of a database. Real SQL Server projects this as
/// <c>sql_variant</c> carrying a per-property inner base type; the
/// simulator doesn't model sql_variant, so it surfaces the bare true type
/// instead — numeric properties as <see cref="SqlType.Int32"/> /
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
/// tooling that may query newer property names).
/// </remarks>
internal sealed class DatabasePropertyEx : Expression
{
    private static readonly FrozenDictionary<string, (SqlType Type, Func<Database, SqlValue> Produce)> Properties = new Dictionary<string, (SqlType Type, Func<Database, SqlValue> Produce)>
    {
        ["Status"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("ONLINE")),
        ["Version"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["Recovery"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("FULL")),
        // Updateability is always READ_WRITE (the simulator models no
        // read-only databases at the DATABASEPROPERTYEX surface). SMO's
        // database-properties preamble reads it as [IsUpdateable].
        ["Updateability"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("READ_WRITE")),
        // DBCC CHECKDB isn't modeled, so the last-good-checkdb time is NULL —
        // typed datetime (not the unknown-property NVarchar) so SMO's
        // CAST(ISNULL(..., 0) AS datetime) resolves to 1900-01-01 rather than
        // failing to convert the string '0' to datetime.
        ["LastGoodCheckDbTime"] = (SqlType.DateTime, _ => SqlValue.Null(SqlType.DateTime)),
        ["Collation"] = (SqlType.NVarchar, db => SqlValue.FromNVarchar(db.CollationName)),
        ["UserAccess"] = (SqlType.NVarchar, _ => SqlValue.FromNVarchar("MULTI_USER")),
        ["IsAutoClose"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["IsAutoShrink"] = (SqlType.Int32, _ => SqlValue.FromInt32(0)),
        ["SnapshotIsolationState"] = (SqlType.Int32, db => SqlValue.FromInt32(db.AllowSnapshotIsolation ? 1 : 0)),
        ["IsReadCommittedSnapshotOn"] = (SqlType.Int32, db => SqlValue.FromInt32(db.ReadCommittedSnapshot ? 1 : 0)),
        ["ComparisonStyle"] = (SqlType.Int32, _ => SqlValue.FromInt32(196609)),
        ["LCID"] = (SqlType.Int32, _ => SqlValue.FromInt32(1033)),
        ["SQLSortOrder"] = (SqlType.TinyInt, db => SqlValue.FromByte(SortIdFor(db.CollationName))),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

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

        if (!Properties.TryGetValue(property, out var def))
            return SqlValue.Null(SqlType.NVarchar);

        var value = def.Produce(db);
        // A non-constant property name couldn't resolve a true type at parse
        // time (GetSqlType fell back to NVarchar); coerce so runtime agrees.
        return this.propertyArg is Value ? value : value.CoerceTo(SqlType.NVarchar);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => this.propertyArg is Value { Constant: { IsNull: false } constant }
            && Properties.TryGetValue(constant.CoerceTo(SqlType.NVarchar).AsString, out var def)
            ? def.Type
            : SqlType.NVarchar;

    // Derive the SQL sort-order id from the collation name; real SQL Server
    // reports 0 for collations with no SQL_* sort order.
    private static byte SortIdFor(string collationName)
        => Collation.SqlServerSortOrders.TryGetValue(collationName, out var so) ? checked((byte)so.OrderNumber) : (byte)0;

    internal override string DebugDisplay() => $"DATABASEPROPERTYEX({this.dbNameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
