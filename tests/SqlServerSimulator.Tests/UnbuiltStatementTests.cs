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
    [DataRow("backup certificate c to file = 'c.cer'", "BACKUP CERTIFICATE")]
    [DataRow("create column master key cmk with (key_store_provider_name = 'MSSQL_CERTIFICATE_STORE', key_path = 'x')", "CREATE COLUMN MASTER KEY")]
    [DataRow("create database encryption key with algorithm = aes_256 encryption by server certificate c", "CREATE DATABASE ENCRYPTION KEY")]
    [DataRow("create workload group w", "CREATE WORKLOAD GROUP")]
    [DataRow("alter service master key regenerate", "ALTER SERVICE MASTER KEY")]
    [DataRow("create external data source e with (location = 'sqlserver://x')", "CREATE EXTERNAL DATA SOURCE")]
    [DataRow("alter resource governor reconfigure", "ALTER RESOURCE GOVERNOR")]
    [DataRow("create selective xml index sx on t (x) for (p = '/a')", "CREATE SELECTIVE XML INDEX")]
    [DataRow("backup service master key to file = 'x' encryption by password = 'x'", "BACKUP SERVICE MASTER KEY")]
    [DataRow("restore master key from file = 'x' decryption by password = 'x' encryption by password = 'x'", "RESTORE MASTER KEY")]
    [DataRow("backup symmetric key k to file = 'k.key' encryption by password = 'Xx!12345678'", "BACKUP SYMMETRIC KEY")]
    [DataRow("restore symmetric key k from file = 'k.key' decryption by password = 'x' encryption by password = 'x'", "RESTORE SYMMETRIC KEY")]
    [DataRow("add signature to p by certificate c", "ADD SIGNATURE")]
    public void UnbuiltStatement_RaisesNotSupported(string sql, string statement)
    {
        var ex = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(sql));
        StartsWith(statement + " isn't modeled by the simulator (", ex.Message);
    }

    [TestMethod]
    public void CreateDatabaseNeighbours_CreateNoDatabase()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database scoped credential dc with identity = 'x'");
        _ = simulation.AssertSqlError("create database audit specification s for server audit a", 33073);
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

    [TestMethod]
    [DataRow("drop external data source zzn", 15151, 1)]
    [DataRow("drop external table zzn", 15151, 1)]
    [DataRow("drop external library zzn", 15151, 2)]
    [DataRow("drop resource pool zzn", 15151, 2)]
    [DataRow("drop workload group zzn", 15151, 4)]
    [DataRow("drop column master key zzn", 33293, 7)]
    [DataRow("drop endpoint zzn", 1088, 103)]
    [DataRow("drop availability group zzn", 35208, 242)]
    [DataRow("drop database encryption key", 33104, 1)]
    [DataRow("drop search property list zzn", 30025, 13)]
    [DataRow("drop fulltext stoplist zzn", 30023, 8)]
    [DataRow("drop external data source if exists zzn", 156, 1)]
    [DataRow("drop fulltext stoplist if exists zzn", 156, 1)]
    public void ADropOfAKindNothingCreates_IsRealsMissingObject(string statement, int number, int state)
        => AreEqual((byte)state, new Simulation().AssertSqlError(statement, number).State);

    [TestMethod]
    public void ADropOfAShippedName_StaysUnbuilt()
        => _ = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("drop resource pool [default]"));
}
