using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_getapplock / sp_releaseapplock: cooperative application locks over the
// shared LockManager. One LockResource per (database-principal, resource name)
// keeps conflict semantics unified across the two owner kinds; per-owner
// ledgers (SimulatedDbConnection.SessionAppLocks /
// SimulatedDbTransaction.TransactionAppLocks) carry the identity the
// owner-scoped views need. Every behavior here — return codes vs raised
// errors, reference counting, name truncation, lifecycle — is probe-confirmed
// against SQL Server 2025; the deep-dive lives in docs/claude/app-locks.md.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
public partial class Simulation
{
    /// <summary>
    /// <c>EXEC @rc = sp_getapplock @Resource, @LockMode, @LockOwner,
    /// @LockTimeout, @DbPrincipal</c>. Return codes (never exceptions for
    /// lock-arbitration outcomes, probe-confirmed): 0 granted, 1 granted
    /// after waiting, -1 timeout, -3 deadlock victim, -999 refused. The
    /// procedure judges the mode, then the owner, then a Transaction owner
    /// outside a transaction, each printing its message and answering -999;
    /// what passes goes to <c>sys.xp_userlock</c>, which refuses a timeout
    /// below -1 (Msg 1227), a missing or NULL resource (Msg 1224) and an
    /// unknown principal (Msg 1202) in that order, after which the procedure
    /// still answers -999 (probed 2026-10-17 against SQL Server 2025). A
    /// missing <c>@LockMode</c> is Msg 201, a binding-time check. Each of the
    /// statements real's procedure runs on the way closes with a DONEINPROC
    /// of its own, which <c>NOCOUNT</c> drops (<see cref="AppLockStep"/>).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpGetAppLock(BatchContext batch, string? returnCodeVariableName)
    {
        var args = ParseAppLockArguments(batch, "sp_getapplock", acceptsModeAndTimeout: true);
        if (batch.IsSkipping)
            yield break;

        // Missing @LockMode is a parameter-binding failure (Msg 201) and so
        // precedes every body-level check.
        if (!args.HasMode)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_getapplock", "LockMode");

        var connection = batch.Connection;
        // A NULL timeout falls back to the session default, like an omitted
        // parameter (probe-confirmed rc=0 with @LockTimeout=NULL), which the
        // procedure assigns in a statement of its own; one out of int's range
        // fails binding (Msg 8114).
        var timeoutGiven = args.HasTimeout && !args.Timeout.IsNull;
        var timeout = timeoutGiven
            ? ScalarArguments.CoerceProcedureParameter(args.Timeout, SqlType.Int32)
            : connection.LockTimeoutMillis;
        yield return AppLockStep(batch, StatementDoneKind.Select);
        yield return AppLockStep(batch, StatementDoneKind.Condition);
        var writtenMode = args.Mode.IsNull ? "" : args.Mode.CoerceTo(SqlType.NVarchar).AsString;
        if (!AppLock.TryParseMode(writtenMode, out var mode))
        {
            foreach (var step in AppLockRefusal(batch, returnCodeVariableName, UnrecognizedAppLockOption(batch, "sp_getapplock", 26, writtenMode, "LockMode")))
                yield return step;
            yield break;
        }
        yield return AppLockStep(batch, StatementDoneKind.Select);
        yield return AppLockStep(batch, StatementDoneKind.Condition);
        if (!TryResolveAppLockOwner(args, out var isTransaction))
        {
            foreach (var step in AppLockRefusal(batch, returnCodeVariableName, UnrecognizedAppLockOption(batch, "sp_getapplock", 39, WrittenOwner(args), "LockOwner")))
                yield return step;
            yield break;
        }

        yield return AppLockStep(batch, StatementDoneKind.Condition);
        yield return AppLockStep(batch, StatementDoneKind.Select);
        if (!timeoutGiven)
            yield return AppLockStep(batch, StatementDoneKind.Select);
        yield return AppLockStep(batch, StatementDoneKind.Condition);

        // Transaction owner without an active transaction: Msg 15626 and
        // -999, where APPLOCK_MODE / APPLOCK_TEST raise Msg 3918.
        var transaction = connection.CurrentTransaction;
        if (isTransaction && transaction is null)
        {
            foreach (var step in AppLockRefusal(batch, returnCodeVariableName, Printed(SimulatedSqlException.TransactionalAppLockWithoutTransactionMessage(batch))))
                yield return step;
            yield break;
        }

        var refusal = timeout < -1 ? SimulatedSqlException.InvalidAppLockTimeout()
            : !args.HasResource || args.Resource.IsNull ? SimulatedSqlException.InvalidAppLockResource()
            : !TryResolveAppLockPrincipal(batch, args, out _, out var unknownPrincipal) ? SimulatedSqlException.DatabasePrincipalDoesNotExist(unknownPrincipal)
            : null;
        if (refusal is not null)
        {
            foreach (var step in UserLockFailure(batch, returnCodeVariableName, refusal))
                yield return step;
            yield break;
        }
        _ = TryResolveAppLockPrincipal(batch, args, out var principalId, out _);

        var resourceName = AppLock.NormalizeResource(args.Resource.CoerceTo(SqlType.NVarchar).AsString);
        var resource = batch.CurrentDatabase.GetOrCreateApplicationLock(principalId, resourceName);

        // A transaction's lock is held by the owner the sessions bound to it
        // share, a session's by the session alone.
        var owner = isTransaction ? connection.LockOwner : connection.Session;
        var outcome = connection.Simulation.LockManager.TryAcquire(resource, mode, owner, timeout);
        var code = outcome switch
        {
            LockAcquireOutcome.Granted => 0,
            LockAcquireOutcome.GrantedAfterWait => 1,
            LockAcquireOutcome.TimedOut => -1,
            _ => -3,
        };

        if (code >= 0)
        {
            var hold = new AppLockHold(principalId, resourceName, resource, mode);
            if (isTransaction)
            {
                // The generic HeldLocks entry is what releases the manager
                // hold at transaction end; the app-lock ledger carries the
                // identity for the owner-scoped views.
                transaction!.HeldLocks.Add((resource, mode, owner));
                transaction.TransactionAppLocks.Add(hold);
            }
            else
            {
                connection.SessionAppLocks.Add(hold);
            }
        }

        yield return AppLockStep(batch, StatementDoneKind.Execute);
        yield return AppLockStep(batch, StatementDoneKind.Select);
        SetReturnCode(batch, returnCodeVariableName, code);
    }

