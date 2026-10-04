using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// A <c>CREATE TYPE … FROM</c> whose base or arguments real refuses, as it
/// raises them — when the statement runs, which is why an earlier statement of
/// the batch has answered and a <c>CATCH</c> reads the second error of the
/// pair (probed 2026-09-30 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class CreateTypeRefusalTests
{
    [TestMethod]
    [DataRow("char", 2724, 1)]
    [DataRow("binary", 2724, 1)]
    [DataRow("decimal(50)", 2750, 1)]
    [DataRow("int(9)", 2716, 1)]
    [DataRow("int(9,1)", 2724, 4)]
    [DataRow("varchar(9,1)", 2724, 4)]
    [DataRow("datetime2(2,1)", 2724, 5)]
    [DataRow("time(9,1)", 2724, 5)]
    [DataRow("datetimeoffset(3,1)", 2724, 5)]
    public void Refusal_IsAPair_TheSecondBeing225_AndRunsAfterEarlierStatements(string @base, int first, int state)
    {
        var simulation = new Simulation();
        var error = Throws<SimulatedSqlException>(() => simulation.ExecuteScalar($"select 'before'; create type t1 from {@base}"));
        CollectionAssert.AreEqual(new[] { first, 225 }, Numbers(error));
        AreEqual(state, error.Errors[0].State);
    }

    [TestMethod]
    [DataRow("char")]
    [DataRow("decimal(50)")]
    [DataRow("time(9,1)")]
    public void TryCatch_ReadsTheSecondError_AndAThrowSendsBothAgain(string @base)
    {
        var simulation = new Simulation();
        AreEqual(225, simulation.ExecuteScalar($"begin try create type t1 from {@base} end try begin catch select error_number() end catch"));
        var error = Throws<SimulatedSqlException>(() => simulation.ExecuteScalar($"begin try create type t1 from {@base} end try begin catch select error_number(); throw end catch"));
        AreEqual(2, error.Errors.Count);
        AreEqual(225, error.Number == 225 ? 225 : error.Errors[1].Number);
    }

    [TestMethod]
    public void ScaleAbovePrecision_Is192_AtCompile_AheadOfEarlierStatements()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("select 'before'; create type t1 from datetime2(2,5)", 192);
        _ = simulation.AssertSqlError("select 'before'; create type t1 from int(2,3)", 192);
    }

    [TestMethod]
    public void MaxWithASecondArgument_IsAtTheComma()
        => _ = new Simulation().AssertSqlError("create type t1 from varbinary(max, 1)", 102);

    [TestMethod]
    public void BareThrow_SendsEveryEntryOfAnErrorRaisedAsSeveral()
    {
        var simulation = new Simulation();
        var error = Throws<SimulatedSqlException>(() => simulation.ExecuteScalar(
            "create table t (a int primary key); begin try alter table t drop constraint nope end try begin catch throw end catch"));
        CollectionAssert.AreEqual(new[] { 3728, 3727 }, Numbers(error));
    }
}
