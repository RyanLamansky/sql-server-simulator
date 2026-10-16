using System.Globalization;
using System.Text;

namespace SqlServerSimulator;

/// <summary>
/// The instance's SQL Server error log, which <c>xp_readerrorlog</c> reads and
/// <c>xp_enumerrorlogs</c> lists: the current log (archive 0) and the archives
/// <c>sp_cycle_errorlog</c> / <c>DBCC ERRORLOG</c> leave behind, the newest
/// first, at most <see cref="ArchiveLimit"/> of them as real keeps by default.
/// Every time is UTC, the simulated server's clock.
/// </summary>
/// <remarks>
/// <para>
/// Real writes the log as UTF-8 lines of a 22-character centisecond timestamp,
/// a space, the process column padded to 12 and the text, each ending CRLF —
/// so an entry costs 37 bytes beyond its text, the size
/// <c>xp_enumerrorlogs</c> reports — and drops, unwritten, an entry whose text
/// passes 2,011 bytes, the error-number line ahead of it included (probed
/// 2026-10-16 against SQL Server 2025 by the file size before and after).
/// </para>
/// <para>
/// A log holds at most <see cref="EntryLimit"/> entries, where real's file
/// grows without bound: a write past it discards the oldest tenth, which
/// leaves the reported size counting only the entries kept.
/// </para>
/// </remarks>
internal sealed class ErrorLog
{
    /// <summary>The archives a cycle keeps, as real's default (<c>NumErrorLogs</c> unset) does.</summary>
    public const int ArchiveLimit = 6;

    /// <summary>The entries one log keeps before discarding its oldest.</summary>
    public const int EntryLimit = 20_000;

    /// <summary>The longest text, in UTF-8 bytes, real writes.</summary>
    private const int MaxTextBytes = 2011;

    /// <summary>The bytes a line spends beyond its text: timestamp, space, padded process column, CRLF.</summary>
    private const int LineOverhead = 37;

    /// <summary>The <c>-e</c> path real's startup parameters name, <c>SERVERPROPERTY('ErrorLogFileName')</c>'s.</summary>
    public const string FileName = "/var/opt/mssql/log/errorlog";

    /// <summary>One entry: when it was written, the process column (<c>Server</c>, <c>spid53</c>, <c>Logon</c>) and its text, which an empty <c>xp_logevent</c> message leaves NULL.</summary>
    internal readonly struct Entry(DateTime logDate, string processInfo, string? text)
    {
        public readonly DateTime LogDate = logDate;
        public readonly string ProcessInfo = processInfo;
        public readonly string? Text = text;
    }

    /// <summary>One log file: its entries, its last write and its size in bytes.</summary>
    private sealed class Log(DateTime created)
    {
        public readonly List<Entry> Entries = [];
        public DateTime LastWrite = created;
        public long Bytes;
    }

    private readonly Lock gate = new();
    private readonly List<Log> archives = [];
    private Log current;

    /// <summary>
    /// Opens the log as a server starting at <paramref name="startUtc"/> does:
    /// the version banner, the copyright, the process id
    /// (<c>SERVERPROPERTY('ProcessID')</c>'s), the log's own path, the startup
    /// parameters and the ready line, each a line real's startup writes in its
    /// first moments (probed 2026-10-16 against SQL Server 2025). Real's
    /// environmental lines between them — memory, CPU, NUMA, TLS, database
    /// recovery — are left out.
    /// </summary>
    public ErrorLog(DateTime startUtc)
    {
        this.current = new Log(startUtc);
        var at = Centiseconds(startUtc);
        WriteBanner(this.current, at, "Server");
        Append(this.current, at, "Server", "Registry startup parameters: \r\n\t -d /var/opt/mssql/data/master.mdf\r\n\t -l /var/opt/mssql/data/mastlog.ldf\r\n\t -e " + FileName);
        Append(this.current, at, "Server", "SQL Server is now ready for client connections. This is an informational message; no user action is required.");
    }

    /// <summary>The process column of an entry a session writes: <c>spid</c> and its session id.</summary>
    public static string SessionProcess(int spid) => string.Create(CultureInfo.InvariantCulture, $"spid{spid}");

    /// <summary>Writes one entry, unless its text is longer than real writes.</summary>
    public void Write(DateTime utc, string processInfo, string? text)
    {
        if (TooLong(text))
            return;
        lock (this.gate)
            Append(this.current, Centiseconds(utc), processInfo, text);
    }