    /// <summary>
    /// <c>EXEC @rc = sp_releaseapplock @Resource, @LockOwner,
    /// @DbPrincipal</c>. Releases ONE reference of the owner's hold on the
    /// resource (probe-confirmed reference counting); after a mode
    /// conversion the strongest-mode hold releases first. A bad owner string
    /// prints Msg 15625 and returns -999; <c>sys.xp_userlock</c> then refuses
    /// a missing or NULL <c>@Resource</c> (Msg 1224), a Transaction owner
    /// outside a transaction (Msg 3918), an unknown principal (Msg 1202) and
    /// a resource the owner doesn't hold (Msg 1223) in that order, the
    /// procedure answering -999 after each (probed 2026-10-17 against SQL
    /// Server 2025). Its statements close in DONEINPROCs of their own, as
    /// <c>sp_getapplock</c>'s do.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpReleaseAppLock(BatchContext batch, string? returnCodeVariableName)
    {
        var args = ParseAppLockArguments(batch, "sp_releaseapplock", acceptsModeAndTimeout: false);
        if (batch.IsSkipping)
            yield break;

        yield return AppLockStep(batch, StatementDoneKind.Select);
        yield return AppLockStep(batch, StatementDoneKind.Condition);
        if (!TryResolveAppLockOwner(args, out var isTransaction))
        {
            foreach (var step in AppLockRefusal(batch, returnCodeVariableName, UnrecognizedAppLockOption(batch, "sp_releaseapplock", 20, WrittenOwner(args), "LockOwner")))
                yield return step;
            yield break;
        }
        yield return AppLockStep(batch, StatementDoneKind.Select);

        var connection = batch.Connection;
        var transaction = connection.CurrentTransaction;
        var refusal = !args.HasResource || args.Resource.IsNull ? SimulatedSqlException.InvalidAppLockResource()
            : isTransaction && transaction is null ? SimulatedSqlException.MustExecuteInUserTransaction(state: 1)
            : !TryResolveAppLockPrincipal(batch, args, out _, out var unknownPrincipal) ? SimulatedSqlException.DatabasePrincipalDoesNotExist(unknownPrincipal)
            : null;
        if (refusal is not null)
        {
            foreach (var step in UserLockFailure(batch, returnCodeVariableName, refusal))
                yield return step;
            yield break;
        }
        _ = TryResolveAppLockPrincipal(batch, args, out var principalId, out var principalName);

        var resourceName = AppLock.NormalizeResource(args.Resource.CoerceTo(SqlType.NVarchar).AsString);
        var ledger = isTransaction ? transaction!.TransactionAppLocks : connection.SessionAppLocks;

        // Pick this owner's strongest-mode hold on the resource — after a
        // Shared→Exclusive conversion both holds are outstanding and the
        // release order approximates the converted lock draining first.
        var bestIndex = -1;
        for (var i = 0; i < ledger.Count; i++)
        {
            if (ledger[i].PrincipalId == principalId
                && string.Equals(ledger[i].Resource, resourceName, StringComparison.Ordinal)
                && (bestIndex < 0 || AppLock.ModeStrength(ledger[i].Mode) > AppLock.ModeStrength(ledger[bestIndex].Mode)))
            {
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            foreach (var step in UserLockFailure(batch, returnCodeVariableName, SimulatedSqlException.CannotReleaseAppLockNotHeld(principalName, resourceName)))
                yield return step;
            yield break;
        }

        var hold = ledger[bestIndex];
        var owner = connection.Session;
        ledger.RemoveAt(bestIndex);
        if (isTransaction)
        {
            // Retire one matching HeldLocks entry too, so transaction-end
            // release doesn't double-release the manager hold.
            for (var i = transaction!.HeldLocks.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(transaction.HeldLocks[i].Resource, hold.LockResource) && transaction.HeldLocks[i].Mode == hold.Mode)
                {
                    owner = transaction.HeldLocks[i].Owner;
                    transaction.HeldLocks.RemoveAt(i);
                    break;
                }
            }
        }
        connection.Simulation.LockManager.Release(hold.LockResource, hold.Mode, owner);

        yield return AppLockStep(batch, StatementDoneKind.Execute);
        yield return AppLockStep(batch, StatementDoneKind.Select);
        SetReturnCode(batch, returnCodeVariableName, 0);
    }

