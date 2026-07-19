using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for table-value-constructor derived tables:
/// <c>(VALUES (row), (row), …) alias(col, …)</c> as a FROM source, a JOIN
/// source, and a correlated <c>CROSS</c> / <c>OUTER APPLY</c> source (the
/// SSMS server-properties shape). Per-column result types promote across
/// rows like set-op / CASE branches. Grammar edges (required alias / column
/// list, column-count mismatches) probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ValuesDerivedTableTests
{
    private static List<object?[]> ReadAll(DbDataReader reader)
    {
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Fully drains <paramref name="sql"/> so a per-row runtime error (raised
    /// only when a later row is materialized, unlike a parse-time error that
    /// <c>AssertSqlError</c> catches via the first-value read) surfaces.
    /// Returns the raised error's number.
    /// </summary>
    private static string DrainError(string sql)
    {
        var ex = Throws<DbException>(() =>
        {
            using var reader = new Simulation().ExecuteReader(sql);
            while (reader.Read())
            {
            }
        });
        return (string)ex.Data["HelpLink.EvtID"]!;
    }

    [TestMethod]
    public void StarProjection_NamesColumnsFromAliasList()
    {
        using var reader = new Simulation().ExecuteReader("select * from (values (1, 'a'), (2, 'b')) v(id, nm)");
        AreEqual("id", reader.GetName(0));
        AreEqual("nm", reader.GetName(1));
        var rows = ReadAll(reader);
        HasCount(2, rows);
        AreEqual(1, rows[0][0]);
        AreEqual("a", rows[0][1]);
        AreEqual(2, rows[1][0]);
        AreEqual("b", rows[1][1]);
    }

    [TestMethod]
    public void QualifiedReferences_ResolveThroughAliasAndWhere()
    {
        using var reader = new Simulation().ExecuteReader("select v.a, v.b from (values (1, 'p'), (2, 'q')) v(a, b) where v.a > 1");
        var rows = ReadAll(reader);
        HasCount(1, rows);
        AreEqual(2, rows[0][0]);
        AreEqual("q", rows[0][1]);
    }

    [TestMethod]
    public void ExpressionsAndSubqueries_AllowedInRows()
    {
        using var reader = new Simulation().ExecuteReader("select a from (values (1 + 1), (len('xyz')), ((select 6))) v(a)");
        CollectionAssert.AreEqual(new object?[] { 2, 3, 6 }, ReadAll(reader).Select(r => r[0]).ToArray());
    }

    [TestMethod]
    public void Join_AgainstValuesSource()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table x (n int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert x values (1), (2), (3)").ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select x.n, v.nm from x join (values (1, 'one'), (2, 'two')) v(id, nm) on x.n = v.id order by x.n").ExecuteReader();
        var rows = ReadAll(reader);
        HasCount(2, rows);
        AreEqual(1, rows[0][0]);
        AreEqual("one", rows[0][1]);
        AreEqual(2, rows[1][0]);
        AreEqual("two", rows[1][1]);
    }

    [TestMethod]
    public void CrossApply_CorrelatedRows_SeeOuterColumns()
    {
        // The SSMS server-properties shape: an APPLY whose VALUES rows mix
        // literals with references to the outer row's columns.
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table host_info (host_platform varchar(20), host_sku int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert host_info values ('Linux', 5)").ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select t.id, t.[name], t.internal_value, t.[value] from host_info " +
            "cross apply (values (1001, 'host_platform', 0, host_platform), (1005, 'host_sku', host_sku, '')) " +
            "t(id, [name], internal_value, [value])").ExecuteReader();
        var rows = ReadAll(reader);
        HasCount(2, rows);
        CollectionAssert.AreEqual(new object?[] { 1001, "host_platform", 0, "Linux" }, rows[0]);
        CollectionAssert.AreEqual(new object?[] { 1005, "host_sku", 5, "" }, rows[1]);
    }

    [TestMethod]
    public void OuterApply_FilteredValues_NullFillsWhenNoRow()
    {
        // A VALUES constructor always yields rows, so null-fill needs a
        // filtered wrapper: OUTER APPLY over a derived SELECT whose VALUES
        // source correlates to the outer row and whose WHERE can exclude all.
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table o (id int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert o values (1), (100)").ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select o.id, x.v from o " +
            "outer apply (select t.v from (values (o.id), (o.id * 10)) t(v) where t.v > 50) x " +
            "order by o.id, x.v").ExecuteReader();
        var rows = ReadAll(reader);
        // id=1: (1),(10) both <= 50 → no row → null-fill.
        // id=100: (100),(1000) both > 50 → two rows.
        HasCount(3, rows);
        CollectionAssert.AreEqual(new object?[] { 1, null }, rows[0]);
        CollectionAssert.AreEqual(new object?[] { 100, 100 }, rows[1]);
        CollectionAssert.AreEqual(new object?[] { 100, 1000 }, rows[2]);
    }

    [TestMethod]
    public void TypePromotion_IntAndDecimal_YieldsDecimal()
    {
        using var reader = new Simulation().ExecuteReader("select a from (values (1), (2.5)) v(a)");
        var rows = ReadAll(reader);
        HasCount(2, rows);
        AreEqual(1m, rows[0][0]);
        AreEqual(2.5m, rows[1][0]);
    }

    [TestMethod]
    public void TypePromotion_VarcharAndNvarchar_PreservesUnicode()
    {
        // varchar → nvarchar promotion: the CP1252-unrepresentable character
        // survives, proving the column widened to nvarchar rather than
        // collapsing to '?'.
        using var reader = new Simulation().ExecuteReader("select a from (values ('a'), (N'あ')) v(a)");
        var rows = ReadAll(reader);
        HasCount(2, rows);
        AreEqual("a", rows[0][0]);
        AreEqual("あ", rows[1][0]);
    }

    [TestMethod]
    public void RuntimeConversionFailure_WhenPromotedTypeRejectsCell()
    {
        // int wins over varchar by precedence (Promote → int); 'abc' then
        // fails to convert at runtime (Msg 245), matching SQL Server.
        AreEqual("245", DrainError("select a from (values (1), ('abc')) v(a)"));
    }

    [TestMethod]
    public void MissingColumnList_RaisesMsg8155() =>
        new Simulation().AssertSqlError("select * from (values (1), (2)) v", 8155);

    [TestMethod]
    public void MissingAlias_RaisesMsg102() =>
        new Simulation().AssertSqlError("select * from (values (1), (2))", 102);

    [TestMethod]
    public void MoreRowColumnsThanList_RaisesMsg8158() =>
        new Simulation().AssertSqlError("select * from (values (1, 2), (3, 4)) v(a)", 8158);

    [TestMethod]
    public void FewerRowColumnsThanList_RaisesMsg8159() =>
        new Simulation().AssertSqlError("select * from (values (1), (2)) v(a, b)", 8159);

    [TestMethod]
    public void RowsWithDifferingArity_RaiseMsg10709() =>
        new Simulation().AssertSqlError("select * from (values (1), (2, 3)) v(a)", 10709);

    [TestMethod]
    public void EmptyRow_RaisesMsg102() =>
        new Simulation().AssertSqlError("select * from (values ()) v(a)", 102);
}
