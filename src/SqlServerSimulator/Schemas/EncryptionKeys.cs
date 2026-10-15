using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace SqlServerSimulator.Schemas;

/// <summary>What protects a key, as <c>sys.key_encryptions.crypt_type</c> names it.</summary>
internal enum KeyEncryptionKind : byte
{
    /// <summary><c>ESKM</c>: the service master key, the master key's alone.</summary>
    ServiceMasterKey,

    /// <summary><c>ESP2</c>: a password.</summary>
    Password,

    /// <summary><c>C256</c>: a certificate's public key.</summary>
    Certificate,

    /// <summary><c>A256</c>: an asymmetric key's public key.</summary>
    AsymmetricKey,

    /// <summary><c>ESKS</c>: another symmetric key.</summary>
    SymmetricKey,
}

/// <summary>
/// One encryption protecting a key: a <c>sys.key_encryptions</c> row. The key
/// material itself stays in the clear on its key, since nothing outside the
/// simulator reads the encrypted form; <see cref="CryptProperty"/> is random
/// bytes of the length real's encrypted form has.
/// </summary>
internal sealed class KeyEncryption(KeyEncryptionKind kind, string? password, KeyPair? keyPair, SymmetricKeyReference? symmetricKey, byte[] cryptProperty)
{
    public readonly KeyEncryptionKind Kind = kind;

    /// <summary>The password, compared exactly, for <see cref="KeyEncryptionKind.Password"/>.</summary>
    public readonly string? Password = password;

    /// <summary>
    /// The certificate or asymmetric key, as it stood when the encryption was
    /// made: its id, name and thumbprint never change, but its owner and
    /// private key may have since, so ask the store for those.
    /// </summary>
    public readonly KeyPair? KeyPair = keyPair;

    /// <summary>The encrypting symmetric key, by the id, name and GUID it never changes.</summary>
    public readonly SymmetricKeyReference? SymmetricKey = symmetricKey;

    public readonly byte[] CryptProperty = cryptProperty;

    /// <summary>
    /// The encryption of a key of <paramref name="keyBytes"/> bytes by a
    /// password or another symmetric key: real's form is the key padded to the
    /// next whole AES block (probed 2026-10-15 against SQL Server 2025: 32
    /// bytes for a 128- or 192-bit key, 48 for a 256-bit one).
    /// </summary>
    public static byte[] BlockEncryptedLength(int keyBytes) => RandomNumberGenerator.GetBytes(((keyBytes / 16) + 1) * 16);

    /// <summary>Whether this is an encryption by <paramref name="keyPair"/>.</summary>
    public bool IsBy(KeyPair keyPair) => this.KeyPair is { } own && own.IsCertificate == keyPair.IsCertificate && own.Id == keyPair.Id;

    /// <summary>Whether this encryption is the one <paramref name="other"/> describes: the same password, key pair or key.</summary>
    public bool SameProtector(KeyEncryption other) => this.Kind == other.Kind && this.Kind switch
    {
        KeyEncryptionKind.Password => string.Equals(this.Password, other.Password, StringComparison.Ordinal),
        KeyEncryptionKind.Certificate or KeyEncryptionKind.AsymmetricKey => this.IsBy(other.KeyPair!),
        KeyEncryptionKind.SymmetricKey => this.SymmetricKey!.Id == other.SymmetricKey!.Id,
        _ => true,
    };
}

/// <summary>The symmetric key an encryption by one names: the id, name and GUID a key keeps for life.</summary>
internal sealed class SymmetricKeyReference(int id, string name, Guid keyGuid)
{
    public readonly int Id = id;

    public readonly string Name = name;

    public readonly Guid KeyGuid = keyGuid;
}

/// <summary>
/// A database's master key: <c>sys.symmetric_keys</c>' <c>##MS_DatabaseMasterKey##</c>
/// row, id 101, always AES-256. Immutable: <c>ALTER MASTER KEY</c> replaces it.
/// </summary>
internal sealed class DatabaseMasterKey(Guid keyGuid, DateTime createDate, DateTime modifyDate, KeyEncryption[] encryptions)
{
    public const int Id = 101;

    public const string Name = "##MS_DatabaseMasterKey##";

    public readonly Guid KeyGuid = keyGuid;

