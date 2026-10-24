using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Logical backup devices: sp_addumpdevice / sp_dropdevice register and remove
// them, sys.backup_devices, sysdevices and sp_helpdevice list them, and BACKUP /
// RESTORE name one in place of DISK = path. Each check and its source line are
// real's procedure's (probed 2026-10-15 against SQL Server 2025).
partial class Simulation
{
    /// <summary>
    /// One logical backup device: its name, its <c>sys.backup_devices.type</c>
    /// (2 disk, 5 tape, 7 virtual device), the physical name as registered,
    /// and the order it was added in, which is the order every listing keeps.
    /// </summary>
    internal sealed class LogicalBackupDevice(string name, byte type, string physicalName, long sequence)
    {
        public readonly string Name = name;
        public readonly byte Type = type;
        public readonly string PhysicalName = physicalName;
        public readonly long Sequence = sequence;

        /// <summary>The <c>type_desc</c> <c>sys.backup_devices</c> reports.</summary>
        public string TypeDescription => this.Type switch
        {
            2 => "DISK",
            5 => "TAPE",
            _ => "VIRTUAL_DEVICE",
        };

        /// <summary>The word <c>sp_helpdevice</c> describes the device by, as <c>sp_addumpdevice</c> spells its type.</summary>
        public string TypeWord => this.Type switch
        {
            2 => "disk",
            5 => "tape",
            _ => "virtual_device",
        };
    }

    /// <summary>
    /// The server's logical backup devices, keyed by name under
    /// <see cref="BuiltInToken.Comparer"/> with trailing spaces trimmed, as
    /// <c>master</c>'s collation compares them. Server scope: a device outlives
    /// every database, as real keeps it in <c>master</c>.
    /// </summary>
    internal readonly ConcurrentDictionary<string, LogicalBackupDevice> LogicalBackupDevices = new(concurrencyLevel: 1, capacity: 0, BuiltInToken.Comparer);

    private long lastBackupDeviceSequence;

    /// <summary>The logical backup devices in the order they were added.</summary>
    internal LogicalBackupDevice[] LogicalBackupDevicesInOrder()
    {
        var devices = this.LogicalBackupDevices.ToArray();
        var ordered = new LogicalBackupDevice[devices.Length];
        for (var i = 0; i < devices.Length; i++)
            ordered[i] = devices[i].Value;
        Array.Sort(ordered, static (x, y) => x.Sequence.CompareTo(y.Sequence));
        return ordered;
    }

    private static readonly SystemProcedureParameter[] AddUmpDeviceParameters =
    [
        new("devtype", SqlType.NVarchar, 20),
        new("logicalname", SqlType.NVarchar, 128),
        new("physicalname", SqlType.NVarchar, 260),
        new("cntrltype", SqlType.SmallInt, 0, SqlValue.Null(SqlType.SmallInt)),
        new("devstatus", SqlType.NVarchar, 40, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] DropDeviceParameters =
    [
        new("logicalname", SqlType.NVarchar, 128),
        new("delfile", SqlType.NVarchar, 7, SqlValue.Null(SqlType.NVarchar)),
    ];

    /// <summary>Whether two names compare equal as <c>master</c>'s case-insensitive collation compares them, trailing spaces ignored.</summary>
    private static bool DeviceNamesEqual(string x, string y) =>
        string.Equals(x.TrimEnd(' '), y.TrimEnd(' '), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>sp_addumpdevice @devtype, @logicalname, @physicalname [, @cntrltype
    /// [, @devstatus]]</c>: registers a logical backup device. Inside a
    /// transaction it is Msg 15002, for a login outside <c>sysadmin</c> and
    /// <c>diskadmin</c> Msg 15247, then a type other than <c>disk</c>,
    /// <c>tape</c> or <c>virtual_device</c> (case and trailing spaces aside)
    /// Msg 15044, a NULL logical name Msg 15045, an empty one
    /// <c>sp_validname</c>'s Msg 15004, a NULL physical name Msg 15046, a
    /// type's own word as the name Msg 15285, a name already registered Msg
    /// 15026 and a physical name another device holds Msg 15061. The obsolete
    /// last two parameters are read and ignored, and the physical name is
    /// kept as written, an empty one included.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddUmpDevice(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_addumpdevice", calledAs, arguments, AddUmpDeviceParameters);
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction("sp_addumpdevice"), 17);
        if (!SessionInServerRole(batch, DiskAdminRoleId))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotHavePermission(), 24);

        var typeWritten = values[0].IsNull ? null : values[0].AsString;
        var type = typeWritten is null ? (byte)0
            : DeviceNamesEqual(typeWritten, "disk") ? (byte)2
            : DeviceNamesEqual(typeWritten, "tape") ? (byte)5
            : DeviceNamesEqual(typeWritten, "virtual_device") ? (byte)7
            : (byte)0;
        if (type == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UnknownBackupDeviceType(typeWritten ?? "(null)"), 37);
        if (values[1].IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.LogicalDeviceNameNull(), 44);
        var name = RequireValidName(values[1]);
        if (values[2].IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PhysicalDeviceNameNull(), 55);
        if (DeviceNamesEqual(name, "disk") || DeviceNamesEqual(name, "tape") || DeviceNamesEqual(name, "virtual_device"))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.SpecialWordAsDeviceName(name), 62);

