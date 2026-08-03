using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A SELECT with no FROM clause reads one synthesized row, so an aggregate,
/// GROUP BY, HAVING or window written over it binds exactly as it would over a
/// one-row table: <c>SELECT COUNT(*)</c> is 1 and <c>SELECT MIN(-57)</c> is
/// -57. Probed against SQL Server 2025 (2026-08-03) — every expected value
/// below is real's own answer.
/// </summary>
[TestClass]
public sealed class SourcelessAggregateTests
{
    /// <summary>
    /// Runs <paramref name="commandText"/> and renders the result set as one
    /// comma-joined string per row, rows semicolon-separated — so a shape's row
    /// <em>count</em> and its values pin in a single assertion. NULL renders as
    /// <c>NULL</c>; an empty result set is the empty string. The count is the
    /// point of several cases here: an aggregate keeps its row when WHERE
    /// admits none, while a window loses it.
    /// </summary>
    private static string Rows(string commandText)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand(commandText);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString()!;
            rows.Add(string.Join(", ", values));
        }
        return string.Join("; ", rows);
    }

    [TestMethod]
    public void Aggregate_NoFromClause_CollapsesToOneRow()
    {
        AreEqual("1", Rows("select count(*)"));
        AreEqual("-57", Rows("select min(-57)"));
        AreEqual("1", Rows("select sum(cast(1 as real))"));
        AreEqual("-124", Rows("select -67 + min(distinct -57)"));
        AreEqual("5", Rows("select avg(5)"));
        AreEqual("1", Rows("select count(1)"));
    }

    [TestMethod]
    public void Aggregate_NoFromClause_ObservesNullOperandRules()
    {
        // The synthesized row is read, then the operand's NULL is skipped —
        // COUNT sees an empty input and SUM answers NULL, the same split an
        // all-NULL column produces.
        AreEqual("0", Rows("select count(cast(null as int))"));
        AreEqual("NULL", Rows("select sum(cast(null as int))"));
    }

    [TestMethod]
    public void Aggregate_NoFromClause_WhereExcludingTheRow_StillProjectsOneRow()
    {
        // WHERE removes the synthesized row, but the implicit empty group
        // survives it — real's rule for an aggregate over no rows.
        AreEqual("0", Rows("select count(*) where 1 = 0"));
        AreEqual("NULL", Rows("select min(-57) where 1 = 0"));
    }

    [TestMethod]
    public void Aggregate_NoFromClause_BesideAConstantProjection()
    {
        AreEqual("1, 1", Rows("select 1, count(*)"));
        AreEqual("1, 0", Rows("select 1, count(*) where 1 = 0"));
    }

    [TestMethod]
    public void Aggregate_NoFromClause_DistinctAndNestedForms()
    {
        AreEqual("1", Rows("select count(distinct 5)"));
        AreEqual("a", Rows("select string_agg(cast('a' as varchar(10)), ',')"));
        AreEqual("1", Rows("select (select count(*))"));
        AreEqual("1", Rows("select max(x) from (select count(*) as x) t"));
        AreEqual("1", Rows("select distinct count(*)"));
    }

    [TestMethod]
    public void Aggregate_NoFromClause_HonorsRowLimitsAndOrderBy()
    {
        AreEqual("1", Rows("select top 1 count(*)"));
        AreEqual("", Rows("select top 0 count(*)"));
        AreEqual("1", Rows("select count(*) order by count(*)"));
        AreEqual("", Rows("select count(*) order by 1 offset 1 rows"));
    }

    [TestMethod]
    public void GroupByEmptySet_NoFromClause_ProjectsOneRow()
    {
        // The empty grouping set is the one GROUP BY form a source-less query
        // accepts — every other item would have to name a column.
        AreEqual("1", Rows("select count(*) group by ()"));
        AreEqual("1", Rows("select count(*) group by grouping sets(())"));
        AreEqual("1", Rows("select 1 group by ()"));
    }

    [TestMethod]
    public void GroupBy_NoFromClause_ColumnlessItem_RaisesMsg164()
        => new Simulation().AssertSqlError(
            "select count(*) group by 1",
            164,
            "Each GROUP BY expression must contain at least one column that is not an outer reference.");

    [TestMethod]
    public void Having_NoFromClause_FiltersTheImplicitGroup()
    {
        AreEqual("1", Rows("select count(*) having count(*) > 0"));
        AreEqual("", Rows("select count(*) having count(*) > 5"));
        AreEqual("1", Rows("select count(*) group by () having count(*) = 1"));
    }

    [TestMethod]
    public void Having_NoFromClause_WithoutAnAggregate_StillGroupsTheRow()
    {
        // HAVING alone makes the query an aggregate query, so the row survives
        // or vanishes on the predicate rather than on a WHERE.
        AreEqual("1", Rows("select 1 having 1 = 1"));
        AreEqual("", Rows("select 1 having 1 = 2"));
    }

    [TestMethod]
    public void Window_NoFromClause_ReadsTheSynthesizedRow()
    {
        AreEqual("1", Rows("select count(*) over ()"));
        AreEqual("1", Rows("select sum(1) over ()"));
        AreEqual("1", Rows("select row_number() over (order by (select 1))"));
    }

    [TestMethod]
    public void Window_NoFromClause_WhereExcludingTheRow_ProjectsNoRow()
    {
        // The split against the aggregate case: a window has no group to
        // collapse to, so losing the only row loses the result row with it.
        AreEqual("", Rows("select count(*) over () where 1 = 0"));
    }

    [TestMethod]
    public void AggregateOverAggregate_NoFromClause_RaisesMsg130()
        => new Simulation().AssertSqlError(
            "select sum(count(*))",
            130,
            "Cannot perform an aggregate function on an expression containing an aggregate or a subquery.");

    [TestMethod]
    public void Aggregate_NoFromClause_InsideAnOuterQuerysSelectList()
    {
        // The source-less aggregate is its own one-row query per outer row, so
        // the outer row count is untouched.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int); insert t values (1), (2), (3)").ExecuteNonQuery();
        using var command = connection.CreateCommand("select a, (select count(*)) from t");
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetValue(0)}, {reader.GetValue(1)}");
        AreEqual("1, 1; 2, 1; 3, 1", string.Join("; ", rows));
    }

    [TestMethod]
    public void Aggregate_NoFromClause_AsASetOperationBranch()
        => AreEqual("1; 9", Rows("select count(*) union all select 9"));

    [TestMethod]
    public void Aggregate_NoFromClause_SelectInto_DeclaresAggregateColumnsNullable()
    {
        // Real derives the destination declaration from the same nullability
        // inference the wire's fNullable flag reports: an aggregate is nullable,
        // the literal beside it is not.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("select count(*) as c, min(-57) as m, 1 as lit into dest").ExecuteNonQuery();
        using (var command = connection.CreateCommand("""
            select COLUMN_NAME + ' ' + DATA_TYPE + ' ' + IS_NULLABLE from INFORMATION_SCHEMA.COLUMNS
            where TABLE_NAME = 'dest' order by ORDINAL_POSITION
            """))
        {
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
                columns.Add(reader.GetString(0));
            AreEqual("c int YES; m int YES; lit int NO", string.Join("; ", columns));
        }

        using var rowCommand = connection.CreateCommand("select * from dest");
        using var rowReader = rowCommand.ExecuteReader();
        IsTrue(rowReader.Read());
        AreEqual("1, -57, 1", $"{rowReader.GetValue(0)}, {rowReader.GetValue(1)}, {rowReader.GetValue(2)}");
        IsFalse(rowReader.Read());
    }
}
