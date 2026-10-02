using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>EXEC ( … )</c> takes string literals and variables joined by <c>+</c>
/// and nothing else. Every expectation probed 2026-09-26 against SQL Server
/// 2025.
/// </summary>
[TestClass]
public sealed class ExecStringOperandTests
{
    [TestMethod]
    [DataRow("exec ('select ' + '1' + '2')", 12)]
    [DataRow("declare @v varchar(9) = '1'; exec ('select ' + @v + ' + 1')", 2)]
    [DataRow("declare @v nvarchar(20) = N'select 3'; exec (@v)", 3)]
    public void LiteralsAndVariables_Concatenate(string sql, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));

    [TestMethod]
    [DataRow("exec (('select 1'))", 102, "Incorrect syntax near '('.")]
    [DataRow("exec ('select ' + upper('1'))", 102, "Incorrect syntax near 'upper'.")]
    [DataRow("exec (0x73656c6563742036)", 102, "Incorrect syntax near '0x73656c6563742036'.")]
    [DataRow("exec (null)", 156, "Incorrect syntax near the keyword 'null'.")]
    [DataRow("exec ('select 1' + null)", 156, "Incorrect syntax near the keyword 'null'.")]
    [DataRow("exec (N'select 3' collate Latin1_General_BIN)", 156, "Incorrect syntax near the keyword 'collate'.")]
    [DataRow("declare @x xml = '<a/>'; exec (@x)", 257, "Implicit conversion from data type xml to nvarchar is not allowed. Use the CONVERT function to run this query.")]
    public void AnythingElse_IsRefused(string sql, int number, string message)
        => new Simulation().AssertSqlError(sql, number, message);
    // ---- probed 2026-10-02 against SQL Server 2025 ----

    [TestMethod]
    public void Operands_ConvertOneByOne_AndNullAddsNothing()
        => AreEqual("5|1|Jan  2 2024 12:00AM|A", new Simulation().ExecuteScalar("""
            declare @i int = 5, @n varchar(10), @d datetime = '2024-01-02', @b varbinary(4) = 0x41;
            create table #r (id int identity, s varchar(40));
            exec ('insert #r select ' + @i);
            exec ('insert #r select 1' + @n);
            exec ('insert #r select ''' + @d + '''');
            exec ('insert #r select ''' + @b + '''');
            select string_agg(s, '|') within group (order by id) from #r
            """));

    [TestMethod]
    public void AsUserOrLogin_RunsTheBatchInThatContext()
        => AreEqual("dbo|sa|dbo", new Simulation().ExecuteScalar("""
            create table #r (id int identity, s sysname);
            exec ('insert #r select user_name()') as user = 'dbo';
            exec ('insert #r select suser_name()') as login = 'sa';
            insert #r select user_name();
            select string_agg(s, '|') within group (order by id) from #r
            """));

    [TestMethod]
    public void UseInsideADynamicBatch_SendsNoContextMessage()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SimulatedError>().Select(error => error.Number));
        AreEqual("master", connection.CreateCommand("exec ('use master; select db_name()')").ExecuteScalar());
        IsEmpty(messages);
    }

    [TestMethod]
    public void RowCountAfterACalledBatchACompileErrorEnded_IsItsLastStatements()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int identity, n int)");
        _ = sim.AssertSqlError("select 1 union all select 2; exec ('select 1 +'); insert t (n) select @@rowcount", 102);
        _ = sim.AssertSqlError("exec ('select 1; select * from nosuch'); insert t (n) select @@rowcount", 208);
        AreEqual("2,1", sim.ExecuteScalar("select string_agg(cast(n as varchar), ',') within group (order by id) from t"));
    }

    [TestMethod]
    public void SpExecuteSql_WithNoArguments_Msg201()
    {
        var ex = new Simulation().AssertSqlError("exec sp_executesql", 201);
        AreEqual("Procedure or function 'sp_executesql' expects parameter '@statement', which was not supplied.", ex.Errors[0].Message);
        AreEqual("sp_executesql", ex.Errors[0].Procedure);
        AreEqual((byte)10, ex.Errors[0].State);
    }

    [TestMethod]
    public void SpExecuteSql_CalledThroughADatabase_RunsThere()
        => AreEqual("master|simulated", new Simulation().ExecuteScalar(
            "create table #r (id int identity, s sysname); exec master.sys.sp_executesql N'insert #r select db_name()'; insert #r select db_name(); select string_agg(s, '|') within group (order by id) from #r"));

    [TestMethod]
    public void SpExecuteSql_OutputWriteBack_IsCutToTheVariablesWidth()
        => AreEqual("abc", new Simulation().ExecuteScalar(
            "declare @r varchar(3); exec sp_executesql N'set @o = ''abcdef''', N'@o varchar(10) output', @o = @r output; select @r"));

    [TestMethod]
    public void SpExecuteSql_OutputOnAParameterNotDeclaredOutput_Msg8162()
        => _ = new Simulation().AssertSqlError(
            "declare @r int = 1; exec sp_executesql N'set @o = 42', N'@o int', @o = @r output", 8162);
}