        var physicalName = values[2].AsString;
        var device = new LogicalBackupDevice(name, type, physicalName, Interlocked.Increment(ref this.lastBackupDeviceSequence));
        var key = name.TrimEnd(' ');
        if (!this.LogicalBackupDevices.TryAdd(key, device))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.LogicalDeviceAlreadyExists(name), 74);
        foreach (var (_, other) in this.LogicalBackupDevices)
        {
            if (!ReferenceEquals(other, device) && DeviceNamesEqual(other.PhysicalName, physicalName))
            {
                _ = this.LogicalBackupDevices.TryRemove(new KeyValuePair<string, LogicalBackupDevice>(key, device));
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.PhysicalDeviceAlreadyExists(physicalName), 82);
            }
        }

        // Its statements, the last two in a transaction of its own (captured
        // 2026-10-17 against SQL Server 2025).
        foreach (var step in ProcedureSteps(batch, "SOIISIIISXIII"))
            yield return step;
        foreach (var outcome in ProcedureTransactionSteps(batch, ProcedureSteps(batch, "II")))
            yield return outcome;
        yield return StatementDone(batch, StatementDoneKind.Select, 1);
    }

    /// <summary>
    /// <c>sp_dropdevice @logicalname [, @delfile]</c>: removes a logical
    /// backup device, sending Msg 15463 "Device dropped." A <c>@delfile</c>
    /// other than <c>delfile</c> (case and trailing spaces aside, cut to its
    /// seven characters) is Msg 15216 ahead of everything else, then a
    /// transaction Msg 15002, a login outside <c>sysadmin</c> and
    /// <c>diskadmin</c> Msg 15247 and a device that doesn't exist Msg 15012.
    /// <c>delfile</c> also deletes the device's content, with real's Msg 2503,
    /// when the simulation holds it in memory; a file the host's
    /// <see cref="OpenBackupFile"/> opens is left in place, since the host
    /// supplies no way to delete one.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDropDevice(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_dropdevice", calledAs, arguments, DropDeviceParameters);
        var deleteFile = false;
        if (!values[1].IsNull)
        {
            if (!DeviceNamesEqual(values[1].AsString, "delfile"))
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidDelfileOption(values[1].AsString), 13);
            deleteFile = true;
        }
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureCannotRunInTransaction("sp_dropdevice"), 22);
        if (!SessionInServerRole(batch, DiskAdminRoleId))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotHavePermission(), 30);
        var name = values[0].IsNull ? "(null)" : values[0].AsString;
        if (values[0].IsNull || !this.LogicalBackupDevices.TryRemove(name.TrimEnd(' '), out var device))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.BackupDeviceDoesNotExist(name), 37);
        if (deleteFile && device.Type == 2 && this.OpenBackupFile is null && this.BackupDevices.TryRemove(BackupDeviceKey(device.PhysicalName), out _))
            QueueProcedureMessage(batch, calledAs, 2503, $"Successfully deleted the physical file '{device.PhysicalName}'.", 43);
        // Its statements, the DBCC among them, then the confirmation it
        // raises and an assignment (captured 2026-10-17 against SQL Server
        // 2025).
        foreach (var step in ProcedureSteps(batch, "SIOIIIIDI"))
            yield return step;
        yield return new SimulatedInfoOutcome(SimulatedSqlException.SystemProcedureMessage(batch, calledAs, 52, 15463, "Device dropped."));
        foreach (var step in ProcedureSteps(batch, "RS"))
            yield return step;
    }

    /// <summary>
    /// The logical backup device <paramref name="name"/> a <c>BACKUP</c> or
    /// <c>RESTORE</c> names, as the path it opens and the name its messages
    /// give it, <c>logical(physical)</c>: Msg 3206 for a device that doesn't
    /// exist, and Msg 3201 for a tape or virtual device, which a Linux server
    /// can't open.
    /// </summary>
    private static BackupDeviceTarget ResolveLogicalBackupDevice(BatchContext batch, string name, string verb)
    {
        if (!batch.Connection.Simulation.LogicalBackupDevices.TryGetValue(name.TrimEnd(' '), out var device))
            throw SimulatedSqlException.BackupDeviceDoesNotExist(name, verb);
        if (device.Type != 2)
            throw SimulatedSqlException.CannotOpenNonDiskBackupDevice(device.Name, device.PhysicalName, device.Type == 5, verb);
        return new(device.PhysicalName, $"{device.Name}({device.PhysicalName})", device.Name);
    }

    /// <summary>
    /// One device a <c>BACKUP</c> or <c>RESTORE</c> reads or writes: the path
    /// handed to <see cref="OpenBackupFile"/>, the name its messages give
    /// it — the path itself for <c>DISK = path</c>, <c>logical(physical)</c>
    /// for a logical device — and the name the error log gives it, a logical
    /// device's own name alone.
    /// </summary>
    private readonly struct BackupDeviceTarget(string path, string display, string logged)
    {
        public readonly string Path = path;
        public readonly string Display = display;
        public readonly string Logged = logged;
    }
}
