using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>MERGE</c> over a target whose non-persisted computed column fails for
/// some row (<c>c AS 10 / (price - 1)</c> at <c>price = 1</c>): the column is
/// evaluated only where the statement reads it for a row, so the failure is
/// Msg 8134 exactly when the <c>ON</c>, a <c>WHEN</c> condition or an action
/// reads it for that row, with the other <c>ON</c> conjuncts filtering first
/// (probed 2026-10-01 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class MergeComputedColumnTests
{
    private const string Setup = """
        create table p (id int primary key, price int, c as 10 / (price - 1));
        insert p (id, price) values (1, 1), (2, 5);
        create table h (id int, price int, c as 10 / (price - 1));
        insert h (id, price) values (1, 1), (2, 5);
        create table log (m varchar(20));
        """;

    private static Simulation Open(string more = "")
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup + more);
        return simulation;
    }

    private static void AssertDivideByZero(Simulation simulation, string merge)
        => _ = simulation.AssertSqlError(merge, 8134);

    [TestMethod]
    public void AnOnReadingIt_RaisesAndWritesNothing()
    {
        var simulation = Open();
        _ = simulation.ExecuteNonQuery("create trigger tp on p after insert as insert log values ('fired')");
        AssertDivideByZero(simulation, "merge p using (values (100)) s(x) on p.c = 1 when not matched then insert (id, price) values (s.x, 3);");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from p"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    public void ClientOutput_ReturnsNoRowsAheadOfTheError()
    {
        var simulation = Open();
        AssertDivideByZero(simulation, "merge h using (values (2), (1)) s(x) on h.id = s.x and h.c = 1 when not matched then insert (id, price) values (s.x + 10, 3) output inserted.id, $action;");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from h"));
    }

    [TestMethod]
    public void AConstantFalseOn_ReadsNoTargetRow()
    {
        var simulation = Open();
        _ = simulation.ExecuteNonQuery("""
            merge p using (values (101)) s(x) on 1 = 0 when not matched then insert (id, price) values (s.x, 3);
            merge p using (values (102)) s(x) on 1 = 0 and p.c = 1 when not matched then insert (id, price) values (s.x, 3);
            merge p using (values (103)) s(x) on p.c = 1 and 1 = 0 when not matched then insert (id, price) values (s.x, 3);
            merge p using (values (104)) s(x) on 1 = 0 when not matched by source and p.id > 1000 then delete;
            """);
        AreEqual(5, simulation.ExecuteScalar("select count(*) from p"));
    }

    [TestMethod]
    public void AnEmptySource_PairsNoTargetRow()
    {
        var simulation = Open();
        _ = simulation.ExecuteNonQuery("merge p using (select 1 where 1 = 0) s(x) on p.c = s.x when not matched then insert (id, price) values (s.x, 3);");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from p"));
    }

    [TestMethod]
    public void ASeek_EvaluatesOnlyTheRowsItFinds()
    {
        var simulation = Open();
        _ = simulation.ExecuteNonQuery("""
            merge p using (values (2)) s(x) on p.id = s.x and p.c = 1 when not matched then insert (id, price) values (s.x + 200, 3);
            merge p using (values (2)) s(x) on p.id = s.x when matched and p.c = 1 then delete;
            """);
        AreEqual(3, simulation.ExecuteScalar("select count(*) from p"));
        AssertDivideByZero(simulation, "merge p using (values (1)) s(x) on p.id = s.x and p.c = 1 when not matched then insert (id, price) values (s.x + 300, 3);");
        AssertDivideByZero(simulation, "merge p using (values (1)) s(x) on p.id = s.x when matched and p.c = 1 then delete;");
    }

    [TestMethod]
    public void AnUnindexedTarget_HashKeyOnItRaises_AnotherKeyFiltersFirst()
    {
        var simulation = Open();
        AssertDivideByZero(simulation, "merge h using (values (5)) s(x) on h.c = s.x when not matched then insert (id, price) values (9, 3);");
        _ = simulation.ExecuteNonQuery("merge h using (values (2)) s(x) on h.id = s.x and h.c = 1 when not matched then insert (id, price) values (9, 3);");
        AreEqual(3, simulation.ExecuteScalar("select count(*) from h"));
    }

    [TestMethod]
    public void OtherConjuncts_FilterFirstWhereverWritten()
    {
        var simulation = Open();
        _ = simulation.ExecuteNonQuery("""
            merge p using (values (106)) s(x) on p.c = 1 and s.x = 0 when not matched then insert (id, price) values (s.x, 3);
            merge p using (values (107)) s(x) on p.c = 1 and p.price = 5 when not matched then insert (id, price) values (s.x, 3);
            merge h using (values (108)) s(x) on h.c = 1 and s.x = 0 when not matched then insert (id, price) values (s.x, 3);
            """);
        AreEqual(4, simulation.ExecuteScalar("select count(*) from p"));
    }

    [TestMethod]
    public void AnOr_EvaluatesInWrittenOrder()
    {
        var simulation = Open();
        _ = simulation.ExecuteNonQuery("merge p using (values (1)) s(x) on p.id = s.x or p.c = 1 when matched then update set price = 7;");
        AreEqual(7, simulation.ExecuteScalar("select price from p where id = 1"));
        _ = simulation.ExecuteNonQuery("update p set price = 1 where id = 1");
        AssertDivideByZero(simulation, "merge p using (values (1)) s(x) on p.c = 1 or p.id = s.x when matched then update set price = 7;");
    }

    [TestMethod]
    public void NotMatchedBySource_ConditionAndActionReadIt()
    {
        var simulation = Open();
        AssertDivideByZero(simulation, "merge p using (values (102)) s(x) on 1 = 0 when not matched by source and p.c = 1 then delete;");
        AssertDivideByZero(simulation, "merge p using (values (2)) s(x) on p.id = s.x when matched then update set price = 9 when not matched by source then update set price = p.c;");
        AssertDivideByZero(simulation, "merge p using (values (5)) s(x) on p.c = s.x when not matched by source then delete;");
        AreEqual(5, simulation.ExecuteScalar("select price from p where id = 2"));
    }
}
