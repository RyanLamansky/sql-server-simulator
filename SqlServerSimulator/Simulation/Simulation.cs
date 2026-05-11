using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
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

    /// <summary>
    /// The database name woven into error messages that include a fully
    /// qualified table reference (e.g. Msg 515's <c>"&lt;db&gt;.dbo.&lt;t&gt;"</c>,
    /// Msg 547's <c>database "&lt;db&gt;"</c> wording). Also the key of the
    /// single <see cref="Database"/> entry in <see cref="Databases"/> that
    /// every freshly-constructed <see cref="Simulation"/> ships with.
    /// </summary>
    internal const string DefaultDatabaseName = "simulated";

    /// <summary>
    /// Per-database state hosted by this server instance, keyed by name.
    /// Constructor seeds one entry (<see cref="DefaultDatabaseName"/>);
    /// <c>USE &lt;db&gt;</c> / multi-database support graft onto the dictionary
    /// when needed. <see cref="SimulatedDbConnection.CurrentDatabase"/>
    /// tracks which entry the session is pointed at.
    /// </summary>
    internal readonly Dictionary<string, Database> Databases = new(Collation.Default)
    {
        [DefaultDatabaseName] = new Database(DefaultDatabaseName),
    };

    /// <summary>
    /// System tables (e.g. <c>systypes</c>). Materialized once per process and
    /// shared across all <see cref="Simulation"/> instances; the bytes are
    /// immutable.
    /// </summary>
    internal static Dictionary<string, HeapTable> SystemHeapTables => BuiltInResources.SystemHeapTables.Value;

    /// <summary>
    /// Virtual <c>sys.&lt;view&gt;</c> catalog views (<c>sys.schemas</c>,
    /// <c>sys.tables</c>, <c>sys.objects</c>), keyed by leaf name. Each
    /// projects live <see cref="Database"/> / <see cref="Schema"/> /
    /// <see cref="HeapTable"/> metadata on every read; rows aren't cached.
    /// Materialized once per process via <see cref="BuiltInResources"/>.
    /// </summary>
    internal static Dictionary<string, CatalogView> CatalogViews => BuiltInResources.CatalogViews.Value;

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
    /// <remarks>
    /// Statement separators (<c>;</c>) are <i>optional</i> between most
    /// statements, mirroring real SQL Server's relaxed batch grammar. Two
    /// exceptions: a CTE (<c>WITH</c>) directly following another statement
    /// raises Msg 319, and a <c>MERGE</c> not terminated by <c>;</c> raises
    /// Msg 10713. The loop drains explicit separators at the top of each
    /// iteration; statement parsers are expected to leave <see cref="ParserContext.Token"/>
    /// at their first un-consumed token (the lookahead-position contract on
    /// <see cref="ParserContext"/>). For parsers that historically left
    /// <c>Token</c> on the last token they consumed (DBCC's closing <c>)</c>,
    /// SET-session-state's <c>ON</c>/<c>OFF</c>, etc.) the bottom of the loop
    /// normalizes by advancing one token when <c>Token</c> isn't already at a
    /// recognizable statement boundary.
    /// </remarks>
    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
        var batch = new BatchContext(command);
        var context = batch.Parser;
        context.MoveNextOptional();
        foreach (var outcome in DispatchStatementsUntil(batch, endKeyword: null))
            yield return outcome;
        WriteBackOutputParameters(batch);
    }

    /// <summary>
    /// Drives the per-statement dispatch loop until either end-of-batch
    /// (when <paramref name="endKeyword"/> is null — top-level call from
    /// <see cref="CreateResultSetsForCommand"/>) or the matching keyword
    /// (when <paramref name="endKeyword"/> is <c>END</c> — block-scoped
    /// call from <see cref="ParseBeginBlock"/>). Handles statement-separator
    /// (<c>;</c>) draining and the CTE-must-be-separated rule
    /// (<c>requireSemicolonBeforeCte</c>); the body of each statement is
    /// dispatched by <see cref="DispatchOneStatement"/>.
    /// </summary>
    internal IEnumerable<SimulatedStatementOutcome> DispatchStatementsUntil(BatchContext batch, Keyword? endKeyword)
    {
        var context = batch.Parser;
        var requireSemicolonBeforeCte = false;

        while (context.Token is not null)
        {
            // Early-exit on RETURN: stop dispatching once the batch has been
            // signaled to exit. Any remaining statements (including the END
            // terminator of an enclosing block) are abandoned — the caller
            // handles cursor state. Checked here at the top of every iteration
            // so RETURN inside a block exits the block dispatcher promptly
            // (the block's "expect END" check has a matching short-circuit).
            if (batch.ReturnSignaled)
                yield break;

            if (endKeyword is Keyword end && context.Token is ReservedKeyword rk && rk.Keyword == end)
                yield break;

            if (context.Token is Operator { Character: ';' })
            {
                requireSemicolonBeforeCte = false;
                context.MoveNextOptional();
                continue;
            }

            foreach (var outcome in DispatchOneStatement(batch, requireSemicolonBeforeCte))
                yield return outcome;
            requireSemicolonBeforeCte = true;
        }
    }

    /// <summary>
    /// Dispatches a single statement at <see cref="ParserContext.Token"/>'s
    /// current position. Handles the optional CTE prefix (<c>WITH</c>),
    /// runs the per-statement frame setup (<see cref="StatementContext.UtcNow"/>),
    /// then routes by leading keyword to the matching parser. Yields zero
    /// or more outcomes (a SELECT produces a result set; an INSERT with
    /// <c>OUTPUT</c> produces one; DML without OUTPUT and DDL produce a
    /// <see cref="SimulatedNonQuery"/>; IF / BEGIN…END recursively yield
    /// their body's outcomes; SET / DECLARE / transaction statements yield
    /// nothing). When <see cref="BatchContext.IsSkipping"/> is true, every
    /// branch suppresses its outcome yield (the body's parser still ran
    /// for cursor-advance + name resolution, but no result reaches the
    /// client) and the <c>LastStatementRowCount</c> update is skipped.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> DispatchOneStatement(BatchContext batch, bool requireSemicolonBeforeCte)
    {
        var context = batch.Parser;
        var connection = context.Connection;

        // CTE bindings live for exactly one statement. Clear at the top of
        // every iteration; a WITH prefix below repopulates.
        context.CteBindings = null;
        batch.CurrentStatement.UtcNow = DateTime.UtcNow;

        // WITH prefix applies to the immediately-following SELECT / INSERT /
        // UPDATE / DELETE / MERGE. ParseCteBindings sets context.CteBindings
        // and advances the cursor to the dispatched statement's leading
        // keyword; the switch below runs unchanged.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (requireSemicolonBeforeCte)
                throw SimulatedSqlException.CteRequiresPrecedingSemicolon();
            ParseCteBindings(context);
        }

        SimulatedStatementOutcome? outcome;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Select }:
                {
                    var selection = Selection.Parse(context, 0);
                    if (selection.IntoTarget is not null)
                    {
                        // SELECT INTO: creates the destination table and
                        // inserts each projected row. RunMutation gives
                        // the executor access to the active undo log so
                        // transactional CREATE+INSERT can roll back. In
                        // skip mode, ExecuteSelectInto returns SimulatedNonQuery(0)
                        // without touching the heap.
                        outcome = RunMutation(context, _ => ExecuteSelectInto(selection, batch));
                        if (!batch.IsSkipping)
                        {
                            connection.LastStatementRowCount = outcome.RecordsAffected;
                            yield return outcome;
                        }
                        break;
                    }
                    if (batch.IsSkipping)
                        break;
                    // Materialize rows up-front so @@ROWCOUNT reflects the
                    // statement's full row count for the next statement in
                    // the same batch (real SQL Server runs server-side and
                    // sets @@ROWCOUNT on completion; the simulator
                    // materializes to mirror that).
                    var rows = selection.Execute(batch).RowBytes.ToList();
                    connection.LastStatementRowCount = rows.Count;
                    outcome = selection.IsAssignmentOnly
                        ? new SimulatedNonQuery(rows.Count)
                        : new SimulatedSqlResultSet(selection.Schema, selection.ColumnNames, rows);
                    yield return outcome;
                    break;
                }

            case ReservedKeyword { Keyword: Keyword.Insert }:
                outcome = RunMutation(context, ParseInsert);
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Merge }:
                outcome = RunMutation(context, ParseMerge);
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                // Real SQL Server requires `;` after MERGE (Msg 10713) —
                // the only statement family with a mandatory terminator.
                // Check before normalization so the cursor is still on the
                // parser's lookahead position. The check runs even in skip
                // mode — the grammar requirement is independent of execution.
                if (context.Token is not Operator { Character: ';' })
                    throw SimulatedSqlException.MergeMustBeTerminated();
                break;

            case ReservedKeyword { Keyword: Keyword.Update }:
                outcome = RunMutation(context, ParseUpdate);
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Delete }:
                outcome = RunMutation(context, ParseDelete);
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.If }:
                foreach (var o in ParseIfStatement(batch))
                    yield return o;
                break;

            case ReservedKeyword { Keyword: Keyword.While }:
                foreach (var o in ParseWhileStatement(batch))
                    yield return o;
                break;

            case ReservedKeyword { Keyword: Keyword.Break }:
                ParseBreakStatement(batch);
                break;

            case ReservedKeyword { Keyword: Keyword.Continue }:
                ParseContinueStatement(batch);
                break;

            case ReservedKeyword { Keyword: Keyword.Return }:
                ParseReturnStatement(batch);
                break;

            case ReservedKeyword { Keyword: Keyword.Print }:
                ParsePrintStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.WaitFor }:
                ParseWaitForStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Truncate }:
                ParseTruncateStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Begin }:
                // Peek the token after BEGIN to disambiguate transaction-start
                // (BEGIN TRAN / BEGIN TRANSACTION / BEGIN DISTRIBUTED TRAN) from
                // not-modeled forms (BEGIN TRY / BEGIN ATOMIC) from a compound
                // statement block (BEGIN … END). The transaction case restores
                // and re-parses via TryParseBeginTransaction so its existing
                // BEGIN-consuming flow stays untouched.
                {
                    var checkpoint = context.SaveCheckpoint();
                    context.MoveNextRequired();
                    var afterBegin = context.Token;
                    context.RestoreCheckpoint(checkpoint);
                    switch (afterBegin)
                    {
                        case ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction }:
                            if (TryParseBeginTransaction(context) && !batch.IsSkipping)
                                connection.LastStatementRowCount = 0;
                            break;
                        case ReservedKeyword { Keyword: Keyword.Distributed }:
                            throw new NotSupportedException("BEGIN DISTRIBUTED TRANSACTION isn't modeled (no distributed transaction coordinator).");
                        case UnquotedString u
                            when u.Span.Equals("TRY", StringComparison.OrdinalIgnoreCase)
                                 || u.Span.Equals("ATOMIC", StringComparison.OrdinalIgnoreCase):
                            throw new NotSupportedException(
                                $"BEGIN {u.Value.ToUpperInvariant()} blocks aren't modeled (T-SQL TRY/CATCH and natively-compiled stored-proc ATOMIC blocks).");
                        default:
                            foreach (var o in ParseBeginBlock(batch))
                                yield return o;
                            if (!batch.IsSkipping)
                                connection.LastStatementRowCount = 0;
                            break;
                    }
                    break;
                }

            case ReservedKeyword { Keyword: Keyword.Commit } when TryParseCommit(context):
            case ReservedKeyword { Keyword: Keyword.Save } when TryParseSavepoint(context):
            case ReservedKeyword { Keyword: Keyword.Rollback } when TryParseRollbackTransaction(context):
            case ReservedKeyword { Keyword: Keyword.Create } when TryParseCreate(context):
            case ReservedKeyword { Keyword: Keyword.Drop } when TryParseDrop(context):
            case ReservedKeyword { Keyword: Keyword.Alter } when TryParseAlter(context):
            case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseDbcc(context):
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;
            case ReservedKeyword { Keyword: Keyword.Set } when TryParseSet(context):
                // SET @v = expr (probe-confirmed to set @@ROWCOUNT to 1).
                // Other SET shapes (SET NOCOUNT etc.) reach here too; the
                // simulator can't distinguish without re-parsing, but the
                // session-state SET shapes are rare and the rowcount they
                // leave isn't asserted-on in practice.
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 1;
                break;
            case ReservedKeyword { Keyword: Keyword.Declare }:
                {
                    var initRowCount = TryParseDeclare(context);
                    if (!batch.IsSkipping && initRowCount is int n)
                        connection.LastStatementRowCount = n;
                    // No initializer → @@ROWCOUNT preserved (probe-confirmed).
                }
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // Normalize cursor to a lookahead position. Well-behaved parsers
        // already left Token at their first un-consumed token (`;`, the
        // next statement's leading keyword, or null at EOF); for parsers
        // that ended on the last consumed token, advance once.
        if (!IsStatementBoundary(context.Token))
            context.MoveNextOptional();
    }

    /// <summary>
    /// Returns true when <paramref name="token"/> is at a place the
    /// dispatch loop can resume from without advancing: a <c>;</c>, end of
    /// batch, a recognized statement-starting keyword, or the <c>END</c>
    /// terminator of a BEGIN…END block. Used to decide whether to re-normalize
    /// a parser's leftover cursor position.
    /// </summary>
    private static bool IsStatementBoundary(Token? token) =>
        token is null
        or Operator { Character: ';' }
        or ReservedKeyword
        {
            Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete
                or Keyword.Merge or Keyword.Begin or Keyword.Commit or Keyword.Rollback
                or Keyword.Save or Keyword.Create or Keyword.Drop or Keyword.Alter or Keyword.Dbcc
                or Keyword.Set or Keyword.Declare or Keyword.With or Keyword.If or Keyword.Else
                or Keyword.End or Keyword.While or Keyword.Break or Keyword.Continue
                or Keyword.Return or Keyword.Print or Keyword.WaitFor or Keyword.Truncate
        };

    /// <summary>
    /// At end-of-batch, copies the final values of every InputOutput /
    /// Output direction <see cref="DbParameter"/> from its variable slot
    /// back into <see cref="DbParameter.Value"/>. Mirrors SqlClient's
    /// behavior of round-tripping mutations made by SQL-text in the batch
    /// (probe-confirmed against SQL Server 2025: a parameter sent in as 5,
    /// mutated by `SET @x = 999`, reads 999 from the caller's
    /// <c>param.Value</c> after <c>ExecuteNonQuery</c>).
    /// </summary>
    private static void WriteBackOutputParameters(BatchContext batch)
    {
        foreach (var slot in batch.Variables.Values)
        {
            if (slot.Parameter is { } parameter
                && parameter.Direction is System.Data.ParameterDirection.InputOutput or System.Data.ParameterDirection.Output)
            {
                parameter.Value = slot.Value.IsNull ? DBNull.Value : slot.Value.ToObject();
            }
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

        if (context.Batch.IsSkipping)
            return true;

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

        if (context.Batch.IsSkipping)
            return true;

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

        if (context.Batch.IsSkipping)
            return true;

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

                    if (context.Batch.IsSkipping)
                        return true;

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

        if (context.Batch.IsSkipping)
            return true;

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

        var savedLog = context.Batch.CurrentUndoLog;
        context.Batch.CurrentUndoLog = log;
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
            context.Batch.CurrentUndoLog = savedLog;
        }
    }
}
