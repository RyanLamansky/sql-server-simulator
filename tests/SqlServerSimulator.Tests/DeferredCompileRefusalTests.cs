using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Refusals real settles as it compiles a statement — a DML <c>TOP</c>'s
/// written constant, a <c>NEXT VALUE FOR</c> in a nested query — wait with a
/// statement reading what the batch has yet to create, so the statements
/// ahead of it run first; and a compile walk carries on past such a deferral
/// raised partway through a statement it can find the end of (probed
/// 2026-10-06 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class DeferredCompileRefusalTests
{
    private static (List<object?> Rows, SimulatedSqlException Error) Run(Simulation simulation, string sql)
    {
        var rows = new List<object?>();
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(sql);
        var error = Throws<SimulatedSqlException>(() =>
        {
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                    rows.Add(reader.GetValue(0));
            }
            while (reader.NextResult());
        });
        return (rows, error);
    }

    [TestMethod]
    [DataRow("delete top (-1) from t", 127)]
    [DataRow("update top (101) percent t set a = 1", 1031)]
    [DataRow("delete top (1.5) from t", 1060)]
    [DataRow("merge top (-1) t using (select 1 a) s on t.a = s.a when matched then delete", 127)]
    [DataRow("begin try delete top (-1) from t end try begin catch select 'caught' end catch", 127)]
    public void TopOverACreatedTable_RunsTheStatementsAheadFirst(string statement, int number)
    {
        var (rows, error) = Run(new Simulation(), $"select 'first';\ncreate table t (a int);\n{statement};\nselect 'mid'");
        CollectionAssert.AreEqual(new object[] { "first" }, rows);
        AreEqual(number, error.Number);
        AreEqual(3, error.LineNumber);
        HasCount(1, error.Errors);
    }

    [TestMethod]
    [DataRow("select 'first';\ndelete top (-1) from e")]
    [DataRow("select 'first';\ncreate table t (a int);\ninsert top (-1) into t values (1)")]
    public void TopOverAnExistingTableOrIntoAnInsert_RefusesTheBatch(string sql)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table e (a int)");
        var (rows, error) = Run(simulation, sql);
        IsEmpty(rows);
        AreEqual(127, error.Number);
    }

    [TestMethod]
    public void NextValueForInADerivedTableOverACreatedTable_Waits()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create sequence s");
        var (rows, error) = Run(simulation, "select 'first';\ncreate table t (a int);\nselect * from (select next value for s x from t) d;\nselect 'mid'");
        CollectionAssert.AreEqual(new object[] { "first" }, rows);
        AreEqual(11719, error.Number);
    }

    [TestMethod]
    public void NextValueForOverACreatedSequenceAlone_RefusesTheBatch()
    {
        var (rows, error) = Run(new Simulation(), "select 'first';\ncreate sequence s;\nselect * from (select next value for s x from sys.objects) d");
        IsEmpty(rows);
        AreEqual(11719, error.Number);
    }

    [TestMethod]
    public void NextValueForInAnUntakenBranchOverACreatedTable_SendsNothing()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create sequence s");
        AreEqual("mid", simulation.ExecuteScalar("create table t (a int);\nif 1 = 0 select * from (select next value for s x from t) d;\nselect 'mid'"));
    }

    /// <summary>
    /// Past a deferral raised inside a statement ending in a separator, the
    /// compile walk binds the rest of the batch, so a later binder error
    /// refuses it before anything runs.
    /// </summary>
    [TestMethod]
    public void WalkResumesAfterADeferredStatementsSeparator()
    {
        var (rows, error) = Run(new Simulation(), "select 'first';\ncreate table t (a int);\nselect a from t where a = cast(1 as xml);\nselect 'mid';\nselect nosuchcol from sys.objects;");
        IsEmpty(rows);
        AreEqual(207, error.Number);
        AreEqual(5, error.LineNumber);
    }
}
