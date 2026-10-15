using System.Security.Cryptography;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>The built-ins <see cref="CryptographicFunction"/> answers.</summary>
internal enum CryptographicFunctionKind : byte
{
    KeyId,
    KeyGuid,
    KeyName,
    CertId,
    AsymKeyId,
    CertProperty,
    AsymKeyProperty,
    SymKeyProperty,
    EncryptByKey,
    DecryptByKey,
    DecryptByKeyAutoCert,
    DecryptByKeyAutoAsymKey,
    EncryptByCert,
    DecryptByCert,
    EncryptByAsymKey,
    DecryptByAsymKey,
    SignByCert,
    SignByAsymKey,
    VerifySignedByCert,
    VerifySignedByAsymKey,
    IsObjectSigned,
    EncryptByPassPhrase,
    DecryptByPassPhrase,
}

/// <summary>
/// The SQL Server 2025 cryptographic built-ins: the key and certificate
/// lookups, the <c>ENCRYPTBY…</c> / <c>DECRYPTBY…</c> / <c>SIGNBY…</c> /
/// <c>VERIFYSIGNEDBY…</c> families and <c>IS_OBJECTSIGNED</c>, which answer
/// as real does when the key or certificate they name doesn't exist — the
/// simulator builds no key store, so none ever does — and
/// <c>ENCRYPTBYPASSPHRASE</c> / <c>DECRYPTBYPASSPHRASE</c>, which need none
/// and are byte-compatible with real's (see <see cref="PassPhraseCipher"/>).
/// </summary>
/// <remarks>
/// Each argument position takes one family of types (<see cref="Slot"/>),
/// refused with Msg 8116 while the batch compiles, a bare <c>NULL</c> included
/// where the slot needs a value; a string or binary argument past 8000 bytes
/// is Msg 8152 state 10 as it runs (all probed 2026-10-15 against SQL Server
/// 2025).
/// </remarks>
internal sealed class CryptographicFunction : Expression
{
    /// <summary>The types an argument position takes.</summary>
    private enum Slot : byte
    {
        /// <summary>A key or certificate name, a property name: a character string.</summary>
        Name,

        /// <summary>A certificate or key id: <c>int</c> alone.</summary>
        Id,

        /// <summary>An <c>add_authenticator</c> / <c>has_authenticator</c> flag: <c>int</c> alone, a bare <c>NULL</c> allowed.</summary>
        Flag,

        /// <summary>Clear text, cipher text or a passphrase: a character or binary string.</summary>
        Data,

        /// <summary>An authenticator: a character or binary string, a bare <c>NULL</c> allowed.</summary>
        Authenticator,

        /// <summary>A certificate or key password: a Unicode string, a bare <c>NULL</c> allowed.</summary>
        Password,

        /// <summary>A key GUID: <c>uniqueidentifier</c> alone.</summary>
        KeyGuid,

        /// <summary><c>KEY_NAME</c>'s cipher text or key GUID: <c>varbinary</c> or <c>uniqueidentifier</c>.</summary>
        CipherOrGuid,

        /// <summary>A signature: <c>binary</c> or <c>varbinary</c>.</summary>
        Signature,

        /// <summary><c>IS_OBJECTSIGNED</c>'s thumbprint: <c>varbinary</c> alone.</summary>
        Thumbprint,
    }

    private readonly struct Signature(string name, int minimum, Slot[] slots)
    {
        /// <summary>The spelling real's argument errors use.</summary>
        public readonly string Name = name;

        public readonly int Minimum = minimum;

        public readonly Slot[] Slots = slots;
    }

