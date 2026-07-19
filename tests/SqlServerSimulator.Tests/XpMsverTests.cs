using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>xp_msver</c> system procedure: a single 20-row result set
/// (columns <c>Index smallint</c>, <c>Name nvarchar</c>,
/// <c>Internal_Value int</c>, <c>Character_Value nvarchar</c>) that SSMS calls
/// on connect. Every cell is fixed, mirroring the SQL Server 2025 reference
/// (17.0.4065.4, RTM-CU7); the host-shaped cells (processor count, memory,
/// platform) report the reference's fixed placeholders. The proc resolves as
/// <c>xp_msver</c>, <c>dbo.xp_msver</c>, and <c>master.dbo.xp_msver</c> from
/// any current database.
/// </summary>
[TestClass]
public sealed class XpMsverTests
{
    private static List<(short Index, string Name, object InternalValue, object CharacterValue)> ReadRows(string sql)
    {
        using var reader = new Simulation().ExecuteReader(sql);
        return
        [
            .. reader.EnumerateRecords()
                .Select(r => (
                    Index: r.GetInt16(0),
                    Name: r.GetString(1),
                    InternalValue: r.IsDBNull(2) ? (object)DBNull.Value : r.GetInt32(2),
                    CharacterValue: r.IsDBNull(3) ? (object)DBNull.Value : r.GetString(3))),
        ];
    }

    [TestMethod]
    public void XpMsver_ReturnsTwentyRows()
        => HasCount(20, ReadRows("exec xp_msver"));

    [TestMethod]
    public void XpMsver_ColumnShape()
    {
        using var reader = new Simulation().ExecuteReader("exec xp_msver");
        AreEqual(4, reader.FieldCount);
        AreEqual("Index", reader.GetName(0));
        AreEqual("Name", reader.GetName(1));
        AreEqual("Internal_Value", reader.GetName(2));
        AreEqual("Character_Value", reader.GetName(3));
        AreEqual(typeof(short), reader.GetFieldType(0));
        AreEqual(typeof(string), reader.GetFieldType(1));
        AreEqual(typeof(int), reader.GetFieldType(2));
        AreEqual(typeof(string), reader.GetFieldType(3));
    }

    [TestMethod]
    public void XpMsver_ProductName_IsFirstRow()
    {
        var row = ReadRows("exec xp_msver").Single(r => r.Index == 1);
        AreEqual("ProductName", row.Name);
        AreEqual(DBNull.Value, row.InternalValue);
        AreEqual("Microsoft SQL Server", row.CharacterValue);
    }

    [TestMethod]
    public void XpMsver_ProductVersion_CarriesReferenceBuild()
    {
        var row = ReadRows("exec xp_msver").Single(r => r.Name == "ProductVersion");
        AreEqual(17 << 16, row.InternalValue);
        AreEqual("17.0.4065.4", row.CharacterValue);
    }

    [TestMethod]
    public void XpMsver_Platform_IsFixedNtX64()
    {
        var row = ReadRows("exec xp_msver").Single(r => r.Name == "Platform");
        AreEqual(DBNull.Value, row.InternalValue);
        AreEqual("NT x64", row.CharacterValue);
    }

    [TestMethod]
    public void XpMsver_ProcessorCount_IsFixed()
    {
        var row = ReadRows("exec xp_msver").Single(r => r.Name == "ProcessorCount");
        AreEqual(16, row.InternalValue);
        AreEqual("16", row.CharacterValue);
    }

    [TestMethod]
    public void XpMsver_ProcessorType_Is8664()
    {
        var row = ReadRows("exec xp_msver").Single(r => r.Name == "ProcessorType");
        AreEqual(8664, row.InternalValue);
        AreEqual(DBNull.Value, row.CharacterValue);
    }

    [TestMethod]
    public void XpMsver_CallableWithDboSchema()
        => HasCount(20, ReadRows("exec dbo.xp_msver"));

    [TestMethod]
    public void XpMsver_CallableThroughMasterThreePartName()
        => HasCount(20, ReadRows("exec master.dbo.xp_msver"));

    [TestMethod]
    public void XpMsver_OptnameArguments_SelectOnlyRequestedRows()
        => CollectionAssert.AreEqual(
            new[] { "Platform", "ProcessorCount" },
            ReadRows("exec xp_msver 'Platform', 'ProcessorCount'").Select(r => r.Name).ToList());

    [TestMethod]
    public void XpMsver_OptnameArguments_AlwaysOrderedByIndex_NotArgumentOrder()
        => CollectionAssert.AreEqual(
            new[] { (short)4, (short)16 },
            ReadRows("exec xp_msver 'ProcessorCount', 'Platform'").Select(r => r.Index).ToList());

    [TestMethod]
    public void XpMsver_DacFxFiveOptnames_ReturnExactIndexOrderedSet()
        => CollectionAssert.AreEqual(
            new[] { (short)4, (short)7, (short)15, (short)16, (short)19 },
            ReadRows("exec xp_msver 'PhysicalMemory', 'Platform', 'FileDescription', 'WindowsVersion', 'ProcessorCount'")
                .Select(r => r.Index).ToList());

    [TestMethod]
    public void XpMsver_OptnameCaseInsensitive()
    {
        var row = ReadRows("exec xp_msver 'pLaTfOrM'").Single();
        AreEqual("Platform", row.Name);
    }

    [TestMethod]
    public void XpMsver_UnknownOptname_ReturnsEmptySet()
        => IsEmpty(ReadRows("exec xp_msver 'BogusName'"));

    [TestMethod]
    public void XpMsver_DuplicateOptname_ReturnsRowOnce()
        => HasCount(1, ReadRows("exec xp_msver 'Platform', 'Platform'"));

    [TestMethod]
    public void XpMsver_NamedOptnameArgument_Selects()
    {
        var row = ReadRows("exec xp_msver @optname = 'Platform'").Single();
        AreEqual("Platform", row.Name);
    }

    // SSMS Activity Monitor's opener: capture xp_msver's rowset into a temp
    // table via INSERT … EXEC, then read it back. The four-column shape maps
    // positionally (ID <- Index, Name, Internal_Value, Value <- Character_Value).
    [TestMethod]
    public void XpMsver_InsertExecIntoTempTable_ActivityMonitorPattern()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table #SVer(ID int, Name sysname, Internal_Value int, Value nvarchar(512));
            insert #SVer exec master.dbo.xp_msver
            """).ExecuteNonQuery();
        AreEqual(20, connection.CreateCommand("select count(*) from #SVer").ExecuteScalar());
        AreEqual("17.0.4065.4", connection.CreateCommand(
            "select Value from #SVer where Name = 'ProductVersion'").ExecuteScalar());
    }
}
