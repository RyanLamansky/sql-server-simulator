using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Statements real SQL Server runs that the simulator hasn't built raise
/// <see cref="NotSupportedException"/> naming the statement, where they were a
/// syntax error real never raises — or, for the <c>CREATE DATABASE …</c>
/// neighbours, a database created under the second word's name.
/// </summary>
[TestClass]
public sealed class UnbuiltStatementTests
{
    [TestMethod]
    [DataRow("create asymmetric key a with algorithm = rsa_2048", "CREATE ASYMMETRIC KEY")]
    [DataRow("alter certificate c remove private key", "ALTER CERTIFICATE")]
    [DataRow("backup certificate c to file = 'c.cer'", "BACKUP CERTIFICATE")]
    [DataRow("create column master key cmk with (key_store_provider_name = 'MSSQL_CERTIFICATE_STORE', key_path = 'x')", "CREATE COLUMN MASTER KEY")]
    [DataRow("create database encryption key with algorithm = aes_256 encryption by server certificate c", "CREATE DATABASE ENCRYPTION KEY")]
    [DataRow("alter database scoped credential dc with identity = 'y'", "ALTER DATABASE SCOPED CREDENTIAL")]
    [DataRow("create server audit specification s for server audit a", "CREATE SERVER AUDIT SPECIFICATION")]
    [DataRow("create queue q", "CREATE QUEUE")]
    [DataRow("alter service master key regenerate", "ALTER SERVICE MASTER KEY")]
    [DataRow("create event session s on server add event sqlserver.sql_batch_completed", "CREATE EVENT SESSION")]
    [DataRow("create external data source e with (location = 'sqlserver://x')", "CREATE EXTERNAL DATA SOURCE")]
    [DataRow("alter resource governor reconfigure", "ALTER RESOURCE GOVERNOR")]
    [DataRow("create selective xml index sx on t (x) for (p = '/a')", "CREATE SELECTIVE XML INDEX")]
    [DataRow("backup service master key to file = 'x' encryption by password = 'x'", "BACKUP SERVICE MASTER KEY")]
    [DataRow("restore master key from file = 'x' decryption by password = 'x' encryption by password = 'x'", "RESTORE MASTER KEY")]
    [DataRow("add signature to p by certificate c", "ADD SIGNATURE")]
    [DataRow("declare @h uniqueidentifier; begin dialog conversation @h from service s to service 's'", "BEGIN DIALOG")]
    [DataRow("declare @h uniqueidentifier; send on conversation @h (0x00)", "SEND ON CONVERSATION")]
    [DataRow("receive top (1) * from q", "RECEIVE")]
    [DataRow("declare @h uniqueidentifier; end conversation @h", "END CONVERSATION")]
    [DataRow("waitfor (receive top (1) * from q), timeout 10", "WAITFOR (RECEIVE")]
    public void UnbuiltStatement_RaisesNotSupported(string sql, string statement)
    {
        var ex = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(sql));
        StartsWith(statement + " isn't modeled by the simulator (", ex.Message);
    }

    [TestMethod]
    public void CreateDatabaseNeighbours_CreateNoDatabase()
    {
        var simulation = new Simulation();
        _ = Throws<NotSupportedException>(() => simulation.ExecuteNonQuery("create database scoped credential dc with identity = 'x'"));
        _ = Throws<NotSupportedException>(() => simulation.ExecuteNonQuery("create database audit specification s for server audit a"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.databases where name in ('SCOPED', 'AUDIT')"));
    }

    [TestMethod]
    public void ACursorNamedLikeAKeyWord_StillCloses()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            declare master cursor for select 1;
            open master;
            close master;
            deallocate master;
            select 0
            """));

    /// <summary>
    /// A workload classifier is a Synapse surface, which real refuses as the
    /// batch compiles — in an untaken branch too, with nothing ahead of it run
    /// (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void WorkloadClassifier_RaisesMsg22487WhileCompiling()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError(
            "create table t (a int); if 1 = 0 create workload classifier c with (workload_group = 'g', membername = 'x')",
            22487, "CREATE WORKLOAD CLASSIFIER is allowed only when connected to Synapse frontend.");
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('t')"));
    }
}
