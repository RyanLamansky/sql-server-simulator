using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET NUMERIC_ROUNDABORT ON</c> turns a lost fractional digit into
/// Msg 8115: state 7 for a conversion into a narrower-scale <c>decimal</c>,
/// judged by the two scales rather than the value, and state 1 for a
/// multiplication or division whose result scale the 38-digit cap cut
/// (probed 2026-09-25 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class NumericRoundAbortTests
{
    [TestMethod]
    [DataRow("cast(1.25 as decimal(2,1))", (byte)7)]
    [DataRow("cast(1.20 as decimal(2,1))", (byte)7)]
    [DataRow("cast(cast(0 as decimal(3,2)) as decimal(10,0))", (byte)7)]
    [DataRow("convert(decimal(2,1), '1.25')", (byte)7)]
    [DataRow("cast('1.20' as decimal(3,1))", (byte)7)]
    [DataRow("cast(cast(1.23456 as money) as decimal(5,2))", (byte)7)]
    [DataRow("coalesce(cast(1.25 as decimal(38,2)), cast(1 as decimal(38,0)))", (byte)7)]
    [DataRow("case when 1=1 then cast(1.25 as decimal(38,37)) else cast(1 as decimal(38,0)) end", (byte)7)]
    [DataRow("cast(10 as decimal(38,10)) * cast(10 as decimal(38,10))", (byte)1)]
    [DataRow("cast(1 as decimal(38,0)) / cast(3 as decimal(10,0))", (byte)1)]
    [DataRow("cast(1 as decimal(38,30)) * 2", (byte)1)]
    [DataRow("avg(x) from (values (cast(7 as decimal(3,0)))) v(x)", (byte)1)]
    [DataRow("avg(x) over () from (values (cast(1 as decimal(3,0))), (2)) v(x)", (byte)1)]
    public void LostDigit_RaisesMsg8115(string expression, byte state)
        => AreEqual(state, new Simulation().AssertSqlError($"set numeric_roundabort on; select {expression}", 8115).State);

    [TestMethod]
    [DataRow("cast(1.2 as decimal(3,1))", "1.2")]
    [DataRow("cast('1.2' as decimal(3,1))", "1.2")]
    [DataRow("cast(1.25e0 as decimal(3,1))", "1.3")]
    [DataRow("cast(2.5 as int)", "2")]
    [DataRow("1.0/3", "0.333333")]
    [DataRow("cast(1 as decimal(10,0)) / cast(3 as decimal(10,0))", "0.33333333333")]
    [DataRow("cast(1.25 as decimal(3,2)) % cast(1 as decimal(2,1))", "0.25")]
    [DataRow("cast(1.25 as money)", "1.2500")]
    [DataRow("try_cast(1.25 as decimal(3,1))", null)]
    [DataRow("cast(cast(null as decimal(3,2)) as decimal(2,1))", null)]
    [DataRow("avg(x) from (values (cast(1 as money)), (2)) v(x)", "1.5000")]
    public void NothingLost_Answers(string expression, string? expected)
    {
        var result = new Simulation().ExecuteScalar($"set numeric_roundabort on; select {expression}");
        AreEqual(expected, result is null or DBNull ? null : Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void RoundingWrite_IsRefused()
        => new Simulation().AssertSqlError("set numeric_roundabort on; create table t (d decimal(3,1)); insert t values (1.25)", 8115);

    /// <summary>A DECLARE whose initializer fails still declares its variable, NULL.</summary>
    [TestMethod]
    [DataRow("set numeric_roundabort on; begin try declare @d decimal(2,1) = 1.25; end try begin catch end catch select @d")]
    [DataRow("begin try declare @d int = 1/0; end try begin catch end catch select @d")]
    public void FailedDeclareInitializer_LeavesTheVariableNull(string sql)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(sql));
}
