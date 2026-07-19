using System.Security.Cryptography;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared password-hash format for <see cref="PwdEncrypt"/> /
/// <see cref="PwdCompare"/>. Reproduces SQL Server's real on-disk hash layout
/// so simulator-generated hashes verify against a live server and vice versa
/// (both directions probe-confirmed against SQL Server 2025, 2026-07-10).
/// </summary>
/// <remarks>
/// Layout: a 2-byte big-endian version tag, a 4-byte random salt, then the
/// derived key. SQL Server 2025 emits version <c>0x0300</c>:
/// <c>PBKDF2-HMAC-SHA512(UTF-16LE(password), salt, 100000 iterations, 64
/// bytes)</c> — 70 bytes total. Legacy <c>0x0200</c> (SQL Server 2012–2022)
/// is a single-pass <c>SHA-512(UTF-16LE(password) || salt)</c>. PWDCOMPARE
/// recognizes both so it can verify hashes produced by any of those engines;
/// PWDENCRYPT always emits the current <c>0x0300</c> form.
/// </remarks>
internal static class PasswordHash
{
    /// <summary>
    /// SQL Server's password-length cap, shared by every consumer of this
    /// machinery: <c>PWDENCRYPT</c> rejects a longer input with Msg 6607
    /// (probe-confirmed at exactly 128/129), <c>CREATE LOGIN</c> documents
    /// the same cap, and SqlClient refuses to put a longer password in a
    /// connection string. Bounding here is what lets the hashing paths
    /// stackalloc unconditionally.
    /// </summary>
    public const int MaxClearTextChars = 128;

    private const int SaltLength = 4;

    private const int Pbkdf2Iterations = 100_000;

    private const int DerivedKeyLength = 64;

    /// <summary>Callers gate on <see cref="MaxClearTextChars"/> with the appropriate SQL error before reaching the hash paths.</summary>
    private static void ThrowIfOversized(string clearText) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clearText.Length, MaxClearTextChars, nameof(clearText));

    public static byte[] Encrypt(string clearText)
    {
        ThrowIfOversized(clearText);
        var hash = new byte[2 + SaltLength + DerivedKeyLength];
        hash[0] = 0x03;
        RandomNumberGenerator.Fill(hash.AsSpan(2, SaltLength));
        Span<byte> clearBytes = stackalloc byte[clearText.Length * 2];
        _ = Encoding.Unicode.GetBytes(clearText, clearBytes);
        Rfc2898DeriveBytes.Pbkdf2(clearBytes, hash.AsSpan(2, SaltLength), hash.AsSpan(2 + SaltLength), Pbkdf2Iterations, HashAlgorithmName.SHA512);
        return hash;
    }

    /// <summary>
    /// Hashes in the legacy <c>0x0200</c> single-pass-SHA-512 format —
    /// <see cref="Verify"/> dispatches on the version tag, so it verifies
    /// interchangeably with <see cref="Encrypt"/>'s output. This is the form
    /// the <c>CREATE LOGIN</c> registry stores: those hashes only ever live
    /// in a <see cref="Simulation"/>'s memory, where PBKDF2's 100k-iteration
    /// brute-force hardening buys nothing but a per-connection-open cost at
    /// the TDS endpoint.
    /// </summary>
    public static byte[] EncryptLegacy(string clearText)
    {
        ThrowIfOversized(clearText);
        var hash = new byte[2 + SaltLength + DerivedKeyLength];
        hash[0] = 0x02;
        RandomNumberGenerator.Fill(hash.AsSpan(2, SaltLength));
        var byteCount = clearText.Length * 2;
        Span<byte> buffer = stackalloc byte[byteCount + SaltLength];
        _ = Encoding.Unicode.GetBytes(clearText, buffer);
        hash.AsSpan(2, SaltLength).CopyTo(buffer[byteCount..]);
        _ = SHA512.HashData(buffer, hash.AsSpan(2 + SaltLength));
        return hash;
    }

    /// <summary>
    /// Recomputes the stored hash's derived key from <paramref name="clearText"/>
    /// and compares. Returns <c>false</c> for a hash that's too short, carries
    /// an unrecognized version tag (matches real SQL Server returning 0 for a
    /// garbage/short varbinary — probe-confirmed), or has a key section that
    /// isn't the expected 64 bytes. A clear text over
    /// <see cref="MaxClearTextChars"/> is <c>false</c> without hashing: no
    /// genuine hash of one exists (the encrypt paths cap at 128), and real
    /// PWDCOMPARE compares an oversized clear in full rather than truncating —
    /// probe-confirmed 0 for a 129-char clear against its own 128-char
    /// prefix's hash.
    /// </summary>
    public static bool Verify(string clearText, byte[] hash)
    {
        if (clearText.Length > MaxClearTextChars || hash.Length < 2 + SaltLength)
            return false;
        var version = (hash[0] << 8) | hash[1];
        var salt = hash.AsSpan(2, SaltLength);
        var stored = hash.AsSpan(2 + SaltLength);
        var byteCount = clearText.Length * 2;
        Span<byte> recomputed = stackalloc byte[DerivedKeyLength];
        switch (version)
        {
            case 0x0200:
                Span<byte> buffer = stackalloc byte[byteCount + SaltLength];
                _ = Encoding.Unicode.GetBytes(clearText, buffer);
                salt.CopyTo(buffer[byteCount..]);
                _ = SHA512.HashData(buffer, recomputed);
                break;
            case 0x0300:
                Span<byte> clearBytes = stackalloc byte[byteCount];
                _ = Encoding.Unicode.GetBytes(clearText, clearBytes);
                Rfc2898DeriveBytes.Pbkdf2(clearBytes, salt, recomputed, Pbkdf2Iterations, HashAlgorithmName.SHA512);
                break;
            default:
                return false;
        }
        return CryptographicOperations.FixedTimeEquals(recomputed, stored);
    }
}

