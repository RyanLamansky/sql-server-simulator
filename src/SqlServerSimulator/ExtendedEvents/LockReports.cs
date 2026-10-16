using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// One frame of a session's execution stack as real's deadlock and
/// blocked-process reports give it: the module — <c>adhoc</c> for a batch —
/// the statement's line and byte offsets, the handle of the text the offsets
/// index, and the statement's text, null where real writes <c>unknown</c>.
/// </summary>
internal readonly struct ReportFrame(string procName, int line, int statementStart, int statementEnd, byte[] sqlHandle, string? text)
{
    public readonly string ProcName = procName;
    public readonly int Line = line;
    public readonly int StatementStart = statementStart;

    /// <summary>The offset of the statement's last character, or -1 for one running to its text's end.</summary>
    public readonly int StatementEnd = statementEnd;

    public readonly byte[] SqlHandle = sqlHandle;
    public readonly string? Text = text;
}

/// <summary>
/// A session as real's deadlock and blocked-process reports describe it,
/// read while it waits: its transaction, its client, its settings and its
/// execution stack (probed 2026-10-16 against SQL Server 2025).
/// </summary>
internal sealed class ReportedProcess
{
    public readonly SimulatedDbConnection Connection;
    public readonly SessionToken Owner;
    public readonly string Id;
    public readonly int Spid;
    public readonly int Priority;
    public readonly long LogUsed;
    public readonly string WaitResource;
    public readonly long WaitTime;
    public readonly long TransactionId;
    public readonly string TransactionName;
    public readonly DateTime TransactionStarted;
    public readonly string Xdes;
    public readonly string? LockMode;
    public readonly int Kpid;
    public readonly bool Running;
    public readonly int OpenTransactions;

    /// <summary>The <c>trancount</c> a waiting statement reports: its transaction's count, one more for a write, which runs in a nested one of its own.</summary>
    public readonly int StatementTranCount;

    public readonly DateTime LastBatchStarted;
    public readonly DateTime LastBatchCompleted;
    public readonly string StatementVerb;
    public readonly int StatementLine;
    public readonly ReportFrame[] Frames;
    public readonly string InputBuffer;

    public ReportedProcess(SimulatedDbConnection connection, SessionToken owner, string waitResource, LockMode? waitMode, long waitStartedTicks)
    {
        this.Connection = connection;
        this.Owner = owner;
        this.Spid = connection.Spid;
        this.Id = LockReports.ProcessId(owner, connection.Spid);
        this.Priority = connection.DeadlockPriority;
        var session = connection.Session;
        var transaction = connection.CurrentTransaction;
        this.OpenTransactions = connection.OpenTransactionCount;
        this.LogUsed = transaction is null ? 0 : 120L * transaction.UndoLog.Position;
        this.WaitResource = waitResource;
        this.WaitTime = waitMode is null ? 0 : Math.Max(0, Environment.TickCount64 - waitStartedTicks);
        this.TransactionId = transaction?.TransactionId ?? connection.RunningStatementTransactionId();
        this.TransactionName = transaction is null ? session.CurrentCommand
            : transaction.Name ?? (transaction.BegunImplicitly ? "implicit_transaction" : "user_transaction");
        this.LastBatchStarted = BuiltInResources.LastRequestStartUtc(connection);
        this.LastBatchCompleted = BuiltInResources.LastRequestEndUtc(connection);
        this.TransactionStarted = transaction?.BeginTimeUtc ?? this.LastBatchStarted;
        this.Xdes = LockReports.Address(0xC2B000000L, connection.Spid);
        this.LockMode = waitMode is { } mode ? LockDmvs.ModeAbbreviation(mode) : null;
        this.Kpid = owner.Acting.CurrentExecutingThreadId ?? 0;
        this.Running = owner.Acting.CurrentExecutingThreadId is not null;
        this.StatementVerb = session.CurrentCommand;
        var writes = this.StatementVerb is "UPDATE" or "INSERT" or "DELETE" or "MERGE";
        this.StatementTranCount = this.OpenTransactions + (writes ? (this.OpenTransactions == 0 ? 2 : 1) : 0);
        this.Frames = LockReports.Frames(connection, out this.StatementLine);
        this.InputBuffer = session.BatchText ?? "";
    }
}

/// <summary>
/// What real's lock reports share: a process's attributes, its frames, and
/// the stand-ins for the addresses they print.
/// </summary>
internal static class LockReports
{
    /// <summary><c>2026-10-16T07:30:15.723</c>: a time as the reports write one, to the millisecond.</summary>
    public static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>A stand-in process address, distinct per session and per MARS request.</summary>
    public static string ProcessId(SessionToken owner, int spid) =>
        "process" + (0xC10000000L + ((long)spid * 0x100000) + (owner.IsRequestOwner ? 0x438 : 0xC18)).ToString("x", CultureInfo.InvariantCulture);

