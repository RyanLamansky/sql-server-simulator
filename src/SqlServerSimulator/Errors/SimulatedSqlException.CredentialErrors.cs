namespace SqlServerSimulator;

// Credential DDL error factories: CREATE / ALTER / DROP CREDENTIAL and a
// login's CREDENTIAL option (probed 2026-10-06 against SQL Server 2025).
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>Msg 15530: <c>CREATE CREDENTIAL</c> naming a credential that exists.</summary>
    internal static SimulatedSqlException CredentialAlreadyExists(string name) =>
        new($"The credential with name \"{name}\" already exists.", 15530, 16, 1);

    /// <summary>
    /// Msg 15151: <c>ALTER</c> or <c>DROP CREDENTIAL</c> (<paramref name="verb"/>)
    /// naming a credential that doesn't exist, or run without <c>ALTER ANY CREDENTIAL</c>.
    /// </summary>
    internal static SimulatedSqlException CannotAlterOrDropCredential(string verb, string name) =>
        new($"Cannot {verb} the credential '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Msg 15151: <c>CREATE CREDENTIAL … FOR CRYPTOGRAPHIC PROVIDER</c>, which names no provider the server has.</summary>
    internal static SimulatedSqlException CannotCreateCredentialForProvider(string provider) =>
        new($"Cannot create credential for the cryptographic provider '{provider}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Msg 15541: <c>DROP CREDENTIAL</c> of a credential a login is mapped to.</summary>
    internal static SimulatedSqlException CredentialUsedByServerPrincipal(string name) =>
        new($"Cannot drop the credential '{name}' because it is used by a server principal.", 15541, 16, 1);
}
