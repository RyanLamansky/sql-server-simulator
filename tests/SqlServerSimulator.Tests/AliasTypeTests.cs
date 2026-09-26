using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Exercises scalar user-defined alias types (UDDTs) created via
/// <c>CREATE TYPE schema.name FROM &lt;builtin&gt;[(N[, S])] [NULL | NOT NULL]</c>.
/// Alias types are the second bacpac prerequisite (after the database-options
/// expansion); AdventureWorks2025 declares 6 of them (<c>AccountNumber</c>,
/// <c>Flag</c>, <c>Name</c>, <c>NameStyle</c>, <c>OrderNumber</c>,
/// <c>Phone</c>). Behavior probed against SQL Server 2025 (2026-05-14).
/// </summary>
[TestClass]
public class AliasTypeTests
{
    /// <summary>
    /// A type created in the batch that uses it doesn't exist yet when real
    /// compiles the batch, so nothing in the batch runs (probed 2026-09-24
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void CreateTypeAndUseItInOneBatch_RaisesMsg2715()
    {
        var sim = new Simulation();
        sim.AssertSqlError(
            "CREATE TYPE dbo.Probe FROM int; CREATE TABLE t (c dbo.Probe)",
            2715,
            "Column, parameter, or variable #1: Cannot find data type dbo.Probe.");
        AreEqual(0, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.types WHERE name = 'Probe'"));
    }

    [TestMethod]
    public void CreateAlias_NotNull_Then_ColumnInheritsNotNullByDefault()
    {
        // Probe-confirmed: column with no explicit nullability marker inherits
        // NOT NULL from the alias.
        var ex = WithType("CREATE TYPE dbo.AccountNumber FROM nvarchar(15) NOT NULL").AssertSqlError("""
            CREATE TABLE t (c dbo.AccountNumber);
            INSERT INTO t (c) VALUES (NULL);
            """, 515);
        Contains("does not allow nulls", ex.Message);
    }

    /// <summary>
    /// Bare CREATE TYPE (no NULL/NOT NULL marker) → alias is nullable; the
    /// column declaration without its own marker stays nullable.
    /// </summary>
    [TestMethod]
    public void CreateAlias_Bare_ColumnIsNullable()
        => AreEqual(1, WithType("CREATE TYPE dbo.Probe FROM int").ExecuteScalar("""
            CREATE TABLE t (c dbo.Probe);
            INSERT INTO t (c) VALUES (NULL);
            SELECT COUNT(*) FROM t WHERE c IS NULL
            """));

    [TestMethod]
    public void CreateAlias_ExplicitNullKeyword_AliasIsNullable()
        => AreEqual(1, WithType("CREATE TYPE dbo.Probe FROM int NULL").ExecuteScalar("""
            CREATE TABLE t (c dbo.Probe);
            INSERT INTO t (c) VALUES (NULL);
            SELECT COUNT(*) FROM t WHERE c IS NULL
            """));

    /// <summary>
    /// Column-level explicit NULL overrides alias-defined NOT NULL — probe-
    /// confirmed (real SQL Server treats the column-side marker as
    /// authoritative when present).
    /// </summary>
    [TestMethod]
    public void ColumnNullOverride_TrumpsAliasNotNull()
        => AreEqual(1, WithType("CREATE TYPE dbo.Tight FROM int NOT NULL").ExecuteScalar("""
            CREATE TABLE t (c dbo.Tight NULL);
            INSERT INTO t (c) VALUES (NULL);
            SELECT COUNT(*) FROM t WHERE c IS NULL
            """));

    /// <summary>
    /// Probe-confirmed: alias can be referenced without the schema qualifier
    /// when it's in dbo (the default schema).
    /// </summary>
    [TestMethod]
    public void UnqualifiedReference_Works()
        => AreEqual(42, WithType("CREATE TYPE dbo.Probe FROM int").ExecuteScalar("""
            CREATE TABLE t (c Probe);
            INSERT INTO t (c) VALUES (42);
            SELECT c FROM t
            """));

    [TestMethod]
    public void QualifiedReference_Works()
        => AreEqual(42, WithType("CREATE TYPE dbo.Probe FROM int").ExecuteScalar("""
            CREATE TABLE t (c [dbo].[Probe]);
            INSERT INTO t (c) VALUES (42);
            SELECT c FROM t
            """));

    [TestMethod]
    public void LengthAtUsageSite_RaisesMsg2716()
    {
        // Probe-confirmed verbatim: Msg 2716 St 3 with the alias's fully-
        // qualified name in the message.
        var ex = WithType("CREATE TYPE dbo.Name FROM nvarchar(50) NOT NULL").AssertSqlError("""
            CREATE TABLE t (c dbo.Name(100));
            """, 2716);
        Contains("dbo.Name", ex.Message);
        Contains("Cannot specify a column width", ex.Message);
    }

    [TestMethod]
    public void DuplicateTypeName_RaisesMsg219()
    {
        var ex = new Simulation().AssertSqlError("""
            CREATE TYPE dbo.Probe FROM int;
            CREATE TYPE dbo.Probe FROM int;
            """, 219);
        Contains("dbo.Probe", ex.Message);
    }

    [TestMethod]
    public void AliasName_CollidesWithTableType_RaisesMsg219()
    {
        // Alias + table types share one type-name namespace.
        var ex = new Simulation().AssertSqlError("""
            CREATE TYPE dbo.Shared AS TABLE (id int);
            CREATE TYPE dbo.Shared FROM int;
            """, 219);
        Contains("dbo.Shared", ex.Message);
    }

    [TestMethod]
    public void InvalidBaseType_RaisesMsg222()
    {
        var ex = new Simulation().AssertSqlError(
            "CREATE TYPE dbo.Bogus FROM not_a_type",
            222);
        Contains("not_a_type", ex.Message);
        Contains("not a valid base type", ex.Message);
    }

    [TestMethod]
    public void DropType_OnAlias_Removes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TYPE dbo.Probe FROM int");
        AreEqual(1, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sys.types WHERE name = 'Probe' AND is_user_defined = 1"));
        _ = sim.ExecuteNonQuery("DROP TYPE dbo.Probe");
        AreEqual(0, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sys.types WHERE name = 'Probe' AND is_user_defined = 1"));
    }

    [TestMethod]
    public void DropType_OnMissingAlias_RaisesMsg218()
    {
        _ = new Simulation().AssertSqlError("DROP TYPE dbo.NoSuchAlias", 218);
    }

    [TestMethod]
    public void DropType_IfExists_OnMissingAlias_Succeeds()
        => AreEqual(-1, new Simulation().ExecuteNonQuery("DROP TYPE IF EXISTS dbo.NoSuchAlias"));

    [TestMethod]
    public void SysTypes_Row_ShipsCorrectShape()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TYPE dbo.AccountNumber FROM nvarchar(15) NOT NULL");
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand(
            "SELECT system_type_id, is_user_defined, is_table_type, is_nullable FROM sys.types WHERE name = 'AccountNumber'");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        // nvarchar's system_type_id is 231 per probe.
        AreEqual((byte)231, reader.GetByte(0));
        IsTrue(reader.GetBoolean(1));   // is_user_defined
        IsFalse(reader.GetBoolean(2));  // is_table_type
        IsFalse(reader.GetBoolean(3));  // is_nullable (alias was NOT NULL)
    }

    [TestMethod]
    public void SysTypes_BareAlias_IsNullable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TYPE dbo.Probe FROM int");
        IsTrue((bool)sim.ExecuteScalar(
            "SELECT is_nullable FROM sys.types WHERE name = 'Probe'")!);
    }

