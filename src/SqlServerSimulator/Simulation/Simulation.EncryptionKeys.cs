using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The database key store's statements: CREATE / ALTER / DROP MASTER KEY, OPEN
// / CLOSE MASTER KEY, CREATE / DROP CERTIFICATE, CREATE / ALTER / DROP
// SYMMETRIC KEY and OPEN / CLOSE SYMMETRIC KEY. Every check and message here
// was probed 2026-10-15 against SQL Server 2025.
partial class Simulation
{
    private const byte ClassSymmetricKey = PermissionChecker.ClassSymmetricKey;

    private const byte ClassCertificate = PermissionChecker.ClassCertificate;

    /// <summary>
    /// <c>CREATE MASTER KEY ENCRYPTION BY PASSWORD = '…'</c>, cursor on
    /// <c>MASTER</c>. Without the password clause it is Msg 33161 while the
    /// batch compiles; a session without <c>CONTROL</c> on the database is Msg
    /// 15247, a database that has one Msg 15578. The new key is protected by
    /// the service master key too, so it is available to every session.
    /// </summary>
    private static bool TryParseCreateMasterKey(ParserContext context)
    {
        ExpectWord(context, "KEY");
        if (!IsKeyWord(context.GetNextOptional(), "ENCRYPTION"))
            throw SimulatedSqlException.MasterKeyPasswordRequired();
        ExpectWord(context, "BY");
        ExpectWord(context, "PASSWORD");
        var masks = new List<(int Start, int End)>(1);
        var password = ParseKeyLiteral(context, masks);
        EndKeyStatement(context);
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, database, Permission.Control))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        ValidateKeyPassword(password);
        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            if (store.MasterKey is not null)
                throw SimulatedSqlException.MasterKeyAlreadyExists();
            RecordKeyStoreUndo(context, store);
            var now = context.Batch.CurrentStatement.UtcNow;
            store.MasterKey = new(NewKeyGuid(), now, now,
            [
                new(KeyEncryptionKind.ServiceMasterKey, null, null, null, KeyEncryption.BlockEncryptedLength(32)),
                PasswordEncryption(password, 32),
            ]);
        }
        RecordKeyDdlEvent(context, "CREATE_MASTER_KEY", "MASTER KEY", null, null, masks);
        return true;
    }

    /// <summary>
    /// <c>ALTER MASTER KEY</c>: <c>[FORCE] REGENERATE WITH ENCRYPTION BY
    /// PASSWORD</c>, or <c>ADD</c> / <c>DROP ENCRYPTION BY</c> the service
    /// master key or a password. The key must exist and the session hold
    /// <c>CONTROL</c> (Msg 15151 otherwise); a new password meets the password
    /// policy, and then the key must be available to the session — protected
    /// by the service master key or opened — or it is Msg 15581 state 7. A
    /// password it already has is Msg 15579; dropping one it hasn't, or its
    /// last, is Msg 15558. A regenerated key has a new GUID and the one
    /// password.
    /// </summary>
    private static bool TryParseAlterMasterKey(ParserContext context)
    {
        ExpectWord(context, "KEY");
        var masks = new List<(int Start, int End)>(1);
        string? password = null;
        var regenerate = false;
        var add = false;
        var serviceMasterKey = false;
        var token = context.GetNextRequired();
        if (IsKeyWord(token, "FORCE"))
            token = context.GetNextRequired();
        if (IsKeyWord(token, "REGENERATE"))
        {
            regenerate = true;
            ExpectWord(context, "WITH");
            ExpectWord(context, "ENCRYPTION");
            ExpectWord(context, "BY");
            ExpectWord(context, "PASSWORD");
            password = ParseKeyLiteral(context, masks);
        }
        else if (IsKeyWord(token, "ADD") || IsKeyWord(token, "DROP"))
        {
            add = IsKeyWord(token, "ADD");
            ExpectWord(context, "ENCRYPTION");
            ExpectWord(context, "BY");
            var protector = context.GetNextRequired();
            if (IsKeyWord(protector, "SERVICE"))
            {
                ExpectWord(context, "MASTER");
                ExpectWord(context, "KEY");
                serviceMasterKey = true;
            }
            else if (IsKeyWord(protector, "PASSWORD"))
            {
                password = ParseKeyLiteral(context, masks);
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
        else
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        EndKeyStatement(context);
        if (context.Batch.IsSkipping)
            return true;

        var store = context.CurrentDatabase.EncryptionKeys;
        lock (store.Gate)
            AlterMasterKey(context, store, password, regenerate, add, serviceMasterKey);
        RecordKeyDdlEvent(context, "ALTER_MASTER_KEY", "MASTER KEY", null, null, masks);
        return true;
    }

    private static void AlterMasterKey(ParserContext context, EncryptionKeyStore store, string? password, bool regenerate, bool add, bool serviceMasterKey)
    {
        var database = context.CurrentDatabase;
        var masterKey = VisibleMasterKey(context.Batch, database);
        if (password is not null && (regenerate || add))
            ValidateKeyPassword(password);
        RequireMasterKey(context.Batch.Connection, database);
        var now = context.Batch.CurrentStatement.UtcNow;
        var encryptions = new List<KeyEncryption>(masterKey.Encryptions);
        if (regenerate)
        {
            _ = encryptions.RemoveAll(static encryption => encryption.Kind == KeyEncryptionKind.Password);
            encryptions.Add(PasswordEncryption(password!, 32));
            RecordKeyStoreUndo(context, store);
            store.MasterKey = new(NewKeyGuid(), masterKey.CreateDate, now, [.. encryptions]);
            return;
        }
        if (serviceMasterKey)
        {
            var existing = encryptions.FindIndex(static encryption => encryption.Kind == KeyEncryptionKind.ServiceMasterKey);
            if (add == (existing >= 0))
                return;
            if (add)
                encryptions.Insert(0, new(KeyEncryptionKind.ServiceMasterKey, null, null, null, KeyEncryption.BlockEncryptedLength(32)));
            else
                encryptions.RemoveAt(existing);
        }
        else
        {
            var index = encryptions.FindIndex(encryption => encryption.Kind == KeyEncryptionKind.Password && string.Equals(encryption.Password, password, StringComparison.Ordinal));
            if (add)
            {
                if (index >= 0)
                    throw SimulatedSqlException.EncryptionAlreadyExists("password '********'");
                encryptions.Add(PasswordEncryption(password!, 32));
            }
            else
            {
                if (index < 0 || encryptions.Count(static encryption => encryption.Kind == KeyEncryptionKind.Password) == 1)
                    throw SimulatedSqlException.CannotDropEncryption("password '********'");
                encryptions.RemoveAt(index);
            }
        }
        RecordKeyStoreUndo(context, store);
        store.MasterKey = masterKey.With([.. encryptions], now);
    }

    /// <summary>
    /// <c>DROP MASTER KEY</c>: Msg 15151 when the database has none or the
    /// session lacks <c>CONTROL</c>, Msg 15580 while a certificate's private
    /// key is encrypted by it.
    /// </summary>
    private static bool TryParseDropMasterKey(ParserContext context)
    {
        ExpectWord(context, "KEY");
        context.MoveNextOptional();
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            _ = VisibleMasterKey(context.Batch, database);
            foreach (var (_, certificate) in store.Certificates.ToArray().OrderBy(static entry => entry.Value.Id))
            {
                if (certificate.Password is null)
                    throw SimulatedSqlException.MasterKeyInUse($"certificate '{certificate.Name}'");
            }
            RecordKeyStoreUndo(context, store);
            store.MasterKey = null;
        }
        RecordKeyDdlEvent(context, "DROP_MASTER_KEY", "MASTER KEY", null, null, null);
        return true;
    }

    /// <summary>
    /// <c>OPEN MASTER KEY DECRYPTION BY PASSWORD = '…'</c>, cursor on
    /// <c>OPEN</c>: makes the master key available to the session. Msg 15151
    /// for a database without one, Msg 15313 state 11 for a password it isn't
    /// encrypted by; opening it again is harmless.
    /// </summary>
    private static void ParseOpenMasterKey(ParserContext context)
    {
        ExpectWord(context, "MASTER");
        ExpectWord(context, "KEY");
        ExpectWord(context, "DECRYPTION");
        ExpectWord(context, "BY");
        ExpectWord(context, "PASSWORD");
        var password = ParseKeyLiteral(context);
        EndKeyStatement(context);
        if (context.Batch.IsSkipping)
            return;

        var database = context.CurrentDatabase;
        var masterKey = VisibleMasterKey(context.Batch, database);
        if (!Array.Exists(masterKey.Encryptions, encryption => encryption.Kind == KeyEncryptionKind.Password && string.Equals(encryption.Password, password, StringComparison.Ordinal)))
            throw SimulatedSqlException.KeyNotEncryptedByDecryptor(11);
        var opened = context.Connection.OpenMasterKeys;
        lock (opened)
        {
            if (!opened.Contains(database))
                opened.Add(database);
        }
    }

    /// <summary>
    /// <c>CLOSE MASTER KEY</c>, cursor on <c>CLOSE</c>: harmless when the
    /// session hasn't opened it, Msg 15581 state 5 in a database without one.
    /// Symmetric keys it opened stay open.
    /// </summary>
    private static void ParseCloseMasterKey(ParserContext context)
    {
        ExpectWord(context, "MASTER");
        ExpectWord(context, "KEY");
        context.MoveNextOptional();
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return;

        var database = context.CurrentDatabase;
        if (database.EncryptionKeys.MasterKey is null)
            throw SimulatedSqlException.MasterKeyUnavailable(5);
        var opened = context.Connection.OpenMasterKeys;
        lock (opened)
            _ = opened.Remove(database);
    }

    /// <summary>
    /// <c>CREATE CERTIFICATE name [AUTHORIZATION owner] [ENCRYPTION BY
    /// PASSWORD = '…'] WITH SUBJECT = '…' [, START_DATE = '…'] [, EXPIRY_DATE
    /// = '…'] [ACTIVE FOR BEGIN_DIALOG = ON | OFF]</c>, cursor on
    /// <c>CERTIFICATE</c>: a self-signed certificate, starting now (to the
    /// second) and expiring a year after it starts unless the dates say
    /// otherwise. A date that isn't one is Msg 190 while the batch compiles.
    /// As it runs: Msg 15247 without <c>CREATE CERTIFICATE</c>, the password
    /// policy, Msg 15212 for a subject outside 1 to 128 characters, Msg 15297
    /// for one that isn't an X.500 name (state 46) or an expiry not after the
    /// start (state 45), Msg 15232 for a name in use, and Msg 15581 when the
    /// master key that would protect the private key isn't available. One
    /// already expired or not yet valid is created with Msg 15214 / 15215.
    /// Loading one from a file, a binary or an assembly isn't built.
    /// </summary>
    private static bool TryParseCreateCertificate(ParserContext context)
    {
        var name = ParseKeyObjectName(context);
        var owner = ParseKeyAuthorization(context);
        if (IsKeyWord(context.Token, "FROM"))
            throw new NotSupportedException("CREATE CERTIFICATE … FROM a file, binary or assembly isn't modeled by the simulator; certificates are created self-signed.");
        string? password = null;
        var masks = new List<(int Start, int End)>(1);
        if (IsKeyWord(context.Token, "ENCRYPTION"))
        {
            ExpectWord(context, "BY");
            ExpectWord(context, "PASSWORD");
            password = ParseKeyLiteral(context, masks);
            context.MoveNextRequired();
        }
        if (!IsKeyWord(context.Token, "WITH"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        string? subject = null;
        DateTime? start = null;
        DateTime? expiry = null;
        do
        {
            var option = context.GetNextRequired();
            if (IsKeyWord(option, "SUBJECT") && subject is null)
                subject = ParseKeyLiteral(context);
            else if (IsKeyWord(option, "START_DATE") && start is null)
                start = ParseCertificateDate(context);
            else if (IsKeyWord(option, "EXPIRY_DATE") && expiry is null)
                expiry = ParseCertificateDate(context);
            else
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        while (context.GetNextOptional() is Operator { Character: ',' });
        if (subject is null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var activeForBeginDialog = true;
        if (IsKeyWord(context.Token, "ACTIVE"))
        {
            ExpectWord(context, "FOR");
            ExpectWord(context, "BEGIN_DIALOG");
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var setting = context.GetNextRequired();
            if (!IsKeyWord(setting, "ON") && !IsKeyWord(setting, "OFF"))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            activeForBeginDialog = IsKeyWord(setting, "ON");
            context.MoveNextOptional();
        }
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return true;

        var batch = context.Batch;
        var database = context.CurrentDatabase;
        if (!PermissionEnforcement.HoldsDatabasePermission(batch, database, "CREATE CERTIFICATE"))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        var principalId = owner is null ? KeyCreatorId(batch) : ResolveNewOwner(context, database, owner, acceptsSchemaOwner: false)!.Value;
        if (password is not null)
            ValidateKeyPassword(password);
        if (subject.Length is 0 or > 128)
            throw SimulatedSqlException.CertificateSubjectInvalid();
        var displaySubject = CertificateSubject.Display(subject) ?? throw SimulatedSqlException.CertificateDataInvalid(46);
        var now = batch.CurrentStatement.UtcNow;
        var startDate = start ?? TruncateToSecond(now);
        var expiryDate = expiry ?? startDate.AddYears(1);
        if (expiryDate <= startDate)
            throw SimulatedSqlException.CertificateDataInvalid(45);

        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            if (store.Certificates.ContainsKey(name))
                throw SimulatedSqlException.CertificateAlreadyExists(name);
            if (password is null)
                RequireMasterKey(context.Connection, database);
            RecordKeyStoreUndo(context, store);
            var serial = RandomNumberGenerator.GetBytes(20);
            store.Certificates[name] = new(++store.LastCertificateId, name, principalId, displaySubject,
                string.Join(' ', Array.ConvertAll(serial, static b => b.ToString("x2", CultureInfo.InvariantCulture))),
                RandomNumberGenerator.GetBytes(20), startDate, expiryDate, password, activeForBeginDialog);
        }
        RecordKeyDdlEvent(context, "CREATE_CERTIFICATE", "CERTIFICATE", name, principalId, masks);
        if (expiryDate < now)
            context.Connection.PendingMessages.Enqueue(SimulatedSqlException.CertificateExpiredMessage(batch));
        else if (startDate > now)
            context.Connection.PendingMessages.Enqueue(SimulatedSqlException.CertificateNotYetValidMessage(batch));
        return true;
    }

    /// <summary>
    /// <c>DROP CERTIFICATE name</c>: Msg 15151 for one that doesn't exist or
    /// the session can't control, Msg 15352 while a symmetric key is
    /// encrypted by it.
    /// </summary>
    private static bool TryParseDropCertificate(ParserContext context)
    {
        var name = ParseKeyObjectName(context);
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            if (!store.Certificates.TryGetValue(name, out var certificate)
                || !PermissionEnforcement.HoldsPermission(context.Batch, database, Permission.Control, ClassCertificate, certificate.Id, 0))
            {
                throw SimulatedSqlException.CannotFindKey("drop", "certificate", name);
            }
            foreach (var (_, key) in store.SymmetricKeys)
            {
                if (Array.Exists(key.Encryptions, encryption => encryption.Certificate?.Id == certificate.Id))
                    throw SimulatedSqlException.KeyInUse("certificate");
            }
            RecordKeyStoreUndo(context, store);
            _ = store.Certificates.TryRemove(name, out _);
            DropSecurablePermissions(context, database, ClassCertificate, certificate.Id);
            RecordKeyDdlEvent(context, "DROP_CERTIFICATE", "CERTIFICATE", name, certificate.PrincipalId, null);
        }
        return true;
    }

    /// <summary>
    /// <c>CREATE SYMMETRIC KEY name [AUTHORIZATION owner] WITH option [, …]
    /// ENCRYPTION BY encryption [, …]</c>, cursor on <c>SYMMETRIC</c>. The
    /// options are <c>ALGORITHM = AES_128 | AES_192 | AES_256</c> — the only
    /// algorithms SQL Server 2025 still parses —, <c>KEY_SOURCE</c>, whose
    /// SHA-512 over its <c>nvarchar</c> form is the key, and
    /// <c>IDENTITY_VALUE</c>, whose MD5 over the same is the key's GUID with
    /// its first byte cleared; a repeated option is Msg 102 near
    /// <c>WITH</c>. Each encryption is a password, a certificate or another
    /// symmetric key, which must be open (Msg 15315 state 1). As it runs: Msg
    /// 15247 without <c>ALTER ANY SYMMETRIC KEY</c>, the password policy, Msg
    /// 15314 without an algorithm, Msg 15151 for a certificate or key that
    /// isn't there, and Msg 15282 for a name or GUID in use. Temporary keys
    /// and keys from a provider aren't built.
    /// </summary>
    private static bool TryParseCreateSymmetricKey(ParserContext context)
    {
        ExpectWord(context, "KEY");
        var name = ParseKeyObjectName(context);
        if (name.StartsWith('#'))
            throw new NotSupportedException("Temporary symmetric keys (CREATE SYMMETRIC KEY #name) aren't modeled by the simulator.");
        var owner = ParseKeyAuthorization(context);
        if (IsKeyWord(context.Token, "FROM"))
            throw new NotSupportedException("Symmetric keys from an extensible key management provider aren't modeled by the simulator.");
        if (!IsKeyWord(context.Token, "WITH"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        int? keyLength = null;
        string? keySource = null;
        string? identityValue = null;
        do
        {
            var option = context.GetNextRequired();
            if (IsKeyWord(option, "ALGORITHM"))
            {
                if (keyLength is not null)
                    throw SimulatedSqlException.SyntaxErrorNearText("WITH");
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var algorithm = context.GetNextRequired();
                keyLength = IsKeyWord(algorithm, "AES_128") ? 128
                    : IsKeyWord(algorithm, "AES_192") ? 192
                    : IsKeyWord(algorithm, "AES_256") ? 256
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            else if (IsKeyWord(option, "KEY_SOURCE"))
            {
                if (keySource is not null)
                    throw SimulatedSqlException.SyntaxErrorNearText("WITH");
                keySource = ParseKeyLiteral(context);
            }
            else if (IsKeyWord(option, "IDENTITY_VALUE"))
            {
                if (identityValue is not null)
                    throw SimulatedSqlException.SyntaxErrorNearText("WITH");
                identityValue = ParseKeyLiteral(context);
            }
            else if (IsKeyWord(option, "PROVIDER_KEY_NAME") || IsKeyWord(option, "CREATION_DISPOSITION"))
            {
                throw new NotSupportedException("Symmetric keys from an extensible key management provider aren't modeled by the simulator.");
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
        while (context.GetNextOptional() is Operator { Character: ',' });
        if (!IsKeyWord(context.Token, "ENCRYPTION"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        ExpectWord(context, "BY");
        var masks = new List<(int Start, int End)>();
        var protectors = ParseKeyProtectors(context, masks);
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return true;

        var batch = context.Batch;
        var database = context.CurrentDatabase;
        if (!PermissionEnforcement.HoldsDatabasePermission(batch, database, "ALTER ANY SYMMETRIC KEY"))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        var principalId = owner is null ? KeyCreatorId(batch) : ResolveNewOwner(context, database, owner, acceptsSchemaOwner: false)!.Value;
        foreach (var protector in protectors)
        {
            if (protector.Password is { } password)
                ValidateKeyPassword(password);
        }
        if (keyLength is not { } bits)
            throw SimulatedSqlException.KeyAlgorithmMissing();

        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            // A protector named twice encrypts the key twice, as real's two
            // rows in sys.key_encryptions show (probed 2026-10-15).
            var encryptions = new KeyEncryption[protectors.Count];
            for (var i = 0; i < encryptions.Length; i++)
                encryptions[i] = ResolveProtector(context, store, protectors[i], bits / 8);
            var keyGuid = identityValue is null ? NewKeyGuid() : IdentityGuid(identityValue);
            if (store.SymmetricKeys.ContainsKey(name) || store.SymmetricKeyByGuid(keyGuid) is not null)
                throw SimulatedSqlException.KeyAlreadyExists(name);
            var material = keySource is null
                ? RandomNumberGenerator.GetBytes(bits / 8)
                : SHA512.HashData(Encoding.Unicode.GetBytes(keySource)).AsSpan(0, bits / 8).ToArray();
            RecordKeyStoreUndo(context, store);
            var now = batch.CurrentStatement.UtcNow;
            store.SymmetricKeys[name] = new(++store.LastSymmetricKeyId, name, principalId, bits, keyGuid, material, now, now, encryptions);
        }
        RecordKeyDdlEvent(context, "CREATE_SYMMETRIC_KEY", "SYMMETRIC KEY", name, principalId, masks);
        return true;
    }

    /// <summary>
    /// <c>ALTER SYMMETRIC KEY name { ADD | DROP } ENCRYPTION BY encryption [,
    /// …]</c>: Msg 15151 for a key that isn't there or the session can't
    /// alter, Msg 15315 state 2 while the session hasn't opened it, Msg 15579
    /// adding an encryption it has, Msg 15313 state 1 dropping one it hasn't
    /// and Msg 15558 dropping its last.
    /// </summary>
    private static bool TryParseAlterSymmetricKey(ParserContext context)
    {
        ExpectWord(context, "KEY");
        var name = ParseKeyObjectName(context);
        var add = IsKeyWord(context.Token, "ADD");
        if (!add && !IsKeyWord(context.Token, "DROP"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        ExpectWord(context, "ENCRYPTION");
        ExpectWord(context, "BY");
        var masks = new List<(int Start, int End)>();
        var protectors = ParseKeyProtectors(context, masks);
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return true;

        var batch = context.Batch;
        var database = context.CurrentDatabase;
        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            if (!store.SymmetricKeys.TryGetValue(name, out var key)
                || !PermissionEnforcement.HoldsPermission(batch, database, Permission.Alter, ClassSymmetricKey, key.Id, 0))
            {
                throw SimulatedSqlException.CannotFindKey("alter", "symmetric key", name);
            }
            if (add)
            {
                foreach (var protector in protectors)
                {
                    if (protector.Password is { } password)
                        ValidateKeyPassword(password);
                }
            }
            if (!IsKeyOpen(context.Connection, database, key.Id))
                throw SimulatedSqlException.KeyNotOpen(key.Name, 2);
            var encryptions = new List<KeyEncryption>(key.Encryptions);
            foreach (var protector in protectors)
            {
                var encryption = ResolveProtector(context, store, protector, key.KeyLength / 8);
                var index = encryptions.FindIndex(encryption.SameProtector);
                if (add)
                {
                    if (index >= 0)
                        throw SimulatedSqlException.EncryptionAlreadyExists(ProtectorText(encryption));
                    encryptions.Add(encryption);
                }
                else
                {
                    if (index < 0)
                        throw SimulatedSqlException.KeyNotEncryptedByDecryptor(1);
                    if (encryptions.Count == 1)
                        throw SimulatedSqlException.CannotDropEncryption(ProtectorText(encryption));
                    encryptions.RemoveAt(index);
                }
            }
            RecordKeyStoreUndo(context, store);
            store.SymmetricKeys[name] = key.With([.. encryptions], batch.CurrentStatement.UtcNow);
            RecordKeyDdlEvent(context, "ALTER_SYMMETRIC_KEY", "SYMMETRIC KEY", name, key.PrincipalId, masks);
        }
        return true;
    }

    /// <summary>
    /// <c>DROP SYMMETRIC KEY name [REMOVE PROVIDER KEY]</c>: Msg 15151 for a
    /// key that isn't there or the session can't control, Msg 15352 while
    /// another key is encrypted by it. A key some session has open is dropped
    /// all the same, and is no longer open.
    /// </summary>
    private static bool TryParseDropSymmetricKey(ParserContext context)
    {
        ExpectWord(context, "KEY");
        var name = ParseKeyObjectName(context);
        if (IsKeyWord(context.Token, "REMOVE"))
        {
            ExpectWord(context, "PROVIDER");
            ExpectWord(context, "KEY");
            context.MoveNextOptional();
        }
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        var store = database.EncryptionKeys;
        lock (store.Gate)
        {
            if (!store.SymmetricKeys.TryGetValue(name, out var key)
                || !PermissionEnforcement.HoldsPermission(context.Batch, database, Permission.Control, ClassSymmetricKey, key.Id, 0))
            {
                throw SimulatedSqlException.CannotFindKey("drop", "symmetric key", name);
            }
            foreach (var (_, other) in store.SymmetricKeys)
            {
                if (Array.Exists(other.Encryptions, encryption => encryption.SymmetricKey?.Id == key.Id))
                    throw SimulatedSqlException.KeyInUse("symmetric key");
            }
            RecordKeyStoreUndo(context, store);
            _ = store.SymmetricKeys.TryRemove(name, out _);
            DropSecurablePermissions(context, database, ClassSymmetricKey, key.Id);
            RecordKeyDdlEvent(context, "DROP_SYMMETRIC_KEY", "SYMMETRIC KEY", name, key.PrincipalId, null);
        }
        return true;
    }

    /// <summary>
    /// <c>OPEN SYMMETRIC KEY name DECRYPTION BY { CERTIFICATE c [WITH PASSWORD
    /// = '…'] | ASYMMETRIC KEY a [WITH PASSWORD = '…'] | SYMMETRIC KEY k |
    /// PASSWORD = '…' }</c>, cursor on <c>OPEN</c>. Msg 15151 for a key or
    /// decryptor that isn't there or the session can't see. A certificate's
    /// private key needs its password (Msg 15334 without it, 15466 for a
    /// wrong one) or, when the master key protects it, no password (Msg
    /// 15465) and the master key available (Msg 15581); a decrypting key must
    /// be open (Msg 15315 state 6). A decryptor the key isn't encrypted by is
    /// Msg 15313 state 8. Opening an open key again is harmless, and the open
    /// key outlives a rollback.
    /// </summary>
    private static void ParseOpenSymmetricKey(ParserContext context)
    {
        ExpectWord(context, "SYMMETRIC");
        ExpectWord(context, "KEY");
        var name = ParseKeyObjectName(context);
        if (!IsKeyWord(context.Token, "DECRYPTION"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        ExpectWord(context, "BY");
        var decryptor = ParseKeyProtector(context, decrypting: true, masks: null);
        context.MoveNextOptional();
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return;

        var batch = context.Batch;
        var database = context.CurrentDatabase;
        var store = database.EncryptionKeys;
        if (!store.SymmetricKeys.TryGetValue(name, out var key) || !PermissionEnforcement.SeesKey(batch, database, ClassSymmetricKey, key.Id, key.PrincipalId))
            throw SimulatedSqlException.CannotFindKey("find", "symmetric key", name);
        KeyEncryption? match;
        if (decryptor.Password is { } password)
        {
            match = Array.Find(key.Encryptions, encryption => encryption.Kind == KeyEncryptionKind.Password && string.Equals(encryption.Password, password, StringComparison.Ordinal));
        }
        else if (decryptor.IsAsymmetricKey)
        {
            throw SimulatedSqlException.CannotFindKey("find", "asymmetric key", decryptor.Name!);
        }
        else if (decryptor.Kind == KeyEncryptionKind.Certificate)
        {
            var certificate = FindVisibleCertificate(batch, database, decryptor.Name!);
            RequirePrivateKey(context.Connection, database, certificate, decryptor.CertificatePassword);
            match = Array.Find(key.Encryptions, encryption => encryption.Certificate?.Id == certificate.Id);
        }
        else
        {
            if (!store.SymmetricKeys.TryGetValue(decryptor.Name!, out var decrypting) || !PermissionEnforcement.SeesKey(batch, database, ClassSymmetricKey, decrypting.Id, decrypting.PrincipalId))
                throw SimulatedSqlException.CannotFindKey("find", "symmetric key", decryptor.Name!);
            if (!IsKeyOpen(context.Connection, database, decrypting.Id))
                throw SimulatedSqlException.KeyNotOpen(decrypting.Name, 6);
            match = Array.Find(key.Encryptions, encryption => encryption.SymmetricKey?.Id == decrypting.Id);
        }
        if (match is null)
            throw SimulatedSqlException.KeyNotEncryptedByDecryptor(8);
        var opened = context.Connection.OpenSymmetricKeys;
        lock (opened)
        {
            if (!opened.Exists(open => open.Database == database && open.KeyId == key.Id))
                opened.Add(new(database, key.Id, batch.CurrentStatement.UtcNow));
        }
    }

    /// <summary>
    /// <c>CLOSE SYMMETRIC KEY name</c> or <c>CLOSE ALL SYMMETRIC KEYS</c>,
    /// cursor on <c>CLOSE</c>: Msg 15151 for a key that isn't there, Msg 15315
    /// state 7 for one the session hasn't opened.
    /// </summary>
    private static void ParseCloseSymmetricKey(ParserContext context)
    {
        var all = IsKeyWord(context.GetNextRequired(), "ALL");
        if (all)
        {
            ExpectWord(context, "SYMMETRIC");
            ExpectWord(context, "KEYS");
            context.MoveNextOptional();
        }
        else
        {
            if (!IsKeyWord(context.Token, "SYMMETRIC"))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ExpectWord(context, "KEY");
        }
        var name = all ? null : ParseKeyObjectName(context);
        EndKeyStatement(context, alreadyPast: true);
        if (context.Batch.IsSkipping)
            return;

        var opened = context.Connection.OpenSymmetricKeys;
        if (name is null)
        {
            lock (opened)
                opened.Clear();
            return;
        }
        var database = context.CurrentDatabase;
        if (!database.EncryptionKeys.SymmetricKeys.TryGetValue(name, out var key) || !PermissionEnforcement.SeesKey(context.Batch, database, ClassSymmetricKey, key.Id, key.PrincipalId))
            throw SimulatedSqlException.CannotFindKey("find", "symmetric key", name);
        lock (opened)
        {
            if (opened.RemoveAll(open => open.Database == database && open.KeyId == key.Id) == 0)
                throw SimulatedSqlException.KeyNotOpen(key.Name, 7);
        }
    }

    /// <summary>A password, certificate, symmetric key or asymmetric key a statement names to encrypt or decrypt a key.</summary>
    private readonly struct KeyProtector(KeyEncryptionKind kind, string? name, string? password, string? certificatePassword, bool isAsymmetricKey = false)
    {
        /// <summary><see cref="KeyEncryptionKind.Password"/>, <see cref="KeyEncryptionKind.Certificate"/> or <see cref="KeyEncryptionKind.SymmetricKey"/>; an asymmetric key is a certificate-kind protector with <see cref="IsAsymmetricKey"/> set.</summary>
        public readonly KeyEncryptionKind Kind = kind;

        /// <summary>The certificate's or key's name; null for a password.</summary>
        public readonly string? Name = name;

        public readonly string? Password = password;

        /// <summary>A decrypting certificate's <c>WITH PASSWORD</c>.</summary>
        public readonly string? CertificatePassword = certificatePassword;

        /// <summary>Whether the protector is an asymmetric key, which can't exist yet.</summary>
        public readonly bool IsAsymmetricKey = isAsymmetricKey;
    }

    /// <summary>The comma list after <c>ENCRYPTION BY</c>, cursor past it on return.</summary>
    private static List<KeyProtector> ParseKeyProtectors(ParserContext context, List<(int Start, int End)> masks)
    {
        var protectors = new List<KeyProtector>();
        do
            protectors.Add(ParseKeyProtector(context, decrypting: false, masks));
        while (context.GetNextOptional() is Operator { Character: ',' });
        return protectors;
    }

    /// <summary>One protector, cursor on the word before it on entry and on its last token on return.</summary>
    private static KeyProtector ParseKeyProtector(ParserContext context, bool decrypting, List<(int Start, int End)>? masks)
    {
        var word = context.GetNextRequired();
        if (IsKeyWord(word, "PASSWORD"))
            return new(KeyEncryptionKind.Password, null, ParseKeyLiteral(context, masks), null);
        if (IsKeyWord(word, "CERTIFICATE") || IsKeyWord(word, "ASYMMETRIC"))
        {
            var certificate = IsKeyWord(word, "CERTIFICATE");
            if (!certificate)
                ExpectWord(context, "KEY");
            context.MoveNextRequired();
            var name = context.Token is Name { Value: var value } ? value : throw SimulatedSqlException.SyntaxErrorNear(context);
            string? password = null;
            if (decrypting && IsKeyWord(PeekAfterLead(context), "WITH"))
            {
                context.MoveNextRequired();
                ExpectWord(context, "PASSWORD");
                password = ParseKeyLiteral(context);
            }
            return new(KeyEncryptionKind.Certificate, name, null, password, isAsymmetricKey: !certificate);
        }
        if (IsKeyWord(word, "SYMMETRIC"))
        {
            ExpectWord(context, "KEY");
            context.MoveNextRequired();
            return context.Token is Name { Value: var value }
                ? new(KeyEncryptionKind.SymmetricKey, value, null, null)
                : throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// The encryption <paramref name="protector"/> gives a new or altered key
    /// of <paramref name="keyBytes"/> bytes: Msg 15151 for a certificate or
    /// key that isn't there, Msg 15315 state 1 for a key that isn't open.
    /// </summary>
    private static KeyEncryption ResolveProtector(ParserContext context, EncryptionKeyStore store, KeyProtector protector, int keyBytes)
    {
        if (protector.Password is { } password)
            return PasswordEncryption(password, keyBytes);
        var batch = context.Batch;
        var database = context.CurrentDatabase;
        if (protector.IsAsymmetricKey)
            throw SimulatedSqlException.CannotFindKey("find", "asymmetric key", protector.Name!);
        if (protector.Kind == KeyEncryptionKind.Certificate)
        {
            var certificate = FindVisibleCertificate(batch, database, protector.Name!);
            return new(KeyEncryptionKind.Certificate, null, certificate, null, RandomNumberGenerator.GetBytes(Certificate.KeyLength / 8));
        }
        if (!store.SymmetricKeys.TryGetValue(protector.Name!, out var key) || !PermissionEnforcement.SeesKey(batch, database, ClassSymmetricKey, key.Id, key.PrincipalId))
            throw SimulatedSqlException.CannotFindKey("find", "symmetric key", protector.Name!);
        if (!IsKeyOpen(context.Connection, database, key.Id))
            throw SimulatedSqlException.KeyNotOpen(key.Name, 1);
        return new(KeyEncryptionKind.SymmetricKey, null, null, key, KeyEncryption.BlockEncryptedLength(keyBytes));
    }

    private static string ProtectorText(KeyEncryption encryption) => encryption.Kind switch
    {
        KeyEncryptionKind.Certificate => $"certificate '{encryption.Certificate!.Name}'",
        KeyEncryptionKind.SymmetricKey => $"symmetric key '{encryption.SymmetricKey!.Name}'",
        _ => "password '********'",
    };

    private static Certificate FindVisibleCertificate(BatchContext batch, Database database, string name) =>
        database.EncryptionKeys.Certificates.TryGetValue(name, out var certificate) && PermissionEnforcement.SeesKey(batch, database, ClassCertificate, certificate.Id, certificate.PrincipalId)
            ? certificate
            : throw SimulatedSqlException.CannotFindKey("find", "certificate", name);

    /// <summary>
    /// Refuses the use of <paramref name="certificate"/>'s private key as
    /// <c>OPEN SYMMETRIC KEY</c> does: a password-protected one needs its
    /// password, a master-key-protected one none and the master key available.
    /// </summary>
    private static void RequirePrivateKey(SimulatedDbConnection connection, Database database, Certificate certificate, string? password)
    {
        if (certificate.Password is { } expected)
        {
            if (password is null)
                throw SimulatedSqlException.CertificatePasswordRequired();
            if (!string.Equals(password, expected, StringComparison.Ordinal))
                throw SimulatedSqlException.DecryptionFailed();
            return;
        }
        if (password is not null)
            throw SimulatedSqlException.PrivateKeyPasswordInvalid();
        RequireMasterKey(connection, database);
    }

    /// <summary>Whether <paramref name="certificate"/>'s private key opens with <paramref name="password"/>, as the cryptographic functions ask it: they answer NULL rather than raise.</summary>
    internal static bool PrivateKeyAvailable(SimulatedDbConnection connection, Database database, Certificate certificate, string? password) =>
        certificate.Password is { } expected
            ? string.Equals(password, expected, StringComparison.Ordinal)
            : password is null && MasterKeyAvailable(connection, database);

    /// <summary>Msg 15581 — state 1 without a master key, 7 with one the session can't use — unless the master key is available.</summary>
    private static void RequireMasterKey(SimulatedDbConnection connection, Database database)
    {
        if (database.EncryptionKeys.MasterKey is null)
            throw SimulatedSqlException.MasterKeyUnavailable(1);
        if (!MasterKeyAvailable(connection, database))
            throw SimulatedSqlException.MasterKeyUnavailable(7);
    }

    /// <summary>Whether the session can use <paramref name="database"/>'s master key: the service master key protects it, or the session opened it.</summary>
    internal static bool MasterKeyAvailable(SimulatedDbConnection connection, Database database)
    {
        if (database.EncryptionKeys.MasterKey is not { } masterKey)
            return false;
        if (masterKey.EncryptedByServiceMasterKey)
            return true;
        lock (connection.OpenMasterKeys)
            return connection.OpenMasterKeys.Contains(database);
    }

    /// <summary>The master key, when the database has one and the session holds <c>CONTROL</c> on the database; Msg 15151 otherwise.</summary>
    private static DatabaseMasterKey VisibleMasterKey(BatchContext batch, Database database) =>
        database.EncryptionKeys.MasterKey is { } masterKey && PermissionEnforcement.HasDatabasePermission(batch, database, Permission.Control)
            ? masterKey
            : throw SimulatedSqlException.CannotFindKey("find", "symmetric key", "master key");

    /// <summary>
    /// The key an entry of the session's open keys names, or null once the
    /// key or its database is gone — dropped, or replaced by a restore.
    /// </summary>
    internal static SymmetricKey? OpenedKey(SimulatedDbConnection connection, OpenSymmetricKey open) =>
        connection.Simulation.Databases.TryGetValue(open.Database.Name, out var live) && ReferenceEquals(live, open.Database)
            ? open.Database.EncryptionKeys.SymmetricKeyById(open.KeyId)
            : null;

    internal static bool IsKeyOpen(SimulatedDbConnection connection, Database database, int keyId)
    {
        lock (connection.OpenSymmetricKeys)
            return connection.OpenSymmetricKeys.Exists(open => open.Database == database && open.KeyId == keyId);
    }

    /// <summary>The key's owner when no <c>AUTHORIZATION</c> names one: the creating user.</summary>
    private static int KeyCreatorId(BatchContext batch) => batch.Connection.Security.Effective.DatabasePrincipalId;

    private static KeyEncryption PasswordEncryption(string password, int keyBytes) =>
        new(KeyEncryptionKind.Password, password, null, null, KeyEncryption.BlockEncryptedLength(keyBytes));

    /// <summary>A random key GUID, its first byte cleared as real's are.</summary>
    private static Guid NewKeyGuid()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        bytes[0] = 0;
        return new(bytes);
    }

    /// <summary>The GUID an <c>IDENTITY_VALUE</c> gives a key: the MD5 of its UTF-16 form, first byte cleared.</summary>
#pragma warning disable CA5351 // real's derivation fixes MD5
    private static Guid IdentityGuid(string identityValue)
    {
        var bytes = MD5.HashData(Encoding.Unicode.GetBytes(identityValue));
#pragma warning restore CA5351
        bytes[0] = 0;
        return new(bytes);
    }

    private static DateTime TruncateToSecond(DateTime value) => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);

    /// <summary>The password policy a key's password meets: as a login's, without a login name to exclude.</summary>
    private static void ValidateKeyPassword(string password)
    {
        if (password.Length < 8)
            throw SimulatedSqlException.PasswordTooShort();
        var sets = 0;
        sets += password.Any(char.IsUpper) ? 1 : 0;
        sets += password.Any(char.IsLower) ? 1 : 0;
        sets += password.Any(char.IsDigit) ? 1 : 0;
        sets += password.Any(static c => !char.IsLetterOrDigit(c)) ? 1 : 0;
        if (sets < 3)
            throw SimulatedSqlException.PasswordNotComplex();
    }

    private static void RecordKeyStoreUndo(ParserContext context, EncryptionKeyStore store)
    {
        if (LogsDdlUndo(context.Batch))
            RecordDdlUndo(context, store.Snapshot());
    }

    /// <summary>Whether the two tokens after the cursor are <paramref name="first"/> and <paramref name="second"/>, so <c>OPEN</c> / <c>CLOSE</c> name a key rather than a cursor.</summary>
    private static bool FollowedByWords(ParserContext context, string first, string second)
    {
        var checkpoint = context.SaveCheckpoint();
        var matches = IsKeyWord(context.GetNextOptional(), first) && IsKeyWord(context.GetNextOptional(), second);
        context.RestoreCheckpoint(checkpoint);
        return matches;
    }

    private static bool IsKeyWord(Token? token, string word) =>
        token is UnquotedString or ReservedKeyword && BuiltInToken.Equals(token.ErrorText, word);

    private static void ExpectWord(ParserContext context, string word)
    {
        if (!IsKeyWord(context.GetNextRequired(), word))
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>The name after the cursor, cursor left past it.</summary>
    private static string ParseKeyObjectName(ParserContext context)
    {
        var name = context.GetNextRequired() is Name { Value: var value } ? value : throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return name;
    }

    /// <summary>An optional <c>AUTHORIZATION owner</c> at the cursor, cursor left past it.</summary>
    private static string? ParseKeyAuthorization(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Authorization })
            return null;
        var owner = context.GetNextRequired() is Name { Value: var value } ? value : throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        return owner;
    }

    /// <summary>
    /// The <c>= '…'</c> after a password or source word, cursor left on the
    /// literal; a password's place in the text joins <paramref name="masks"/>,
    /// which a DDL event's command text reads as <c>'******'</c>.
    /// </summary>
    private static string ParseKeyLiteral(ParserContext context, List<(int Start, int End)>? masks = null)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Literal { Value: var value } literal || !SqlType.IsStringCategory(value.Type))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        masks?.Add((literal.StartIndex, literal.EndIndex));
        return value.AsString;
    }

    /// <summary>The key DDL event a statement raises, its passwords masked; the master key's names no object or owner.</summary>
    private static void RecordKeyDdlEvent(ParserContext context, string eventType, string objectType, string? name, int? ownerId, List<(int Start, int End)>? masks) =>
        RecordDdlEvent(context, eventType, schemaName: null, name, objectType,
            ownerName: ownerId is { } owner ? Ownership.PrincipalName(context.CurrentDatabase, owner) : null,
            masks: masks is { Count: > 0 } ? [.. masks] : null);

    /// <summary>A certificate date: a string that converts to <c>datetime</c>, to the second, or Msg 190 while the batch compiles.</summary>
    private static DateTime ParseCertificateDate(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Literal { Value: var value } || !SqlType.IsStringCategory(value.Type))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        try
        {
            return TruncateToSecond(value.CoerceTo(SqlType.DateTime).AsDateTime);
        }
        catch (SimulatedSqlException)
        {
            throw SimulatedSqlException.InvalidDateInStatement();
        }
    }

    /// <summary>Requires the statement to end at the cursor — on its last token, or past it with <paramref name="alreadyPast"/>.</summary>
    private static void EndKeyStatement(ParserContext context, bool alreadyPast = false)
    {
        if (!alreadyPast)
            context.MoveNextOptional();
        if (!IsStatementBoundary(context.Token))
        {
            throw context.Token is ReservedKeyword keyword
                ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
                : SimulatedSqlException.SyntaxErrorNear(context);
        }
    }
}