    /// <summary>A stand-in address in real's lower-case <c>0x…</c> form.</summary>
    public static string Address(long baseAddress, int spid) =>
        "0x" + (baseAddress + ((long)spid * 0x40000) + 0x480).ToString("x", CultureInfo.InvariantCulture);

    /// <summary>An isolation level as the reports name it: <c>read committed (2)</c>.</summary>
    public static string IsolationLevel(System.Data.IsolationLevel level) => level switch
    {
        System.Data.IsolationLevel.ReadUncommitted => "read uncommitted (1)",
        System.Data.IsolationLevel.RepeatableRead => "repeatable read (3)",
        System.Data.IsolationLevel.Serializable => "serializable (4)",
        System.Data.IsolationLevel.Snapshot => "snapshot (5)",
        _ => "read committed (2)",
    };

    /// <summary>
    /// The attributes a process reports about its session, after its own:
    /// the batch times, its client, its settings. Real's <c>lastattention</c>
    /// is the first day of 1900 at the milliseconds of the last batch's end,
    /// and <c>clientoption1</c> / <c>clientoption2</c> stand in for the
    /// option words real reads off the login, here sqlcmd's (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static List<(string Name, string Value)> SessionAttributes(ReportedProcess process)
    {
        var connection = process.Connection;
        var invariant = CultureInfo.InvariantCulture;
        var attributes = new List<(string, string)>
        {
            ("lastbatchstarted", Time(process.LastBatchStarted)),
            ("lastbatchcompleted", Time(process.LastBatchCompleted)),
            ("lastattention", "1900-01-01T00:00:00." + process.LastBatchCompleted.ToString("fff", invariant)),
        };
        if (connection.ClientApplicationName.Length != 0)
            attributes.Add(("clientapp", connection.ClientApplicationName));
        if (connection.ClientHostName.Length != 0)
            attributes.Add(("hostname", connection.ClientHostName));
        var hostProcess = connection.ClientProcessId;
        attributes.Add(("hostpid", hostProcess.ToString(invariant)));
        attributes.Add(("loginname", connection.Security.OriginalLoginName));
        attributes.Add(("isolationlevel", IsolationLevel(connection.SessionIsolationLevel)));
        attributes.Add(("xactid", process.TransactionId.ToString(invariant)));
        attributes.Add(("currentdb", connection.CurrentDatabase.Id.ToString(invariant)));
        attributes.Add(("currentdbname", connection.CurrentDatabase.Name));
        attributes.Add(("lockTimeout", ((uint)connection.LockTimeoutMillis).ToString(invariant)));
        attributes.Add(("clientoption1", "536870944"));
        attributes.Add(("clientoption2", "128056"));
        return attributes;
    }

    /// <summary>
    /// A deadlock process's attributes, in real's order — what it waits for
    /// and the transaction it waits in, then <see cref="SessionAttributes"/>.
    /// <c>taskpriority</c> is the negated deadlock priority real reports
    /// beside it (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static List<(string Name, string Value)> ProcessAttributes(ReportedProcess process)
    {
        var invariant = CultureInfo.InvariantCulture;
        var attributes = new List<(string, string)>
        {
            ("id", process.Id),
            ("taskpriority", (-process.Priority).ToString(invariant)),
            ("logused", process.LogUsed.ToString(invariant)),
            ("waitresource", process.WaitResource),
            ("waittime", process.WaitTime.ToString(invariant)),
            ("ownerId", process.TransactionId.ToString(invariant)),
            ("transactionname", process.TransactionName),
            ("lasttranstarted", Time(process.TransactionStarted)),
            ("XDES", process.Xdes),
            ("lockMode", process.LockMode ?? ""),
            ("schedulerid", "1"),
            ("kpid", process.Kpid.ToString(invariant)),
            ("status", "suspended"),
            ("spid", process.Spid.ToString(invariant)),
            ("sbid", "0"),
            ("ecid", "0"),
            ("priority", process.Priority.ToString(invariant)),
            ("trancount", process.StatementTranCount.ToString(invariant)),
        };
        attributes.AddRange(SessionAttributes(process));
        return attributes;
    }