    // Parsed sp_getapplock / sp_releaseapplock arguments. Presence flags are
    // distinct from NULL-ness, so an omitted argument never reads a default
    // SqlValue.
    private struct AppLockArguments
    {
        public SqlValue Resource;
        public bool HasResource;
        public SqlValue Mode;
        public bool HasMode;
        public SqlValue Owner;
        public bool HasOwner;
        public SqlValue Timeout;
        public bool HasTimeout;
        public SqlValue Principal;
        public bool HasPrincipal;
    }

    // Binds positional / named EXEC arguments to the app-lock parameter
    // shape. sp_getapplock's positional order is (@Resource, @LockMode,
    // @LockOwner, @LockTimeout, @DbPrincipal); sp_releaseapplock's is
    // (@Resource, @LockOwner, @DbPrincipal).
    private static AppLockArguments ParseAppLockArguments(BatchContext batch, string procName, bool acceptsModeAndTimeout)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        var result = default(AppLockArguments);
        var positional = 0;
        foreach (var arg in arguments)
        {
            string parameterName;
            if (arg.Name is { } name)
            {
                parameterName = name;
            }
            else
            {
                var index = positional++;
                parameterName = (acceptsModeAndTimeout, index) switch
                {
                    (true, 0) => "Resource",
                    (true, 1) => "LockMode",
                    (true, 2) => "LockOwner",
                    (true, 3) => "LockTimeout",
                    (true, 4) => "DbPrincipal",
                    (false, 0) => "Resource",
                    (false, 1) => "LockOwner",
                    (false, 2) => "DbPrincipal",
                    _ => throw SimulatedSqlException.InvalidProcedureParameters(procName),
                };
            }

            switch (parameterName)
            {
                case var n when BuiltInToken.Equals(n, "Resource"):
                    (result.Resource, result.HasResource) = (arg.Value, true);
                    break;
                case var n when acceptsModeAndTimeout && BuiltInToken.Equals(n, "LockMode"):
                    (result.Mode, result.HasMode) = (arg.Value, true);
                    break;
                case var n when BuiltInToken.Equals(n, "LockOwner"):
                    (result.Owner, result.HasOwner) = (arg.Value, true);
                    break;
                case var n when acceptsModeAndTimeout && BuiltInToken.Equals(n, "LockTimeout"):
                    (result.Timeout, result.HasTimeout) = (arg.Value, true);
                    break;
                case var n when BuiltInToken.Equals(n, "DbPrincipal"):
                    (result.Principal, result.HasPrincipal) = (arg.Value, true);
                    break;
                default:
                    throw SimulatedSqlException.InvalidProcedureParameters(procName);
            }
        }

