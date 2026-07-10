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
    private const int SaltLength = 4;

    private const int Pbkdf2Iterations = 100_000;

    private const int DerivedKeyLength = 64;

    public static byte[] Encrypt(string clearText)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.Unicode.GetBytes(clearText), salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, DerivedKeyLength);
        var hash = new byte[2 + SaltLength + DerivedKeyLength];
        hash[0] = 0x03;
        hash[1] = 0x00;
        salt.CopyTo(hash, 2);
        key.CopyTo(hash, 2 + SaltLength);
        return hash;
    }

    /// <summary>
    /// Recomputes the stored hash's derived key from <paramref name="clearText"/>
    /// and compares. Returns <c>false</c> for a hash that's too short or carries
    /// an unrecognized version tag (matches real SQL Server returning 0 for a
    /// garbage/short varbinary — probe-confirmed).
    /// </summary>
    public static bool Verify(string clearText, byte[] hash)
    {
        if (hash.Length < 2 + SaltLength)
            return false;
        var version = (hash[0] << 8) | hash[1];
        var salt = hash[2..(2 + SaltLength)];
        var stored = hash[(2 + SaltLength)..];
        var pwBytes = Encoding.Unicode.GetBytes(clearText);
        var recomputed = version switch
        {
            0x0200 => SHA512.HashData([.. pwBytes, .. salt]),
            0x0300 => Rfc2898DeriveBytes.Pbkdf2(pwBytes, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, DerivedKeyLength),
            _ => null,
        };
        return recomputed is not null && CryptographicOperations.FixedTimeEquals(recomputed, stored);
    }
}

/// <summary>
/// SQL <c>PWDENCRYPT(clear)</c>: hashes a clear-text password with a fresh
/// random salt and returns the <c>varbinary</c> hash (70 bytes for the
/// current <c>0x0300</c> format — probe-confirmed <c>DATALENGTH</c> 70).
/// NULL input → NULL. Successive calls with the same password produce
/// different bytes (random salt). The undocumented sibling of the
/// <c>PWDCOMPARE</c> verifier.
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
        return clear.IsNull
            ? SqlValue.Null(SqlType.Varbinary)
            : SqlValue.FromVarbinary(PasswordHash.Encrypt(clear.CoerceTo(SqlType.NVarchar).AsString));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Varbinary;

    internal override string DebugDisplay() => $"PWDENCRYPT({this.clearArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>PWDCOMPARE(clear, hash [, version])</c>: returns <c>1</c> when
/// <c>clear</c> hashes to <c>hash</c>, else <c>0</c>; NULL <c>clear</c> or NULL
/// <c>hash</c> → NULL (probe-confirmed). A short / malformed / unrecognized-
/// version hash → 0. The optional third <c>version</c> argument (legacy
/// "upgrade hint") is accepted and ignored — real SQL Server ignores it for
/// comparison too (probe-confirmed: both <c>0</c> and <c>1</c> yield the same
/// result). Result type is <c>int</c>.
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
