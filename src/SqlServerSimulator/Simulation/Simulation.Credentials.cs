using System.Collections.Concurrent;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The server's credentials, keyed by name under <see cref="BuiltInToken.Comparer"/>,
    /// projected into <c>sys.credentials</c>. Server scope outlives any database,
    /// hence the <see cref="Simulation"/>-level home.
    /// </summary>
    internal readonly ConcurrentDictionary<string, ServerCredential> Credentials = new(BuiltInToken.Comparer);

    /// <summary>
    /// The credential each login is mapped to (<c>CREATE</c> / <c>ALTER LOGIN … CREDENTIAL =</c>),
    /// by server <c>principal_id</c> → <c>credential_id</c>; read by
    /// <c>sys.server_principals</c> / <c>sys.sql_logins</c>' <c>credential_id</c>.
    /// </summary>
    internal readonly ConcurrentDictionary<int, int> LoginCredentials = new();

    /// <summary>
    /// The last <c>credential_id</c> handed out. Real numbers credentials
    /// from 65536 server-wide, never reuses one, and spends one on a
    /// <c>CREATE CREDENTIAL</c> refused as a duplicate (probed 2026-10-06
    /// against SQL Server 2025).
    /// </summary>
    private int lastCredentialId = 65535;

    /// <summary>
    /// Parses <c>CREATE CREDENTIAL name WITH IDENTITY = '…' [, SECRET = '…']
    /// [FOR CRYPTOGRAPHIC PROVIDER provider]</c>. Cursor on entry: the
    /// <c>CREDENTIAL</c> word. The secret is checked for shape and discarded,
    /// since nothing reads it back. A session without <c>ALTER ANY
    /// CREDENTIAL</c> is Msg 15247, a provider (none exists) Msg 15151, and a
    /// duplicate name Msg 15530 (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static bool TryParseCreateCredential(ParserContext context)
    {
        var (name, identity) = ParseCredentialClauses(context, allowProvider: true, out var provider);
        if (context.Batch.IsSkipping)
            return true;
        var simulation = context.Batch.Connection.Simulation;
        if (!simulation.SessionHoldsServerPermission(context.Connection, Permission.AlterAnyCredential))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        if (provider is not null)
            throw SimulatedSqlException.CannotCreateCredentialForProvider(provider);
        RecordServerSecurityUndo(context.Batch);
        var id = Interlocked.Increment(ref simulation.lastCredentialId);
        var now = context.Batch.CurrentStatement.UtcNow;
        if (!simulation.Credentials.TryAdd(name, new ServerCredential(id, name, identity, now, now)))
            throw SimulatedSqlException.CredentialAlreadyExists(name);
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER CREDENTIAL name WITH IDENTITY = '…' [, SECRET = '…']</c>,
    /// which replaces the identity and stamps <c>modify_date</c>. A missing
    /// credential and a session without <c>ALTER ANY CREDENTIAL</c> are the
    /// same Msg 15151.
    /// </summary>
    internal static bool TryParseAlterCredential(ParserContext context)
    {
        var (name, identity) = ParseCredentialClauses(context, allowProvider: false, out _);
        if (context.Batch.IsSkipping)
            return true;
        var simulation = context.Batch.Connection.Simulation;
        if (!simulation.SessionHoldsServerPermission(context.Connection, Permission.AlterAnyCredential)
            || !simulation.Credentials.TryGetValue(name, out var existing))
        {
            throw SimulatedSqlException.CannotAlterOrDropCredential("alter", name);
        }
        RecordServerSecurityUndo(context.Batch);
        simulation.Credentials[name] = new ServerCredential(existing.Id, existing.Name, identity, existing.CreateDate, context.Batch.CurrentStatement.UtcNow);
        return true;
    }

    /// <summary>
    /// Parses <c>DROP CREDENTIAL name</c>, which has no <c>IF EXISTS</c>. A
    /// missing credential and a session without <c>ALTER ANY CREDENTIAL</c>
    /// are the same Msg 15151, and one a login is mapped to is Msg 15541
    /// (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static bool TryParseDropCredential(ParserContext context)
    {
        context.MoveNextRequired();
        var name = ParseCredentialName(context);
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return true;
        var simulation = context.Batch.Connection.Simulation;
        if (!simulation.SessionHoldsServerPermission(context.Connection, Permission.AlterAnyCredential)
            || !simulation.Credentials.TryGetValue(name, out var existing))
        {
            throw SimulatedSqlException.CannotAlterOrDropCredential("drop", name);
        }
        foreach (var (_, credentialId) in simulation.LoginCredentials)
        {
            if (credentialId == existing.Id)
                throw SimulatedSqlException.CredentialUsedByServerPrincipal(name);
        }
        RecordServerSecurityUndo(context.Batch);
        _ = simulation.Credentials.TryRemove(name, out _);
        return true;
    }

    /// <summary>
    /// Resolves the credential a login option names, as <c>CREATE</c> /
    /// <c>ALTER LOGIN</c> do before changing anything: Msg 15151 for a name
    /// that is no credential.
    /// </summary>
    private int ResolveCredentialId(string name) =>
        this.Credentials.TryGetValue(name, out var credential)
            ? credential.Id
            : throw SimulatedSqlException.CannotFindSecurable("credential", name);

    /// <summary>The <c>credential_id</c> the login <paramref name="principalId"/> is mapped to, NULL for none.</summary>
    internal SqlValue CredentialIdOf(int principalId) =>
        this.LoginCredentials.TryGetValue(principalId, out var credentialId) ? SqlValue.FromInt32(credentialId) : SqlValue.Null(SqlType.Int32);

    /// <summary>
    /// Maps the login <paramref name="principalId"/> to the credential
    /// <paramref name="credentialId"/>, or clears its mapping for null.
    /// </summary>
    private void MapLoginCredential(int principalId, int? credentialId)
    {
        if (credentialId is { } id)
            this.LoginCredentials[principalId] = id;
        else
            _ = this.LoginCredentials.TryRemove(principalId, out _);
    }

    /// <summary>
    /// Whether the session sees <c>sys.credentials</c>' rows: with <c>VIEW
    /// ANY DEFINITION</c>, or with <c>ALTER ANY CREDENTIAL</c> while <c>VIEW
    /// ANY DEFINITION</c> isn't denied it (probed 2026-10-06 against SQL
    /// Server 2025).
    /// </summary>
    internal bool SessionSeesCredentials(SimulatedDbConnection connection) =>
        this.SessionHoldsServerPermission(connection, Permission.ViewAnyDefinition)
        || (this.SessionHoldsServerPermission(connection, Permission.AlterAnyCredential)
            && !this.ServerPermissionDenied(connection.Security.Effective.LoginName, Permission.ViewAnyDefinition));

    /// <summary>
    /// The name, identity and provider of a <c>CREATE</c> / <c>ALTER
    /// CREDENTIAL</c>, cursor on the <c>CREDENTIAL</c> word on entry and past
    /// the statement on return. Every refusal here is a syntax error real
    /// raises while the batch compiles: an empty identity is Msg 102 near
    /// <c>'IDENTITY'</c> in capitals, a missing or repeated clause is Msg 102
    /// near the token that took its place, and <c>ALTER</c>'s provider clause
    /// is Msg 156 near <c>FOR</c> (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static (string Name, string Identity) ParseCredentialClauses(ParserContext context, bool allowProvider, out string? provider)
    {
        provider = null;
        context.MoveNextRequired();
        var name = ParseCredentialName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.With })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Identity })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var identity = ParseCredentialString(context);
        if (identity.Length == 0)
            throw SimulatedSqlException.SyntaxErrorNearText("IDENTITY");
        if (context.GetNextOptional() is Operator { Character: ',' })
        {
            if (context.GetNextRequired() is not UnquotedString { Span: var secretWord } || !secretWord.Equals("SECRET", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            _ = ParseCredentialString(context);
            context.MoveNextOptional();
        }
        if (allowProvider && context.Token is ReservedKeyword { Keyword: Keyword.For })
        {
            if (context.GetNextRequired() is not UnquotedString { Span: var cryptographic } || !cryptographic.Equals("CRYPTOGRAPHIC", StringComparison.OrdinalIgnoreCase)
                || context.GetNextRequired() is not UnquotedString { Span: var providerWord } || !providerWord.Equals("PROVIDER", StringComparison.OrdinalIgnoreCase)
                || context.GetNextRequired() is not Name providerName)
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            provider = providerName.Value;
            context.MoveNextOptional();
        }
        if (!IsStatementBoundary(context.Token))
        {
            throw context.Token is ReservedKeyword keyword
                ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
                : SimulatedSqlException.SyntaxErrorNear(context);
        }
        return (name, identity);
    }

    /// <summary>The <c>= '…'</c> after <c>IDENTITY</c> or <c>SECRET</c>, cursor left on the literal.</summary>
    private static string ParseCredentialString(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return context.GetNextRequired() is Literal { Value: var value } && SqlType.IsStringCategory(value.Type)
            ? value.AsString
            : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>The credential name at the cursor, without advancing; <c>[]</c> is Msg 1038.</summary>
    private static string ParseCredentialName(ParserContext context) => context.Token switch
    {
        Name { Value.Length: 0 } => throw SimulatedSqlException.EmptyColumnAlias(),
        Name nameToken => nameToken.Value,
        ReservedKeyword keyword => throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };
}

/// <summary>
/// One server credential: its <c>sys.credentials</c> row. Immutable — <c>ALTER
/// CREDENTIAL</c> replaces the entry, keeping the id and creation date.
/// </summary>
internal sealed class ServerCredential(int id, string name, string identity, DateTime createDate, DateTime modifyDate)
{
    public readonly int Id = id;
    public readonly string Name = name;
    public readonly string Identity = identity;
    public readonly DateTime CreateDate = createDate;
    public readonly DateTime ModifyDate = modifyDate;
}
