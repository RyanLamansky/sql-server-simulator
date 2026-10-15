# Encryption keys

A database's master key, certificates and symmetric keys, the statements that create, alter, open and drop them, and the functions that encrypt, decrypt and sign with them.
Statements in `Simulation/Simulation.EncryptionKeys.cs`, the store in `Schemas/EncryptionKeys.cs` (`Database.EncryptionKeys`), the functions in `Parser/Expressions/CryptographicFunctions.cs`, the catalog rows in `BuiltInResources.EncryptionKeys.cs`.
Every behavior below was probed 2026-10-15 against SQL Server 2025; the error matrix and catalog rows were diffed line for line against real through the TDS endpoint.

## What a key is here

The store keeps each key's **material in the clear** on the key, and each `sys.key_encryptions` row's `crypt_property` is random bytes of the length real's encrypted form has.
What a protector does is modeled as a check rather than as cryptography: opening a key by a password compares the password, opening it by a certificate checks the certificate's private key is reachable and that the key lists the certificate.
Nothing outside the simulator reads the encrypted form, and that is what lets the master key's own material go unmodeled.

The store's entries are immutable, and `ALTER` replaces one under the same id, GUID and material.
A statement's undo is a copy of the master key and both dictionaries (`EncryptionKeyStore.Snapshot`), so key DDL rolls back with its transaction as real's does.
A session's open keys live on the connection by id (`SimulatedDbConnection.OpenSymmetricKeys`), and those survive a rollback: `OPEN` and `CLOSE` aren't transactional on real.

## Symmetric keys and real's cipher text

SQL Server 2025 parses only `AES_128`, `AES_192` and `AES_256`; `DES`, `TRIPLE_DES`, `RC4` and the rest are Msg 102 near the name.
A key needs an `ENCRYPTION BY` clause, and a protector named twice encrypts it twice.

- **`KEY_SOURCE`** makes the key the first 16, 24 or 32 bytes of the SHA-512 of the source's UTF-16LE form.
- **`IDENTITY_VALUE`** makes its GUID the MD5 of the value's UTF-16LE form with the first byte cleared; without one the GUID is random, with the first byte cleared too, as every key GUID real hands out is.

So a key created from the same pair as a real key decrypts real's cipher text, and real decrypts the simulator's: `ENCRYPTBYKEY` writes the key GUID (`bytes_le` order) followed by the version-2 envelope `ENCRYPTBYPASSPHRASE` uses (`PassPhraseCipher`), under the key's material instead of a passphrase's SHA-256.
The 8000-byte ceiling counts the GUID, so the data limit is 7,943 bytes without an authenticator.

`ENCRYPTBYKEY` uses a key only while the session has it open, and `DECRYPTBYKEY` finds the key by the GUID at the head of the cipher text among the session's open keys, in whichever database they were opened; both answer NULL otherwise.
`sys.openkeys` lists the session's open keys in every database, read from any of them, and a key dropped since it was opened isn't there.

**`DECRYPTBYKEYAUTOCERT`** opens the key for the rest of the statement, and any later call in that statement decrypts with it whatever certificate and password that call names — `DECRYPTBYKEY` in the same statement still sees it closed, and the next statement starts without it.
That cache is `StatementContext.AutoOpenedKeys`.

## The master key

`CREATE MASTER KEY` needs `ENCRYPTION BY PASSWORD` (Msg 33161 while the batch compiles), and the key starts protected by the service master key too.
While the service master key protects it, every session can use it; drop that encryption and a session must `OPEN MASTER KEY` first, which is what creating a certificate without a password and using a certificate's private key need (Msg 15581: state 1 without a master key, 7 with one the session can't use).
`ALTER MASTER KEY` checks the password policy before availability, and a master key keeps at least one password.
`REGENERATE` gives a new GUID and leaves the one new password.

Passwords meet the login policy's rules without a login name to exclude: eight characters (Msg 33062), then three of four character sets (Msg 33064).

