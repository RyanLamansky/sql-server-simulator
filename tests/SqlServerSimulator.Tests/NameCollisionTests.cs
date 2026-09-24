using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What real reports when a CREATE takes a name already in use, and the
/// Msg 1750 it sends after every constraint that can't be created (probed
/// 2026-09-24 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class NameCollisionTests
{
    private static (int Number, byte State)[] ErrorsOf(SimulatedSqlException ex) =>
        [.. ex.Errors.Cast<SimulatedError>().Select(error => (error.Number, error.State))];

    private static SimulatedSqlException Fails(Simulation simulation, string commandText) =>
        Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery(commandText));

    [TestMethod]
    [DataRow("create table x (a int)", 6)]
    [DataRow("create view x as select 1 a", 3)]
    [DataRow("create procedure x as select 1", 3)]
    [DataRow("create function x() returns int as begin return 1 end", 3)]
    [DataRow("create trigger x on t after insert as select 1", 2)]
    [DataRow("create sequence x", 8)]
    [DataRow("create synonym x for t", 8)]
    public void ObjectNameTaken_ReportsTheStateOfTheKindBeingCreated(string create, int state)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int)", "create table x (a int)");
        CollectionAssert.AreEqual(new[] { (2714, (byte)state) }, ErrorsOf(Fails(simulation, create)));
    }

    [TestMethod]
    [DataRow("create table dbo.x (a int)", "x")]
    [DataRow("create view dbo.x as select 1 a", "x")]
    [DataRow("create sequence dbo.x", "dbo.x")]
    [DataRow("create synonym dbo.x for t", "dbo.x")]
    public void ObjectNameTaken_NamesTheLeaf_ButASequenceOrSynonymAsWritten(string create, string named)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int)", "create table x (a int)");
        AreEqual($"There is already an object named '{named}' in the database.", Fails(simulation, create).Errors[0].Message);
    }

    [TestMethod]
    public void SchemaNameTaken_IsFollowedByMsg2759_WhichACatchReads()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create schema s");
        CollectionAssert.AreEqual(new[] { (2714, (byte)6), (2759, (byte)0) }, ErrorsOf(Fails(simulation, "create schema s")));
        AreEqual(2759, simulation.ExecuteScalar("begin try exec ('create schema s') end try begin catch select error_number() end catch"));
    }

    [TestMethod]
    [DataRow("alter table t add constraint k check (a > 0)")]
    [DataRow("alter table t add constraint k default 1 for a")]
    [DataRow("alter table t add constraint k unique (a)")]
    [DataRow("create table u (a int constraint k check (a > 0))")]
    [DataRow("create table u (a int, constraint k primary key (a))")]
    [DataRow("create table u (a int constraint k default 1)")]
    [DataRow("alter table t add constraint v check (a > 0)")]
    [DataRow("create table u (a int constraint u check (a > 0))")]
    public void ConstraintNameTaken_Raises2714State5ThenMsg1750(string statement)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int not null, constraint k check (a < 99))", "create view v as select 1 a");
        CollectionAssert.AreEqual(new[] { (2714, (byte)5), (1750, (byte)1) }, ErrorsOf(Fails(simulation, statement)));
    }

    [TestMethod]
    public void ConstraintNames_AreUniquePerSchema()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int constraint k check (a > 0))", "create schema s");
        _ = simulation.ExecuteNonQuery("create table s.t (a int constraint k check (a > 0))");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.check_constraints where name = 'k' and schema_id = schema_id('s')"));
    }

    [TestMethod]
    public void ViewNamedAfterAConstraint_Raises2714()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int constraint k check (a > 0))");
        CollectionAssert.AreEqual(new[] { (2714, (byte)3) }, ErrorsOf(Fails(simulation, "create view k as select 1 a")));
    }

    [TestMethod]
    public void OneCreateTableNamingTwoConstraintsAlike_RaisesMsg8168()
        => new Simulation().AssertSqlError(
            "create table u (a int constraint k check (a > 0), b int constraint k check (b > 0))",
            8168,
            "Cannot create, drop, enable, or disable more than one constraint, column, index, or trigger named 'k' in this context. Duplicate names are not allowed.");

    [TestMethod]
    public void CatchReadsTheMsg1750ThatFollowsAConstraintFailure()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int, constraint k check (a > 0))");
        AreEqual(1750, simulation.ExecuteScalar("begin try exec ('alter table t add constraint k check (a < 9)') end try begin catch select error_number() end catch"));
    }

    [TestMethod]
    [DataRow("alter table t add constraint k2 primary key (a)", 1779, 0, 0)]
    [DataRow("alter table d add constraint pk_d primary key (a)", 1505, 1, 1)]
    [DataRow("alter table t alter column h add rowguidcol", 4925, 0, 0)]
    public void ConstraintFailure_IsFollowedByMsg1750(string statement, int number, int state, int trailerState)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int not null primary key, g uniqueidentifier rowguidcol, h uniqueidentifier)",
            "create table d (a int not null); insert d values (1), (1)");
        var errors = ErrorsOf(Fails(simulation, statement));
        CollectionAssert.AreEqual(new[] { (number, (byte)state), (1750, (byte)trailerState) }, errors.Take(2).ToArray());
    }

    [TestMethod]
    public void TypeNameTaken_NamesTheTypeAsWritten()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create type ty from int");
        simulation.AssertSqlError("create type ty from int", 219, "The type 'ty' already exists, or you do not have permission to create it.");
    }

    [TestMethod]
    [DataRow("create index ix on t (a)", "t")]
    [DataRow("create index ix on dbo.t (a)", "dbo.t")]
    public void IndexNameTaken_NamesTheTableAsWritten(string create, string written)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int)", "create index ix on t (a)");
        simulation.AssertSqlError(create, 1913, $"The operation failed because an index or statistics with name 'ix' already exists on table '{written}'.");
    }
}
