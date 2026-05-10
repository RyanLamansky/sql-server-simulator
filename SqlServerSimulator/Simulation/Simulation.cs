using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;

namespace SqlServerSimulator;

/// <summary>
/// Simulates a SQL Server instance.
/// </summary>
/// <remarks>
/// Implementation is split across <c>Simulation.*.cs</c> partial-class files
/// by statement family (<c>Create</c>, <c>Insert</c>, <c>Output</c>,
/// <c>Merge</c>, <c>Set</c>, <c>Alter</c>, <c>Dbcc</c>, plus <c>Coerce</c>
/// for the value-coercion helpers shared between INSERT and MERGE). This file
/// holds the public surface (<see cref="CreateDbConnection"/>), the
/// simulation-wide state, and the top-level statement dispatcher.
/// </remarks>
public sealed partial class Simulation
{
    /// <summary>
    /// Creates a new simulated SQL Server instance with no tables or data.
    /// </summary>
    public Simulation()
    {
        RandomNumberGenerator.Fill(this.newSequentialIdAnchor);
    }

    /// <summary>
    /// Creates a simulated database connection.
    /// </summary>
    /// <returns>A new simulated database connection instance.</returns>
    public DbConnection CreateDbConnection() => new SimulatedDbConnection(this);

    /// <summary>User tables, keyed by name.</summary>
    internal readonly ConcurrentDictionary<string, HeapTable> HeapTables = new(Collation.Default);

    /// <summary>
    /// The database name woven into error messages that include a fully
    /// qualified table reference (e.g. Msg 515's <c>"&lt;db&gt;.dbo.&lt;t&gt;"</c>,
    /// Msg 547's <c>database "&lt;db&gt;"</c> wording). The simulator has no
    /// real per-database namespacing; this is a fixed placeholder so the
    /// emitted text stays well-formed and recognizable.
    /// </summary>
    internal const string DefaultDatabaseName = "simulated";

    /// <summary>
    /// Database compatibility level. New simulations default to the most recent
    /// supported level; user code switches via
    /// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c>.
    /// </summary>
    internal CompatibilityLevel CompatibilityLevel = CompatibilityLevel.Sql170;

    /// <summary>
    /// Active session-scoped trace flags (the simulator doesn't model separate
    /// global vs session scope yet — flags set here apply simulation-wide).
    /// Toggled via <c>DBCC TRACEON(N)</c> / <c>DBCC TRACEOFF(N)</c>.
    /// </summary>
    internal readonly HashSet<int> TraceFlags = [];

    /// <summary>
    /// UTC timestamp captured at the top of each top-level statement (in
    /// <see cref="CreateResultSetsForCommand"/>'s loop body) and consumed by
    /// the current-time scalar functions (<c>GETDATE</c>, <c>GETUTCDATE</c>,
    /// <c>SYSDATETIME</c>, <c>SYSUTCDATETIME</c>, <c>SYSDATETIMEOFFSET</c>,
    /// <c>CURRENT_TIMESTAMP</c>). Real SQL Server freezes these within a
    /// statement (probe-confirmed 2026-05-09 — two <c>SYSDATETIME()</c> calls
    /// in one SELECT return identical values to the 7th decimal digit; an
    /// UPDATE that stamps every row with <c>SYSDATETIME()</c> writes the same
    /// value into all rows). The simulator follows by capturing once per
    /// statement and serving every call within that statement from the same
    /// snapshot. The simulator does no local-time conversion: per the
    /// Azure SQL Database default, local-time-returning variants
    /// (<c>GETDATE</c> / <c>SYSDATETIME</c> / <c>CURRENT_TIMESTAMP</c>) and
    /// UTC-returning variants share this single UTC instant, and
    /// <c>SYSDATETIMEOFFSET</c> reports a <c>+00:00</c> offset.
    /// </summary>
    internal DateTime CurrentStatementUtcNow;

    /// <summary>
    /// Last identity value produced by an INSERT in this simulation —
    /// the source for both <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c>.
    /// SQL Server scopes these per session/scope; the simulator collapses
    /// both to a single simulation-wide slot for the same reason
    /// <see cref="TraceFlags"/> does.
    /// </summary>
    /// <remarks>
    /// Cleared (set to <c>null</c>) by every INSERT that doesn't generate
    /// or accept an identity value — matching SQL Server's behavior of
    /// resetting <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c> when the
    /// most recent statement didn't touch an identity column.
    /// </remarks>
    internal decimal? LastIdentity;

    /// <summary>
    /// Name of the table currently under <c>SET IDENTITY_INSERT ... ON</c>,
    /// or <c>null</c> when no table is in that mode. SQL Server allows only
    /// one table at a time per session; the simulator enforces the same.
    /// </summary>
    internal string? IdentityInsertTable;

