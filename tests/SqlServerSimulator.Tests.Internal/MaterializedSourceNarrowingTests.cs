using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the WHERE narrowing of a <b>materialized</b>
/// derived source (<c>MaterializedFilter(alias,conjuncts,kept)</c>): a
/// non-leftmost body nothing can seek into, materialized once, is cut by its own
/// sargable WHERE conjuncts before the join reads it — an equality with an
/// enclosing query's column included — so a correlated body joining it drives
/// from the few rows that match (<c>Reorder(1,0)</c>) and seeks its base-table
/// partner per row, instead of pairing the partner's whole table with the whole
/// body on every outer row. The result-level counterpart is <c>Tests</c>'
/// <c>JoinPredicatePushdownTests</c>.
/// </summary>
[TestClass]
public sealed class MaterializedSourceNarrowingTests
{
    // 40 orders over 20 customers, two lines each, the lines indexed on the
    // order they belong to so a narrowed union of orders can seek them.
    private static SimulatedDbConnection Open()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            create table cust (cust_id int not null primary key);
            create table ord (ord_id int not null primary key, cust_id int not null, code varchar(10) not null);
            create table line (line_id int not null primary key, ord_id int not null, qty int not null);
            create index ix_line_ord on line (ord_id);
            declare @i int = 1;
            while @i <= 40 begin
                if @i <= 20 insert cust values (@i);
                insert ord values (@i, (@i + 1) / 2, cast(@i as varchar(10)));
                insert line values (@i * 2, @i, @i), (@i * 2 + 1, @i, 1000);
                set @i += 1;
            end
            """;
        _ = command.ExecuteNonQuery();
        return connection;
    }

    private const string OrdersUnion = "(select ord_id, cust_id, code from ord union select ord_id, cust_id, code from ord)";

    // Runs `query`, capturing both traces and the first column of every row.
    private static (List<string> Seeks, List<string> Joins, List<object?> Rows) Run(SimulatedDbConnection connection, string query)
    {
        IndexSeekDiagnostics.Sink = [];
        JoinDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            var rows = new List<object?>();
            while (reader.Read())
                rows.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
            return (IndexSeekDiagnostics.Sink, JoinDiagnostics.Sink, rows);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
            JoinDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void LiteralEquality_NarrowsTheUnionAndDrivesFromIt()
    {
        using var connection = Open();
        var (seeks, joins, rows) = Run(connection, $"""
            select sum(l.qty) from line l join {OrdersUnion} o on l.ord_id = o.ord_id where o.cust_id = 2
            """);
        Contains("MaterializedFilter(o,1,2)", seeks);
        Contains("Reorder(1,0)", joins);
        Contains("Inner:NestedLoopIndexSeek(keys=1)", joins);
        AreEqual(2007, rows[0]);
    }

    [TestMethod]
    public void ApplyBody_NarrowsTheUnionByTheOuterRow()
    {
        using var connection = Open();
        var (seeks, joins, rows) = Run(connection, $"""
            select sum(x.qty) from cust c cross apply (
                select l.qty from line l join {OrdersUnion} o on l.ord_id = o.ord_id
                where o.cust_id = c.cust_id) x
            """);
        // Each customer's execution keeps its own two orders and seeks their lines.
        HasCount(20, seeks.FindAll(static s => s == "MaterializedFilter(o,1,2)"));
        HasCount(20, joins.FindAll(static s => s == "Reorder(1,0)"));
        IsEmpty(joins.FindAll(static s => s.StartsWith("Inner:HashMatch", StringComparison.Ordinal)));
        AreEqual((40 * 41 / 2) + (40 * 1000), rows[0]);
    }

    [TestMethod]
    public void ExistsBody_NarrowsTheUnionByTheOuterRow()
    {
        using var connection = Open();
        var (seeks, _, rows) = Run(connection, $"""
            select count(*) from cust c where exists (
                select 1 from line l join {OrdersUnion} o on l.ord_id = o.ord_id
                where o.cust_id = c.cust_id and l.qty between 5 and 8)
            """);
        Contains("MaterializedFilter(o,1,2)", seeks);
        AreEqual(2, rows[0]);
    }

    [TestMethod]
    public void SiblingValueSide_Declines()
    {
        // `o.cust_id = l.qty` reads a sibling of the same FROM, which isn't
        // readable before the join runs.
        using var connection = Open();
        var (seeks, _, rows) = Run(connection, $"""
            select count(*) from line l join {OrdersUnion} o on l.ord_id = o.ord_id where o.cust_id = l.qty
            """);
        IsEmpty(seeks.FindAll(static s => s.StartsWith("MaterializedFilter(", StringComparison.Ordinal)));
        AreEqual(1, rows[0]);
    }

    [TestMethod]
    public void LeftmostDerivedTable_IsNotMaterialized_SoNotNarrowed()
    {
        // The leftmost slot streams its plan once rather than materializing,
        // so the narrowing has no rows in hand to cut.
        using var connection = Open();
        var (seeks, _, rows) = Run(connection, $"""
            select sum(l.qty) from {OrdersUnion} o join line l on l.ord_id = o.ord_id where o.cust_id = 2
            """);
        IsEmpty(seeks.FindAll(static s => s.StartsWith("MaterializedFilter(", StringComparison.Ordinal)));
        AreEqual(2007, rows[0]);
    }

    [TestMethod]
    public void RaisingConjunct_KeepsTheRowForTheResidual()
    {
        // `o.code = 5` converts every code to int, and one code isn't numeric.
        // The narrowing keeps that row rather than raising, so whether the
        // statement raises stays the residual WHERE's decision over the tuples
        // the join produces — and no line joins the bad order. (Real raises Msg
        // 245 here, probed 2026-10-10 against SQL Server 2025: its plan
        // evaluates the conjunct ahead of the join, as the simulator's written
        // order never has.)
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "insert ord values (99, 1, 'abc')";
            _ = command.ExecuteNonQuery();
        }

        var (seeks, _, rows) = Run(connection, $"""
            select sum(l.qty) from line l join {OrdersUnion} o on l.ord_id = o.ord_id where o.code = 5
            """);
        Contains("MaterializedFilter(o,1,2)", seeks);
        AreEqual(1005, rows[0]);
    }
}
