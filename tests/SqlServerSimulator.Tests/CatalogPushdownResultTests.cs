using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-API result-parity coverage for the catalog-view predicate pushdown:
/// a <c>WHERE col.object_id = &lt;value&gt;</c> filter over <c>sys.columns</c> /
/// <c>sys.all_columns</c> (and peers) is pushed into the row generator so only
/// the matching object's rows materialize. The pushdown is a transparent
/// optimization — these confirm the visible results are identical to the
/// full-scan-then-filter behavior across every comparand form (literal /
/// variable / OBJECT_ID / parameter), predicate composition (ANDed,
/// OR-combined), placement (catalog view not leftmost), NULL / unknown
/// comparand, and a 3-part cross-database reference. The diagnostics-level "did
/// the pushdown fire" assertions live in the internal suite
/// (<c>CatalogPushdownTests</c>).
/// </summary>
[TestClass]
public sealed class CatalogPushdownResultTests
{
    private const string ThreeTables = """
        create table t1 (a int not null primary key, b nvarchar(20) null);
        create table t2 (c int not null primary key, d date null, e money null);
        create table t3 (f bigint not null primary key);
        """;

    private static List<string> ColumnNames(Simulation sim, string query)
    {
        using var reader = sim.ExecuteReader(query);
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [TestMethod]
    public void ObjectIdFunctionComparand_ReturnsTargetColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        CollectionAssert.AreEqual(
            new[] { "c", "d", "e" },
            ColumnNames(sim, "select name from sys.columns c where c.object_id = object_id('dbo.t2') order by column_id"));
    }

    [TestMethod]
    public void AllColumnsView_ReturnsTargetColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        CollectionAssert.AreEqual(
            new[] { "c", "d", "e" },
            ColumnNames(sim, "select name from sys.all_columns where object_id = object_id('dbo.t2') order by column_id"));
    }

    [TestMethod]
    public void LiteralComparand_ReturnsTargetColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        var id = sim.ExecuteScalar<int>("select object_id('dbo.t2')");
        CollectionAssert.AreEqual(
            new[] { "c", "d", "e" },
            ColumnNames(sim, $"select name from sys.columns where object_id = {id} order by column_id"));
    }

    [TestMethod]
    public void VariableComparand_ReturnsTargetColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        CollectionAssert.AreEqual(
            new[] { "f" },
            ColumnNames(sim, """
                declare @id int = object_id('dbo.t3');
                select name from sys.columns where object_id = @id order by column_id
                """));
    }

    [TestMethod]
    public void ParameterComparand_ReturnsTargetColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        var id = sim.ExecuteScalar<int>("select object_id('dbo.t2')");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand(
            "select name from sys.columns where object_id = @id order by column_id",
            ("@id", id));
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "c", "d", "e" }, names);
    }

    [TestMethod]
    public void AndedWithResidualPredicate_AppliesBoth()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        // Pushdown narrows to t2; residual WHERE drops the NOT NULL key column.
        CollectionAssert.AreEqual(
            new[] { "d", "e" },
            ColumnNames(sim, "select name from sys.columns c where c.object_id = object_id('dbo.t2') and c.is_nullable = 1 order by column_id"));
    }

    [TestMethod]
    public void OrCombinedPredicate_StillCorrect()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        // OR isn't a top-level AND-conjunct, so no pushdown fires — the union of
        // t2 (c, d, e) and t3 (f) columns must still come back.
        CollectionAssert.AreEqual(
            new[] { "c", "d", "e", "f" },
            ColumnNames(sim, "select c.name from sys.columns c where c.object_id = object_id('dbo.t2') or c.object_id = object_id('dbo.t3') order by object_id, column_id"));
    }

    [TestMethod]
    public void CatalogViewNotLeftmost_StillCorrect()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        _ = sim.ExecuteNonQuery("insert t2 values (1, '2020-01-01', 5)");
        // sys.columns is the join's right side; the leftmost source is the base
        // table, so no pushdown — but the projected column names stay correct
        // (one left row cross-joined to t2's three matching catalog rows).
        CollectionAssert.AreEqual(
            new[] { "c", "d", "e" },
            ColumnNames(sim, """
                select col.name from t2 cross join sys.columns col
                where col.object_id = object_id('dbo.t2') order by col.column_id
                """));
    }

    [TestMethod]
    public void UnknownObjectComparand_ReturnsEmpty()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        IsEmpty(ColumnNames(sim, "select name from sys.columns where object_id = object_id('dbo.missing')"));
    }

    [TestMethod]
    public void NullComparand_ReturnsEmpty()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        IsEmpty(ColumnNames(sim, "select name from sys.columns where object_id = null"));
    }

    [TestMethod]
    public void ThreePartCurrentDatabaseReference_ReturnsTargetColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        // 3-part `simulated.sys.columns` resolves the generator's target database
        // through the pushdown seam — same result as the 2-part reference.
        CollectionAssert.AreEqual(
            new[] { "c", "d", "e" },
            ColumnNames(sim, "select name from simulated.sys.columns c where c.object_id = object_id('dbo.t2') order by column_id"));
    }

    [TestMethod]
    public void BigIntComparandInRange_ReturnsTargetColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        var id = sim.ExecuteScalar<int>("select object_id('dbo.t2')");
        // object_id is int; a bigint comparand still keys the seek (widened
        // losslessly), and the result matches the int form.
        CollectionAssert.AreEqual(
            new[] { "c", "d", "e" },
            ColumnNames(sim, $"select name from sys.columns where object_id = cast({id} as bigint) order by column_id"));
    }

    [TestMethod]
    public void OutOfIntRangeComparand_ReturnsEmptyWithoutError()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        // No int object_id can equal a value past int range — empty, and the
        // pushdown must not raise a coercion overflow where the residual
        // filter would just return nothing.
        IsEmpty(ColumnNames(sim, "select name from sys.columns where object_id = cast(5000000000 as bigint)"));
    }

    [TestMethod]
    public void NonIntegerComparand_StillCorrect()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        var id = sim.ExecuteScalar<int>("select object_id('dbo.t2')");
        // A decimal comparand isn't lossily narrowed (left to the residual
        // filter), but the whole-value equality still matches object_id = 100.
        CollectionAssert.AreEqual(
            new[] { "c", "d", "e" },
            ColumnNames(sim, $"select name from sys.columns where object_id = cast({id} as decimal(10,0)) order by column_id"));
    }

    [TestMethod]
    public void IndexesByObjectId_ReturnsTargetIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeTables);
        // t1's clustered primary-key index only.
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from sys.indexes where object_id = object_id('dbo.t1') and index_id > 0"));
    }
}
