using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class ClrAssemblyTests
{
    private static Simulation ClrSimulation() => new() { EnableClr = true };

    private static string CreateSafeAssembly(string name = "sim_safe") =>
        $"CREATE ASSEMBLY {name} FROM {ClrAssemblyFixture.HexLiteral(ClrAssemblyFixture.Safe(name))} WITH PERMISSION_SET = SAFE";

    [TestMethod]
    [Description("The host opt-in is required; without it the assembly bytes are never loaded.")]
    public void CreateAssembly_WithoutEnableClr_Rejected()
    {
        var ex = Throws<NotSupportedException>(() => _ = new Simulation().ExecuteNonQuery(CreateSafeAssembly()));
        Contains("EnableClr", ex.Message);
    }

    [TestMethod]
    public void CreateAssembly_ThenScalarFunction_Invokes()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            "create function dbo.Doubler(@v int) returns int as external name sim_safe.UserDefinedFunctions.Doubler");
        AreEqual(84, sim.ExecuteScalar("select dbo.Doubler(42)"));
    }

    [TestMethod]
    [Description("NULL reaches the routine as SqlString.Null, not as a skipped call — real only short-circuits under RETURNS NULL ON NULL INPUT.")]
    public void ClrFunction_NullArgument_ReachesRoutine()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            "create function dbo.Shout(@s nvarchar(max)) returns nvarchar(max) as external name sim_safe.UserDefinedFunctions.Shout");
        AreEqual("hi!", sim.ExecuteScalar("select dbo.Shout(N'hi')"));
        AreEqual(1, sim.ExecuteScalar("select case when dbo.Shout(NULL) is null then 1 else 0 end"));
    }

    [TestMethod]
    public void ClrFunction_OverTableRows_Projects()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            "create table t (v int not null)",
            CreateSafeAssembly(),
            "create function dbo.Doubler(@v int) returns int as external name sim_safe.UserDefinedFunctions.Doubler");
        _ = sim.ExecuteNonQuery("insert t values (1), (2), (3)");
        AreEqual(12, sim.ExecuteScalar("select sum(dbo.Doubler(v)) from t"));
    }

    [TestMethod]
    [Description("A routine that throws surfaces as Msg 6522 naming the routine and the CLR exception.")]
    public void ClrFunction_Throwing_RaisesMsg6522()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            "create function dbo.Boom(@v int) returns int as external name sim_safe.UserDefinedFunctions.Boom");
        var ex = sim.AssertSqlError("select dbo.Boom(1)", 6522);
        Contains("user-defined routine or aggregate \"Boom\"", ex.Message);
        Contains("System.InvalidOperationException: boom", ex.Message);
    }

    [TestMethod]
    [Description("Static SAFE verification refuses a denied API before anything is loaded.")]
    public void CreateAssembly_FileIo_FailsVerification()
    {
        var bytes = ClrAssemblyFixture.WithFileIo();
        var ex = ClrSimulation().AssertSqlError(
            $"CREATE ASSEMBLY sim_fileio FROM {ClrAssemblyFixture.HexLiteral(bytes)} WITH PERMISSION_SET = SAFE", 6218);
        Contains("System.IO.File", ex.Message);
    }

    [TestMethod]
    [Description("EXTERNAL_ACCESS opts out of the SAFE API restrictions, matching real's permission ladder.")]
    public void CreateAssembly_FileIo_AllowedUnderExternalAccess()
    {
        var bytes = ClrAssemblyFixture.WithFileIo();
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery($"CREATE ASSEMBLY sim_fileio FROM {ClrAssemblyFixture.HexLiteral(bytes)} WITH PERMISSION_SET = EXTERNAL_ACCESS");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.assemblies where name = 'sim_fileio'"));
    }

    [TestMethod]
    [Description("A writable static in a SAFE assembly is Msg 6211 on real SQL Server.")]
    public void CreateAssembly_MutableStatic_RaisesMsg6211()
    {
        var bytes = ClrAssemblyFixture.WithMutableStatic();
        var ex = ClrSimulation().AssertSqlError(
            $"CREATE ASSEMBLY sim_static FROM {ClrAssemblyFixture.HexLiteral(bytes)} WITH PERMISSION_SET = SAFE", 6211);
        Contains("static field 'Counter'", ex.Message);
    }

    [TestMethod]
    public void CreateAssembly_NotAnAssembly_RaisesMsg6544()
        => ClrSimulation().AssertSqlError("CREATE ASSEMBLY junk FROM 0x4D5A9000 WITH PERMISSION_SET = SAFE", 6544);

    [TestMethod]
    public void CreateAssembly_Duplicate_RaisesMsg6246()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        var ex = sim.AssertSqlError(CreateSafeAssembly(), 6246);
        Contains("already exists in database", ex.Message);
    }

    [TestMethod]
    [Description("Same bytes under a second name is Msg 6285 — real matches on module MVID.")]
    public void CreateAssembly_SameMvidDifferentName_RaisesMsg6285()
    {
        var bytes = ClrAssemblyFixture.Safe();
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery($"CREATE ASSEMBLY first FROM {ClrAssemblyFixture.HexLiteral(bytes)}");
        _ = sim.AssertSqlError($"CREATE ASSEMBLY second FROM {ClrAssemblyFixture.HexLiteral(bytes)}", 6285);
    }

    [TestMethod]
    public void ExternalName_UnknownAssembly_RaisesMsg6528()
        => ClrSimulation().AssertSqlError(
            "create function dbo.f(@v int) returns int as external name nosuch.UserDefinedFunctions.Doubler", 6528);

    [TestMethod]
    public void ExternalName_UnknownType_RaisesMsg6505()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        _ = sim.AssertSqlError("create function dbo.f(@v int) returns int as external name sim_safe.NoSuchType.Doubler", 6505);
    }

    [TestMethod]
    public void ExternalName_UnknownMethod_RaisesMsg6506()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        _ = sim.AssertSqlError("create function dbo.f(@v int) returns int as external name sim_safe.UserDefinedFunctions.NoSuchMethod", 6506);
    }

    [TestMethod]
    public void ExternalName_ArityMismatch_RaisesMsg6550()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        _ = sim.AssertSqlError("create function dbo.f(@a int, @b int) returns int as external name sim_safe.UserDefinedFunctions.Doubler", 6550);
    }

    [TestMethod]
    [Description("bit does not bind to SqlInt32 — probe-confirmed the mapping is strict 1:1.")]
    public void ExternalName_ReturnTypeMismatch_RaisesMsg6551()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        _ = sim.AssertSqlError("create function dbo.f(@v int) returns bit as external name sim_safe.UserDefinedFunctions.Doubler", 6551);
    }

    [TestMethod]
    [Description("varchar does not bind to SqlString — only nvarchar / nchar do.")]
    public void ExternalName_ParameterTypeMismatch_RaisesMsg6552()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        var ex = sim.AssertSqlError("create function dbo.f(@s varchar(50)) returns nvarchar(max) as external name sim_safe.UserDefinedFunctions.Shout", 6552);
        Contains("parameter \"@s\"", ex.Message);
    }

    [TestMethod]
    public void DropAssembly_WithDependentFunction_RaisesMsg6590()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            "create function dbo.Doubler(@v int) returns int as external name sim_safe.UserDefinedFunctions.Doubler");
        var ex = sim.AssertSqlError("drop assembly sim_safe", 6590);
        Contains("referenced by object 'Doubler'", ex.Message);
    }

    [TestMethod]
    public void DropAssembly_Unreferenced_Succeeds()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        _ = sim.ExecuteNonQuery("drop assembly sim_safe");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.assemblies where name = 'sim_safe'"));
    }

    [TestMethod]
    public void DropAssembly_IfExists_Missing_NoOp()
        => AreEqual(0, ClrSimulation().ExecuteScalar(
            "drop assembly if exists nosuch; select count(*) from sys.assemblies where is_user_defined = 1"));

    [TestMethod]
    public void DropAssembly_Missing_RaisesMsg6528()
        => ClrSimulation().AssertSqlError("drop assembly nosuch", 6528);

    [TestMethod]
    [Description("Dropping then recreating under the same name works — the load context is released.")]
    public void DropAssembly_ThenRecreate_Succeeds()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            "create function dbo.Doubler(@v int) returns int as external name sim_safe.UserDefinedFunctions.Doubler");
        AreEqual(4, sim.ExecuteScalar("select dbo.Doubler(2)"));
        sim.ExecuteBatches("drop function dbo.Doubler", "drop assembly sim_safe", CreateSafeAssembly());
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.assemblies where name = 'sim_safe'"));
    }

    [TestMethod]
    public void SysAssemblies_ProjectsRegisteredAssembly()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        AreEqual("SAFE_ACCESS", sim.ExecuteScalar("select permission_set_desc from sys.assemblies where name = 'sim_safe'"));
        IsTrue((bool)sim.ExecuteScalar("select is_user_defined from sys.assemblies where name = 'sim_safe'")!);
        AreEqual("sim_safe, version=0.0.0.0, culture=neutral, publickeytoken=null, processorarchitecture=msil",
            sim.ExecuteScalar("select clr_name from sys.assemblies where name = 'sim_safe'"));
    }

    [TestMethod]
    [Description("The Microsoft.SqlServer.Types system row is present on real SQL Server even with no user assemblies.")]
    public void SysAssemblies_CarriesSystemRow()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select count(*) from sys.assemblies where name = 'Microsoft.SqlServer.Types' and is_user_defined = 0"));

    [TestMethod]
    public void SysAssemblyFiles_CarriesContent()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        AreEqual(
            ClrAssemblyFixture.Safe().Length,
            sim.ExecuteScalar("select cast(datalength(content) as int) from sys.assembly_files where name = 'sim_safe'"));
    }

    [TestMethod]
    public void SysAssemblyModules_CarriesBoundRoutine()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            "create function dbo.Doubler(@v int) returns int as external name sim_safe.UserDefinedFunctions.Doubler");
        AreEqual("UserDefinedFunctions", sim.ExecuteScalar("select assembly_class from sys.assembly_modules"));
        AreEqual("Doubler", sim.ExecuteScalar("select assembly_method from sys.assembly_modules"));
    }

    [TestMethod]
    [Description("A CLR scalar function is type FS with no sys.sql_modules row.")]
    public void SysObjects_ReportsClrScalarFunction()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            "create function dbo.Doubler(@v int) returns int as external name sim_safe.UserDefinedFunctions.Doubler");
        AreEqual("FS", sim.ExecuteScalar("select type from sys.objects where name = 'Doubler'"));
        AreEqual("CLR_SCALAR_FUNCTION", sim.ExecuteScalar("select type_desc from sys.objects where name = 'Doubler'"));
        AreEqual(0, sim.ExecuteScalar(
            "select count(*) from sys.sql_modules m join sys.objects o on o.object_id = m.object_id where o.name = 'Doubler'"));
    }

    [TestMethod]
    public void AssemblyProperty_ReportsManifestVersion()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        AreEqual(1, sim.ExecuteScalar("select assemblyproperty('sim_safe', 'VersionMajor')"));
        AreEqual(2, sim.ExecuteScalar("select assemblyproperty('sim_safe', 'VersionMinor')"));
        AreEqual(3, sim.ExecuteScalar("select assemblyproperty('sim_safe', 'VersionBuild')"));
        AreEqual(4, sim.ExecuteScalar("select assemblyproperty('sim_safe', 'VersionRevision')"));
        AreEqual("sim_safe", sim.ExecuteScalar("select assemblyproperty('sim_safe', 'SimpleName')"));
    }

    [TestMethod]
    public void AssemblyProperty_UnknownAssembly_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select assemblyproperty('nosuch', 'SimpleName')"));

    [TestMethod]
    [Description("mssql-django reads sys.configurations to decide whether to run sp_configure; EnableClr drives that value.")]
    public void SysConfigurations_ClrEnabledTracksEnableClr()
    {
        AreEqual(0, new Simulation().ExecuteScalar("select cast(value as int) from sys.configurations where name = 'clr enabled'"));
        AreEqual(1, ClrSimulation().ExecuteScalar("select cast(value as int) from sys.configurations where name = 'clr enabled'"));
    }
}
