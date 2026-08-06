using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CREATE SCHEMA</c>'s <c>AUTHORIZATION</c> clause and its
/// <c>&lt;schema_element&gt;</c> list. The element list is part of the
/// statement rather than a run of trailing statements, and the point of that is
/// the name scope: an unqualified name inside an element resolves to the schema
/// being created. Probed against SQL Server 2025 — see
/// <c>docs/claude/schemas.md</c>.
/// </summary>
[TestClass]
public sealed class CreateSchemaElementTests
{
    // --- AUTHORIZATION ---

    [TestMethod]
    public void NoAuthorization_IsOwnedByDbo()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create schema s1;
            select principal_id from sys.schemas where name = 's1'
            """));

    [TestMethod]
    public void Authorization_BindsTheNamedUserAsOwner()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create user u1 without login",
            "create schema s1 authorization u1");
        AreEqual("u1", simulation.ExecuteScalar("select user_name(principal_id) from sys.schemas where name = 's1'"));
    }

    [TestMethod]
    public void Authorization_AcceptsARole()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create schema s1 authorization db_datareader");
        AreEqual("db_datareader", simulation.ExecuteScalar("select user_name(principal_id) from sys.schemas where name = 's1'"));
    }

    [TestMethod]
    public void Authorization_UnknownPrincipal_ReportsMsg15151ThenMsg2759()
    {
        var exception = new Simulation().AssertSqlError("create schema s1 authorization nobody", 15151);
        AreEqual("Cannot find the user 'nobody', because it does not exist or you do not have permission.", exception.Errors[0].Message);
        AreEqual(2759, exception.Errors[1].Number);
        AreEqual("CREATE SCHEMA failed due to previous errors.", exception.Errors[1].Message);
    }

    [TestMethod]
    public void Authorization_UnknownPrincipal_LeavesNoSchema()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create schema s1 authorization nobody", 15151);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.schemas where name = 's1'"));
    }

    [TestMethod]
    public void AuthorizationWithoutAName_NamesTheSchemaAfterTheOwner()
        // `CREATE SCHEMA AUTHORIZATION dbo` claims the name `dbo`, which is
        // then the reserved-name refusal.
        => AreEqual(2760, new Simulation().AssertSqlError("create schema authorization dbo", 2760).Number);

    [TestMethod]
    public void DuplicateSchema_ReportsMsg2714ThenMsg2759()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create schema s1");
        var exception = simulation.AssertSqlError("create schema s1", 2714);
        AreEqual(2759, exception.Errors[1].Number);
    }

    [TestMethod]
    public void DropUser_OwningASchema_ReportsMsg15138()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create user u1 without login",
            "create schema s1 authorization u1");
        simulation.AssertSqlError(
            "drop user u1",
            15138,
            "The database principal owns a schema in the database, and cannot be dropped.");
    }

    [TestMethod]
    public void DropUser_OwningNoSchema_StillWorks()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create user u1 without login");
        _ = simulation.ExecuteNonQuery("drop user u1");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.database_principals where name = 'u1'"));
    }

    // --- the element list: unqualified names land in the new schema ---

    [TestMethod]
    public void CreateTableElement_LandsInTheNewSchema()
        => AreEqual("s1", new Simulation().ExecuteScalar("""
            create schema s1 create table t (a int, b int);
            select schema_name(schema_id) from sys.tables where name = 't'
            """));

    [TestMethod]
    public void CreateTableElement_IsNotVisibleAsDbo()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create schema s1 create table t (a int);
            select count(*) from sys.tables where schema_id = schema_id('dbo') and name = 't'
            """));

    [TestMethod]
    public void SeveralCreateTableElements_AllLandInTheNewSchema()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create schema s1 create table t1 (a int) create table t2 (b int);
            select count(*) from sys.tables where schema_id = schema_id('s1')
            """));

    [TestMethod]
    public void ElementForeignKey_ResolvesToASiblingElement()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create schema s1 create table p (a int not null primary key) create table c (a int references p(a));
            select count(*) from sys.foreign_keys where schema_id = schema_id('s1')
            """));

    [TestMethod]
    public void AQualifiedElementNameStillWins()
        => AreEqual("dbo", new Simulation().ExecuteScalar("""
            create schema s1 create table dbo.qq (a int);
            select schema_name(schema_id) from sys.tables where name = 'qq'
            """));

    [TestMethod]
    public void CreateViewElement_LandsInTheNewSchemaAndResolvesItsBodyThere()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create schema s1 create table t (a int, b int) create view v as select * from t
            """);
        AreEqual("s1", simulation.ExecuteScalar("select schema_name(schema_id) from sys.views where name = 'v'"));
        AreEqual(2, simulation.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('s1.v')"));
    }

    [TestMethod]
    public void CreateViewElement_MayBeFollowedByFurtherElements()
    {
        // A view body normally runs to the end of its batch; inside an element
        // list it ends at the next element keyword instead.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create user u1 without login",
            "create schema s1 create table t (a int) create view v as select * from t grant select on v to u1");
        AreEqual("s1", simulation.ExecuteScalar("select schema_name(schema_id) from sys.views where name = 'v'"));
        AreEqual(1, simulation.ExecuteScalar("""
            select count(*) from sys.database_permissions
            where class = 1 and major_id = object_id('s1.v') and grantee_principal_id = user_id('u1')
            """));
    }

    [TestMethod]
    public void GrantElement_GrantsOnTheNewSchemasObject()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create user u1 without login",
            "create schema s1 create table t (a int) grant select on t to u1");
        AreEqual("s1", simulation.ExecuteScalar("""
            select object_schema_name(major_id) from sys.database_permissions
            where class = 1 and grantee_principal_id = user_id('u1')
            """));
    }

    [TestMethod]
    public void GrantElement_TakesAQualifiedSecurableToo()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create user u1 without login",
            "create table dbo.t (a int)",
            "create schema s1 grant select on dbo.t to u1");
        AreEqual("dbo", simulation.ExecuteScalar("""
            select object_schema_name(major_id) from sys.database_permissions
            where class = 1 and grantee_principal_id = user_id('u1')
            """));
    }

    [TestMethod]
    public void RevokeAndDenyAreElementsToo()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create user u1 without login",
            "create schema s1 create table t (a int) deny select on t to u1");
        AreEqual(1, simulation.ExecuteScalar("""
            select count(*) from sys.database_permissions
            where class = 1 and state = 'D' and grantee_principal_id = user_id('u1')
            """));
    }

    [TestMethod]
    public void AuthorizationAndElementsCombine()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create user u1 without login",
            "create schema s1 authorization u1 create table t (a int)");
        AreEqual("u1", simulation.ExecuteScalar("select user_name(principal_id) from sys.schemas where name = 's1'"));
        AreEqual("s1", simulation.ExecuteScalar("select schema_name(schema_id) from sys.tables where name = 't'"));
    }

    // --- atomicity ---

    [TestMethod]
    public void ADuplicateElement_RollsTheWholeStatementBack()
    {
        var simulation = new Simulation();
        var exception = simulation.AssertSqlError("create schema s1 create table t (a int) create table t (a int)", 2714);
        AreEqual(2759, exception.Errors[1].Number);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.schemas where name = 's1'"));
    }

    [TestMethod]
    public void AFailingGrantElement_RollsTheEarlierTableBack()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("create schema s1 create table t (a int) grant select on nosuch to public", 15151);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.schemas where name = 's1'"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    // --- the element grammar is closed ---

    [TestMethod]
    public void CreateProcedureElement_IsMsg156()
        => new Simulation().AssertSqlError(
            "create schema s1 create procedure p as select 1",
            156,
            "Incorrect syntax near the keyword 'procedure'.");

    [TestMethod]
    public void CreateFunctionElement_IsMsg156()
        => new Simulation().AssertSqlError("create schema s1 create function f() returns int as begin return 1 end", 156);

    [TestMethod]
    public void CreateIndexElement_IsMsg1018()
        => new Simulation().AssertSqlError("create schema s1 create table t (a int) create index ix on t(a)", 1018);

    [TestMethod]
    public void CreateTypeElement_IsMsg102()
        => new Simulation().AssertSqlError("create schema s1 create type ty from int", 102);
}