        return result;
    }

    // Resolves @DbPrincipal (default 'public') against the current
    // database's principals; false for an unknown one (Msg 1202). The
    // simulator has no membership model, so an existing principal always
    // passes — real SQL Server additionally requires the caller to be a
    // member.
    private static bool TryResolveAppLockPrincipal(BatchContext batch, in AppLockArguments args, out int principalId, out string name)
    {
        name = args.HasPrincipal && !args.Principal.IsNull
            ? args.Principal.CoerceTo(SqlType.NVarchar).AsString
            : "public";
        principalId = batch.CurrentDatabase.Principals.TryGetValue(name, out var principal) ? principal.PrincipalId : 0;
        return principal is not null;
    }

    // Owner defaults to Transaction when omitted (probe-confirmed); a NULL
    // or unrecognized owner string reads as invalid (caller maps to -999).
    private static bool TryResolveAppLockOwner(in AppLockArguments args, out bool isTransaction)
    {
        if (!args.HasOwner)
        {
            isTransaction = true;
            return true;
        }

        if (!args.Owner.IsNull && AppLock.TryParseOwner(args.Owner.CoerceTo(SqlType.NVarchar).AsString, out isTransaction))
            return true;

        isTransaction = default;
        return false;
    }

    /// <summary>
    /// An error the application-lock procedures raise through
    /// <c>sys.xp_userlock</c>, which real attributes to it at line 1: the
    /// procedure's call of it fails, closing in a DONEINPROC of its own, and
    /// the procedure goes on to its last statement and answers -999 (probed
    /// 2026-10-17 against SQL Server 2025). A <c>TRY</c> around the call
    /// catches it before the procedure returns, the call's DONEINPROC going
    /// out without the error and the caller's return code left unset, and
    /// under <c>XACT_ABORT</c> it ends the batch; both throw it.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> UserLockFailure(BatchContext batch, string? returnCodeVariableName, SimulatedSqlException error)
    {
        error.PreserveDiagnostics(1, "sys.xp_userlock");
        var connection = batch.Connection;
        if (connection.OpenTryFrames > 0 || connection.XactAbort)
        {
            error.CaughtBeforeReturn = connection.OpenTryFrames > 0;
            if (error.CaughtBeforeReturn)
                yield return AppLockStep(batch, StatementDoneKind.Execute);
            throw error;
        }
        SetReturnCode(batch, returnCodeVariableName, -999);
        yield return new SimulatedErrorOutcome(error)
        {
            DoneKind = StatementDoneKind.Execute,
            InModule = batch.ProcFrame is not null || batch.TriggerFrame is not null,
            TransactionEventMark = connection.TransactionEventsRecorded,
        };
        yield return AppLockStep(batch, StatementDoneKind.Select);
    }

    /// <summary>
    /// A check the application-lock procedures fail themselves: its message,
    /// the <c>RAISERROR</c> that printed it closing, the statement after it,
    /// and -999.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> AppLockRefusal(BatchContext batch, string? returnCodeVariableName, SimulatedInfoOutcome message)
    {
        yield return message;
        yield return AppLockStep(batch, StatementDoneKind.RaisError);
        yield return AppLockStep(batch, StatementDoneKind.Select);
        SetReturnCode(batch, returnCodeVariableName, -999);
    }

    /// <summary>
    /// The DONEINPROC one of the statements real's application-lock procedures
    /// are written in closes with — a variable assignment counting its one
    /// row, an <c>IF</c>, the call of <c>sys.xp_userlock</c>, a
    /// <c>RAISERROR</c> — which <c>NOCOUNT ON</c> drops, as it drops any
    /// that carries neither rows nor an error (probed 2026-10-17 against SQL
    /// Server 2025).
    /// </summary>
    private static SimulatedNonQuery AppLockStep(BatchContext batch, ushort kind) =>
        StatementDone(batch, kind, kind == StatementDoneKind.Select ? 1 : -1);

    // Msg 15625, which the procedure prints ahead of answering -999.
    private static SimulatedInfoOutcome UnrecognizedAppLockOption(BatchContext batch, string procedure, int line, string written, string parameter) =>
        Printed(SimulatedSqlException.AppLockOptionNotRecognizedMessage(batch, procedure, line, written, parameter));

    // The owner string as the caller wrote it, for Msg 15625.
    private static string WrittenOwner(in AppLockArguments args) =>
        args.Owner.IsNull ? "" : args.Owner.CoerceTo(SqlType.NVarchar).AsString;
}
