using System.Data;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// The transaction a legacy security or object procedure wraps its work in
// when the caller has none open: BEGIN TRANSACTION, its statements, then a
// COMMIT, or a ROLLBACK after a failed statement — the ENVCHANGE pair real
// announces around the call. Probed 2026-10-17 against SQL Server 2025.
partial class Simulation
{
    /// <summary>
    /// Where a system procedure ends the transaction it opened around an error
    /// it raises itself (a <c>RAISERROR</c> of its own, rather than a failed
    /// statement's error, after which it always rolls back).
    /// </summary>
    internal enum ProcedureTransactionEnding : byte
    {
        /// <summary>Rolls back, then raises (<c>sp_addrolemember</c>'s Msg 15410).</summary>
        RollbackFirst,

        /// <summary>Commits, then raises (<c>sp_rename</c>'s Msg 15248).</summary>
        CommitFirst,

        /// <summary>Raises, then commits (<c>sp_settriggerorder</c>'s Msg 15165).</summary>
        CommitAfter,
    }

    /// <summary>
    /// Msg 15002 naming <c>sys.</c><paramref name="refusing"/>, raised from
    /// <paramref name="procedure"/>'s line <paramref name="line"/>, when a
    /// transaction is open: the legacy security procedures refuse to run in
    /// one, implicit or explicit, ahead of any check of their arguments
    /// (probed 2026-10-17 against SQL Server 2025). <paramref name="procedure"/>
    /// is the name the call wrote, or the inner procedure that refuses
    /// (<c>sp_adduser</c>'s <c>sys.sp_grantdbaccess</c>).
    /// </summary>
    private static void RefuseInTransaction(BatchContext batch, string procedure, string refusing, int line)
    {
        if (batch.Connection.CurrentTransaction is not null)
            throw AtSystemProcedureLine(procedure, SimulatedSqlException.ProcedureCannotRunInTransaction(refusing), line);
    }

    /// <summary>
    /// Marks <paramref name="error"/> as a failed statement's inside a system
    /// procedure (<see cref="SimulatedSqlException.RaisedByProcedureStatement"/>),
    /// raised at <paramref name="procedure"/>'s line <paramref name="line"/> —
    /// line 1 of no procedure for the statement a procedure builds and runs
    /// as dynamic SQL.
    /// </summary>
    private static SimulatedSqlException ProcedureStatementError(SimulatedSqlException error, string procedure = "", int line = 1)
    {
        error.RaisedByProcedureStatement = true;
        return AtSystemProcedureLine(procedure, error, line);
    }