    /// <summary>One per <see cref="CryptographicFunctionKind"/>, in its order.</summary>
    private static readonly Signature[] Signatures =
    [
        new("Key_Id", 1, [Slot.Name]),
        new("Key_Guid", 1, [Slot.Name]),
        new("Key_Name", 1, [Slot.CipherOrGuid]),
        new("Cert_Id", 1, [Slot.Name]),
        new("AsymKey_Id", 1, [Slot.Name]),
        new("CertProperty", 2, [Slot.Id, Slot.Name]),
        new("AsymKeyProperty", 2, [Slot.Id, Slot.Name]),
        new("SymKeyProperty", 2, [Slot.Id, Slot.Name]),
        new("EncryptByKey", 2, [Slot.KeyGuid, Slot.Data, Slot.Flag, Slot.Authenticator]),
        new("DecryptByKey", 1, [Slot.Data, Slot.Flag, Slot.Authenticator]),
        new("DecryptByKeyAutoCert", 3, [Slot.Id, Slot.Password, Slot.Data, Slot.Flag, Slot.Authenticator]),
        new("DecryptByKeyAutoAsymKey", 3, [Slot.Id, Slot.Password, Slot.Data, Slot.Flag, Slot.Authenticator]),
        new("EncryptByCert", 2, [Slot.Id, Slot.Data]),
        new("DecryptByCert", 2, [Slot.Id, Slot.Data, Slot.Password]),
        new("EncryptByAsymKey", 2, [Slot.Id, Slot.Data]),
        new("DecryptByAsymKey", 2, [Slot.Id, Slot.Data, Slot.Password]),
        new("SignByCert", 2, [Slot.Id, Slot.Data, Slot.Password]),
        new("SignByAsymKey", 2, [Slot.Id, Slot.Data, Slot.Password]),
        new("VerifySignedByCert", 3, [Slot.Id, Slot.Data, Slot.Signature]),
        new("VerifySignedByAsymKey", 3, [Slot.Id, Slot.Data, Slot.Signature]),
        new("is_objectsigned", 4, [Slot.Name, Slot.Id, Slot.Name, Slot.Thumbprint]),
        new("EncryptByPassPhrase", 2, [Slot.Data, Slot.Data, Slot.Flag, Slot.Authenticator]),
        new("DecryptByPassPhrase", 2, [Slot.Data, Slot.Data, Slot.Flag, Slot.Authenticator]),
    ];

    private static readonly VarbinarySqlType BinaryResult = VarbinarySqlType.Get(8000);

    private readonly CryptographicFunctionKind kind;
    private readonly Expression[] arguments;

