using System.Collections.Concurrent;

namespace SqlServerSimulator;

// The simulated server's own disk, as the surfaces that ask about paths see
// it — sys.dm_os_file_exists, xp_fileexist, xp_dirtree, xp_create_subdir and
// xp_delete_file, and the directories a backup creates on its way to a device.
// The server is SQL Server 2025 on Linux, whose file layer matches a path case
// aside, reads C:\ as / and settles . and .. (probed 2026-10-16).
partial class Simulation
{
    /// <summary>The directory a relative backup device path resolves against, <c>SERVERPROPERTY('InstanceDefaultBackupPath')</c>.</summary>
    internal const string DefaultBackupDirectory = "/var/opt/mssql/data";

    /// <summary>
    /// The directories the server's disk holds beyond those a file implies:
    /// the root, the installation's tree and <c>/tmp</c>, then each one
    /// <c>xp_create_subdir</c> or a backup creates, by canonical path.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> serverDirectories = new(
        concurrencyLevel: 1,
        Array.ConvertAll(["/", "/tmp", "/var", "/var/opt", "/var/opt/mssql", "/var/opt/mssql/data", "/var/opt/mssql/log"], static path => new KeyValuePair<string, byte>(path, 0)),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="path"/> as the server's file layer reads it — rooted,
    /// one <c>/</c> between names, <c>.</c> and <c>..</c> settled, a drive's
    /// <c>X:\</c> as the root — or null for a relative path, which names no
    /// file.
    /// </summary>
    internal static string? CanonicalServerPath(string path)
    {
        var text = path.Replace('\\', '/');
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
            text = text[2..];
        if (!text.StartsWith('/'))
            return null;
        var names = new List<string>();
        foreach (var name in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (name == ".")
                continue;
            if (name == "..")
            {
                if (names.Count > 0)
                    names.RemoveAt(names.Count - 1);
                continue;
            }
            names.Add(name);
        }
        return "/" + string.Join('/', names);
    }

    /// <summary>A backup device path as real reaches it: a relative one under <see cref="DefaultBackupDirectory"/>.</summary>
    internal static string ResolveBackupDevicePath(string path) =>
        CanonicalServerPath(path) is null && !path.StartsWith('\\') ? DefaultBackupDirectory + "/" + path : path;

    /// <summary>The key a device's bytes sit under in <see cref="BackupDevices"/>.</summary>
    private static string BackupDeviceKey(string path) => CanonicalServerPath(ResolveBackupDevicePath(path)) ?? path;

    /// <summary>The directory holding <paramref name="canonical"/>, null for the root.</summary>
    internal static string? ServerParentDirectory(string canonical) =>
        canonical == "/" ? null : canonical.LastIndexOf('/') is var slash and > 0 ? canonical[..slash] : "/";

    /// <summary>Whether <paramref name="canonical"/> is a directory: one the disk holds, or one a file lies beneath.</summary>
    internal bool IsServerDirectory(string canonical)
    {
        if (this.serverDirectories.ContainsKey(canonical))
            return true;
        var prefix = canonical == "/" ? "/" : canonical + "/";
        foreach (var file in this.ServerFiles())
        {
            if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="canonical"/> is a file: one of
    /// <see cref="ServerFiles"/>, or — with <see cref="OpenBackupFile"/> set —
    /// a path the host opens for reading.
    /// </summary>
    internal bool IsServerFile(string canonical)
    {
        foreach (var file in this.ServerFiles())
        {
            if (string.Equals(file, canonical, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (this.OpenBackupFile is not { } open)
            return false;
        try
        {
            using var stream = open(canonical, FileAccess.Read);
            return stream is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every file the simulation can name by canonical path: each database's
    /// files, the error log and its archives, the in-memory backup devices,
    /// the default trace's file, and the files traces, audits and Extended
    /// Events sessions have written — the last once anything has made the
    /// Extended Events server, before which no statement has named one.
    /// </summary>
    private IEnumerable<string> ServerFiles()
    {
        foreach (var (_, database) in this.Databases)
        {
            foreach (var file in database.FilesInOrder())
            {
                if (!file.IsContainer && CanonicalServerPath(file.PhysicalName) is { } canonical)
                    yield return canonical;
            }
        }
        foreach (var (number, _, _) in this.ErrorLog.Enumerate())
            yield return number == 0 ? ErrorLog.FileName : ErrorLog.FileName + "." + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, _) in this.BackupDevices)
            yield return key;
        yield return this.TracesIfMade?.DefaultLog.CurrentPath ?? BuiltInResources.DefaultTracePath;
        if (this.TracesIfMade is { } traces)
        {
            foreach (var (path, _) in traces.Files)
            {
                if (CanonicalServerPath(path) is { } canonical)
                    yield return canonical;
            }
        }
        if (this.AuditsIfMade is { } audits)
        {
            foreach (var (path, _) in audits.Files)
            {
                if (CanonicalServerPath(path) is { } canonical)
                    yield return canonical;
            }
        }
        if (this.ExtendedEventsIfMade is { } extendedEvents)
        {
            yield return extendedEvents.InternalEventFilePath;
            foreach (var (path, _) in extendedEvents.Files)
            {
                if (CanonicalServerPath(path) is { } canonical)
                    yield return canonical;
            }
        }
    }

    /// <summary>Creates <paramref name="canonical"/> and every directory above it.</summary>
    internal void CreateServerDirectory(string canonical)
    {
        for (var directory = (string?)canonical; directory is not null; directory = ServerParentDirectory(directory))
            _ = this.serverDirectories.TryAdd(directory, 0);
    }

    /// <summary>
    /// The entries directly inside directory <paramref name="canonical"/>,
    /// each a name and whether it is a file, in name order.
    /// </summary>
    internal List<(string Name, bool IsFile)> ServerDirectoryEntries(string canonical)
    {
        var prefix = canonical == "/" ? "/" : canonical + "/";
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, _) in this.serverDirectories)
            Collect(directory, isFile: false);
        foreach (var file in this.ServerFiles())
            Collect(file, isFile: true);
        var list = new List<(string Name, bool IsFile)>(entries.Count);
        foreach (var (name, isFile) in entries)
            list.Add((name, isFile));
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;

        void Collect(string path, bool isFile)
        {
            if (path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return;
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            // A name a deeper path passes through is a directory, whatever the path ends in.
            if (slash >= 0)
                entries[rest[..slash]] = false;
            else
                _ = entries.TryAdd(rest, isFile);
        }
    }
}