    public readonly DateTime CreateDate = createDate;

    public readonly DateTime ModifyDate = modifyDate;

    /// <summary>The service master key's encryption, if it has one, then its passwords in the order they were added.</summary>
    public readonly KeyEncryption[] Encryptions = encryptions;

    public bool EncryptedByServiceMasterKey => Array.Exists(this.Encryptions, static encryption => encryption.Kind == KeyEncryptionKind.ServiceMasterKey);

    public DatabaseMasterKey With(KeyEncryption[] encryptions, DateTime modifyDate) => new(this.KeyGuid, this.CreateDate, modifyDate, encryptions);
}

/// <summary>
/// What a certificate and an asymmetric key share: an RSA key pair generated
/// on first use, whose private half a password or the master key protects —
/// or that has been removed. Immutable apart from the lazily made key, which
/// an instance an <c>ALTER</c> makes shares, so a rollback restores one by
/// putting the old instance back.
/// </summary>
internal abstract class KeyPair(int id, string name, int principalId, int keyLength, byte[] thumbprint, string? password, bool hasPrivateKey, StrongBox<RSA?>? rsa)
{
    public readonly int Id = id;

    public readonly string Name = name;

    public readonly int PrincipalId = principalId;

    /// <summary>The key length in bits.</summary>
    public readonly int KeyLength = keyLength;

    /// <summary>The thumbprint, which is also the tail of the SID: 20 bytes for a certificate, 8 for an asymmetric key.</summary>
    public readonly byte[] Thumbprint = thumbprint;

    /// <summary>The password protecting the private key, or null when the master key protects it or there is none.</summary>
    public readonly string? Password = password;

    /// <summary>False once <c>REMOVE PRIVATE KEY</c> has dropped the private half.</summary>
    public readonly bool HasPrivateKey = hasPrivateKey;

    /// <summary>Where the key pair lives once made, shared with the instances an <c>ALTER</c> replaces this one by.</summary>
    protected readonly StrongBox<RSA?> Rsa = rsa ?? new();

    public bool IsCertificate => this is Certificate;

    /// <summary>The key pair if an operation has made it, for a backup to carry; null while none has.</summary>
    public RSA? MadeKey => Volatile.Read(ref this.Rsa.Value);

    /// <summary>The class name real's messages use: <c>certificate</c> or <c>asymmetric key</c>.</summary>
    public string ClassName => this.IsCertificate ? "certificate" : "asymmetric key";

    /// <summary>The key pair, generated when an operation first needs it.</summary>
    public RSA Key
    {
        get
        {
            if (Volatile.Read(ref this.Rsa.Value) is { } existing)
                return existing;
            var made = RSA.Create(this.KeyLength);
            if (Interlocked.CompareExchange(ref this.Rsa.Value, made, null) is not { } winner)
                return made;
            made.Dispose();
            return winner;
        }
    }

    /// <summary><c>pvt_key_encryption_type</c>: <c>MK</c>, <c>PW</c> or <c>NA</c>.</summary>
    public string ProtectionCode => !this.HasPrivateKey ? "NA" : this.Password is null ? "MK" : "PW";

    /// <summary><c>pvt_key_encryption_type_desc</c>.</summary>
    public string ProtectionDescription => !this.HasPrivateKey ? "NO_PRIVATE_KEY" : this.Password is null ? "ENCRYPTED_BY_MASTER_KEY" : "ENCRYPTED_BY_PASSWORD";

    /// <summary>The SID: authority 9, then 1 for a certificate or 2 for an asymmetric key, then the thumbprint's words.</summary>
    public byte[] Sid => [1, (byte)(1 + (this.Thumbprint.Length / 4)), 0, 0, 0, 0, 0, 9, this.IsCertificate ? (byte)1 : (byte)2, 0, 0, 0, .. this.Thumbprint];

