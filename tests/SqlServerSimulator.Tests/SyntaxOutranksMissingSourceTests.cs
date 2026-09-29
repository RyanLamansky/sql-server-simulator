namespace SqlServerSimulator;

/// <summary>
/// Real parses a batch before binding any of it, so a syntax error later in a
/// statement outranks the Msg 208 for a FROM source that doesn't exist
/// (probed 2026-09-29 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class SyntaxOutranksMissingSourceTests
{
    [TestMethod]
    [DataRow("select 1 from missing_t where", 102, "Incorrect syntax near 'where'.")]
    [DataRow("select 1 from missing_t tablesample (10 percent) where", 102, "Incorrect syntax near 'where'.")]
    [DataRow("select 1 from missing_t x tablesample system (10 percent) repeatable (1) where", 102, "Incorrect syntax near 'where'.")]
    [DataRow("select 1 from t x cross apply dbo.nof(x.a) where", 102, "Incorrect syntax near 'where'.")]
    [DataRow("select x.a from t x outer apply dbo.nof(x.a) f where", 102, "Incorrect syntax near 'where'.")]
    [DataRow("select 1 from missing_t tablesample", 102, "Incorrect syntax near 'tablesample'.")]
    public void ASyntaxErrorPastTheSource_IsReportedInsteadOfTheMissingObject(string sql, int number, string message)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int)");
        sim.AssertSqlError(sql, number, message);
    }

    [TestMethod]
    [DataRow("select 1 from missing_t tablesample (10 percent)")]
    [DataRow("select 1 from t x cross apply dbo.nof(x.a) f")]
    public void AWellFormedStatementOverAMissingSource_StillRaisesMsg208(string sql)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int)");
        _ = sim.AssertSqlError(sql, 208);
    }
}
