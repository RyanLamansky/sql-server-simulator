using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// The database key store's refusals: the master key, certificates and
// symmetric keys, all probed 2026-10-15 against SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 15151 for a key or certificate a statement names that doesn't exist
    /// or that the principal can't see: <paramref name="verb"/> is <c>find</c>,
    /// <c>alter</c> or <c>drop</c>, <paramref name="kind"/> <c>symmetric key</c>,
    /// <c>certificate</c> or <c>asymmetric key</c>. A master key the database
    /// doesn't have is the symmetric key <c>'master key'</c>.
    /// </summary>
    internal static SimulatedSqlException CannotFindKey(string verb, string kind, string name) =>
        new($"Cannot {verb} the {kind} '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Msg 15282: <c>CREATE SYMMETRIC KEY</c> under a name, or with an
    /// <c>IDENTITY_VALUE</c> GUID, another key of the database already has.
    /// </summary>
    internal static SimulatedSqlException KeyAlreadyExists(string name) =>
        new($"A key with name '{name}' or user defined unique identifier already exists or you do not have permissions to create it.", 15282, 16, 1);

    /// <summary>Msg 15232: <c>CREATE CERTIFICATE</c> under a name the database already has.</summary>
    internal static SimulatedSqlException CertificateAlreadyExists(string name) =>
        new($"A certificate with name '{name}' already exists or this certificate already has been added to the database.", 15232, 16, 1);

    /// <summary>
    /// Msg 15313: a decryptor the key isn't encrypted by — state 8 opening a
    /// symmetric key, 11 opening the master key, 1 dropping an encryption the
    /// key doesn't have.
    /// </summary>
    internal static SimulatedSqlException KeyNotEncryptedByDecryptor(byte state) =>
        new("The key is not encrypted using the specified decryptor.", 15313, 16, state);

    /// <summary>
    /// Msg 15315: a symmetric key a statement needs open that the session
    /// hasn't opened — state 1 encrypting a new key by it, 2 altering it, 6
    /// opening another key by it, 7 closing it.
    /// </summary>
    internal static SimulatedSqlException KeyNotOpen(string name, byte state) =>
        new($"The key '{name}' is not open. Please open the key before using it.", 15315, 16, state);

    /// <summary>
    /// Msg 15334: a certificate whose private key a password protects, used to
    /// open a key without that password.
    /// </summary>
    internal static SimulatedSqlException CertificatePasswordRequired() =>
        new("The certificate has a private key that is protected by a user defined password. That password needs to be provided to enable the use of the private key.", 15334, 16, 5);

    /// <summary>Msg 15465: a password given for a certificate whose private key the master key protects.</summary>
    internal static SimulatedSqlException PrivateKeyPasswordInvalid() =>
        new("The private key password is invalid.", 15465, 16, 10);

    /// <summary>Msg 15466: the wrong password for a certificate's private key.</summary>
    internal static SimulatedSqlException DecryptionFailed() =>
        new("An error occurred during decryption.", 15466, 16, 9);

    /// <summary>
    /// Msg 15352: <c>DROP SYMMETRIC KEY</c> or <c>DROP CERTIFICATE</c> of one
    /// another key is encrypted by.
    /// </summary>
    internal static SimulatedSqlException KeyInUse(string kind) =>
        new($"The {kind} cannot be dropped because one or more entities are either signed or encrypted using it.", 15352, 16, 1);

    /// <summary>
    /// Msg 15558: an <c>ALTER … DROP ENCRYPTION</c> that would leave the key
    /// unprotected, or — for the master key — names a password it doesn't
    /// have; <paramref name="encryption"/> is <c>password '********'</c> or
    /// <c>certificate 'name'</c>.
    /// </summary>
    internal static SimulatedSqlException CannotDropEncryption(string encryption) =>
        new($"Cannot drop encryption by {encryption}.", 15558, 16, 1);

    /// <summary>Msg 15579: an <c>ALTER … ADD ENCRYPTION</c> the key already has; <paramref name="encryption"/> as in <see cref="CannotDropEncryption"/>.</summary>
    internal static SimulatedSqlException EncryptionAlreadyExists(string encryption) =>
        new($"Adding an encryption to the symmetric key failed. An encryption by the same {encryption} may already exist.", 15579, 16, 1);

    /// <summary>Msg 15578: <c>CREATE MASTER KEY</c> in a database that has one.</summary>
    internal static SimulatedSqlException MasterKeyAlreadyExists() =>
        new("There is already a master key in the database. Please drop it before performing this statement.", 15578, 16, 1);

    /// <summary>
    /// Msg 15580: <c>DROP MASTER KEY</c> while a certificate's private key is
    /// encrypted by it; <paramref name="what"/> is <c>certificate 'name'</c>.
    /// </summary>
    internal static SimulatedSqlException MasterKeyInUse(string what) =>
        new($"Cannot drop master key because {what} is encrypted by it.", 15580, 16, 1);

    /// <summary>
    /// Msg 15581: an operation needing the master key — state 1 in a database
    /// without one, 7 where it exists but the service master key doesn't
    /// protect it and the session hasn't opened it.
    /// </summary>
    internal static SimulatedSqlException MasterKeyUnavailable(byte state) =>
        new("Please create a master key in the database or open the master key in the session before performing this operation.", 15581, 16, state);

    /// <summary>Msg 15314: <c>CREATE SYMMETRIC KEY</c> without an <c>ALGORITHM</c>.</summary>
    internal static SimulatedSqlException KeyAlgorithmMissing() =>
        new("Either no algorithm has been specified or the bitlength and the algorithm specified for the key are not available in this installation of this operating system.", 15314, 16, 1);

    /// <summary>
    /// Msg 15297: a certificate that couldn't be built — state 45 for an expiry
    /// date not after its start date, 46 for a subject that isn't a valid
    /// X.500 name.
    /// </summary>
    internal static SimulatedSqlException CertificateDataInvalid(byte state) =>
        new("The certificate, asymmetric key, or private key data is invalid.", 15297, 16, state);

    /// <summary>Msg 15212: a certificate subject outside 1 to 128 characters.</summary>
    internal static SimulatedSqlException CertificateSubjectInvalid() =>
        new("Invalid certificate subject. The certificate subject must have between 1 and 128 characters.", 15212, 16, 1);

    /// <summary>Msg 33161, raised while the batch compiles: <c>CREATE MASTER KEY</c> without <c>ENCRYPTION BY PASSWORD</c>.</summary>
    internal static SimulatedSqlException MasterKeyPasswordRequired() =>
        new("Database master keys without password are not supported in this version of SQL Server.", 33161, 15, 1);

    /// <summary>Msg 190, raised while the batch compiles: a certificate's <c>START_DATE</c> or <c>EXPIRY_DATE</c> that isn't a date.</summary>
    internal static SimulatedSqlException InvalidDateInStatement() =>
        new("An invalid date or time was specified in the statement.", 190, 15, 1);

    /// <summary>Msg 15214, class 0: a certificate created already expired.</summary>
    internal static SimulatedError CertificateExpiredMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 15214, "Warning: The certificate you created is expired.");

    /// <summary>Msg 15215, class 0: a certificate created with a start date in the future.</summary>
    internal static SimulatedError CertificateNotYetValidMessage(BatchContext batch) =>
        batch.InfoMessage(@class: 0, state: 1, number: 15215, "Warning: The certificate you created is not yet valid; its start date is in the future.");
}
