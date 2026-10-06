using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>The highest session id a <c>KILL</c> accepts; one past it is Msg 6101 (probed 2026-09-30 against SQL Server 2025).</summary>
    private const int MaxKillSessionId = 32767;

    /// <summary>The highest session id real reserves for its own system sessions; kill targets at or below it are Msg 6107.</summary>
    private const int MaxSystemSessionId = 50;

    /// <summary>
    /// <c>SHUTDOWN [WITH NOWAIT]</c>, entered with the cursor on
    /// <c>SHUTDOWN</c>. A session without the <c>SHUTDOWN</c> permission
    /// (<c>sysadmin</c> and <c>serveradmin</c> carry it) gets the
    /// informational Msg 6004, and the batch ends: silently inside a
    /// <c>TRY</c> with no transaction open, otherwise rolling the transaction
    /// back with the client's own severe error, uncaught (probed 2026-10-06
    /// against SQL Server 2025 as a login real refuses). A permitted one
    /// would stop the server, which the simulation has no process to do.
    /// </summary>
    private static bool ParseShutdown(ParserContext context, BatchContext batch)
    {
        if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not UnquotedString { Span: var word } || !word.Equals("NOWAIT", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        if (batch.IsSkipping)
            return true;
        if (batch.Connection.Simulation.SessionHoldsServerPermission(batch.Connection, Permission.Shutdown))
            throw new NotSupportedException("SHUTDOWN isn't modeled: the simulation has no server process to stop.");
        batch.AppendInfoError(@class: 0, state: 1, number: 6004, message: "User does not have permission to perform this action.");
        if (batch.Connection.OpenTryFrames > 0 && batch.Connection.CurrentTransaction is null)
        {
            batch.ReturnSignaled = true;
            return true;
        }
        throw SimulatedSqlException.ShutdownRefused();
    }

    /// <summary>
    /// <c>KILL { session_id | 'UOW' } [WITH STATUSONLY]</c>, entered with the
    /// cursor on <c>KILL</c>. The target is a literal — a variable, a
    /// parenthesized value or a bare <c>NULL</c> is a syntax error — an
    /// integer for a session and a string for a distributed transaction,
    /// which the simulator has none of (Msg 6110 for a well-formed id). What
    /// a session id meets, in order (probed 2026-09-30 against SQL Server
    /// 2025): <c>WITH COMMIT | ROLLBACK</c> is Msg 6108, an open user
    /// transaction Msg 6115, an id outside 1 to 32767 Msg 6101, a system
    /// session (1 to 50; only 7 probed) Msg 6107, an id no session holds
    /// Msg 6106, the caller's own Msg 6104, a caller without <c>ALTER ANY
    /// CONNECTION</c> Msg 6102; then the session ends as
    /// <see cref="SimulatedDbConnection.Kill"/> describes, or, under
    /// <c>STATUSONLY</c>, Msg 6120 since no rollback is ever in progress.
    /// </summary>
    private static bool ParseKill(ParserContext context, BatchContext batch)
    {
        var negative = false;
        var token = context.GetNextOptional();
        if (token is Operator { Character: '-' })
        {
            negative = true;
            token = context.GetNextOptional();
            if (token is not Numeric)
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        long? sessionId = null;
        string? unitOfWork = null;
        switch (token)
        {
            case Numeric number:
                {
                    var written = number.Source.ToString();
                    if (written.Contains('e', StringComparison.OrdinalIgnoreCase))
                        throw SimulatedSqlException.SyntaxErrorNear(number);
                    if (!long.TryParse(written, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude)
                        || (negative ? -magnitude : magnitude) is < int.MinValue or > int.MaxValue)
                    {
                        throw SimulatedSqlException.IntegerValueOutOfRange((negative ? "-" : "") + written);
                    }
                    sessionId = negative ? -magnitude : magnitude;
                    break;
                }
            case Literal { Value: { IsNull: false } text } when text.Type is VarcharSqlType or NVarcharSqlType:
                unitOfWork = text.AsString;
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();

        var statusOnly = false;
        var commitOrRollback = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            switch (context.Token)
            {
                case UnquotedString { Span: var word } when word.Equals("STATUSONLY", StringComparison.OrdinalIgnoreCase):
                    statusOnly = true;
                    break;
                case ReservedKeyword { Keyword: Keyword.Commit or Keyword.Rollback }:
                    commitOrRollback = true;
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }

        if (!IsStatementBoundary(context.Token))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (batch.IsSkipping)
            return true;

        var connection = batch.Connection;
        if (unitOfWork is not null)
        {
            if (!Guid.TryParse(unitOfWork, out var unit))
                throw SimulatedSqlException.KillUnitOfWorkNotGuid();
            throw connection.CurrentTransaction is not null
                ? SimulatedSqlException.KillInTransaction()
                : SimulatedSqlException.KillUnitOfWorkMissing(unit);
        }

        if (commitOrRollback)
            throw SimulatedSqlException.KillSessionWithCommitOrRollback();
        if (connection.CurrentTransaction is not null)
            throw SimulatedSqlException.KillInTransaction();
        var target = sessionId!.Value;
        if (target is < 1 or > MaxKillSessionId)
            throw SimulatedSqlException.KillSessionIdNotValid(target);
        var spid = (int)target;
        if (spid <= MaxSystemSessionId)
            throw SimulatedSqlException.KillSystemProcess();

        SimulatedDbConnection? victim = null;
        foreach (var candidate in connection.Simulation.SnapshotConnections())
        {
            if (candidate.Spid == spid && !candidate.Killed)
            {
                victim = candidate;
                break;
            }
        }
        if (victim is null)
            throw SimulatedSqlException.KillProcessNotActive(spid);
        if (ReferenceEquals(victim, connection))
            throw SimulatedSqlException.KillOwnProcess();
        if (!connection.Simulation.SessionHoldsServerPermission(connection, Permission.AlterAnyConnection))
            throw SimulatedSqlException.KillNotPermitted();
        if (statusOnly)
            throw SimulatedSqlException.KillNoRollbackInProgress(spid);

        victim.Kill();
        connection.LastStatementRowCount = 0;
        return true;
    }
}
