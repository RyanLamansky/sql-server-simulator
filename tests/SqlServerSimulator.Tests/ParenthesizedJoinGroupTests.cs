using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Parenthesized join groups in FROM — <c>A LEFT JOIN (B JOIN C ON …) ON …</c> —
/// where a parenthesized join expression is a join operand. The group is a
/// grammar grouping (its members resolve by their own qualifiers outside the
/// parens), not a derived-table scope: the interior join binds first, then the
/// outer ON joins the left spine against the whole group, and an outer-join miss
/// NULL-fills every group member. Semantics probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ParenthesizedJoinGroupTests
{
    private static DbConnection SeededABCD()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table a (id int, av int);
            create table b (id int, bv int);
            create table c (id int, cv int);
            create table d (id int, dv int);
            insert a values (1, 10), (2, 20), (3, 30);
            insert b values (1, 100), (2, 200);
            insert c values (1, 1000), (2, 2000);
            insert d values (1, 9000);
            """).ExecuteNonQuery();
        return connection;
    }

    private static List<int?[]> ReadRows(DbCommand command, int width)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<int?[]>();
        while (reader.Read())
        {
            var row = new int?[width];
            for (var i = 0; i < width; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetInt32(i);
            rows.Add(row);
        }
        return rows;
    }

    private static void AssertRows(List<int?[]> actual, params int?[][] expected)
    {
        HasCount(expected.Length, actual, "row count");
        for (var r = 0; r < expected.Length; r++)
            CollectionAssert.AreEqual(expected[r], actual[r], $"row {r}");
    }

    [TestMethod]
    public void LeftJoinGroup_NullExtendsBothMembers()
    {
        // A LEFT JOIN (B JOIN C ON B.id=C.id) ON A.id=B.id — a.id=3 misses the
        // group, so BOTH B and C columns NULL-fill (probe case 1).
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.id, c.id, b.bv, c.cv from a left outer join (b join c on b.id=c.id) on a.id=b.id order by a.id"), 5);
        AssertRows(rows,
            [1, 1, 1, 100, 1000],
            [2, 2, 2, 200, 2000],
            [3, null, null, null, null]);
    }

    [TestMethod]
    public void LeftJoinGroup_OuterOnReferencesGroupMember()
    {
        // Outer ON binds against a group member (A.id=C.id) and the SELECT reads
        // group columns qualified by their interior aliases (probe case 9).
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.bv, c.cv from a left join (b join c on b.id=c.id) on a.id=c.id order by a.id"), 3);
        AssertRows(rows,
            [1, 100, 1000],
            [2, 200, 2000],
            [3, null, null]);
    }

    [TestMethod]
    public void LeftJoinGroup_NestedDepthTwo()
    {
        // A LEFT JOIN (B JOIN (C JOIN D ON C.id=D.id) ON B.id=C.id) ON A.id=B.id
        // (probe case 2): only a.id=1 threads all the way through.
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.id, c.id, d.id from a left join (b join (c join d on c.id=d.id) on b.id=c.id) on a.id=b.id order by a.id"), 4);
        AssertRows(rows,
            [1, 1, 1, 1],
            [2, null, null, null],
            [3, null, null, null]);
    }

    [TestMethod]
    public void GroupOnLeft_MatchesLeftDeepDefault()
    {
        // (A JOIN B ON A.id=B.id) LEFT JOIN C ON A.id=C.id (probe case 3): a
        // left-operand group is a no-op grouping over the already-left-deep spine.
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.id, c.id from (a join b on a.id=b.id) left join c on a.id=c.id order by a.id"), 3);
        AssertRows(rows,
            [1, 1, 1],
            [2, 2, 2]);
    }

    [TestMethod]
    public void CommaThenGroup_CrossFilteredByWhere()
    {
        // FROM A, (B JOIN C ON B.id=C.id) WHERE A.id=B.id (probe case 4).
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.id, c.id from a, (b join c on b.id=c.id) where a.id=b.id order by a.id"), 3);
        AssertRows(rows,
            [1, 1, 1],
            [2, 2, 2]);
    }

    [TestMethod]
    public void GroupContainingLeftJoin()
    {
        // A JOIN (B LEFT JOIN C ON B.id=C.id) ON A.id=B.id (probe case 5).
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.id, c.id from a join (b left join c on b.id=c.id) on a.id=b.id order by a.id"), 3);
        AssertRows(rows,
            [1, 1, 1],
            [2, 2, 2]);
    }

    [TestMethod]
    public void GroupContainingCrossJoin()
    {
        // A JOIN (B CROSS JOIN C) ON A.id=B.id (probe R5): interior Cartesian.
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.id, c.id from a join (b cross join c) on a.id=b.id order by a.id, c.id"), 3);
        AssertRows(rows,
            [1, 1, 1],
            [1, 1, 2],
            [2, 2, 1],
            [2, 2, 2]);
    }

    [TestMethod]
    public void RightJoinGroup()
    {
        // A RIGHT JOIN (B JOIN C ON B.id=C.id) ON A.id=B.id (probe R1): every
        // group row matches an A row, so no left-null-fill occurs.
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.id, c.id from a right join (b join c on b.id=c.id) on a.id=b.id order by a.id, b.id"), 3);
        AssertRows(rows,
            [1, 1, 1],
            [2, 2, 2]);
    }

    [TestMethod]
    public void FullJoinGroup_UnmatchedLeftNullFillsGroup()
    {
        // A FULL JOIN (B JOIN C ON B.id=C.id) ON A.id=B.id (probe R2): a.id=3 has
        // no group match, emitted with the group NULL-filled.
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select a.id, b.id, c.id from a full join (b join c on b.id=c.id) on a.id=b.id order by a.id, b.id"), 3);
        AssertRows(rows,
            [1, 1, 1],
            [2, 2, 2],
            [3, null, null]);
    }

    [TestMethod]
    public void DerivedTableAsGroupLeftmost()
    {
        // ((SELECT 1 v) x JOIN B ON x.v=B.id) LEFT JOIN C ON B.id=C.id (probe R3):
        // a derived table is a valid leftmost member of a group.
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand(
            "select x.v, b.id, c.id from ((select 1 v) x join b on x.v=b.id) left join c on b.id=c.id order by b.id"), 3);
        AssertRows(rows, [1, 1, 1]);
    }

    [TestMethod]
    public void AliasedGroup_WithAs_Rejected()
    {
        // A parenthesized join group cannot take an alias (probe: Msg 156 near AS).
        using var connection = SeededABCD();
        var ex = Throws<DbException>(() => connection.CreateCommand(
            "select * from a left join (b join c on b.id=c.id) as x on a.id=b.id").ExecuteScalar());
        AreEqual("156", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AliasedGroup_BareName_Rejected()
    {
        // A bare-name alias on a group is also rejected (probe: Msg 102 near 'x').
        using var connection = SeededABCD();
        var ex = Throws<DbException>(() => connection.CreateCommand(
            "select * from a left join (b join c on b.id=c.id) x on a.id=b.id").ExecuteScalar());
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void ParenthesizedSingleSource_Rejected()
    {
        // A parenthesized single source is not a join group — real rejects `(t)`
        // with Msg 102 (probe R4).
        using var connection = SeededABCD();
        var ex = Throws<DbException>(() => connection.CreateCommand("select * from (a)").ExecuteScalar());
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void SsmsTableDesignerQuery_ReturnsEmptyWithoutError()
    {
        // The exact query SSMS's Table Designer issues — a parenthesized join
        // group over the partition catalog views. Partitioning isn't modeled, so
        // the views are empty; the query must parse, run, and return zero rows.
        using var connection = SeededABCD();
        var rows = ReadRows(connection.CreateCommand("""
            select func.name, func.function_id, func.type, func.fanout, func.boundary_value_on_right,
                   para.parameter_id, tp.name as type_name,
                   convert(smallint, case when (tp.name = N'nchar' or tp.name = N'nvarchar') then para.max_length / 2 else para.max_length end) as max_length,
                   para.precision, para.scale, para.collation_name
            from sys.partition_functions func
            left outer join (sys.partition_parameters para join sys.types tp on tp.user_type_id = para.system_type_id)
            on para.function_id = func.function_id
            order by func.function_id, para.parameter_id
            """), 1);
        IsEmpty(rows);
    }

    [TestMethod]
    public void PartitionCatalogViews_EmptyWithExpectedColumns()
    {
        using var connection = SeededABCD();
        foreach (var view in new[] { "sys.partition_functions", "sys.partition_schemes", "sys.partition_parameters", "sys.partition_range_values" })
            AreEqual(0, connection.CreateCommand($"select count(*) from {view}").ExecuteScalar());

        // Column shapes are addressable (SELECT the probe-confirmed columns).
        AreEqual(0, connection.CreateCommand(
            "select count(*) from sys.partition_functions where name is null and function_id is null and fanout is null and boundary_value_on_right is null").ExecuteScalar());
        AreEqual(0, connection.CreateCommand(
            "select count(*) from sys.partition_parameters where parameter_id is null and system_type_id is null and max_length is null and collation_name is null").ExecuteScalar());
    }
}
