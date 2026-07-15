using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the legacy SQL-Server-2000 compatibility views (<c>sysobjects</c>
/// / <c>sysusers</c>) and <c>sys.system_objects</c> — the surface SSMS's
/// Database-Properties dialog reaches. <c>sysobjects</c> / <c>sysusers</c>
/// resolve unqualified (probe-confirmed against SQL Server 2025: bare
/// <c>sysobjects</c> works, bare <c>objects</c> raises Msg 208) and track live
/// metadata (<c>id = object_id</c>, <c>uid = schema_id</c>, joining
/// <c>sysusers.uid = principal_id</c>). <c>sys.system_objects</c> is an honest
/// projection that deliberately omits <c>sp_db_vardecimal_storage_format</c> so
/// SSMS's vardecimal probe skips the unmodeled proc and reads storage as OFF.
/// </summary>
[TestClass]
public sealed class LegacyCompatCatalogTests
{
    [TestMethod]
    public void Sysobjects_ResolvesUnqualified_TracksCreatedTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key)");
        AreEqual(
            sim.ExecuteScalar<int>("select object_id('t')"),
            sim.ExecuteScalar<int>("select id from sysobjects where name = 't'"));
        AreEqual(1, sim.ExecuteScalar<int>("select cast(uid as int) from sysobjects where name = 't'"));
        AreEqual("U ", (string?)sim.ExecuteScalar("select type from sysobjects where name = 't'"));
    }

    [TestMethod]
    public void Sysobjects_TracksCreatedProcedure_WithProcTypeCode()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p1 as select 1");
        AreEqual("P ", (string?)sim.ExecuteScalar("select type from sysobjects where name = 'p1'"));
    }

    [TestMethod]
    public void Sysobjects_ProjectsPrimaryKeyConstraint_WithLegacyKCode()
        => AreEqual("K ", (string?)new Simulation().ExecuteScalar("""
            create table t (id int not null constraint pk_t primary key);
            select type from sysobjects where name = 'pk_t'
            """));

    [TestMethod]
    public void Sysusers_HasDbo_AtSchemaIdOne_AsSqlUser()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar<int>("select cast(uid as int) from sysusers where name = 'dbo'"));
        AreEqual(1, sim.ExecuteScalar<int>("select issqluser from sysusers where name = 'dbo'"));
    }

    [TestMethod]
    public void Sysobjects_JoinsSysusers_OnUid_ForCreatedTable()
        => AreEqual("dbo", (string?)new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            select su.name from sysobjects so
            inner join sysusers su on so.uid = su.uid
            where so.name = 't' and so.type = 'U '
            """));

    /// <summary>
    /// SSMS's aggregate-function enumeration (harvested verbatim, minus the
    /// three-part db qualifiers): joins <c>sysobjects</c> → <c>sysusers</c> →
    /// <c>INFORMATION_SCHEMA.ROUTINES</c> on <c>SPECIFIC_NAME</c> and filters
    /// <c>type = N'AF'</c>. No CLR aggregates are modeled, so it must return
    /// zero rows cleanly — even with a scalar function present so the ROUTINES
    /// join is exercised.
    /// </summary>
    [TestMethod]
    public void AggregateFunctionEnumeration_ReturnsZeroRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function f1 (@x int) returns int as begin return @x end");
        using var reader = sim.ExecuteReader("""
            SELECT su.name, so.name, isr.DATA_TYPE
            FROM sysobjects so
            INNER JOIN sysusers su ON so.uid = su.uid
            INNER JOIN INFORMATION_SCHEMA.ROUTINES isr ON so.name = isr.SPECIFIC_NAME
            WHERE so.type = N'AF'
            """);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void InformationSchemaRoutines_ExposesSpecificName_MatchingRoutineName()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p1 as select 1");
        AreEqual("p1", (string?)sim.ExecuteScalar(
            "select SPECIFIC_NAME from INFORMATION_SCHEMA.ROUTINES where ROUTINE_NAME = 'p1'"));
    }

    [TestMethod]
    public void SystemObjects_OmitsVardecimalProc_ButListsCatalogViews()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar<int>(
            "select count(*) from sys.system_objects where name = N'sp_db_vardecimal_storage_format'"));
        IsGreaterThanOrEqualTo(1, sim.ExecuteScalar<int>(
            "select count(*) from sys.system_objects where name = N'tables'"));
    }

    [TestMethod]
    public void SystemObjects_ListsModeledSystemProcedures()
        => AreEqual(1, new Simulation().ExecuteScalar<int>(
            "select count(*) from sys.system_objects where name = N'sp_executesql'"));

    /// <summary>
    /// The Database-Properties vardecimal batch replays clean end-to-end: the
    /// <c>if exists (select … from sys.system_objects where name =
    /// N'sp_db_vardecimal_storage_format')</c> gate is false (honest absence),
    /// so the <c>insert … exec sys.sp_db_vardecimal_storage_format</c> never
    /// runs and storage reads as OFF (0).
    /// </summary>
    [TestMethod]
    public void VardecimalProbe_GateSkips_ReadsStorageAsOff()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            create table #tmp_vardec (dbname sysname null, vardecimal_enabled varchar(3) null)
            if exists (select o.object_id from sys.system_objects o where o.name = N'sp_db_vardecimal_storage_format')
            begin
              insert into #tmp_vardec exec sys.sp_db_vardecimal_storage_format
            end
            select cast(case when vardec.vardecimal_enabled = 'ON' then 1 else 0 end as int) as IsVarDecimalStorageFormatEnabled
            from master.sys.databases as dtb
            left outer join #tmp_vardec as vardec on dtb.database_id = db_id(vardec.dbname)
            where dtb.name = db_name()
            """));
}
