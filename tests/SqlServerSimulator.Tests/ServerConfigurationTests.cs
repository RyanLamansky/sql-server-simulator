using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_configure</c> + <c>RECONFIGURE</c> and the server-configuration state
/// they write, read back through <c>sys.configurations</c> and the legacy
/// <c>sysconfigures</c>. Every shape and error probe-confirmed against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class ServerConfigurationTests
{
    // 'nested triggers': non-advanced, dynamic, minimum 0 / maximum 1.
    private const int NestedTriggers = 115;

    [TestMethod]
    public void Read_ByName_ReportsMinimumMaximumConfigAndRunValues()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("exec sp_configure 'nested triggers'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("name", reader.GetName(0));
        AreEqual("run_value", reader.GetName(4));
        AreEqual("nested triggers", reader.GetString(0));
        AreEqual(0, reader.GetInt32(1));
        AreEqual(1, reader.GetInt32(2));
        AreEqual(1, reader.GetInt32(3));
        AreEqual(1, reader.GetInt32(4));
        IsFalse(reader.Read());
    }

    /// <summary>A unique prefix resolves to the option it names.</summary>
    [TestMethod]
    public void Read_ByUniquePrefix_Resolves()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("exec sp_configure @configname = 'nested'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("nested triggers", reader.GetString(0));
    }

    [TestMethod]
    public void Read_AmbiguousPrefix_Raises15124()
        => new Simulation().AssertSqlError(
            "exec sp_configure 'remote'", 15124, "The configuration option 'remote' is not unique.");

    [TestMethod]
    public void Read_UnknownOption_Raises15123()
        => new Simulation().AssertSqlError(
            "exec sp_configure 'no such option'",
            15123,
            "The configuration option 'no such option' does not exist, or it may be an advanced option.");

    /// <summary>
    /// An advanced option is hidden behind the same message until
    /// <c>show advanced options</c> is <em>installed</em> as 1.
    /// </summary>
    [TestMethod]
    public void Read_AdvancedOption_HiddenUntilShowAdvancedOptionsInstalled()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("exec sp_configure 'max degree of parallelism'", 15123);
        _ = simulation.ExecuteNonQuery("exec sp_configure 'show advanced options', 1");
        // Staged but not installed — still hidden.
        _ = simulation.AssertSqlError("exec sp_configure 'max degree of parallelism'", 15123);
        _ = simulation.ExecuteNonQuery("reconfigure");
        AreEqual(0, simulation.ExecuteScalar(
            "select convert(int, value_in_use) from sys.configurations where name = 'max degree of parallelism'"));
    }

    /// <summary>
    /// The no-argument listing is ordered by name and omits advanced options
    /// while they're hidden.
    /// </summary>
    [TestMethod]
    public void List_NoArguments_OmitsAdvancedOptions()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var names = new List<string>();
        using (var reader = connection.CreateCommand("exec sp_configure").ExecuteReader())
        {
            while (reader.Read())
                names.Add(reader.GetString(0));
        }

        Contains("nested triggers", names);
        DoesNotContain("max degree of parallelism", names);
        AreEqual(string.Join(",", names.Order(StringComparer.OrdinalIgnoreCase)), string.Join(",", names));
    }

    [TestMethod]
    public void Write_OutOfRangeValue_Raises15129()
        => new Simulation().AssertSqlError(
            "exec sp_configure 'nested triggers', 2",
            15129,
            "'2' is not a valid value for configuration option 'nested triggers'.");

    /// <summary>
    /// A write stages the value and surfaces the sev-0 install reminder —
    /// which real emits even when the value doesn't change.
    /// </summary>
    [TestMethod]
    public void Write_StagesValue_AndSurfacesMsg15457()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        SimulatedError? info = null;
        connection.InfoMessage += (_, e) => info = e.Errors[0];
        _ = connection.CreateCommand("exec sp_configure 'nested triggers', 0").ExecuteNonQuery();

        IsNotNull(info);
        AreEqual(15457, info.Number);
        AreEqual(0, info.Class);
        AreEqual("Configuration option 'nested triggers' changed from 1 to 0. Run the RECONFIGURE statement to install.", info.Message);
    }

    [TestMethod]
    public void Reconfigure_InstallsStagedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_configure 'nested triggers', 0");
        AreEqual(0, ConfiguredValue(simulation));
        AreEqual(1, InstalledValue(simulation));
        _ = simulation.ExecuteNonQuery("reconfigure with override");
        AreEqual(0, ConfiguredValue(simulation));
        AreEqual(0, InstalledValue(simulation));
    }

    /// <summary>The write is server-scoped: a later connection sees it.</summary>
    [TestMethod]
    public void Write_IsVisibleOnAnotherConnection()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_configure 'nested triggers', 0; reconfigure;");
        AreEqual(0, simulation.ExecuteScalar(
            "select convert(int, value_in_use) from sys.configurations where configuration_id = 115"));
    }

    /// <summary>The legacy projection tracks the staged value too.</summary>
    [TestMethod]
    public void Sysconfigures_TracksStagedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_configure 'nested triggers', 0");
        AreEqual(0, simulation.ExecuteScalar("select value from master.dbo.sysconfigures where config = 115"));
    }

    /// <summary>
    /// <c>clr enabled</c> starts as the host opt-in and then reports what
    /// <c>sp_configure</c> and <c>RECONFIGURE</c> installed, the move reported
    /// from the opt-in's value.
    /// </summary>
    [TestMethod]
    public void ClrEnabled_StartsAsHostOptIn_ThenReportsTheWrittenValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_configure 'clr enabled', 1; reconfigure;");
        AreEqual(1, simulation.ExecuteScalar(
            "select convert(int, value_in_use) from sys.configurations where name = 'clr enabled'"));
        var clr = new Simulation { EnableClr = true };
        AreEqual("1|1", clr.ExecuteScalar("select concat(convert(int, value), '|', convert(int, value_in_use)) from sys.configurations where name = 'clr enabled'"));
        using var connection = clr.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.Add(e.Message);
        _ = connection.CreateCommand("exec sp_configure 'clr enabled', 0; reconfigure").ExecuteNonQuery();
        AreEqual("Configuration option 'clr enabled' changed from 1 to 0. Run the RECONFIGURE statement to install.", string.Join('|', messages));
        AreEqual("0|0", clr.ExecuteScalar("select concat(convert(int, value), '|', convert(int, value_in_use)) from sys.configurations where name = 'clr enabled'"));
    }

    /// <summary>
    /// RECONFIGURE is a statement like any other: it composes with the ones
    /// around it, with or without a separating <c>;</c>.
    /// </summary>
    [TestMethod]
    public void Reconfigure_ComposesWithNeighbouringStatements()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            exec sp_configure 'nested triggers', 0
            reconfigure
            select convert(int, value_in_use) from sys.configurations where configuration_id = 115
            """));

    [TestMethod]
    public void Reconfigure_BadTrailer_IsSyntaxError()
        => new Simulation().ValidateSyntaxError("reconfigure with nonsense", "nonsense");

    private static object? ConfiguredValue(Simulation simulation) => simulation.ExecuteScalar(
        $"select convert(int, value) from sys.configurations where configuration_id = {NestedTriggers}");

    private static object? InstalledValue(Simulation simulation) => simulation.ExecuteScalar(
        $"select convert(int, value_in_use) from sys.configurations where configuration_id = {NestedTriggers}");

    /// <summary>
    /// The options the Linux server takes no write to, the value already held
    /// included, past the range check, staging nothing (probed 2026-10-17
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("xp_cmdshell", 0, 15392, 177)]
    [DataRow("Ole Automation Procedures", 1, 15392, 177)]
    [DataRow("priority boost", 0, 15392, 177)]
    [DataRow("set working set size", 0, 15392, 177)]
    [DataRow("filestream access level", 0, 15392, 177)]
    [DataRow("external xtp dll gen util enabled", 0, 15392, 177)]
    [DataRow("max server tiered memory (MB)", 0, 5876, 177)]
    [DataRow("suppress recovery model errors", 0, 5870, 164)]
    [DataRow("xp_cmdshell", 5, 15129, 0)]
    public void Write_LinuxRefusedOption(string option, int value, int number, int line)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_configure 'show advanced options', 1; reconfigure");
        var ex = simulation.AssertSqlError(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"exec sp_configure '{option}', {value}"), number);
        if (line != 0)
            AreEqual(line, ex.LineNumber);
        AreEqual(0, simulation.ExecuteScalar($"select count(*) from sys.configurations where name = '{option}' and value <> value_in_use"));
    }
}
