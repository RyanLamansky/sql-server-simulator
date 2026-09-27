using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER AUTHORIZATION</c> and ownership: the classes and their refusals,
/// the catalog columns an owner surfaces in, the refusals to drop an owner,
/// the owner's implicit rights, ownership chaining by owner, <c>WITH EXECUTE
/// AS OWNER</c>, and database ownership (probed 2026-09-27 against SQL Server
/// 2025).
/// </summary>
[TestClass]
public sealed class AlterAuthorizationTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create user u1 without login; create user u2 without login; create role r1;
            create application role ar with password = 'Xx!12345678';
            create table t (a int constraint pk primary key, b int constraint ck check (b > 0));
            create type ty from int; create type tt as table (a int);
            create sequence sq; create synonym sy for t;
            """,
            "create view v as select a from t",
            "create procedure p as select 1",
            "create function f() returns int as begin return 1 end",
            "create trigger tr on t after insert as select 1",
            "create schema s authorization u1",
            "create xml schema collection xc as N'<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><xsd:element name=\"a\"/></xsd:schema>'");
        return sim;
    }

    [TestMethod]
    [DataRow("t", "u1", "sys.tables", 5)]
    [DataRow("object::v", "u2", "sys.views", 6)]
    [DataRow("p", "r1", "sys.procedures", 7)]
    [DataRow("f", "ar", "sys.objects", 8)]
    [DataRow("sq", "u1", "sys.sequences", 5)]
    [DataRow("sy", "u1", "sys.synonyms", 5)]
    [DataRow("dbo.t", "db_owner", "sys.objects", 16384)]
    [DataRow("t", "[public]", "sys.objects", 0)]
    [DataRow("t", "guest", "sys.objects", 2)]
    [DataRow("t", "dbo", "sys.objects", 1)]
    public void Object_TakesExplicitOwner(string entity, string owner, string view, int expected)
    {
        var sim = Seeded();
        var name = entity.Split(':', '.')[^1];
        AreEqual(expected, sim.ExecuteScalar($"alter authorization on {entity} to {owner}; select principal_id from {view} where name = '{name}'"));
        AreEqual(expected, sim.ExecuteScalar($"select objectproperty(object_id('{name}'), 'OwnerId')"));
    }

    [TestMethod]
    public void Object_SchemaOwner_ResetsToNull()
        => AreEqual(DBNull.Value, Seeded().ExecuteScalar("alter authorization on t to u1; alter authorization on t to schema owner; select principal_id from sys.objects where name = 't'"));

    [TestMethod]
    public void TriggerAndConstraint_FollowTheParentsOwner()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter authorization on t to u1");
        AreEqual(5, sim.ExecuteScalar("select objectproperty(object_id('tr'), 'OwnerId')"));
        AreEqual(5, sim.ExecuteScalar("select objectproperty(object_id('pk'), 'OwnerId')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select principal_id from sys.objects where name = 'tr'"));
    }

    [TestMethod]
    public void Object_InUserSchema_OwnedThroughSchema()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table s.x (a int); create table s.y (a int); alter authorization on s.y to u2");
        AreEqual(5, sim.ExecuteScalar("select objectproperty(object_id('s.x'), 'OwnerId')"));
        _ = sim.ExecuteNonQuery("alter authorization on schema::s to u2; alter schema dbo transfer s.y");
        AreEqual(6, sim.ExecuteScalar("select objectproperty(object_id('s.x'), 'OwnerId')"));
        AreEqual(6, sim.ExecuteScalar("select principal_id from sys.objects where name = 'y'"));
    }

    [TestMethod]
    [DataRow("alter authorization on pk to u1", 15346, "Cannot change owner for an object that is owned by a parent object. Change the owner of the parent object instead.")]
    [DataRow("alter authorization on ck to u1", 15346, "Cannot change owner for an object that is owned by a parent object. Change the owner of the parent object instead.")]
    [DataRow("alter authorization on tr to u1", 15346, "Cannot change owner for an object that is owned by a parent object. Change the owner of the parent object instead.")]
    [DataRow("alter authorization on t to nosuch", 15151, "Cannot find the user 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on nosuch to nosuch", 15151, "Cannot find the object 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on t to sys", 15151, "Cannot find the principal 'sys', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on simulated.dbo.t to u1", 15151, "Cannot find the object 't', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on sys.objects to u1", 15151, "Cannot find the object 'objects', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on user::u1 to u2", 15344, "Ownership change for user is not supported.")]
    [DataRow("alter authorization on application role::ar to u1", 15344, "Ownership change for application role is not supported.")]
    [DataRow("create table #x (a int); alter authorization on #x to u1", 15344, "Ownership change for object is not supported.")]
    [DataRow("alter authorization on schema::dbo to u1", 15150, "Cannot alter the schema 'dbo'.")]
    [DataRow("alter authorization on schema::guest to u1", 15150, "Cannot alter the schema 'guest'.")]
    [DataRow("alter authorization on schema::nosuch to u1", 15151, "Cannot find the schema 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on schema::s to schema owner", 15151, "Cannot find the user 'SCHEMA OWNER', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on type::int to u1", 15247, "User does not have permission to perform this action.")]
    [DataRow("alter authorization on type::nosuch to u1", 15151, "Cannot find the type 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on role::nosuch to u1", 15151, "Cannot find the role 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on role::u1 to dbo", 15151, "Cannot find the role 'u1', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on fulltext catalog::nosuch to u1", 15151, "Cannot find the fulltext catalog 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on database::nosuch to sa", 15151, "Cannot find the database 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on database::master to sa", 15109, "Cannot change the owner of the master, model, tempdb or distribution database.")]
    [DataRow("alter authorization on database::simulated to sysadmin", 15353, "An entity of type database cannot be owned by a role, a group, an approle, or by principals mapped to certificates or asymmetric keys.")]
    [DataRow("alter authorization on database::simulated to u1", 15151, "Cannot find the principal 'u1', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on t to public", 156, "Incorrect syntax near the keyword 'public'.")]
    [DataRow("alter authorization on database::current to sa", 156, "Incorrect syntax near the keyword 'current'.")]
    public void Refusals(string sql, int number, string message) => Seeded().AssertSqlError(sql, number, message);

    [TestMethod]
    [DataRow("alter authorization on t to u1, u2", ",")]
    [DataRow("alter authorization t to u1", "t")]
    public void SyntaxErrors(string sql, string near) => Seeded().ValidateSyntaxError(sql, near);

    [TestMethod]
    public void Types_TakeAnOwnerAndResetToSchemaOwner()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter authorization on type::ty to u1; alter authorization on type::dbo.tt to u2");
        AreEqual(5, sim.ExecuteScalar("select principal_id from sys.types where name = 'ty'"));
        AreEqual(6, sim.ExecuteScalar("select principal_id from sys.table_types where name = 'tt'"));
        AreEqual(6, sim.ExecuteScalar("select typeproperty('tt', 'OwnerId')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select principal_id from sys.objects where type = 'TT'"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("alter authorization on type::ty to schema owner; select principal_id from sys.types where name = 'ty'"));
    }

    [TestMethod]
    public void XmlSchemaCollection_TakesAnOwner()
    {
        var sim = Seeded();
        AreEqual(5, sim.ExecuteScalar("alter authorization on xml schema collection::xc to u1; select principal_id from sys.xml_schema_collections where name = 'xc'"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("alter authorization on xml schema collection::dbo.xc to schema owner; select principal_id from sys.xml_schema_collections where name = 'xc'"));
    }

    [TestMethod]
    public void Schema_TakesAnOwner_FixedRoleSchemaToo()
    {
        var sim = Seeded();
        AreEqual(6, sim.ExecuteScalar("alter authorization on schema::s to u2; select principal_id from sys.schemas where name = 's'"));
        AreEqual("u2", sim.ExecuteScalar("select SCHEMA_OWNER from INFORMATION_SCHEMA.SCHEMATA where SCHEMA_NAME = 's'"));
        AreEqual(5, sim.ExecuteScalar("alter authorization on schema::db_owner to u1; select principal_id from sys.schemas where name = 'db_owner'"));
    }

    [TestMethod]
    public void Role_TakesAnOwner_ItselfIncluded()
    {
        var sim = Seeded();
        AreEqual(7, sim.ExecuteScalar("create role r2 authorization r1; select owning_principal_id from sys.database_principals where name = 'r2'"));
        AreEqual(5, sim.ExecuteScalar("alter authorization on role::r2 to u1; select owning_principal_id from sys.database_principals where name = 'r2'"));
        AreEqual(7, sim.ExecuteScalar("alter authorization on role::r1 to r1; select owning_principal_id from sys.database_principals where name = 'r1'"));
        sim.AssertSqlError("create role r3 authorization nosuch", 15151, "Cannot find the user 'nosuch', because it does not exist or you do not have permission.");
    }

    [TestMethod]
    public void FullTextCatalog_TakesAnOwner()
        => AreEqual(5, Seeded().ExecuteScalar("create fulltext catalog c1; alter authorization on fulltext catalog::c1 to u1; select principal_id from sys.fulltext_catalogs where name = 'c1'"));

    [TestMethod]
    public void Rollback_RestoresTheOwner()
        => AreEqual(DBNull.Value, Seeded().ExecuteScalar("begin tran; alter authorization on t to u1; rollback; select principal_id from sys.objects where name = 't'"));

    [TestMethod]
    public void OwnerChange_DropsPermissions_OnlyWhenTheOwnerMoves()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("""
            create table t2 (a int); create table t3 (a int);
            grant select on t2 to u2; grant select on t3 to u2; grant alter on role::r1 to u2; grant select on schema::s to u2;
            alter authorization on t2 to dbo;
            alter authorization on t3 to u1;
            alter authorization on role::r1 to u1;
            alter authorization on schema::s to dbo
            """);
        AreEqual("OBJECT_OR_COLUMN:t2", sim.ExecuteScalar("select string_agg(class_desc + ':' + isnull(object_name(major_id), ''), ',') from sys.database_permissions where grantee_principal_id = user_id('u2') and class <> 0"));
    }

    [TestMethod]
    [DataRow("alter authorization on t to u1", 15183, "The database principal owns objects in the database and cannot be dropped.")]
    [DataRow("alter authorization on type::ty to u1", 15184, "The database principal owns data types in the database and cannot be dropped.")]
    [DataRow("alter authorization on role::r1 to u1", 15421, "The database principal owns a database role and cannot be dropped.")]
    [DataRow("alter authorization on xml schema collection::xc to u1", 15138, "The database principal owns a XML namespace in the database, and cannot be dropped.")]
    public void DropUser_RefusedWhileItOwns(string setup, int number, string message)
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery($"alter authorization on schema::s to dbo; {setup}");
        sim.AssertSqlError("drop user u1", number, message);
    }

    [TestMethod]
    public void DropUser_ObjectsOutrankTypesOutrankSchemas()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter authorization on type::ty to u1; alter authorization on t to u1");
        _ = sim.AssertSqlError("drop user u1", 15183);
        _ = sim.AssertSqlError("alter authorization on t to dbo; drop user u1", 15184);
        _ = sim.AssertSqlError("alter authorization on type::ty to dbo; drop user u1", 15138);
    }

    [TestMethod]
    public void DropRoleAndApplicationRole_RefusedWhileTheyOwn()
    {
        var sim = Seeded();
        _ = sim.AssertSqlError("alter authorization on t to r1; drop role r1", 15183);
        _ = sim.AssertSqlError("alter authorization on t to ar; drop application role ar", 15183);
        _ = sim.AssertSqlError("alter authorization on t to dbo; alter authorization on role::r1 to r1; drop role r1", 15421);
    }

    [TestMethod]
    public void Owner_HoldsControl_AndSeesItsMetadata()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table t2 (a int); alter authorization on t2 to u1");
        AreEqual("t2", sim.ExecuteScalar("execute as user = 'u1'; alter table t2 add b int; select string_agg(name, ',') from sys.tables"));
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u1'; select has_perms_by_name('t2', 'object', 'CONTROL')"));
        AreEqual(0, sim.ExecuteScalar("execute as user = 'u1'; select has_perms_by_name('t', 'object', 'SELECT')"));
    }

    [TestMethod]
    public void RoleOwner_SharesOwnershipWithItsMembers()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table t2 (a int); insert t2 values (4); alter authorization on t2 to r1; alter role r1 add member u2");
        AreEqual(4, sim.ExecuteScalar("execute as user = 'u2'; select a from t2"));
    }

    [TestMethod]
    public void TakeOwnership_GatesTheChange_AndImpersonateTheNewOwner()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create user u3 without login; grant select on t to u3");
        sim.AssertSqlError("execute as user = 'u3'; alter authorization on t to u3", 15151, "Cannot find the object 't', because it does not exist or you do not have permission.");
        _ = sim.ExecuteNonQuery("grant take ownership on t to u3");
        sim.AssertSqlError("execute as user = 'u3'; alter authorization on t to u2", 15151, "Cannot find the principal 'u2', because it does not exist or you do not have permission.");
        AreEqual(9, sim.ExecuteScalar("execute as user = 'u3'; alter authorization on t to u3; select principal_id from sys.objects where name = 't'"));
    }

    [TestMethod]
    [DataRow("create view cv as select a from ct", "select * from cv", "grant select on cv to u2", "SELECT")]
    [DataRow("create procedure cp as select a from ct", "exec cp", "grant execute on cp to u2", "SELECT")]
    [DataRow("create function cf() returns int as begin return (select max(a) from ct) end", "select dbo.cf()", "grant execute on cf to u2", "SELECT")]
    public void Chain_BrokenByAnotherOwner(string module, string call, string grant, string permission)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create user u1 without login; create user u2 without login; create table ct (a int)", module);
        var name = module.Split(' ')[2].Split('(')[0];
        _ = sim.ExecuteNonQuery($"alter authorization on {name} to u1; {grant}");
        var ex = sim.AssertSqlError($"execute as user = 'u2'; {call}", 229);
        Contains($"The {permission} permission was denied on the object 'ct'", ex.Errors[0].Message);
        _ = sim.ExecuteNonQuery("alter authorization on ct to u1");
        _ = sim.ExecuteScalar($"execute as user = 'u2'; {call}");
    }

    [TestMethod]
    public void Chain_BrokenInATriggerBody()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u1 without login; create user u2 without login; create table t2 (a int); create table lg (a int)",
            "create trigger tr on t2 after insert as insert lg values (9)");
        _ = sim.ExecuteNonQuery("alter authorization on t2 to u1; grant insert on t2 to u2");
        var ex = sim.AssertSqlError("execute as user = 'u2'; insert t2 values (5)", 229);
        Contains("The INSERT permission was denied on the object 'lg'", ex.Errors[0].Message);
    }

    [TestMethod]
    public void Chain_BrokenForAnInsertThroughAView()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create user u1 without login; create user u2 without login; create table t (a int)", "create view v as select a from t");
        _ = sim.ExecuteNonQuery("alter authorization on v to u1; grant insert on v to u2");
        var ex = sim.AssertSqlError("execute as user = 'u2'; insert v values (1)", 229);
        Contains("The INSERT permission was denied on the object 't'", ex.Errors[0].Message);
    }

    // A dbo-owned view over a w-owned table: the chain breaks at the table.
    private static Simulation BrokenViewChain(string grants)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user c without login; create user w without login; create table t (a int, b int); insert t values (1, 1), (2, 2); alter authorization on t to w",
            "create view v as select a, b from t");
        _ = sim.ExecuteNonQuery(grants);
        return sim;
    }

    [TestMethod]
    [DataRow("grant update on v to c", "update v set a = 5", "The UPDATE permission was denied on the object 't'")]
    [DataRow("grant update, select on v to c; grant update on t to c", "update v set a = 5 where b = 1", "The SELECT permission was denied on the object 't'")]
    [DataRow("grant update, select on v to c; grant update on t to c", "update v set a = a + 1", "The SELECT permission was denied on the object 't'")]
    [DataRow("grant delete on v to c", "delete v", "The DELETE permission was denied on the object 't'")]
    [DataRow("grant delete, select on v to c; grant delete on t to c", "delete v where a = 1", "The SELECT permission was denied on the object 't'")]
    [DataRow("grant select, update on v to c", "merge v using (select 1 k) s on v.a = s.k when matched then update set b = 9;", "The SELECT permission was denied on the object 't'")]
    [DataRow("grant select, update on v to c; grant select on t to c", "merge v using (select 1 k) s on v.a = s.k when matched then update set b = 9;", "The UPDATE permission was denied on the object 't'")]
    [DataRow("grant select, insert, delete on v to c; grant select on t to c", "merge v using (select 1 k) s on v.a = s.k when matched then delete when not matched then insert (a, b) values (7, 7);", "The INSERT permission was denied on the object 't'")]
    [DataRow("grant select, delete on v to c; grant select on t to c", "merge v using (select 1 k) s on v.a = s.k when matched then delete;", "The DELETE permission was denied on the object 't'")]
    public void Chain_BrokenForDmlThroughAView(string grants, string statement, string message)
    {
        var ex = BrokenViewChain(grants).AssertSqlError($"execute as user = 'c'; {statement}", 229);
        Contains(message, ex.Errors[0].Message);
    }

    [TestMethod]
    [DataRow("grant select, update on t to c", "update v set a = 5 where b = 1")]
    [DataRow("grant update (a) on t to c; grant select (b) on t to c", "update v set a = 5 where b = 1")]
    [DataRow("grant select, delete on t to c", "delete v where a = 1")]
    [DataRow("grant select, update on t to c", "merge v using (select 1 k) s on v.a = s.k when matched then update set b = 9;")]
    public void Chain_BrokenForDmlThroughAView_BaseGrantAdmits(string grants, string statement)
    {
        var sim = BrokenViewChain("grant select, insert, update, delete on v to c; " + grants);
        AreEqual(1, sim.ExecuteScalar($"execute as user = 'c'; {statement} select @@rowcount"));
    }

    [TestMethod]
    [DataRow("grant update (b) on t to c; grant select (b) on t to c", "update v set a = 5 where b = 1", "The UPDATE permission was denied on the column 'a' of the object 't'")]
    [DataRow("grant update (a) on t to c; grant select (a) on t to c", "update v set a = 5 where b = 1", "The SELECT permission was denied on the column 'b' of the object 't'")]
    public void Chain_BrokenForAnUpdateThroughAView_IsColumnGrain(string grants, string statement, string message)
    {
        var ex = BrokenViewChain("grant select, update on v to c; " + grants).AssertSqlError($"execute as user = 'c'; {statement}", 230);
        Contains(message, ex.Errors[0].Message);
    }

    [TestMethod]
    public void Chain_BrokenThroughAViewOwnedByAnother()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user c without login; create user w without login; create table t (a int); insert t values (1)",
            "create view v as select a from t");
        _ = sim.ExecuteNonQuery("alter authorization on v to w; grant update, delete on v to c");
        Contains("The UPDATE permission was denied on the object 't'",
            sim.AssertSqlError("execute as user = 'c'; update v set a = 5", 229).Errors[0].Message);
        Contains("The DELETE permission was denied on the object 't'",
            sim.AssertSqlError("execute as user = 'c'; delete v", 229).Errors[0].Message);
    }

    [TestMethod]
    public void Chain_BrokenThroughAView_InsteadOfTriggers()
    {
        // INSTEAD OF INSERT writes nothing through the view and checks nothing
        // on the base; INSTEAD OF UPDATE / DELETE still read the base rows for
        // their pseudo-tables, which takes SELECT on it.
        var sim = BrokenViewChain("grant insert, update, delete on v to c");
        sim.ExecuteBatches(
            "create trigger tri on v instead of insert as select 'fired'",
            "create trigger tru on v instead of update as select 'fired'",
            "create trigger trd on v instead of delete as select 'fired'");
        AreEqual("fired", sim.ExecuteScalar("execute as user = 'c'; insert v values (3, 3)"));
        Contains("The SELECT permission was denied on the object 't'",
            sim.AssertSqlError("execute as user = 'c'; update v set a = 3", 229).Errors[0].Message);
        Contains("The SELECT permission was denied on the object 't'",
            sim.AssertSqlError("execute as user = 'c'; delete v", 229).Errors[0].Message);
        _ = sim.ExecuteNonQuery("grant select on t to c");
        AreEqual("fired", sim.ExecuteScalar("execute as user = 'c'; update v set a = 3"));
        AreEqual("fired", sim.ExecuteScalar("execute as user = 'c'; delete v"));
    }

    [TestMethod]
    public void Chain_IntactThroughAView_NeedsNoBaseGrant()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user c without login; create table t (a int, b int); insert t values (1, 1)",
            "create view v as select a, b from t");
        _ = sim.ExecuteNonQuery("grant select, update, delete on v to c");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'c'; update v set a = 5 where b = 1; select @@rowcount"));
        AreEqual(1, sim.ExecuteScalar("execute as user = 'c'; delete v where a = 5; select @@rowcount"));
    }

    [TestMethod]
    public void ExecuteAsOwner_RunsAsTheEffectiveOwner()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u1 without login; create role r1; create table t (a int)",
            "create procedure p with execute as owner as select user_name()",
            "create trigger tr on t with execute as owner after insert as select user_name()");
        AreEqual("dbo", sim.ExecuteScalar("exec p"));
        AreEqual("u1", sim.ExecuteScalar("alter authorization on p to u1; exec p"));
        AreEqual("u1", sim.ExecuteScalar("alter authorization on t to u1; insert t values (1)"));
        var ex = sim.AssertSqlError("alter authorization on p to r1; exec p", 15517);
        AreEqual("p", ex.Errors[0].Procedure);
    }

    [TestMethod]
    public void SpHelp_ReportsTheEffectiveOwner()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table s.x (a int); alter authorization on t to u2");
        using var reader = sim.ExecuteReader("exec sp_help 's.x'");
        IsTrue(reader.Read());
        AreEqual("u1", reader["Owner"]);
        reader.Close();
        using var list = sim.ExecuteReader("exec sp_help");
        var owners = new Dictionary<string, string>();
        while (list.Read())
            owners[(string)list["Name"]] = (string)list["Owner"];
        AreEqual("u2", owners["t"]);
        AreEqual("u2", owners["pk"]);
    }

    [TestMethod]
    public void DdlTrigger_SeesTheNewOwner()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create trigger dt on database for alter_authorization_database as select eventdata().value('(/EVENT_INSTANCE/OwnerName)[1]', 'sysname') + ':' + eventdata().value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname')");
        AreEqual("u1:TABLE", sim.ExecuteScalar("alter authorization on t to u1"));
        AreEqual("u2:SCHEMA", sim.ExecuteScalar("alter authorization on schema::s to u2"));
    }

    [TestMethod]
    public void Database_OwnedByALogin_WhichThenMapsToDbo()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login l1 with password = 'Xx!12345678'; create login l2 with password = 'Xx!12345678'; create user ul2 for login l2");
        AreEqual("sa", sim.ExecuteScalar("select suser_sname(owner_sid) from sys.databases where name = 'simulated'"));
        _ = sim.ExecuteNonQuery("alter authorization on database::simulated to l1");
        AreEqual("l1", sim.ExecuteScalar("select suser_sname(owner_sid) from sys.databases where name = 'simulated'"));
        AreEqual("l1", sim.ExecuteScalar("select suser_sname(sid) from sys.database_principals where name = 'dbo'"));
        AreEqual("dbo", sim.ExecuteScalar("execute as login = 'l1'; select user_name()"));
        using (var reader = sim.ExecuteReader("exec sp_helpdb 'simulated'"))
        {
            IsTrue(reader.Read());
            AreEqual("l1", reader["owner"]);
        }
        sim.AssertSqlError("alter authorization on database::simulated to l2", 15110, "The proposed new database owner is already a user or aliased in the database.");
        sim.AssertSqlError("create user ux for login l1", 15063, "The login already has an account with the user name 'dbo'.");
        sim.AssertSqlError("drop login l1", 15174, "Login 'l1' owns one or more database(s). Change the owner of the database(s) before dropping the login.");
        _ = sim.ExecuteNonQuery("begin tran; exec sp_changedbowner 'sa'; rollback");
        AreEqual("l1", sim.ExecuteScalar("select suser_sname(owner_sid) from sys.databases where name = 'simulated'"));
        _ = sim.ExecuteNonQuery("exec sp_changedbowner 'sa'; drop login l1");
        AreEqual("sa", sim.ExecuteScalar("select suser_sname(owner_sid) from sys.databases where name = 'simulated'"));
    }

    [TestMethod]
    [DataRow("grant take ownership on database::simulated to u", "alter authorization on database::simulated to l2", "l2")]
    [DataRow("alter role db_owner add member u", "alter authorization on database::simulated to l2", "l2")]
    [DataRow("grant take ownership on database::simulated to u", "alter authorization on database::simulated to sa", "sa")]
    [DataRow("grant take ownership on database::simulated to u", "exec sp_changedbowner 'l2'", "l2")]
    public void Database_RestrictedCaller_NeedsImpersonateOnTheNewOwner(string grant, string statement, string owner)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create login l1 with password = 'Xx!12345678'; create login l2 with password = 'Xx!12345678'; create user u for login l1; {grant}");
        sim.AssertSqlError($"execute as login = 'l1'; {statement}", 15151,
            $"Cannot find the principal '{owner}', because it does not exist or you do not have permission.");
        AreEqual("sa", sim.ExecuteScalar("select suser_sname(owner_sid) from sys.databases where name = 'simulated'"));
        _ = sim.ExecuteNonQuery($"use master; grant impersonate on login::{owner} to l1");
        _ = sim.ExecuteNonQuery($"execute as login = 'l1'; {statement}; revert");
        AreEqual(owner, sim.ExecuteScalar("select suser_sname(owner_sid) from sys.databases where name = 'simulated'"));
    }

    [TestMethod]
    public void Database_RestrictedCaller_ImpersonateCheckPrecedes15110()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login l1 with password = 'Xx!12345678'; create login l2 with password = 'Xx!12345678'; create user u for login l1; create user u2 for login l2; grant take ownership on database::simulated to u");
        _ = sim.AssertSqlError("execute as login = 'l1'; alter authorization on database::simulated to l2", 15151);
    }

    [TestMethod]
    public void Database_OwnerChange_RaisesTheDatabaseEventInTheSessionsDatabase()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database other");
        sim.ExecuteBatches("create trigger dt on database for alter_authorization_database as select eventdata().value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname') + ':' + eventdata().value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname') + ':' + eventdata().value('(/EVENT_INSTANCE/OwnerName)[1]', 'sysname') + ':' + eventdata().value('(/EVENT_INSTANCE/SchemaName)[1]', 'sysname')");
        AreEqual("DATABASE:simulated:sa:", sim.ExecuteScalar("alter authorization on database::simulated to sa"));
        AreEqual("DATABASE:other:sa:", sim.ExecuteScalar("alter authorization on database::other to sa"));
        AreEqual("DATABASE:simulated:sa:", sim.ExecuteScalar("exec sp_changedbowner 'sa'"));
    }

    [TestMethod]
    [DataRow("exec sp_changedbowner", 201)]
    [DataRow("exec sp_changedbowner 'l2'", 15110)]
    public void SpChangeDbOwner_Refusals(string sql, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login l2 with password = 'Xx!12345678'; create user ul2 for login l2");
        _ = sim.AssertSqlError(sql, number);
    }

    [TestMethod]
    public void SpChangeDbOwner_NullIsANoOp()
        => AreEqual("sa", new Simulation().ExecuteScalar("exec sp_changedbowner null; select suser_sname(owner_sid) from sys.databases where name = db_name()"));

    [TestMethod]
    public void CreateDatabase_IsOwnedByTheCreatingLogin()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lc with password = 'Xx!12345678'; create user lc for login lc; alter server role dbcreator add member lc");
        _ = sim.ExecuteNonQuery("execute as login = 'lc'; create database authc");
        AreEqual("lc", sim.ExecuteScalar("select suser_sname(owner_sid) from sys.databases where name = 'authc'"));
    }
}
