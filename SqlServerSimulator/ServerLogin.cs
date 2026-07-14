namespace SqlServerSimulator;

/// <summary>
/// One SQL-authentication server login created by <c>CREATE LOGIN name WITH
/// PASSWORD = '…'</c>, held in <see cref="Simulation.Logins"/>. The password
/// is stored only as a PWDCOMPARE-verifiable hash (never clear text),
/// verified by the TDS endpoint at LOGIN7 time. Instances are immutable;
/// <c>ALTER LOGIN … WITH PASSWORD</c> swaps in a replacement entry.
/// </summary>
internal sealed class ServerLogin(string name, byte[] passwordHash, DateTime createDate, DateTime passwordLastSetTime)
{
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
}
