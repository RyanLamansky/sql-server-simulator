using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>sysname</c> alias and the catalog's name columns as SQL Server 2025
/// types them (probed 2026-10-18): the alias survives a conversion, a variable
/// and a promotion between two of them but not a string function or a
/// <c>COLLATE</c> naming another collation, each catalog view declares its name
/// columns <c>sysname</c> or <c>nvarchar(128)</c> as real's does, and a
/// database-scoped view's names take the collation of the database they are
/// read in, so they compare with that database's columns and conflict with the
/// server's.
/// </summary>
[TestClass]
public sealed class CatalogNameTypeTests
{
    private static object? UserTypeOf(Simulation sim, string query, string column, string database = "simulated") => sim.ExecuteScalar($"""
        use {database};
        select isnull(cast(user_type_id as varchar(10)), 'none') + '/' + system_type_name + '/' + collation_name
        from sys.dm_exec_describe_first_result_set(N'{query.Replace("'", "''", StringComparison.Ordinal)}', null, 0) where name = '{column}'
        """);

    [TestMethod]
    [DataRow("select cast(N'a' as sysname) c from sys.objects", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("declare @n sysname; select @n c", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("declare @n sysname; select isnull(@n, N'y') c", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("declare @n sysname; select coalesce(name, @n) c from sys.objects", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select name collate SQL_Latin1_General_CP1_CI_AS c from sys.objects", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select name collate Latin1_General_BIN c from sys.objects", "none/nvarchar(128)/Latin1_General_BIN")]
    [DataRow("select upper(name) c from sys.objects", "none/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select left(name, 3) c from sys.objects", "none/nvarchar(3)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select substring(name, 1, 2) c from sys.objects", "none/nvarchar(2)/SQL_Latin1_General_CP1_CI_AS")]
    public void Sysname_KeepsItsAliasWhereRealDoes(string query, string described)
        => AreEqual(described, UserTypeOf(new Simulation(), query, "c"));

    [TestMethod]
    [DataRow("select * from sys.sysservers", "srvname", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select * from sys.sysservers", "providername", "none/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select * from INFORMATION_SCHEMA.COLUMN_PRIVILEGES", "COLUMN_NAME", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select * from sys.all_columns", "name", "none/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select * from sys.columns", "encryption_algorithm_name", "256/nvarchar(128)/Latin1_General_CI_AS_KS_WS")]
    [DataRow("select * from sys.services", "name", "256/nvarchar(128)/Latin1_General_BIN")]
    [DataRow("select * from sys.syscolumns", "collation", "256/nvarchar(128)/Latin1_General_CI_AI")]
    [DataRow("select * from sys.server_event_sessions", "name", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select * from sys.trusted_assemblies", "created_by", "none/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select * from sys.dm_exec_describe_first_result_set(N'select 1', null, 0)", "name", "none/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select * from sys.fn_my_permissions(null, 'database')", "entity_name", "256/nvarchar(128)/Latin1_General_CI_AI")]
    public void CatalogNameColumns_AreTypedAsRealTypesThem(string query, string column, string described)
        => AreEqual(described, UserTypeOf(new Simulation(), query, column));

    /// <summary>
    /// <c>sys.all_columns</c> lists a union view's sysname columns as
    /// <c>sysname</c> though they read as <c>nvarchar(128)</c>, and
    /// <c>sys.server_event_sessions.name</c> the reverse.
    /// </summary>
    [TestMethod]
    public void AllColumns_ListsTheTypesRealsCatalogLists()
        => AreEqual("all_columns.name 256;server_event_sessions.name 231;sysservers.srvname 256", new Simulation().ExecuteScalar("""
            select string_agg(object_name(object_id) + '.' + name + ' ' + cast(user_type_id as varchar(10)), ';') within group (order by object_name(object_id))
            from sys.all_columns
            where object_id in (object_id('sys.all_columns'), object_id('sys.server_event_sessions'), object_id('sys.sysservers'))
                and name in ('name', 'srvname')
            """));

    [TestMethod]
    [DataRow("select * from sys.objects", "name", "256/nvarchar(128)/Latin1_General_CS_AS")]
    [DataRow("select * from INFORMATION_SCHEMA.TABLES", "TABLE_NAME", "256/nvarchar(128)/Latin1_General_CS_AS")]
    [DataRow("select * from sys.sql_modules", "definition", "none/nvarchar(max)/Latin1_General_CS_AS")]
    [DataRow("select * from sys.objects", "type_desc", "none/nvarchar(60)/Latin1_General_CI_AS_KS_WS")]
    [DataRow("select * from sys.databases", "name", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select * from master.sys.objects", "name", "256/nvarchar(128)/SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("declare @n sysname; select @n c", "c", "256/nvarchar(128)/Latin1_General_CS_AS")]
    [DataRow("declare @n nvarchar(10); select @n c", "c", "none/nvarchar(10)/Latin1_General_CS_AS")]
    [DataRow("select * from sys.dm_sql_referenced_entities('dbo.x', 'OBJECT')", "referenced_entity_name", "none/nvarchar(128)/Latin1_General_CS_AS")]
    public void DatabaseScopedNames_TakeTheDatabasesCollation(string query, string column, string described)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database cs collate Latin1_General_CS_AS");
        AreEqual(described, UserTypeOf(sim, query, column, "cs"));
    }

    [TestMethod]
    public void CatalogNames_CompareInTheDatabasesCollation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database cs collate Latin1_General_CS_AS");
        _ = sim.ExecuteNonQuery("""
            use cs;
            create table zt (n sysname, c nvarchar(128) collate Latin1_General_CI_AS);
            insert zt values (N'sysrowsets', N'SYSROWSETS');
            """);
        AreEqual(1, sim.ExecuteScalar("use cs; select count(*) from sys.objects o join zt on o.name = zt.n"));
        AreEqual(0, sim.ExecuteScalar("use cs; select count(*) from sys.objects where name = N'SYSROWSETS'"));
        AreEqual(0, sim.ExecuteScalar("use cs; declare @v sysname = N'SYSROWSETS'; select count(*) from sys.objects where name = @v"));
        AreEqual(0, sim.ExecuteScalar("use cs; declare @a nvarchar(10) = N'a', @b nvarchar(10) = N'A'; select case when @a = @b then 1 else 0 end"));
        _ = sim.AssertSqlError("use cs; select count(*) from sys.objects o join zt on o.name = zt.c", 468);
        _ = sim.AssertSqlError("use cs; select count(*) from sys.databases d join sys.objects o on d.name = o.name", 468);
        _ = sim.AssertSqlError("use cs; select count(*) from sys.objects o join master.sys.objects m on o.name = m.name", 468);
    }
}