    /// <summary>
    /// Runs <paramref name="body"/> in a transaction of the procedure's own
    /// when the caller has none open; inside the caller's it nests, announcing
    /// nothing. The transaction commits when the body ends, and rolls back
    /// after a statement of it failed — or commits there too with
    /// <paramref name="commitsAfterFailedStatement"/> (<c>sp_changedbowner</c>).
    /// An error the procedure raises itself ends it as
    /// <paramref name="raisedEnding"/> says. An error a <c>TRY</c> catches
    /// before the procedure reaches its ending leaves the transaction open,
    /// as real's does: the <c>CATCH</c> reads <c>@@TRANCOUNT</c> 1 (probed
    /// 2026-10-17 against SQL Server 2025). With
    /// <paramref name="sendsMessagesInside"/> the messages the body queued go
    /// out ahead of the commit (<c>sp_rename</c>'s caution), where otherwise
    /// they follow it (<c>sp_recompile</c>'s). Inside the caller's
    /// transaction it nests, announcing nothing: <c>@@TRANCOUNT</c> is one
    /// higher until it ends, which an error a <c>TRY</c> catches first never
    /// reaches — the <c>CATCH</c> reads the caller's count plus one after a
    /// failed statement or a refusal raised ahead of a commit (probed
    /// 2026-10-17 against SQL Server 2025). With
    /// <paramref name="rollsBackCallerTransaction"/> a failure there rolls the
    /// caller's transaction back, whole, where its own would roll back
    /// (<c>sp_addrolemember</c>, <c>sp_addsrvrolemember</c>; the call then
    /// closes with Msg 266). With <paramref name="rollsBack"/> the procedure's
    /// own transaction rolls back when the body ends, a refused change's
    /// (<c>sp_addlinkedserver</c> naming a server that exists).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InProcedureTransaction(
        BatchContext batch,
        IEnumerable<SimulatedStatementOutcome> body,
        ProcedureTransactionEnding raisedEnding = ProcedureTransactionEnding.RollbackFirst,
        bool commitsAfterFailedStatement = false,
        bool sendsMessagesInside = false,
        bool rollsBackCallerTransaction = false,
        bool rollsBack = false)
    {
        var connection = batch.Connection;
        if (batch.IsSkipping)
            return body;
        if (connection.CurrentTransaction is { } caller)
            return Nested(connection, caller, body, raisedEnding, commitsAfterFailedStatement, rollsBackCallerTransaction);
        return Wrapped(connection, body, raisedEnding, commitsAfterFailedStatement, sendsMessagesInside, rollsBack);

        // Inside the caller's transaction the procedure's BEGIN TRANSACTION
        // nests, raising @@TRANCOUNT by one until its COMMIT, so an error a
        // TRY catches before then leaves the count raised.
        static IEnumerable<SimulatedStatementOutcome> Nested(SimulatedDbConnection connection, SimulatedDbTransaction caller, IEnumerable<SimulatedStatementOutcome> body, ProcedureTransactionEnding raisedEnding, bool commitsAfterFailedStatement, bool rollsBackCaller)
        {
            caller.TranCount++;
            var open = true;
            using (var outcomes = body.GetEnumerator())
            {
                while (true)
                {
                    try
                    {
                        if (!outcomes.MoveNext())
                            break;
                    }
                    catch (SimulatedSqlException error)
                    {
                        if (open)
                            EndNestedAtError(connection, caller, error, raisedEnding, commitsAfterFailedStatement, rollsBackCaller);
                        throw;
                    }
                    yield return outcomes.Current;
                    if (open && outcomes.Current is SimulatedErrorOutcome && rollsBackCaller)
                    {
                        End(connection, caller, commit: false);
                        open = false;
                    }
                }
            }
            if (open)
                Unnest(connection, caller);
        }

        static IEnumerable<SimulatedStatementOutcome> Wrapped(SimulatedDbConnection connection, IEnumerable<SimulatedStatementOutcome> body, ProcedureTransactionEnding raisedEnding, bool commitsAfterFailedStatement, bool sendsMessagesInside, bool rollsBack)
        {
            var transaction = connection.StartTransaction(IsolationLevel.Unspecified);
            var failed = false;
            using (var outcomes = body.GetEnumerator())
            {
                while (true)
                {
                    try
                    {
                        if (!outcomes.MoveNext())
                            break;
                    }
                    catch (SimulatedSqlException error)
                    {
                        EndAtError(connection, transaction, error, raisedEnding, commitsAfterFailedStatement);
                        throw;
                    }
                    failed |= outcomes.Current is SimulatedErrorOutcome;
                    yield return outcomes.Current;
                }
            }
            if (sendsMessagesInside)
            {
                foreach (var message in DrainPendingMessages(connection))
                {
                    message.TransactionEventMark = connection.TransactionEventsRecorded;
                    yield return message;
                }
            }
            End(connection, transaction, commit: (!failed || commitsAfterFailedStatement) && !rollsBack);
        }
    }

    /// <summary>
    /// <paramref name="error"/>, raised once the procedure has opened its own
    /// transaction, which then ends as <paramref name="raisedEnding"/> says
    /// (<c>sp_unbindefault</c>'s missing column, refused after the commit);
    /// raised as it stands inside the caller's transaction.
    /// </summary>
    private static SimulatedSqlException RaisedInProcedureTransaction(BatchContext batch, SimulatedSqlException error, ProcedureTransactionEnding raisedEnding)
    {
        var connection = batch.Connection;
        if (!batch.IsSkipping && connection.CurrentTransaction is null)
            EndAtError(connection, connection.StartTransaction(IsolationLevel.Unspecified), error, raisedEnding, commitsAfterFailedStatement: false);
        return error;
    }

    /// <summary>
    /// Runs <paramref name="body"/> — the security statement a legacy login
    /// procedure builds — so that it opens no <c>IMPLICIT_TRANSACTIONS</c>
    /// transaction: under the option real's <c>sp_password</c>,
    /// <c>sp_addlogin</c>, <c>sp_droplogin</c>, <c>sp_revokelogin</c> and <c>sp_dropsrvrolemember</c>
    /// leave <c>@@TRANCOUNT</c> 0, where the statement run on its own opens
    /// one (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> WithoutImplicitTransaction(SimulatedDbConnection connection, IEnumerable<SimulatedStatementOutcome> body)
    {
        using var outcomes = body.GetEnumerator();
        while (true)
        {
            var implicitTransactions = connection.ImplicitTransactions;
            connection.ImplicitTransactions = false;
            try
            {
                if (!outcomes.MoveNext())
                    yield break;
            }
            finally
            {
                connection.ImplicitTransactions = implicitTransactions;
            }
            yield return outcomes.Current;
        }
    }

    /// <summary>
    /// Ends a procedure's own transaction around the error ending the
    /// procedure: before a raised one that follows its ending, after one
    /// raised ahead of it — a failed statement's included — whose error goes
    /// out first (<see cref="SimulatedSqlException.TransactionEventMark"/>),
    /// unless a <c>TRY</c> catches it there and the ending never runs. An
    /// error that aborts as under <c>XACT_ABORT</c> always rolls back.
    /// </summary>
    private static void EndAtError(SimulatedDbConnection connection, SimulatedDbTransaction transaction, SimulatedSqlException error, ProcedureTransactionEnding raisedEnding, bool commitsAfterFailedStatement)
    {
        var raised = !error.RaisedByProcedureStatement;
        if (raised && raisedEnding != ProcedureTransactionEnding.CommitAfter)
        {
            error.FollowsTransactionEnd = true;
            End(connection, transaction, commit: raisedEnding == ProcedureTransactionEnding.CommitFirst);
            return;
        }
        error.TransactionEventMark = connection.TransactionEventsRecorded;
        if (connection.OpenTryFrames == 0)
            End(connection, transaction, commit: (raised || commitsAfterFailedStatement) && !error.AbortsAsUnderXactAbort);
    }

    /// <summary>
    /// <see cref="EndAtError"/> for the transaction a procedure nested inside
    /// its caller's: its COMMIT takes the nesting level back, and its ROLLBACK
    /// ends the caller's transaction whole when the procedure rolls back
    /// (<c>sp_addrolemember</c>) — or, for the others, leaves it as a COMMIT
    /// would.
    /// </summary>
    private static void EndNestedAtError(SimulatedDbConnection connection, SimulatedDbTransaction caller, SimulatedSqlException error, ProcedureTransactionEnding raisedEnding, bool commitsAfterFailedStatement, bool rollsBackCaller)
    {
        var raised = !error.RaisedByProcedureStatement;
        if (raised && raisedEnding != ProcedureTransactionEnding.CommitAfter)
        {
            if (raisedEnding == ProcedureTransactionEnding.RollbackFirst && rollsBackCaller)
            {
                error.FollowsTransactionEnd = true;
                End(connection, caller, commit: false);
            }
            else
            {
                Unnest(connection, caller);
            }
            return;
        }
        error.TransactionEventMark = connection.TransactionEventsRecorded;
        if (connection.OpenTryFrames != 0 || error.AbortsAsUnderXactAbort)
            return;
        if (!raised && !commitsAfterFailedStatement && rollsBackCaller)
            End(connection, caller, commit: false);
        else
            Unnest(connection, caller);
    }

    /// <summary>The nested procedure transaction's COMMIT: <c>@@TRANCOUNT</c> falls back by one.</summary>
    private static void Unnest(SimulatedDbConnection connection, SimulatedDbTransaction caller)
    {
        if (ReferenceEquals(connection.CurrentTransaction, caller) && caller.TranCount > 1)
            caller.TranCount--;
    }

    private static void End(SimulatedDbConnection connection, SimulatedDbTransaction transaction, bool commit)
    {
        if (!ReferenceEquals(connection.CurrentTransaction, transaction))
            return;
        if (commit && !transaction.Doomed)
            transaction.EndCommit();
        else
            transaction.EndRollback();
    }
}