    /// <summary>The SID's string form: <c>S-1-9-1-…</c> or <c>S-1-9-2-…</c> over the thumbprint's little-endian words.</summary>
    public string StringSid
    {
        get
        {
            var parts = new string[this.Thumbprint.Length / 4];
            for (var i = 0; i < parts.Length; i++)
                parts[i] = BitConverter.ToUInt32(this.Thumbprint, i * 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (this.IsCertificate ? "S-1-9-1-" : "S-1-9-2-") + string.Join('-', parts);
        }
    }

    /// <summary>This key pair under another owner or protection, its key shared.</summary>
    public abstract KeyPair With(int principalId, string? password, bool hasPrivateKey);
}

/// <summary>A certificate (<c>sys.certificates</c>): self-signed, over a 3072-bit key.</summary>
internal sealed class Certificate(int id, string name, int principalId, string subject, string serialNumber, byte[] thumbprint,
    DateTime startDate, DateTime expiryDate, string? password, bool hasPrivateKey, bool activeForBeginDialog, StrongBox<RSA?>? rsa = null)
    : KeyPair(id, name, principalId, CertificateKeyLength, thumbprint, password, hasPrivateKey, rsa)
{
    public const int CertificateKeyLength = 3072;

    /// <summary>The subject as <c>sys.certificates</c> shows it, which is its issuer too.</summary>
    public readonly string Subject = subject;

    /// <summary>The serial number as real shows it: twenty hex bytes, space-separated.</summary>
    public readonly string SerialNumber = serialNumber;

    public readonly DateTime StartDate = startDate;

    public readonly DateTime ExpiryDate = expiryDate;

    public readonly bool ActiveForBeginDialog = activeForBeginDialog;

    public override KeyPair With(int principalId, string? password, bool hasPrivateKey) =>
        new Certificate(this.Id, this.Name, principalId, this.Subject, this.SerialNumber, this.Thumbprint, this.StartDate, this.ExpiryDate, password, hasPrivateKey, this.ActiveForBeginDialog, this.Rsa);

    public Certificate WithActiveForBeginDialog(bool active) =>
        new(this.Id, this.Name, this.PrincipalId, this.Subject, this.SerialNumber, this.Thumbprint, this.StartDate, this.ExpiryDate, this.Password, this.HasPrivateKey, active, this.Rsa);
}

/// <summary>An asymmetric key (<c>sys.asymmetric_keys</c>): an RSA key of 2048, 3072 or 4096 bits.</summary>
internal sealed class AsymmetricKey(int id, string name, int principalId, int keyLength, byte[] thumbprint, string? password, bool hasPrivateKey, StrongBox<RSA?>? rsa = null)
    : KeyPair(id, name, principalId, keyLength, thumbprint, password, hasPrivateKey, rsa)
{
    /// <summary><c>sys.asymmetric_keys.algorithm</c>: <c>3R</c>, <c>4R</c> or <c>5R</c>.</summary>
    public string AlgorithmCode => this.KeyLength switch { 2048 => "3R", 3072 => "4R", _ => "5R" };

    public string AlgorithmDescription => "RSA_" + this.KeyLength.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>sys.asymmetric_keys.public_key</c>: the public key as a CryptoAPI
    /// <c>PUBLICKEYBLOB</c> — the header real writes (probed 2026-10-15
    /// against SQL Server 2025), <c>RSA1</c>, the bit length and exponent, and
    /// the modulus little-endian.
    /// </summary>
    public byte[] PublicKeyBlob
    {
        get
        {
            var parameters = this.Key.ExportParameters(includePrivateParameters: false);
            var modulus = parameters.Modulus!;
            var blob = new byte[20 + modulus.Length];
            ReadOnlySpan<byte> header = [6, 2, 0, 0, 0, 0x24, 0, 0, (byte)'R', (byte)'S', (byte)'A', (byte)'1'];
            header.CopyTo(blob);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(12), this.KeyLength);
            var exponent = parameters.Exponent!;
            for (var i = 0; i < exponent.Length; i++)
                blob[16 + i] = exponent[exponent.Length - 1 - i];
            for (var i = 0; i < modulus.Length; i++)
                blob[20 + i] = modulus[modulus.Length - 1 - i];
            return blob;
        }
    }

    public override KeyPair With(int principalId, string? password, bool hasPrivateKey) =>
        new AsymmetricKey(this.Id, this.Name, principalId, this.KeyLength, this.Thumbprint, password, hasPrivateKey, this.Rsa);
}

