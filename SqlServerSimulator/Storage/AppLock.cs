namespace SqlServerSimulator.Storage;

/// <summary>
/// One <c>sp_getapplock</c> acquisition as tracked by its owner-scoped
/// ledger — <c>SimulatedDbConnection.SessionAppLocks</c> for the
/// <c>Session</c> owner, <c>SimulatedDbTransaction.TransactionAppLocks</c>
/// for the <c>Transaction</c> owner. One entry per successful acquire (the
/// probe-confirmed reference counting: N acquires need N releases), so a
/// ledger's entry count for a (principal, resource) pair IS its outstanding
/// count. The <see cref="LockManager"/> hold on <see cref="LockResource"/>
/// remains the single conflict authority across both owner kinds; the
/// ledgers exist for the per-owner views (<c>APPLOCK_MODE</c>,
/// <c>sp_releaseapplock</c>'s not-held check, lifecycle release).
/// </summary>
internal readonly struct AppLockHold(int principalId, string resource, LockResource lockResource, LockMode mode)
{
    public readonly int PrincipalId = principalId;

    public readonly string Resource = resource;

    public readonly LockResource LockResource = lockResource;

    public readonly LockMode Mode = mode;
}

/// <summary>
/// Shared vocabulary of the application-lock surface (<c>sp_getapplock</c> /
/// <c>sp_releaseapplock</c> / <c>APPLOCK_MODE</c> / <c>APPLOCK_TEST</c>):
/// mode / owner string parsing, mode strength ranking, resource-name
/// normalization. All rules probe-confirmed against SQL Server 2025.
/// </summary>
internal static class AppLock
{
    /// <summary>
    /// The longest resource name that stays distinct: longer names silently
    /// truncate to 255 characters (no error at any probed length up to
    /// 4001), so two names sharing their first 255 characters collide.
    /// </summary>
    private const int MaxResourceLength = 255;

    /// <summary>
    /// Normalizes a resource name: truncation only — names are otherwise
    /// case-sensitive and trailing-space-significant (both probe-confirmed),
    /// so the ledgers and the intern dictionary compare ordinally.
    /// </summary>
    public static string NormalizeResource(string resource) =>
        resource.Length > MaxResourceLength ? resource[..MaxResourceLength] : resource;

    /// <summary>
    /// Parses an application lock-mode string (case-insensitive — probe:
    /// <c>'exclusive'</c> grants and reports <c>Exclusive</c>). Returns false
    /// for anything unrecognized; the caller maps that to -999
    /// (<c>sp_getapplock</c>) or Msg 1225 (<c>APPLOCK_TEST</c>).
    /// </summary>
    public static bool TryParseMode(string text, out LockMode mode)
    {
        if (text.Equals("Shared", StringComparison.OrdinalIgnoreCase))
        {
            mode = LockMode.Shared;
            return true;
        }
        if (text.Equals("Update", StringComparison.OrdinalIgnoreCase))
        {
            mode = LockMode.Update;
            return true;
        }
        if (text.Equals("IntentShared", StringComparison.OrdinalIgnoreCase))
        {
            mode = LockMode.IntentShared;
            return true;
        }
        if (text.Equals("IntentExclusive", StringComparison.OrdinalIgnoreCase))
        {
            mode = LockMode.IntentExclusive;
            return true;
        }
        if (text.Equals("Exclusive", StringComparison.OrdinalIgnoreCase))
        {
            mode = LockMode.Exclusive;
            return true;
        }

        mode = default;
        return false;
    }

    /// <summary>
    /// Parses a lock-owner string (case-insensitive). Returns false for
    /// anything unrecognized; the caller maps that to -999
    /// (<c>sp_getapplock</c> / <c>sp_releaseapplock</c>) or Msg 1226 (the
    /// functions).
    /// </summary>
    public static bool TryParseOwner(string text, out bool isTransaction)
    {
        if (text.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
        {
            isTransaction = true;
            return true;
        }
        if (text.Equals("Session", StringComparison.OrdinalIgnoreCase))
        {
            isTransaction = false;
            return true;
        }

        isTransaction = default;
        return false;
    }

    /// <summary>
    /// Relative strength of the application-lock modes, used to pick which
    /// hold <c>sp_releaseapplock</c> decrements first after a mode
    /// conversion, and which mode <c>APPLOCK_MODE</c> reports when an owner
    /// holds several. Follows SQL Server's lock-strength hierarchy.
    /// </summary>
    public static int ModeStrength(LockMode mode) => mode switch
    {
        LockMode.Exclusive => 5,
        LockMode.Update => 4,
        LockMode.IntentExclusive => 3,
        LockMode.IntentShared => 2,
        _ => 1,
    };

    /// <summary>
    /// The exact strings <c>APPLOCK_MODE</c> returns per held mode
    /// (probe-confirmed casing); <c>NoLock</c> when nothing is held.
    /// </summary>
    public static string ModeDisplayName(LockMode mode) => mode switch
    {
        LockMode.Shared => "Shared",
        LockMode.Update => "Update",
        LockMode.IntentShared => "IntentShared",
        LockMode.IntentExclusive => "IntentExclusive",
        LockMode.Exclusive => "Exclusive",
        _ => "NoLock",
    };
}
