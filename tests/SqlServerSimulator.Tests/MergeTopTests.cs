using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>MERGE TOP (n) [PERCENT]</c>: the cap on the actions a MERGE takes,
/// probed 2026-09-28 against SQL Server 2025. See <c>docs/claude/dml.md</c>.
/// </summary>
[TestClass]
public sealed class MergeTopTests
{
    private const string Seed = """
        create table t (id int primary key, v int);
        create table s (id int, v int);
        insert t values (1, 10), (2, 20), (3, 30), (4, 40);
        """;

    /// <summary>
    /// The result: the statement's <c>@@ROWCOUNT</c>, then the target's rows
    /// as <c>id:v</c>.
    /// </summary>
    private static object? Run(string rows, string merge) => new Simulation().ExecuteScalar($"""
        {Seed}
        insert s values {rows};
        {merge};
        declare @n int = @@rowcount;
        select concat(@n, '|', string_agg(concat(id, ':', v), ',') within group (order by id)) from t
        """);

    [TestMethod]
    [DataRow("(1, 100), (2, 200), (3, 300), (5, 500), (6, 600)", "top (2)", "2|1:100,2:200,3:30,4:40")]
    [DataRow("(3, 1), (1, 1), (2, 1)", "top (1)", "1|1:10,2:20,3:1,4:40")]
    [DataRow("(5, 1), (2, 1)", "top (1)", "1|1:10,2:20,3:30,4:40,5:1")]
    [DataRow("(1, 100), (2, 200), (3, 300), (5, 500), (6, 600)", "top (50) percent", "3|1:100,2:200,3:300,4:40")]
    [DataRow("(1, 100), (2, 200), (3, 300)", "top (34.5) percent", "2|1:100,2:200,3:30,4:40")]
    [DataRow("(1, 100), (2, 200), (3, 300)", "top (10) percent", "1|1:100,2:20,3:30,4:40")]
    [DataRow("(1, 100)", "top (0)", "0|1:10,2:20,3:30,4:40")]
    [DataRow("(1, 100), (2, 200), (3, 300)", "top ((select 2)) into", "2|1:100,2:200,3:30,4:40")]
    public void CapsActionsInSourceOrder(string rows, string top, string expected)
        => AreEqual(expected, Run(rows, $"merge {top} t using s on t.id = s.id when matched then update set v = s.v when not matched then insert values (s.id, s.v)"));

    /// <summary>A NOT MATCHED BY SOURCE delete follows every source row's action.</summary>
    [TestMethod]
    public void BySourceDeletesComeLast()
        => AreEqual("3|2:200,3:30,4:40,5:500", Run("(5, 500), (2, 200)",
            "merge top (3) t using s on t.id = s.id when matched then update set v = s.v when not matched by target then insert values (s.id, s.v) when not matched by source then delete"));

    /// <summary>A row the WHEN clauses decline costs nothing.</summary>
    [TestMethod]
    public void DeclinedRowsAreNotCounted()
        => AreEqual("2|1:10,2:22,3:30,4:40,5:55", Run("(1, 10), (2, 22), (5, 55), (6, 66)",
            "merge top (2) t using s on t.id = s.id when matched and t.v <> s.v then update set v = s.v when not matched then insert values (s.id, s.v)"));

    /// <summary>Nothing past the cap is evaluated, so its errors never raise.</summary>
    [TestMethod]
    [DataRow("create table x (id int primary key, v int); insert x values (1, 10), (2, 20); create table y (id int, v int); insert y values (1, 1), (2, 2), (2, 3); merge top (1) x using y on x.id = y.id when matched then update set v = y.v")]
    [DataRow("create table x (id int primary key, v int); create table y (id int, v varchar(10)); insert y values (1, '1'), (2, 'x'); merge top (1) x using y on x.id = y.id when not matched then insert values (y.id, y.v)")]
    public void ErrorsPastTheCapDoNotRaise(string batch)
        => AreEqual(1, new Simulation().ExecuteScalar($"{batch}; select count(*) from x where v = 1"));

    [TestMethod]
    public void OutputListsTheTakenActions()
    {
        using var reader = new Simulation().ExecuteReader($"""
            {Seed}
            insert s values (1, 100), (2, 200), (5, 500);
            merge top (2) t using s on t.id = s.id
            when matched then update set v = s.v
            when not matched then insert values (s.id, s.v)
            output $action, inserted.id;
            """);
        AreEqual("UPDATE:1,UPDATE:2", string.Join(",", reader.EnumerateRecords().Select(r => $"{r.GetString(0)}:{r.GetInt32(1)}")));
    }

    [TestMethod]
    public void ViewTarget()
        => AreEqual(2, new Simulation().ExecuteBatchesScalar(
            "create table t (id int primary key, v int); create table s (id int, v int); insert t values (1, 10), (2, 20); insert s values (1, 100), (2, 200), (3, 300)",
            "create view vt as select id, v from t",
            "merge top (2) vt using s on vt.id = s.id when matched then update set v = s.v when not matched then insert values (s.id, s.v); select count(*) from t where v >= 100"));

    [TestMethod]
    public void AfterTriggerSeesTheCappedRows()
        => AreEqual(2, new Simulation().ExecuteBatchesScalar(
            "create table t (id int primary key, v int); create table s (id int, v int); create table log (n int); insert s values (1, 100), (2, 200), (3, 300)",
            "create trigger tr on t after insert as insert log select count(*) from inserted",
            "merge top (2) t using s on t.id = s.id when not matched then insert values (s.id, s.v); select n from log"));

    [TestMethod]
    public void ComposesWithSetRowCount()
        => AreEqual(1, new Simulation().ExecuteScalar($"""
            {Seed}
            insert s values (1, 100), (2, 200), (3, 300);
            set rowcount 1;
            merge top (2) t using s on t.id = s.id when matched then update set v = s.v;
            set rowcount 0;
            select count(*) from t where v >= 100
            """));

    [TestMethod]
    [DataRow("declare @n int = -1; merge top (@n) t using s on t.id = s.id when not matched then insert values (s.id, s.v);", 127)]
    [DataRow("merge top (-1) t using s on t.id = s.id when not matched then insert values (s.id, s.v);", 127)]
    [DataRow("declare @n int = null; merge top (@n) t using s on t.id = s.id when not matched then insert values (s.id, s.v);", 1014)]
    [DataRow("merge top (1.7) t using s on t.id = s.id when not matched then insert values (s.id, s.v);", 1060)]
    [DataRow("merge top ('2') t using s on t.id = s.id when not matched then insert values (s.id, s.v);", 1060)]
    [DataRow("merge top (101) percent t using s on t.id = s.id when not matched then insert values (s.id, s.v);", 1031)]
    [DataRow("merge top 1 t using s on t.id = s.id when not matched then insert values (s.id, s.v);", 102)]
    public void InvalidCount(string merge, int error)
        => _ = new Simulation().AssertSqlError($"{Seed}\ninsert s values (1, 1);\n{merge}", error);

    /// <summary>A variable's bad count reports at the statement's first line, a written one's while compiling with no Msg 3621.</summary>
    [TestMethod]
    public void RuntimeCountErrorReportsTheStatementLine()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int primary key, v int);
            declare @n int = -1;
            update top (@n) t
            set v = 2;
            """, 127);
        AreEqual(3, ex.Errors[0].LineNumber);
    }

    [TestMethod]
    public void WrittenNegativeCountHasNoStatementTerminated()
        => AreEqual(1, new Simulation().AssertSqlError("create table t (id int primary key, v int);\ndelete top (-1) t;", 127).Errors.Count);
}
