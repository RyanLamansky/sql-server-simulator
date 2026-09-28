using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A FOREIGN KEY definition error ends the batch and rolls the transaction
/// back, from <c>CREATE TABLE</c> as from <c>ALTER TABLE</c>; its column pairs
/// must agree in type, length and collation; and a temporary table's is
/// skipped with a notice (probed 2026-09-28 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ForeignKeyDefinitionErrorTests
{
    private const string Parents = "create table p (x int primary key, y int); create table q (x int); create table p2 (x varchar(5) primary key)";

    [TestMethod]
    [DataRow("references nope(x)", 1767)]
    [DataRow("references p(y)", 1776)]
    [DataRow("references q", 1773)]
    [DataRow("references p(zz)", 1770)]
    [DataRow("references p2(x)", 1778)]
    public void CreateTable_EndsTheBatch_AndRollsBack(string reference, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Parents);
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand($"begin tran; create table t (a int {reference}); select 'not reached'").ExecuteNonQuery());
        AreEqual((number, 1750, 2), (ex.Errors[0].Number, ex.Errors[1].Number, ex.Errors.Count));
        AreEqual("0|", connection.CreateCommand("select concat(@@trancount, '|', object_id('t'))").ExecuteScalar());
    }

    [TestMethod]
    public void Caught_DoomsTheTransaction()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Parents);
        using var connection = sim.CreateOpenConnection();
        AreEqual("1750|-1", connection.CreateCommand(
            "begin tran; begin try create table t (a int references p(zz)); end try begin catch select cast(error_number() as varchar) + '|' + cast(xact_state() as varchar); end catch; rollback").ExecuteScalar());
    }

    [TestMethod]
    public void AlterTable_ReportsTheInvalidColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Parents + "; create table t (a int)");
        AreEqual("Foreign key 'fk' references invalid column 'zz' in referenced table 'p'.",
            sim.AssertSqlError("alter table t add constraint fk foreign key (a) references p(zz)", 1770).Errors[0].Message);
    }

    [TestMethod]
    [DataRow("varchar(5)", "varchar(10)", 1753)]
    [DataRow("decimal(10,2)", "decimal(10,3)", 1753)]
    [DataRow("int", "bigint", 1778)]
    [DataRow("numeric(10,2)", "decimal(10,2)", 1778)]
    [DataRow("char(5)", "varchar(5)", 1778)]
    [DataRow("varchar(5) collate Latin1_General_BIN", "varchar(5)", 1757)]
    public void ColumnPairs_MustAgree(string parentType, string childType, int number)
    {
        var ex = new Simulation().AssertSqlError($"create table p (x {parentType} primary key); create table t (a {childType} references p(x))", number);
        StartsWith("Column 'p.x' is ", ex.Errors[0].Message);
    }

    [TestMethod]
    public void AnAliasType_MatchesItsBase()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type ty from int", "create table p (x ty primary key); create table t (a int references p(x))");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.foreign_keys"));
    }

    [TestMethod]
    public void TemporaryTable_SkipsTheConstraintWithANotice()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SimulatedError>().Select(error => $"{error.Number}: {error.Message}"));
        AreEqual(1, connection.CreateCommand("create table p (x int primary key); create table #t (a int references p(x), b int references nope(z)); insert #t values (5, 1); select count(*) from #t").ExecuteScalar());
        HasCount(2, messages);
        AreEqual("1756: Skipping FOREIGN KEY constraint '#t' definition for temporary table. FOREIGN KEY constraints are not enforced on local or global temporary tables.", messages[0]);
    }
}