/// <summary>
/// A symmetric key (<c>sys.symmetric_keys</c>). Immutable: <c>ALTER SYMMETRIC
/// KEY</c> replaces it under the same id, GUID and material, so a session's
/// open keys are tracked by id.
/// </summary>
internal sealed class SymmetricKey(int id, string name, int principalId, int keyLength, Guid keyGuid, byte[] material,
    DateTime createDate, DateTime modifyDate, KeyEncryption[] encryptions)
{
    public readonly int Id = id;

    public readonly string Name = name;

    public readonly int PrincipalId = principalId;

    /// <summary>The key length in bits: 128, 192 or 256, for <c>AES_128</c> / <c>AES_192</c> / <c>AES_256</c>.</summary>
    public readonly int KeyLength = keyLength;

    public readonly Guid KeyGuid = keyGuid;

    /// <summary>The AES key itself.</summary>
    public readonly byte[] Material = material;

    public readonly DateTime CreateDate = createDate;

    public readonly DateTime ModifyDate = modifyDate;

    public readonly KeyEncryption[] Encryptions = encryptions;

    public string AlgorithmCode => this.KeyLength switch { 128 => "A1", 192 => "A2", _ => "A3" };

    public string AlgorithmDescription => "AES_" + this.KeyLength.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public SymmetricKey With(KeyEncryption[] encryptions, DateTime modifyDate) =>
        new(this.Id, this.Name, this.PrincipalId, this.KeyLength, this.KeyGuid, this.Material, this.CreateDate, modifyDate, encryptions);

    public SymmetricKey WithOwner(int principalId) =>
        new(this.Id, this.Name, principalId, this.KeyLength, this.KeyGuid, this.Material, this.CreateDate, this.ModifyDate, this.Encryptions);
}

/// <summary>
/// A database's keys and certificates. The master key is replaced whole and
/// the two dictionaries hold immutable entries, so a statement's undo is a
/// copy of all three.
/// </summary>
internal sealed class EncryptionKeyStore(Collation collation)
{
    public DatabaseMasterKey? MasterKey;

    public readonly ConcurrentDictionary<string, Certificate> Certificates = new(collation.NameKeys);

    public readonly ConcurrentDictionary<string, SymmetricKey> SymmetricKeys = new(collation.NameKeys);

    public readonly ConcurrentDictionary<string, AsymmetricKey> AsymmetricKeys = new(collation.NameKeys);

    /// <summary>Serializes the key DDL, whose checks span the whole store.</summary>
    public readonly Lock Gate = new();

    // Certificates and symmetric keys count from 256, each on its own, and
    // never reuse an id (probed 2026-10-15 against SQL Server 2025).
    internal int LastCertificateId = 255;

    internal int LastSymmetricKeyId = 255;

    internal int LastAsymmetricKeyId = 255;

    public bool IsEmpty => this.MasterKey is null && this.Certificates.IsEmptyLockFree() && this.SymmetricKeys.IsEmptyLockFree() && this.AsymmetricKeys.IsEmptyLockFree();

    public SymmetricKey? SymmetricKeyById(int id)
    {
        foreach (var (_, key) in this.SymmetricKeys)
        {
            if (key.Id == id)
                return key;
        }
        return null;
    }

    public SymmetricKey? SymmetricKeyByGuid(Guid guid)
    {
        foreach (var (_, key) in this.SymmetricKeys)
        {
            if (key.KeyGuid == guid)
                return key;
        }
        return null;
    }

    public Certificate? CertificateById(int id)
    {
        foreach (var (_, certificate) in this.Certificates)
        {
            if (certificate.Id == id)
                return certificate;
        }
        return null;
    }

    public AsymmetricKey? AsymmetricKeyById(int id)
    {
        foreach (var (_, key) in this.AsymmetricKeys)
        {
            if (key.Id == id)
                return key;
        }
        return null;
    }

    /// <summary>The certificate or asymmetric key <paramref name="stale"/> names, as it stands now; null once dropped.</summary>
    public KeyPair? Current(KeyPair stale) => stale.IsCertificate ? this.CertificateById(stale.Id) : this.AsymmetricKeyById(stale.Id);

