using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Backup;

namespace SqlServerSimulator;

// The extended procedures over the server's disk — xp_create_subdir,
// xp_delete_file, xp_fileexist and xp_dirtree — and sys.dm_os_file_exists,
// probed 2026-10-16 against SQL Server 2025 on Linux. A refusal of the first
// two is an error the batch runs past, neither entering a CATCH nor setting
// @@ERROR, the procedure answering 1.
partial class Simulation
{
    /// <summary>
    /// <c>sys.dm_os_file_exists(path)</c>'s one row: whether the path is a
    /// file, whether it is a directory, and whether the directory above it
    /// is; a relative path is none of the three, and a NULL or empty one
    /// Msg 2561 state 102.
    /// </summary>
    internal static List<SqlValue[]> FileExistsRows(BatchContext batch, SqlValue path)
    {
        if (path.IsNull || path.CoerceTo(SqlType.NVarchar).AsString.Length == 0)
            throw SimulatedSqlException.ParameterIncorrectForStatement(0, state: 102);
        var (file, directory, parent) = batch.Connection.Simulation.PathKind(path.CoerceTo(SqlType.NVarchar).AsString);
        return [[SqlValue.FromByte(file ? (byte)1 : (byte)0), SqlValue.FromByte(directory ? (byte)1 : (byte)0), SqlValue.FromByte(parent ? (byte)1 : (byte)0)]];
    }

    /// <summary>The columns of <see cref="FileExistsRows"/>.</summary>
    internal static readonly (string Name, SqlType Type)[] FileExistsColumns =
        [("file_exists", SqlType.TinyInt), ("file_is_a_directory", SqlType.TinyInt), ("parent_directory_exists", SqlType.TinyInt)];

    /// <summary>What <paramref name="path"/> names on the server's disk, as <c>dm_os_file_exists</c> and <c>xp_fileexist</c> report it.</summary>
    private (bool File, bool Directory, bool Parent) PathKind(string path)
    {
        if (CanonicalServerPath(path) is not { } canonical)
            return (false, false, false);
        var file = this.IsServerFile(canonical);
        var directory = !file && this.IsServerDirectory(canonical);
        var parent = ServerParentDirectory(canonical) is not { } above || this.IsServerDirectory(above);
        return (file, directory, parent);
    }

