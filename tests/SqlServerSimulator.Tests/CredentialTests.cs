using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CREATE</c> / <c>ALTER</c> / <c>DROP CREDENTIAL</c>, <c>sys.credentials</c>,
/// a login's <c>CREDENTIAL</c> option, and <c>SHUTDOWN</c>'s refusal (probed
/// 2026-10-06 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class CredentialTests
{
    [TestMethod]
    public void CreateAlterDrop_RoundTripsThroughTheCatalog()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create credential c1 with identity = 'idA', secret = 's'; create credential [c 2] with identity = N'idB'");
        AreEqual("65536:c1:idA,65537:c 2:idB", simulation.ExecuteScalar(
            "select string_agg(concat(credential_id, ':', name, ':', credential_identity), ',') within group (order by credential_id) from sys.credentials"));
        _ = simulation.ExecuteNonQuery("alter credential c1 with identity = 'idC'");
        AreEqual("idC", simulation.ExecuteScalar("select credential_identity from sys.credentials where name = 'c1'"));
        _ = simulation.ExecuteNonQuery("drop credential c1");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.credentials"));
    }

    [TestMethod]
    public void Duplicate_Raises15530()
        => new Simulation().AssertSqlError(
            "create credential c with identity = 'a'; create credential c with identity = 'b'",
            15530, "The credential with name \"c\" already exists.");

    [TestMethod]
    public void Missing_Raises15151()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError("alter credential c with identity = 'a'", 15151,
            "Cannot alter the credential 'c', because it does not exist or you do not have permission.");
        simulation.AssertSqlError("drop credential c", 15151,
            "Cannot drop the credential 'c', because it does not exist or you do not have permission.");
    }

    [TestMethod]
    public void CryptographicProvider_Raises15151()
        => new Simulation().AssertSqlError("create credential c with identity = 'a' for cryptographic provider p", 15151,
            "Cannot create credential for the cryptographic provider 'p', because it does not exist or you do not have permission.");

    [TestMethod]
    public void EmptyIdentity_IsASyntaxErrorBeforeTheBatchRuns()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError("create table t (a int); create credential c with identity = ''", 102, "Incorrect syntax near 'IDENTITY'.");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    [TestMethod]
    public void SecretWithoutIdentity_IsASyntaxError()
        => new Simulation().ValidateSyntaxError("create credential c with secret = 'a'", "secret");

    [TestMethod]
    public void Create_RollsBackWithTheTransaction()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("begin tran; create credential c with identity = 'a'; rollback");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.credentials"));
    }

    [TestMethod]
    public void LoginMapping_SurfacesAndBlocksTheDrop()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create credential c with identity = 'a';
            create login zl with password = 'P@ssw0rd!long', credential = c
            """);
        AreEqual("c", simulation.ExecuteScalar("select c.name from sys.server_principals p join sys.credentials c on c.credential_id = p.credential_id where p.name = 'zl'"));
        simulation.AssertSqlError("drop credential c", 15541, "Cannot drop the credential 'c' because it is used by a server principal.");
        _ = simulation.ExecuteNonQuery("alter login zl with no credential; drop credential c");
        simulation.AssertSqlError("alter login zl with credential = c", 15151,
            "Cannot find the credential 'c', because it does not exist or you do not have permission.");
    }

    [TestMethod]
    public void RestrictedLogin_IsRefusedAndSeesNoRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login zl with password = 'P@ssw0rd!long'; create credential c with identity = 'a'");
        _ = simulation.AssertSqlError("use master; execute as login = 'zl'; create credential d with identity = 'a'", 15247);
        _ = simulation.AssertSqlError("use master; execute as login = 'zl'; drop credential c", 15151);
        AreEqual(0, simulation.ExecuteScalar("use master; execute as login = 'zl'; select count(*) from sys.credentials"));
        _ = simulation.ExecuteNonQuery("use master; grant alter any credential to zl");
        AreEqual(1, simulation.ExecuteScalar("use master; execute as login = 'zl'; select count(*) from sys.credentials"));
        _ = simulation.ExecuteNonQuery("use master; deny view any definition to zl");
        AreEqual(0, simulation.ExecuteScalar("use master; execute as login = 'zl'; select count(*) from sys.credentials"));
    }

    [TestMethod]
    public void Shutdown_WithoutPermission_EndsTheBatchAndRollsBack()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login zl with password = 'P@ssw0rd!long'");
        using var connection = simulation.CreateOpenConnection();
        using (var command = connection.CreateCommand("use master; execute as login = 'zl'; begin tran; shutdown; select 1"))
        {
            var ex = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
            AreEqual((0, (byte)11), (ex.Number, ex.Class));
            AreEqual("6004:User does not have permission to perform this action.", $"{ex.Errors[^1].Number}:{ex.Errors[^1].Message}");
        }
        using var check = connection.CreateCommand("select @@trancount");
        AreEqual(0, check.ExecuteScalar());
    }

    [TestMethod]
    public void Shutdown_WithoutPermission_InsideTry_EndsTheBatchQuietly()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login zl with password = 'P@ssw0rd!long'");
        AreEqual(1, simulation.ExecuteScalar("use master; execute as login = 'zl'; select 1; begin try shutdown end try begin catch select 2 end catch; select 3"));
    }
}
