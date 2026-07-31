using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Concurrent;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary><c>configuration_id</c> of <c>nested triggers</c>.</summary>
    private const int NestedTriggersConfigurationId = 115;

    /// <summary><c>configuration_id</c> of <c>show advanced options</c>.</summary>
    private const int ShowAdvancedOptionsConfigurationId = 518;

    private static readonly SqlType[] SpConfigureSchema =
    [
        SqlType.NVarchar, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32,
    ];

    private static readonly string[] SpConfigureColumnNames =
    [
        "name", "minimum", "maximum", "config_value", "run_value",
    ];

    /// <summary>
    /// Server-configuration values written by <c>sp_configure</c>, keyed by
    /// <c>configuration_id</c>. An option absent here reports the stock
    /// defaults <c>BuiltInResources.ConfigurationData</c> carries. Each entry
    /// holds the pending value <c>sp_configure</c> set (<c>config_value</c> /
    /// <c>sys.configurations.value</c>) and the installed one
    /// <c>RECONFIGURE</c> promoted it to (<c>run_value</c> /
    /// <c>value_in_use</c>) — the split real SQL Server keeps, and the reason
    /// a configuration change without <c>RECONFIGURE</c> has no effect.
    /// Server-scoped: every connection into this simulation reads the same
    /// values, matching a real instance.
    /// </summary>
    internal readonly ConcurrentDictionary<int, (int Configured, int InUse)> ServerConfiguration = new();

    /// <summary>
    /// The installed value of the <c>nested triggers</c> server option. When
    /// <c>false</c>, an AFTER trigger doesn't fire while another AFTER trigger
    /// is running anywhere up the stack — only the first AFTER level runs.
    /// INSTEAD OF triggers nest regardless (probe-confirmed against SQL Server
    /// 2025). Also disables direct recursion, since a trigger re-firing itself
    /// is an AFTER trigger running under an AFTER trigger.
    /// </summary>
    internal bool NestedTriggersEnabled => this.ConfigurationInUse(NestedTriggersConfigurationId) != 0;

    /// <summary>
    /// The installed value of <paramref name="configurationId"/>, falling back
    /// to the stock default when <c>sp_configure</c> never wrote it.
    /// </summary>
    private int ConfigurationInUse(int configurationId) =>
        this.ServerConfiguration.TryGetValue(configurationId, out var setting)
            ? setting.InUse
            : BuiltInResources.ConfigurationData[IndexOfConfiguration(configurationId)].ValueInUse;

    private static int IndexOfConfiguration(int configurationId)
    {
        for (var i = 0; i < BuiltInResources.ConfigurationData.Length; i++)
        {
            if (BuiltInResources.ConfigurationData[i].Id == configurationId)
                return i;
        }
        throw new InvalidOperationException($"configuration_id {configurationId} is not in ConfigurationData.");
    }

    /// <summary>
    /// <c>sp_configure [@configname [, @configvalue]]</c> — reads or writes the
    /// server-configuration catalog. No arguments lists every visible option
    /// ordered by name; a name alone reports that one option; a name plus a
    /// value stages the change and surfaces the sev-0 <strong>Msg 15457</strong>
    /// "Run the RECONFIGURE statement to install" notice without returning rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed against SQL Server 2025: the name argument prefix-matches
    /// (<c>'nested'</c> finds <c>nested triggers</c>), an ambiguous prefix is
    /// <strong>Msg 15124</strong>, an unknown name — or an advanced option while
    /// <c>show advanced options</c>' installed value is 0 — is <strong>Msg
    /// 15123</strong>, and a value outside the option's minimum / maximum is
    /// <strong>Msg 15129</strong>. The advanced-option gate reads the
    /// <em>installed</em> value, so hiding takes effect only after RECONFIGURE.
    /// </para>
    /// <para>
    /// The staged value lands in <see cref="ServerConfiguration"/> whatever the
    /// option, so every option round-trips through <c>sys.configurations</c>;
    /// only <c>nested triggers</c> carries behavior. The two CLR rows are the
    /// exception — they keep reporting the <see cref="EnableClr"/> host opt-in,
    /// which is the simulator's actual gate on assembly registration.
    /// Real SQL Server also requires ALTER SETTINGS permission and returns a
    /// <c>duplicate_options</c> result set alongside Msg 15124; neither is
    /// modeled.
    /// </para>
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpConfigure(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (configName, configValue) = ParseSpConfigureArgs(arguments);
        var simulation = batch.Connection.Simulation;
        if (configName is null)
        {
            yield return new SimulatedSqlResultSet(SpConfigureSchema, SpConfigureColumnNames, ListConfigurationOptions(simulation));
            yield break;
        }

        var index = ResolveConfigurationOption(simulation, configName);
        var option = BuiltInResources.ConfigurationData[index];
        if (configValue is not { } requested)
        {
            yield return new SimulatedSqlResultSet(SpConfigureSchema, SpConfigureColumnNames, [ConfigurationOptionRow(simulation, index)]);
            yield break;
        }

        if (requested < option.Minimum || requested > option.Maximum)
            throw SimulatedSqlException.InvalidConfigurationValue(requested, option.Name);

        // Real reports the move from the option's staged value, and reports it
        // even when the value doesn't change ("changed from 1 to 1").
        var previous = simulation.ServerConfiguration.TryGetValue(option.Id, out var current) ? current.Configured : option.Value;
        _ = simulation.ServerConfiguration.AddOrUpdate(
            option.Id,
            (requested, option.ValueInUse),
            (_, existing) => (requested, existing.InUse));
        batch.AppendInfoError(
            @class: 0,
            state: 1,
            number: 15457,
            message: $"Configuration option '{option.Name}' changed from {previous} to {requested}. Run the RECONFIGURE statement to install.");
    }

    /// <summary>
    /// <c>RECONFIGURE [WITH OVERRIDE]</c> — installs every value
    /// <c>sp_configure</c> has staged, promoting <c>config_value</c> to
    /// <c>run_value</c>. Real validates the staged values against the running
    /// server's state and <c>WITH OVERRIDE</c> waives that check; the simulator
    /// validates at <c>sp_configure</c> time only, so the clause parses and
    /// makes no difference.
    /// </summary>
    private static void ParseReconfigureStatement(BatchContext batch)
    {
        var context = batch.Parser;
        if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not UnquotedString withOption || !BuiltInToken.Equals(withOption.ToString(), "OVERRIDE"))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }

        if (batch.IsSkipping)
            return;

        var configuration = batch.Connection.Simulation.ServerConfiguration;
        foreach (var entry in configuration)
            configuration[entry.Key] = (entry.Value.Configured, entry.Value.Configured);
    }

    /// <summary>
    /// Every option <c>sp_configure</c>'s no-argument form lists — advanced
    /// options only while <c>show advanced options</c> is installed as 1 —
    /// ordered by name the way real emits them.
    /// </summary>
    private static List<SqlValue[]> ListConfigurationOptions(Simulation simulation)
    {
        var showAdvanced = simulation.ConfigurationInUse(ShowAdvancedOptionsConfigurationId) != 0;
        var rows = new List<SqlValue[]>();
        for (var i = 0; i < BuiltInResources.ConfigurationData.Length; i++)
        {
            if (BuiltInResources.ConfigurationData[i].IsAdvanced && !showAdvanced)
                continue;
            rows.Add(ConfigurationOptionRow(simulation, i));
        }

        rows.Sort(static (a, b) => string.Compare(a[0].AsString, b[0].AsString, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    private static SqlValue[] ConfigurationOptionRow(Simulation simulation, int index)
    {
        var option = BuiltInResources.ConfigurationData[index];
        var (configured, inUse) = BuiltInResources.EffectiveConfigurationValues(simulation, index);
        return
        [
            SqlValue.FromNVarchar(option.Name),
            SqlValue.FromInt32(option.Minimum),
            SqlValue.FromInt32(option.Maximum),
            SqlValue.FromInt32(configured),
            SqlValue.FromInt32(inUse),
        ];
    }

    /// <summary>
    /// Matches <paramref name="configName"/> to a configuration option by exact
    /// name or unique prefix, skipping advanced options while they're hidden.
    /// Raises <strong>Msg 15123</strong> (no match) or <strong>Msg 15124</strong>
    /// (ambiguous prefix).
    /// </summary>
    private static int ResolveConfigurationOption(Simulation simulation, string configName)
    {
        var name = configName.Trim();
        var showAdvanced = simulation.ConfigurationInUse(ShowAdvancedOptionsConfigurationId) != 0;
        var match = -1;
        var matches = 0;
        for (var i = 0; i < BuiltInResources.ConfigurationData.Length; i++)
        {
            var candidate = BuiltInResources.ConfigurationData[i].Name;
            if (BuiltInResources.ConfigurationData[i].IsAdvanced && !showAdvanced)
                continue;
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return i;
            if (!candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;
            match = i;
            matches++;
        }

        return matches switch
        {
            0 => throw SimulatedSqlException.ConfigurationOptionDoesNotExist(name),
            1 => match,
            _ => throw SimulatedSqlException.ConfigurationOptionNotUnique(name),
        };
    }

    private static (string? Name, int? Value) ParseSpConfigureArgs(List<ProcArgument> arguments)
    {
        string? name = null;
        int? value = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: name = ConfigureStringArg(arg); break;
                    case 1: value = ConfigureIntArg(arg); break;
                    default: break;
                }
                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "configname"): name = ConfigureStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "configvalue"): value = ConfigureIntArg(arg); break;
                default: break;
            }
        }

        return (name, value);
    }

    private static string? ConfigureStringArg(ProcArgument arg) =>
        arg.IsDefault || arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.NVarchar).AsString;

    private static int? ConfigureIntArg(ProcArgument arg) =>
        arg.IsDefault || arg.Value.IsNull ? null : ScalarArguments.CoerceProcedureParameter(arg.Value, SqlType.Int32);
}