/// <summary>
/// SQL <c>PWDENCRYPT(clear)</c>: hashes a clear-text password with a fresh
/// random salt and returns the <c>varbinary</c> hash (70 bytes for the
/// current <c>0x0300</c> format — probe-confirmed <c>DATALENGTH</c> 70).
/// NULL input → NULL; an input over 128 characters raises Msg 6607
/// (probe-confirmed at exactly the 128/129 boundary for both varchar and
/// nvarchar). Successive calls with the same password produce different
/// bytes (random salt). The undocumented sibling of the <c>PWDCOMPARE</c>
/// verifier.
/// </summary>
internal sealed class PwdEncrypt : Expression
{
    private readonly Expression clearArg;

    public PwdEncrypt(ParserContext context)
    {
        this.clearArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var clear = this.clearArg.Run(runtime);
        if (clear.IsNull)
            return SqlValue.Null(SqlType.Varbinary);
        var clearText = clear.CoerceTo(SqlType.NVarchar).AsString;
        return clearText.Length > PasswordHash.MaxClearTextChars
            ? throw SimulatedSqlException.PasswordEncryptionInvalidValue()
            : SqlValue.FromVarbinary(PasswordHash.Encrypt(clearText));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Varbinary;

    internal override string DebugDisplay() => $"PWDENCRYPT({this.clearArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>PWDCOMPARE(clear, hash [, version])</c>: returns <c>1</c> when
/// <c>clear</c> hashes to <c>hash</c>, else <c>0</c>; NULL <c>clear</c> or NULL
/// <c>hash</c> → NULL (probe-confirmed). A short / malformed / unrecognized-
/// version hash → 0. A clear text over 128 characters → 0 without hashing:
/// real compares it in full rather than truncating (probe-confirmed 0 for a
/// 129-char clear against its own 128-char prefix's hash), and no genuine
/// hash of one can exist. (Real's parameter plumbing raises Msg 8152 for
/// much larger clears — an unmodeled internal coercion boundary; the
/// simulator returns 0 there.) The optional third <c>version</c> argument
/// (legacy "upgrade hint") is accepted and ignored — real SQL Server ignores
/// it for comparison too (probe-confirmed: both <c>0</c> and <c>1</c> yield
/// the same result). Result type is <c>int</c>.
/// </summary>
internal sealed class PwdCompare : Expression
{
    private readonly Expression clearArg;
    private readonly Expression hashArg;

    public PwdCompare(ParserContext context)
    {
        this.clearArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("PWDCOMPARE", 2);
        this.hashArg = Parse(context.MoveNextRequiredReturnSelf());
        // Optional third argument (version hint) — parse and discard.
        if (context.Token is Tokens.Operator { Character: ',' })
            _ = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var clear = this.clearArg.Run(runtime);
        var hash = this.hashArg.Run(runtime);
        if (clear.IsNull || hash.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var match = PasswordHash.Verify(clear.CoerceTo(SqlType.NVarchar).AsString, hash.CoerceTo(SqlType.Varbinary).AsBytes);
        return SqlValue.FromInt32(match ? 1 : 0);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"PWDCOMPARE({this.clearArg.DebugDisplay()}, {this.hashArg.DebugDisplay()})";
}
