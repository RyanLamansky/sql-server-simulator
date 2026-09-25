using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    // sp_server_info: attribute_id int, attribute_name varchar(60),
    // attribute_value varchar(255), the two strings at the catalog collation
    // (probed 2026-09-25).
    private static readonly SqlType[] SpServerInfoSchema =
    [
        SqlType.Int32,
        VarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit),
        VarcharSqlType.Get(255, Collation.Catalog, Coercibility.Implicit),
    ];

    private static readonly string[] SpServerInfoColumnNames = ["attribute_id", "attribute_name", "attribute_value"];

    /// <summary>
    /// <c>sys.spt_server_info</c>'s rows as SQL Server 2025 ships them, in
    /// attribute order, less the two <c>sp_server_info</c> computes
    /// (<c>IDENTIFIER_CASE</c>, 16, and <c>COLLATION_SEQ</c>, 18) — probed
    /// 2026-09-25.
    /// </summary>
    private static readonly (int Id, string Name, string Value)[] SpServerInfoStockRows =
    [
        (1, "DBMS_NAME", "Microsoft SQL Server"),
        (2, "DBMS_VER", $"Microsoft SQL Server 2025 - {ReferenceBuild.ProductVersion}"),
        (10, "OWNER_TERM", "owner"),
        (11, "TABLE_TERM", "table"),
        (12, "MAX_OWNER_NAME_LENGTH", "128"),
        (13, "TABLE_LENGTH", "128"),
        (14, "MAX_QUAL_LENGTH", "128"),
        (15, "COLUMN_LENGTH", "128"),
        (17, "TX_ISOLATION", "2"),
        (19, "SAVEPOINT_SUPPORT", "Y"),
        (20, "MULTI_RESULT_SETS", "Y"),
        (22, "ACCESSIBLE_TABLES", "Y"),
        (100, "USERID_LENGTH", "128"),
        (101, "QUALIFIER_TERM", "database"),
        (102, "NAMED_TRANSACTIONS", "Y"),
        (103, "SPROC_AS_LANGUAGE", "Y"),
        (104, "ACCESSIBLE_SPROC", "Y"),
        (105, "MAX_INDEX_COLS", "16"),
        (106, "RENAME_TABLE", "Y"),
        (107, "RENAME_COLUMN", "Y"),
        (108, "DROP_COLUMN", "Y"),
        (109, "INCREASE_COLUMN_LENGTH", "Y"),
        (110, "DDL_IN_TRANSACTION", "Y"),
        (111, "DESCENDING_INDEXES", "Y"),
        (112, "SP_RENAME", "Y"),
        (113, "REMOTE_SPROC", "Y"),
        (500, "SYS_SPROC_VERSION", $"{ReferenceBuild.Version.Major}.{ReferenceBuild.Version.Minor:00}.{ReferenceBuild.Version.Build}"),
    ];

    /// <summary>
    /// Handles <c>EXEC sp_server_info [@attribute_id]</c> — ODBC's
    /// <c>SQLGetInfo</c> backing proc: the stock attribute rows plus
    /// <c>IDENTIFIER_CASE</c> (<c>MIXED</c> unless the server collation tells
    /// <c>'a'</c> from <c>'A'</c>) and <c>COLLATION_SEQ</c> (the server
    /// collation's character set and sort order, or its name when it has no
    /// SQL sort order), in attribute order, narrowed to one attribute when
    /// <c>@attribute_id</c> is given.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpServerInfo(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        if (arguments.Count > 1 || (arguments is [{ Name: { } parameterName }] && !BuiltInToken.Equals(parameterName, "attribute_id")))
            throw SimulatedSqlException.InvalidProcedureParameters("sp_server_info");
        int? attributeId = arguments is [{ IsDefault: false, Value.IsNull: false } arg]
            ? ScalarArguments.CoerceProcedureParameter(arg.Value, SqlType.Int32)
            : null;

        var serverCollationName = batch.Connection.Simulation.ServerCollationName;
        var sortOrder = ServerProperty.SortIdFor(serverCollationName);
        var collationSequence = sortOrder == 0
            ? $"charset=iso_1 collation={serverCollationName}"
            : $"charset=iso_1 sort_order={(sortOrder == 52 ? "nocase_iso" : "BIN")} charset_num=1 sort_order_num={sortOrder}";
        var identifierCase = Collation.Get(serverCollationName).Equals("a", "A") ? "MIXED" : "SENSITIVE";

        var all = new List<(int Id, string Name, string Value)>(SpServerInfoStockRows)
        {
            (16, "IDENTIFIER_CASE", identifierCase),
            (18, "COLLATION_SEQ", collationSequence),
        };
        all.Sort((a, b) => a.Id.CompareTo(b.Id));

        var rows = new List<SqlValue[]>();
        foreach (var (id, name, value) in all)
        {
            if (attributeId is { } wanted && wanted != id)
                continue;
            rows.Add([
                SqlValue.FromInt32(id),
                SqlValue.FromString(SpServerInfoSchema[1], name),
                SqlValue.FromString(SpServerInfoSchema[2], value),
            ]);
        }

        yield return new SimulatedSqlResultSet(SpServerInfoSchema, SpServerInfoColumnNames, rows);
    }

    // sp_databases: DATABASE_NAME nvarchar(128), DATABASE_SIZE int (KB),
    // REMARKS varchar(254) — probed 2026-09-25.
    private static readonly SqlType[] SpDatabasesSchema =
    [
        NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.CoercibleDefault),
        SqlType.Int32,
        VarcharSqlType.Get(254, Collation.Baseline, Coercibility.CoercibleDefault),
    ];

    private static readonly string[] SpDatabasesColumnNames = ["DATABASE_NAME", "DATABASE_SIZE", "REMARKS"];

    /// <summary>
    /// Handles <c>EXEC sp_databases</c> — ODBC's catalog list: one row per
    /// database the caller can access, sized in kilobytes as the sum of its
    /// files' pages (the same sizes <c>sys.master_files</c> reports), ordered
    /// by name.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDatabases(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count > 0)
            throw SimulatedSqlException.InvalidProcedureParameters("sp_databases");

        var simulation = batch.Connection.Simulation;
        var rows = new List<SqlValue[]>();
        foreach (var (database, _) in DbId.DatabasesWithIds(simulation))
        {
            if (!HasDbAccess.IsAccessible(batch.Connection, database))
                continue;
            var pages = (long)BuiltInResources.ComputeDataFileSizePages(database) + BuiltInResources.LogFileSizePages;
            rows.Add([
                SqlValue.FromString(SpDatabasesSchema[0], database.Name),
                SqlValue.FromInt32(checked((int)(pages * 8))),
                SqlValue.Null(SpDatabasesSchema[2]),
            ]);
        }

        var collation = Collation.Get(simulation.ServerCollationName);
        rows.Sort((a, b) => collation.Compare(a[0].AsString, b[0].AsString));
        yield return new SimulatedSqlResultSet(SpDatabasesSchema, SpDatabasesColumnNames, rows);
    }
}