    private long rowVersionCounter;

    /// <summary>
    /// Allocates the next <c>rowversion</c> counter value (also surfaced as
    /// <c>@@DBTS</c> in real SQL Server). Database-scoped, monotonic,
    /// shared across every <c>rowversion</c> column in every table — INSERT
    /// and UPDATE on a rowversion-bearing table both advance it. The
    /// counter is the in-memory representation; the 8-byte big-endian wire
    /// form materializes on demand via <see cref="SqlValue.AsBytes"/> /
    /// <see cref="RowVersionSqlType.Encode"/>, never per-row in the hot
    /// path.
    /// </summary>
    internal long AllocateRowVersion() => Interlocked.Increment(ref this.rowVersionCounter);

    /// <summary>
    /// Explicit override of the per-database <c>VERBOSE_TRUNCATION_WARNINGS</c>
    /// scoped configuration; <c>null</c> means follow the compatibility-level
    /// default. Set via
    /// <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON|OFF</c>.
    /// </summary>
    internal bool? VerboseTruncationWarnings;

    /// <summary>
    /// Decides whether string truncation should raise the verbose Msg 2628
    /// (with table, column, and truncated value) or the legacy Msg 8152
    /// (single line, no detail). Precedence: an explicit
    /// <see cref="VerboseTruncationWarnings"/> setting wins; otherwise trace
    /// flag 460 forces verbose; otherwise the compatibility level decides
    /// (verbose iff &gt;= <see cref="CompatibilityLevel.Sql160"/>, the level
    /// at which it became default in SQL Server 2022).
    /// </summary>
    internal bool IsVerboseTruncationActive() =>
        this.VerboseTruncationWarnings
        ?? (this.TraceFlags.Contains(460)
            || this.CompatibilityLevel >= CompatibilityLevel.Sql160);

    /// <summary>
    /// System tables (e.g. <c>systypes</c>). Materialized once per process and
    /// shared across all <see cref="Simulation"/> instances; the bytes are
    /// immutable.
    /// </summary>
    internal static Dictionary<string, HeapTable> SystemHeapTables => BuiltInResources.SystemHeapTables.Value;

    /// <summary>
    /// Random 12-byte tail (raw bytes [4..15] of the produced GUID) for
    /// <see cref="GenerateNewSequentialId"/>. Filled once at construction —
    /// stands in for SQL Server's "MAC address + boot timestamp" anchor that
    /// distinguishes one server's sequence from another's.
    /// </summary>
    private readonly byte[] newSequentialIdAnchor = new byte[12];

    /// <summary>
    /// Monotonic counter for <see cref="GenerateNewSequentialId"/>; each call
    /// reserves the next value via <see cref="Interlocked.Increment(ref long)"/>
    /// and packs it into raw bytes [0..3] of the produced GUID.
    /// </summary>
    private long newSequentialIdCounter;