    public CryptographicFunction(ParserContext context, CryptographicFunctionKind kind)
    {
        this.kind = kind;
        var signature = Signatures[(int)kind];
        var parsed = new List<Expression>(signature.Slots.Length);
        if (context.Token is not Tokens.Operator { Character: ')' })
        {
            parsed.Add(Parse(context));
            while (context.Token is Tokens.Operator { Character: ',' })
                parsed.Add(Parse(context.MoveNextRequiredReturnSelf()));
            if (context.Token is not Tokens.Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (parsed.Count < signature.Minimum || parsed.Count > signature.Slots.Length)
        {
            throw signature.Minimum == signature.Slots.Length
                ? SimulatedSqlException.FunctionRequiresNArguments(signature.Name, signature.Minimum)
                : SimulatedSqlException.FunctionArgumentCountRange(signature.Name, signature.Minimum, signature.Slots.Length);
        }
        for (var i = 0; i < parsed.Count; i++)
        {
            if (signature.Slots[i] is not (Slot.Flag or Slot.Authenticator or Slot.Password) && IsUntypedNullLiteral(parsed[i]))
                throw SimulatedSqlException.InvalidArgumentDataType("NULL", i + 1, signature.Name);
        }
        this.arguments = [.. parsed];
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var signature = Signatures[(int)this.kind];
        for (var i = 0; i < this.arguments.Length; i++)
        {
            var argument = this.arguments[i];
            if (IsUntypedNullLiteral(argument))
                continue;
            var type = argument.GetSqlType(batch, resolveColumnType);
            if (!Accepts(signature.Slots[i], type))
                throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(type, argument), i + 1, signature.Name);
        }
        return this.kind switch
        {
            CryptographicFunctionKind.KeyId or CryptographicFunctionKind.CertId or CryptographicFunctionKind.AsymKeyId
                or CryptographicFunctionKind.VerifySignedByCert or CryptographicFunctionKind.VerifySignedByAsymKey
                or CryptographicFunctionKind.IsObjectSigned => SqlType.Int32,
            CryptographicFunctionKind.KeyGuid => SqlType.UniqueIdentifier,
            CryptographicFunctionKind.KeyName => KeyNameType(batch),
            CryptographicFunctionKind.CertProperty or CryptographicFunctionKind.AsymKeyProperty
                or CryptographicFunctionKind.SymKeyProperty => SqlType.SqlVariant,
            _ => BinaryResult,
        };
    }

    private static NVarcharSqlType KeyNameType(BatchContext batch) =>
        NVarcharSqlType.Get(4000, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    private static bool Accepts(Slot slot, SqlType type) => slot switch
    {
        Slot.Name => type is CharSqlType or VarcharSqlType or NCharSqlType or NVarcharSqlType or SystemNameSqlType,
        Slot.Id or Slot.Flag => type is Int32SqlType,
        Slot.Data or Slot.Authenticator => type is CharSqlType or VarcharSqlType or NCharSqlType or NVarcharSqlType or SystemNameSqlType or BinarySqlType or VarbinarySqlType,
        Slot.Password => type is NCharSqlType or NVarcharSqlType or SystemNameSqlType,
        Slot.KeyGuid => type is UniqueIdentifierSqlType,
        Slot.CipherOrGuid => type is VarbinarySqlType or UniqueIdentifierSqlType,
        Slot.Signature => type is BinarySqlType or VarbinarySqlType,
        Slot.Thumbprint => type is VarbinarySqlType,
        _ => false,
    };

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Every argument is evaluated, and a string or binary one converted to
        // its 8000-byte form, whether or not the key it names exists.
        var values = new SqlValue[this.arguments.Length];
        var bytes = new byte[]?[this.arguments.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = this.arguments[i].Run(runtime);
            bytes[i] = ArgumentBytes(values[i]);
            if (bytes[i] is { Length: > 8000 })
                throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy(10);
        }

        switch (this.kind)
        {
            case CryptographicFunctionKind.KeyId or CryptographicFunctionKind.CertId or CryptographicFunctionKind.AsymKeyId
                or CryptographicFunctionKind.IsObjectSigned:
                return SqlValue.Null(SqlType.Int32);
            case CryptographicFunctionKind.KeyGuid:
                return SqlValue.Null(SqlType.UniqueIdentifier);
            case CryptographicFunctionKind.KeyName:
                return SqlValue.Null(KeyNameType(runtime.Batch));
            case CryptographicFunctionKind.CertProperty or CryptographicFunctionKind.AsymKeyProperty
                or CryptographicFunctionKind.SymKeyProperty:
                return SqlValue.Null(SqlType.SqlVariant);
            case CryptographicFunctionKind.VerifySignedByCert or CryptographicFunctionKind.VerifySignedByAsymKey:
                // A signature checked against a key that doesn't exist fails,
                // unless there is no key id to look up at all.
                return values[0].IsNull ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(0);
            case CryptographicFunctionKind.EncryptByPassPhrase or CryptographicFunctionKind.DecryptByPassPhrase:
                {
                    // A non-Unicode passphrase that fits 8000 bytes can still
                    // overflow nvarchar(4000) (probed 2026-10-15).
                    var passphrase = PassPhraseBytes(values[0]);
                    if (passphrase is { Length: > 8000 })
                        throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy(4);
                    if (passphrase is null || bytes[1] is not { } input)
                        return SqlValue.Null(BinaryResult);
                    var authenticate = values.Length > 2 && !values[2].IsNull && values[2].AsInt32 == 1;
                    var authenticator = values.Length > 3 ? bytes[3] : null;
                    var result = this.kind == CryptographicFunctionKind.EncryptByPassPhrase
                        ? PassPhraseCipher.Encrypt(passphrase, input, authenticate, authenticator)
                        : PassPhraseCipher.Decrypt(passphrase, input, authenticate, authenticator);
                    return result is null ? SqlValue.Null(BinaryResult) : SqlValue.FromVarbinary(BinaryResult, result);
                }
            default:
                return SqlValue.Null(BinaryResult);
        }
    }

    /// <summary>
    /// A string or binary argument's bytes as the function reads them: a
    /// binary value's own, a Unicode string's UTF-16LE and a non-Unicode one's
    /// in its collation's code page. Null for a NULL value or a type that
    /// isn't a string.
    /// </summary>
    private static byte[]? ArgumentBytes(SqlValue value) => value.IsNull ? null : value.Type switch
    {
        BinarySqlType or VarbinarySqlType => value.AsBytes,
        NCharSqlType or NVarcharSqlType or SystemNameSqlType => SystemNameSqlType.Utf16LeBytes(value.AsString),
        CharSqlType or VarcharSqlType => (value.Type.Collation ?? Collation.Baseline).StorageEncoding.GetBytes(value.AsString),
        _ => null,
    };

    /// <summary>
    /// A passphrase as real reads it, converted to <c>nvarchar</c>: a
    /// character one as UTF-16LE whatever its type, a binary one as UTF-16
    /// code units, an odd last byte padded with a zero (probed 2026-10-15
    /// against SQL Server 2025).
    /// </summary>
    private static byte[]? PassPhraseBytes(SqlValue value) => value.IsNull ? null : value.Type switch
    {
        BinarySqlType or VarbinarySqlType => value.AsBytes.Length % 2 == 0 ? value.AsBytes : [.. value.AsBytes, 0],
        _ => SystemNameSqlType.Utf16LeBytes(value.AsString),
    };

    internal override string DebugDisplay() =>
        $"{Signatures[(int)this.kind].Name.ToUpperInvariant()}({string.Join(", ", this.arguments.Select(static argument => argument.DebugDisplay()))})";

    internal override void Describe(NodeShape shape)
    {
        _ = shape.Local((int)this.kind);
        foreach (var argument in this.arguments)
            _ = shape.Child(argument);
    }
}

/// <summary>
/// The version-2 format <c>ENCRYPTBYPASSPHRASE</c> writes since SQL Server
/// 2017, recovered from real's output (probed 2026-10-15 against SQL Server
/// 2025): the version <c>0x02000000</c>, a random 16-byte IV, then
/// AES-256-CBC with PKCS #7 padding under the SHA-256 of the passphrase
/// converted to <c>nvarchar</c>. The plaintext is the magic <c>0xBAADF00D</c> (little-endian),
/// a 16-bit authenticator field — 0 for none, 20 when the SHA-1 of the data
/// followed by the authenticator's bytes comes next, <c>0xFFFF</c> for an
/// authenticator asked for but NULL — the data's 16-bit length, the hash and
/// the data.
/// </summary>
internal static class PassPhraseCipher
{
    private const int Magic = unchecked((int)0xBAADF00D);

