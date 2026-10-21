using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CREATE</c> / <c>ALTER</c> / <c>DROP DATABASE SCOPED CREDENTIAL</c>,
/// <c>sys.database_scoped_credentials</c>, and the credential as a securable
/// (probed 2026-10-21 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class DatabaseScopedCredentialTests
{
    private const string Rows = "select string_agg(concat(credential_id, ':', name, ':', credential_identity), ',') within group (order by credential_id) from sys.database_scoped_credentials";

    [TestMethod]
    public void CreateAlterDrop_RoundTripsThroughTheCatalog()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create master key encryption by password = 'Zz!Passw0rd#1';
            create database scoped credential c1 with identity = 'idA', secret = 's';
            create database scoped credential c2 with identity = N'idB';
            alter database scoped credential c1 with identity = 'idC';
            """);
        AreEqual("65536:c1:idC,65537:c2:idB", simulation.ExecuteScalar(Rows));
        AreEqual("65536:c1:idC,65537:c2:idB", simulation.ExecuteScalar(Rows.Replace("database_scoped_credentials", "database_credentials", StringComparison.Ordinal)));
        _ = simulation.ExecuteNonQuery("drop database scoped credential c1");
        AreEqual("65537:c2:idB", simulation.ExecuteScalar(Rows));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.credentials"));
    }

    [TestMethod]
    public void ASecret_NeedsTheMasterKey_AndTheRefusalSpendsAnId()
    {
        var simulation = new Simulation();
        var ex = simulation.AssertSqlError("create database scoped credential c with identity = 'a', secret = 's'", 15581);
        AreEqual(6, ex.State);
        _ = simulation.ExecuteNonQuery("create database scoped credential c with identity = 'a'");
        AreEqual("65537:c:a", simulation.ExecuteScalar(Rows));
    }

    [TestMethod]
    public void AMasterKeyTheSessionCantOpen_IsState7()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create master key encryption by password = 'Zz!Passw0rd#1';
            alter master key drop encryption by service master key;
            create database scoped credential c with identity = 'a';
            """);
        AreEqual(7, simulation.AssertSqlError("alter database scoped credential c with identity = 'b', secret = 's'", 15581).State);
        _ = simulation.ExecuteNonQuery("alter database scoped credential c with identity = 'b'");
        AreEqual("65536:c:b", simulation.ExecuteScalar(Rows));
    }

    [TestMethod]
    public void ADuplicate_Raises15530_AndSpendsAnId()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError("create database scoped credential c with identity = 'a'; create database scoped credential c with identity = 'b'",
            15530, "The credential with name \"c\" already exists.");
        _ = simulation.ExecuteNonQuery("create database scoped credential d with identity = 'd'");
        AreEqual("65536:c:a,65538:d:d", simulation.ExecuteScalar(Rows));
    }

    [TestMethod]
    public void AMissingOne_Raises15151()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError("alter database scoped credential c with identity = 'a'", 15151,
            "Cannot alter the credential 'c', because it does not exist or you do not have permission.");
        simulation.AssertSqlError("drop database scoped credential c", 15151,
            "Cannot drop the credential 'c', because it does not exist or you do not have permission.");
    }

    [TestMethod]
    public void ACryptographicProvider_Raises33157()
        => new Simulation().AssertSqlError("create database scoped credential c with identity = 'a' for cryptographic provider p", 33157,
            "Cryptographic providers are not supported on database credentials.");

    [TestMethod]
    public void DropIfExists_IsASyntaxError()
        => new Simulation().AssertSqlError("drop database scoped credential if exists c", 156);

    [TestMethod]
    public void TheDdl_RollsBackWithTheTransaction()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database scoped credential c with identity = 'a';
            begin tran;
            create database scoped credential d with identity = 'd';
            drop database scoped credential c;
            rollback;
            """);
        AreEqual("65536:c:a", simulation.ExecuteScalar(Rows));
    }

    [TestMethod]
    public void EveryStatement_NeedsControlOfTheDatabase()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database scoped credential c with identity = 'a'; create user u without login; grant alter to u");
        AreEqual("15247|15151|15151|0", simulation.ExecuteScalar("""
            execute as user = 'u';
            declare @r nvarchar(100) = '';
            begin try create database scoped credential d with identity = 'd'; end try begin catch set @r += concat(error_number(), '|'); end catch;
            begin try alter database scoped credential c with identity = 'b'; end try begin catch set @r += concat(error_number(), '|'); end catch;
            begin try drop database scoped credential c; end try begin catch set @r += concat(error_number(), '|'); end catch;
            set @r += (select concat(count(*), '') from sys.database_scoped_credentials);
            revert;
            select @r;
            """));
    }

    [TestMethod]
    public void Grants_OnTheCredential_LetAPrincipalSeeAlterAndDropIt()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database scoped credential c with identity = 'a';
            create database scoped credential d with identity = 'd';
            create user u without login;
            grant alter on database scoped credential::c to u;
            grant view definition on database scoped credential::d to u;
            """);
        AreEqual("ALTER:DATABASE_SCOPED_CREDENTIAL,VIEW DEFINITION:DATABASE_SCOPED_CREDENTIAL", simulation.ExecuteScalar("""
            select string_agg(concat(permission_name, ':', class_desc), ',') within group (order by permission_name)
            from sys.database_permissions where class = 32
            """));
        AreEqual("c,d|altered|15151", simulation.ExecuteScalar("""
            execute as user = 'u';
            declare @r nvarchar(100) = (select string_agg(name, ',') within group (order by name) from sys.database_scoped_credentials);
            begin try alter database scoped credential c with identity = 'b'; set @r += '|altered'; end try begin catch set @r += concat('|', error_number()); end catch;
            begin try drop database scoped credential c; set @r += '|dropped'; end try begin catch set @r += concat('|', error_number()); end catch;
            revert;
            select @r;
            """));
        _ = simulation.ExecuteNonQuery("grant control on database scoped credential::c to u");
        _ = simulation.ExecuteNonQuery("execute as user = 'u'; drop database scoped credential c; revert;");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.database_permissions where class = 32"));
    }

    [TestMethod]
    public void AGrantOnAMissingCredential_Raises15151()
        => new Simulation().AssertSqlError("create user u without login; grant control on database scoped credential::c to u", 15151,
            "Cannot find the database scoped credential 'c', because it does not exist or you do not have permission.");

    [TestMethod]
    public void AlterAuthorization_ChangesTheOwner_AndDropsTheGrants()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database scoped credential c with identity = 'a';
            create user u without login;
            create role r;
            grant view definition on database scoped credential::c to u;
            alter authorization on database scoped credential::c to r;
            """);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.database_scoped_credentials where principal_id = user_id('r')"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.database_permissions where class = 32"));
        simulation.AssertSqlError("alter authorization on database scoped credential::missing to r", 15151,
            "Cannot find the database scoped credential 'missing', because it does not exist or you do not have permission.");
    }

    [TestMethod]
    public void ACredential_SurvivesABackupAndRestore_AndItsIdsKeepCounting()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "use d; create database scoped credential c with identity = 'a'; create database scoped credential gone with identity = 'g'; drop database scoped credential gone",
            "use master; backup database d to disk = N'd.bak'; drop database d; restore database d from disk = N'd.bak'",
            "use d; create database scoped credential e with identity = 'e'");
        AreEqual("65536:c:a,65538:e:e", simulation.ExecuteScalar("use d; " + Rows));
    }

    [TestMethod]
    public void TheDdl_RunsInsideATransaction()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            begin tran;
            create database scoped credential c with identity = 'a';
            commit;
            select count(*) from sys.database_scoped_credentials;
            """));
}
