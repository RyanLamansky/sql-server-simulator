using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>DEFAULT</c> keyword as a value element inside an
/// <c>INSERT … VALUES (…)</c> tuple. Each such cell resolves to the target
/// column's DEFAULT constraint value, or NULL when the column has no default —
/// mirroring SQL Server (probe-confirmed against SQL Server 2025). Distinct
/// from <c>INSERT … DEFAULT VALUES</c> (all columns defaulted), covered
/// elsewhere. Django 5.0+'s <c>db_default</c> emits this shape.
/// </summary>
[TestClass]
public sealed class InsertDefaultValuesTests
{
    [TestMethod]
    public void SingleColumn_ResolvesConstantDefault()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            create table t (a int default 7);
            insert into t (a) values (default);
            select a from t
            """));

    [TestMethod]
    public void NoDefaultNullableColumn_ResolvesToNull()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (a int null);
            insert into t (a) values (default);
            select count(*) from t where a is null
            """));

    [TestMethod]
    public void MixedTuple_EachCellResolvesPerColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int default 7, b int, c int default 9);
            insert into t (a, b, c) values (default, 5, default)
            """);
        AreEqual(7, sim.ExecuteScalar("select a from t"));
        AreEqual(5, sim.ExecuteScalar("select b from t"));
        AreEqual(9, sim.ExecuteScalar("select c from t"));
    }

    [TestMethod]
    public void MultiRow_DefaultResolvesPerRowAndColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int default 1, b int default 2);
            insert into t (a, b) values (default, 5), (6, default)
            """);
        AreEqual(5, sim.ExecuteScalar("select b from t where a = 1"));
        AreEqual(2, sim.ExecuteScalar("select b from t where a = 6"));
    }

    [TestMethod]
    public void ExplicitValueStillSuppressesDefault()
        => AreEqual(42, new Simulation().ExecuteScalar("""
            create table t (a int default 7);
            insert into t (a) values (42);
            select a from t
            """));

    // Probe-confirmed (SQL Server 2025): DEFAULT for an identity column raises
    // Msg 339 with IDENTITY_INSERT both OFF and ON — the explicit-identity gate
    // (Msg 544) never gets a look-in.
    [TestMethod]
    public void IdentityColumn_DefaultRaises339_IdentityInsertOff()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int identity(1, 1), x int);
            insert into t (id, x) values (default, 1)
            """, 339);
        Assert.Contains("DEFAULT or NULL are not allowed as explicit identity values", ex.Message);
    }

    [TestMethod]
    public void IdentityColumn_DefaultRaises339_IdentityInsertOn()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int identity(1, 1), x int);
            set identity_insert t on;
            insert into t (id, x) values (default, 1)
            """, 339);

    // The exact Django db_default multi-row shape, with the middle column
    // supplied as a parameter and every other cell DEFAULT.
    [TestMethod]
    public void DjangoDbDefaultShape_ResolvesDefaultsAroundParameter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table dbarticle (headline nvarchar(100) default 'default headline', pub_date date null, cost int default 42)");

        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand(
            "insert into dbarticle (headline, pub_date, cost) values (default, @p0, default), (default, default, default)",
            ("@p0", new DateTime(2024, 1, 15)));
        AreEqual(2, command.ExecuteNonQuery());

        AreEqual(2, sim.ExecuteScalar("select count(*) from dbarticle where headline = N'default headline' and cost = 42"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from dbarticle where pub_date is null"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from dbarticle where pub_date = '2024-01-15'"));
    }

    // Probe-confirmed: a NOT NULL column with no default receiving DEFAULT hits
    // the same NULL-into-non-nullable path an explicit NULL would (Msg 515).
    [TestMethod]
    public void NoDefaultNotNullColumn_Raises515()
        => _ = new Simulation().AssertSqlError("""
            create table t (a int not null);
            insert into t (a) values (default)
            """, 515);

    // DEFAULT is legal only inside INSERT … VALUES. A FROM-clause table-value
    // constructor must keep rejecting it (Msg 156) — probe-confirmed.
    [TestMethod]
    public void DefaultInFromClauseValuesConstructor_StillRejected()
        => _ = new Simulation().AssertSqlError("select * from (values (default)) v(x)", 156);
}
