using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Resolution + value tests for the catalog surface added to unblock SMO's
/// property-bag / scripting queries (the SMO API sweep campaign): the
/// sys.types-derived columns on <c>sys.table_types</c>, <c>sys.all_parameters</c>,
/// the encryption-key / server-permission empty views SMO's Login / User bags
/// LEFT JOIN, <c>sys.endpoints</c>, <c>sys.numbered_procedures</c>, and the
/// default-language columns on <c>sys.database_principals</c>. Shapes / values
/// probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SmoSweepCatalogTests
{
    /// <summary>
    /// sys.table_types carries the sys.types-inherited columns SMO's UDTT bag
    /// reads (tt.max_length / is_nullable / collation_name / principal_id), all
    /// constant for a table type: system_type_id 243, max_length -1, precision
    /// 0, scale 0, collation_name NULL, is_nullable 0, is_table_type 1.
    /// </summary>
    [TestMethod]
    public void TableTypes_ExposesSysTypesInheritedColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type MyList as table (id int, name nvarchar(50))");
        AreEqual((short)-1, sim.ExecuteScalar("select max_length from sys.table_types where name = 'MyList'"));
        AreEqual((byte)243, sim.ExecuteScalar("select system_type_id from sys.table_types where name = 'MyList'"));
        AreEqual((byte)0, sim.ExecuteScalar("select precision from sys.table_types where name = 'MyList'"));
        AreEqual((byte)0, sim.ExecuteScalar("select scale from sys.table_types where name = 'MyList'"));
        IsTrue((bool)sim.ExecuteScalar("select is_table_type from sys.table_types where name = 'MyList'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_nullable from sys.table_types where name = 'MyList'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_assembly_type from sys.table_types where name = 'MyList'")!);
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select collation_name from sys.table_types where name = 'MyList'"));
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select principal_id from sys.table_types where name = 'MyList'"));
    }

    /// <summary>
    /// sys.all_parameters shares sys.parameters' rows (user objects only) and
    /// exposes the columns SMO's function/proc scripting reads — including
    /// is_cursor_ref / has_default_value / is_xml_document / default_value /
    /// xml_collection_id, all at their non-parameterized defaults.
    /// </summary>
    [TestMethod]
    public void AllParameters_SharesParametersRows_WithFullShape()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.f1(@a int, @b nvarchar(10)) returns int as begin return @a end");
        // Scalar UDF: parameter_id=0 return row + 2 declared params.
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.all_parameters where object_id = object_id('dbo.f1')"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.parameters where object_id = object_id('dbo.f1')"));
        IsFalse((bool)sim.ExecuteScalar("select is_cursor_ref from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1")!);
        IsFalse((bool)sim.ExecuteScalar("select has_default_value from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1")!);
        IsFalse((bool)sim.ExecuteScalar("select is_xml_document from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1")!);
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select default_value from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1"));
        AreEqual(0, sim.ExecuteScalar("select xml_collection_id from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1"));
    }

    /// <summary>
    /// The encryption-key / server-permission / role-membership / numbered-proc
    /// views resolve and are always empty (unmodeled features). SMO's Login /
    /// User bags LEFT JOIN certificates / asymmetric_keys / credentials /
    /// server_permissions / server_role_members; sys.endpoints backs
    /// Server.Endpoints.
    /// </summary>
    [TestMethod]
    public void UnmodeledSecurityViews_ResolveEmpty()
    {
        var sim = new Simulation();
        foreach (var view in new[]
        {
            "asymmetric_keys", "certificates", "credentials", "server_permissions",
            "server_role_members", "numbered_procedures", "endpoints",
        })
        {
            AreEqual(0, sim.ExecuteScalar($"select count(*) from sys.{view}"), view);
        }
        // Login / User scripting reaches certificates / asymmetric_keys through
        // three-part master.sys names too.
        AreEqual(0, sim.ExecuteScalar("select count(*) from master.sys.certificates"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from master.sys.asymmetric_keys"));
    }

    /// <summary>
    /// sys.database_principals exposes the default-language columns SMO's User
    /// bag reads via ISNULL(u.default_language_lcid, -1) /
    /// ISNULL(u.default_language_name, N''); both are always NULL (untracked).
    /// </summary>
    [TestMethod]
    public void DatabasePrincipals_ExposesDefaultLanguageColumns()
    {
        var sim = new Simulation();
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select default_language_name from sys.database_principals where name = 'dbo'"));
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select default_language_lcid from sys.database_principals where name = 'dbo'"));
    }
}