    [TestMethod]
    public void Declare_AliasTypedVariable_Works()
        => AreEqual(42, WithType("CREATE TYPE dbo.Probe FROM int").ExecuteScalar("""
            DECLARE @v dbo.Probe;
            SET @v = 42;
            SELECT @v
            """));

    [TestMethod]
    public void AdventureWorksAliasTypes_LoadSuccessfully()
    {
        // Smoke test for the AW alias-type set — all six should be declarable
        // and usable as column types end-to-end.
        var sim = WithType("""
            CREATE TYPE dbo.AccountNumber FROM nvarchar(15) NOT NULL;
            CREATE TYPE dbo.Flag FROM bit NOT NULL;
            CREATE TYPE dbo.Name FROM nvarchar(50) NOT NULL;
            CREATE TYPE dbo.NameStyle FROM bit NOT NULL;
            CREATE TYPE dbo.OrderNumber FROM nvarchar(25) NOT NULL;
            CREATE TYPE dbo.Phone FROM nvarchar(25);
            """);
        _ = sim.ExecuteNonQuery("""
            CREATE TABLE dbo.Customer (
                AccountNumber [dbo].[AccountNumber],
                Title [dbo].[Name],
                NameStyle [dbo].[NameStyle],
                Phone [dbo].[Phone]);
            INSERT INTO dbo.Customer (AccountNumber, Title, NameStyle, Phone)
            VALUES ('AW-001', 'Ms.', 0, '555-1234');
            """);
        AreEqual(1, sim.ExecuteScalar("SELECT COUNT(*) FROM dbo.Customer"));
        // Verify NOT NULL inheritance from alias: AccountNumber, Title,
        // NameStyle should all be NOT NULL; Phone is nullable.
        AreEqual(3, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Customer') AND is_nullable = 0"));
    }

    [TestMethod]
    public void CrossSchema_Alias_Resolves()
    {
        var sim = new Simulation().WithSchemas("HR");
        _ = sim.ExecuteNonQuery("CREATE TYPE HR.EmployeeId FROM int NOT NULL");
        _ = sim.ExecuteNonQuery("""
            CREATE TABLE HR.Employee (Id HR.EmployeeId);
            INSERT INTO HR.Employee (Id) VALUES (1);
            """);
        AreEqual(1, sim.ExecuteScalar("SELECT Id FROM HR.Employee"));
    }

    /// <summary>
    /// <c>sysname</c> is SQL Server's built-in alias for <c>nvarchar(128) NOT NULL</c>.
    /// Reachable as a bare keyword in column / parameter / DECLARE positions.
    /// </summary>
    [TestMethod]
    public void Sysname_AsColumnType_StoresNvarcharBytes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table o (id int primary key, name sysname);
            insert o values (1, N'my-object'), (2, N'another');
            """);
        AreEqual("my-object", simulation.ExecuteScalar("select name from o where id = 1"));
        AreEqual("another", simulation.ExecuteScalar("select name from o where id = 2"));
    }

    /// <summary><c>DECLARE @x sysname</c> creates a string-typed scalar variable.</summary>
    [TestMethod]
    public void Sysname_AsVariableType_Works()
        => AreEqual("hello", new Simulation().ExecuteScalar("""
            declare @x sysname = N'hello';
            select @x
            """));

    /// <summary>
    /// <c>sysname</c> as a procedure parameter type — the path that WWI's
    /// <c>AddRoleMemberIfNonexistent</c>, <c>CreateRoleIfNonexistent</c>,
    /// and <c>ReseedSequenceBeyondTableValues</c> all depend on.
    /// </summary>
    [TestMethod]
    public void Sysname_AsProcedureParameter_Works()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table msgs (id int identity primary key, body nvarchar(200))",
            "create procedure dbo.log_msg @text sysname as insert msgs (body) values (@text)");
        _ = simulation.ExecuteNonQuery("exec dbo.log_msg N'a sysname-typed parameter'");
        AreEqual("a sysname-typed parameter", simulation.ExecuteScalar("select body from msgs"));
    }

    /// <summary><c>sys.columns</c> reports the sysname type-name for a sysname-declared column (was nvarchar(128) before the keyword landed).</summary>
    [TestMethod]
    public void Sysname_SurfacesAs_sysname_InSysColumns()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, label sysname)");
        AreEqual("sysname", simulation.ExecuteScalar("""
            select ty.name from sys.columns c
            join sys.types ty on c.user_type_id = ty.user_type_id
            join sys.tables tab on c.object_id = tab.object_id
            where tab.name = 't' and c.name = 'label'
            """));
    }

    /// <summary>
    /// sysname is itself a system alias type declared NOT NULL, so a column
    /// or table-variable column that doesn't say otherwise refuses NULL
    /// (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void BareSysnameColumn_IsNotNull()
    {
        AreEqual("a:0;b:1;c:0", new Simulation().ExecuteScalar("""
            create table t (a sysname, b sysname null, c sysname not null);
            select string_agg(concat(name, ':', cast(is_nullable as bit)), ';') within group (order by column_id)
            from sys.columns where object_id = object_id('t')
            """));
        _ = TestHelpers.AssertSqlError("declare @t table (a sysname); insert @t values (null)", 515);
    }

    /// <summary>
    /// An alias-typed column or parameter keeps its alias in the catalog:
    /// user_type_id, TYPE_NAME, the ISO domain and user-defined-type columns
    /// and sp_help's Type (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void AliasTypedColumnsAndParameters_KeepTheirAlias()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type dbo.phone from varchar(20) null",
            "create table t (id int, p phone, q varchar(5))",
            "alter table t alter column q phone",
            "create procedure p @x phone as select 1",
            "create function f (@a phone) returns phone as begin return @a end");
        AreEqual("int,phone,phone", sim.ExecuteScalar(
            "select string_agg(type_name(user_type_id), ',') within group (order by column_id) from sys.columns where object_id = object_id('t')"));
        AreEqual("-,dbo.phone,dbo.phone", sim.ExecuteScalar(
            "select string_agg(isnull(domain_schema + '.' + domain_name, '-'), ',') within group (order by ordinal_position) from information_schema.columns where table_name = 't'"));
        AreEqual("phone,phone", sim.ExecuteScalar(
            "select string_agg(type_name(user_type_id), ',') within group (order by parameter_id) from sys.parameters where object_id = object_id('f')"));
        AreEqual("phone|varchar|20", sim.ExecuteScalar(
            "select concat_ws('|', user_defined_type_name, data_type, character_maximum_length) from information_schema.parameters where specific_name = 'p'"));
    }

    [TestMethod]
    public void SpHelp_NamesTheAlias()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.phone from varchar(20) null", "create table t (p phone)");
        using var reader = sim.ExecuteReader("exec sp_help 't'");
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("phone", reader.GetString(1));
    }

    [TestMethod]
    [DataRow("create table x2 (p phone)", "create table x1 (p phone)", "drop type phone", "x2")]
    [DataRow("create procedure dp @x phone as select 1", "create table x1 (a int)", "drop type dbo.phone", "dp")]
    public void DropType_WhileReferenced_IsMsg3732(string first, string second, string drop, string referencing)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.phone from varchar(20) null", first, second);
        sim.AssertSqlError(drop, 3732,
            $"Cannot drop type '{drop["drop type ".Length..]}' because it is being referenced by object '{referencing}'. There may be other objects that reference this type.");
    }

    [TestMethod]
    public void TempTable_CannotUseTheDatabasesAliasType()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.phone from varchar(20) null");
        sim.AssertSqlError("create table #t (p phone)", 2715, "Column, parameter, or variable #1: Cannot find data type phone.");
        AreEqual("1", sim.ExecuteScalar("declare @t table (p phone); insert @t values ('1'); select p from @t"));
    }

    [TestMethod]
    [DataRow("cast('1' as phone)", "Type phone is not a defined system type.", 2)]
    [DataRow("convert(dbo.phone, '1')", "Type dbo.phone is not a defined system type.", 2)]
    [DataRow("cast(1 as dbo.nosuch)", "Type dbo.nosuch is not a defined system type.", 1)]
    public void Cast_RefusesAnAliasType(string expression, string message, int state)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.phone from varchar(20) null");
        var exception = sim.AssertSqlError($"select {expression}", 243);
        AreEqual(message, exception.Errors[0].Message);
        AreEqual((byte)state, exception.State);
    }

    /// <summary>
    /// A projection keeps an alias it passes through unchanged — a view, a
    /// derived table, a CTE, a UNION, SELECT … INTO — and drops it once it
    /// computes anything; a #temp target has no alias types to keep it
    /// (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void Projections_CarryTheAliasTheyPassThrough()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type dbo.phone from varchar(20) null",
            "create table t (p phone, q int)",
            "create view v1 as select p, p + '' r, (select max(p) from t) m from t",
            "create view v2 as with c as (select p from v1) select d.p from (select p from c) d union all select p from t",
            "select p, p + '' r into t2 from t",
            "select top 0 p into #t from t");
        AreEqual("t2.p:phone,t2.r:varchar,v1.p:phone,v1.r:varchar,v1.m:phone,v2.p:phone", sim.ExecuteScalar("""
            select string_agg(concat(object_name(object_id), '.', name, ':', type_name(user_type_id)), ',') within group (order by object_name(object_id), column_id)
            from sys.columns where object_id in (object_id('v1'), object_id('v2'), object_id('t2'))
            """));
    }

    [TestMethod]
    [DataRow("p", "phone")]
    [DataRow("isnull(p, '')", "phone")]
    [DataRow("case when 1 = 1 then p end", "phone")]
    [DataRow("sum(c)", "code")]
    [DataRow("lag(p) over (order by c)", "phone")]
    [DataRow("@v", "phone")]
    [DataRow("coalesce(p, '')", "-")]
    [DataRow("c + 0", "-")]
    [DataRow("cast(p as varchar(20))", "-")]
    public void Describe_NamesTheUserType(string expression, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.phone from varchar(20) null", "create type dbo.code from int not null", "create table t (p phone, c code)");
        AreEqual(expected, sim.ExecuteScalar($"""
            select isnull(user_type_name, '-') from sys.dm_exec_describe_first_result_set(N'declare @v phone; select {expression.Replace("'", "''", StringComparison.Ordinal)} from t', null, 0)
            """));
    }
}
