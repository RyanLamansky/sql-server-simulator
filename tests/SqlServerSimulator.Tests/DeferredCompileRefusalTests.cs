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

    /// <summary>A write naming an identity, rowversion or computed column over a created table ends the batch past its TRY (probed 2026-10-19 against SQL Server 2025).</summary>
    [TestMethod]
    [DataRow("update t set id = 5", 8102)]
    [DataRow("update t set rv = 0x01", 272)]
    [DataRow("update t set c = 1", 271)]
    [DataRow("insert t (v, rv) values (1, 0x01)", 273)]
    [DataRow("insert t (v, c) values (1, 1)", 271)]
    public void ColumnRefusalOverACreatedTable_EndsTheBatchPastTheTry(string statement, int number)
    {
        var (rows, error) = Run(new Simulation(), $"select 'first';\ncreate table t (id int identity, v int, rv rowversion, c as v + 1);\nbegin try {statement}; end try begin catch select 'caught' end catch;\nselect 'after'");
        CollectionAssert.AreEqual(new object[] { "first" }, rows);
        AreEqual(number, error.Number);
    }

    /// <summary>
    /// An added constraint real refuses from the table's definition alone — a
    /// second primary key, a second default, a key over a nullable column —
    /// ends the batch past its TRY, while through EXEC the TRY catches it; a
    /// data-dependent refusal stays catchable (probed 2026-10-19 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("alter table t add constraint pk2 primary key (v)", 1779)]
    [DataRow("alter table t add constraint t primary key (v)", 1779)]
    [DataRow("alter table t add constraint df2 default 8 for v", 1781)]
    [DataRow("alter table u add constraint pku primary key (id)", 8111)]
    public void DefinitionRefusedConstraint_EndsTheBatchPastTheTry(string statement, int number)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int not null primary key, v int null constraint df default 9); create table u (id int)");
        var (rows, error) = Run(simulation, $"select 'first';\nbegin try {statement}; end try begin catch select 'caught' end catch;\nselect 'after'");
        CollectionAssert.AreEqual(new object[] { "first" }, rows);
        AreEqual(number, error.Number);
        AreEqual(1750, error.Errors[^1].Number);
    }

    [TestMethod]
    [DataRow("begin try exec('alter table t add constraint df2 default 8 for v'); end try begin catch select 'caught', error_number() end catch", "caught")]
    [DataRow("begin try alter table t add constraint uq unique (v); end try begin catch select 'caught', error_number() end catch", "caught")]
    public void CalledOrDataDependentConstraintRefusal_IsCaught(string sql, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int not null primary key, v int null constraint df default 9); insert t values (1, 1), (2, 1)");
        AreEqual(expected, simulation.ExecuteScalar(sql));
    }

    /// <summary>
    /// An added CHECK binds as the batch compiles against the table as it stands
    /// then, so nothing ahead of it runs — a column an earlier statement adds
    /// included — and a second PRIMARY KEY in a CREATE TABLE refuses the batch
    /// the same way (probed 2026-10-19 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select 'first'; alter table t add w int; alter table t add constraint ck check (w > 0); select 'after'", 207)]
    [DataRow("select 'first'; if 1 = 0 alter table t add constraint ck check (nosuch > 0); select 'after'", 207)]
    [DataRow("select 'first'; begin try create table x (a int primary key, b int primary key); end try begin catch select 'caught' end catch;", 8110)]
    public void CompileTimeDdlRefusal_RunsNothing(string sql, int number)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int not null primary key, v int null)");
        var (rows, error) = Run(simulation, sql);
        IsEmpty(rows);
        AreEqual(number, error.Number);
    }

    /// <summary>An index naming a column twice refuses the batch as it compiles, over a table the batch creates too (probed 2026-10-19 against SQL Server 2025).</summary>
    [TestMethod]
    [DataRow("select 'first'; begin try create index ix on t (a, a); end try begin catch select 'caught' end catch", (byte)1)]
    [DataRow("select 'first'; begin try create index ix on t (a) include (a); end try begin catch select 'caught' end catch", (byte)2)]
    [DataRow("create table u (a int); select 'first'; begin try create index ix on u (a, a); end try begin catch select 'caught' end catch", (byte)1)]
    public void IndexColumnNamedTwice_RunsNothing(string sql, byte state)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int, a int)");
        var (rows, error) = Run(simulation, sql);
        IsEmpty(rows);
        AreEqual(1909, error.Number);
        AreEqual(state, error.Errors[0].State);
    }

    /// <summary>A query over a table whose clustered index the batch disabled ends the batch past its TRY (probed 2026-10-19 against SQL Server 2025).</summary>
    [TestMethod]
    public void QueryOverADisabledClusteredIndex_EndsTheBatchPastTheTry()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int); create clustered index cx on t (id)");
        var (rows, error) = Run(simulation, "select 'first'; begin try alter index cx on t disable; select count(*) from t; end try begin catch select 'caught' end catch; select 'after'");
        CollectionAssert.AreEqual(new object[] { "first" }, rows);
        AreEqual(8655, error.Number);
    }

    /// <summary>A failing ALTER COLUMN rewrite a TRY catches leaves no row count (probed 2026-10-19 against SQL Server 2025).</summary>
    [TestMethod]
    public void CaughtAlterColumnRewrite_SendsNoCount()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int not null primary key, s varchar(10)); insert t values (1, 'abc')");
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("begin try alter table t alter column s varchar(1); end try begin catch select 1 end catch");
        using var reader = command.ExecuteReader();
        AreEqual(-1, reader.RecordsAffected);
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

    /// <summary>
    /// With no separator, the walk goes on past a deferral raised inside a
    /// query or DML statement when the next keyword certainly starts another
    /// statement — a query past the clauses the deferred one owns, or one no
    /// query holds — so a later binder error still refuses the batch before
    /// anything runs (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select a from t where a = cast(1 as xml)\nprint 'x'\nselect nosuchcol from u")]
    [DataRow("update t set a = 1 where a = cast(1 as xml)\nset @v = 1\nselect nosuchcol from u")]
    [DataRow("delete t where a = cast(1 as xml)\nexec sp_who\nselect nosuchcol from u")]
    [DataRow("select a from t where a = cast(1 as xml)\nselect 1 union select nosuchcol from u")]
    [DataRow("insert u select a from t where a = cast(1 as xml)\nselect nosuchcol from u")]
    [DataRow("select a from t where a = cast(1 as xml)\nif 1 = 1 select nosuchcol from u")]
    [DataRow("select a from t where a = cast(1 as xml)\nbegin\nselect nosuchcol from u\nend")]
    public void WalkResumesWhereTheNextStatementCertainlyStarts(string tail)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table u (b int)");
        var (rows, error) = Run(simulation, "select 'first'\ndeclare @v int\ncreate table t (a int)\n" + tail);
        IsEmpty(rows);
        AreEqual(207, error.Number);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('t')"));
    }
}
