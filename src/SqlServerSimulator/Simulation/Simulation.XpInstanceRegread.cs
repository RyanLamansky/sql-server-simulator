using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Column schema for the <c>xp_instance_regread</c> result set (the form
    /// without an OUTPUT parameter): <c>Value nvarchar</c>, <c>Data nvarchar</c>
    /// — probe-confirmed against SQL Server 2025.
    /// </summary>
    private static readonly SqlType[] XpInstanceRegreadSchema = [SqlType.NVarchar, SqlType.NVarchar];

    private static readonly string[] XpInstanceRegreadColumnNames = ["Value", "Data"];

    /// <summary>
    /// Synthetic instance-registry defaults returned by
    /// <c>xp_instance_regread</c>. Values are machine-specific on a real
    /// server; the simulator returns fictional paths rooted at
    /// <c>/var/opt/mssql</c> (consistent with the physical paths surfaced by
    /// <c>sys.master_files</c> / <c>sys.database_files</c>). Only <c>SQLPath</c>
    /// (SSMS's instance-root read on connect) is load-bearing; the common
    /// default-directory value names are answered plausibly, everything else
    /// reads NULL (value not found).
    /// </summary>
    private static readonly FrozenDictionary<string, string> InstanceRegistryDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SQLPath"] = "/var/opt/mssql",
        ["DefaultData"] = "/var/opt/mssql/data",
        ["DefaultLog"] = "/var/opt/mssql/data",
        ["BackupDirectory"] = "/var/opt/mssql/data",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Handles <c>EXEC master.dbo.xp_instance_regread</c> (SSMS reads the
    /// instance <c>SQLPath</c> registry value on connect to derive the SMO
    /// RootDirectory). The positional arguments are hive, subkey, value-name,
    /// and an optional <c>@output OUTPUT</c> variable. When an OUTPUT variable
    /// is supplied (SSMS's shape) the resolved value is written into it and no
    /// result set is yielded; otherwise a two-column <c>(Value, Data)</c>
    /// result set is produced (matching real SQL Server). Unrecognized value
    /// names read NULL (registry value not found).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpInstanceRegread(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        // Third positional argument is the registry value name (hive, subkey,
        // value-name, [@output]). Resolve it to a synthetic instance path.
        var valueName = arguments.Count >= 3 && !arguments[2].Value.IsNull
            ? arguments[2].Value.AsString
            : null;
        var resolved = valueName is not null && InstanceRegistryDefaults.TryGetValue(valueName, out var path)
            ? path
            : null;

        // OUTPUT form: write the value into the caller's @variable (or NULL)
        // and yield no result set. This is the shape SSMS uses.
        var outputSlot = arguments.FirstOrDefault(a => a.OutputSlot is not null).OutputSlot;
        if (outputSlot is not null)
        {
            outputSlot.Value = resolved is null
                ? SqlValue.Null(outputSlot.DeclaredType)
                : SqlValue.FromNVarchar(resolved).CoerceTo(outputSlot.DeclaredType);
            yield break;
        }

        // No OUTPUT parameter: real xp_instance_regread emits a (Value, Data)
        // row when the value resolves, and no rows when it does not.
        var rows = resolved is null
            ? []
            : new SqlValue[][] { [SqlValue.FromNVarchar(valueName!), SqlValue.FromNVarchar(resolved)] };
        yield return new SimulatedSqlResultSet(XpInstanceRegreadSchema, XpInstanceRegreadColumnNames, rows);
    }
}
