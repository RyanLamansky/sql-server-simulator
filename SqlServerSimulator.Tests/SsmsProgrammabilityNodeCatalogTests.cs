using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the catalog surface SSMS Object Explorer's Views and
/// Programmability nodes read via SMO: <c>sys.all_views</c> (create_date /
/// principal_id / is_ms_shipped / ledger_view_type), the <c>principal_id</c>
/// column SMO's function / procedure enumeration reads off
/// <c>sys.all_objects</c>, <c>sys.sequences</c> (create_date / principal_id —
/// the missing columns that left the Sequences node empty),
/// <c>sys.types.max_length</c> / precision / scale (User-Defined Data Types
/// node), <c>sys.assembly_types</c> (the three CLR system types), the empty
/// <c>sys.plan_guides</c> view, and <c>sys.database_files</c> (current-database
/// projection of <c>sys.master_files</c>, cross-database resolvable). Shapes /
/// values probed against SQL Server 2025 (2026-07-15).
/// </summary>
[TestClass]
public sealed class SsmsProgrammabilityNodeCatalogTests
{
    [TestMethod]
    public void AllViews_ExposesCreateDateNotNull()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view v as select 1 as c");
        AreEqual(1, sim.ExecuteScalar<int>(
            "select count(*) from sys.all_views where name = 'v' and create_date is not null and modify_date is not null"));
    }

    [TestMethod]
    public void AllViews_PrincipalIdIsNull()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view v as select 1 as c");
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from sys.all_views where name = 'v' and principal_id is null"));
    }

    [TestMethod]
    public void AllViews_IsMsShippedAndLedgerViewTypeAreZero()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view v as select 1 as c");
        AreEqual(0, sim.ExecuteScalar<int>("select cast(is_ms_shipped as int) from sys.all_views where name = 'v'"));
        AreEqual(0, sim.ExecuteScalar<int>("select cast(ledger_view_type as int) from sys.all_views where name = 'v'"));
    }

    [TestMethod]
    public void AllObjects_ExposesPrincipalId_NullForUserFunction()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.f(@x int) returns int as begin return @x + 1 end");
        AreEqual(1, sim.ExecuteScalar<int>(
            "select count(*) from sys.all_objects where name = 'f' and type = 'FN' and principal_id is null"));
    }

    [TestMethod]
    public void Objects_ExposesPrincipalId_NullForUserProcedure()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as select 1");
        AreEqual(1, sim.ExecuteScalar<int>(
            "select count(*) from sys.objects where name = 'p' and type = 'P' and principal_id is null"));
    }

    [TestMethod]
    public void Sequences_ExposeCreateDateAndPrincipalId()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create sequence dbo.s as int start with 1 increment by 1");
        AreEqual(1, sim.ExecuteScalar<int>(
            "select count(*) from sys.sequences where name = 's' and create_date is not null and principal_id is null"));
    }

    [TestMethod]
    public void Types_ScalarAliasReportsUnderlyingByteWidth()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type dbo.MyStr from nvarchar(50)");
        // nvarchar(50) byte width is 100; SMO halves it for the display Length.
        AreEqual(100, sim.ExecuteScalar<int>("select cast(max_length as int) from sys.types where name = 'MyStr'"));
    }

    [TestMethod]
    public void Types_ScalarAliasReportsPrecisionAndScale()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type dbo.Amt from decimal(10, 2)");
        AreEqual(9, sim.ExecuteScalar<int>("select cast(max_length as int) from sys.types where name = 'Amt'"));
        AreEqual(10, sim.ExecuteScalar<int>("select cast(precision as int) from sys.types where name = 'Amt'"));
        AreEqual(2, sim.ExecuteScalar<int>("select cast(scale as int) from sys.types where name = 'Amt'"));
    }

    [TestMethod]
    public void Types_SystemTypeMaxLengthMatchesSystypes()
    {
        var sim = new Simulation();
        AreEqual(4, sim.ExecuteScalar<int>("select cast(max_length as int) from sys.types where name = 'int'"));
        AreEqual(8000, sim.ExecuteScalar<int>("select cast(max_length as int) from sys.types where name = 'nvarchar'"));
        AreEqual(8016, sim.ExecuteScalar<int>("select cast(max_length as int) from sys.types where name = 'sql_variant'"));
    }

    [TestMethod]
    public void AssemblyTypes_ProjectsThreeClrSystemTypes()
        => AreEqual(3, new Simulation().ExecuteScalar<int>("select count(*) from sys.assembly_types"));

    [TestMethod]
    public void AssemblyTypes_HierarchyidShape()
    {
        var sim = new Simulation();
        AreEqual(892, sim.ExecuteScalar<int>("select cast(max_length as int) from sys.assembly_types where name = 'hierarchyid'"));
        AreEqual(240, sim.ExecuteScalar<int>("select cast(system_type_id as int) from sys.assembly_types where name = 'hierarchyid'"));
        AreEqual(128, sim.ExecuteScalar<int>("select user_type_id from sys.assembly_types where name = 'hierarchyid'"));
        AreEqual(4, sim.ExecuteScalar<int>("select schema_id from sys.assembly_types where name = 'hierarchyid'"));
        AreEqual(1, sim.ExecuteScalar<int>("select assembly_id from sys.assembly_types where name = 'hierarchyid'"));
        AreEqual(1, sim.ExecuteScalar<int>("select cast(is_assembly_type as int) from sys.assembly_types where name = 'hierarchyid'"));
        AreEqual(0, sim.ExecuteScalar<int>("select cast(is_user_defined as int) from sys.assembly_types where name = 'hierarchyid'"));
    }

    [TestMethod]
    public void AssemblyTypes_SpatialTypesReportMaxLengthMinusOne()
    {
        var sim = new Simulation();
        AreEqual(-1, sim.ExecuteScalar<int>("select cast(max_length as int) from sys.assembly_types where name = 'geometry'"));
        AreEqual(-1, sim.ExecuteScalar<int>("select cast(max_length as int) from sys.assembly_types where name = 'geography'"));
    }

    /// <summary>
    /// SMO's User-Defined Types node filters is_user_defined = 1; the three
    /// system CLR types all report 0, so the node lists nothing (matching a
    /// WWI database with no user CLR types).
    /// </summary>
    [TestMethod]
    public void AssemblyTypes_UserDefinedFilterReturnsEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("select count(*) from sys.assembly_types where is_user_defined = 1"));

    [TestMethod]
    public void PlanGuides_IsEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("select count(*) from sys.plan_guides"));

    [TestMethod]
    public void PlanGuides_ExposesNameAndIsDisabled()
    {
        using var reader = new Simulation().ExecuteReader("select name, is_disabled from sys.plan_guides");
        AreEqual(2, reader.FieldCount);
        AreEqual("name", reader.GetName(0));
        AreEqual("is_disabled", reader.GetName(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void DatabaseFiles_ReturnsDataAndLogRow()
    {
        var sim = new Simulation();
        AreEqual(2, sim.ExecuteScalar<int>("select count(*) from sys.database_files"));
        AreEqual(0, sim.ExecuteScalar<int>("select cast(type as int) from sys.database_files where file_id = 1"));
        AreEqual(1, sim.ExecuteScalar<int>("select cast(type as int) from sys.database_files where file_id = 2"));
    }

    [TestMethod]
    public void DatabaseFiles_AgreesWithMasterFilesOnName()
    {
        var sim = new Simulation();
        var fromDatabaseFiles = (string?)sim.ExecuteScalar("select name from sys.database_files where file_id = 1");
        var fromMasterFiles = (string?)sim.ExecuteScalar(
            "select name from sys.master_files where database_id = db_id() and file_id = 1");
        AreEqual(fromMasterFiles, fromDatabaseFiles);
        IsTrue(fromDatabaseFiles!.EndsWith("_Data", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DatabaseFiles_ResolvesCrossDatabaseThroughMaster()
    {
        var sim = new Simulation();
        AreEqual(2, sim.ExecuteScalar<int>("select count(*) from master.sys.database_files"));
        AreEqual("master_Data", (string?)sim.ExecuteScalar("select name from master.sys.database_files where file_id = 1"));
        AreEqual("master_Log", (string?)sim.ExecuteScalar("select name from master.sys.database_files where file_id = 2"));
    }

    [TestMethod]
    public void ViewsNode_TrimmedSmoQuery_ReturnsCreatedView()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view dbo.v as select 1 as c");
        using var reader = sim.ExecuteReader("""
            select v.name, schema_name(v.schema_id) as [Schema], v.create_date,
                   cast(isnull(v.ledger_view_type, 0) as tinyint) as ledger
            from sys.all_views as v
            left outer join sys.database_principals as sv
                on sv.principal_id = isnull(v.principal_id, objectproperty(v.object_id, 'OwnerId'))
            where v.type = N'V' and cast(v.is_ms_shipped as bit) = 0
            order by [Schema] asc, v.name asc
            """);
        IsTrue(reader.Read());
        AreEqual("v", reader.GetString(0));
        AreEqual("dbo", reader.GetString(1));
        IsFalse(reader.Read());
    }
}
