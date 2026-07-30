using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A derived table's column-alias list — <c>(SELECT …) s(a, b)</c> — renames
/// every output column, overriding whatever the inner projection called them.
/// Without one, every column must already have a name. All behaviors
/// probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DerivedTableColumnAliasTests
{
    private static string[] ColumnNames(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var names = new string[reader.FieldCount];
        for (var i = 0; i < names.Length; i++)
            names[i] = reader.GetName(i);
        return names;
    }

    [TestMethod]
    public void AliasList_NamesUnnamedColumns()
        => CollectionAssert.AreEqual(new[] { "a", "b" }, ColumnNames(new Simulation(), "select * from (select 1, 2) s(a, b)"));

    /// <summary>The list wins over names the inner projection already gave.</summary>
    [TestMethod]
    public void AliasList_OverridesInnerAliases()
        => CollectionAssert.AreEqual(new[] { "a", "b" }, ColumnNames(new Simulation(), "select * from (select 1 x, 2 y) s(a, b)"));

    [TestMethod]
    public void AliasList_RenamesRealColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b int); insert t values (7, 8)");
        CollectionAssert.AreEqual(new[] { "p", "q" }, ColumnNames(sim, "select * from (select a, b from t) s(p, q)"));
        AreEqual(8, sim.ExecuteScalar("select s.q from (select a, b from t) s(p, q)"));
    }

    [TestMethod]
    public void AliasList_AcceptsAsKeywordAndBracketedNames()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(new[] { "a", "b" }, ColumnNames(sim, "select * from (select 1, 2) as s(a, b)"));
        CollectionAssert.AreEqual(new[] { "a b", "c d" }, ColumnNames(sim, "select * from (select 1, 2) s([a b], [c d])"));
    }

    /// <summary>The same list applies to an APPLY's derived table.</summary>
    [TestMethod]
    public void AliasList_AppliesToCrossApply()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b int); insert t values (7, 8)");
        CollectionAssert.AreEqual(new[] { "a", "b", "v" }, ColumnNames(sim, "select * from t cross apply (select t.a) x(v)"));
        AreEqual(7, sim.ExecuteScalar("select x.v from t cross apply (select t.a) x(v)"));
    }

    // === Arity and duplicate rules ===

    [TestMethod]
    public void AliasList_ShorterThanProjection_RaisesMsg8158()
        => new Simulation().AssertSqlError(
            "select * from (select 1, 2) s(a)", 8158, "'s' has more columns than were specified in the column list.");

    [TestMethod]
    public void AliasList_LongerThanProjection_RaisesMsg8159()
        => new Simulation().AssertSqlError(
            "select * from (select 1, 2) s(a, b, c)", 8159, "'s' has fewer columns than were specified in the column list.");

    [TestMethod]
    public void AliasList_RepeatedName_RaisesMsg8156()
        => new Simulation().AssertSqlError(
            "select * from (select 1, 2) s(a, a)", 8156, "The column 'a' was specified multiple times for 's'.");

    /// <summary>An empty list isn't a list at all — it fails at the paren.</summary>
    [TestMethod]
    public void AliasList_Empty_RaisesMsg102()
        => new Simulation().AssertSqlError("select * from (select 1, 2) s()", 102, "Incorrect syntax near ')'.");

    // === Msg 8155: no list, and a column has no name of its own ===

    [TestMethod]
    public void NoAliasList_UnnamedColumn_RaisesMsg8155()
        => new Simulation().AssertSqlError(
            "select * from (select 1 x, 2) s", 8155, "No column name was specified for column 2 of 's'.");

    /// <summary>
    /// Real reports every unnamed column rather than stopping at the first, so
    /// the exception carries one error per column.
    /// </summary>
    [TestMethod]
    public void NoAliasList_SeveralUnnamedColumns_ReportOneErrorEach()
    {
        var ex = new Simulation().AssertSqlError("select * from (select 1, 2) s", 8155);
        AreEqual(2, ex.Errors.Count);
        AreEqual("No column name was specified for column 1 of 's'.", ex.Errors[0].Message);
        AreEqual("No column name was specified for column 2 of 's'.", ex.Errors[1].Message);
    }

    /// <summary>A projection whose columns are all named needs no list.</summary>
    [TestMethod]
    public void NoAliasList_AllColumnsNamed_Parses()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b int); insert t values (7, 8)");
        CollectionAssert.AreEqual(new[] { "a", "b" }, ColumnNames(sim, "select * from (select a, b from t) s"));
        CollectionAssert.AreEqual(new[] { "x", "y" }, ColumnNames(sim, "select * from (select 1 x, 2 y) s"));
    }

    /// <summary>
    /// The VALUES constructor and CTE forms of the same syntax keep working —
    /// they reached the shared list parser by their own routes.
    /// </summary>
    [TestMethod]
    public void ValuesAndCteFormsStillParse()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(new[] { "a", "b" }, ColumnNames(sim, "select * from (values (1, 2), (3, 4)) v(a, b)"));
        CollectionAssert.AreEqual(new[] { "m", "n" }, ColumnNames(sim, "with c(m, n) as (select 1, 2) select * from c"));
    }
}
