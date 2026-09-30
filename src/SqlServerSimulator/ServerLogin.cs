namespace SqlServerSimulator;

/// <summary>
/// One SQL-authentication server login created by <c>CREATE LOGIN name WITH
/// PASSWORD = '…'</c>, held in <see cref="Simulation.Logins"/>. The password
/// is stored only as a PWDCOMPARE-verifiable hash (never clear text),
/// verified by the TDS endpoint at LOGIN7 time. Instances are immutable;
/// <c>ALTER LOGIN … WITH PASSWORD</c> swaps in a replacement entry.
/// </summary>
internal sealed class ServerLogin(int principalId, string name, byte[] passwordHash, DateTime createDate, DateTime passwordLastSetTime, bool isDisabled = false, string defaultDatabase = "master", string defaultLanguage = "us_english", bool isPolicyChecked = true, bool isExpirationChecked = false)
{
    /// <summary>
    /// Server-level principal id, allocated once at <c>CREATE LOGIN</c> and
    /// preserved across the wholesale replacement <c>ALTER LOGIN</c> performs.
    /// Surfaces as <c>sys.server_principals.principal_id</c> /
    /// <c>sys.sql_logins.principal_id</c>. Ids 1 and 2 are reserved for the
    /// synthetic <c>sa</c> / <c>public</c> rows, so allocation starts at 3.
    /// </summary>
    public readonly int PrincipalId = principalId;

    public readonly string Name = name;

    /// <summary>
    /// Version-tagged hash (tag + salt + 64-byte key). Written in the legacy
    /// <c>0x0200</c> single-pass-SHA-512 form — these hashes never leave the
    /// simulation's memory, so PBKDF2's brute-force hardening would only be
    /// a per-connection-open cost — but verification dispatches on the tag,
    /// so a <c>0x0300</c> PBKDF2 hash would verify too.
    /// </summary>
    public readonly byte[] PasswordHash = passwordHash;

    public readonly DateTime CreateDate = createDate;

    /// <summary>Read back by <c>LOGINPROPERTY(name, 'PasswordLastSetTime')</c>.</summary>
    public readonly DateTime PasswordLastSetTime = passwordLastSetTime;

    /// <summary>
    /// Set by <c>ALTER LOGIN … DISABLE</c> and cleared by <c>… ENABLE</c>:
    /// the login's correct password then fails with Msg 18470 at both front
    /// doors, while <c>EXECUTE AS LOGIN</c> still reaches it (probed 2026-09-29
    /// against SQL Server 2025). Projected as <c>is_disabled</c>.
    /// </summary>
    public readonly bool IsDisabled = isDisabled;

    /// <summary>
    /// The <c>DEFAULT_DATABASE</c> the login was created or altered with, as it was written
    /// (<c>sys.server_principals.default_database_name</c>, <c>LOGINPROPERTY 'DefaultDatabase'</c>).
    /// The session doesn't land there: a connection opens on the database it names, or <c>simulated</c>.
    /// </summary>
    public readonly string DefaultDatabase = defaultDatabase;

    /// <summary>
    /// The <c>DEFAULT_LANGUAGE</c> as written — an official name or an alias, kept as given
    /// (<c>sys.server_principals.default_language_name</c>). It doesn't set the session's language.
    /// </summary>
    public readonly string DefaultLanguage = defaultLanguage;

    /// <summary><c>CHECK_POLICY</c>: <c>sys.sql_logins.is_policy_checked</c>, and whether a new password meets the password policy.</summary>
    public readonly bool IsPolicyChecked = isPolicyChecked;

    /// <summary><c>CHECK_EXPIRATION</c>: <c>sys.sql_logins.is_expiration_checked</c>. Nothing here expires a password.</summary>
    public readonly bool IsExpirationChecked = isExpirationChecked;

    /// <summary>A login like this one with the given fields changed — logins are replaced whole, never mutated.</summary>
    public ServerLogin With(
        byte[]? passwordHash = null, DateTime? passwordLastSetTime = null, bool? isDisabled = null, string? defaultDatabase = null,
        string? defaultLanguage = null, bool? isPolicyChecked = null, bool? isExpirationChecked = null) =>
        new(this.PrincipalId, this.Name, passwordHash ?? this.PasswordHash, this.CreateDate, passwordLastSetTime ?? this.PasswordLastSetTime,
            isDisabled ?? this.IsDisabled, defaultDatabase ?? this.DefaultDatabase, defaultLanguage ?? this.DefaultLanguage,
            isPolicyChecked ?? this.IsPolicyChecked, isExpirationChecked ?? this.IsExpirationChecked);
}
