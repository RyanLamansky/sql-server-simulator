using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class ClrAssemblyTests
{
    private static Simulation ClrSimulation() => ClrAssemblyFixture.TrustingSimulation();

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
    [DataRow("EXTERNAL_ACCESS")]
    [DataRow("UNSAFE")]
    [Description("The Linux server loads SAFE assemblies alone, refusing another permission set ahead of everything else (probed 2026-10-07 against SQL Server 2025).")]
    public void CreateAssembly_NotSafe_RaisesMsg10342(string permissionSet)
    {
        var ex = ClrSimulation().AssertSqlError(
            $"CREATE ASSEMBLY sim_net FROM {ClrAssemblyFixture.HexLiteral(ClrAssemblyFixture.NetTargeted())} WITH PERMISSION_SET = {permissionSet}", 10342);
        AreEqual("Assembly 'sim_net' cannot be loaded because this edition of SQL Server only supports SAFE assemblies.", ex.Errors[0].Message);
        AreEqual(100, ex.State);
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

    /// <summary>
    /// The whole T-SQL&#8596;<see cref="System.Data.SqlTypes"/> binding table,
    /// one row per pair, driven through an identity routine so a value has to
    /// convert in and back out to survive. The pairs that share a CLR type
    /// (<c>money</c> / <c>smallmoney</c>, <c>datetime</c> / <c>smalldatetime</c>,
    /// <c>varbinary</c> / <c>binary</c>) each get their own row, since the
    /// declared T-SQL type is what the return conversion reads.
    /// </summary>
    [TestMethod]
    [DataRow("nvarchar(20)", "SqlString", "N'hi'")]
    [DataRow("int", "SqlInt32", "42")]
    [DataRow("bigint", "SqlInt64", "cast(9000000000 as bigint)")]
    [DataRow("smallint", "SqlInt16", "cast(-42 as smallint)")]
    [DataRow("tinyint", "SqlByte", "cast(255 as tinyint)")]
    [DataRow("bit", "SqlBoolean", "cast(1 as bit)")]
    [DataRow("float", "SqlDouble", "cast(3.5 as float)")]
    [DataRow("real", "SqlSingle", "cast(1.25 as real)")]
    [DataRow("decimal(12,3)", "SqlDecimal", "cast(123.456 as decimal(12,3))")]
    [DataRow("money", "SqlMoney", "cast(19.99 as money)")]
    [DataRow("smallmoney", "SqlMoney", "cast(4.50 as smallmoney)")]
    [DataRow("datetime", "SqlDateTime", "cast('2021-06-15T13:30:00' as datetime)")]
    [DataRow("smalldatetime", "SqlDateTime", "cast('2024-03-15T13:45:00' as smalldatetime)")]
    [DataRow("varbinary(10)", "SqlBinary", "0x010203")]
    [DataRow("binary(3)", "SqlBinary", "cast(0x010203 as binary(3))")]
    [DataRow("uniqueidentifier", "SqlGuid", "cast('11111111-2222-3333-4444-555555555555' as uniqueidentifier)")]
    public void ClrFunction_TypeBinding_RoundTripsAndCarriesNull(string sqlType, string clrType, string literal)
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            $"create function dbo.Echo(@v {sqlType}) returns {sqlType} as external name sim_safe.UserDefinedFunctions.Echo{clrType}");
        AreEqual(1, sim.ExecuteScalar($"select case when dbo.Echo({literal}) = {literal} then 1 else 0 end"));
        AreEqual(1, sim.ExecuteScalar($"select case when dbo.Echo(cast(null as {sqlType})) is null then 1 else 0 end"));
    }

    /// <summary><c>xml</c> binds to <see cref="System.Data.SqlTypes.SqlXml"/>;
    /// it has its own test because <c>xml</c> is not a comparable type.</summary>
    [TestMethod]
    public void ClrFunction_XmlBinding_RoundTrips()
    {
        var sim = ClrSimulation();
        sim.ExecuteBatches(
            CreateSafeAssembly(),
            "create function dbo.Echo(@v xml) returns xml as external name sim_safe.UserDefinedFunctions.EchoSqlXml");
        AreEqual("<a>1</a>", sim.ExecuteScalar("select cast(dbo.Echo(cast(N'<a>1</a>' as xml)) as nvarchar(max))"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select dbo.Echo(cast(null as xml))"));
    }

    /// <summary>
    /// A declared type the binder has no CLR pair for fails the bind rather than
    /// marshalling something approximate.
    /// </summary>
    [TestMethod]
    public void ClrFunction_UnmarshalledType_RejectedAtCreate()
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
        _ = sim.AssertSqlError(
            "create function dbo.Echo(@v date) returns date as external name sim_safe.UserDefinedFunctions.EchoSqlDateTime", 6551);
    }

    private static List<(string Message, byte State, string Procedure)> ExecuteCollectingMessages(Simulation sim, string commandText)
    {
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<(string, byte, string)>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (var error in e.Errors)
                messages.Add((error.Message, error.State, error.Procedure));
        };
        using var command = connection.CreateCommand(commandText);
        _ = command.ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    [Description("A .NET Framework assembly referencing System.Data 4.0 loads, and its SqlContext answers inside a function by refusing the pipe, as real does.")]
    public void FrameworkAssembly_ScalarFunction_SeesNoPipe()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create function dbo.lg(@n int) returns nvarchar(3) as external name simclr.Funcs.Long",
            "create function dbo.pn() returns bit as external name simclr.Funcs.PipeIsNull");
        AreEqual("yy", sim.ExecuteScalar("select dbo.lg(2)"));
        var ex = sim.AssertSqlError("select dbo.pn()", 6522);
        AreEqual(2, ex.State);
        Contains("System.InvalidOperationException: Data access is not allowed in this context.", ex.Message);
    }

    /// <summary>
    /// A routine runs under SQL Server's CLR host culture, <c>en-US</c> with
    /// .NET Framework's formats, whatever the session's language or the
    /// host process's culture (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void FrameworkAssembly_RunsUnderTheServersEnUsCulture()
        => AreEqual("en-US|en-US|1/5/2026 1:04:05 PM|-1,234.50|($1,234.50)|50.00%|\u221E", ClrFrameworkFixture.Simulation(
                "create function dbo.cul() returns nvarchar(200) as external name simclr.Funcs.Culture")
            .ExecuteScalar("set language german; select dbo.cul()"));

    /// <summary>
    /// A base-library exception reads in .NET Framework's words: a number or
    /// date that won't parse isn't quoted back, a Base64 refusal ends in a
    /// space, and a failed unboxing is the bare cast message (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("ParseInt", "int", "N'x'", "System.FormatException: Input string was not in a correct format.\r\n")]
    [DataRow("Unbox", "int", "N'a'", "System.InvalidCastException: Specified cast is not valid.\r\n")]
    [DataRow("ParseDate", "datetime", "N'notadate'", "System.FormatException: The string was not recognized as a valid DateTime. There is an unknown word starting at index 0.\r\n")]
    [DataRow("ParseDateExact", "datetime", "N'2020/01/01'", "System.FormatException: String was not recognized as a valid DateTime.\r\n")]
    [DataRow("Base64", "varbinary(100)", "N'%%%'", "illegal character among the padding characters. \r\n")]
    public void FrameworkAssembly_BaseLibraryException_ReadsInFrameworksWords(string method, string returns, string argument, string expected)
        => Contains(expected, ClrFrameworkFixture.Simulation($"create function dbo.bcl(@s nvarchar(30)) returns {returns} as external name simclr.Bcl.{method}")
            .AssertSqlError($"select dbo.bcl({argument})", 6522).Message);

    [TestMethod]
    [Description("An nvarchar(n) return value longer than n is the server's TruncationException, not a silent cut.")]
    public void FrameworkAssembly_ScalarReturnTooLong_RaisesTruncation()
        => ClrFrameworkFixture.Simulation("create function dbo.lg(@n int) returns nvarchar(3) as external name simclr.Funcs.Long")
            .AssertSqlError("select dbo.lg(5)", 6522,
                "A .NET Framework error occurred during execution of user-defined routine or aggregate \"lg\": \r\n"
                + "System.Data.SqlServer.TruncationException: Trying to convert return value or output parameter of size 10 bytes to a T-SQL type with a smaller size limit of 6 bytes.\r\n"
                + "System.Data.SqlServer.TruncationException: \r\n"
                + "   at System.Data.SqlServer.Internal.CXVariantBase.StringToWSTR(String pstrValue, Int64 cbMaxLength, Int32 iOffset, EPadding ePad)\r\n.");

    [TestMethod]
    [Description("SqlContext.Pipe.Send(string) is a class-0 state-2 message naming the procedure as the call spelled it.")]
    public void ClrProcedure_PipeSendString_IsMessage()
    {
        var sim = ClrFrameworkFixture.Simulation("create procedure dbo.hello as external name simclr.Procs.Hello");
        var messages = ExecuteCollectingMessages(sim, "exec DBO.HELLO");
        HasCount(1, messages);
        AreEqual(("hello from clr", (byte)2, "DBO.HELLO"), messages[0]);
    }

    [TestMethod]
    [Description("An OUTPUT parameter binds to an out argument, and an int return is the procedure's status.")]
    public void ClrProcedure_OutputAndReturnStatus()
        => AreEqual(507, ClrFrameworkFixture.Simulation(
                "create procedure dbo.addout @a int, @b int, @sum int output as external name simclr.Procs.AddOut")
            .ExecuteScalar("declare @s int, @r int; exec @r = dbo.addout 2, 3, @s output; select @s * 100 + @r"));

    [TestMethod]
    [Description("A ref argument sees the caller's value — NULL as the SqlString.Null sentinel — and its new value writes back.")]
    public void ClrProcedure_RefParameter_RoundTrips()
    {
        var sim = ClrFrameworkFixture.Simulation("create procedure dbo.rp @s nvarchar(20) output as external name simclr.Procs.RefParam");
        AreEqual("hi!", sim.ExecuteScalar("declare @x nvarchar(20) = N'hi'; exec dbo.rp @x output; select @x"));
        AreEqual("was null", sim.ExecuteScalar("declare @x nvarchar(20); exec dbo.rp @x output; select @x"));
    }

    [TestMethod]
    [Description("An output value wider than its nvarchar(n) parameter is the TruncationException, and the caller's variable keeps its value.")]
    public void ClrProcedure_OutputTooLong_RaisesAndLeavesVariable()
    {
        var sim = ClrFrameworkFixture.Simulation("create procedure dbo.rp @s nvarchar(3) output as external name simclr.Procs.RefParam");
        var ex = sim.AssertSqlError("declare @x nvarchar(20) = N'abc'; exec dbo.rp @x output", 6522);
        Contains("of size 8 bytes to a T-SQL type with a smaller size limit of 6 bytes", ex.Message);
        AreEqual("abc", sim.ExecuteScalar("declare @x nvarchar(20) = N'abc'; begin try exec dbo.rp @x output end try begin catch end catch; select @x"));
    }

    [TestMethod]
    [Description("SendResultsStart / Row / End deliver a result set typed by the SqlMetaData the procedure declared.")]
    public void ClrProcedure_SendResults_TypedResultSet()
    {
        var sim = ClrFrameworkFixture.Simulation("create procedure dbo.rws @n int as external name simclr.Procs.Rows");
        using var reader = sim.ExecuteReader("exec dbo.rws 2");
        AreEqual("n,d,s,m", string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        AreEqual("int,decimal,varchar,nvarchar", string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName)));
        IsTrue(reader.Read());
        AreEqual((1, 0.25m, "r1", "max1"), (reader.GetInt32(0), reader.GetDecimal(1), reader.GetString(2), reader.GetString(3)));
        IsTrue(reader.Read());
        AreEqual((2, 0.50m, "r2", true), (reader.GetInt32(0), reader.GetDecimal(1), reader.GetString(2), reader.IsDBNull(3)));
        IsFalse(reader.Read());
    }

    [TestMethod]
    [Description("A CLR procedure's result sets feed INSERT … EXEC like a T-SQL one's.")]
    public void ClrProcedure_InsertExec_Collects()
        => AreEqual(3, ClrFrameworkFixture.Simulation(
                "create procedure dbo.rws @n int as external name simclr.Procs.Rows",
                "create table t (n int, d decimal(10,2), s varchar(20), m nvarchar(max))")
            .ExecuteScalar("insert t exec dbo.rws 3; select count(*) from t"));

    [TestMethod]
    [Description("Messages and result sets arrive in the order the procedure sent them; a result set left open is sent when the procedure returns.")]
    public void ClrProcedure_MessagesAndOpenResultSet_InOrder()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create procedure dbo.mixed as external name simclr.Procs.Mixed",
            "create procedure dbo.open_set as external name simclr.Procs.StartNoEnd");
        AreEqual(42, sim.ExecuteScalar("exec dbo.mixed"));
        AreEqual(5, sim.ExecuteScalar("exec dbo.open_set"));
        var messages = ExecuteCollectingMessages(sim, "exec dbo.mixed");
        AreEqual("before,after", string.Join(",", messages.Select(message => message.Message)));
    }

    [TestMethod]
    [Description("A throw is Msg 6522 state 1 at line 0, reported with the exception's type, message and stack as the server hosts it.")]
    public void ClrProcedure_Throw_RaisesMsg6522WithStack()
    {
        var sim = ClrFrameworkFixture.Simulation("create procedure dbo.throws as external name simclr.Procs.Throws");
        var ex = sim.AssertSqlError("exec dbo.throws", 6522);
        AreEqual((1, 0, "dbo.throws"), (ex.State, ex.LineNumber, ex.Procedure));
        AreEqual(
            "A .NET Framework error occurred during execution of user-defined routine or aggregate \"throws\": \r\n"
            + "System.InvalidOperationException: proc boom\r\nSystem.InvalidOperationException: \r\n   at Procs.Throws()\r\n.",
            ex.Errors[0].Message);
    }

    [TestMethod]
    [Description("The pipe's own refusals carry its frame and the procedure's, as the server's do.")]
    public void ClrProcedure_PipeMisuse_ReportsPipeFrame()
    {
        var ex = ClrFrameworkFixture.Simulation("create procedure dbo.x as external name simclr.Procs.RowWithoutStart")
            .AssertSqlError("exec dbo.x", 6522);
        Contains(
            "System.InvalidOperationException: Result set has not been initiated.  Call SendResultSetStart before calling SendResultsRow.\r\n"
            + "System.InvalidOperationException: \r\n"
            + "   at Microsoft.SqlServer.Server.SqlPipe.SendResultsRow(SqlDataRecord record)\r\n"
            + "   at Procs.RowWithoutStart()\r\n.",
            ex.Errors[0].Message);
    }

    [TestMethod]
    [Description("SqlDataRecord.SetValue takes only the CLR types its column's typed setter would: an int into a bigint column is InvalidCastException.")]
    public void ClrProcedure_SetValueWrongKind_Refused()
        => Contains(
            "System.InvalidCastException: Specified cast is not valid.",
            ClrFrameworkFixture.Simulation("create procedure dbo.x as external name simclr.Procs.SetValueWrongKind")
                .AssertSqlError("exec dbo.x", 6522).Message);

    [TestMethod]
    [Description("Rows sent before a throw reach the client ahead of the error.")]
    public void ClrProcedure_ThrowAfterRows_KeepsRows()
    {
        var sim = ClrFrameworkFixture.Simulation("create procedure dbo.x as external name simclr.Procs.ThrowAfterRows");
        using var reader = sim.ExecuteReader("exec dbo.x");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(6522, Throws<SimulatedSqlException>(() => reader.Read()).Number);
    }

    [TestMethod]
    [Description("OUTPUT on one side of the binding and by-value on the other is Msg 6580 followed by Msg 6552.")]
    [DataRow("create procedure dbo.x @a int, @b int, @sum int as external name simclr.Procs.AddOut", 6580, 6552)]
    [DataRow("create procedure dbo.x @a int output as external name simclr.Procs.ByValue", 6580, 6552)]
    [DataRow("create procedure dbo.x as external name simclr.Procs.ReturnsString", 6567, 0)]
    [DataRow("create procedure dbo.x @a int as external name simclr.Procs.Hello", 6550, 0)]
    [DataRow("create procedure dbo.x @a bigint as external name simclr.Procs.ByValue", 6552, 0)]
    [DataRow("create procedure dbo.x with recompile as external name simclr.Procs.Hello", 155, 0)]
    public void ClrProcedure_BindingErrors(string create, int first, int second)
    {
        var ex = ClrFrameworkFixture.Simulation().AssertSqlError(create, first);
        AreEqual(second == 0 ? 1 : 2, ex.Errors.Count);
        if (second != 0)
            AreEqual(second, ex.Errors[1].Number);
    }

    [TestMethod]
    [Description("A T-SQL body can't replace a CLR procedure (Msg 2010), nor a CLR binding a T-SQL one (Msg 6530).")]
    public void ClrProcedure_AlterAcrossKinds_Refused()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create procedure dbo.clr as external name simclr.Procs.Hello",
            "create procedure dbo.tsql as select 1 a");
        _ = sim.AssertSqlError("alter procedure dbo.clr as select 1 a", 2010);
        _ = sim.AssertSqlError("alter procedure dbo.tsql as external name simclr.Procs.Hello", 6530);
    }

    [TestMethod]
    [Description("A CLR procedure is PC in sys.objects, has an assembly_modules row and no sql_modules one, and reports its parameters' defaults.")]
    public void ClrProcedure_Catalog()
    {
        var sim = ClrFrameworkFixture.Simulation("create procedure dbo.addout @a int, @b int = 4, @sum int output as external name simclr.Procs.AddOut");
        AreEqual("PC|CLR_STORED_PROCEDURE", sim.ExecuteScalar("select rtrim(type) + '|' + type_desc from sys.objects where name = 'addout'"));
        AreEqual("Procs.AddOut", sim.ExecuteScalar("select assembly_class + '.' + assembly_method from sys.assembly_modules"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.sql_modules where object_id = object_id('dbo.addout')"));
        AreEqual(4, sim.ExecuteScalar("select cast(default_value as int) from sys.parameters where object_id = object_id('dbo.addout') and has_default_value = 1"));
        AreEqual("EXTERNAL", sim.ExecuteScalar("select routine_body from information_schema.routines where routine_name = 'addout'"));
        AreEqual("1|1|", sim.ExecuteScalar("select concat(objectproperty(object_id('dbo.addout'), 'IsProcedure'), '|', objectproperty(object_id('dbo.addout'), 'IsExecuted'), '|', objectproperty(object_id('dbo.addout'), 'IsEncrypted'))"));
        _ = sim.AssertSqlError("drop assembly simclr", 6590);
    }

    [TestMethod]
    [Description("sp_describe_first_result_set can't see into a CLR procedure (Msg 11515).")]
    public void ClrProcedure_DescribeFirstResultSet_Refused()
        => Contains("invokes a CLR procedure", ClrFrameworkFixture.Simulation("create procedure dbo.x @n int as external name simclr.Procs.Rows")
            .AssertSqlError("exec sp_describe_first_result_set N'exec dbo.x 1'", 11515).Message);

    [TestMethod]
    [Description("A CLR table-valued function's rows come from its FillRow method, NULLs included, and it takes part in FROM and APPLY.")]
    public void ClrTableFunction_Rows()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create function dbo.series(@c int) returns table (n int, label nvarchar(20)) as external name simclr.Tvfs.Series");
        AreEqual("1:item 1,2:,3:item 3", sim.ExecuteScalar("select string_agg(concat(n, ':', label), ',') within group (order by n) from dbo.series(3)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.series(null)"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from (values (1), (2)) t(v) cross apply dbo.series(t.v)"));
    }

    [TestMethod]
    [Description("An iterator method (yield return) is the ordinary init method, and loads under SAFE.")]
    public void ClrTableFunction_Iterator()
        => AreEqual("a||b", ClrFrameworkFixture.Simulation(
                "create function dbo.split(@s nvarchar(max), @sep nvarchar(10)) returns table (part nvarchar(100)) as external name simclr.Tvfs.Split")
            .ExecuteScalar("select string_agg(part, '|') from dbo.split(N'a,,b', N',')"));

    [TestMethod]
    [Description("A throw from the init method is Msg 6522 state 2; one from FillRow is Msg 6260.")]
    public void ClrTableFunction_Throws()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create function dbo.fi(@c int) returns table (n int, label nvarchar(20)) as external name simclr.Tvfs.ThrowsInInit",
            "create function dbo.ff(@c int) returns table (n int) as external name simclr.Tvfs.ThrowsInFill");
        AreEqual(2, sim.AssertSqlError("select * from dbo.fi(1)", 6522).State);
        Contains("System.ArgumentException: fill boom", sim.AssertSqlError("select * from dbo.ff(1)", 6260).Message);
    }

    [TestMethod]
    [Description("CREATE refuses a result table a streaming function can't return, and a FillRow that doesn't fit it.")]
    [DataRow("(n int, label varchar(20))", "Series", 6514)]
    [DataRow("(n int not null, label nvarchar(20))", "Series", 6526)]
    [DataRow("(n int primary key, label nvarchar(20))", "Series", 6525)]
    [DataRow("(n int)", "Series", 6208)]
    [DataRow("(n bigint, label nvarchar(20))", "Series", 6258)]
    [DataRow("(n int)", "NoFill", 10306)]
    public void ClrTableFunction_CreateErrors(string table, string method, int error)
        => _ = ClrFrameworkFixture.Simulation().AssertSqlError(
            $"create function dbo.f(@c int) returns table {table} as external name simclr.Tvfs.{method}", error);

    [TestMethod]
    [Description("A CLR table-valued function is FT, with its declared columns in sys.columns.")]
    public void ClrTableFunction_Catalog()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create function dbo.series(@c int = 2) returns table (n int, label nvarchar(20)) as external name simclr.Tvfs.Series");
        AreEqual("FT", sim.ExecuteScalar("select type from sys.objects where name = 'series'"));
        AreEqual("n,label", sim.ExecuteScalar("select string_agg(name, ',') within group (order by column_id) from sys.columns where object_id = object_id('dbo.series')"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.series(default)"));
    }

    [TestMethod]
    [Description("A CLR aggregate accumulates per group, NULLs reaching Accumulate, and an empty input still calls Terminate.")]
    public void ClrAggregate_GroupBy()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create aggregate dbo.sumsq (@v int) returns bigint external name simclr.SumSquares",
            "create aggregate dbo.countall (@v nvarchar(10)) returns int external name simclr.CountAll");
        AreEqual("1:13,2:16,3:", sim.ExecuteScalar(
            "select string_agg(concat(g, ':', s), ',') within group (order by g) from (select g, dbo.sumsq(v) s from (values (1, 2), (1, 3), (2, null), (2, 4), (3, null)) t(g, v) group by g) x"));
        AreEqual(3, sim.ExecuteScalar("select dbo.countall(v) from (values (N'a'), (null), (N'b')) t(v)"));
        AreEqual(0, sim.ExecuteScalar("select dbo.countall(v) from (values (N'a')) t(v) where 1 = 0"));
    }

    [TestMethod]
    [Description("A Format.UserDefined aggregate takes several arguments and DISTINCT, and runs over a partition window.")]
    public void ClrAggregate_MultipleArgumentsDistinctAndWindow()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create aggregate dbo.concat (@v nvarchar(100), @sep nvarchar(5)) returns nvarchar(max) external name simclr.Concat",
            "create aggregate dbo.sumsq (@v int) returns bigint external name simclr.SumSquares");
        AreEqual("a|b", sim.ExecuteScalar("select dbo.concat(v, N'|') from (select top 10 v from (values (N'a'), (N'b')) t(v) order by v) x"));
        AreEqual("a", sim.ExecuteScalar("select dbo.concat(distinct v, N',') from (values (N'a'), (N'a')) t(v)"));
        AreEqual(13L, sim.ExecuteScalar("select top 1 dbo.sumsq(v) over (partition by g) from (values (1, 2), (1, 3), (2, 5)) t(g, v) order by v"));
        _ = sim.AssertSqlError("select dbo.sumsq(v) over (order by v) from (values (1)) t(v)", 156);
    }

    [TestMethod]
    [Description("A throw from Accumulate is Msg 6522 state 2 naming the aggregate.")]
    public void ClrAggregate_Throw()
    {
        var ex = ClrFrameworkFixture.Simulation("create aggregate dbo.aggthrows (@v int) returns int external name simclr.AggThrows")
            .AssertSqlError("select dbo.aggthrows(v) from (values (1), (3)) t(v)", 6522);
        AreEqual(2, ex.State);
        Contains("\"aggthrows\": \r\nSystem.InvalidOperationException: agg boom\r\n", ex.Message);
    }

    [TestMethod]
    [Description("CREATE AGGREGATE checks the class against the aggregate contract.")]
    [DataRow("dbo.a (@v int) returns int external name simclr.NoMerge", 6558)]
    [DataRow("dbo.a (@v int) returns int external name simclr.NoAttr", 6255)]
    [DataRow("dbo.a (@v int) returns int external name simclr.NativeWithRef", 6225)]
    [DataRow("dbo.a (@v int) returns int external name simclr.Nope", 6556)]
    [DataRow("dbo.a (@v bigint) returns bigint external name simclr.SumSquares", 6552)]
    [DataRow("dbo.a (@v int) returns int external name simclr.SumSquares", 6558)]
    [DataRow("dbo.a (@v int = 1) returns bigint external name simclr.SumSquares", 10726)]
    [DataRow("dbo.a (@v int) returns bigint as external name simclr.SumSquares", 156)]
    public void ClrAggregate_CreateErrors(string rest, int error)
        => _ = ClrFrameworkFixture.Simulation().AssertSqlError("create aggregate " + rest, error);

    [TestMethod]
    [Description("An aggregate is AF, answers to DROP AGGREGATE only, can't be EXECuted, and needs its schema at the call site.")]
    public void ClrAggregate_CatalogAndNameRules()
    {
        var sim = ClrFrameworkFixture.Simulation("create aggregate dbo.sumsq (@v int) returns bigint external name simclr.SumSquares");
        AreEqual("AF|AGGREGATE_FUNCTION", sim.ExecuteScalar("select rtrim(type) + '|' + type_desc from sys.objects where name = 'sumsq'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.assembly_modules where assembly_class = 'SumSquares' and assembly_method is null"));
        AreEqual("0|", sim.ExecuteScalar("select concat(objectproperty(object_id('dbo.sumsq'), 'IsExecuted'), '|', objectproperty(object_id('dbo.sumsq'), 'IsDeterministic'))"));
        _ = sim.AssertSqlError("select sumsq(v) from (values (1)) t(v)", 195);
        _ = sim.AssertSqlError("select dbo.sumsq(v, v) from (values (1)) t(v)", 174);
        _ = sim.AssertSqlError("exec dbo.sumsq 1", 2809);
        _ = sim.AssertSqlError("drop function dbo.sumsq", 3705);
        _ = sim.ExecuteNonQuery("drop aggregate dbo.sumsq");
        _ = sim.AssertSqlError("drop aggregate dbo.sumsq", 3701);
        _ = sim.ExecuteNonQuery("drop aggregate if exists dbo.sumsq; drop assembly simclr");
    }

    /// <summary>
    /// Real resolves every reference against a fixed catalog of .NET Framework
    /// assemblies, by name and public key token, so an assembly built for .NET
    /// is refused, naming its first reference (probed 2026-10-07 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void CreateAssembly_NetTargeted_RaisesMsg6503()
    {
        var ex = ClrSimulation().AssertSqlError($"CREATE ASSEMBLY sim_net FROM {ClrAssemblyFixture.HexLiteral(ClrAssemblyFixture.NetTargeted())}", 6503);
        StartsWith("Assembly 'system.", ex.Errors[0].Message);
        EndsWith(".' was not found in the SQL catalog.", ex.Errors[0].Message);
        AreEqual(12, ex.State);
    }

    [TestMethod]
    [DataRow("netstandard", "2.0.0.0", "cc7b13ffcd2ddd51", "netstandard, version=2.0.0.0, culture=neutral, publickeytoken=cc7b13ffcd2ddd51.")]
    [DataRow("System.Runtime", "10.0.0.0", "b03f5f7f11d50a3a", "system.runtime, version=10.0.0.0, culture=neutral, publickeytoken=b03f5f7f11d50a3a.")]
    [DataRow("Microsoft.SqlServer.Server", "1.0.0.0", "cc7b13ffcd2ddd51", "microsoft.sqlserver.server, version=1.0.0.0, culture=neutral, publickeytoken=cc7b13ffcd2ddd51.")]
    [DataRow("System.Drawing", "4.0.0.0", "b03f5f7f11d50a3a", "system.drawing, version=4.0.0.0, culture=neutral, publickeytoken=b03f5f7f11d50a3a.")]
    [DataRow("Microsoft.CSharp", "4.0.0.0", "b03f5f7f11d50a3a", "microsoft.csharp, version=4.0.0.0, culture=neutral, publickeytoken=b03f5f7f11d50a3a.")]
    [DataRow("System.Data", "4.0.0.0", "b03f5f7f11d50a3a", "system.data, version=4.0.0.0, culture=neutral, publickeytoken=b03f5f7f11d50a3a.")]
    [DataRow("System.Data", "4.0.0.0", null, "system.data, version=0.0.0.0, culture=neutral, publickeytoken=null.")]
    [DataRow("Microsoft.SqlServer.Types", "18.0.0.0", "89845dcd8080cc91", "microsoft.sqlserver.types, version=18.0.0.0, culture=neutral, publickeytoken=89845dcd8080cc91.")]
    [DataRow("MyLib", "1.0.0.0", null, "mylib, version=0.0.0.0, culture=neutral, publickeytoken=null.")]
    public void CreateAssembly_ReferenceOutsideTheCatalog_RaisesMsg6503(string name, string version, string? token, string described)
        => ClrSimulation().AssertSqlError(
            $"CREATE ASSEMBLY refs FROM {ClrAssemblyFixture.HexLiteral(ClrAssemblyFixture.WithReferences("refs", (name, version, token)))}",
            6503,
            $"Assembly '{described}' was not found in the SQL catalog.");

    [TestMethod]
    [DataRow("System", "4.0.0.0", "b77a5c561934e089")]
    [DataRow("System.Data", "4.0.0.1", "b77a5c561934e089")]
    [DataRow("System.Xml.Linq", "4.0.0.0", "b77a5c561934e089")]
    [DataRow("System.Security", "4.0.0.0", "b03f5f7f11d50a3a")]
    [DataRow("Microsoft.VisualBasic", "10.0.0.0", "b03f5f7f11d50a3a")]
    [DataRow("System.Numerics", "4.0.0.0", "b77a5c561934e089")]
    [DataRow("Microsoft.SqlServer.Types", "16.0.0.0", "89845dcd8080cc91")]
    [DataRow("Microsoft.SqlServer.Types", "10.0.0.0", "89845dcd8080cc91")]
    public void CreateAssembly_ReferenceInTheCatalog_Registers(string name, string version, string token)
    {
        var sim = ClrSimulation();
        _ = sim.ExecuteNonQuery($"CREATE ASSEMBLY refs FROM {ClrAssemblyFixture.HexLiteral(ClrAssemblyFixture.WithReferences("refs", (name, version, token)))}");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.assemblies where name = 'refs'"));
    }

    private const string Msg10343 = "CREATE or ALTER ASSEMBLY for assembly 'sim_safe' with the SAFE or EXTERNAL_ACCESS option failed because the 'clr strict security' option of sp_configure is set to 1. Microsoft recommends that you sign the assembly with a certificate or asymmetric key that has a corresponding login with UNSAFE ASSEMBLY permission. Alternatively, you can trust the assembly using sp_add_trusted_assembly.";

    /// <summary>
    /// <c>clr strict security</c> is on as real installs it, refusing an
    /// assembly the server doesn't trust with a severity-14 Msg 10343 that ends
    /// only its statement (probed 2026-10-07 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void StrictSecurity_IsOnByDefault_AndRefusesAnUntrustedAssembly()
    {
        var sim = new Simulation { EnableClr = true };
        AreEqual(1, sim.ExecuteScalar("select cast(value_in_use as int) from sys.configurations where name = 'clr strict security'"));
        sim.AssertSqlError(CreateSafeAssembly(), 10343, Msg10343);
        AreEqual(10343, sim.ExecuteScalar($"begin try {CreateSafeAssembly()} end try begin catch select error_number() end catch"));
        AreEqual(2, sim.ExecuteScalar($"declare @n int = 1; begin try {CreateSafeAssembly()} end try begin catch set @n = 2 end catch select @n"));
    }

    [TestMethod]
    public void StrictSecurity_TurnedOff_Registers()
    {
        var sim = new Simulation { EnableClr = true };
        _ = sim.ExecuteNonQuery(ClrAssemblyFixture.TrustAllAssemblies);
        AreEqual(0, sim.ExecuteScalar("select cast(value_in_use as int) from sys.configurations where name = 'clr strict security'"));
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
    }

    [TestMethod]
    public void StrictSecurity_ATrustworthyDatabase_Registers()
    {
        var sim = new Simulation { EnableClr = true };
        _ = sim.ExecuteNonQuery("alter database simulated set trustworthy on");
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());
    }

    [TestMethod]
    public void StrictSecurity_ATrustedHash_Registers()
    {
        var sim = new Simulation { EnableClr = true };
        var hash = "0x" + Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(ClrAssemblyFixture.Safe()));
        _ = sim.ExecuteNonQuery($"exec sys.sp_add_trusted_assembly {hash}, N'the fixture'");
        AreEqual("the fixture", sim.ExecuteScalar("select description from sys.trusted_assemblies"));
        AreEqual(1, sim.ExecuteScalar($"select count(*) from sys.trusted_assemblies where hash = {hash} and created_by = N'sa'"));
        _ = sim.ExecuteNonQuery(CreateSafeAssembly());

        sim.AssertSqlError($"exec sys.sp_add_trusted_assembly {hash}", 10345, $"The assembly hash '{hash}' is already trusted.");
        _ = sim.ExecuteNonQuery($"exec sp_drop_trusted_assembly {hash}");
        sim.AssertSqlError($"exec sys.sp_drop_trusted_assembly {hash}", 10346, $"The assembly hash '{hash}' is not currently trusted. No action was taken.");
        sim.AssertSqlError("exec sys.sp_add_trusted_assembly 0x1234", 214, "Procedure expects parameter 'hash' of type 'binary(64)/varbinary(64)'.");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.trusted_assemblies"));
    }

    /// <summary>
    /// A <c>Format.UserDefined</c> aggregate's state passes through
    /// <c>Write</c> and a fresh instance's <c>Read</c> once per group before
    /// <c>Terminate</c>, so a field <c>Write</c> leaves out is lost — whatever
    /// the group's size, an empty input and a window included (probed
    /// 2026-10-07 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select dbo.leaky(v) from (values (5)) t (v)", "sum=0 accumulated=1 reads=1 writes=1")]
    [DataRow("select dbo.leaky(v) from (values (5), (7)) t (v)", "sum=0 accumulated=2 reads=1 writes=1")]
    [DataRow("select dbo.leaky(v) from (values (5)) t (v) where v < 0", "sum=0 accumulated=0 reads=1 writes=1")]
    [DataRow("select top 1 dbo.leaky(v) from (values (1, 5), (1, 7), (2, 9)) t (g, v) group by g order by g", "sum=0 accumulated=2 reads=1 writes=1")]
    [DataRow("select top 1 dbo.leaky(v) over (partition by g) from (values (1, 5), (1, 7)) t (g, v)", "sum=0 accumulated=2 reads=1 writes=1")]
    public void UserDefinedAggregate_StateRoundTripsBeforeTerminate(string query, string expected)
        => AreEqual(expected, ClrFrameworkFixture.Simulation("create aggregate dbo.leaky (@v int) returns nvarchar(200) external name simclr.Leaky").ExecuteScalar(query));

    /// <summary>
    /// A CLR aggregate under GROUP BY can't be planned as a hash aggregate, so
    /// <c>OPTION (HASH GROUP)</c> is Msg 8622 as the batch compiles; a scalar
    /// or windowed one, or the statement's built-ins alone, take it (probed
    /// 2026-10-07 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select g, dbo.sumsq(v) from t group by g option (hash group)", true)]
    [DataRow("select g, count(*), dbo.leaky(v) from t group by g option (hash group)", true)]
    [DataRow("select g, dbo.leaky(distinct v) from t group by g option (hash group)", true)]
    [DataRow("select dbo.sumsq(v) from t option (hash group)", false)]
    [DataRow("select dbo.leaky(v) over (partition by g) from t option (hash group)", false)]
    [DataRow("select g, count(*) from t group by g option (hash group)", false)]
    [DataRow("select g, dbo.sumsq(v) from t group by g option (order group)", false)]
    public void HashGroup_OverAGroupedClrAggregate_RaisesMsg8622(string query, bool refused)
    {
        var sim = ClrFrameworkFixture.Simulation(
            "create aggregate dbo.sumsq (@v int) returns bigint external name simclr.SumSquares",
            "create aggregate dbo.leaky (@v int) returns nvarchar(200) external name simclr.Leaky",
            "create table t (g int, v int); insert t values (1, 1), (1, 2), (2, 3)");
        if (refused)
            _ = sim.AssertSqlError("print 'ran'; " + query, 8622);
        else
            _ = sim.ExecuteNonQuery(query);
    }
}
