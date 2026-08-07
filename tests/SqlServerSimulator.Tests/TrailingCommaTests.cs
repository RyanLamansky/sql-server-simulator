using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Real tolerates a single trailing comma before the closing paren of a
/// <c>CREATE TABLE</c> element list — and nowhere else. Notably not in
/// <c>DECLARE @t TABLE</c> or <c>CREATE TYPE … AS TABLE</c>, which share the
/// simulator's element-list parser and are the shapes a "column-definition
/// lists are lenient" reading would wrongly widen to. Every case
/// probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class TrailingCommaTests
{
    [TestMethod]
    [DataRow("create table t (A int not null, B int not null,)")]
    [DataRow("create table t (A int not null,)")]                                  // single column
    [DataRow("create table #t (A int not null, B int not null,)")]                 // temp table
    [DataRow("create table t (A int not null, constraint pk_t primary key (A),)")] // after a constraint
    public void CreateTable_ToleratesOneTrailingComma(string create)
    {
        // Reading the table back proves it exists whether it landed in the
        // database or the session's temp-table namespace, which sys.objects
        // in the current database would not show.
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand(create).ExecuteNonQuery();
        var name = create.Contains("#t", StringComparison.Ordinal) ? "#t" : "t";
        AreEqual(0, connection.CreateCommand($"select count(*) from {name}").ExecuteScalar());
    }

    [TestMethod]
    public void CreateTable_TheColumnsAreStillReadCorrectly()
    {
        // The comma is dropped, not treated as an unnamed column.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (A int not null, B int null,)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t')"));
        _ = sim.ExecuteNonQuery("insert t (A, B) values (1, 2)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    [DataRow("create table t (A int not null, B int not null,,)")]     // two commas
    [DataRow("create table t (,A int not null)")]                      // leading comma
    [DataRow("create table t (A int not null, constraint pk_t primary key (A,))")]  // constraint's own list
    public void CreateTable_RefusesEverythingElse(string create)
        => _ = new Simulation().AssertSqlError(create, 102);

    [TestMethod]
    public void DeclareTableVariable_RefusesATrailingComma()
        // Shares the element-list parser with CREATE TABLE and must not
        // inherit the leniency.
        => _ = new Simulation().AssertSqlError("declare @t table (a int not null,); select 1", 102);

    [TestMethod]
    public void CreateTypeAsTable_RefusesATrailingComma()
        => _ = new Simulation().AssertSqlError("create type dbo.tt as table (a int not null,)", 102);

    [TestMethod]
    [DataRow("insert t (A, B,) values (1, 2)")]
    [DataRow("insert t (A, B) values (1, 2,)")]
    [DataRow("create index ix_t on t (A,)")]
    [DataRow("alter table t add C int null,")]
    public void OtherListsRefuseATrailingComma(string statement)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (A int not null, B int null)");
        _ = sim.AssertSqlError(statement, 102);
    }
}
