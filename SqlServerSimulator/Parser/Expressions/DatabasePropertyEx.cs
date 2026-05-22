using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATABASEPROPERTYEX(database_name, property_name)</c>: returns
/// the named property of a database as <c>sql_variant</c> (the simulator
/// returns the matching scalar type per-property). NULL database / NULL
/// property → NULL result; unknown database → NULL; unknown property →
/// NULL (matches real SQL Server, probe-confirmed 2026-05-16).
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

        // Property-name lookup is case-insensitive (SQL Server convention).
        // Use the Span overload (SSS003) — the upper-cased form drives the
        // switch without allocating.
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            6 when upper.SequenceEqual("STATUS") => SqlValue.FromNVarchar("ONLINE"),
            7 when upper.SequenceEqual("VERSION") => SqlValue.FromInt32(0),
            8 when upper.SequenceEqual("RECOVERY") => SqlValue.FromNVarchar("FULL"),
            9 when upper.SequenceEqual("COLLATION") => SqlValue.FromNVarchar(db.CollationName),
            10 when upper.SequenceEqual("USERACCESS") => SqlValue.FromNVarchar("MULTI_USER"),
            11 when upper.SequenceEqual("ISAUTOCLOSE") => SqlValue.FromInt32(0),
            12 when upper.SequenceEqual("ISAUTOSHRINK") => SqlValue.FromInt32(0),
            22 when upper.SequenceEqual("SNAPSHOTISOLATIONSTATE") => SqlValue.FromInt32(db.AllowSnapshotIsolation ? 1 : 0),
            25 when upper.SequenceEqual("ISREADCOMMITTEDSNAPSHOTON") => SqlValue.FromInt32(db.ReadCommittedSnapshot ? 1 : 0),
            _ => SqlValue.Null(SqlType.NVarchar),
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"DATABASEPROPERTYEX({this.dbNameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
