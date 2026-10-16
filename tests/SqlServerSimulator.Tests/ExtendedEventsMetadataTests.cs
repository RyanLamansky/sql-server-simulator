using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The Extended Events engine's catalog — <c>sys.dm_xe_packages</c>,
/// <c>sys.dm_xe_objects</c>, <c>sys.dm_xe_object_columns</c> and
/// <c>sys.dm_xe_map_values</c> — listing SQL Server 2025's own rows (dumped
/// 2026-10-16 from 17.0.4065.4).
/// </summary>
[TestClass]
public sealed class ExtendedEventsMetadataTests
{
    [TestMethod]
    public void Views_ListRealsRowCounts()
        => AreEqual("18,3864,32264,10275", new Simulation().ExecuteScalar("""
            select concat((select count(*) from sys.dm_xe_packages), ',', (select count(*) from sys.dm_xe_objects), ',',
                (select count(*) from sys.dm_xe_object_columns), ',', (select count(*) from sys.dm_xe_map_values))
            """));

    [TestMethod]
    public void Packages_CarryRealsGuidsInRealsOrder()
    {
        var sim = new Simulation();
        AreEqual("package0", sim.ExecuteScalar("select top 1 name from sys.dm_xe_packages"));
        AreEqual(new Guid("655FD93F-3364-40D5-B2BA-330F7FFB6491"), sim.ExecuteScalar("select guid from sys.dm_xe_packages where description = 'Extended events for Microsoft SQL Server' and module_guid = 'CE79811F-1A80-40E1-8F5D-7445A3F375E7'"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.dm_xe_packages where name = 'sqlserver'"));
        AreEqual("utility", sim.ExecuteScalar("select capabilities_desc from sys.dm_xe_packages where name = 'package0'"));
    }

    [TestMethod]
    public void Objects_JoinTheirPackages()
        => AreEqual("event:sqlserver:sds_visible no_block", new Simulation().ExecuteScalar("""
            select concat(o.object_type, ':', p.name, ':', o.capabilities_desc)
            from sys.dm_xe_objects o join sys.dm_xe_packages p on p.guid = o.package_guid
            where o.name = 'error_reported' and p.name = 'sqlserver'
            """));

    [TestMethod]
    public void ObjectColumns_ListAnEventsFieldsInColumnOrder()
        => AreEqual("error_number,severity,state,user_defined,category,destination,is_intercepted,message,nsp", new Simulation().ExecuteScalar("""
            select string_agg(c.name, ',') within group (order by c.column_id)
            from sys.dm_xe_object_columns c join sys.dm_xe_packages p on p.guid = c.object_package_guid
            where c.object_name = 'error_reported' and p.name = 'sqlserver' and c.column_type = 'data'
            """));

    [TestMethod]
    public void ObjectColumns_CustomizableColumnsCarryTheirDefaults()
        => AreEqual("filename::mandatory,max_file_size:1024:,max_rollover_files:5:", new Simulation().ExecuteScalar("""
            select string_agg(concat(name, ':', column_value, ':', capabilities_desc), ',') within group (order by column_id)
            from sys.dm_xe_object_columns where object_name = 'event_file' and column_id < 3
            """));

    [TestMethod]
    public void MapValues_ListAMapsKeys()
    {
        var sim = new Simulation();
        AreEqual("SERVER", sim.ExecuteScalar("select map_value from sys.dm_xe_map_values where name = 'error_category' and map_key = 2"));
        AreEqual(5, sim.ExecuteScalar("select map_key from sys.dm_xe_map_values where name = 'lock_mode' and map_value = 'X'"));
    }

    [TestMethod]
    public void Views_CarryRealsObjectIds()
        => AreEqual("-66559004,-15918666,-15626663,-917034300", new Simulation().ExecuteScalar("""
            select concat(object_id('sys.dm_xe_packages'), ',', object_id('sys.dm_xe_objects'), ',',
                object_id('sys.dm_xe_object_columns'), ',', object_id('sys.dm_xe_map_values'))
            """));

    [DataRow("sys.dm_xe_packages")]
    [DataRow("sys.dm_xe_objects")]
    [DataRow("sys.dm_xe_object_columns")]
    [DataRow("sys.dm_xe_map_values")]
    [TestMethod]
    public void Views_RestrictedWithoutPermission_Raise300(string view)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo");
        var ex = sim.AssertSqlError($"execute as login = 'lo'; select count(*) from {view}", 300);
        AreEqual(297, ex.Errors[1].Number);
    }
}
