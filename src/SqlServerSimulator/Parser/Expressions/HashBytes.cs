using System.Security.Cryptography;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>HASHBYTES(algorithm, input)</c>: computes a cryptographic hash of
/// the input, returned as <c>varbinary(8000)</c>. Probe-confirmed against
/// SQL Server 2025 (2026-07-21):
/// <list type="bullet">
/// <item>Recognized algorithms (case-insensitive): <c>MD5</c>, <c>MD4</c>,
/// <c>SHA</c> / <c>SHA1</c> (identical output), <c>SHA2_256</c>,
/// <c>SHA2_512</c>. Any other name — including the removed <c>MD2</c> and an
/// unknown string — yields a NULL result rather than an error.</item>
/// <item>The input must be a character or binary type. An untyped / integer /
/// numeric argument raises Msg 8116 (the input arg's data-type word, "hashbytes"
/// function). A typed-but-NULL input yields a NULL result.</item>
/// <item>Input bytes: <c>char</c>/<c>varchar</c>/<c>text</c> encode as CP1252,
/// <c>nchar</c>/<c>nvarchar</c>/<c>ntext</c> as UTF-16LE, binary types verbatim
/// (probe-confirmed <c>HASHBYTES('SHA2_256','x')</c> == <c>HASHBYTES('SHA2_256',0x78)</c>).</item>
/// </list>
/// MD4 isn't in the .NET BCL, so it's hand-rolled here (RFC 1320) — the only
/// algorithm the framework doesn't provide.
/// </summary>
internal sealed class HashBytes : Expression
{
    private static readonly VarbinarySqlType ResultType = VarbinarySqlType.Get(8000);

    private readonly Expression algorithmArg;
    private readonly Expression inputArg;

