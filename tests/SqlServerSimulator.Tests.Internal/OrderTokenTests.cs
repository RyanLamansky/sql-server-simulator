using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The ORDER token (0xA9) real SQL Server 2025 sends after a result's
/// COLMETADATA when the statement has an ORDER BY — each term's select-list
/// position, 0 for one the select list doesn't hold, constants left out —
/// captured 2026-10-17 with a raw TDS client.
/// </summary>
[TestClass]
public sealed class OrderTokenTests
{
    private static List<string> Batch(TdsSessionFixture fixture, string sql) => TdsSessionFixture.Describe(fixture.RunBatch(sql));

    private static TdsSessionFixture Seeded()
    {
        var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int primary key, b int); insert t values (1, 2), (2, 1)");
        return fixture;
    }

    [TestMethod]
    [DataRow("select a, b from t order by b desc, a", "ORDER 2,1")]
    [DataRow("select b, a from t order by a", "ORDER 2")]
    [DataRow("select a from t order by b", "ORDER 0")]
    [DataRow("select a, b from t order by 2, 1", "ORDER 2,1")]
    [DataRow("select a, b + 1 as x from t order by x", "ORDER 2")]
    [DataRow("select a, b + 1 from t order by b + 1", "ORDER 2")]
    [DataRow("select b + 1 from t order by 1 + b", "ORDER 0")]
    [DataRow("select a x, a y from t order by a", "ORDER 1")]
    [DataRow("select a x, a y from t order by y", "ORDER 2")]
    [DataRow("select a as b, b as a from t order by a", "ORDER 2")]
    [DataRow("select a, b from t order by a, (select 1)", "ORDER 1,0")]
    [DataRow("select a, 1 as x from t order by a, x", "ORDER 1")]
    [DataRow("select max(a) from t order by max(a)", "ORDER 1")]
    [DataRow("select top 1 a, b from t order by b", "ORDER 2")]
    [DataRow("select a from t union all select b from t order by 1", "ORDER 1")]
    [DataRow("select a as x from t union select b from t order by a", "ORDER 1")]
    [DataRow("declare @v int = 1; select @v as v order by v", "ORDER 1")]
    public void OrderedResult_SendsTheTermsPositions(string sql, string order)
    {
        using var fixture = Seeded();
        var tokens = Batch(fixture, sql);
        AreEqual(order, tokens[tokens.IndexOf("ROWS") + 1], string.Join(" / ", tokens));
    }

    [TestMethod]
    [DataRow("select a from t")]
    [DataRow("select a, 1 as x from t order by x")]
    [DataRow("select 1 as x order by x")]
    [DataRow("select a from (select top 5 a from t order by a) d")]
    public void ResultWithNoOrderToSend_SendsNoToken(string sql)
    {
        using var fixture = Seeded();
        DoesNotContain(static token => token.StartsWith("ORDER", StringComparison.Ordinal), Batch(fixture, sql));
    }

    [TestMethod]
    public void ProcedureAndCursorResults_CarryTheirOrderToo()
    {
        using var fixture = Seeded();
        _ = fixture.RunBatch("create procedure p as select a, b from t order by b");
        CollectionAssert.AreEqual(
            new[] { "ROWS", "ORDER 2", "DONEINPROC C1 MORE|COUNT 2", "RET 0", "DONEPROC E0 FINAL 0" },
            Batch(fixture, "exec p"));
        var fetched = Batch(fixture, "declare c cursor for select a, b from t order by b; open c; fetch next from c; close c; deallocate c");
        AreEqual("ORDER 2", fetched[fetched.IndexOf("ROWS") + 1]);
    }

    /// <summary>
    /// A system procedure's results carry the ORDER its query has: by
    /// position within the one object a name picked, by owner and name for a
    /// pattern or a miss.
    /// </summary>
    [TestMethod]
    [DataRow("exec sp_columns t", "ORDER 17")]
    [DataRow("exec sp_columns 'nosuch'", "ORDER 2,3,17")]
    [DataRow("exec sp_tables 'nosuch'", "ORDER 4,1,2,3")]
    [DataRow("exec sp_pkeys 'nosuch'", "ORDER 1,2,3,5")]
    [DataRow("exec sp_fkeys 'nosuch'", "ORDER 5,6,7,9,8")]
    [DataRow("exec sp_fkeys @fktable_name = 'nosuch'", "ORDER 1,2,3,9,4")]
    [DataRow("exec sp_stored_procedures 'nosuch'", "ORDER 2,3")]
    [DataRow("exec sp_datatype_info 99", "ORDER 2,0,12,11,20")]
    public void SystemProcedureResult_SendsItsQuerysOrder(string sql, string order)
    {
        using var fixture = Seeded();
        var tokens = Batch(fixture, sql);
        AreEqual(order, tokens[tokens.IndexOf("ROWS") + 1], string.Join(" / ", tokens));
    }
}
