using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which refusal real reports when a statement meets several, and the edges
/// of what a deny takes away (probed 2026-10-06 against SQL Server 2025). A
/// non-dbo session is established in-batch via <c>EXECUTE AS USER</c>.
/// </summary>
[TestClass]
public sealed class PermissionRefusalOrderTests
{
    [TestMethod]
    public void DdlAdmin_CreatesInADeniedSchema_ButCannotDropThere()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "alter role db_ddladmin add member u",
            "create schema s",
            "deny alter on schema::s to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; create table s.t (a int); revert; select count(*) from sys.tables where name = 't'"));
        _ = sim.AssertSqlError("execute as user = 'u'; drop table s.t", 3701);
    }

    [TestMethod]
    public void DatabaseControl_UnderASchemaDeny_CannotDropThere()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "grant control to u",
            "create schema s",
            "create table s.t (a int)",
            "deny alter on schema::s to u");
        _ = sim.AssertSqlError("execute as user = 'u'; drop table s.t", 3701);
        _ = sim.AssertSqlError("execute as user = 'u'; alter table s.t add b int", 1088);
    }

    [TestMethod]
    public void SchemaBoundView_ReferencesRefusal_OutranksCreateView()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "create table t (a int)",
            "grant alter on schema::dbo to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; exec('create view v with schemabinding as select a from dbo.t')", 229);
        AreEqual(1088, ex.Errors[1].Number);
    }

    [TestMethod]
    public void AlterAuthorization_MissingOwner_OutranksTakeOwnership()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create user u without login", "create table t (a int)", "create role r");
        sim.AssertSqlError("execute as user = 'u'; alter authorization on t to nosuch", 15151,
            "Cannot find the user 'nosuch', because it does not exist or you do not have permission.");
        sim.AssertSqlError("execute as user = 'u'; alter authorization on role::r to nosuch", 15151,
            "Cannot find the user 'nosuch', because it does not exist or you do not have permission.");
        sim.AssertSqlError("execute as user = 'u'; alter authorization on object::nosuchobject to nosuch", 15151,
            "Cannot find the object 'nosuchobject', because it does not exist or you do not have permission.");
    }

    [TestMethod]
    public void FullTextCatalog_BelongsToItsCreator()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create user u without login", "grant create fulltext catalog to u");
        AreEqual("u", sim.ExecuteScalar("execute as user = 'u'; create fulltext catalog c; revert; select user_name(principal_id) from sys.fulltext_catalogs"));
        AreEqual(0, sim.ExecuteScalar("execute as user = 'u'; drop fulltext catalog c; revert; select count(*) from sys.fulltext_catalogs"));
    }

    [TestMethod]
    public void DatabaseDenyViewDefinition_HidesTheUserItself()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "create role r",
            "alter role r add member u",
            "grant create view to r",
            "grant alter any user to u",
            "deny view definition to u");
        AreEqual("r", sim.ExecuteScalar("execute as user = 'u'; select string_agg(user_name(grantee_principal_id), ',') from sys.database_permissions where class = 0 and grantee_principal_id <> 0"));
        AreEqual(0, sim.ExecuteScalar("execute as user = 'u'; select count(*) from sys.database_principals where name = 'u'"));
    }

    [TestMethod]
    public void AlterRole_UnbracketedPublic_IsTwoSyntaxErrors()
    {
        var ex = new Simulation().AssertSqlError("alter role public add member dbo", 156);
        AreEqual("Incorrect syntax near 'member'.", ex.Errors[1].Message);
    }

    [TestMethod]
    public void DropSynonym_Missing_NamesItAsWritten()
        => new Simulation().AssertSqlError("drop synonym dbo.nosuch", 3701,
            "Cannot drop the synonym 'dbo.nosuch', because it does not exist or you do not have permission.");

    [TestMethod]
    public void CreateUser_NameOption_IsASyntaxError()
        => new Simulation().ValidateSyntaxError("create user u with name = x without login", "name");

    [TestMethod]
    public void GrantOnAUserToItself_IsASilentNoOp()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        var messages = 0;
        ((SimulatedDbConnection)connection).InfoMessage += (_, _) => messages++;
        using var command = connection.CreateCommand("create user u without login; grant impersonate on user::u to u; select count(*) from sys.database_permissions where class = 4");
        AreEqual(0, command.ExecuteScalar());
        AreEqual(0, messages);
    }

    [TestMethod]
    public void AlterUser_NullDefaultSchema_IsRefusedAsItRuns()
    {
        var ex = new Simulation().AssertSqlError("create user u without login; alter user u with default_schema = null; select 1", 102);
        AreEqual(((byte)16, "Incorrect syntax near 'default_schema'."), (ex.Class, ex.Errors[0].Message));
    }
}
