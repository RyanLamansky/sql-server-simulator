using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The DDL statement-permission gates beyond the CREATE TABLE / VIEW /
/// PROCEDURE / FUNCTION / ALTER TABLE / DROP TABLE set: the ALTER (and
/// CREATE OR ALTER) of an existing module, the DROP of every object kind, the
/// index / trigger / synonym / type / XML-collection / full-text / role /
/// schema / database statements, <c>ALTER SCHEMA … TRANSFER</c>,
/// <c>ALTER DATABASE</c> and <c>sp_rename</c>. Each gate is probed against
/// SQL Server 2025 for its permission, message number, severity and state.
/// A non-dbo session is established in-batch via <c>EXECUTE AS USER = 'u'</c>.
/// </summary>
[TestClass]
public sealed class StatementPermissionGateTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t1 (id int not null, v varchar(20) null)",
            "create index ix_t1 on dbo.t1 (id)",
            "create user u without login");
        return sim;
    }

    // ---- ALTER / CREATE OR ALTER of an existing module ----

    [TestMethod]
    public void AlterProcedure_NoObjectAlter_Raises3701State20()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create procedure dbo.p as select 1");
        var ex = sim.AssertSqlError("execute as user = 'u'; exec('alter procedure dbo.p as select 2')", 3701);
        AreEqual((byte)14, ex.Class);
        AreEqual((byte)20, ex.State);
        AreEqual("Cannot alter the procedure 'p', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void AlterProcedure_WithObjectAlter_Succeeds()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create procedure dbo.p as select 1");
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.p to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; exec('alter procedure dbo.p as select 2')");
        Contains("select 2", (string)sim.ExecuteScalar("select object_definition(object_id('dbo.p'))")!);
    }

    [TestMethod]
    public void AlterProcedure_WithSchemaAlter_Succeeds()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create procedure dbo.p as select 1");
        _ = sim.ExecuteNonQuery("grant alter on schema::dbo to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; exec('alter procedure dbo.p as select 2')");
        Contains("select 2", (string)sim.ExecuteScalar("select object_definition(object_id('dbo.p'))")!);
    }

    [TestMethod]
    public void AlterView_NoObjectAlter_Raises3701NamingView()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create view dbo.v as select 1 as c");
        var ex = sim.AssertSqlError("execute as user = 'u'; exec('alter view dbo.v as select 2 as c')", 3701);
        AreEqual("Cannot alter the view 'v', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void AlterFunction_NoObjectAlter_Raises3701NamingFunction()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create function dbo.f() returns int as begin return 1 end");
        var ex = sim.AssertSqlError(
            "execute as user = 'u'; exec('alter function dbo.f() returns int as begin return 2 end')", 3701);
        AreEqual("Cannot alter the function 'f', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void CreateOrAlterProcedure_OverExisting_NeedsObjectAlterNotCreatePermission()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create procedure dbo.p as select 1");
        // The db-scope CREATE PROCEDURE permission does not admit a replacement.
        _ = sim.ExecuteNonQuery("grant create procedure to u");
        _ = sim.AssertSqlError("execute as user = 'u'; exec('create or alter procedure dbo.p as select 2')", 3701);
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.p to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; exec('create or alter procedure dbo.p as select 2')");
    }

    [TestMethod]
    public void CreateOrAlterProcedure_OverMissingName_TakesTheCreateGate()
    {
        var ex = Seeded().AssertSqlError(
            "execute as user = 'u'; exec('create or alter procedure dbo.fresh as select 1')", 262);
        AreEqual((byte)18, ex.State);
        AreEqual("CREATE PROCEDURE permission denied in database 'simulated'.", ex.Message);
    }

    // ---- DROP of each object kind ----

    [TestMethod]
    public void DropView_NoAuthority_Raises3701NamingView()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create view dbo.v as select 1 as c");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop view dbo.v", 3701);
        AreEqual((byte)14, ex.Class);
        AreEqual((byte)20, ex.State);
        AreEqual("Cannot drop the view 'v', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropProcedure_ObjectAlterInsufficient_ObjectControlSuffices()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create procedure dbo.p as select 1");
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.p to u");
        _ = sim.AssertSqlError("execute as user = 'u'; drop procedure dbo.p", 3701);
        _ = sim.ExecuteNonQuery("grant control on object::dbo.p to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop procedure dbo.p");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.procedures where name = 'p'"));
    }

    [TestMethod]
    public void DropTable_ObjectControl_Suffices()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant control on object::dbo.t1 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop table dbo.t1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't1'"));
    }

    [TestMethod]
    public void DropFunction_NoAuthority_Raises3701NamingFunction()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create function dbo.f() returns int as begin return 1 end");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop function dbo.f", 3701);
        AreEqual("Cannot drop the function 'f', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropSequence_NoAuthority_Raises3701NamingSequence()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create sequence dbo.s as int start with 1");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop sequence dbo.s", 3701);
        AreEqual("Cannot drop the sequence 's', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropSynonym_NoAuthority_Raises3701NamingSynonym()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create synonym dbo.syn for dbo.t1");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop synonym dbo.syn", 3701);
        AreEqual("Cannot drop the synonym 'syn', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropSynonym_WithSchemaAlter_Succeeds()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create synonym dbo.syn for dbo.t1");
        _ = sim.ExecuteNonQuery("grant alter on schema::dbo to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop synonym dbo.syn");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.synonyms"));
    }

    [TestMethod]
    public void DropTrigger_NoParentAlter_Raises3701NamingTrigger()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create trigger dbo.tr on dbo.t1 after insert as select 1");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop trigger dbo.tr", 3701);
        AreEqual("Cannot drop the trigger 'tr', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropTrigger_WithParentTableAlter_Succeeds()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create trigger dbo.tr on dbo.t1 after insert as select 1");
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.t1 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop trigger dbo.tr");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'tr'"));
    }

    [TestMethod]
    public void DropAliasType_NoSchemaAlter_Raises218()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create type dbo.ty from int");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop type dbo.ty", 218);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)1, ex.State);
        AreEqual("Could not find the type 'dbo.ty'. Either it does not exist or you do not have the necessary permission.", ex.Message);
    }

    [TestMethod]
    public void DropTableType_WithSchemaAlter_Succeeds()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create type dbo.tt as table (id int)");
        _ = sim.AssertSqlError("execute as user = 'u'; drop type dbo.tt", 218);
        _ = sim.ExecuteNonQuery("grant alter on schema::dbo to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop type dbo.tt");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.table_types where name = 'tt'"));
    }

    [TestMethod]
    public void DropXmlSchemaCollection_NoSchemaAlter_Raises15151()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create xml schema collection dbo.xsc as N'<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><xsd:element name=\"a\" type=\"xsd:string\"/></xsd:schema>'");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop xml schema collection dbo.xsc", 15151);
        AreEqual((byte)16, ex.Class);
        AreEqual("Cannot drop the xml schema collection 'xsc', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropSchema_SchemaAlterInsufficient_AlterAnySchemaSuffices()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create schema s1");
        _ = sim.ExecuteNonQuery("grant alter on schema::s1 to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop schema s1", 15151);
        AreEqual("Cannot drop the schema 's1', because it does not exist or you do not have permission.", ex.Message);
        _ = sim.ExecuteNonQuery("grant alter any schema to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop schema s1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.schemas where name = 's1'"));
    }

    [TestMethod]
    public void DropSchema_SchemaControl_Suffices()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create schema s2");
        _ = sim.ExecuteNonQuery("grant control on schema::s2 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop schema s2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.schemas where name = 's2'"));
    }

    [TestMethod]
    public void DropIndex_NoTableAlter_Raises1088State9()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; drop index ix_t1 on dbo.t1", 1088);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)9, ex.State);
        AreEqual("Cannot find the object \"dbo.t1.ix_t1\" because it does not exist or you do not have permissions.", ex.Message);
    }

    [TestMethod]
    public void DropIndex_WithTableAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.t1 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop index ix_t1 on dbo.t1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where name = 'ix_t1'"));
    }

    // ---- CREATE of the remaining kinds ----

    [TestMethod]
    public void CreateIndex_NoTableAlter_Raises1088State12()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; create index ix_v on dbo.t1 (v)", 1088);
        AreEqual((byte)12, ex.State);
        AreEqual("Cannot find the object \"dbo.t1\" because it does not exist or you do not have permissions.", ex.Message);
    }

    [TestMethod]
    public void CreateIndex_WithTableAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.t1 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create index ix_v on dbo.t1 (v)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.indexes where name = 'ix_v'"));
    }

    [TestMethod]
    public void CreateTrigger_NoParentAlter_Raises2104()
    {
        var ex = Seeded().AssertSqlError(
            "execute as user = 'u'; exec('create trigger dbo.tr on dbo.t1 after insert as select 1')", 2104);
        AreEqual((byte)14, ex.Class);
        AreEqual((byte)1, ex.State);
        AreEqual("Cannot create the trigger 'dbo.tr', because you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void CreateTrigger_WithParentTableAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.t1 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; exec('create trigger dbo.tr on dbo.t1 after insert as select 1')");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'tr'"));
    }

    [TestMethod]
    public void CreateDatabaseDdlTrigger_NeedsAlterAnyDatabaseDdlTrigger()
    {
        var sim = Seeded();
        var ex = sim.AssertSqlError(
            "execute as user = 'u'; exec('create trigger ddltr on database for create_table as select 1')", 2104);
        AreEqual("Cannot create the trigger 'ddltr', because you do not have permission.", ex.Message);
        _ = sim.ExecuteNonQuery("grant alter any database ddl trigger to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; exec('create trigger ddltr on database for create_table as select 1')");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'ddltr'"));
    }

    [TestMethod]
    public void CreateSynonym_NeedsCreateSynonymThenSchemaAlter()
    {
        var sim = Seeded();
        var denied = sim.AssertSqlError("execute as user = 'u'; create synonym dbo.syn for dbo.t1", 262);
        AreEqual((byte)1, denied.State);
        AreEqual("CREATE SYNONYM permission denied in database 'simulated'.", denied.Message);
        _ = sim.ExecuteNonQuery("grant create synonym to u");
        _ = sim.AssertSqlError("execute as user = 'u'; create synonym dbo.syn for dbo.t1", 2760);
        _ = sim.ExecuteNonQuery("grant alter on schema::dbo to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create synonym dbo.syn for dbo.t1");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.synonyms"));
    }

    [TestMethod]
    public void CreateType_NeedsCreateTypeThenSchemaAlter()
    {
        var sim = Seeded();
        var denied = sim.AssertSqlError("execute as user = 'u'; create type dbo.ty from int", 262);
        AreEqual("CREATE TYPE permission denied in database 'simulated'.", denied.Message);
        _ = sim.ExecuteNonQuery("grant create type to u");
        _ = sim.AssertSqlError("execute as user = 'u'; create type dbo.ty from int", 2760);
        _ = sim.ExecuteNonQuery("grant alter on schema::dbo to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create type dbo.ty from int");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.types where name = 'ty'"));
    }

    [TestMethod]
    public void CreateXmlSchemaCollection_ChecksSchemaAlterBeforeTheCreatePermission()
    {
        const string Ddl =
            "exec('create xml schema collection dbo.xsc as N''<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
            + "<xsd:element name=\"a\" type=\"xsd:string\"/></xsd:schema>''')";
        var sim = Seeded();
        // Unlike CREATE TABLE / SYNONYM / TYPE, the schema half runs first here.
        var schemaDenied = sim.AssertSqlError($"execute as user = 'u'; {Ddl}", 15151);
        AreEqual("Cannot alter the schema 'dbo', because it does not exist or you do not have permission.", schemaDenied.Message);
        _ = sim.ExecuteNonQuery("grant alter on schema::dbo to u");
        var createDenied = sim.AssertSqlError($"execute as user = 'u'; {Ddl}", 262);
        AreEqual("CREATE XML SCHEMA COLLECTION permission denied in database 'simulated'.", createDenied.Message);
        _ = sim.ExecuteNonQuery("grant create xml schema collection to u");
        _ = sim.ExecuteNonQuery($"execute as user = 'u'; {Ddl}");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.xml_schema_collections where name = 'xsc'"));
    }

    [TestMethod]
    public void CreateFullTextCatalog_NeedsCreateFullTextCatalog_Raises7666()
    {
        var sim = Seeded();
        var ex = sim.AssertSqlError("execute as user = 'u'; create fulltext catalog ftc", 7666);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)2, ex.State);
        AreEqual("User does not have permission to perform this action.", ex.Message);
        _ = sim.ExecuteNonQuery("grant create fulltext catalog to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create fulltext catalog ftc");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.fulltext_catalogs"));
    }

    [TestMethod]
    public void DropFullTextCatalog_NeedsDatabaseAlter_Raises7641()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create fulltext catalog ftc");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop fulltext catalog ftc", 7641);
        AreEqual((byte)5, ex.State);
        AreEqual("Full-Text catalog 'ftc' does not exist in database 'simulated' or user does not have permission to perform this action.", ex.Message);
    }

    // ---- ALTER of the remaining kinds ----

    [TestMethod]
    public void AlterSequence_NoObjectAlter_Raises15151()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create sequence dbo.s as int start with 1");
        var ex = sim.AssertSqlError("execute as user = 'u'; alter sequence dbo.s restart with 5", 15151);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)1, ex.State);
        AreEqual("Cannot alter the sequence 's', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void AlterSequence_WithObjectAlter_Succeeds()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create sequence dbo.s as int start with 1");
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.s to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; alter sequence dbo.s restart with 5");
        AreEqual(5, sim.ExecuteScalar("select next value for dbo.s"));
    }

    [TestMethod]
    public void AlterIndex_NoTableAlter_Raises1088State9()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; alter index ix_t1 on dbo.t1 rebuild", 1088);
        AreEqual((byte)9, ex.State);
        AreEqual("Cannot find the object \"dbo.t1\" because it does not exist or you do not have permissions.", ex.Message);
    }

    [TestMethod]
    public void AlterRole_AddMember_NeedsAlterAnyRole_Raises15151State2()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create role r1", "create user member1 without login");
        var ex = sim.AssertSqlError("execute as user = 'u'; alter role r1 add member member1", 15151);
        AreEqual((byte)2, ex.State);
        AreEqual("Cannot alter the role 'r1', because it does not exist or you do not have permission.", ex.Message);
        _ = sim.ExecuteNonQuery("grant alter any role to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; alter role r1 add member member1");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_role_members"));
    }

    [TestMethod]
    public void DropRole_NeedsAlterAnyRole_Raises15151State1()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create role r2");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop role r2", 15151);
        AreEqual((byte)1, ex.State);
        AreEqual("Cannot drop the role 'r2', because it does not exist or you do not have permission.", ex.Message);
        _ = sim.ExecuteNonQuery("grant alter any role to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop role r2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'r2'"));
    }

    [TestMethod]
    public void DbDdlAdmin_DoesNotCarryRoleDdl()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create role r3", "alter role db_ddladmin add member u");
        _ = sim.AssertSqlError("execute as user = 'u'; drop role r3", 15151);
    }

    [TestMethod]
    public void AlterSchemaTransfer_NeedsTargetSchemaAlterThenObjectControl()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create schema s3");
        var schemaDenied = sim.AssertSqlError("execute as user = 'u'; alter schema s3 transfer dbo.t1", 15151);
        AreEqual("Cannot alter the schema 's3', because it does not exist or you do not have permission.", schemaDenied.Message);
        _ = sim.ExecuteNonQuery("grant alter on schema::s3 to u");
        var objectDenied = sim.AssertSqlError("execute as user = 'u'; alter schema s3 transfer dbo.t1", 15151);
        AreEqual("Cannot transfer the object 't1', because it does not exist or you do not have permission.", objectDenied.Message);
        // ALTER on the *source* schema is not enough either — real wants CONTROL.
        _ = sim.ExecuteNonQuery("grant alter on schema::dbo to u");
        _ = sim.AssertSqlError("execute as user = 'u'; alter schema s3 transfer dbo.t1", 15151);
        _ = sim.ExecuteNonQuery("grant control on object::dbo.t1 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; alter schema s3 transfer dbo.t1");
        AreEqual("s3", sim.ExecuteScalar("select schema_name(schema_id) from sys.tables where name = 't1'"));
    }

    [TestMethod]
    public void AlterDatabase_NoDatabaseAlter_Raises5011State9()
    {
        var ex = Seeded().AssertSqlError(
            "execute as user = 'u'; alter database simulated set recursive_triggers on", 5011);
        AreEqual((byte)14, ex.Class);
        AreEqual((byte)9, ex.State);
        AreEqual(
            "User does not have permission to alter database 'simulated', the database does not exist, or the database is not in a state that allows access checks.",
            ex.Message);
    }

    [TestMethod]
    public void AlterDatabase_WithDatabaseAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; alter database simulated set recursive_triggers on");
        IsTrue((bool)sim.ExecuteScalar("select is_recursive_triggers_on from sys.databases where name = 'simulated'")!);
    }

    [TestMethod]
    public void DbDdlAdmin_DoesNotCarryAlterDatabase()
    {
        var sim = Seeded();
        sim.ExecuteBatches("alter role db_ddladmin add member u");
        _ = sim.AssertSqlError("execute as user = 'u'; alter database simulated set recursive_triggers on", 5011);
    }

    // ---- sp_rename ----

    [TestMethod]
    public void SpRename_NoObjectAlter_Raises15225()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; exec sp_rename 'dbo.t1', 't9'", 15225);
        AreEqual((byte)11, ex.Class);
        Contains("No item by the name of 'dbo.t1' could be found", ex.Message);
    }

    [TestMethod]
    public void SpRename_WithObjectAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.t1 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; exec sp_rename 'dbo.t1', 't9'");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.tables where name = 't9'"));
    }

    // ---- db_ddladmin covering, and the dbo bypass ----

    [TestMethod]
    public void DbDdlAdmin_PassesTheObjectAndSchemaDdlGates()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create procedure dbo.p as select 1",
            "create schema s4",
            "alter role db_ddladmin add member u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; exec('alter procedure dbo.p as select 2')");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create synonym dbo.syn for dbo.t1");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create type dbo.ty from int");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create index ix_v on dbo.t1 (v)");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop schema s4");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop procedure dbo.p");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.procedures where name = 'p'"));
    }

    [TestMethod]
    public void Dbo_BypassesEveryNewGate()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create procedure dbo.p as select 1",
            "create sequence dbo.s as int start with 1",
            "create schema s5");
        _ = sim.ExecuteNonQuery("exec('alter procedure dbo.p as select 2')");
        _ = sim.ExecuteNonQuery("alter sequence dbo.s restart with 3");
        _ = sim.ExecuteNonQuery("alter schema s5 transfer dbo.t1");
        _ = sim.ExecuteNonQuery("alter database simulated set recursive_triggers on");
        _ = sim.ExecuteNonQuery("drop procedure dbo.p");
        _ = sim.ExecuteNonQuery("drop sequence dbo.s");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.sequences"));
    }

    // ---- CREATE / DROP DATABASE answer at server scope ----

    /// <summary>
    /// A restricted session on <c>simulated</c>, reached through the login
    /// registry so the session carries a real server principal (the server-scope
    /// gates read the login, not the database user).
    /// </summary>
    private static SimulatedDbConnection RestrictedLogin(Simulation simulation)
    {
        simulation.ExecuteBatches(
            "create login app with password = 'S3cret!Pass'",
            "create user app for login app");
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = "User ID=app;Password=S3cret!Pass";
        connection.Open();
        return connection;
    }

    [TestMethod]
    public void CreateDatabase_NoServerAuthority_Raises262NamingMaster()
    {
        var sim = Seeded();
        using var connection = RestrictedLogin(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("create database extra").ExecuteNonQuery());
        AreEqual(262, ex.Number);
        AreEqual((byte)14, ex.Class);
        AreEqual((byte)1, ex.State);
        AreEqual("CREATE DATABASE permission denied in database 'master'.", ex.Message);
    }

    [TestMethod]
    public void CreateAndDropDatabase_DbCreatorMembership_Succeeds()
    {
        var sim = Seeded();
        using var connection = RestrictedLogin(sim);
        _ = sim.ExecuteNonQuery("alter server role dbcreator add member app");
        _ = connection.CreateCommand("create database extra").ExecuteNonQuery();
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.databases where name = 'extra'"));
        _ = connection.CreateCommand("drop database extra").ExecuteNonQuery();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.databases where name = 'extra'"));
    }

    [TestMethod]
    public void DropDatabase_NoServerAuthority_Raises3701Severity11State2()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create database extra2");
        using var connection = RestrictedLogin(sim);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("drop database extra2").ExecuteNonQuery());
        AreEqual(3701, ex.Number);
        AreEqual((byte)11, ex.Class);
        AreEqual((byte)2, ex.State);
        AreEqual("Cannot drop the database 'extra2', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void CreateDatabase_AlterAnyDatabaseCoversCreateAnyDatabase()
    {
        var sim = Seeded();
        using var connection = RestrictedLogin(sim);
        _ = sim.ExecuteNonQuery("use master; grant alter any database to app");
        _ = connection.CreateCommand("create database extra3").ExecuteNonQuery();
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.databases where name = 'extra3'"));
    }

    // ---- Ownership chaining: a module body's DDL isn't gated ----

    [TestMethod]
    public void ModuleBody_DdlIsOwnershipChained()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create procedure dbo.dropper as drop table dbo.t1",
            "grant execute on object::dbo.dropper to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; exec dbo.dropper");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't1'"));
    }
}
