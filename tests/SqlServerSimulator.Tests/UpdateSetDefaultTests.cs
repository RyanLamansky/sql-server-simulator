using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET col = DEFAULT</c> in an <c>UPDATE</c> and a <c>MERGE</c>'s update
/// action. Every expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class UpdateSetDefaultTests
{
    [TestMethod]
    public void TheColumn_TakesItsDefaultOrNull()
        => AreEqual("1||9|3", new Simulation().ExecuteScalar("""
            create table u (a int default 1, b int, c int not null default 9, n int not null);
            insert u values (5, 1, 2, 3);
            update u set a = default, b = default, c = default;
            select concat_ws('|', a, isnull(cast(b as varchar), ''), c, n) from u
            """));

    [TestMethod]
    public void ANotNullColumnWithoutADefault_IsMsg515()
        => _ = new Simulation().AssertSqlError("create table u (a int, n int not null); insert u values (1, 1); update u set n = default", 515);

    [TestMethod]
    [DataRow("create table u (id int identity, a int); update u set id = default", 8102)]
    [DataRow("create table u (a int, c as a + 1); update u set c = default", 271)]
    [DataRow("create table u (a int, r rowversion); update u set r = default", 272)]
    [DataRow("create table u (a int default 1); update u set a += default", 10708)]
    [DataRow("create table u (a varchar(2) default 'abc'); insert u values ('x'); update u set a = default", 2628)]
    public void TheShapesRealRefuses_AreRefused(string batch, int number)
        => _ = new Simulation().AssertSqlError(batch, number);

    [TestMethod]
    public void OutputAndABoundDefault_SeeTheDefault()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create default d as 7", "create table u (a int, b int); exec sp_bindefault 'd', 'u.a'; insert u values (5, 1)");
        AreEqual(7, sim.ExecuteScalar("update u set a = default output inserted.a"));
    }

    [TestMethod]
    public void AViewAndATableVariable_TakeIt()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table u (a int default 1, b int); insert u values (5, 1)", "create view v as select a, b from u");
        AreEqual(1, sim.ExecuteScalar("update v set a = default; select a from u"));
        AreEqual(4, sim.ExecuteScalar("declare @t table (a int default 4, b int); insert @t values (1, 1); update @t set a = default; select a from @t"));
    }

    [TestMethod]
    public void AMergeUpdate_TakesIt()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            create table u (a int default 7, b int); insert u values (5, 1);
            merge u using (select 1 x) s on 1 = 1 when matched then update set a = default;
            select a from u
            """));
}
