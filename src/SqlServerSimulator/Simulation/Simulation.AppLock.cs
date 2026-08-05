using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_getapplock</c> / <c>sp_releaseapplock</c>: cooperative
/// application locks over the shared <see cref="Storage.LockManager"/>.
/// One <see cref="LockResource"/> per (database-principal, resource name)
/// keeps conflict semantics unified across the two owner kinds; per-owner
/// ledgers (<c>SimulatedDbConnection.SessionAppLocks</c> /
/// <c>SimulatedDbTransaction.TransactionAppLocks</c>) carry the identity
/// the owner-scoped views need. Every behavior here — return codes vs
/// raised errors, reference counting, name truncation, lifecycle — is
/// probe-confirmed against SQL Server 2025; the deep-dive lives in
/// <c>docs/claude/app-locks.md</c>.
/// </summary>
public partial class Simulation
{
    /// <summary>
    /// <c>EXEC @rc = sp_getapplock @Resource, @LockMode, @LockOwner,
    /// @LockTimeout, @DbPrincipal</c>. Return codes (never exceptions for
    /// lock-arbitration outcomes, probe-confirmed): 0 granted, 1 granted
    /// after waiting, -1 timeout, -3 deadlock victim, -999 validation
    /// failure (bad mode / owner string, Transaction owner outside a
    /// transaction, missing <c>@Resource</c>). Raised errors are reserved
    /// for NULL resource (Msg 1224), timeout below -1 (Msg 1227), unknown
    /// principal (Msg 1202), and missing <c>@LockMode</c> (Msg 201, a
    /// binding-time check).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpGetAppLock(BatchContext batch, string? returnCodeVariableName)
    {
        var args = ParseAppLockArguments(batch, "sp_getapplock", acceptsModeAndTimeout: true);
        if (batch.IsSkipping)
            yield break;

        // Missing @LockMode is a parameter-binding failure (Msg 201) and so
        // precedes every body-level check — including the silent -999 a
        // missing @Resource produces.
        if (!args.HasMode)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_getapplock", "LockMode");

        var connection = batch.Connection;
        if (!args.HasResource)
        {
            SetAppLockReturnCode(batch, returnCodeVariableName, -999);
            yield break;
        }
        if (args.Resource.IsNull)
            throw SimulatedSqlException.InvalidAppLockResource();

        // NULL timeout falls back to the session default, like an omitted
        // parameter (probe-confirmed rc=0 with @LockTimeout=NULL).
        var timeout = args.HasTimeout && !args.Timeout.IsNull
            ? ScalarArguments.CoerceProcedureParameter(args.Timeout, SqlType.Int32)
            : connection.LockTimeoutMillis;
        if (timeout < -1)
            throw SimulatedSqlException.InvalidAppLockTimeout();

        var (principalId, _) = ResolveAppLockPrincipal(batch, args);

        if (!AppLock.TryParseMode(args.Mode.IsNull ? "" : args.Mode.CoerceTo(SqlType.NVarchar).AsString, out var mode)
            || !TryResolveAppLockOwner(args, out var isTransaction))
        {
            SetAppLockReturnCode(batch, returnCodeVariableName, -999);
            yield break;
        }

        // Transaction owner without an active transaction: silent -999
        // (probe-confirmed — unlike APPLOCK_MODE / APPLOCK_TEST, which
        // raise Msg 3918 for the same condition).
        var transaction = connection.CurrentTransaction;
        if (isTransaction && transaction is null)
        {
            SetAppLockReturnCode(batch, returnCodeVariableName, -999);
            yield break;
        }

        var resourceName = AppLock.NormalizeResource(args.Resource.CoerceTo(SqlType.NVarchar).AsString);
        var resource = batch.CurrentDatabase.GetOrCreateApplicationLock(principalId, resourceName);

        var outcome = connection.Simulation.LockManager.TryAcquire(resource, mode, connection.Session, timeout);
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
                transaction!.HeldLocks.Add((resource, mode));
                transaction.TransactionAppLocks.Add(hold);
            }
            else
            {
                connection.SessionAppLocks.Add(hold);
            }
        }

        SetAppLockReturnCode(batch, returnCodeVariableName, code);
    }

    /// <summary>
    /// <c>EXEC @rc = sp_releaseapplock @Resource, @LockOwner,
    /// @DbPrincipal</c>. Releases ONE reference of the owner's hold on the
    /// resource (probe-confirmed reference counting); after a mode
    /// conversion the strongest-mode hold releases first. Missing or NULL
    /// <c>@Resource</c> raises Msg 1224; releasing a resource the owner
    /// doesn't hold raises Msg 1223; a bad owner string returns -999.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpReleaseAppLock(BatchContext batch, string? returnCodeVariableName)
    {
        var args = ParseAppLockArguments(batch, "sp_releaseapplock", acceptsModeAndTimeout: false);
        if (batch.IsSkipping)
            yield break;

        if (!args.HasResource || args.Resource.IsNull)
            throw SimulatedSqlException.InvalidAppLockResource();

        var (principalId, principalName) = ResolveAppLockPrincipal(batch, args);

        if (!TryResolveAppLockOwner(args, out var isTransaction))
        {
            SetAppLockReturnCode(batch, returnCodeVariableName, -999);
            yield break;
        }

        var connection = batch.Connection;
        var transaction = connection.CurrentTransaction;
        if (isTransaction && transaction is null)
        {
            SetAppLockReturnCode(batch, returnCodeVariableName, -999);
            yield break;
        }

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
            throw SimulatedSqlException.CannotReleaseAppLockNotHeld(principalName, resourceName);

        var hold = ledger[bestIndex];
        connection.Simulation.LockManager.Release(hold.LockResource, hold.Mode, connection.Session);
        ledger.RemoveAt(bestIndex);
        if (isTransaction)
        {
            // Retire one matching HeldLocks entry too, so transaction-end
            // release doesn't double-release the manager hold.
            for (var i = transaction!.HeldLocks.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(transaction.HeldLocks[i].Resource, hold.LockResource) && transaction.HeldLocks[i].Mode == hold.Mode)
                {
                    transaction.HeldLocks.RemoveAt(i);
                    break;
                }
            }
        }

        SetAppLockReturnCode(batch, returnCodeVariableName, 0);
    }

    // Parsed sp_getapplock / sp_releaseapplock arguments. Presence flags are
    // distinct from NULL-ness — a missing @Resource behaves differently from
    // an explicit NULL (silent -999 vs Msg 1224 on sp_getapplock).
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
    // database's principals; unknown → Msg 1202. The simulator has no
    // membership model, so an existing principal always passes — real SQL
    // Server additionally requires the caller to be a member.
    private static (int PrincipalId, string Name) ResolveAppLockPrincipal(BatchContext batch, in AppLockArguments args)
    {
        var name = args.HasPrincipal && !args.Principal.IsNull
            ? args.Principal.CoerceTo(SqlType.NVarchar).AsString
            : "public";
        return batch.CurrentDatabase.Principals.TryGetValue(name, out var principal)
            ? (principal.PrincipalId, name)
            : throw SimulatedSqlException.DatabasePrincipalDoesNotExist(name);
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

    // Writes an app-lock return code into the EXEC @rc = … variable, when
    // one was supplied. Mirrors InvokeProcedure's return-code write-back.
    private static void SetAppLockReturnCode(BatchContext batch, string? returnCodeVariableName, int code)
    {
        if (returnCodeVariableName is null)
            return;
        var slot = batch.GetVariableSlot(returnCodeVariableName);
        slot.Value = SqlValue.FromInt32(code).CoerceTo(slot.DeclaredType);
    }
}
