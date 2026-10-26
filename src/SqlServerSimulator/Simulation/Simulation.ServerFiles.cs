using System.Collections.Concurrent;

namespace SqlServerSimulator;

// The simulated server's own disk, as the surfaces that ask about paths see
// it — sys.dm_os_file_exists, xp_fileexist, xp_dirtree, xp_create_subdir and
// xp_delete_file, and the directories a backup creates on its way to a device —
// and the files statements read and write on it, held in memory or reached
// through the host's OpenServerFile. The server is SQL Server 2025 on Linux,
// whose file layer matches a path case aside, reads C:\ as / and settles . and
// .. (probed 2026-10-16).
partial class Simulation
{
    /// <summary>The directory a relative backup device path resolves against, <c>SERVERPROPERTY('InstanceDefaultBackupPath')</c>.</summary>
    internal const string DefaultBackupDirectory = "/var/opt/mssql/data";

    /// <summary>
    /// Opens the files on the simulated server's disk that statements read
    /// and write by path: the devices <c>BACKUP</c> writes and <c>RESTORE</c>
    /// reads, a detached database's files, the undo file of <c>RESTORE …
    /// WITH STANDBY</c>, the data and format files of <c>BULK INSERT</c> and
    /// <c>OPENROWSET(BULK …)</c>, and the attachments <c>sp_send_dbmail</c>
    /// names. Defaults to <see langword="null"/>, under which the disk lives in
    /// memory for the life of the <see cref="Simulation"/>: a file one
    /// statement writes, another reads, and nothing else is there. Set it in
    /// an object initializer to reach the host's files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delegate receives the path as SQL Server on Linux reads it: rooted,
    /// one <c>/</c> between names, <c>.</c> and <c>..</c> settled, a drive's
    /// <c>X:\</c> read as the root, and a relative backup device under
    /// <c>/var/opt/mssql/data</c>; any other relative path names no file and
    /// never reaches the delegate. The server matches a path case aside, as
    /// the in-memory disk does, and the delegate receives the case as written.
    /// </para>
    /// <para>
    /// Given <see cref="FileAccess.Read"/>, the delegate returns a readable
    /// <see cref="Stream"/>, which the simulator reads to its end and
    /// disposes, or <see langword="null"/> for a file that doesn't exist; a
    /// statement asking only whether a file exists disposes the stream
    /// unread. Given <see cref="FileAccess.Write"/>, it returns a writable
    /// stream, which the simulator writes the file's whole new content to and
    /// disposes, or <see langword="null"/> for a path that can't be written.
    /// Whatever the delegate or its stream throws is the operating-system
    /// error the statement reports — 2 for a <see cref="FileNotFoundException"/>,
    /// 3 for a <see cref="DirectoryNotFoundException"/>, 5 for anything else —
    /// and its message reaches no client. A file longer than 1 GiB raises
    /// <see cref="NotSupportedException"/> before the rest of it is read.
    /// Several sessions may call the delegate at once.
    /// </para>
    /// <para>
    /// <strong>The delegate is the boundary between the simulated server and
    /// the host's files.</strong> What it opens, a session reaches with the
    /// permission its statement needs, as on SQL Server: <c>CONTROL SERVER</c>
    /// to read a bulk file, <c>sysadmin</c> to attach one to mail,
    /// <c>CREATE ANY DATABASE</c> to read a backup, and only <c>BACKUP
    /// DATABASE</c> or <c>BACKUP LOG</c> in some database to write one. Map the
    /// paths into a folder kept for the purpose, above all when the
    /// simulation listens on the network endpoint:
    /// </para>
    /// <code>
    /// var root = Path.GetFullPath("server-disk");
    /// var simulation = new Simulation
    /// {
    ///     OpenServerFile = (path, access) =>
    ///     {
    ///         var full = Path.GetFullPath(Path.Join(root, path));
    ///         if (path.Contains(':') || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    ///             return null;
    ///         if (access == FileAccess.Read)
    ///             return File.Exists(full) ? File.OpenRead(full) : null;
    ///         Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    ///         return File.Create(full);
    ///     },
    /// };
    /// </code>
    /// <para>
    /// <c>Path.Join</c> keeps the root where <c>Path.Combine</c> would drop it
    /// for a rooted path, and the colon check keeps a name Linux allows from
    /// opening an alternate data stream on Windows. A backup or a detached
    /// database's file holds the simulator's own format, which only the
    /// simulator build that wrote it reads; data, format and attachment files
    /// are SQL Server's own formats.
    /// </para>
    /// </remarks>
    public Func<string, FileAccess, Stream?>? OpenServerFile { get; init; }