    /// <summary>
    /// Writes an error as real logs one: <c>Error: n, Severity: s, State: t.</c>
    /// and then its message, both or neither.
    /// </summary>
    public void WriteError(DateTime utc, string processInfo, int number, int severity, int state, string message)
    {
        if (TooLong(message))
            return;
        var at = Centiseconds(utc);
        lock (this.gate)
        {
            Append(this.current, at, processInfo, string.Create(CultureInfo.InvariantCulture, $"Error: {number}, Severity: {severity}, State: {state}."));
            Append(this.current, at, processInfo, message);
        }
    }

    /// <summary>
    /// Starts a new current log as <c>sp_cycle_errorlog</c> does: the old one
    /// ends with real's line announcing the cycle and becomes archive 1, the
    /// oldest archive past <see cref="ArchiveLimit"/> is deleted, and the new
    /// one opens with the banner and the line saying it was reinitialized, all
    /// under the cycling session's process column (probed 2026-10-16 against
    /// SQL Server 2025).
    /// </summary>
    public void Cycle(DateTime utc, string processInfo)
    {
        var at = Centiseconds(utc);
        lock (this.gate)
        {
            Append(this.current, at, processInfo, "Attempting to cycle error log. This is an informational message only; no user action is required.");
            this.archives.Insert(0, this.current);
            if (this.archives.Count > ArchiveLimit)
                this.archives.RemoveAt(this.archives.Count - 1);
            this.current = new Log(at);
            WriteBanner(this.current, at, processInfo);
            Append(this.current, at, processInfo, "The error log has been reinitialized. See the previous log for older entries.");
        }
    }

    /// <summary>The entries of log <paramref name="number"/> (0 the current one) in the order written, or null when there is no such log.</summary>
    public Entry[]? Read(int number)
    {
        lock (this.gate)
        {
            var log = number == 0 ? this.current : number <= this.archives.Count ? this.archives[number - 1] : null;
            return log?.Entries.ToArray();
        }
    }

    /// <summary>Each log's number, last write and size in bytes, the current one first.</summary>
    public (int Number, DateTime LastWrite, long Bytes)[] Enumerate()
    {
        lock (this.gate)
        {
            var logs = new (int, DateTime, long)[this.archives.Count + 1];
            logs[0] = (0, this.current.LastWrite, this.current.Bytes);
            for (var i = 0; i < this.archives.Count; i++)
                logs[i + 1] = (i + 1, this.archives[i].LastWrite, this.archives[i].Bytes);
            return logs;
        }
    }

    /// <summary>The banner real opens a log with: <c>@@VERSION</c> over CRLF, the copyright, the process id and the log's path.</summary>
    private static void WriteBanner(Log log, DateTime at, string processInfo)
    {
        Append(log, at, processInfo, ReferenceBuild.Banner.Replace("\n", "\r\n", StringComparison.Ordinal));
        Append(log, at, processInfo, "UTC adjustment: 0:00");
        Append(log, at, processInfo, "(c) Microsoft Corporation.");
        Append(log, at, processInfo, "All rights reserved.");
        Append(log, at, processInfo, string.Create(CultureInfo.InvariantCulture, $"Server process ID is {Environment.ProcessId}."));
        Append(log, at, processInfo, $"Logging SQL Server messages in file '{FileName}'.");
    }

    private static void Append(Log log, DateTime at, string processInfo, string? text)
    {
        if (log.Entries.Count >= EntryLimit)
        {
            var dropped = EntryLimit / 10;
            for (var i = 0; i < dropped; i++)
                log.Bytes -= LineBytes(log.Entries[i].Text);
            log.Entries.RemoveRange(0, dropped);
        }
        log.Entries.Add(new Entry(at, processInfo, text));
        log.Bytes += LineBytes(text);
        log.LastWrite = at;
    }

    private static int LineBytes(string? text) => LineOverhead + (text is null ? 0 : Encoding.UTF8.GetByteCount(text));

    private static bool TooLong(string? text) => text is not null && text.Length > MaxTextBytes / 3 && Encoding.UTF8.GetByteCount(text) > MaxTextBytes;

    /// <summary>A time as the log's timestamp keeps it, to the hundredth of a second.</summary>
    private static DateTime Centiseconds(DateTime utc) => new(utc.Ticks - (utc.Ticks % (TimeSpan.TicksPerMillisecond * 10)), DateTimeKind.Unspecified);
}
