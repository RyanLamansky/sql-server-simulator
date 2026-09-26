using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CHECK NOT FOR REPLICATION</c> and a foreign key's trailing <c>NOT FOR
/// REPLICATION</c>: still enforced, reported as such, and never trusted.
/// Probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class NotForReplicationConstraintTests
{
    private const string Setup = """
        create table par (id int primary key);
        create table t (a int check not for replication (a > 0),
            b int constraint fk_b foreign key references par(id) on delete cascade not for replication,
            c int, d int, constraint ck_tl check not for replication (c < 10));
        alter table t add constraint ck_alt check not for replication (d > 1);
        alter table t add constraint fk_alt foreign key (c) references par(id) not for replication;
        """;

    [TestMethod]
    public void TheCatalogs_ReportEveryFormNotForReplicationAndUntrusted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.check_constraints where is_not_for_replication = 1 and is_not_trusted = 1"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.foreign_keys where is_not_for_replication = 1 and is_not_trusted = 1"));
        AreEqual("1:1:1", sim.ExecuteScalar("select concat(objectproperty(object_id('ck_tl'), 'CnstIsNotRepl'), ':', objectproperty(object_id('fk_b'), 'CnstIsNotRepl'), ':', objectproperty(object_id('fk_b'), 'CnstIsNotTrusted'))"));
    }

    [TestMethod]
    public void RevalidatingWithCheck_LeavesItUntrusted()
        => AreEqual(1, new Simulation().ExecuteScalar(Setup + """
            alter table t with check check constraint ck_tl, fk_alt;
            select min(cast(is_not_trusted as int)) from (select is_not_trusted from sys.check_constraints where name = 'ck_tl' union all select is_not_trusted from sys.foreign_keys where name = 'fk_alt') x
            """));

    [TestMethod]
    [DataRow("insert t (a) values (-1)")]
    [DataRow("insert t (b) values (5)")]
    public void TheConstraint_IsStillEnforced(string statement)
        => _ = new Simulation().AssertSqlError(Setup + statement, 547);

    [TestMethod]
    [DataRow("alter table t add constraint ck check not for replication (a > 0)")]
    [DataRow("alter table t add constraint fk foreign key (b) references par(id) not for replication")]
    public void AddingOne_StillValidatesExistingRows(string statement)
        => _ = new Simulation().AssertSqlError("create table par (id int primary key); create table t (a int, b int); insert t values (-1, 5); " + statement, 547);

    [TestMethod]
    public void SpHelpConstraint_ReportsNotForReplication()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "exec sp_helpconstraint 't', 'nomsg'";
        using var reader = command.ExecuteReader();
        var statuses = new List<string>();
        while (reader.Read())
        {
            if (reader.GetString(1) != " ")
                statuses.Add(reader.GetString(5));
        }
        AreEqual(5, statuses.Count(status => status == "Not_For_Replication"));
    }

    /// <summary>The clause follows the referential actions, never precedes them.</summary>
    [TestMethod]
    public void BeforeTheReferentialActions_IsASyntaxError()
        => _ = new Simulation().AssertSqlError("create table par (id int primary key); create table t (b int references par(id) not for replication on delete cascade)", 156);

    [TestMethod]
    [DataRow("declare @t table (a int check not for replication (a > 0))")]
    [DataRow("create type tt as table (a int check not for replication (a > 0))")]
    [DataRow("declare @t table (a int, check not for replication (a > 0))")]
    public void ATableVariableOrTableType_RefusesIt(string statement)
        => new Simulation().AssertSqlError(statement, 102, "Incorrect syntax near 'not'.");
}
