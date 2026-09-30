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
    /// The instance-registry values <c>xp_instance_regread</c> answers, keyed
    /// by subkey and value name — what SQL Server 2025 on Linux reports for
    /// them (probed 2026-09-30), <c>SQLPath</c>'s <c>C:\</c> included. Every
    /// other value reads NULL (value not found).
    /// </summary>
    private static readonly FrozenDictionary<string, SqlValue> InstanceRegistryValues = new Dictionary<string, SqlValue>(StringComparer.OrdinalIgnoreCase)
    {
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer|AuditLevel"] = SqlValue.FromInt32(2),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer|BackupDirectory"] = SqlValue.FromNVarchar(@"/var/opt/mssql/data"),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer|LoginMode"] = SqlValue.FromInt32(2),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\Filestream|EnableLevel"] = SqlValue.FromInt32(0),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\Filestream|ShareName"] = SqlValue.FromNVarchar(@"MSSQLSERVER"),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib|ForceEncryption"] = SqlValue.FromInt32(0),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib|HideInstance"] = SqlValue.FromInt32(0),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib\Np|Enabled"] = SqlValue.FromInt32(0),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib\Tcp|Enabled"] = SqlValue.FromInt32(1),
        [@"SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib\Tcp\IPAll|TcpPort"] = SqlValue.FromNVarchar(@"1433"),
        [@"SOFTWARE\Microsoft\MSSQLServer\Setup|SQLDataRoot"] = SqlValue.FromNVarchar(@"/var/opt/mssql/"),
        [@"SOFTWARE\Microsoft\MSSQLServer\Setup|SQLGroup"] = SqlValue.FromNVarchar(@"S-1-5-32-544"),
        [@"SOFTWARE\Microsoft\MSSQLServer\Setup|SQLPath"] = SqlValue.FromNVarchar(@"C:\"),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The machine-registry values <c>xp_regread</c> answers, which SMO reads
    /// for the instance id and the SQL Browser service (probed 2026-09-30
    /// against SQL Server 2025 on Linux, which has no Browser service).
    /// </summary>
    private static readonly FrozenDictionary<string, SqlValue> MachineRegistryValues = new Dictionary<string, SqlValue>(StringComparer.OrdinalIgnoreCase)
    {
        [@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL|MSSQLSERVER"] = SqlValue.FromNVarchar("MSSQL"),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Handles <c>EXEC master.dbo.xp_instance_regread</c> (SSMS reads the
    /// instance <c>SQLPath</c> registry value on connect to derive the SMO
    /// RootDirectory) and, with <paramref name="instanceMapped"/> false,
    /// <c>xp_regread</c>, which reads machine keys. The positional arguments are hive, subkey, value-name,
    /// and an optional <c>@output OUTPUT</c> variable. When an OUTPUT variable
    /// is supplied (SSMS's shape) the resolved value is written into it and no
    /// result set is yielded; otherwise a two-column <c>(Value, Data)</c>
    /// result set is produced (matching real SQL Server). Unrecognized value
    /// names read NULL (registry value not found).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpInstanceRegread(BatchContext batch, bool instanceMapped)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        // The positional arguments are hive, subkey, value name and an
        // optional @output.
        var subkey = arguments.Count >= 2 && !arguments[1].Value.IsNull
            ? arguments[1].Value.AsString.Trim('\\')
            : null;
        var valueName = arguments.Count >= 3 && !arguments[2].Value.IsNull
            ? arguments[2].Value.AsString
            : null;
        var resolved = default(SqlValue);
        var found = subkey is not null && valueName is not null && (instanceMapped ? InstanceRegistryValues : MachineRegistryValues).TryGetValue($"{subkey}|{valueName}", out resolved);

        // OUTPUT form: write the value into the caller's @variable (or NULL)
        // and yield no result set. This is the shape SSMS uses.
        var outputSlot = arguments.FirstOrDefault(a => a.OutputSlot is not null).OutputSlot;
        if (outputSlot is not null)
        {
            outputSlot.Value = found ? resolved.CoerceTo(outputSlot.DeclaredType) : SqlValue.Null(outputSlot.DeclaredType);
            yield break;
        }

        // No OUTPUT parameter: real xp_instance_regread emits a (Value, Data)
        // row when the value resolves — Data an int for a DWORD value — and no
        // rows when it does not.
        if (!found)
        {
            yield return new SimulatedSqlResultSet(XpInstanceRegreadSchema, XpInstanceRegreadColumnNames, []);
            yield break;
        }
        SqlValue[][] rows = [[SqlValue.FromNVarchar(valueName!), resolved]];
        yield return new SimulatedSqlResultSet([SqlType.NVarchar, resolved.Type!], XpInstanceRegreadColumnNames, rows);
    }
}