    public HashBytes(ParserContext context)
    {
        this.algorithmArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.inputArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var input = this.inputArg.Run(runtime);
        // Type validation precedes the null-value check: real rejects a
        // non-character / non-binary argument (int, numeric, untyped NULL)
        // with Msg 8116 regardless of value, but a typed-NULL string / binary
        // yields a NULL hash.
        if (!TryExtractBytes(input, out var inputBytes))
            throw SimulatedSqlException.InvalidArgumentDataType(input.Type.ToString()!, 2, "hashbytes");

        var algorithm = this.algorithmArg.Run(runtime);
        if (algorithm.IsNull || input.IsNull)
            return SqlValue.Null(ResultType);

        var hash = Compute(algorithm.AsString, inputBytes!);
        return hash is null
            ? SqlValue.Null(ResultType)
            : SqlValue.FromVarbinary(ResultType, hash);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => $"HASHBYTES({this.algorithmArg.DebugDisplay()}, {this.inputArg.DebugDisplay()})";

    /// <summary>
    /// Extracts the hashing input bytes, returning false when the argument's
    /// type is neither character nor binary (the Msg 8116 path). NULL-valued
    /// character / binary arguments return true with a null byte array — the
    /// caller maps that to a NULL result.
    /// </summary>
    private static bool TryExtractBytes(SqlValue value, out byte[]? bytes)
    {
        switch (value.Type)
        {
            case VarbinarySqlType or BinarySqlType or ImageSqlType:
                bytes = value.IsNull ? null : value.AsBytes;
                return true;
            case NVarcharSqlType or NCharSqlType or NTextSqlType or SystemNameSqlType:
                bytes = value.IsNull ? null : System.Text.Encoding.Unicode.GetBytes(value.AsString);
                return true;
            case VarcharSqlType or CharSqlType or TextSqlType:
                // The collation's own code page, so hashing a Turkish column
                // digests its CP1254 bytes (probe-confirmed real: equal to
                // HASHBYTES over the same bytes as a varbinary literal).
                bytes = value.IsNull ? null : (value.Type.Collation ?? Collation.Baseline).StorageEncoding.GetBytes(value.AsString);
                return true;
            default:
                bytes = null;
                return false;
        }
    }

    /// <summary>
    /// Runs the named algorithm, or returns null for an unrecognized /
    /// removed algorithm (real yields a NULL result there, not an error).
    /// MD5 / SHA1 are deliberately computed for fidelity with SQL Server's
    /// still-supported HASHBYTES surface — the broken/weak-crypto analyzers
    /// (CA5350 / CA5351) are suppressed here for that reason.
    /// </summary>
#pragma warning disable CA5350, CA5351
    private static byte[]? Compute(string algorithm, byte[] input)
    {
        Span<char> upper = stackalloc char[algorithm.Length];
        var written = algorithm.AsSpan().ToUpperInvariant(upper);
        return written < 0 ? null : upper[..written] switch
        {
            "MD5" => MD5.HashData(input),
            "MD4" => Md4(input),
            "SHA" or "SHA1" => SHA1.HashData(input),
            "SHA2_256" => SHA256.HashData(input),
            "SHA2_512" => SHA512.HashData(input),
            _ => null,
        };
    }
#pragma warning restore CA5350, CA5351

    /// <summary>
    /// RFC 1320 MD4. Present only because the .NET BCL doesn't ship MD4 and
    /// SQL Server still computes it; every other supported algorithm routes to
    /// the framework's implementation. The 48 steps are written out verbatim
    /// (three rounds of 16) to keep the operand rotation unambiguous.
    /// </summary>
    private static byte[] Md4(byte[] message)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;

        var padded = new byte[(((message.Length + 8) / 64) + 1) * 64];
        Array.Copy(message, padded, message.Length);
        padded[message.Length] = 0x80;
        var bitLength = (ulong)message.Length * 8;
        for (var i = 0; i < 8; i++)
            padded[padded.Length - 8 + i] = (byte)(bitLength >> (8 * i));

        static uint Rol(uint v, int s) => (v << s) | (v >> (32 - s));

        var x = new uint[16];
        for (var chunk = 0; chunk < padded.Length; chunk += 64)
        {
            for (var i = 0; i < 16; i++)
                x[i] = BitConverter.ToUInt32(padded, chunk + (i * 4));

            var (aa, bb, cc, dd) = (a, b, c, d);

            uint Ff(uint p, uint q, uint r, uint t, int k, int s) => Rol(p + ((q & r) | (~q & t)) + x[k], s);
            uint Gg(uint p, uint q, uint r, uint t, int k, int s) => Rol(p + ((q & r) | (q & t) | (r & t)) + x[k] + 0x5a827999u, s);
            uint Hh(uint p, uint q, uint r, uint t, int k, int s) => Rol(p + (q ^ r ^ t) + x[k] + 0x6ed9eba1u, s);

            a = Ff(a, b, c, d, 0, 3); d = Ff(d, a, b, c, 1, 7); c = Ff(c, d, a, b, 2, 11); b = Ff(b, c, d, a, 3, 19);
            a = Ff(a, b, c, d, 4, 3); d = Ff(d, a, b, c, 5, 7); c = Ff(c, d, a, b, 6, 11); b = Ff(b, c, d, a, 7, 19);
            a = Ff(a, b, c, d, 8, 3); d = Ff(d, a, b, c, 9, 7); c = Ff(c, d, a, b, 10, 11); b = Ff(b, c, d, a, 11, 19);
            a = Ff(a, b, c, d, 12, 3); d = Ff(d, a, b, c, 13, 7); c = Ff(c, d, a, b, 14, 11); b = Ff(b, c, d, a, 15, 19);

            a = Gg(a, b, c, d, 0, 3); d = Gg(d, a, b, c, 4, 5); c = Gg(c, d, a, b, 8, 9); b = Gg(b, c, d, a, 12, 13);
            a = Gg(a, b, c, d, 1, 3); d = Gg(d, a, b, c, 5, 5); c = Gg(c, d, a, b, 9, 9); b = Gg(b, c, d, a, 13, 13);
            a = Gg(a, b, c, d, 2, 3); d = Gg(d, a, b, c, 6, 5); c = Gg(c, d, a, b, 10, 9); b = Gg(b, c, d, a, 14, 13);
            a = Gg(a, b, c, d, 3, 3); d = Gg(d, a, b, c, 7, 5); c = Gg(c, d, a, b, 11, 9); b = Gg(b, c, d, a, 15, 13);

            a = Hh(a, b, c, d, 0, 3); d = Hh(d, a, b, c, 8, 9); c = Hh(c, d, a, b, 4, 11); b = Hh(b, c, d, a, 12, 15);
            a = Hh(a, b, c, d, 2, 3); d = Hh(d, a, b, c, 10, 9); c = Hh(c, d, a, b, 6, 11); b = Hh(b, c, d, a, 14, 15);
            a = Hh(a, b, c, d, 1, 3); d = Hh(d, a, b, c, 9, 9); c = Hh(c, d, a, b, 5, 11); b = Hh(b, c, d, a, 13, 15);
            a = Hh(a, b, c, d, 3, 3); d = Hh(d, a, b, c, 11, 9); c = Hh(c, d, a, b, 7, 11); b = Hh(b, c, d, a, 15, 15);

            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        var result = new byte[16];
        _ = BitConverter.TryWriteBytes(result.AsSpan(0), a);
        _ = BitConverter.TryWriteBytes(result.AsSpan(4), b);
        _ = BitConverter.TryWriteBytes(result.AsSpan(8), c);
        _ = BitConverter.TryWriteBytes(result.AsSpan(12), d);
        return result;
    }
}