    /// <summary>
    /// The files written while <see cref="OpenServerFile"/> is unset — backup
    /// devices, detached databases' files, standby undo files — by canonical
    /// path, case aside.
    /// </summary>
    internal readonly ConcurrentDictionary<string, byte[]> DiskFiles = new(concurrencyLevel: 1, capacity: 0, StringComparer.OrdinalIgnoreCase);

    /// <summary>The longest file the simulator reads whole; a longer one is refused before the rest of it is read.</summary>
    private const int MaxServerFileLength = 1 << 30;

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

    /// <summary>The canonical path the disk knows a device by, a relative one under <see cref="DefaultBackupDirectory"/>.</summary>
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
    /// <see cref="ServerFiles"/>, or a path the host's
    /// <see cref="OpenServerFile"/> opens for reading.
    /// </summary>
    internal bool IsServerFile(string canonical)
    {
        foreach (var file in this.ServerFiles())
        {
            if (string.Equals(file, canonical, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return this.HostOpensFile(canonical);
    }

    /// <summary>Whether <paramref name="path"/> names a file on the server's disk, its content unread.</summary>
    internal bool ServerFileExists(string path) => CanonicalServerPath(path) is { } canonical && this.IsServerFile(canonical);

    /// <summary>
    /// The whole content of the file at <paramref name="canonical"/>, from
    /// the in-memory disk or the host's <see cref="OpenServerFile"/>; null
    /// when it can't be read, with the operating-system error real reports.
    /// </summary>
    internal byte[]? ReadServerFile(string canonical, out int osError)
    {
        osError = 2;
        if (this.OpenServerFile is not { } open)
            return this.DiskFiles.GetValueOrDefault(canonical);
        // The delegate and the stream it opens are the host's code.
        using var culture = CultureScope.Host();
#pragma warning disable CA1031 // Whatever the host's code throws is the statement's operating-system error, its message kept from the client.
        try
        {
            using var stream = open(canonical, FileAccess.Read);
            if (stream is null)
                return null;
            if (ReadWhole(stream) is { } content)
                return content;
        }
        catch (Exception ex)
        {
            osError = OperatingSystemError(ex);
            return null;
        }
#pragma warning restore CA1031
        throw new NotSupportedException($"The simulator reads a server file of at most 1 GiB, and '{canonical}' is longer.");
    }

    /// <summary>The rest of <paramref name="stream"/>, or null once it runs past <see cref="MaxServerFileLength"/>.</summary>
    private static byte[]? ReadWhole(Stream stream)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MaxServerFileLength)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > MaxServerFileLength)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Replaces the file at <paramref name="canonical"/> with
    /// <paramref name="content"/>, on the in-memory disk or through the host's
    /// <see cref="OpenServerFile"/>, creating the directories above it as
    /// real's Linux server does; 0, or the operating-system error real reports.
    /// </summary>
    internal int WriteServerFile(string canonical, byte[] content)
    {
        if (this.OpenServerFile is { } open)
        {
            using var culture = CultureScope.Host();
#pragma warning disable CA1031 // As in ReadServerFile.
            try
            {
                using var stream = open(canonical, FileAccess.Write);
                if (stream is null)
                    return 3;
                stream.Write(content);
            }
            catch (Exception ex)
            {
                return OperatingSystemError(ex);
            }
#pragma warning restore CA1031
        }
        else
        {
            this.DiskFiles[canonical] = content;
        }
        if (ServerParentDirectory(canonical) is { } parent)
            this.CreateServerDirectory(parent);
        return 0;
    }

    /// <summary>Whether the host's <see cref="OpenServerFile"/> opens <paramref name="canonical"/> for reading, its stream disposed unread.</summary>
    private bool HostOpensFile(string canonical)
    {
        if (this.OpenServerFile is not { } open)
            return false;
        using var culture = CultureScope.Host();
#pragma warning disable CA1031 // As in ReadServerFile.
        try
        {
            using var stream = open(canonical, FileAccess.Read);
            return stream is not null;
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>The operating-system error real reports for a file the host's code couldn't open.</summary>
    private static int OperatingSystemError(Exception ex) => ex switch
    {
        FileNotFoundException => 2,
        DirectoryNotFoundException => 3,
        _ => 5,
    };

    /// <summary>
    /// Every file the simulation can name by canonical path: each database's
    /// files, the error log and its archives, the in-memory disk's files,
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
        foreach (var (key, _) in this.DiskFiles)
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