    /// <summary>A copy of the store's state that, run, puts it back.</summary>
    public Action Snapshot()
    {
        var masterKey = this.MasterKey;
        var certificates = this.Certificates.ToArray();
        var symmetricKeys = this.SymmetricKeys.ToArray();
        var asymmetricKeys = this.AsymmetricKeys.ToArray();
        return () =>
        {
            this.MasterKey = masterKey;
            this.Certificates.Clear();
            foreach (var (name, certificate) in certificates)
                this.Certificates[name] = certificate;
            this.SymmetricKeys.Clear();
            foreach (var (name, key) in symmetricKeys)
                this.SymmetricKeys[name] = key;
            this.AsymmetricKeys.Clear();
            foreach (var (name, key) in asymmetricKeys)
                this.AsymmetricKeys[name] = key;
        };
    }
}

/// <summary>A symmetric key a session has open: <c>sys.openkeys</c>' row.</summary>
internal readonly struct OpenSymmetricKey(Database database, int keyId, DateTime openedDate)
{
    public readonly Database Database = database;

    public readonly int KeyId = keyId;

    public readonly DateTime OpenedDate = openedDate;
}

/// <summary>
/// How real turns a <c>CREATE CERTIFICATE</c> subject into the subject it
/// shows, inferred from its output (probed 2026-10-15 against SQL Server
/// 2025): the text is read as an X.500 name after <c>CN=</c>, so
/// <c>'O=Bar, CN=Foo'</c> is two attributes, and the attribute values are
/// joined with <c>", "</c>, each quoted where it holds a special character,
/// and the whole quoted again by the same rule — <c>'a=b'</c> shows as
/// <c>"""a=b"""</c>. A component that isn't <c>type=value</c> with a known
/// type, or a stray quote, makes no name.
/// </summary>
internal static class CertificateSubject
{
    private static readonly string[] AttributeTypes =
    [
        "C", "CN", "DC", "DESCRIPTION", "DNQUALIFIER", "E", "G", "I", "L", "O", "OU", "PHONE", "POBOX",
        "POSTALCODE", "S", "SERIALNUMBER", "SN", "ST", "STREET", "T", "TITLE", "UID", "X21ADDRESS",
    ];

    /// <summary>The displayed subject, or null when <paramref name="subject"/> doesn't make an X.500 name.</summary>
    public static string? Display(string subject)
    {
        var text = "CN=" + subject;
        var values = new List<string>();
        var position = 0;
        while (true)
        {
            var equals = text.IndexOf('=', position);
            if (equals < 0)
                return null;
            var type = text[position..equals].Trim();
            if (!IsAttributeType(type))
                return null;
            position = equals + 1;
            while (position < text.Length && text[position] == ' ')
                position++;
            var value = new System.Text.StringBuilder();
            if (position < text.Length && text[position] == '"')
            {
                position++;
                while (true)
                {
                    if (position >= text.Length)
                        return null;
                    if (text[position] == '"')
                    {
                        if (position + 1 < text.Length && text[position + 1] == '"')
                        {
                            _ = value.Append('"');
                            position += 2;
                            continue;
                        }
                        position++;
                        break;
                    }
                    _ = value.Append(text[position++]);
                }
                while (position < text.Length && text[position] == ' ')
                    position++;
                if (position < text.Length && text[position] is not (',' or ';' or '+'))
                    return null;
            }
            else
            {
                var end = position;
                while (end < text.Length && text[end] is not (',' or ';' or '+'))
                {
                    if (text[end] == '"')
                        return null;
                    end++;
                }
                _ = value.Append(text.AsSpan(position, end - position).Trim(' '));
                position = end;
            }
            values.Add(Quote(value.ToString()));
            if (position >= text.Length)
                break;
            position++;
        }
        return Quote(string.Join(", ", values));
    }

    private static readonly System.Buffers.SearchValues<char> SpecialCharacters = System.Buffers.SearchValues.Create(",+=\"\n<>#;");

    private static bool IsAttributeType(string type)
    {
        if (type.Length == 0)
            return false;
        if (char.IsAsciiDigit(type[0]))
            return type.All(static c => char.IsAsciiDigit(c) || c == '.');
        foreach (var known in AttributeTypes)
        {
            if (type.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Quote(string value) =>
        value.Length != 0 && (value.AsSpan().IndexOfAny(SpecialCharacters) >= 0 || value[0] == ' ' || value[^1] == ' ')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}
