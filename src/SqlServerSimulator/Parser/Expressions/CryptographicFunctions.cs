using System.Security.Cryptography;
using SqlServerSimulator.Schemas;
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
/// <c>VERIFYSIGNEDBY…</c> families and <c>IS_OBJECTSIGNED</c>. The symmetric
/// key and certificate members work over the database's key store
/// (<see cref="Database.EncryptionKeys"/>); the asymmetric key members and
/// <c>IS_OBJECTSIGNED</c> answer as real does for a key or signature that
/// doesn't exist, since neither can be created yet. <c>ENCRYPTBYKEY</c> and
/// <c>ENCRYPTBYPASSPHRASE</c> write real's version-2 format byte for byte (see
/// <see cref="PassPhraseCipher"/>), so a key created from the same
/// <c>KEY_SOURCE</c>, or the same passphrase, reads real's cipher text.
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

        var batch = runtime.Batch;
        switch (this.kind)
        {
            case CryptographicFunctionKind.KeyId:
                return VisibleKeyByName(batch, values[0]) is { } keyById ? SqlValue.FromInt32(keyById.Id) : SqlValue.Null(SqlType.Int32);
            case CryptographicFunctionKind.KeyGuid:
                return VisibleKeyByName(batch, values[0]) is { } keyByGuid ? SqlValue.FromGuid(keyByGuid.KeyGuid) : SqlValue.Null(SqlType.UniqueIdentifier);
            case CryptographicFunctionKind.KeyName:
                {
                    Guid? guid = values[0].IsNull ? null
                        : values[0].Type is UniqueIdentifierSqlType ? values[0].AsGuid
                        : bytes[0] is { Length: >= 16 } cipher ? new Guid(cipher.AsSpan(0, 16)) : null;
                    var store = batch.CurrentDatabase.EncryptionKeys;
                    return guid is { } keyGuid && store.SymmetricKeyByGuid(keyGuid) is { } named && SeesSymmetricKey(batch, named)
                        ? SqlValue.FromNVarchar(KeyNameType(batch), named.Name)
                        : SqlValue.Null(KeyNameType(batch));
                }
            case CryptographicFunctionKind.CertId:
                return VisibleCertificateByName(batch, values[0]) is { } certificateById ? SqlValue.FromInt32(certificateById.Id) : SqlValue.Null(SqlType.Int32);
            case CryptographicFunctionKind.CertProperty:
                return VisibleCertificateById(batch, values[0]) is { } described && !values[1].IsNull
                    ? CertificateProperty(described, values[1].AsString)
                    : SqlValue.Null(SqlType.SqlVariant);
            case CryptographicFunctionKind.AsymKeyId or CryptographicFunctionKind.IsObjectSigned:
                return SqlValue.Null(SqlType.Int32);
            case CryptographicFunctionKind.AsymKeyProperty or CryptographicFunctionKind.SymKeyProperty:
                // SYMKEYPROPERTY answers only for a key from an extensible key
                // management provider, which none is (probed 2026-10-15).
                return SqlValue.Null(SqlType.SqlVariant);
            case CryptographicFunctionKind.EncryptByKey:
                {
                    if (values[0].IsNull || bytes[1] is not { } data || OpenKeyByGuid(batch.Connection, values[0].AsGuid) is not { } key)
                        return SqlValue.Null(BinaryResult);
                    var body = PassPhraseCipher.Encrypt(key.Material, data, Authenticate(values, 2), AuthenticatorBytes(bytes, 3), prefix: 16);
                    if (body is null)
                        return SqlValue.Null(BinaryResult);
                    _ = key.KeyGuid.TryWriteBytes(body);
                    return SqlValue.FromVarbinary(BinaryResult, body);
                }
            case CryptographicFunctionKind.DecryptByKey:
                {
                    if (bytes[0] is not { Length: >= 16 } cipher || OpenKeyByGuid(batch.Connection, new Guid(cipher.AsSpan(0, 16))) is not { } key)
                        return SqlValue.Null(BinaryResult);
                    return Decrypted(PassPhraseCipher.Decrypt(key.Material, cipher.AsSpan(16), Authenticate(values, 1), AuthenticatorBytes(bytes, 2)));
                }
            case CryptographicFunctionKind.DecryptByKeyAutoCert:
                {
                    if (bytes[2] is not { Length: >= 16 } cipher || VisibleCertificateById(batch, values[0]) is not { } certificate)
                        return SqlValue.Null(BinaryResult);
                    var store = batch.CurrentDatabase.EncryptionKeys;
                    if (store.SymmetricKeyByGuid(new Guid(cipher.AsSpan(0, 16))) is not { } key)
                        return SqlValue.Null(BinaryResult);
                    var opened = batch.CurrentStatement.AutoOpenedKeys;
                    if (opened?.Contains(key) != true)
                    {
                        if (!Simulation.PrivateKeyAvailable(batch.Connection, batch.CurrentDatabase, certificate, values[1].IsNull ? null : values[1].AsString)
                            || !Array.Exists(key.Encryptions, encryption => encryption.Certificate?.Id == certificate.Id))
                        {
                            return SqlValue.Null(BinaryResult);
                        }
                        (batch.CurrentStatement.AutoOpenedKeys ??= []).Add(key);
                    }
                    return Decrypted(PassPhraseCipher.Decrypt(key.Material, cipher.AsSpan(16), Authenticate(values, 3), AuthenticatorBytes(bytes, 4)));
                }
            case CryptographicFunctionKind.EncryptByCert:
                return VisibleCertificateById(batch, values[0]) is { } encrypting && bytes[1] is { } plain
                    ? Decrypted(CertificateCipher.Encrypt(encrypting, plain))
                    : SqlValue.Null(BinaryResult);
            case CryptographicFunctionKind.DecryptByCert:
                return VisibleCertificateById(batch, values[0]) is { } decrypting && bytes[1] is { } encrypted
                    && Simulation.PrivateKeyAvailable(batch.Connection, batch.CurrentDatabase, decrypting, PasswordArgument(values, 2))
                    ? Decrypted(CertificateCipher.Decrypt(decrypting, encrypted))
                    : SqlValue.Null(BinaryResult);
            case CryptographicFunctionKind.SignByCert:
                return VisibleCertificateById(batch, values[0]) is { } signing && bytes[1] is { } signed
                    && Simulation.PrivateKeyAvailable(batch.Connection, batch.CurrentDatabase, signing, PasswordArgument(values, 2))
                    ? SqlValue.FromVarbinary(BinaryResult, CertificateCipher.Sign(signing, signed))
                    : SqlValue.Null(BinaryResult);
            case CryptographicFunctionKind.VerifySignedByCert:
                if (values[0].IsNull)
                    return SqlValue.Null(SqlType.Int32);
                return SqlValue.FromInt32(VisibleCertificateById(batch, values[0]) is { } verifying && bytes[1] is { } verified && bytes[2] is { } signature
                    && CertificateCipher.Verify(verifying, verified, signature) ? 1 : 0);
            case CryptographicFunctionKind.VerifySignedByAsymKey:
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
                    var key = SHA256.HashData(passphrase);
                    var authenticate = Authenticate(values, 2);
                    var authenticator = AuthenticatorBytes(bytes, 3);
                    return Decrypted(this.kind == CryptographicFunctionKind.EncryptByPassPhrase
                        ? PassPhraseCipher.Encrypt(key, input, authenticate, authenticator, prefix: 0)
                        : PassPhraseCipher.DecryptPassPhrase(key, input, authenticate, authenticator));
                }
            default:
                return SqlValue.Null(BinaryResult);
        }
    }

    private static SqlValue Decrypted(byte[]? result) => result is null ? SqlValue.Null(BinaryResult) : SqlValue.FromVarbinary(BinaryResult, result);

    private static bool Authenticate(SqlValue[] values, int index) => values.Length > index && !values[index].IsNull && values[index].AsInt32 == 1;

    private static byte[]? AuthenticatorBytes(byte[]?[] bytes, int index) => bytes.Length > index ? bytes[index] : null;

    private static string? PasswordArgument(SqlValue[] values, int index) => values.Length > index && !values[index].IsNull ? values[index].AsString : null;

    /// <summary>
    /// A key or certificate name as the lookup functions read it: one name,
    /// brackets around it stripped, matched under the database's collation
    /// (probed 2026-10-15 against SQL Server 2025: <c>KEY_ID('[k]')</c> finds
    /// <c>k</c>, <c>KEY_ID('dbo.k')</c> nothing).
    /// </summary>
    private static string? LookupName(SqlValue value)
    {
        if (value.IsNull)
            return null;
        var name = value.AsString;
        return name.Length >= 2 && name[0] == '[' && name[^1] == ']' ? name[1..^1].Replace("]]", "]", StringComparison.Ordinal) : name;
    }

    private static SymmetricKey? VisibleKeyByName(BatchContext batch, SqlValue value) =>
        LookupName(value) is { } name && batch.CurrentDatabase.EncryptionKeys.SymmetricKeys.TryGetValue(name, out var key) && SeesSymmetricKey(batch, key) ? key : null;

    private static bool SeesSymmetricKey(BatchContext batch, SymmetricKey key) =>
        PermissionEnforcement.SeesKey(batch, batch.CurrentDatabase, PermissionChecker.ClassSymmetricKey, key.Id, key.PrincipalId);

    private static Certificate? VisibleCertificateByName(BatchContext batch, SqlValue value) =>
        LookupName(value) is { } name && batch.CurrentDatabase.EncryptionKeys.Certificates.TryGetValue(name, out var certificate)
            && PermissionEnforcement.SeesKey(batch, batch.CurrentDatabase, PermissionChecker.ClassCertificate, certificate.Id, certificate.PrincipalId)
            ? certificate
            : null;

    private static Certificate? VisibleCertificateById(BatchContext batch, SqlValue value) =>
        !value.IsNull && batch.CurrentDatabase.EncryptionKeys.CertificateById(value.AsInt32) is { } certificate
            && PermissionEnforcement.SeesKey(batch, batch.CurrentDatabase, PermissionChecker.ClassCertificate, certificate.Id, certificate.PrincipalId)
            ? certificate
            : null;

    /// <summary>The open symmetric key with <paramref name="keyGuid"/> among the session's, in whichever database it was opened.</summary>
    private static SymmetricKey? OpenKeyByGuid(SimulatedDbConnection connection, Guid keyGuid)
    {
        OpenSymmetricKey[] opened;
        lock (connection.OpenSymmetricKeys)
            opened = [.. connection.OpenSymmetricKeys];
        foreach (var open in opened)
        {
            if (Simulation.OpenedKey(connection, open) is { } key && key.KeyGuid == keyGuid)
                return key;
        }
        return null;
    }

    private static readonly NVarcharSqlType CertificateTextType = NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType CertificateNameType = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);

    /// <summary>
    /// <c>CERTPROPERTY</c>'s properties, named case-insensitively, each a
    /// <c>sql_variant</c> of the type real gives it (probed 2026-10-15 against
    /// SQL Server 2025); any other name, <c>Thumbprint</c> included, is NULL.
    /// </summary>
    private static SqlValue CertificateProperty(Certificate certificate, string property)
    {
        Span<char> folded = stackalloc char[Math.Min(property.Length, 32)];
        _ = property.AsSpan(0, folded.Length).ToUpperInvariant(folded);
        var inner = property.Length > 32 ? SqlValue.Null(SqlType.SqlVariant) : folded switch
        {
            "CERT_SERIAL_NUMBER" => SqlValue.FromNVarchar(CertificateNameType, certificate.SerialNumber),
            "EXPIRY_DATE" => SqlValue.FromDateTime(certificate.ExpiryDate),
            "ISSUER_NAME" or "SUBJECT" => SqlValue.FromNVarchar(CertificateTextType, certificate.Subject),
            "KEY_LENGTH" => SqlValue.FromInt32(Certificate.KeyLength),
            "SID" => SqlValue.FromVarbinary(VarbinarySqlType.Get(85), certificate.Sid),
            "START_DATE" => SqlValue.FromDateTime(certificate.StartDate),
            "STRING_SID" => SqlValue.FromNVarchar(CertificateNameType, certificate.StringSid),
            _ => SqlValue.Null(SqlType.SqlVariant),
        };
        return inner.IsNull ? inner : SqlValue.FromVariant(inner);
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

    /// <summary>
    /// The cipher text under the AES key <paramref name="key"/> — the
    /// passphrase's SHA-256, or a symmetric key's material — after
    /// <paramref name="prefix"/> bytes left for the caller (<c>ENCRYPTBYKEY</c>'s
    /// key GUID), or null when the whole wouldn't fit <c>varbinary(8000)</c>,
    /// as real answers past 7,959 bytes of data under a passphrase without an
    /// authenticator.
    /// </summary>
    public static byte[]? Encrypt(byte[] key, byte[] data, bool authenticate, byte[]? authenticator, int prefix)
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
        aes.Key = key;
        var iv = RandomNumberGenerator.GetBytes(16);
        var cipher = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
        if (prefix + 4 + 16 + cipher.Length > 8000)
            return null;
        var result = new byte[prefix + 4 + 16 + cipher.Length];
        result[prefix] = 2;
        iv.CopyTo(result, prefix + 4);
        cipher.CopyTo(result, prefix + 20);
        return result;
    }

    /// <summary>
    /// <see cref="Decrypt"/> under a passphrase's key, where a version-1
    /// cipher text, the triple-DES format SQL Server 2016 and earlier wrote,
    /// raises <see cref="NotSupportedException"/>.
    /// </summary>
    public static byte[]? DecryptPassPhrase(byte[] key, byte[] cipher, bool authenticate, byte[]? authenticator) =>
        cipher.Length >= 4 && cipher[0] == 1 && cipher[1] == 0 && cipher[2] == 0 && cipher[3] == 0
            ? throw new NotSupportedException("DECRYPTBYPASSPHRASE of a version-1 cipher text, the triple-DES format SQL Server 2016 and earlier wrote, isn't modeled by the simulator.")
            : Decrypt(key, cipher, authenticate, authenticator);

    /// <summary>
    /// The data <paramref name="cipher"/> (from its version field on) holds
    /// under the AES key <paramref name="key"/>, or null when the key, the
    /// authenticator or the cipher text doesn't match.
    /// </summary>
    public static byte[]? Decrypt(byte[] key, ReadOnlySpan<byte> cipher, bool authenticate, byte[]? authenticator)
    {
        if (cipher.Length < 36 || (cipher.Length - 20) % 16 != 0 || cipher[0] != 2 || cipher[1] != 0 || cipher[2] != 0 || cipher[3] != 0)
            return null;

        byte[] plain;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            try
            {
                plain = aes.DecryptCbc(cipher[20..], cipher.Slice(4, 16), PaddingMode.PKCS7);
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

/// <summary>
/// <c>ENCRYPTBYCERT</c> / <c>DECRYPTBYCERT</c> and <c>SIGNBYCERT</c> /
/// <c>VERIFYSIGNEDBYCERT</c> over a certificate's 3072-bit RSA key, in real's
/// shapes (probed 2026-10-15 against SQL Server 2025): OAEP with SHA-256, so
/// at most 318 bytes of data, the 384-byte cipher text in the little-endian
/// order of Windows' CryptoAPI followed by <c>0x00010001</c>; a signature is
/// the eight bytes <c>0x0100070204000000</c> and the 384-byte PKCS #1
/// signature, as deterministic as real's. Each certificate's key is the
/// simulator's own, so these round-trip within it but not against real's.
/// </summary>
internal static class CertificateCipher
{
    private static readonly byte[] Trailer = [0, 1, 0, 1];

    private static readonly byte[] SignatureHeader = [1, 0, 7, 2, 4, 0, 0, 0];

    private const int CipherLength = Certificate.KeyLength / 8;

    public static byte[]? Encrypt(Certificate certificate, byte[] data)
    {
        if (data.Length > CipherLength - 66)
            return null;
        var cipher = certificate.Key.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
        Array.Reverse(cipher);
        return [.. cipher, .. Trailer];
    }

    public static byte[]? Decrypt(Certificate certificate, byte[] cipher)
    {
        if (cipher.Length != CipherLength + Trailer.Length || !cipher.AsSpan(CipherLength).SequenceEqual(Trailer))
            return null;
        var reversed = cipher[..CipherLength];
        Array.Reverse(reversed);
        try
        {
            return certificate.Key.Decrypt(reversed, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static byte[] Sign(Certificate certificate, byte[] data)
    {
        var signature = certificate.Key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Array.Reverse(signature);
        return [.. SignatureHeader, .. signature];
    }

    public static bool Verify(Certificate certificate, byte[] data, byte[] signature)
    {
        if (signature.Length != SignatureHeader.Length + CipherLength || !signature.AsSpan(0, SignatureHeader.Length).SequenceEqual(SignatureHeader))
            return false;
        var reversed = signature[SignatureHeader.Length..];
        Array.Reverse(reversed);
        return certificate.Key.VerifyData(data, reversed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