    /// <summary>
    /// Produces the next <c>NEWSEQUENTIALID()</c> value: a
    /// <see cref="Guid"/> whose comparison under SQL Server's
    /// <c>uniqueidentifier</c> ordering rules is strictly greater than
    /// every value previously returned for this <see cref="Simulation"/>.
    /// </summary>
    /// <remarks>
    /// SQL Server's <c>uniqueidentifier</c> compares group-by-group from
    /// most significant to least: bytes <c>[10..15]</c>, then <c>[8..9]</c>,
    /// then <c>[6..7]</c>, then <c>[4..5]</c>, then <c>[0..3]</c>; within
    /// each group the lower-indexed byte is more significant. To get
    /// strict monotonicity the simulator fixes bytes <c>[4..15]</c> for the
    /// lifetime of the simulation and packs an incrementing 64-bit counter
    /// into bytes <c>[0..3]</c> big-endian (raw byte 0 = MSB, raw byte 3 =
    /// LSB). Each increment lands in the comparison-LSB position
    /// (raw byte 3) and carries propagate left toward higher comparison
    /// significance — matching real SQL Server's per-call delta.
    /// Monotonicity holds for the first 2^32 calls; beyond that the counter
    /// wraps and the cycle restarts. The GUID is constructed via
    /// <see cref="Guid(ReadOnlySpan{byte}, bool)"/> with <c>bigEndian</c>
    /// true, so its display order matches the raw byte order assembled here.
    /// </remarks>
    internal Guid GenerateNewSequentialId()
    {
        var counter = (uint)Interlocked.Increment(ref this.newSequentialIdCounter);
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = (byte)(counter >> 24);
        bytes[1] = (byte)(counter >> 16);
        bytes[2] = (byte)(counter >> 8);
        bytes[3] = (byte)counter;
        this.newSequentialIdAnchor.CopyTo(bytes[4..]);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// Top-level statement dispatch. Iterates through the command's tokens,
    /// dispatching each statement to its dedicated parser by leading keyword.
    /// Yields outcomes for data-producing statements (SELECT, INSERT) and runs
    /// schema/control statements for side-effect only (CREATE, SET, ALTER,
    /// DBCC). The shape mirrors <c>Expression.ResolveBuiltIn</c>: a single
    /// switch with one case per keyword, each delegating to a focused method.
    /// </summary>
    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
        var context = new ParserContext(command);

        while (context.MoveNext())
        {
            // CTE bindings live for exactly one statement. Clear at the
            // top of every iteration; a WITH prefix below repopulates.
            context.CteBindings = null;
            this.CurrentStatementUtcNow = DateTime.UtcNow;

            // WITH prefix applies to the immediately-following SELECT /
            // INSERT / UPDATE / DELETE / MERGE. ParseCteBindings sets
            // context.CteBindings and advances the cursor to the dispatched
            // statement's leading keyword; the switch below runs unchanged.
            if (context.Token is ReservedKeyword { Keyword: Keyword.With })
                ParseCteBindings(context);

            switch (context.Token)
            {
                case Operator { Character: ';' }:
                    continue;

                case ReservedKeyword { Keyword: Keyword.Select }:
                    yield return Selection.Parse(context, 0).Execute();
                    continue;

                case ReservedKeyword { Keyword: Keyword.Insert }:
                    yield return RunMutation(context, ParseInsert);
                    continue;

                case ReservedKeyword { Keyword: Keyword.Merge }:
                    yield return RunMutation(context, ParseMerge);
                    continue;

                case ReservedKeyword { Keyword: Keyword.Update }:
                    yield return RunMutation(context, ParseUpdate);
                    continue;

                case ReservedKeyword { Keyword: Keyword.Delete }:
                    yield return RunMutation(context, ParseDelete);
                    continue;

                case ReservedKeyword { Keyword: Keyword.Begin } when TryParseBeginTransaction(context):
                case ReservedKeyword { Keyword: Keyword.Commit } when TryParseCommit(context):
                case ReservedKeyword { Keyword: Keyword.Save } when TryParseSavepoint(context):
                case ReservedKeyword { Keyword: Keyword.Rollback } when TryParseRollbackTransaction(context):
                case ReservedKeyword { Keyword: Keyword.Create } when TryParseCreate(context):
                case ReservedKeyword { Keyword: Keyword.Set } when TryParseSet(context):
                case ReservedKeyword { Keyword: Keyword.Alter } when TryParseAlter(context):
                case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseDbcc(context):
                    continue;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// Wraps a mutation statement (INSERT / UPDATE / DELETE / MERGE) with
    /// statement-level atomicity. Routes mutations to the connection's
    /// active transaction's <see cref="UndoLog"/> when one exists (Bundle 2
    /// — explicit <c>BeginTransaction</c>); otherwise creates a fresh
    /// per-statement log (Bundle 1 — auto-commit). In both cases the
    /// statement captures a marker at entry; on exception only the entries
    /// appended this statement are unwound, which matches SQL Server's
    /// "failed statement leaves the surrounding transaction alive" behavior
    /// (probe-confirmed 2026-05-08). Identity / rowversion counters bypass
    /// the log entirely.
    /// </summary>
    /// <summary>
    /// Parses <c>SAVE TRAN[SACTION] &lt;name&gt;</c> and records the active
    /// transaction's current undo-log position against the name. EF Core 10
    /// emits this per SaveChanges call inside an active
    /// <c>Database.BeginTransaction</c> so a failed SaveChanges can roll
    /// back just that save's writes via <c>ROLLBACK TRANSACTION &lt;name&gt;</c>.
    /// Returns false if the next token isn't <c>TRAN</c> / <c>TRANSACTION</c>
    /// (the <c>case … when</c> dispatch falls through to a syntax error).
    /// </summary>
    private static bool TryParseSavepoint(ParserContext context)
    {
        if (!context.MoveNext() || context.Token is not ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
            return false;
        var name = context.GetNextRequired<Name>().Value;
        context.MoveNextOptional();

        var tx = context.Connection.CurrentTransaction
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        tx.Savepoints[name] = tx.UndoLog.Position;
        return true;
    }

    /// <summary>
    /// Parses <c>BEGIN TRAN[SACTION] [name] [WITH MARK 'description']</c>.
    /// Opens a fresh <see cref="SimulatedDbTransaction"/> on the connection
    /// when none is active (TRANCOUNT 0 → 1) or increments
    /// <see cref="SimulatedDbTransaction.TranCount"/> when one already is
    /// (nested-BEGIN TRANCOUNT bump, no real nesting). The optional name and
    /// WITH MARK clause are accepted but cosmetic — SQL Server treats the
    /// name as documentation only, and only the outermost COMMIT actually
    /// commits regardless of which name the COMMIT references.
    /// </summary>
    private static bool TryParseBeginTransaction(ParserContext context)
    {
        if (!context.MoveNext() || context.Token is not ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
            return false;
        // Optional name (BEGIN TRANSACTION my_tx). Cosmetic; consume and ignore.
        if (context.MoveNext() && context.Token is Name)
            context.MoveNextOptional();

        if (context.Connection.CurrentTransaction is { } existing)
        {
            existing.TranCount++;
        }
        else
        {
            context.Connection.CurrentTransaction = new SimulatedDbTransaction(
                context.Simulation, context.Connection, System.Data.IsolationLevel.Unspecified);
        }
        return true;
    }

    /// <summary>
    /// Parses <c>COMMIT [TRAN[SACTION]] [name] [WORK]</c>. Decrements
    /// <see cref="SimulatedDbTransaction.TranCount"/>; when it reaches 0
    /// the transaction actually commits (drops the undo log and clears
    /// <see cref="SimulatedDbConnection.CurrentTransaction"/>). Raises
    /// <see cref="SimulatedSqlException.NoCorrespondingBeginCommit"/>
    /// (Msg 3902) when no transaction is active — probe-confirmed wording.
    /// </summary>
    private static bool TryParseCommit(ParserContext context)
    {
        // COMMIT alone is the bare form; followed by TRAN/TRANSACTION/WORK
        // gives the qualified form, optionally followed by a name.
        if (context.MoveNext()
            && context.Token is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
        {
            // Optional savepoint-style name. Consume and ignore.
            if (context.MoveNext() && context.Token is Name)
                context.MoveNextOptional();
        }
        // COMMIT WORK is an ANSI-equivalent. WORK isn't reserved in the
        // simulator's keyword list; accept it as an unquoted identifier
        // following COMMIT.
        else if (context.Token is UnquotedString u && u.Span.Equals("WORK", StringComparison.OrdinalIgnoreCase))
        {
            context.MoveNextOptional();
        }

        var tx = context.Connection.CurrentTransaction
            ?? throw SimulatedSqlException.NoCorrespondingBeginCommit();

        tx.TranCount--;
        if (tx.TranCount == 0)
            tx.Commit();
        return true;
    }

    /// <summary>
    /// Parses <c>ROLLBACK [TRAN[SACTION]] [name] [WORK]</c>. Two shapes:
    /// with a savepoint name → partial rollback to the saved position
    /// (EF Core 10's SaveChanges-failure recovery path); without a name →
    /// full transaction rollback regardless of TRANCOUNT depth (probe-
    /// confirmed). Bare <c>ROLLBACK</c> with no active transaction raises
    /// <see cref="SimulatedSqlException.NoCorrespondingBeginRollback"/>
    /// (Msg 3903).
    /// </summary>
    private static bool TryParseRollbackTransaction(ParserContext context)
    {
        // After ROLLBACK, accept TRAN/TRANSACTION/WORK or fall through to
        // bare-ROLLBACK with the cursor on the next un-consumed token.
        if (context.MoveNext())
        {
            if (context.Token is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
            {
                if (context.MoveNext() && context.Token is Name nameToken)
                {
                    // Savepoint-name path: partial rollback to the saved position.
                    var name = nameToken.Value;
                    context.MoveNextOptional();

                    var tx = context.Connection.CurrentTransaction
                        ?? throw SimulatedSqlException.NoCorrespondingBeginRollback();
                    if (!tx.Savepoints.TryGetValue(name, out var marker))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    tx.UndoLog.RollbackTo(marker);
                    return true;
                }
            }
            else if (context.Token is UnquotedString u && u.Span.Equals("WORK", StringComparison.OrdinalIgnoreCase))
            {
                context.MoveNextOptional();
            }
        }

        // Bare ROLLBACK (or ROLLBACK TRAN / ROLLBACK WORK with no name) →
        // full rollback regardless of TRANCOUNT.
        var activeTx = context.Connection.CurrentTransaction
            ?? throw SimulatedSqlException.NoCorrespondingBeginRollback();
        activeTx.Rollback();
        return true;
    }

    private static SimulatedStatementOutcome RunMutation(ParserContext context, Func<ParserContext, SimulatedStatementOutcome> body)
    {
        var log = context.Connection.CurrentTransaction?.UndoLog ?? new UndoLog();
        var marker = log.Position;

        var savedLog = context.CurrentUndoLog;
        context.CurrentUndoLog = log;
        try
        {
            return body(context);
        }
        catch
        {
            log.RollbackTo(marker);
            throw;
        }
        finally
        {
            context.CurrentUndoLog = savedLog;
        }
    }
}
