using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A principal's <c>DEFAULT_SCHEMA</c>: an unqualified name searches it before
/// <c>dbo</c>, an unqualified <c>CREATE</c> lands in it, and a module body
/// searches its own schema instead for the names its queries bind, while its
/// DDL, <c>EXEC</c>, catalog functions and dynamic SQL resolve as the caller
/// does. Probed 2026-10-04 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DefaultSchemaTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation().WithSchemas("s", "s2", "s3");
        sim.ExecuteBatches(
            """
            create table dbo.t (x varchar(20)); insert dbo.t values ('dbo.t');
            create table s.t (x varchar(20)); insert s.t values ('s.t');
            create table dbo.t2 (x varchar(20)); insert dbo.t2 values ('dbo.t2');
            create table s2.t2 (x varchar(20)); insert s2.t2 values ('s2.t2');
            create table s.onlys (x varchar(20)); insert s.onlys values ('s.onlys');
            create sequence dbo.q start with 100;
            create sequence s.q start with 200;
            create sequence s2.q start with 300;
            create synonym dbo.sy for dbo.t2;
            create synonym s.sy for s.onlys;
            create type dbo.al from int;
            create type s.al from varchar(5);
            create type dbo.tt as table (d_col int);
            create type s.tt as table (s_col int);
            """,
            "create view dbo.v as select 'dbo.v' x",
            "create view s.v as select 's.v' x",
            "create procedure dbo.p as select 'dbo.p' x",
            "create procedure s.p as select 's.p' x",
            "create function dbo.tf() returns table as return select 'dbo.tf' x",
            "create function s.tf() returns table as return select 's.tf' x",
            """
            create user u_s without login with default_schema = s;
            create user u_s2 without login with default_schema = s2;
            create user u_d without login;
            create user u_nx without login with default_schema = nosuch;
            grant select, insert, update, delete, execute, references, view definition,
                create table, create view, create procedure, create function, create type, create synonym
                to u_s, u_s2, u_d, u_nx;
            grant alter on schema::dbo to u_s, u_s2, u_d, u_nx;
            grant alter on schema::s to u_s;
            grant alter on schema::s2 to u_s2;
            grant impersonate on user::u_d to u_s;
            """);
        return sim;
    }

    /// <summary>The first column of every row of every result set <paramref name="sql"/> returns, as strings.</summary>
    private static string?[] Values(Simulation sim, string sql)
    {
        using var connection = sim.CreateOpenConnection();
        return [.. Extensions.FirstColumn(connection, sql).Select(static value => value is DBNull or null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))];
    }

    private static string?[] As(Simulation sim, string user, string sql) =>
        Values(sim, $"execute as user = '{user}';\n{sql}\nrevert;");

    [TestMethod]
    public void UnqualifiedNames_SearchTheDefaultSchemaFirst()
        => CollectionAssert.AreEqual(
            new[] { "s.t", "dbo.t2", "s.onlys", "s.v", "s.p", "s.tf", "200", "s.onlys", "s.t" },
            As(Seeded(), "u_s", """
                select x from t;
                select x from t2;
                select x from onlys;
                select x from v;
                exec p;
                select x from tf();
                select next value for q;
                select x from sy;
                select x from simulated..t;
                """));

    [TestMethod]
    public void UndeclaredDefaultSchema_SearchesDbo()
        => CollectionAssert.AreEqual(
            new[] { "dbo.t", "dbo.v", "dbo.p", "100", "dbo" },
            As(Seeded(), "u_d", "select x from t; select x from v; exec p; select next value for q; select schema_name();"));

    [TestMethod]
    public void ObjectId_ResolvesThroughTheDefaultSchema()
        => CollectionAssert.AreEqual(
            new[] { "s", "dbo", "s", "s" },
            As(Seeded(), "u_s", "select object_schema_name(object_id('t')); select object_schema_name(object_id('t2')); select object_schema_name(object_id('tf')); select object_schema_name(object_id('q'));"));

    [TestMethod]
    public void SchemaNameAndId_ReadTheDefaultSchema()
    {
        var sim = Seeded();
        CollectionAssert.AreEqual(new[] { "s", "5" }, As(sim, "u_s", "select schema_name(); select schema_id();"));
        CollectionAssert.AreEqual(new string?[] { null, null }, As(sim, "u_nx", "select schema_name(); select schema_id();"));
    }

    /// <summary>
    /// An object of any kind in the default schema shadows <c>dbo</c>'s, so a
    /// reference expecting another kind fails rather than reaching past it.
    /// </summary>
    [TestMethod]
    public void DefaultSchemaObject_ShadowsDboAcrossKinds()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create procedure s.t2 as select 1");
        _ = sim.AssertSqlError("execute as user = 'u_s'; select x from t2", 208);
    }

    /// <summary>A denial on the default schema's object is not a reason to fall back to <c>dbo</c>'s.</summary>
    [TestMethod]
    public void DeniedDefaultSchemaObject_DoesNotFallBack()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("deny select on s.t to u_s");
        AreEqual(
            "The SELECT permission was denied on the object 't', database 'simulated', schema 's'.",
            sim.AssertSqlError("execute as user = 'u_s'; select x from t", 229).Errors[0].Message);
    }

    [TestMethod]
    public void NonexistentDefaultSchema_ReferencesFallBackToDbo()
        => CollectionAssert.AreEqual(new[] { "dbo.t" }, As(Seeded(), "u_nx", "select x from t;"));

    [TestMethod]
    public void UnqualifiedCreate_LandsInTheDefaultSchema()
    {
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        foreach (var statement in new[]
        {
            "execute as user = 'u_s'; create table newt (a int); revert;",
            "execute as user = 'u_s'; create sequence newq; revert;",
            "execute as user = 'u_s'; create synonym newsy for t; revert;",
            "execute as user = 'u_s'; create type newal from int; revert;",
            "execute as user = 'u_s'; select x into newi from t; revert;",
            "execute as user = 'u_s'; exec('create view newv as select x from t'); revert;",
            "execute as user = 'u_s'; exec('create procedure newp as select x from t'); revert;",
        })
        {
            using var command = connection.CreateCommand(statement);
            _ = command.ExecuteNonQuery();
        }
        CollectionAssert.AreEqual(
            new[] { "s.newi", "s.newp", "s.newq", "s.newsy", "s.newt", "s.newv" },
            Values(sim, "select schema_name(schema_id) + '.' + name from sys.objects where name like 'new%' order by name"));
        CollectionAssert.AreEqual(new[] { "s" }, Values(sim, "select schema_name(schema_id) from sys.types where name = 'newal'"));
        // The view's body bound through the view's schema as it was created.
        CollectionAssert.AreEqual(new[] { "s.t", "s.t" }, Values(sim, "select x from s.newv; exec s.newp;"));
    }

    /// <summary>
    /// Msg 2797 ends the batch for a table, and only the statement for a
    /// module, whose CREATE attributes it to the module.
    /// </summary>
    [TestMethod]
    public void UnqualifiedCreate_NonexistentDefaultSchema_IsMsg2797()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table dbo.done (a int)");
        using var connection = sim.CreateOpenConnection();
        var table = Throws<SimulatedSqlException>(() => connection.CreateCommand("execute as user = 'u_nx'; create table nxt (a int); insert dbo.done values (1);").ExecuteNonQuery());
        AreEqual(2797, table.Number);
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.done"));
        using var other = sim.CreateOpenConnection();
        var view = Throws<SimulatedSqlException>(() => other.CreateCommand("execute as user = 'u_nx'; exec('create view nxv as select 1 a'); insert dbo.done values (1);").ExecuteNonQuery());
        AreEqual(2797, view.Number);
        AreEqual("nxv", view.Errors[0].Procedure);
        AreEqual(1, sim.ExecuteScalar("select count(*) from dbo.done"));
    }

    /// <summary>The same statement text follows each EXECUTE AS as the batch runs.</summary>
    [TestMethod]
    public void ExecuteAs_MidBatch_SwitchesResolution()
        => CollectionAssert.AreEqual(
            new[] { "dbo.t", "s.t", "dbo.t", "s.t", "dbo.t" },
            Values(Seeded(), """
                select x from t;
                execute as user = 'u_s';
                select x from t;
                execute as user = 'u_d';
                select x from t;
                revert;
                select x from t;
                revert;
                select x from t;
                """));

    /// <summary>
    /// A declaration's type binds as the batch compiles, before its
    /// EXECUTE AS has run; dynamic SQL compiles as it runs.
    /// </summary>
    [TestMethod]
    public void DeclaredType_BindsAsTheBatchCompiles()
    {
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        using var reader = connection.CreateCommand("""
            execute as user = 'u_s';
            declare @t tt; insert @t values (1); select * from @t;
            exec('declare @t tt; insert @t values (1); select * from @t;');
            revert;
            """).ExecuteReader();
        AreEqual("d_col", reader.GetName(0));
        IsTrue(reader.NextResult());
        AreEqual("s_col", reader.GetName(0));
    }

    [TestMethod]
    public void AlterUser_DefaultSchema_ChangesResolution()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter user u_d with default_schema = s");
        CollectionAssert.AreEqual(new[] { "s", "s.t" }, As(sim, "u_d", "select schema_name(); select x from t;"));
        AreEqual("s", sim.ExecuteScalar("select default_schema_name from sys.database_principals where name = 'u_d'"));
    }

    [TestMethod]
    public void AlterUserDbo_DefaultSchema_IsMsg15150()
        => Seeded().AssertSqlError("alter user dbo with default_schema = s", 15150, "Cannot alter the user 'dbo'.");

    [TestMethod]
    public void ApplicationRole_ResolvesThroughItsDefaultSchema()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create application role ar with password = 'App!Pass123', default_schema = s; grant select, execute to ar");
        CollectionAssert.AreEqual(
            new[] { "s", "s.onlys", "s.p" },
            Values(sim, "exec sp_setapprole 'ar', 'App!Pass123'; select schema_name(); select x from onlys; exec p;"));
    }

    // ---- Module bodies ----

    private static Simulation WithModules()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create procedure s2.pt as select x from t2",
            "create procedure s3.pt as select x from t",
            "create procedure dbo.pt as select x from t",
            "create procedure s2.inner1 as select 's2.inner1'",
            "create procedure s.inner1 as select 's.inner1'",
            "create procedure dbo.inner1 as select 'dbo.inner1'",
            "create procedure s2.pexec as exec inner1",
            "create procedure s2.pmeta as select schema_name(), object_schema_name(object_id('t2')), next value for q",
            "create procedure s2.pdyn as exec('select x from t')",
            "create procedure s2.pcreate as begin create table newin (a int); select x into newinto from t2; end",
            "create procedure s2.ptypes as begin declare @t tt; insert @t values (1); select * from @t; end",
            "create view s2.vv as select x from t2",
            "create function s2.itf() returns table as return select x from t2",
            "create function s2.sf() returns varchar(20) as begin return (select x from t2) end",
            "create trigger s2.trg on s2.t2 after insert as select schema_name() + ' ' + (select top 1 x from t2 where x <> 'new')");
        return sim;
    }

    [TestMethod]
    public void ModuleBody_SearchesItsOwnSchemaForWhatItsQueriesRead()
    {
        var sim = WithModules();
        foreach (var user in new[] { "u_s", "u_d" })
        {
            CollectionAssert.AreEqual(
                new[] { "s2.t2", "dbo.t", "dbo.t", "s2.t2", "s2.t2", "s2.t2" },
                As(sim, user, "exec s2.pt; exec s3.pt; exec dbo.pt; select x from s2.vv; select x from s2.itf(); select s2.sf();"),
                user);
        }
    }

    [TestMethod]
    public void ModuleBody_ExecCatalogFunctionsAndDynamicSql_ResolveAsTheCaller()
    {
        var sim = WithModules();
        CollectionAssert.AreEqual(new[] { "s.inner1", "s", "dbo", "s.t" }, CallerResolution(sim, "u_s"));
        CollectionAssert.AreEqual(new[] { "dbo.inner1", "dbo", "dbo", "dbo.t" }, CallerResolution(sim, "u_d"));
    }

    /// <summary>The inner procedure an EXEC finds, SCHEMA_NAME(), OBJECT_ID's schema and dynamic SQL's table, from inside s2's modules.</summary>
    private static string?[] CallerResolution(Simulation sim, string user)
    {
        using var connection = sim.CreateOpenConnection();
        using var reader = connection.CreateCommand($"execute as user = '{user}'; exec s2.pexec; exec s2.pmeta; exec s2.pdyn; revert;").ExecuteReader();
        var values = new List<string?>();
        IsTrue(reader.Read());
        values.Add(reader.GetString(0));
        IsTrue(reader.NextResult() && reader.Read());
        values.Add(reader.GetString(0));
        values.Add(reader.GetString(1));
        // The sequence the body draws from is the module's own.
        IsGreaterThanOrEqualTo(300L, reader.GetInt64(2));
        IsTrue(reader.NextResult() && reader.Read());
        values.Add(reader.GetString(0));
        return [.. values];
    }

    [TestMethod]
    public void ModuleBody_UnqualifiedCreate_LandsInTheCallersDefaultSchema()
    {
        var sim = WithModules();
        _ = As(sim, "u_s", "exec s2.pcreate;");
        CollectionAssert.AreEqual(
            new[] { "s.newin", "s.newinto" },
            Values(sim, "select schema_name(schema_id) + '.' + name from sys.tables where name like 'newin%' order by name"));
        _ = sim.AssertSqlError("execute as user = 'u_nx'; exec s2.pcreate", 2797);
    }

    [TestMethod]
    public void ModuleBody_DeclaredTypes_BindThroughTheModuleSchema()
    {
        var sim = WithModules();
        _ = sim.ExecuteNonQuery("create type s2.tt as table (s2_col int)");
        using var connection = sim.CreateOpenConnection();
        using var reader = connection.CreateCommand("execute as user = 'u_s'; exec s2.ptypes; revert;").ExecuteReader();
        AreEqual("s2_col", reader.GetName(0));
    }

    /// <summary>A trigger's body binds through its table's schema; SCHEMA_NAME() reads the firing principal's.</summary>
    [TestMethod]
    public void TriggerBody_BindsThroughItsSchema()
        => CollectionAssert.AreEqual(new[] { "s s2.t2" }, As(WithModules(), "u_s", "insert s2.t2 values ('new');"));
}
