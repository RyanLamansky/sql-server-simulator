using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public sealed class CreateDropDatabaseTests
{
    [TestMethod]
    public void CreateDatabase_Use_RoundTrips()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create database foo;
            use foo;
            create table t (id int);
            insert t values (1);
            select count(*) from t
            """));

    [TestMethod]
    public void CreateDatabase_AllocatesNextFreeId()
    {
        // The first connection lazily seeds `simulated` at database_id 5, so a
        // freshly created user database takes the next free id, 6.
        AreEqual((short)6, new Simulation().ExecuteScalar("create database foo; select db_id('foo')"));
        AreEqual("foo", new Simulation().ExecuteScalar("create database foo; select db_name(6)"));
    }

    [TestMethod]
    public void CreateDatabase_Duplicate_Raises1801()
        => new Simulation().AssertSqlError(
            "create database foo; create database foo",
            1801,
            "Database 'foo' already exists. Choose a different database name.");

    [TestMethod]
    public void DropDatabase_RemovesFromCatalog()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar(
            "create database foo; drop database foo; select db_id('foo')"));

    [TestMethod]
    public void DropDatabase_IfExists_MissingIsNoOp()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar(
            "drop database if exists nope; select db_id('nope')"));

    [TestMethod]
    public void DropDatabase_Missing_Raises3701()
    {
        var ex = new Simulation().AssertSqlError("drop database nope", 3701);
        Contains("Cannot drop the database 'nope'", ex.Message);
    }

    [TestMethod]
    public void DropDatabase_System_Raises3708()
        => new Simulation().AssertSqlError(
            "drop database master",
            3708,
            "Cannot drop the database 'master' because it is a system database.");

    [TestMethod]
    public void DropDatabase_InUse_Raises3702()
        => new Simulation().AssertSqlError(
            "create database foo; use foo; drop database foo",
            3702,
            "Cannot drop database \"foo\" because it is currently in use.");

    [TestMethod]
    public void DropDatabase_FreesIdForReuse()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand(
            "create database a; create database b; drop database a; create database c")
            .ExecuteNonQuery();
        // simulated=5, a=6, b=7; dropping a frees 6, so c reclaims the smallest
        // free id (6) rather than extending to 8.
        AreEqual((short)6, connection.CreateCommand("select db_id('c')").ExecuteScalar());
        AreEqual((short)7, connection.CreateCommand("select db_id('b')").ExecuteScalar());
    }

    [TestMethod]
    public void CreateDatabase_Collate_SetsCollation()
        => AreEqual("Latin1_General_CI_AS", new Simulation().ExecuteScalar(
            "create database foo collate Latin1_General_CI_AS; select databasepropertyex('foo', 'Collation')"));

    [TestMethod]
    public void CreateDatabase_FileAndOptionClauses_Discarded()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create database foo on (name = 'x', filename = 'y') log on (name = 'xl', filename = 'yl');
            use foo;
            create table t (id int);
            insert t values (1);
            select count(*) from t
            """));
}