    /// <summary>The cipher text, or null when it wouldn't fit <c>varbinary(8000)</c>, as real answers past 7,959 bytes of data without an authenticator.</summary>
    public static byte[]? Encrypt(byte[] passphrase, byte[] data, bool authenticate, byte[]? authenticator)
    {
        var field = AuthenticatorField(authenticate, authenticator);
        var plain = new byte[8 + (field == 20 ? 20 : 0) + data.Length];
        _ = BitConverter.TryWriteBytes(plain.AsSpan(0, 4), Magic);
        _ = BitConverter.TryWriteBytes(plain.AsSpan(4, 2), field);
        _ = BitConverter.TryWriteBytes(plain.AsSpan(6, 2), (ushort)data.Length);
        if (field == 20)
            Hash(data, authenticator!).CopyTo(plain, 8);
        data.CopyTo(plain, plain.Length - data.Length);

        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(passphrase);
        var iv = RandomNumberGenerator.GetBytes(16);
        var cipher = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
        if (4 + 16 + cipher.Length > 8000)
            return null;
        var result = new byte[4 + 16 + cipher.Length];
        result[0] = 2;
        iv.CopyTo(result, 4);
        cipher.CopyTo(result, 20);
        return result;
    }

    /// <summary>
    /// The data, or null when the passphrase, the authenticator or the cipher
    /// text doesn't match. A version-1 cipher text, the triple-DES format
    /// SQL Server 2016 and earlier wrote, raises <see cref="NotSupportedException"/>.
    /// </summary>
    public static byte[]? Decrypt(byte[] passphrase, byte[] cipher, bool authenticate, byte[]? authenticator)
    {
        if (cipher.Length >= 4 && cipher[0] == 1 && cipher[1] == 0 && cipher[2] == 0 && cipher[3] == 0)
            throw new NotSupportedException("DECRYPTBYPASSPHRASE of a version-1 cipher text, the triple-DES format SQL Server 2016 and earlier wrote, isn't modeled by the simulator.");
        if (cipher.Length < 36 || (cipher.Length - 20) % 16 != 0 || cipher[0] != 2 || cipher[1] != 0 || cipher[2] != 0 || cipher[3] != 0)
            return null;

        byte[] plain;
        using (var aes = Aes.Create())
        {
            aes.Key = SHA256.HashData(passphrase);
            try
            {
                plain = aes.DecryptCbc(cipher.AsSpan(20), cipher.AsSpan(4, 16), PaddingMode.PKCS7);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
        if (plain.Length < 8 || BitConverter.ToInt32(plain, 0) != Magic)
            return null;
        var field = BitConverter.ToUInt16(plain, 4);
        var hashLength = field == 20 ? 20 : 0;
        var length = BitConverter.ToUInt16(plain, 6);
        if (field != AuthenticatorField(authenticate, authenticator) || plain.Length != 8 + hashLength + length)
            return null;
        var data = plain.AsSpan(8 + hashLength).ToArray();
        return hashLength == 0 || plain.AsSpan(8, 20).SequenceEqual(Hash(data, authenticator!)) ? data : null;
    }

    private static ushort AuthenticatorField(bool authenticate, byte[]? authenticator) =>
        !authenticate ? (ushort)0 : authenticator is null ? ushort.MaxValue : (ushort)20;

    /// <summary>SHA-1, which real's format fixes, so the weak-hash analyzer (CA5350) is suppressed here as in HASHBYTES.</summary>
#pragma warning disable CA5350
    private static byte[] Hash(byte[] data, byte[] authenticator) => SHA1.HashData([.. data, .. authenticator]);
#pragma warning restore CA5350
}
