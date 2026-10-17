using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The registry procedures over RegistryHive: xp_regread / xp_instance_regread
// and the enumerators xp_regenumvalues / xp_instance_regenumvalues /
// xp_regenumkeys / xp_instance_regenumkeys.
partial class Simulation
{
    private static readonly string[] XpInstanceRegreadColumnNames = ["Value", "Data"];

    private static readonly NVarcharSqlType RegistryNameType = NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType RegistryReadType = NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.Implicit);

    private static readonly string[] RegistrySubKeyColumnNames = ["SubKeyName"];

    /// <summary>
    /// Handles <c>EXEC master.dbo.xp_instance_regread</c> (SSMS reads the
    /// instance <c>SQLPath</c> registry value on connect to derive the SMO
    /// RootDirectory) and, with <paramref name="instanceMapped"/> false,
    /// <c>xp_regread</c>, which reads machine keys, from
    /// <see cref="RegistryHive"/>. The positional arguments are hive, subkey,
    /// value-name, and an optional <c>@output OUTPUT</c> variable. When an
    /// OUTPUT variable is supplied (SSMS's shape) the resolved value is written
    /// into it and no result set is yielded; otherwise a two-column
    /// <c>(Value, Data)</c> result set is produced, <c>Data</c> an
    /// <c>int</c> for a DWORD and an <c>nvarchar(4000)</c> otherwise. A value
    /// the key lacks, or a key the hive lacks, sends real's informational Msg
    /// 22001 and leaves the variable as it was, with no result set (probed
    /// 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpInstanceRegread(BatchContext batch, bool instanceMapped)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        // The positional arguments are hive, subkey, value name and an
        // optional @output.
        var subkey = arguments.Count >= 2 && !arguments[1].Value.IsNull
            ? arguments[1].Value.AsString.TrimStart('\\')
            : null;
        var valueName = arguments.Count >= 3 && !arguments[2].Value.IsNull
            ? arguments[2].Value.AsString
            : null;
        RegistryKey? key = null;
        var opened = subkey is not null && RegistryHive.TryOpen(subkey, instanceMapped, out key) == RegistryOpen.Found;
        if (!opened || valueName is null || key!.Find(valueName) is not { } value)
        {
            var missing = batch.InfoMessage(@class: 1, state: 1, number: 22001, opened
                ? "RegQueryValueEx() returned error 2, 'The system cannot find the file specified.'"
                : "RegOpenKeyEx() returned error 2, 'The system cannot find the file specified.'");
            missing.LineNumber = 0;
            yield return new SimulatedInfoOutcome(missing);
            yield break;
        }
        var data = value.Cells.Length < 2 || value.Cells[1] is not { } text ? SqlValue.Null(RegistryReadType)
            : value.Types[1] == 'D' ? SqlValue.FromInt32(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture))
            : SqlValue.FromString(RegistryReadType, text);

        // OUTPUT form: write the value into the caller's @variable and yield
        // no result set. This is the shape SSMS uses.
        var outputSlot = arguments.FirstOrDefault(a => a.OutputSlot is not null).OutputSlot;
        if (outputSlot is not null)
        {
            outputSlot.Value = data.CoerceTo(outputSlot.DeclaredType);
            yield break;
        }
        SqlValue[][] rows = [[SqlValue.FromString(RegistryNameType, value.Cells[0]!), data]];
        yield return new SimulatedSqlResultSet([RegistryNameType, data.Type!], XpInstanceRegreadColumnNames, rows);
    }

    /// <summary>
    /// <c>xp_regenumvalues</c> / <c>xp_instance_regenumvalues @hive, @subkey</c>
    /// and the <c>…regenumkeys</c> pair over <see cref="RegistryHive"/>, the
    /// instance forms naming the instance's key as
    /// <c>SOFTWARE\Microsoft\MSSQLServer</c> (probed 2026-10-17 against SQL
    /// Server 2025 on Linux):
    /// <list type="bullet">
    /// <item>The values come one result set each, in the key's order: the
    /// value's name as <c>Value</c> and its data as <c>Data</c> — an
    /// <c>int</c> for a DWORD, an <c>nvarchar(255)</c> otherwise, a
    /// multi-string's strings each a column — the default value's name
    /// NULL.</item>
    /// <item>The subkeys come as one <c>SubKeyName</c> result set.</item>
    /// <item>A key with no values, or no subkeys, sends no result set at all;
    /// one the registry lacks is Msg 22001 at severity 16
    /// (<c>RegOpenKeyEx()</c>'s error 2, or 161 for a path with a leading
    /// backslash), and an argument count other than two, a hive that isn't a
    /// predefined key or a NULL subkey Msg 22001 at severity 15 or 12, each
    /// returning 1 and leaving the batch running.</item>
    /// <item>A caller without <c>CONTROL SERVER</c> is refused
    /// <c>EXECUTE</c> (Msg 229).</item>
    /// </list>
    /// Another predefined hive, and a key real holds beneath one the hive
    /// carries only a level deep, raise <see cref="NotSupportedException"/>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpRegEnum(BatchContext batch, string procedure, string calledAs, bool instanceMapped, bool values)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireExtendedProcedureExecute(batch, procedure, calledAs);
        if (arguments.Count != 2 || arguments.Exists(static argument => argument.IsDefault))
        {
            yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.RegistryInvalidParameter(15));
            yield break;
        }
        var hive = arguments[0].Value is { IsNull: false } hiveValue && hiveValue.Type is { Category: SqlTypeCategory.String } ? hiveValue.AsString : null;
        if (hive is null || !IsPredefinedRegistryKey(hive))
        {
            yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.RegistryInvalidHive());
            yield break;
        }
        if (arguments[1].Value is not { IsNull: false, Type.Category: SqlTypeCategory.String } subkeyValue)
        {
            yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.RegistryInvalidParameter(12));
            yield break;
        }
        if (!hive.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{procedure} reads HKEY_LOCAL_MACHINE alone; the registry under {hive} is not modeled.");
        switch (RegistryHive.TryOpen(subkeyValue.AsString, instanceMapped, out var key))
        {
            case RegistryOpen.Missing:
                yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.RegistryKeyNotFound(2, "The system cannot find the file specified."));
                yield break;
            case RegistryOpen.InvalidPath:
                yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.RegistryKeyNotFound(161, "The specified path is invalid."));
                yield break;
            case RegistryOpen.Unmodeled:
                throw new NotSupportedException($"{procedure} of '{subkeyValue.AsString}': the registry carries the SQL Server and service keys whole and the keys above them one level deep, not this key's contents.");
        }
        if (!values)
        {
            if (key!.SubKeys.Count > 0)
            {
                yield return new SimulatedSqlResultSet([RegistryNameType], RegistrySubKeyColumnNames,
                    key.SubKeys.ConvertAll(static name => new[] { SqlValue.FromString(RegistryNameType, name) }));
            }
            yield break;
        }
        foreach (var value in key!.Values)
        {
            var types = new SqlType[value.Cells.Length];
            var names = new string[value.Cells.Length];
            var row = new SqlValue[value.Cells.Length];
            for (var i = 0; i < row.Length; i++)
            {
                var isInt = value.Types[i] == 'D';
                types[i] = isInt ? SqlType.Int32 : RegistryNameType;
                names[i] = i == row.Length - 1 ? "Data" : "Value";
                row[i] = value.Cells[i] is not { } text ? SqlValue.Null(types[i])
                    : isInt ? SqlValue.FromInt32(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture))
                    : SqlValue.FromString(RegistryNameType, text);
            }
            yield return new SimulatedSqlResultSet(types, names, [row]);
        }
    }

    /// <summary>Whether <paramref name="hive"/> names one of the registry's predefined keys, as the registry procedures spell them (no <c>HKLM</c> short forms).</summary>
    private static bool IsPredefinedRegistryKey(string hive) =>
        hive.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
        || hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
        || hive.Equals("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase)
        || hive.Equals("HKEY_USERS", StringComparison.OrdinalIgnoreCase)
        || hive.Equals("HKEY_CURRENT_CONFIG", StringComparison.OrdinalIgnoreCase);
}