    /// <summary>
    /// <c>xp_create_subdir 'path'</c>: creates the directory and every one
    /// above it, answering 0, an existing directory included. A relative,
    /// NULL or missing path is Msg 22048 severity 15 (<c>Invalid
    /// Parameter</c>), and a path at or beneath a file Msg 22048 severity 16
    /// naming operating-system error 183. Only a <c>sysadmin</c> or a holder
    /// of <c>EXECUTE</c> on it may call it (Msg 229).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpCreateSubdir(BatchContext batch, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireExtendedProcedureExecute(batch, "xp_create_subdir", calledAs);
        var simulation = batch.Connection.Simulation;
        SimulatedSqlException? refusal = null;
        if (arguments.Count == 0 || arguments[0].IsDefault || arguments[0].Value.IsNull || arguments[0].Value.Type.Category != SqlTypeCategory.String
            || CanonicalServerPath(arguments[0].Value.AsString) is not { } canonical)
        {
            refusal = SimulatedSqlException.ExtendedProcedureInvalidParameter(22048);
        }
        else
        {
            for (var directory = (string?)canonical; directory is not null; directory = ServerParentDirectory(directory))
            {
                if (simulation.IsServerFile(directory))
                {
                    refusal = SimulatedSqlException.ExtendedProcedureOperatingSystemError("xp_create_subdir", 22048, 183);
                    break;
                }
            }
            if (refusal is null)
                simulation.CreateServerDirectory(canonical);
        }
        SetReturnCode(batch, returnCodeVar, refusal is null ? 0 : 1);
        if (refusal is not null)
            yield return ExtendedProcedureRefusal(batch, refusal);
    }

    /// <summary>
    /// <c>xp_delete_file type, 'folder', 'extension', 'date' [, subfolders]</c>
    /// deletes each backup file in the folder — and with <c>subfolders</c> 1,
    /// in the folders directly inside it — whose name ends in the extension
    /// and whose last backup set finished before the date;
    /// <c>xp_delete_file type, 'file'</c> deletes that one backup file. The
    /// date reads as <c>yyyy-mm-dd</c> with an optional <c>Thh:mi:ss</c>; any
    /// other shape, a NULL extension or a missing argument is Msg 22049
    /// severity 15 (<c>Invalid Parameter</c>), and a folder that doesn't
    /// exist Msg 22049 severity 16 naming operating-system error 2. Report
    /// files (type 1) are never written here, so that type deletes nothing.
    /// Only the in-memory device store can be listed: with
    /// <see cref="OpenBackupFile"/> set, nothing is deleted.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpDeleteFile(BatchContext batch, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireExtendedProcedureExecute(batch, "xp_delete_file", calledAs);
        var refusal = batch.Connection.Simulation.DeleteBackupFiles(arguments);
        SetReturnCode(batch, returnCodeVar, refusal is null ? 0 : 1);
        if (refusal is not null)
            yield return ExtendedProcedureRefusal(batch, refusal);
    }

    /// <summary>The work of <c>xp_delete_file</c>: null once done, else its refusal.</summary>
    private SimulatedSqlException? DeleteBackupFiles(List<ProcArgument> arguments)
    {
        var invalid = SimulatedSqlException.ExtendedProcedureInvalidParameter(22049);
        if (arguments.Count is < 2 or > 5 || !IsGivenText(arguments[1]) || arguments[0].IsDefault || arguments[0].Value.IsNull)
            return invalid;
        var type = arguments[0].Value.CoerceTo(SqlType.Int32).AsInt32;
        if (CanonicalServerPath(arguments[1].Value.AsString) is not { } target)
            return SimulatedSqlException.ExtendedProcedureOperatingSystemError("xp_delete_file", 22049, 2);
        if (arguments.Count == 2)
        {
            if (type == 0 && this.IsBackupFile(target))
                _ = this.BackupDevices.TryRemove(target, out _);
            return null;
        }
        if (arguments.Count < 4 || !IsGivenText(arguments[2]) || !IsGivenText(arguments[3])
            || !DateTime.TryParseExact(arguments[3].Value.AsString, ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var before))
        {
            return invalid;
        }
        if (!this.IsServerDirectory(target))
            return SimulatedSqlException.ExtendedProcedureOperatingSystemError("xp_delete_file", 22049, 2);
        if (type != 0)
            return null;
        var extension = "." + arguments[2].Value.AsString.TrimStart('.');
        var subfolders = arguments.Count == 5 && !arguments[4].IsDefault && !arguments[4].Value.IsNull && arguments[4].Value.CoerceTo(SqlType.Int32).AsInt32 != 0;
        List<string> folders = [target];
        if (subfolders)
        {
            foreach (var (name, isFile) in this.ServerDirectoryEntries(target))
            {
                if (!isFile)
                    folders.Add((target == "/" ? "/" : target + "/") + name);
            }
        }
        foreach (var folder in folders)
        {
            foreach (var (name, isFile) in this.ServerDirectoryEntries(folder))
            {
                var path = (folder == "/" ? "/" : folder + "/") + name;
                if (isFile && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    && this.BackupDevices.TryGetValue(path, out var bytes)
                    && BackupMedia.TryRead(bytes, out var media, out _) == BackupMedia.ReadOutcome.Read
                    && media!.Sets.Count > 0 && media.Sets[^1].FinishUtc < before)
                {
                    _ = this.BackupDevices.TryRemove(path, out _);
                }
            }
        }
        return null;
    }

    /// <summary>Whether the in-memory device at <paramref name="canonical"/> holds a backup.</summary>
    private bool IsBackupFile(string canonical) =>
        this.BackupDevices.TryGetValue(canonical, out var bytes) && BackupMedia.TryRead(bytes, out _, out _) == BackupMedia.ReadOutcome.Read;

    private static bool IsGivenText(ProcArgument argument) =>
        !argument.IsDefault && !argument.Value.IsNull && argument.Value.Type.Category == SqlTypeCategory.String;

    /// <summary>
    /// <c>xp_fileexist 'path' [, @exists OUTPUT]</c>: a row of whether the
    /// path is a file, a directory and has a directory above it — or, given
    /// the output, 1 in it for a file alone. A caller short of
    /// <c>sysadmin</c> sees no path at all.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpFileExist(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var simulation = batch.Connection.Simulation;
        var (file, directory, parent) = arguments.Count > 0 && IsGivenText(arguments[0]) && simulation.SessionHoldsServerPermission(batch.Connection, Permission.ControlServer)
            ? simulation.PathKind(arguments[0].Value.AsString)
            : (false, false, false);
        if (arguments.Count >= 2 && arguments[1].OutputSlot is { } output)
        {
            output.Value = SqlValue.FromInt32(file ? 1 : 0).CoerceTo(output.DeclaredType);
            yield break;
        }
        yield return new SimulatedSqlResultSet(FileExistSchema, FileExistColumnNames,
            [[SqlValue.FromByte(file ? (byte)1 : (byte)0), SqlValue.FromByte(directory ? (byte)1 : (byte)0), SqlValue.FromByte(parent ? (byte)1 : (byte)0)]]);
    }

    private static readonly SqlType[] FileExistSchema = [SqlType.TinyInt, SqlType.TinyInt, SqlType.TinyInt];

    private static readonly string[] FileExistColumnNames = ["File Exists", "File is a Directory", "Parent Directory Exists"];

    /// <summary>
    /// <c>xp_dirtree 'path' [, depth [, files]]</c>: the directories beneath
    /// the path, each with its depth from 1, to <c>depth</c> levels (0 or
    /// omitted for all) and with the files too when <c>files</c> is nonzero
    /// (a third <c>file</c> column, 1 on a file), depth first. A missing path
    /// lists nothing, as it does for a caller short of <c>sysadmin</c>.
    /// Real lists each directory in its file system's order; here names
    /// sort.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpDirtree(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var simulation = batch.Connection.Simulation;
        var depth = arguments.Count >= 2 && !arguments[1].IsDefault && !arguments[1].Value.IsNull ? arguments[1].Value.CoerceTo(SqlType.Int32).AsInt32 : 0;
        var files = arguments.Count >= 3 && !arguments[2].IsDefault && !arguments[2].Value.IsNull && arguments[2].Value.CoerceTo(SqlType.Int32).AsInt32 != 0;
        var rows = new List<SqlValue[]>();
        if (arguments.Count > 0 && IsGivenText(arguments[0]) && CanonicalServerPath(arguments[0].Value.AsString) is { } root
            && simulation.SessionHoldsServerPermission(batch.Connection, Permission.ControlServer) && simulation.IsServerDirectory(root))
        {
            Walk(root, 1);
        }
        yield return new SimulatedSqlResultSet(files ? DirtreeFileSchema : DirtreeSchema, files ? DirtreeFileColumnNames : DirtreeColumnNames, rows);

        void Walk(string directory, int level)
        {
            if (depth > 0 && level > depth)
                return;
            foreach (var (name, isFile) in simulation.ServerDirectoryEntries(directory))
            {
                if (isFile && !files)
                    continue;
                rows.Add(files
                    ? [SqlValue.FromString(DirtreeNameType, name), SqlValue.FromInt32(level), SqlValue.FromInt32(isFile ? 1 : 0)]
                    : [SqlValue.FromString(DirtreeNameType, name), SqlValue.FromInt32(level)]);
                if (!isFile)
                    Walk((directory == "/" ? "/" : directory + "/") + name, level + 1);
            }
        }
    }

    private static readonly NVarcharSqlType DirtreeNameType = NVarcharSqlType.Get(260, Collation.Baseline, Coercibility.CoercibleDefault);

    private static readonly SqlType[] DirtreeSchema = [DirtreeNameType, SqlType.Int32];

    private static readonly SqlType[] DirtreeFileSchema = [DirtreeNameType, SqlType.Int32, SqlType.Int32];

    private static readonly string[] DirtreeColumnNames = ["subdirectory", "depth"];

    private static readonly string[] DirtreeFileColumnNames = ["subdirectory", "depth", "file"];
}
