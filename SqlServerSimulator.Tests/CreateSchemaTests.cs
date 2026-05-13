using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>CREATE SCHEMA</c> + schema-qualified table access. Schemas
/// live in <see cref="Database.Schemas"/>; unqualified references resolve
/// through the default <c>dbo</c> entry; two-part <c>schema.table</c> and
/// three-part <c>db.schema.table</c> route through the named schema. Probed
/// against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class CreateSchemaTests
{
    [TestMethod]
    public void CreateSchema_BareForm_Succeeds()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create schema audit;
            create table audit.t (id int);
            insert audit.t values (1);
            select count(*) from audit.t
            """));

    [TestMethod]
    public void CreateSchema_Duplicate_Msg2714()
        => new Simulation().AssertSqlError(
            "create schema audit; create schema audit",
            2714,
            "There is already an object named 'audit' in the database.");

    [TestMethod]
    public void CreateSchema_DuplicateCaseInsensitive_Msg2714()
        => new Simulation().AssertSqlError(
            "create schema audit; create schema AUDIT",
            2714);

    [TestMethod]
    public void CreateSchema_Dbo_Msg2760()
        => new Simulation().AssertSqlError(
            "create schema dbo",
            2760,
            "The specified schema name \"dbo\" either does not exist or you do not have permission to use it.");

    [TestMethod]
    public void CreateSchema_Sys_Msg2760()
        => new Simulation().AssertSqlError("create schema sys", 2760);

    [TestMethod]
    public void CreateSchema_InformationSchema_Msg2760()
        => new Simulation().AssertSqlError("create schema INFORMATION_SCHEMA", 2760);

    [TestMethod]
    public void CreateSchema_Authorization_NotSupported()
        => Throws<NotSupportedException>(() =>
            new Simulation().ExecuteNonQuery("create schema audit authorization dbo"));

    /// <summary>
    /// Real SQL Server treats trailing tokens after <c>CREATE SCHEMA</c> as
    /// the <c>&lt;schema_element&gt;</c> list (inline CREATE TABLE / VIEW /
    /// GRANT) — the simulator doesn't implement schema_element grammar, but
    /// trailing statement-starting keywords parse as the next statement in the
    /// batch (CREATE / SELECT / INSERT / etc. are statement boundaries that
    /// the dispatch loop picks up cleanly). Net effect is the same as the
    /// common idiom; only the AUTHORIZATION clause and unusual non-boundary
    /// trailers raise <see cref="NotSupportedException"/>.
    /// </summary>
    [TestMethod]
    public void CreateSchema_FollowedByCreateTable_DispatchesAsTwoStatements()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create schema audit
            create table audit.t (id int)
            insert audit.t values (1);
            select count(*) from audit.t
            """));

    [TestMethod]
    public void TwoPartName_Select_Works()
        => AreEqual(42, new Simulation().ExecuteScalar("""
            create schema audit;
            create table audit.t (id int);
            insert audit.t values (42);
            select id from audit.t
            """));

    [TestMethod]
    public void TwoPartName_Insert_Works()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create schema audit;
            create table audit.t (id int);
            insert audit.t values (1);
            insert into audit.t values (2);
            select max(id) from audit.t
            """));

    [TestMethod]
    public void TwoPartName_Update_Works()
        => AreEqual(99, new Simulation().ExecuteScalar("""
            create schema audit;
            create table audit.t (id int);
            insert audit.t values (1);
            update audit.t set id = 99;
            select id from audit.t
            """));

    [TestMethod]
    public void TwoPartName_Delete_Works()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create schema audit;
            create table audit.t (id int);
            insert audit.t values (1), (2), (3);
            delete from audit.t;
            select count(*) from audit.t
            """));

    [TestMethod]
    public void TwoPartName_DropTable_Works()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create schema audit;
            create table audit.t (id int);
            drop table audit.t
            """;
        _ = cmd.ExecuteNonQuery();
        cmd.CommandText = "create table audit.t (id int)";
        _ = cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void TwoPartName_Truncate_Works()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create schema audit;
            create table audit.t (id int);
            insert audit.t values (1), (2);
            truncate table audit.t;
            select count(*) from audit.t
            """));

    [TestMethod]
    public void TwoPartName_SelectInto_Works()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create schema staging;
            create table dbo.src (id int);
            insert dbo.src values (1), (2), (3);
            select * into staging.dest from dbo.src;
            select count(*) from staging.dest
            """));

    [TestMethod]
    public void ThreePartName_CorrectDatabase_Works()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            create schema audit;
            create table audit.t (id int);
            insert audit.t values (7);
            select id from simulated.audit.t
            """));

    [TestMethod]
    public void ThreePartName_WrongDatabase_Msg208()
        => new Simulation().AssertSqlError("""
            create schema audit;
            create table audit.t (id int);
            select * from baddb.audit.t
            """, 208);

    [TestMethod]
    public void SchemaDoesNotExist_Select_Msg208()
        => new Simulation().AssertSqlError(
            "select * from badschema.t",
            208,
            "Invalid object name 'badschema.t'.");

    [TestMethod]
    public void SchemaDoesNotExist_CreateTable_Msg2760()
        => new Simulation().AssertSqlError(
            "create table badschema.t (id int)",
            2760,
            "The specified schema name \"badschema\" either does not exist or you do not have permission to use it.");

    [TestMethod]
    public void SchemaDoesNotExist_Insert_Msg208()
        => new Simulation().AssertSqlError(
            "insert badschema.t values (1)",
            208);

    [TestMethod]
    public void SchemaDoesNotExist_DropTable_Msg3701()
        => new Simulation().AssertSqlError(
            "drop table badschema.t",
            3701,
            "Cannot drop the table 'badschema.t', because it does not exist or you do not have permission.");

    [TestMethod]
    public void SchemaDoesNotExist_DropTableIfExists_NoError()
        => _ = new Simulation().ExecuteNonQuery("drop table if exists badschema.t");

    /// <summary>
    /// Probe-confirmed: Msg 4701 carries only the leaf in the message,
    /// unlike Msg 208 / 3701 which embed the full qualified name.
    /// </summary>
    [TestMethod]
    public void SchemaDoesNotExist_Truncate_Msg4701_LeafNameOnly()
        => new Simulation().AssertSqlError(
            "truncate table badschema.t",
            4701,
            "Cannot find the object \"t\" because it does not exist or you do not have permissions.");

    [TestMethod]
    public void SchemaExistsTableDoesNot_Select_Msg208()
        => new Simulation().AssertSqlError("""
            create schema audit;
            select * from audit.nope
            """, 208, "Invalid object name 'audit.nope'.");

    [TestMethod]
    public void IsolatedSchemas_SameTableName()
    {
        using var reader = new Simulation().ExecuteReader("""
            create schema audit;
            create schema staging;
            create table audit.t (id int);
            create table staging.t (id int);
            insert audit.t values (1), (2);
            insert staging.t values (100);
            select (select count(*) from audit.t) as a, (select count(*) from staging.t) as s
            """);
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
    }

    [TestMethod]
    public void UnqualifiedReference_ResolvesToDbo()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create schema audit;
            create table dbo.t (id int);
            create table audit.t (id int);
            insert dbo.t values (5);
            insert audit.t values (999);
            select id from t
            """));

    [TestMethod]
    public void ExplicitDbo_Equivalent_ToBare()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create table dbo.t (id int);
            insert dbo.t values (5);
            select id from t
            """));

    [TestMethod]
    public void CrossSchemaJoin_Works()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create schema audit;
            create table dbo.users (id int, name nvarchar(50));
            create table audit.entries (user_id int, action nvarchar(50));
            insert dbo.users values (1, 'alice'), (2, 'bob');
            insert audit.entries values (1, 'login'), (1, 'logout'), (2, 'login');
            select count(*) from dbo.users u inner join audit.entries e on u.id = e.user_id where u.id = 1
            """));

    [TestMethod]
    public void CreateSchema_PersistsAcrossBatches()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "create schema audit";
        _ = cmd.ExecuteNonQuery();
        cmd.CommandText = "create table audit.t (id int); insert audit.t values (1); select count(*) from audit.t";
        AreEqual(1, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void DropSchema_Empty_Succeeds()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "create schema audit; drop schema audit; select schema_id('audit')";
        AreEqual(DBNull.Value, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void DropSchema_Missing_Msg15151()
        => new Simulation().AssertSqlError(
            "drop schema nope",
            15151,
            "Cannot drop the schema 'nope', because it does not exist or you do not have permission.");

    [TestMethod]
    public void DropSchema_IfExistsMissing_NoOp()
        => AreEqual(1, new Simulation().ExecuteScalar("drop schema if exists nope; select 1"));

    [TestMethod]
    public void DropSchema_Dbo_Msg15150()
        => new Simulation().AssertSqlError(
            "drop schema dbo",
            15150,
            "Cannot drop the schema 'dbo'.");

    [TestMethod]
    public void DropSchema_Sys_Msg15150()
        => new Simulation().AssertSqlError(
            "drop schema sys",
            15150);

    [TestMethod]
    public void DropSchema_InformationSchema_Msg15150()
        => new Simulation().AssertSqlError(
            "drop schema INFORMATION_SCHEMA",
            15150);

    [TestMethod]
    public void DropSchema_NotEmpty_Msg3729()
    {
        var ex = new Simulation().AssertSqlError(
            "create schema audit; create table audit.t (id int); drop schema audit",
            3729);
        IsTrue(ex.Message.StartsWith("Cannot drop schema 'audit' because it is being referenced by object", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AlterSchema_Transfer_TableMoves()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create schema src;
            create schema dst;
            create table src.t (id int);
            insert src.t values (1), (2);
            alter schema dst transfer src.t;
            select s.name from sys.tables t join sys.schemas s on s.schema_id = t.schema_id where t.name = 't'
            """;
        AreEqual("dst", cmd.ExecuteScalar());
    }

    [TestMethod]
    public void AlterSchema_Transfer_ExplicitObjectPrefix()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create schema src;
            create schema dst;
            create table src.t (id int);
            alter schema dst transfer object::src.t;
            select count(*) from dst.t
            """;
        AreEqual(0, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void AlterSchema_Transfer_NameCollision_Msg15530()
        => new Simulation().AssertSqlError(
            """
            create schema src;
            create schema dst;
            create table src.t (id int);
            create table dst.t (id int);
            alter schema dst transfer src.t
            """,
            15530,
            "The object with name \"t\" already exists.");

    [TestMethod]
    public void AlterSchema_Transfer_MissingSource_Msg15151()
        => new Simulation().AssertSqlError(
            "create schema src; create schema dst; alter schema dst transfer src.nope",
            15151,
            "Cannot find the object 'nope', because it does not exist or you do not have permission.");

    [TestMethod]
    public void AlterSchema_Transfer_MissingDest_Msg15151()
        => new Simulation().AssertSqlError(
            "create schema src; create table src.t (id int); alter schema nope transfer src.t",
            15151,
            "Cannot alter the schema 'nope', because it does not exist or you do not have permission.");

    [TestMethod]
    public void AlterSchema_Transfer_TypeMoves()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create schema src;
            create schema dst;
            create type src.MyType as table (id int);
            alter schema dst transfer type::src.MyType;
            select s.name from sys.types tt join sys.schemas s on s.schema_id = tt.schema_id where tt.name = 'MyType'
            """;
        AreEqual("dst", cmd.ExecuteScalar());
    }

    [TestMethod]
    public void AlterSchema_Transfer_SameSchema_NoOp()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create schema src;
            create table src.t (id int);
            alter schema src transfer src.t;
            select count(*) from src.t
            """;
        AreEqual(0, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void AlterSchema_Transfer_TriggerDirect_Msg15347()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "create schema src; create schema dst; create table src.t (id int)";
            _ = setup.ExecuteNonQuery();
        }
        using (var trg = conn.CreateCommand())
        {
            trg.CommandText = "create trigger src.tr on src.t after insert as select 1";
            _ = trg.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "alter schema dst transfer src.tr";
        var ex = Throws<DbException>(() => cmd.ExecuteNonQuery());
        AreEqual("15347", ex.Data["HelpLink.EvtID"]);
        AreEqual("Cannot transfer an object that is owned by a parent object.", ex.Message);
    }

    [TestMethod]
    public void AlterSchema_Transfer_TriggerFollowsParent()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "create schema src; create schema dst; create table src.t (id int)";
            _ = setup.ExecuteNonQuery();
        }
        using (var trg = conn.CreateCommand())
        {
            trg.CommandText = "create trigger src.tr on src.t after insert as select 1";
            _ = trg.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "alter schema dst transfer src.t; select s.name from sys.triggers t join sys.objects o on o.object_id = t.parent_id join sys.schemas s on s.schema_id = o.schema_id where t.name = 'tr'";
        AreEqual("dst", cmd.ExecuteScalar());
    }

    [TestMethod]
    public void AlterSchema_Transfer_View_Works()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create schema src;
            create schema dst;
            create table dbo.base (id int);
            insert dbo.base values (1), (2);
            """;
        _ = cmd.ExecuteNonQuery();
        cmd.CommandText = "create view src.v as select id from dbo.base";
        _ = cmd.ExecuteNonQuery();
        cmd.CommandText = "alter schema dst transfer src.v; select count(*) from dst.v";
        AreEqual(2, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void AlterSchema_Transfer_Sequence_Works()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create schema src;
            create schema dst;
            create sequence src.s start with 100;
            alter schema dst transfer src.s;
            select next value for dst.s
            """;
        AreEqual(100L, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void DropSchema_AfterTransferringOut_Succeeds()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            create schema src;
            create schema dst;
            create table src.t (id int);
            alter schema dst transfer src.t;
            drop schema src;
            select schema_id('src')
            """;
        AreEqual(DBNull.Value, cmd.ExecuteScalar());
    }
}
