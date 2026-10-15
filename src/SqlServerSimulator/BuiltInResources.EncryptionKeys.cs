using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The rows of the key store's catalog views: sys.symmetric_keys,
// sys.certificates, sys.key_encryptions and sys.openkeys. A principal without
// full metadata visibility sees the keys and certificates it owns or holds a
// permission on.
internal static partial class BuiltInResources
{
    private static readonly CharSqlType KeyCodeType = CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit);

    private static readonly CharSqlType CryptTypeType = CharSqlType.Get(4, Collation.Catalog, Coercibility.Implicit);

    private static IEnumerable<SqlValue[]> EnumerateSymmetricKeys(Parser.BatchContext batch, Database database)
    {
        var store = database.EncryptionKeys;
        if (store.MasterKey is { } masterKey && PermissionEnforcement.HasDatabasePermission(batch, database, Permission.Control))
        {
            yield return SymmetricKeyRow(DatabaseMasterKey.Name, Database.DboPrincipalId, DatabaseMasterKey.Id, 256, "A3", "AES_256",
                masterKey.CreateDate, masterKey.ModifyDate, masterKey.KeyGuid);
        }
        foreach (var (_, key) in store.SymmetricKeys.ToArray().OrderBy(static entry => entry.Value.Id))
        {
            if (PermissionEnforcement.SeesKey(batch, database, PermissionChecker.ClassSymmetricKey, key.Id, key.PrincipalId))
                yield return SymmetricKeyRow(key.Name, key.PrincipalId, key.Id, key.KeyLength, key.AlgorithmCode, key.AlgorithmDescription, key.CreateDate, key.ModifyDate, key.KeyGuid);
        }
    }

    private static SqlValue[] SymmetricKeyRow(string name, int principalId, int id, int keyLength, string algorithm, string algorithmDescription, DateTime created, DateTime modified, Guid keyGuid) =>
    [
        SqlValue.FromSystemName(name), SqlValue.FromInt32(principalId), SqlValue.FromInt32(id), SqlValue.FromInt32(keyLength),
        SqlValue.FromChar(KeyCodeType, algorithm), SqlValue.FromNVarchar(algorithmDescription), SqlValue.FromDateTime(created), SqlValue.FromDateTime(modified),
        SqlValue.FromGuid(keyGuid), SqlValue.Null(SqlType.SqlVariant), SqlValue.Null(SqlType.NVarchar), SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(SqlType.SqlVariant),
    ];

    private static IEnumerable<SqlValue[]> EnumerateCertificates(Parser.BatchContext batch, Database database)
    {
        foreach (var (_, certificate) in database.EncryptionKeys.Certificates.ToArray().OrderBy(static entry => entry.Value.Id))
        {
            if (!PermissionEnforcement.SeesKey(batch, database, PermissionChecker.ClassCertificate, certificate.Id, certificate.PrincipalId))
                continue;
            yield return
            [
                SqlValue.FromSystemName(certificate.Name), SqlValue.FromInt32(certificate.Id), SqlValue.FromInt32(certificate.PrincipalId),
                SqlValue.FromChar(KeyCodeType, certificate.ProtectionCode), SqlValue.FromNVarchar(certificate.ProtectionDescription),
                SqlValue.FromBoolean(certificate.ActiveForBeginDialog), SqlValue.FromNVarchar(certificate.Subject), SqlValue.FromNVarchar(certificate.SerialNumber),
                SqlValue.FromVarbinary(certificate.Sid), SqlValue.FromNVarchar(certificate.StringSid), SqlValue.FromNVarchar(certificate.Subject),
                SqlValue.FromDateTime(certificate.ExpiryDate), SqlValue.FromDateTime(certificate.StartDate), SqlValue.FromVarbinary(certificate.Thumbprint),
                SqlValue.Null(SqlType.NVarchar), SqlValue.Null(SqlType.DateTime), SqlValue.FromInt32(certificate.KeyLength),
            ];
        }
    }

    /// <summary>
    /// <c>sys.asymmetric_keys</c>. Reading <c>public_key</c> makes each key's
    /// key pair, which nothing before may have needed.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateAsymmetricKeys(Parser.BatchContext batch, Database database)
    {
        foreach (var (_, key) in database.EncryptionKeys.AsymmetricKeys.ToArray().OrderBy(static entry => entry.Value.Id))
        {
            if (!PermissionEnforcement.SeesKey(batch, database, PermissionChecker.ClassAsymmetricKey, key.Id, key.PrincipalId))
                continue;
            yield return
            [
                SqlValue.FromSystemName(key.Name), SqlValue.FromInt32(key.PrincipalId), SqlValue.FromInt32(key.Id),
                SqlValue.FromChar(KeyCodeType, key.ProtectionCode), SqlValue.FromNVarchar(key.ProtectionDescription), SqlValue.FromVarbinary(key.Thumbprint),
                SqlValue.FromChar(KeyCodeType, key.AlgorithmCode), SqlValue.FromNVarchar(key.AlgorithmDescription), SqlValue.FromInt32(key.KeyLength),
                SqlValue.FromVarbinary(key.Sid), SqlValue.FromNVarchar(key.StringSid), SqlValue.FromVarbinary(VarbinarySqlType.MaxForm, key.PublicKeyBlob),
                SqlValue.Null(SqlType.NVarchar), SqlValue.Null(SqlType.NVarchar), SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(SqlType.SqlVariant),
            ];
        }
    }

    /// <summary>
    /// <c>sys.key_encryptions</c>: one row per encryption of each visible key,
    /// the master key's service-master-key encryption first. The thumbprint is
    /// the certificate's, the encrypting key's GUID, <c>0x01</c> for the
    /// service master key and NULL for a password (probed 2026-10-15 against
    /// SQL Server 2025).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateKeyEncryptions(Parser.BatchContext batch, Database database)
    {
        var store = database.EncryptionKeys;
        if (store.MasterKey is { } masterKey && PermissionEnforcement.HasDatabasePermission(batch, database, Permission.Control))
        {
            foreach (var encryption in masterKey.Encryptions)
                yield return KeyEncryptionRow(DatabaseMasterKey.Id, encryption);
        }
        foreach (var (_, key) in store.SymmetricKeys.ToArray().OrderBy(static entry => entry.Value.Id))
        {
            if (!PermissionEnforcement.SeesKey(batch, database, PermissionChecker.ClassSymmetricKey, key.Id, key.PrincipalId))
                continue;
            foreach (var encryption in key.Encryptions)
                yield return KeyEncryptionRow(key.Id, encryption);
        }
    }

    private static SqlValue[] KeyEncryptionRow(int keyId, KeyEncryption encryption)
    {
        var (code, description, thumbprint) = encryption.Kind switch
        {
            KeyEncryptionKind.ServiceMasterKey => ("ESKM", "ENCRYPTION BY MASTER KEY", [1]),
            KeyEncryptionKind.Certificate => ("C256", "ENCRYPTION BY CERTIFICATE OAEP256", encryption.KeyPair!.Thumbprint),
            KeyEncryptionKind.AsymmetricKey => ("A256", "ENCRYPTION BY ASYMMETRIC KEY OAEP256", encryption.KeyPair!.Thumbprint),
            KeyEncryptionKind.SymmetricKey => ("ESKS", "ENCRYPTION BY SYMMETRIC KEY", encryption.SymmetricKey!.KeyGuid.ToByteArray()),
            _ => ("ESP2", "ENCRYPTION BY PASSWORD V2", null),
        };
        return
        [
            SqlValue.FromInt32(keyId), thumbprint is null ? SqlValue.Null(SqlType.Varbinary) : SqlValue.FromVarbinary(thumbprint),
            SqlValue.FromChar(CryptTypeType, code), SqlValue.FromNVarchar(description), SqlValue.FromVarbinary(VarbinarySqlType.MaxForm, encryption.CryptProperty),
        ];
    }

    /// <summary>
    /// <c>sys.openkeys</c>: the session's open symmetric keys in every
    /// database, whichever database the view is read from (probed 2026-10-15
    /// against SQL Server 2025); a key dropped since it was opened is gone, as
    /// is one whose database was.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateOpenKeys(Parser.BatchContext batch, Database _)
    {
        OpenSymmetricKey[] opened;
        lock (batch.Connection.OpenSymmetricKeys)
            opened = [.. batch.Connection.OpenSymmetricKeys];
        foreach (var open in opened)
        {
            if (Simulation.OpenedKey(batch.Connection, open) is not { } key)
                continue;
            yield return
            [
                SqlValue.FromInt32(open.Database.Id), SqlValue.FromNVarchar(open.Database.Name), SqlValue.FromInt32(key.Id), SqlValue.FromNVarchar(key.Name),
                SqlValue.FromGuid(key.KeyGuid), SqlValue.FromDateTime(open.OpenedDate), SqlValue.FromInt16(1),
            ];
        }
    }
}
