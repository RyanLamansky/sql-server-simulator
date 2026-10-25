using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage.Backup;

// The key store a backup carries: the master key, certificates, asymmetric and
// symmetric keys, each key pair with the RSA key it has made. The store's
// entries are immutable, so capture holds them as they are and restore puts
// rebuilt ones in the new database's store.
partial class DatabaseImage
{
    public DatabaseMasterKey? MasterKey;
    public readonly List<Certificate> Certificates = [];
    public readonly List<AsymmetricKey> AsymmetricKeys = [];
    public readonly List<SymmetricKey> SymmetricKeys = [];
    public readonly List<DatabaseScopedCredential> ScopedCredentials = [];
    public int LastScopedCredentialId;

    private void CaptureKeys(Database database)
    {
        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            this.MasterKey = store.MasterKey;
            this.Certificates.AddRange(store.Certificates.ToArray().Select(static entry => entry.Value).OrderBy(static certificate => certificate.Id));
            this.AsymmetricKeys.AddRange(store.AsymmetricKeys.ToArray().Select(static entry => entry.Value).OrderBy(static key => key.Id));
            this.SymmetricKeys.AddRange(store.SymmetricKeys.ToArray().Select(static entry => entry.Value).OrderBy(static key => key.Id));
            this.ScopedCredentials.AddRange(store.ScopedCredentials.ToArray().Select(static entry => entry.Value).OrderBy(static credential => credential.Id));
            this.LastScopedCredentialId = store.LastScopedCredentialId;
        }
    }

    /// <summary>Replaces <paramref name="database"/>'s key store with the image's.</summary>
    private void ApplyKeys(Database database)
    {
        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            store.MasterKey = this.MasterKey;
            store.Certificates.Clear();
            foreach (var certificate in this.Certificates)
                store.Certificates[certificate.Name] = certificate;
            store.AsymmetricKeys.Clear();
            foreach (var key in this.AsymmetricKeys)
                store.AsymmetricKeys[key.Name] = key;
            store.SymmetricKeys.Clear();
            foreach (var key in this.SymmetricKeys)
                store.SymmetricKeys[key.Name] = key;
            store.ScopedCredentials.Clear();
            foreach (var credential in this.ScopedCredentials)
                store.ScopedCredentials[credential.Name] = credential;
            store.LastScopedCredentialId = this.LastScopedCredentialId;
        }
    }

    private void WriteKeys(BinaryWriter w)
    {
        w.Write(this.MasterKey is not null);
        if (this.MasterKey is { } masterKey)
        {
            w.Write(masterKey.KeyGuid.ToByteArray());
            w.Write(masterKey.CreateDate.Ticks);
            w.Write(masterKey.ModifyDate.Ticks);
            WriteEncryptions(w, masterKey.Encryptions);
        }
        w.Write(this.Certificates.Count);
        foreach (var certificate in this.Certificates)
        {
            WriteKeyPair(w, certificate);
            w.Write(certificate.Subject);
            w.Write(certificate.SerialNumber);
            w.Write(certificate.StartDate.Ticks);
            w.Write(certificate.ExpiryDate.Ticks);
            w.Write(certificate.ActiveForBeginDialog);
        }
        w.Write(this.AsymmetricKeys.Count);
        foreach (var key in this.AsymmetricKeys)
            WriteKeyPair(w, key);
        w.Write(this.SymmetricKeys.Count);
        foreach (var key in this.SymmetricKeys)
        {
            w.Write(key.Id);
            w.Write(key.Name);
            w.Write(key.PrincipalId);
            w.Write(key.KeyLength);
            w.Write(key.KeyGuid.ToByteArray());
            WriteBytes(w, key.Material);
            w.Write(key.CreateDate.Ticks);
            w.Write(key.ModifyDate.Ticks);
            WriteEncryptions(w, key.Encryptions);
        }
        w.Write(this.ScopedCredentials.Count);
        foreach (var credential in this.ScopedCredentials)
        {
            w.Write(credential.Id);
            w.Write(credential.Name);
            w.Write(credential.PrincipalId);
            w.Write(credential.Identity);
            w.Write(credential.CreateDate.Ticks);
            w.Write(credential.ModifyDate.Ticks);
        }
        w.Write(this.LastScopedCredentialId);
    }

    private static void ReadKeys(BinaryReader r, DatabaseImage image)
    {
        if (r.ReadBoolean())
        {
            var keyGuid = new Guid(r.ReadBytes(16));
            var createDate = ReadDate(r);
            var modifyDate = ReadDate(r);
            image.MasterKey = new(keyGuid, createDate, modifyDate, ReadEncryptions(r, image));
        }
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var (id, name, principalId, _, thumbprint, password, hasPrivateKey, rsa) = ReadKeyPair(r);
            image.Certificates.Add(new Certificate(id, name, principalId, r.ReadString(), r.ReadString(), thumbprint, ReadDate(r), ReadDate(r),
                password, hasPrivateKey, r.ReadBoolean(), rsa));
        }
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var (id, name, principalId, keyLength, thumbprint, password, hasPrivateKey, rsa) = ReadKeyPair(r);
            image.AsymmetricKeys.Add(new AsymmetricKey(id, name, principalId, keyLength, thumbprint, password, hasPrivateKey, rsa));
        }
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var id = r.ReadInt32();
            var name = r.ReadString();
            var principalId = r.ReadInt32();
            var keyLength = r.ReadInt32();
            var keyGuid = new Guid(r.ReadBytes(16));
            var material = ReadBytes(r);
            var createDate = ReadDate(r);
            var modifyDate = ReadDate(r);
            image.SymmetricKeys.Add(new SymmetricKey(id, name, principalId, keyLength, keyGuid, material, createDate, modifyDate, ReadEncryptions(r, image)));
        }
        for (var i = r.ReadInt32(); i > 0; i--)
            image.ScopedCredentials.Add(new DatabaseScopedCredential(r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadString(), ReadDate(r), ReadDate(r)));
        image.LastScopedCredentialId = r.ReadInt32();
    }

    private static void WriteKeyPair(BinaryWriter w, KeyPair keyPair)
    {
        w.Write(keyPair.Id);
        w.Write(keyPair.Name);
        w.Write(keyPair.PrincipalId);
        w.Write(keyPair.KeyLength);
        WriteBytes(w, keyPair.Thumbprint);
        BackupBinary.WriteNullable(w, keyPair.Password);
        w.Write(keyPair.HasPrivateKey);
        // The key pair is made now if no operation has made it yet, the
        // database keeping it too, so what the database encrypts after the
        // backup a copy of it decrypts.
        WriteBytes(w, keyPair.Key.ExportRSAPrivateKey());
    }

    private static (int Id, string Name, int PrincipalId, int KeyLength, byte[] Thumbprint, string? Password, bool HasPrivateKey, StrongBox<RSA?> Rsa) ReadKeyPair(BinaryReader r)
    {
        var id = r.ReadInt32();
        var name = r.ReadString();
        var principalId = r.ReadInt32();
        var keyLength = r.ReadInt32();
        var thumbprint = ReadBytes(r);
        var password = BackupBinary.ReadNullable(r);
        var hasPrivateKey = r.ReadBoolean();
        var privateKey = ReadBytes(r);
        var rsa = new StrongBox<RSA?>();
        if (privateKey.Length != 0)
        {
            rsa.Value = RSA.Create();
            rsa.Value.ImportRSAPrivateKey(privateKey, out _);
        }
        return (id, name, principalId, keyLength, thumbprint, password, hasPrivateKey, rsa);
    }

    /// <summary>
    /// A key's encryptions, each naming its certificate or asymmetric key by
    /// id — read back against the image's own, which <see cref="ReadKeys"/>
    /// reads first — and its symmetric key by value.
    /// </summary>
    private static void WriteEncryptions(BinaryWriter w, KeyEncryption[] encryptions)
    {
        w.Write(encryptions.Length);
        foreach (var encryption in encryptions)
        {
            w.Write((byte)encryption.Kind);
            BackupBinary.WriteNullable(w, encryption.Password);
            w.Write(encryption.KeyPair?.Id ?? 0);
            w.Write(encryption.SymmetricKey is not null);
            if (encryption.SymmetricKey is { } key)
            {
                w.Write(key.Id);
                w.Write(key.Name);
                w.Write(key.KeyGuid.ToByteArray());
            }
            WriteBytes(w, encryption.CryptProperty);
        }
    }

    private static KeyEncryption[] ReadEncryptions(BinaryReader r, DatabaseImage image)
    {
        var encryptions = new KeyEncryption[r.ReadInt32()];
        for (var i = 0; i < encryptions.Length; i++)
        {
            var kind = (KeyEncryptionKind)r.ReadByte();
            var password = BackupBinary.ReadNullable(r);
            var keyPairId = r.ReadInt32();
            KeyPair? keyPair = kind switch
            {
                KeyEncryptionKind.Certificate => image.Certificates.Find(certificate => certificate.Id == keyPairId),
                KeyEncryptionKind.AsymmetricKey => image.AsymmetricKeys.Find(key => key.Id == keyPairId),
                _ => null,
            };
            var symmetricKey = r.ReadBoolean() ? new SymmetricKeyReference(r.ReadInt32(), r.ReadString(), new Guid(r.ReadBytes(16))) : null;
            encryptions[i] = new(kind, password, keyPair, symmetricKey, ReadBytes(r));
        }
        return encryptions;
    }

    private static void WriteBytes(BinaryWriter w, byte[] bytes)
    {
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private static byte[] ReadBytes(BinaryReader r) => r.ReadBytes(r.ReadInt32());

    private static DateTime ReadDate(BinaryReader r) => new(r.ReadInt64(), DateTimeKind.Utc);
}
