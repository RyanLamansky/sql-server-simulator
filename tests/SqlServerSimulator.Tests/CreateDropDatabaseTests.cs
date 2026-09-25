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

    [TestMethod]
    public void ModifyName_RenamesInPlace()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database foo; create table foo.dbo.t (id int); insert foo.dbo.t values (1)");
        var id = simulation.ExecuteScalar("select db_id('foo')");
        _ = simulation.ExecuteNonQuery("alter database foo modify name = [bar baz]");
        AreEqual(id, simulation.ExecuteScalar("select db_id('bar baz')"));
        IsTrue(simulation.ExecuteScalar("select db_id('foo')") is DBNull);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from [bar baz].dbo.t"));
    }

    [TestMethod]
    public void ModifyName_OfTheSessionsDatabase_AnnouncesTheNewContext()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        var log = new List<string>();
        connection.InfoMessage += (_, e) => log.Add($"{e.Errors[0].Number}: {e.Message}");
        using var command = connection.CreateCommand();
        command.CommandText = "create database foo; use foo; alter database foo modify name = Foo2; select db_name()";
        AreEqual("Foo2", command.ExecuteScalar());
        CollectionAssert.AreEqual(
            new[] { "5701: Changed database context to 'foo'.", "5021: The database name 'Foo2' has been set.", "5701: Changed database context to 'Foo2'." },
            log);
    }

    [TestMethod]
    [DataRow("alter database nosuchdb modify name = x", 911, "Database 'nosuchdb' does not exist. Make sure that the name is entered correctly.")]
    [DataRow("alter database foo modify name = master", 1801, "Database 'master' already exists. Choose a different database name.")]
    [DataRow("alter database master modify name = m2", 5016, "Cannot change the name of the system database master.")]
    [DataRow("alter database foo modify name = 'quoted'", 102, "Incorrect syntax near 'quoted'.")]
    [DataRow("alter database foo modify name = foo2, file = (name = x)", 102, "Incorrect syntax near ','.")]
    [DataRow("use master; alter database current modify name = x", 12104, "ALTER DATABASE CURRENT failed because 'master' is a system database. System databases cannot be altered by using the CURRENT keyword. Use the database name to alter a system database.")]
    [DataRow("use tempdb; alter database current set ansi_nulls on", 12104, "ALTER DATABASE CURRENT failed because 'tempdb' is a system database. System databases cannot be altered by using the CURRENT keyword. Use the database name to alter a system database.")]
    public void ModifyName_Refusals(string statement, int number, string message)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database foo");
        simulation.AssertSqlError(statement, number, message);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'foo'"));
    }
}
