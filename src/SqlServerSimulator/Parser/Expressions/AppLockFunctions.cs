using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>APPLOCK_MODE(@DbPrincipal, @Resource, @LockOwner)</c>: the mode the
/// calling session's given owner holds on an application-lock resource, as
/// the probe-confirmed strings (<c>NoLock</c> / <c>Shared</c> / <c>Update</c>
/// / <c>IntentShared</c> / <c>IntentExclusive</c> / <c>Exclusive</c>); the
/// strongest mode wins when a conversion left several holds outstanding.
/// Reads the owner-scoped ledger, so a lock the same connection holds under
/// the OTHER owner reports <c>NoLock</c> (probe-confirmed per-owner
/// visibility). Returns <c>nvarchar(32)</c>.
/// </summary>
internal sealed class AppLockMode : Expression
{
    private readonly Expression principal;
    private readonly Expression resource;
    private readonly Expression owner;

    public AppLockMode(ParserContext context)
    {
        this.principal = Parse(context);
        this.resource = ParseAfterComma(context);
        this.owner = ParseAfterComma(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    internal static Expression ParseAfterComma(ParserContext context) =>
        context.Token is Tokens.Operator { Character: ',' }
            ? Parse(context.MoveNextRequiredReturnSelf())
            : throw SimulatedSqlException.SyntaxErrorNear(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var ledger = AppLockFunctionArguments.ResolveOwnerLedger(runtime, this.owner, "applock_mode");
        var (principalId, resourceName) = AppLockFunctionArguments.Resolve(runtime, this.principal, this.resource, "applock_mode");

        LockMode? strongest = null;
        foreach (var hold in ledger)
        {
            if (hold.PrincipalId == principalId
                && string.Equals(hold.Resource, resourceName, StringComparison.Ordinal)
                && (strongest is not { } best || AppLock.ModeStrength(hold.Mode) > AppLock.ModeStrength(best)))
            {
                strongest = hold.Mode;
            }
        }

        return SqlValue.FromNVarchar(strongest is { } mode ? AppLock.ModeDisplayName(mode) : "NoLock");
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        AppLockFunctionArguments.RequireStrings(batch, resolveColumnType, "applock_mode", this.principal, this.resource, this.owner);
        return NVarcharSqlType.Get(32, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);
    }

    internal override string DebugDisplay() =>
        $"APPLOCK_MODE({this.principal.DebugDisplay()}, {this.resource.DebugDisplay()}, {this.owner.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.principal).Child(this.resource).Child(this.owner);
}

/// <summary>
/// <c>APPLOCK_TEST(@DbPrincipal, @Resource, @LockMode, @LockOwner)</c>:
/// 1 when the calling session could acquire the mode on the resource
/// without blocking (a re-entrant grant over its own holds counts — probe-
/// confirmed), 0 when another session's hold conflicts. Returns
/// <c>smallint</c>. An unrecognized mode string raises Msg 1225 — unlike
/// <c>sp_getapplock</c>'s silent -999 for the same string.
/// </summary>
internal sealed class AppLockTest : Expression
{
    private readonly Expression principal;
    private readonly Expression resource;
    private readonly Expression mode;
    private readonly Expression owner;

    public AppLockTest(ParserContext context)
    {
        this.principal = Parse(context);
        this.resource = AppLockMode.ParseAfterComma(context);
        this.mode = AppLockMode.ParseAfterComma(context);
        this.owner = AppLockMode.ParseAfterComma(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // The owner argument gates the Msg 3918 transaction-context check
        // but doesn't change the answer: conflicts are computed against
        // OTHER connections' holds, and this connection's own holds are
        // compatible regardless of which owner carries them.
        _ = AppLockFunctionArguments.ResolveOwnerLedger(runtime, this.owner, "applock_test");

        var modeValue = this.mode.Run(runtime);
        if (modeValue.IsNull)
            throw SimulatedSqlException.InvalidAppLockModeForTest(state: 2);
        if (!AppLock.TryParseMode(modeValue.CoerceTo(SqlType.NVarchar).AsString, out var probeMode))
            throw SimulatedSqlException.InvalidAppLockModeForTest();

        var (principalId, resourceName) = AppLockFunctionArguments.Resolve(runtime, this.principal, this.resource, "applock_test");

        var connection = runtime.Batch.Connection;
        var database = runtime.Batch.CurrentDatabase;
        LockResource? resource;
        lock (database.ApplicationLocks)
        {
            _ = database.ApplicationLocks.TryGetValue((principalId, resourceName), out resource);
        }

        var wouldGrant = resource is null
            || !connection.Simulation.LockManager.HasIncompatibleHolderOtherThan(resource, probeMode, connection.Session);
        return SqlValue.FromInt16((short)(wouldGrant ? 1 : 0));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        AppLockFunctionArguments.RequireStrings(batch, resolveColumnType, "applock_test", this.principal, this.resource, this.mode, this.owner);
        return SqlType.SmallInt;
    }

    // Every argument path either answers 1 / 0 or raises, so the NOT NULL real
    // projects here is reachable unconditionally — unlike APPLOCK_MODE, which
    // real projects nullable.
    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() =>
        $"APPLOCK_TEST({this.principal.DebugDisplay()}, {this.resource.DebugDisplay()}, {this.mode.DebugDisplay()}, {this.owner.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.principal).Child(this.resource).Child(this.mode).Child(this.owner);
}

/// <summary>
/// Shared argument resolution for <see cref="AppLockMode"/> /
/// <see cref="AppLockTest"/>. Every argument is a string, checked while
/// compiling; at run time real judges the owner first (Msg 1226 for a bad
/// one, Msg 3918 for the (defaulted-to-)Transaction owner outside a user
/// transaction), then <c>APPLOCK_TEST</c>'s mode, then the principal (Msg
/// 1230 for a typed NULL, Msg 1202 for an unknown one) — probed 2026-09-26
/// against SQL Server 2025.
/// </summary>
internal static class AppLockFunctionArguments
{
    /// <summary>
    /// Refuses a non-string argument, or a bare <c>NULL</c> in any slot but
    /// the owner's, with Msg 8116 naming its position.
    /// </summary>
    public static void RequireStrings(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType, string functionName, params ReadOnlySpan<Expression> arguments)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (i < arguments.Length - 1 && Expression.IsUntypedNullLiteral(argument))
                throw SimulatedSqlException.InvalidArgumentDataType("NULL", i + 1, functionName);
            _ = StringScalars.RequireStringArgument(argument, argument.GetSqlType(batch, resolveColumnType), functionName, i + 1, acceptsLegacyLob: false);
        }
    }

    public static (int PrincipalId, string ResourceName) Resolve(RuntimeContext runtime, Expression principal, Expression resource, string functionName)
    {
        var principalValue = principal.Run(runtime);
        if (principalValue.IsNull)
            throw SimulatedSqlException.InvalidAppLockPrincipalForFunction(functionName);
        var resourceValue = resource.Run(runtime);
        if (resourceValue.IsNull)
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", 2, functionName);

        var principalName = principalValue.CoerceTo(SqlType.NVarchar).AsString;
        return runtime.Batch.CurrentDatabase.Principals.TryGetValue(principalName, out var found)
            ? (found.PrincipalId, AppLock.NormalizeResource(resourceValue.CoerceTo(SqlType.NVarchar).AsString))
            : throw SimulatedSqlException.DatabasePrincipalDoesNotExist(principalName);
    }

    /// <summary>
    /// Resolves the owner argument to its ledger. NULL defaults toward the
    /// Transaction owner (probe: NULL owner outside a tx raises Msg 3918,
    /// the same as an explicit <c>'Transaction'</c>); an unrecognized
    /// string raises Msg 1226 with the function's name interpolated.
    /// </summary>
    public static List<AppLockHold> ResolveOwnerLedger(RuntimeContext runtime, Expression owner, string functionName)
    {
        var ownerValue = owner.Run(runtime);
        var isTransaction = true;
        if (!ownerValue.IsNull && !AppLock.TryParseOwner(ownerValue.CoerceTo(SqlType.NVarchar).AsString, out isTransaction))
            throw SimulatedSqlException.InvalidAppLockOwnerForFunction(functionName);

        var connection = runtime.Batch.Connection;
        return !isTransaction
            ? connection.SessionAppLocks
            : connection.CurrentTransaction is { } transaction
                ? transaction.TransactionAppLocks
                : throw SimulatedSqlException.MustExecuteInUserTransaction();
    }
}