## Certificates

Every certificate is self-signed and holds a 3072-bit RSA key, generated when an operation first needs it, so a database full of certificates costs nothing until one is used.
`START_DATE` and `EXPIRY_DATE` keep whole seconds, the start defaulting to now and the expiry to a year after the start; one created already expired or not yet valid is created with Msg 15214 / 15215.

**The subject is read as an X.500 name after `CN=`** (`CertificateSubject`), which is why real shows some subjects oddly: `'O=Bar, CN=Foo'` is two attributes and shows as `"""O=Bar"", Foo"`, `'a=b'` as `"""a=b"""`, leading and trailing spaces are dropped, and a component that isn't `type=value` — `'a,b'`, `'a+b'`, a stray quote — is Msg 15297 state 46.
The rule was inferred from real's output, not from documentation: each attribute value is quoted where it holds a special character, the values join with `", "`, and the result is quoted again by the same rule.

`ENCRYPTBYCERT` / `DECRYPTBYCERT` use RSA-OAEP with SHA-256, so the data limit is 318 bytes, and real's 388-byte output is the cipher text in CryptoAPI's little-endian order followed by `0x00010001`.
`SIGNBYCERT` writes the eight bytes `0x0100070204000000` and a deterministic 384-byte signature.
The certificate functions answer NULL rather than raise where `OPEN SYMMETRIC KEY` raises: a password-protected private key without its password, a master-key-protected one with a password, or a master key the session can't use.

## Permissions

A principal sees a key or certificate — its catalog row, its name through `KEY_ID` / `CERT_ID`, opening it — as its owner, with full metadata visibility, or holding any permission on it through the permission graph (`PermissionChecker.CanViewKeyMetadata`).
Creating a certificate takes `CREATE CERTIFICATE` and a symmetric key `ALTER ANY SYMMETRIC KEY` (Msg 15247 without), the master key's DDL `CONTROL` on the database, and a principal that can't see a key gets the same Msg 15151 as for a key that doesn't exist.

## Divergences

- **A certificate is no X.509 certificate**: its thumbprint and serial number are random, and its RSA key is the simulator's own, so cipher text and signatures round-trip within the simulator but not against real.
  `CERTENCODED` / `CERTPRIVATEKEY` of a certificate that exists raise `NotSupportedException` for that reason.
- **`sys.key_encryptions.crypt_property`** is random bytes of real's length, not an encryption of the key.
- **The DONE token** a key statement sends over TDS follows its leading keyword (`OPEN` and `CLOSE` close as the cursor statements do); real's was not captured.

## Not modeled yet

- Asymmetric keys (`CREATE ASYMMETRIC KEY` raises `NotSupportedException`; an `ENCRYPTION BY` / `DECRYPTION BY ASYMMETRIC KEY` is Msg 15151, since none can exist), and so the `…ASYMKEY…` functions answer as for a missing key.
- `ALTER CERTIFICATE`, `CREATE CERTIFICATE … FROM` a file, binary or assembly, `BACKUP CERTIFICATE`, `BACKUP` / `RESTORE MASTER KEY`, and the service master key's statements.
- Temporary symmetric keys (`#name`) and keys from an extensible key management provider.
- `GRANT` / `DENY` / `REVOKE` on the `SYMMETRIC KEY::` and `CERTIFICATE::` classes and `ALTER AUTHORIZATION` of a key or certificate — owner and database-level permissions are all that reach one.
- Module signing (`ADD SIGNATURE`, `IS_OBJECTSIGNED`, `sys.crypt_properties`), and users and logins mapped to a certificate.
- `BACKUP DATABASE` of a database holding keys raises `NotSupportedException` ([`backup-restore.md`](backup-restore.md)).
- Real's `master` holds the `##MS_…##` system certificates, which `CERT_ID` finds there; here `master` starts with none.