    /// <summary>
    /// The session's execution stack, innermost first, as it stands while
    /// the session waits — <paramref name="line"/> the innermost statement's.
    /// A module's frame names it and its statement's text; the client batch
    /// a module was called from gives its text under the batch's legacy
    /// handle; the batch statement running innermost writes <c>unknown</c>
    /// under its handle, behind a frame of its own for the prepared text a
    /// simple parameterization runs it as (probed 2026-10-16 against SQL
    /// Server 2025). A frame's text stops one character short of its
    /// statement's last, as real's does.
    /// </summary>
    public static ReportFrame[] Frames(SimulatedDbConnection connection, out int line)
    {
        line = 1;
        var frames = new List<ReportFrame>();
        var simulation = connection.Simulation;
        var innermost = true;
        for (var batch = connection.RunningBatch; batch is not null; batch = batch.CallerBatch)
        {
            var text = batch.Parser.Command.CommandText;
            var start = Math.Clamp(batch.CurrentStatement.StartIndex, 0, text.Length);
            var last = StatementEnd(simulation, text, start);
            var statementLine = batch.CurrentStatement.StartLine + batch.LineOffset;
            if (innermost)
                line = statementLine;
            var database = batch.ModuleSchema?.Database ?? batch.CurrentDatabase;
            if (batch.ModuleObjectId != 0 && Parser.Expressions.ObjectProperty.FindObject(database, batch.ModuleObjectId) is { } module)
            {
                var definition = batch.ModuleDefinitionText ?? text;
                var offset = batch.ModuleDefinitionText is null ? 0 : batch.ModuleBodyOffset();
                var end = last < 0 ? text.Length - 1 : last;
                frames.Add(new ReportFrame(
                    $"{database.Name}.{PermissionEnforcement.SchemaNameFor(database, module.SchemaId)}.{module.Name}",
                    statementLine, 2 * (offset + start), 2 * (offset + end),
                    CachedPlan.ModuleSqlHandle(database.Id, module), Slice(definition, offset + start, offset + end)));
            }
            else if (innermost)
            {
                if (connection.NestingLevel == 0 && simulation.ReportedStatement(connection, text, start, database) is { } reported
                    && !ReferenceEquals(reported.Text, text))
                {
                    frames.Add(new ReportFrame("adhoc", 1, reported.StartOffset, reported.EndOffset, reported.SqlHandle, null));
                }
                frames.Add(new ReportFrame("adhoc", statementLine, 2 * start, last < 0 ? -1 : 2 * last, BuiltInResources.SqlHandleOf(text), null));
            }
            else
            {
                var end = last < 0 ? text.Length - 1 : last;
                frames.Add(new ReportFrame("adhoc", statementLine, 2 * start, 2 * end, BuiltInResources.LegacySqlHandle(text, database.Id), Slice(text, start, end)));
            }
            innermost = false;
        }
        return [.. frames];
    }

    private static string Slice(string text, int start, int end) =>
        start >= text.Length ? "" : text[start..Math.Clamp(end, start, text.Length)];

    /// <summary>
    /// Where the statement of <paramref name="text"/> starting at
    /// <paramref name="start"/> ends, as real's reports and statement events
    /// place it: a query — <c>SELECT</c>, a DML statement, <c>EXEC</c>,
    /// <c>WITH</c> — at its own last character, anything else at the
    /// character before the next statement, its terminator and the white
    /// space after it included, or -1 when it runs to the text's end (probed
    /// 2026-10-16 against SQL Server 2025: <c>select 1 as a;</c> ends at its
    /// <c>a</c>, <c>set nocount on</c> at the line break after it, and a
    /// query ending its batch on its last character still).
    /// </summary>
    public static int StatementEnd(Simulation simulation, string text, int start)
    {
        var end = simulation.StatementEndIndex(text, start);
        if (!StartsQuery(text, start))
            return end;
        var last = end < 0 ? text.Length - 1 : end;
        while (last > start && (char.IsWhiteSpace(text[last]) || text[last] == ';'))
            last--;
        return last;
    }

    private static bool StartsQuery(string text, int start)
    {
        var at = start;
        while (at < text.Length && char.IsAsciiLetter(text[at]))
            at++;
        var word = text.AsSpan(start, at - start);
        return word.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            || word.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
            || word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
            || word.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
            || word.Equals("MERGE", StringComparison.OrdinalIgnoreCase)
            || word.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
            || word.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)
            || word.Equals("WITH", StringComparison.OrdinalIgnoreCase)
            || (word.IsEmpty && start < text.Length && text[start] == '(');
    }

    /// <summary>Escapes text and attribute values as real's report XML does, the apostrophe included.</summary>
    public static string Escape(string text)
    {
        if (text.AsSpan().IndexOfAny("&<>'\"") < 0)
            return text;
        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            _ = c switch
            {
                '"' => builder.Append("&quot;"),
                '&' => builder.Append("&amp;"),
                '\'' => builder.Append("&apos;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                _ => builder.Append(c),
            };
        }
        return builder.ToString();
    }

    /// <summary>A handle as the reports write it: <c>0x</c> and lower-case hex.</summary>
    public static string Hex(byte[] bytes) => "0x" + Convert.ToHexStringLower(bytes);
}
